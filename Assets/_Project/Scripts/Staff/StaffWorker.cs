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
    /// A member of staff on shift. Works the bar the way a player would, so you can watch them do it:
    ///  serve: grab a clean glass from the rack → walk to the right tap → pull it (handle + stream
    ///         show for everyone, the glass in their hand fills, the keg drains) → carry it over
    ///         → hand it across the bar.
    ///  clear: walk out to an empty glass on the customer side, pick it up, take it back to the rack.
    ///  idle:  wait behind the bar.
    /// Staff can't change kegs: a dry tap stops them and they shout for a player.
    /// SERVER runs the brain; clients see movement, name tag, status and the glass in hand.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class StaffWorker : NetworkBehaviour
    {
        private enum Task { None, FetchGlass, WalkToTap, Pouring, CarryToCustomer, WalkToEmpty, ReturnEmpty, GoingHome }

        [SerializeField] private Text nameTag;
        [SerializeField] private Text statusText;
        [SerializeField] private GameObject carriedGlass;
        [SerializeField] private Transform carriedLiquid;
        [SerializeField] private Renderer carriedLiquidRenderer;
        [SerializeField, Tooltip("Customer-side glasses are those with z below this (the bar front).")]
        private float publicSideMaxZ = 2.4f;
        [SerializeField] private float handOverReach = 2.4f;
        [SerializeField, Tooltip("Seconds a finished pint waits for someone to want it before going down the sink.")]
        private float pintShelfLife = 12f;

        private readonly NetworkVariable<FixedString32Bytes> _name = new();
        private readonly NetworkVariable<FixedString64Bytes> _status = new();
        private readonly NetworkVariable<bool> _carrying = new();
        private readonly NetworkVariable<float> _glassFill = new();
        private readonly NetworkVariable<DrinkType> _glassDrink = new();

        // Server-only.
        private NavMeshAgent _agent;
        private StaffManager _manager;
        private StaffData _data;
        private int _slot;
        private Task _task;
        private Customer _customer;
        private DrinkType _order;
        private BeerTap _tap;
        private PintGlass _empty;
        private float _nextThinkAt;
        private float _pintReadyAt;
        private MaterialPropertyBlock _props;

        public StaffData Data => _data;
        public bool GlassIsFull => _glassFill.Value >= 0.99f;
        public float PourRateMultiplier => Mathf.Lerp(0.75f, 1.4f, (_data.Speed - 1) / 4f);
        public float CharmTipBonus => 0.08f * (_data.Charm - 3);

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false;
            _props = new MaterialPropertyBlock();
        }

        public override void OnNetworkSpawn()
        {
            _name.OnValueChanged += (_, v) => { if (nameTag != null) nameTag.text = v.ToString(); };
            _status.OnValueChanged += (_, v) => { if (statusText != null) statusText.text = v.ToString(); };
            _carrying.OnValueChanged += (_, _) => RefreshGlass();
            _glassFill.OnValueChanged += (_, _) => RefreshGlass();
            _glassDrink.OnValueChanged += (_, _) => RefreshGlass();
            if (nameTag != null) nameTag.text = _name.Value.ToString();
            if (statusText != null) statusText.text = _status.Value.ToString();
            RefreshGlass();

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
            EmptyHands();
            SetStatus(line);
            _task = Task.GoingHome;
            var director = CustomerDirector.Instance;
            MoveTo(director != null ? director.ExitPosition : transform.position);
        }

        /// <summary>SERVER (called by the tap while pouring): beer into the glass in our hand.</summary>
        public void ServerAddToGlass(DrinkType drink, float amount)
        {
            _glassDrink.Value = drink;
            _glassFill.Value = Mathf.Min(1f, _glassFill.Value + amount);
        }

        /// <summary>SERVER: maybe pocket some of the money (dishonest staff). Returns pence stolen.</summary>
        public int ServerMaybeSkim(int amount)
        {
            float chance = (5 - _data.Honesty) * 0.07f; // honesty 5 = never, honesty 1 = 28% of sales
            return Random.value < chance ? Mathf.RoundToInt(amount * 0.5f / 10f) * 10 : 0;
        }

        // ================================================================ brain

        private void Update()
        {
            if (!IsServer || !IsSpawned || _manager == null) return;

            switch (_task)
            {
                case Task.None:
                    if (Time.time >= _nextThinkAt) Think();
                    break;

                case Task.FetchGlass:
                    if (!CustomerStillWaiting()) { DropTask(); break; }
                    if (Arrived(0.35f))
                    {
                        _carrying.Value = true;
                        _glassFill.Value = 0f;
                        _glassDrink.Value = DrinkType.None;
                        _tap = TapFor(_order);
                        if (_tap == null) { SetStatus("Where's the tap for that?!"); DropTask(); break; }
                        _task = Task.WalkToTap;
                        SetStatus($"Pulling a pint of {Drinks.Name(_order)}");
                        MoveTo(LaneSpot(_tap.transform.position.x));
                    }
                    break;

                case Task.WalkToTap:
                    if (Arrived(0.3f))
                    {
                        if (!_tap.HasBeer) { ShoutDryKeg(_order); DropTask(); break; }
                        if (_tap.IsPouring) break; // wait for whoever's using it
                        if (_tap.ServerStartStaffPour(this)) _task = Task.Pouring;
                    }
                    break;

                case Task.Pouring:
                    FaceTowards(Vector3.back);
                    if (_tap.IsStaffPouringFor(this)) break;
                    // Tap stopped: glass full, or the keg ran dry part way.
                    if (_glassFill.Value < 0.6f)
                    {
                        ShoutDryKeg(_order);
                        DropTask();
                        break;
                    }
                    _pintReadyAt = Time.time;
                    if (CustomerStillWaiting()) HeadToCustomerWithPint();
                    else LookForAnotherTaker();
                    break;

                case Task.CarryToCustomer:
                    if (!CustomerStillWaiting()) { LookForAnotherTaker(); break; }
                    if (Arrived(0.35f) || HorizontalDistance(_customer.transform.position) <= handOverReach * 0.7f)
                    {
                        FaceTowards(_customer.transform.position - transform.position);
                        HandOver();
                    }
                    break;

                case Task.WalkToEmpty:
                    if (_empty == null || !_empty.IsSpawned || _empty.Grabbable.IsHeld || !_empty.IsEmpty) { DropTask(); break; }
                    if (HorizontalDistance(_empty.transform.position) < 1.2f) PickUpEmpty();
                    else if (Arrived(0.3f)) DropTask(); // couldn't reach it
                    break;

                case Task.ReturnEmpty:
                    if (Arrived(0.4f))
                    {
                        EmptyHands();
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

            // 1) Someone waiting at the bar whose drink we can pour?
            Customer best = null;
            float bestDistance = float.MaxValue;
            DrinkType dry = DrinkType.None;
            foreach (var c in Customer.All)
            {
                if (!IsWaiting(c) || _manager.IsCustomerClaimed(c)) continue;
                var tap = TapFor(c.WantsDrink);
                if (tap != null && !tap.HasBeer) { dry = c.WantsDrink; continue; }
                float d = HorizontalDistance(c.transform.position);
                if (d < bestDistance) { best = c; bestDistance = d; }
            }

            if (best != null && _manager.TryClaimCustomer(best))
            {
                _customer = best;
                _order = best.WantsDrink;
                _task = Task.FetchGlass;
                SetStatus("Coming, love!");
                MoveTo(_manager.GlassReturnPosition);
                return;
            }

            if (dry != DrinkType.None) ShoutDryKeg(dry);

            // 2) Empties on the customer side?
            foreach (var g in PintGlass.All)
            {
                if (g == null || !g.IsSpawned || !g.IsEmpty || g.Grabbable.IsHeld) continue;
                if (g.transform.position.z > publicSideMaxZ || g.transform.position.y < -1f) continue;
                if (_manager.IsGlassClaimed(g) || !_manager.TryClaimGlass(g)) continue;
                _empty = g;
                _task = Task.WalkToEmpty;
                SetStatus("Collecting glasses");
                MoveTo(g.transform.position);
                return;
            }

            // 3) Nothing to do.
            GoIdle();
        }

        private void HeadToCustomerWithPint()
        {
            _task = Task.CarryToCustomer;
            SetStatus($"One {Drinks.Name(_order)}, coming up");
            MoveTo(LaneSpot(_customer.transform.position.x));
        }

        /// <summary>Our customer got served elsewhere or walked out: give the pint to someone else who wants one.</summary>
        private void LookForAnotherTaker()
        {
            _manager.ReleaseCustomer(_customer);
            _customer = null;

            if (Time.time - _pintReadyAt <= pintShelfLife)
            {
                foreach (var c in Customer.All)
                {
                    if (!IsWaiting(c) || c.WantsDrink != _order || _manager.IsCustomerClaimed(c)) continue;
                    if (!_manager.TryClaimCustomer(c)) continue;
                    _customer = c;
                    HeadToCustomerWithPint();
                    return;
                }
                _task = Task.CarryToCustomer; // hold on to it a moment longer
                SetStatus($"Anyone for a {Drinks.Name(_order)}?");
                return;
            }

            SetStatus("Oh well, down the sink");
            EmptyHands();
            DropTask();
        }

        private void HandOver()
        {
            _customer.ServerServeByStaff(_order, _glassFill.Value, this);
            EmptyHands();
            SetStatus("");
            DropTask();
        }

        private void PickUpEmpty()
        {
            _manager.ReleaseGlass(_empty);
            _empty.NetworkObject.Despawn(); // it's now "in hand"; frees a slot at the rack
            _empty = null;
            _carrying.Value = true;
            _glassFill.Value = 0f;
            _glassDrink.Value = DrinkType.None;
            _task = Task.ReturnEmpty;
            SetStatus("Taking it to be washed");
            MoveTo(_manager.GlassReturnPosition);
        }

        private void ShoutDryKeg(DrinkType drink) => SetStatus($"{Drinks.Name(drink)}'s off! Change the keg!");

        private void GoIdle()
        {
            _task = Task.None;
            MoveTo(_manager.IdlePosition(_slot));
        }

        private void DropTask()
        {
            if (_tap != null) _tap.ServerStopStaffPour(this);
            if (_manager != null)
            {
                _manager.ReleaseCustomer(_customer);
                _manager.ReleaseGlass(_empty);
            }
            if (_task == Task.FetchGlass || _task == Task.WalkToTap || _task == Task.Pouring || _task == Task.CarryToCustomer)
                EmptyHands();
            _customer = null;
            _empty = null;
            _tap = null;
            if (_task != Task.GoingHome) _task = Task.None;
            _nextThinkAt = Time.time + 0.3f;
        }

        private void EmptyHands()
        {
            _carrying.Value = false;
            _glassFill.Value = 0f;
            _glassDrink.Value = DrinkType.None;
        }

        // ================================================================ helpers

        private static bool IsWaiting(Customer c) =>
            c != null && c.IsSpawned && c.State == CustomerState.Ordering && c.WantsDrink != DrinkType.None;

        private bool CustomerStillWaiting() => IsWaiting(_customer);

        private static BeerTap TapFor(DrinkType drink)
        {
            foreach (var t in BeerTap.All)
                if (t != null && t.Drink == drink) return t;
            return null;
        }

        private Vector3 LaneSpot(float x) => new(x, 0f, _manager.StaffLaneZ);

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
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction), 8f * Time.deltaTime);
        }

        private void SetStatus(string text)
        {
            if (text.Length > 60) text = text.Substring(0, 60); // FixedString64Bytes holds ~61 UTF-8 bytes
            _status.Value = new FixedString64Bytes(text);
        }

        // ================================================================ visuals (everyone)

        private void RefreshGlass()
        {
            if (carriedGlass != null) carriedGlass.SetActive(_carrying.Value);
            if (carriedLiquid == null) return;

            float fill = _glassFill.Value;
            bool show = _carrying.Value && fill > 0.01f;
            carriedLiquid.gameObject.SetActive(show);
            if (!show) return;

            // Same maths as PintGlass: unit cylinder, local Y from -1 to +1.
            float height = 2f * fill * 0.96f;
            carriedLiquid.localScale = new Vector3(0.86f, Mathf.Max(0.001f, height * 0.5f), 0.86f);
            carriedLiquid.localPosition = new Vector3(0f, -0.98f + height * 0.5f, 0f);
            if (carriedLiquidRenderer != null)
            {
                carriedLiquidRenderer.GetPropertyBlock(_props);
                _props.SetColor("_BaseColor", Drinks.LiquidColour(_glassDrink.Value));
                carriedLiquidRenderer.SetPropertyBlock(_props);
            }
        }
    }
}
