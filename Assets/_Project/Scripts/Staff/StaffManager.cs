using System.Collections.Generic;
using BarOwner.Bar;
using BarOwner.Customers;
using BarOwner.Day;
using BarOwner.Economy;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Staff
{
    /// <summary>
    /// Hiring, firing, the weekly rota and wages (server-authoritative; the lists sync to everyone
    /// for the staff noticeboard). When a day starts, everyone rostered for that weekday turns up
    /// as a <see cref="StaffWorker"/>; at the end of the day they're paid and go home.
    /// </summary>
    public class StaffManager : NetworkBehaviour
    {
        public static StaffManager Instance { get; private set; }

        public const int MaxStaff = 3;
        public const int ApplicantsPerDay = 3;

        [SerializeField] private NetworkObject workerPrefab;
        [SerializeField] private Transform arrivalPoint;
        [SerializeField, Tooltip("Where staff wait behind the bar when there's nothing to do.")] private Transform[] idleSpots;
        [SerializeField, Tooltip("Where staff drop collected glasses (near the glass rack).")] private Transform glassReturnPoint;
        [SerializeField, Tooltip("Z of the walkway behind the bar where staff stand to serve.")] private float staffLaneZ = 3.55f;

        private NetworkList<StaffData> _applicants;
        private NetworkList<StaffData> _hired;

        private readonly List<StaffWorker> _workers = new();
        private readonly HashSet<Customer> _claimedCustomers = new();
        private readonly HashSet<PintGlass> _claimedGlasses = new();
        private readonly List<StaffData> _workedToday = new();
        private int _nextId = 1;

        public NetworkList<StaffData> Applicants => _applicants;
        public NetworkList<StaffData> Hired => _hired;
        public float StaffLaneZ => staffLaneZ;
        public Vector3 GlassReturnPosition => glassReturnPoint != null ? glassReturnPoint.position : transform.position;

        private void Awake()
        {
            _applicants = new NetworkList<StaffData>();
            _hired = new NetworkList<StaffData>();
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) ServerRollApplicants(DayClock.Instance != null ? DayClock.Instance.Day : 1);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- noticeboard requests (any client)

        [Rpc(SendTo.Server)]
        public void RequestHireRpc(int applicantId)
        {
            if (_hired.Count >= MaxStaff) return;
            int index = IndexOf(_applicants, applicantId);
            if (index < 0) return;

            StaffData data = _applicants[index];
            _applicants.RemoveAt(index);
            data.Rota = 0b1111111;
            _hired.Add(data);
            DayClock.Instance?.ServerAnnounce($"Hired {data.Name}! They start next shift.", 3f);
        }

        [Rpc(SendTo.Server)]
        public void RequestFireRpc(int staffId)
        {
            int index = IndexOf(_hired, staffId);
            if (index < 0) return;
            StaffData data = _hired[index];
            _hired.RemoveAt(index);
            foreach (var worker in _workers.ToArray())
                if (worker != null && worker.Data.Id == staffId) worker.ServerGoHome("Fine, I'm off then!");
            DayClock.Instance?.ServerAnnounce($"{data.Name} has been let go.", 3f);
        }

        [Rpc(SendTo.Server)]
        public void RequestToggleRotaRpc(int staffId, int weekday)
        {
            int index = IndexOf(_hired, staffId);
            if (index < 0 || weekday < 0 || weekday > 6) return;
            StaffData data = _hired[index];
            data.Rota ^= (byte)(1 << weekday);
            _hired[index] = data;
        }

        // ---------------------------------------------------------------- day hooks (server, called by DayClock)

        public void ServerOnDayStarted(int day)
        {
            if (!IsServer) return;
            _workedToday.Clear();
            int weekday = StaffNames.WeekdayOf(day);
            for (int i = 0; i < _hired.Count; i++)
            {
                if (!_hired[i].WorksOn(weekday)) continue;
                _workedToday.Add(_hired[i]);
                SpawnWorker(_hired[i], i);
            }
            if (_workedToday.Count > 0) DayClock.Instance?.ServerAnnounce($"{_workedToday.Count} staff on shift today", 2.5f);
        }

        /// <summary>Pay everyone who worked, send them home. Call before the ledger closes the day.</summary>
        public void ServerOnDayEnded()
        {
            if (!IsServer) return;
            int wages = 0;
            foreach (var data in _workedToday) wages += data.WagePence;
            if (wages > 0)
            {
                BarTill.Instance?.ServerSpend(wages, transform.position);
                PubLedger.Instance?.ServerRecordWages(wages);
            }
            _workedToday.Clear();

            foreach (var worker in _workers.ToArray())
                if (worker != null && worker.IsSpawned) worker.NetworkObject.Despawn();
            _workers.Clear();
            _claimedCustomers.Clear();
            _claimedGlasses.Clear();
        }

        /// <summary>New faces on the noticeboard each day (deterministic per day).</summary>
        public void ServerRollApplicants(int day)
        {
            if (!IsServer) return;
            _applicants.Clear();
            var rng = new System.Random(day * 7919 + 17);
            for (int i = 0; i < ApplicantsPerDay; i++)
                _applicants.Add(StaffNames.RandomApplicant(rng, _nextId++));
        }

        // ---------------------------------------------------------------- saving

        public StaffData[] ServerGetHired()
        {
            var result = new StaffData[_hired.Count];
            for (int i = 0; i < _hired.Count; i++) result[i] = _hired[i];
            return result;
        }

        public void ServerSetHired(IEnumerable<StaffData> staff)
        {
            if (!IsServer) return;
            _hired.Clear();
            foreach (var s in staff)
            {
                _hired.Add(s);
                _nextId = Mathf.Max(_nextId, s.Id + 1);
            }
            // Make sure applicant ids don't collide with staff ids.
            ServerRollApplicants(DayClock.Instance != null ? DayClock.Instance.Day : 1);
        }

        // ---------------------------------------------------------------- worker coordination (server)

        public Vector3 IdlePosition(int slot) =>
            idleSpots != null && idleSpots.Length > 0 ? idleSpots[Mathf.Abs(slot) % idleSpots.Length].position : transform.position;

        public bool TryClaimCustomer(Customer c) => c != null && _claimedCustomers.Add(c);
        public void ReleaseCustomer(Customer c) { if (c != null) _claimedCustomers.Remove(c); }
        public bool TryClaimGlass(PintGlass g) => g != null && _claimedGlasses.Add(g);
        public void ReleaseGlass(PintGlass g) { if (g != null) _claimedGlasses.Remove(g); }
        public bool IsGlassClaimed(PintGlass g) => _claimedGlasses.Contains(g);
        public bool IsCustomerClaimed(Customer c) => _claimedCustomers.Contains(c);

        public void ServerOnWorkerGone(StaffWorker worker) => _workers.Remove(worker);

        private void SpawnWorker(StaffData data, int slot)
        {
            if (workerPrefab == null) return;
            Vector3 at = arrivalPoint != null ? arrivalPoint.position : transform.position;
            NetworkObject obj = Instantiate(workerPrefab, at, Quaternion.identity);
            obj.Spawn();
            var worker = obj.GetComponent<StaffWorker>();
            _workers.Add(worker);
            worker.ServerInit(this, data, slot);
        }

        private static int IndexOf(NetworkList<StaffData> list, int id)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id) return i;
            return -1;
        }
    }
}
