using BarOwner.Bar;
using BarOwner.Day;
using BarOwner.Economy;
using BarOwner.Interaction;
using BarOwner.Player;
using BarOwner.Staff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

namespace BarOwner.Customers
{
    public enum CustomerState : byte
    {
        Arriving = 0,
        Queueing = 1,
        WalkingToBar = 2,
        Ordering = 3,
        WalkingToSeat = 4,
        Drinking = 5,
        Leaving = 6,
    }

    /// <summary>
    /// A pub customer. The SERVER runs the brain (NavMeshAgent pathing, patience, ordering, paying);
    /// clients only see the synced state: position, speech bubble, patience bar and the pint in hand.
    /// Flow: arrive → queue (if the bar is full) → order at the bar → get served or storm out →
    /// drink at a table → leave the empty glass behind → go home.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class Customer : NetworkBehaviour, IInteractable
    {
        [Header("Visuals")]
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private GameObject speechRoot;
        [SerializeField] private Text speech;
        [SerializeField] private GameObject patienceRoot;
        [SerializeField] private RectTransform patienceFill;
        [SerializeField] private Image patienceImage;
        [SerializeField] private GameObject heldPint;
        [SerializeField] private Renderer heldPintRenderer;

        [Header("Behaviour")]
        [SerializeField, Tooltip("Seconds of waiting at the bar before they storm out (randomised ±30%).")]
        private float patienceSeconds = 50f;
        [SerializeField, Tooltip("Patience drains this much slower while queueing.")] private float queuePatienceFactor = 0.4f;
        [SerializeField] private Vector2 drinkSeconds = new(25f, 45f);
        [SerializeField] private float serveRange = 3f;
        [SerializeField, Tooltip("Patience lost when you hand over the wrong thing.")] private float refusePenalty = 0.12f;

        private static readonly Color[] Outfits =
        {
            new(0.75f, 0.3f, 0.25f), new(0.25f, 0.45f, 0.7f), new(0.3f, 0.6f, 0.35f), new(0.85f, 0.65f, 0.2f),
            new(0.55f, 0.35f, 0.65f), new(0.2f, 0.2f, 0.25f), new(0.9f, 0.9f, 0.88f), new(0.6f, 0.4f, 0.25f),
        };

        private readonly NetworkVariable<CustomerState> _state = new(CustomerState.Arriving);
        private readonly NetworkVariable<DrinkType> _wants = new(DrinkType.None);
        private readonly NetworkVariable<DrinkType> _holding = new(DrinkType.None);
        private readonly NetworkVariable<float> _patience = new(1f);
        private readonly NetworkVariable<int> _seed = new(0);

        // Server-only brain state.
        private NavMeshAgent _agent;
        private CustomerDirector _director;
        private float _patienceRate;
        private float _priceTolerance; // how much dearer than standard they'll pay (1.1 = +10%)
        private float _drinkUntil;
        private Vector3 _faceDirection;
        private Vector3 _glassDropPoint;
        private bool _hasDestination;

        // Client-side visuals.
        private float _reactionUntil;
        private MaterialPropertyBlock _props;

        /// <summary>Every spawned customer (staff look for people waiting).</summary>
        public static readonly System.Collections.Generic.List<Customer> All = new();

        public CustomerState State => _state.Value;
        public DrinkType WantsDrink => _wants.Value;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false; // only the server paths; clients just follow the NetworkTransform
            _props = new MaterialPropertyBlock();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            _state.OnValueChanged += OnStateChanged;
            _wants.OnValueChanged += OnWantsChanged;
            _holding.OnValueChanged += OnHoldingChanged;
            _seed.OnValueChanged += OnSeedChanged;
            ApplyLook();
            RefreshVisuals();

            if (IsServer)
            {
                _agent.enabled = true;
                if (!_agent.Warp(transform.position))
                    Debug.LogWarning("[Customer] Spawned off the NavMesh. Did the CustomerDirector build it (navMeshRoot set)?");
            }
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            _state.OnValueChanged -= OnStateChanged;
            _wants.OnValueChanged -= OnWantsChanged;
            _holding.OnValueChanged -= OnHoldingChanged;
            _seed.OnValueChanged -= OnSeedChanged;
            if (IsServer && _director != null) _director.ServerOnCustomerGone(this);
        }

        // ================================================================ server brain

        /// <summary>SERVER: called by the director right after spawning.</summary>
        public void ServerInit(CustomerDirector director, int seed)
        {
            _director = director;
            _seed.Value = seed;
            var rng = new System.Random(seed);
            _patienceRate = 1f / (patienceSeconds * (0.7f + 0.6f * (float)rng.NextDouble()));
            _priceTolerance = 0.95f + 0.4f * (float)rng.NextDouble();
        }

        public void ServerQueueAt(Vector3 position)
        {
            _state.Value = CustomerState.Queueing;
            MoveTo(position);
        }

        public void ServerGoToBar(Transform spot)
        {
            _state.Value = CustomerState.WalkingToBar;
            _faceDirection = spot.forward;
            MoveTo(spot.position);
        }

        private void Update()
        {
            if (IsServer && IsSpawned && _director != null) ServerTick();
            ClientTick();
        }

        private void ServerTick()
        {
            var clock = DayClock.Instance;
            bool closing = clock != null && !clock.IsServing;
            float dt = Time.deltaTime;

            switch (_state.Value)
            {
                case CustomerState.Arriving:
                    if (!_director.ServerRequestPlace(this)) Leave();
                    break;

                case CustomerState.Queueing:
                    if (closing) { Leave(); break; }
                    _patience.Value = Mathf.Max(0f, _patience.Value - _patienceRate * queuePatienceFactor * dt);
                    if (_patience.Value <= 0f) StormOut();
                    break;

                case CustomerState.WalkingToBar:
                    if (closing) { Leave(); break; }
                    if (Arrived()) BeginOrdering();
                    break;

                case CustomerState.Ordering:
                    if (closing) { ReactRpc("Aw, closed already?"); Leave(); break; }
                    FaceTowards(_faceDirection);
                    _patience.Value = Mathf.Max(0f, _patience.Value - _patienceRate * dt);
                    if (_patience.Value <= 0f) StormOut();
                    break;

                case CustomerState.WalkingToSeat:
                    if (Arrived())
                    {
                        _state.Value = CustomerState.Drinking;
                        _drinkUntil = Time.time + Random.Range(drinkSeconds.x, drinkSeconds.y);
                    }
                    break;

                case CustomerState.Drinking:
                    FaceTowards(_faceDirection);
                    // At closing time they neck it.
                    if (Time.time >= _drinkUntil || (clock != null && clock.Phase == PubPhase.ClosingUp))
                        FinishDrink();
                    break;

                case CustomerState.Leaving:
                    if (Arrived()) NetworkObject.Despawn();
                    break;
            }
        }

        private void BeginOrdering()
        {
            DrinkType wants = Drinks.OnTap[Random.Range(0, Drinks.OnTap.Length)];
            var ledger = PubLedger.Instance;
            if (ledger != null && ledger.PriceRatio(wants) > EffectiveTolerance())
            {
                ReactRpc($"{Money.Format(ledger.PricePence(wants))} for a {Drinks.Name(wants)}?! No chance.");
                ledger.ServerRecordPricedOut();
                Leave();
                return;
            }

            _state.Value = CustomerState.Ordering;
            _wants.Value = wants;
        }

        /// <summary>A well-loved pub can charge more before people balk.</summary>
        private float EffectiveTolerance()
        {
            float reputation = PubLedger.Instance != null ? PubLedger.Instance.Reputation : 1f;
            return _priceTolerance * (1f + 0.05f * reputation);
        }

        private void StormOut()
        {
            ReactRpc(Random.value < 0.5f ? "Forget it, I'm off!" : "Worst service in town!");
            PubLedger.Instance?.ServerRecordWalkout();
            Leave();
        }

        private void FinishDrink()
        {
            _director.ServerSpawnEmptyGlass(_glassDropPoint);
            _holding.Value = DrinkType.None;
            Leave();
        }

        private void Leave()
        {
            _director.ServerReleaseEverything(this);
            _wants.Value = DrinkType.None;
            _state.Value = CustomerState.Leaving;
            MoveTo(_director.ExitPosition);
        }

        private void MoveTo(Vector3 position)
        {
            if (!_agent.enabled || !_agent.isOnNavMesh) return;
            _agent.SetDestination(position);
            _hasDestination = true;
        }

        private bool Arrived()
        {
            if (!_hasDestination || !_agent.enabled || !_agent.isOnNavMesh) return true;
            if (_agent.pathPending) return false;
            return _agent.remainingDistance <= _agent.stoppingDistance + 0.2f;
        }

        private void FaceTowards(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction), 6f * Time.deltaTime);
        }

        // ================================================================ serving (IInteractable)

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!IsSpawned || _state.Value != CustomerState.Ordering) return null;
            var glass = interactor.Held != null ? interactor.Held.GetComponent<PintGlass>() : null;
            return glass != null ? $"Serve {glass.Describe()}" : null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            var glass = interactor.Held != null ? interactor.Held.GetComponent<PintGlass>() : null;
            if (glass != null) ServeRpc(glass.NetworkObject);
        }

        [Rpc(SendTo.Server)]
        private void ServeRpc(NetworkObjectReference glassRef, RpcParams rpcParams = default)
        {
            if (_state.Value != CustomerState.Ordering || _wants.Value == DrinkType.None) return;
            if (!glassRef.TryGet(out NetworkObject glassObject)) return;
            var glass = glassObject.GetComponent<PintGlass>();
            if (glass == null) return;

            // Validate: the sender really is holding it, and it's actually here.
            if (glass.Grabbable.HolderId != rpcParams.Receive.SenderClientId) return;
            if (Vector3.Distance(glass.transform.position, transform.position) > serveRange) return;

            DrinkType wanted = _wants.Value;
            string refusal = null;
            if (glass.IsEmpty) refusal = "It's empty, mate.";
            else if (glass.Drink == DrinkType.Slops) refusal = "What on earth is THAT?";
            else if (glass.Drink != wanted) refusal = $"I asked for {Drinks.Name(wanted)}!";
            else if (glass.Fill < 0.6f) refusal = "Half a pint? Top it up!";

            if (refusal != null)
            {
                _patience.Value = Mathf.Max(0.05f, _patience.Value - refusePenalty);
                ReactRpc(refusal);
                return;
            }

            float fill = glass.Fill;
            glassObject.Despawn(); // it's theirs now; the empty comes back later
            ServerCompleteServe(wanted, fill, null);
        }

        /// <summary>SERVER: a member of staff hands over a pint they poured.</summary>
        public void ServerServeByStaff(DrinkType drink, float fill, StaffWorker staff)
        {
            if (!IsServer || _state.Value != CustomerState.Ordering || drink != _wants.Value) return;
            if (fill < 0.6f) { ReactRpc("That's barely half a pint!"); return; }
            ServerCompleteServe(drink, fill, staff);
        }

        /// <summary>SERVER: take payment, then go and sit down with the drink.</summary>
        private void ServerCompleteServe(DrinkType wanted, float fill, StaffWorker staff)
        {
            var ledger = PubLedger.Instance;
            int price = ledger != null ? ledger.PricePence(wanted) : Drinks.PricePence(wanted);
            bool feelsCheap = ledger == null || ledger.PriceRatio(wanted) <= EffectiveTolerance() * 0.85f;
            int paid, tip = 0;
            bool shortPint = fill < 0.95f;
            string line;
            if (!shortPint)
            {
                paid = price;
                // Quick, full, fairly-priced pints earn tips: the happier they were, the likelier.
                float charm = staff != null ? staff.CharmTipBonus : 0f;
                if (feelsCheap && Random.value < 0.15f + 0.35f * _patience.Value + charm)
                {
                    tip = Mathf.Max(10, Mathf.RoundToInt(price * 0.1f / 10f) * 10);
                    line = "Lovely pint! Keep the change.";
                }
                else line = feelsCheap ? "Cheers!" : "Pricey, but cheers.";
            }
            else
            {
                paid = Mathf.RoundToInt(price * fill / 10f) * 10;
                line = "Bit short, that...";
            }

            int skimmed = staff != null ? staff.ServerMaybeSkim(paid + tip) : 0;
            BarTill.Instance?.ServerDeposit(paid + tip - skimmed, transform.position + Vector3.up * 2f);
            ledger?.ServerRecordSale(paid, tip, shortPint);
            if (skimmed > 0) ledger?.ServerRecordSkimmed(skimmed);
            ReactRpc($"{line}  (+{Money.Format(paid + tip)})");

            _wants.Value = DrinkType.None;
            _holding.Value = wanted;
            _director.ServerReleaseBarSpot(this);

            if (_director.ServerTakeSeat(this, out Vector3 seat, out Vector3 face, out Vector3 glassDrop))
            {
                _faceDirection = face;
                _glassDropPoint = glassDrop;
            }
            else
            {
                // No seat: prop up the wall near the door and leave the glass on the floor, like a true regular.
                seat = _director.StandingPosition();
                _faceDirection = Vector3.forward;
                _glassDropPoint = seat + Vector3.up * 0.1f + transform.forward * 0.4f;
            }

            _state.Value = CustomerState.WalkingToSeat;
            MoveTo(seat);
        }

        // ================================================================ visuals (everyone)

        [Rpc(SendTo.Everyone)]
        private void ReactRpc(string line)
        {
            if (speech != null) speech.text = line;
            _reactionUntil = Time.time + 2.5f;
            RefreshVisuals();
        }

        private void ClientTick()
        {
            if (_reactionUntil > 0f && Time.time >= _reactionUntil)
            {
                _reactionUntil = 0f;
                RefreshVisuals();
            }

            bool waiting = _state.Value == CustomerState.Queueing || _state.Value == CustomerState.Ordering;
            if (waiting && patienceFill != null)
            {
                float p = _patience.Value;
                patienceFill.localScale = new Vector3(p, 1f, 1f);
                if (patienceImage != null)
                    patienceImage.color = p > 0.5f ? Color.Lerp(Color.yellow, Color.green, (p - 0.5f) * 2f) : Color.Lerp(Color.red, Color.yellow, p * 2f);
            }
        }

        private void OnStateChanged(CustomerState previous, CustomerState current) => RefreshVisuals();
        private void OnWantsChanged(DrinkType previous, DrinkType current) => RefreshVisuals();
        private void OnHoldingChanged(DrinkType previous, DrinkType current) => RefreshVisuals();
        private void OnSeedChanged(int previous, int current) => ApplyLook();

        private void RefreshVisuals()
        {
            bool reacting = _reactionUntil > 0f;
            bool ordering = _state.Value == CustomerState.Ordering && _wants.Value != DrinkType.None;

            if (speechRoot != null) speechRoot.SetActive(reacting || ordering);
            if (!reacting && ordering && speech != null) speech.text = $"Pint of {Drinks.Name(_wants.Value)}, please!";

            if (patienceRoot != null)
                patienceRoot.SetActive(_state.Value == CustomerState.Queueing || _state.Value == CustomerState.Ordering);

            bool holding = _holding.Value != DrinkType.None;
            if (heldPint != null) heldPint.SetActive(holding);
            if (holding && heldPintRenderer != null)
            {
                heldPintRenderer.GetPropertyBlock(_props);
                _props.SetColor("_BaseColor", Drinks.LiquidColour(_holding.Value));
                heldPintRenderer.SetPropertyBlock(_props);
            }
        }

        private void ApplyLook()
        {
            var rng = new System.Random(_seed.Value);
            if (bodyRenderer != null)
            {
                bodyRenderer.GetPropertyBlock(_props);
                _props.SetColor("_BaseColor", Outfits[rng.Next(Outfits.Length)]);
                bodyRenderer.SetPropertyBlock(_props);
            }
            float height = 0.9f + 0.2f * (float)rng.NextDouble();
            transform.localScale = new Vector3(1f, height, 1f);
        }
    }
}
