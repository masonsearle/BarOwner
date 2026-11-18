using System.Collections.Generic;
using BarOwner.Bar;
using BarOwner.Day;
using BarOwner.Decor;
using BarOwner.Economy;
using BarOwner.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace BarOwner.Customers
{
    /// <summary>
    /// SERVER: spawns customers while the pub is open (busier in the evening) and hands out places:
    /// spots at the bar, a queue for when the bar is full, and seats at tables. Also builds the
    /// NavMesh at runtime on the server (clients don't need one).
    /// </summary>
    public class CustomerDirector : NetworkBehaviour
    {
        public static CustomerDirector Instance { get; private set; }

        [Header("Prefabs")]
        [SerializeField] private NetworkObject customerPrefab;
        [SerializeField] private NetworkObject glassPrefab;

        [Header("Places")]
        [SerializeField, Tooltip("Root of the building's colliders; the NavMesh is built from everything under it.")]
        private Transform navMeshRoot;
        [SerializeField, Tooltip("Smaller than Unity's default 0.5 so staff fit down the walkway behind the bar.")]
        private float navAgentRadius = 0.22f;
        [SerializeField] private Transform entrance;
        [SerializeField] private Transform exit;
        [SerializeField] private Transform standingArea;
        [SerializeField, Tooltip("Where customers stand to order; forward = facing the bar.")] private Transform[] barSpots;
        [SerializeField, Tooltip("Queue line, first = next to be served.")] private Transform[] queueSpots;
        [SerializeField, Tooltip("Seats; forward = facing the table.")] private Transform[] seats;

        [Header("Crowd")]
        [SerializeField] private int maxCustomers = 12;
        [SerializeField, Tooltip("Seconds between arrivals when it's quiet.")] private float quietInterval = 24f;
        [SerializeField, Tooltip("Seconds between arrivals at the evening peak.")] private float busyInterval = 8f;
        [SerializeField] private int maxGlassesInPub = 24;

        private readonly List<Customer> _active = new();
        private readonly List<Customer> _queue = new();
        private Customer[] _barOccupants;
        private Customer[] _seatOccupants;
        private float _nextSpawnAt;
        private bool _wasAccepting;
        private NavMeshDataInstance _navMesh;

        public int ActiveCustomers => _active.Count;
        public Vector3 ExitPosition => exit != null ? exit.position : transform.position;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer) return;

            _barOccupants = new Customer[barSpots.Length];
            _seatOccupants = new Customer[seats.Length];
            BuildNavMesh();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            if (_navMesh.valid) NavMesh.RemoveNavMeshData(_navMesh);
        }

        /// <summary>
        /// Builds the walkable area at runtime from the building's colliders, with a slim agent radius
        /// so the narrow walkway behind the bar (and the cellar ramp) are walkable.
        /// </summary>
        private void BuildNavMesh()
        {
            if (navMeshRoot == null)
            {
                Debug.LogError("[CustomerDirector] No navMeshRoot assigned; customers and staff can't walk.");
                return;
            }

            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = navAgentRadius;
            settings.agentHeight = 1.8f;
            settings.agentClimb = 0.3f;
            settings.agentSlope = 40f;

            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(navMeshRoot, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
                new List<NavMeshBuildMarkup>(), sources);
            var bounds = new Bounds(navMeshRoot.position + new Vector3(0f, 0f, 4f), new Vector3(40f, 14f, 44f));
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (data != null) _navMesh = NavMesh.AddNavMeshData(data);
            else Debug.LogError("[CustomerDirector] NavMesh build failed.");
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;
            var clock = DayClock.Instance;
            bool accepting = clock != null && clock.AcceptingNewCustomers;

            if (accepting && !_wasAccepting) _nextSpawnAt = Time.time + 2f; // first customer soon after opening
            _wasAccepting = accepting;

            if (!accepting || _active.Count >= maxCustomers || Time.time < _nextSpawnAt) return;

            SpawnCustomer();
            // Word gets around: a 5-star pub draws customers ~2.5x as often as a 0-star dive.
            float reputation = PubLedger.Instance != null ? PubLedger.Instance.Reputation : 1f;
            float popularity = Mathf.Lerp(1.5f, 0.6f, reputation / 5f);
            if (Atmosphere.Instance != null) popularity *= Mathf.Lerp(1f, 0.8f, Atmosphere.Instance.Normalised);
            if (EventSchedule.Instance != null) popularity *= ThemeNights.ArrivalIntervalMultiplier(EventSchedule.Instance.Tonight);
            _nextSpawnAt = Time.time + ArrivalInterval(clock.Hour) * popularity * Random.Range(0.7f, 1.3f);
        }

        /// <summary>Quiet afternoon, lunchtime bump, busy evening.</summary>
        private float ArrivalInterval(float hour)
        {
            float busyness = hour switch
            {
                >= 17f and < 21.5f => 1f,
                >= 12f and < 14f => 0.45f,
                >= 21.5f => 0.6f,
                _ => 0.25f,
            };
            return Mathf.Lerp(quietInterval, busyInterval, busyness);
        }

        private void SpawnCustomer()
        {
            Vector3 at = entrance != null ? entrance.position : transform.position;
            NetworkObject obj = Instantiate(customerPrefab, at, entrance != null ? entrance.rotation : Quaternion.identity);
            obj.Spawn();
            var customer = obj.GetComponent<Customer>();
            _active.Add(customer);
            customer.ServerInit(this, Random.Range(int.MinValue, int.MaxValue));
        }

        // ---------------------------------------------------------------- places (server, called by Customer)

        /// <summary>Send a new arrival to a free bar spot, or the back of the queue. False = no room, go home.</summary>
        public bool ServerRequestPlace(Customer c)
        {
            int spot = FreeBarSpot();
            if (spot >= 0)
            {
                _barOccupants[spot] = c;
                c.ServerGoToBar(barSpots[spot]);
                return true;
            }

            if (_queue.Count >= queueSpots.Length) return false;
            _queue.Add(c);
            c.ServerQueueAt(queueSpots[_queue.Count - 1].position);
            return true;
        }

        public void ServerReleaseBarSpot(Customer c)
        {
            for (int i = 0; i < _barOccupants.Length; i++)
            {
                if (_barOccupants[i] != c) continue;
                _barOccupants[i] = null;

                // Next in the queue steps up.
                if (_queue.Count > 0)
                {
                    Customer next = _queue[0];
                    _queue.RemoveAt(0);
                    _barOccupants[i] = next;
                    next.ServerGoToBar(barSpots[i]);
                    ShuffleQueueForward();
                }
            }
        }

        public bool ServerTakeSeat(Customer c, out Vector3 position, out Vector3 facing, out Vector3 glassDrop)
        {
            position = facing = glassDrop = Vector3.zero;
            int free = 0;
            for (int i = 0; i < _seatOccupants.Length; i++) if (_seatOccupants[i] == null) free++;
            if (free == 0) return false;

            int pick = Random.Range(0, free);
            for (int i = 0; i < _seatOccupants.Length; i++)
            {
                if (_seatOccupants[i] != null) continue;
                if (pick-- > 0) continue;
                _seatOccupants[i] = c;
                Transform seat = seats[i];
                position = seat.position;
                facing = seat.forward;
                glassDrop = seat.position + seat.forward * 0.8f + Vector3.up * 0.86f; // on the table top
                return true;
            }
            return false;
        }

        public Vector3 StandingPosition()
        {
            Vector3 centre = standingArea != null ? standingArea.position : ExitPosition;
            Vector2 offset = Random.insideUnitCircle * 0.9f;
            return centre + new Vector3(offset.x, 0f, offset.y);
        }

        /// <summary>Free every place this customer held (bar spot, queue slot, seat).</summary>
        public void ServerReleaseEverything(Customer c)
        {
            ServerReleaseBarSpot(c);
            if (_queue.Remove(c)) ShuffleQueueForward();
            for (int i = 0; i < _seatOccupants.Length; i++)
                if (_seatOccupants[i] == c) _seatOccupants[i] = null;
        }

        public void ServerOnCustomerGone(Customer c)
        {
            ServerReleaseEverything(c);
            _active.Remove(c);
        }

        public void ServerSpawnEmptyGlass(Vector3 position)
        {
            if (glassPrefab == null || PintGlass.All.Count >= maxGlassesInPub) return; // they "took it home"
            NetworkObject glass = Instantiate(glassPrefab, position, Quaternion.identity);
            glass.DontDestroyWithOwner = true;
            glass.Spawn();
        }

        private int FreeBarSpot()
        {
            for (int i = 0; i < _barOccupants.Length; i++)
                if (_barOccupants[i] == null) return i;
            return -1;
        }

        private void ShuffleQueueForward()
        {
            for (int i = 0; i < _queue.Count; i++)
                _queue[i].ServerQueueAt(queueSpots[i].position);
        }
    }
}
