using BarOwner.Bar;
using BarOwner.Customers;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

namespace BarOwner.Staff
{
    /// <summary>
    /// A member of staff on shift. SERVER brain, in priority order:
    ///  1. Serve a waiting customer nobody else is serving (pours from the tap's keg, so a dry keg
    ///     stops them, and changing kegs is still the players' job).
    ///  2. Collect empty glasses from the customer side of the pub.
    ///  3. Wait behind the bar.
    /// Clients just see them move, a name tag and what they're up to.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class StaffWorker : NetworkBehaviour
    {
        private enum Task { None, WalkToCustomer, Pouring, WalkToGlass, ReturnGlass, GoingHome }

        [SerializeField] private Text nameTag;
        [SerializeField] private Text statusText;
        [SerializeField] private GameObject carriedGlass;
        [SerializeField, Tooltip("Customer-side glasses are those with z below this (the bar front).")]
        private float publicSideMaxZ = 2.4f;
        [SerializeField] private float serveReach = 2.6f;

        private readonly NetworkVariable<FixedString32Bytes> _name = new();
        private readonly NetworkVariable<FixedString64Bytes> _status = new();
        private readonly NetworkVariable<bool> _carrying = new();

        // Server-only.
        private NavMeshAgent _agent;
        private StaffManager _manager;
        private StaffData _data;
        private int _slot;
        private Task _task;
        private Customer _customer;
        private PintGlass _glass;
        private float _taskTimer;
        private float _nextThinkAt;
        private float _pouredFill;
        private DrinkType _pouring;

        public StaffData Data => _data;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            _name.OnValueChanged += (_, v) => { if (nameTag != null) nameTag.text = v.ToString(); };
            _status.OnValueChanged += (_, v) => { if (statusText != null) statusText.text = v.ToString(); };
            _carrying.OnValueChanged += (_, v) => { if (carriedGlass != null) carriedGlass.SetActive(v); };
            if (nameTag != null) nameTag.text = _name.Value.ToString();
            if (statusText != null) statusText.text = _status.Value.ToString();
            if (carriedGlass != null) carriedGlass.SetActive(_carrying.Value);

            if (IsServer)
            {
                _agent.enabled = true;
                _agent.Warp(transform.position);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && _manager != null)
            {
                DropTask();
                _manager.ServerOnWorkerGone(this);
            }
        }

        public void ServerInit(StaffManager manager, StaffData data, int slot)
        {
            _manager = manager;
            _data = data;
            _slot = slot;
            _name.Value = data.Name;
            _agent.speed = 1.3f + 0.25f * data.Speed;
            SetStatus("Arriving for shift");
            GoIdle();
        }

        public void ServerGoHome(string line)
        {
            DropTask();
            SetStatus(line);
            _task = Task.GoingHome;
            MoveTo(Customers.CustomerDirector.Instance != null ? Customers.CustomerDirector.Instance.ExitPosition : transform.position);
        }

        /// <summary>SERVER: maybe pocket some of the money (dishonest staff). Returns pence stolen.</summary>
        public int ServerMaybeSkim(int amount)
        {
            float chance = (5 - _data.Honesty) * 0.07f; // honesty 5 = never, honesty 1 = 28% of sales
            return Random.value < chance ? Mathf.RoundToInt(amount * 0.5f / 10f) * 10 : 0;
        }

        public float CharmTipBonus => 0.08f * (_data.Charm - 3);

        // ================================================================ brain

        private void Update()
        {
            if (!IsServer || !IsSpawned || _manager == null) return;

            switch (_task)
            {
                case Task.None:
                    if (Time.time >= _nextThinkAt) Think();
                    break;

                case Task.WalkToCustomer:
                    if (!CustomerStillWaiting()) { DropTask(); break; }
                    if (Arrived(0.4f)) StartPouring();
                    break;

                case Task.Pouring:
                    if (!CustomerStillWaiting()) { SetStatus("Oh, they've been served"); DropTask(); break; }
                    FaceTowards(_customer.transform.position - transform.position);
                    _taskTimer -= Time.deltaTime;
                    if (_taskTimer <= 0f) FinishPouring();
                    break;

                case Task.WalkToGlass:
                    if (_glass == null || !_glass.IsSpawned || _glass.Grabbable.IsHeld || !_glass.IsEmpty) { DropTask(); break; }
                    if (HorizontalDistance(_glass.transform.position) < 1.3f) PickUpGlass();
                    else if (Arrived(0.3f)) DropTask(); // couldn't reach it
                    break;

                case Task.ReturnGlass:
                    if (Arrived(0.5f))
                    {
                        _carrying.Value = false;
                        SetStatus("");
                        _task = Task.None;
                    }
                    break;

                case Task.GoingHome:
                    if (Arrived(0.5f)) NetworkObject.Despawn();
                    break;
            }
        }

        private void Think()
        {
            _nextThinkAt = Time.time + 0.5f;

            // 1) Someone waiting at the bar whose drink we can actually pour?
            Customer best = null;
            float bestDistance = float.MaxValue;
            foreach (var c in FindCustomersWaiting())
            {
                if (_manager.IsCustomerClaimed(c)) continue;
                var socket = SocketFor(c.WantsDrink);
                if (socket != null && !socket.HasBeer) continue; // dry keg: a player has to change it
                float d = HorizontalDistance(c.transform.position);
                if (d < bestDistance) { best = c; bestDistance = d; }
            }
            if (best != null && _manager.TryClaimCustomer(best))
            {
                _customer = best;
                _task = Task.WalkToCustomer;
                SetStatus($"Coming, love!");
                MoveTo(LaneSpotFor(best));
                return;
            }

            // Shout about dry kegs so the players know.
            foreach (var c in FindCustomersWaiting())
            {
                var socket = SocketFor(c.WantsDrink);
                if (socket != null && !socket.HasBeer)
                {
                    SetStatus($"{Drinks.Name(c.WantsDrink)}'s off! Change the keg!");
                    break;
                }
            }

            // 2) Empties on the customer side?
            foreach (var g in PintGlass.All)
            {
                if (g == null || !g.IsSpawned || !g.IsEmpty || g.Grabbable.IsHeld) continue;
                if (g.transform.position.z > publicSideMaxZ || _manager.IsGlassClaimed(g)) continue;
                if (!_manager.TryClaimGlass(g)) continue;
                _glass = g;
                _task = Task.WalkToGlass;
                SetStatus("Collecting glasses");
                MoveTo(g.transform.position);
                return;
            }

            // 3) Nothing to do.
            GoIdle();
        }

        private void StartPouring()
        {
            _pouring = _customer.WantsDrink;
            var socket = SocketFor(_pouring);
            // Take a pint from the keg now; if it runs out mid-pour the customer gets a short one.
            _pouredFill = socket != null ? socket.ServerDrain(1f) : 1f;
            if (_pouredFill <= 0.01f)
            {
                SetStatus($"The {Drinks.Name(_pouring)}'s off!");
                DropTask();
                return;
            }
            _taskTimer = Mathf.Lerp(7f, 3f, (_data.Speed - 1) / 4f);
            _task = Task.Pouring;
            SetStatus($"Pouring a {Drinks.Name(_pouring)}...");
        }

        private void FinishPouring()
        {
            if (HorizontalDistance(_customer.transform.position) <= serveReach)
                _customer.ServerServeByStaff(_pouring, _pouredFill, this);
            SetStatus("");
            DropTask();
        }

        private void PickUpGlass()
        {
            _manager.ReleaseGlass(_glass);
            _glass.NetworkObject.Despawn(); // frees a slot at the rack
            _glass = null;
            _carrying.Value = true;
            _task = Task.ReturnGlass;
            SetStatus("Washing up");
            MoveTo(_manager.GlassReturnPosition);
        }

        private void GoIdle()
        {
            _task = Task.None;
            MoveTo(_manager.IdlePosition(_slot));
        }

        private void DropTask()
        {
            if (_manager != null)
            {
                _manager.ReleaseCustomer(_customer);
                _manager.ReleaseGlass(_glass);
            }
            _customer = null;
            _glass = null;
            if (_task != Task.GoingHome) _task = Task.None;
            _nextThinkAt = Time.time + 0.3f;
        }

        // ================================================================ helpers

        private bool CustomerStillWaiting() =>
            _customer != null && _customer.IsSpawned && _customer.State == CustomerState.Ordering;

        private static System.Collections.Generic.IEnumerable<Customer> FindCustomersWaiting()
        {
            foreach (var c in Customer.All)
                if (c != null && c.IsSpawned && c.State == CustomerState.Ordering && c.WantsDrink != DrinkType.None)
                    yield return c;
        }

        private static KegSocket SocketFor(DrinkType drink)
        {
            foreach (var s in KegSocket.All)
                if (s != null && s.Line == drink) return s;
            return null;
        }

        private Vector3 LaneSpotFor(Customer c) => new(c.transform.position.x, 0f, _manager.StaffLaneZ);

        private void MoveTo(Vector3 position)
        {
            if (_agent.enabled && _agent.isOnNavMesh) _agent.SetDestination(position);
        }

        private bool Arrived(float tolerance)
        {
            if (!_agent.enabled || !_agent.isOnNavMesh) return true;
            if (_agent.pathPending) return false;
            return _agent.remainingDistance <= _agent.stoppingDistance + tolerance;
        }

        private float HorizontalDistance(Vector3 p)
        {
            Vector3 d = p - transform.position;
            d.y = 0f;
            return d.magnitude;
        }

        private void FaceTowards(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction), 6f * Time.deltaTime);
        }

        private void SetStatus(string text)
        {
            if (text.Length > 60) text = text.Substring(0, 60); // FixedString64Bytes holds ~61 UTF-8 bytes
            _status.Value = new FixedString64Bytes(text);
        }
    }
}
