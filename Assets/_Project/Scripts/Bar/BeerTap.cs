using BarOwner.Interaction;
using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// Hold Interact on the tap to pour. The SERVER does the pouring: each frame it fills the target
    /// glass, which is (1) a glass the pouring player is holding near the tap, or (2) a glass sitting
    /// under the spout on the drip tray. Everyone sees the stream and the handle pulled.
    /// </summary>
    public class BeerTap : NetworkBehaviour, IHoldInteractable
    {
        private const ulong Nobody = ulong.MaxValue;

        [SerializeField] private DrinkType drink = DrinkType.Lager;
        [SerializeField] private Transform spout;
        [SerializeField] private Transform handle;
        [SerializeField] private GameObject stream;
        [SerializeField, Tooltip("The cellar coupler feeding this tap. Empty = endless beer (testing).")]
        private KegSocket keg;

        [Header("Pouring")]
        [SerializeField, Tooltip("Pints per second.")] private float pourRate = 0.22f;
        [SerializeField, Tooltip("A held glass this close to the spout gets filled.")] private float heldGlassRange = 1.6f;
        [SerializeField, Tooltip("A glass this close to the spout's drip point gets filled.")] private float trayRadius = 0.12f;
        [SerializeField, Tooltip("Safety: stop pouring after this long even if nobody let go.")] private float maxPourSeconds = 20f;

        private readonly NetworkVariable<ulong> _pourer = new(Nobody);

        private float _pourStartedAt;
        private float _spilt; // server-side running total; becomes "waste" in the M5 end-of-day report

        public DrinkType Drink => drink;
        public bool IsPouring => _pourer.Value != Nobody;

        public override void OnNetworkSpawn()
        {
            _pourer.OnValueChanged += OnPourerChanged;
            ApplyVisuals();
            if (IsServer) NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            _pourer.OnValueChanged -= OnPourerChanged;
            if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        // ---------------------------------------------------------------- IHoldInteractable (local)

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!IsSpawned) return null;
            string name = Drinks.Name(drink);
            if (keg != null && !keg.IsConnected) return $"~{name}: no keg connected. Plug one in down in the cellar";
            if (keg != null && !keg.HasBeer) return $"~{name}: *sputter* The keg's empty. Change it in the cellar";
            if (IsPouring && _pourer.Value != NetworkManager.LocalClientId) return $"~{name} (someone's pouring)";
            string price = Economy.PubLedger.Instance != null ? Money.Format(Economy.PubLedger.Instance.PricePence(drink)) : "";
            string left = keg != null ? $"   ({price} · {Mathf.FloorToInt(keg.Pints)} pints left)" : "";
            return $"to pour {name}{left}";
        }

        public void Interact(PlayerInteractor interactor) { } // pouring is hold-only

        public void HoldStart(PlayerInteractor interactor) => StartPourRpc();
        public void HoldEnd(PlayerInteractor interactor) => StopPourRpc();

        // ---------------------------------------------------------------- server

        [Rpc(SendTo.Server)]
        private void StartPourRpc(RpcParams rpcParams = default)
        {
            if (IsPouring || (keg != null && !keg.HasBeer)) return;
            _pourer.Value = rpcParams.Receive.SenderClientId;
            _pourStartedAt = Time.time;
        }

        [Rpc(SendTo.Server)]
        private void StopPourRpc(RpcParams rpcParams = default)
        {
            if (_pourer.Value == rpcParams.Receive.SenderClientId) _pourer.Value = Nobody;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_pourer.Value == clientId) _pourer.Value = Nobody;
        }

        private void Update()
        {
            if (!IsServer || !IsPouring) return;

            if (Time.time - _pourStartedAt > maxPourSeconds)
            {
                _pourer.Value = Nobody;
                return;
            }

            float amount = pourRate * Time.deltaTime;
            if (keg != null)
            {
                amount = keg.ServerDrain(amount);
                if (amount <= 0f)
                {
                    _pourer.Value = Nobody; // ran dry mid-pour
                    return;
                }
            }

            PintGlass target = FindTarget(_pourer.Value);
            if (target != null) _spilt += target.ServerAddDrink(drink, amount);
            else _spilt += amount;
        }

        private PintGlass FindTarget(ulong pourer)
        {
            Vector3 spoutPos = spout != null ? spout.position : transform.position;
            PintGlass best = null;
            float bestDistance = float.MaxValue;

            foreach (var glass in PintGlass.All)
            {
                if (glass == null || !glass.IsSpawned) continue;
                float distance = Vector3.Distance(glass.transform.position, spoutPos);

                // 1) The glass in the pourer's hand wins if it's near enough.
                if (glass.Grabbable.HolderId == pourer && distance <= heldGlassRange) return glass;

                // 2) Otherwise the closest unheld glass on the drip tray, roughly under the spout.
                if (glass.Grabbable.IsHeld) continue;
                Vector3 offset = glass.transform.position - spoutPos;
                bool underSpout = new Vector2(offset.x, offset.z).magnitude <= trayRadius && offset.y < 0f && offset.y > -0.45f;
                if (underSpout && distance < bestDistance)
                {
                    best = glass;
                    bestDistance = distance;
                }
            }

            return best;
        }

        // ---------------------------------------------------------------- visuals (everyone)

        private void OnPourerChanged(ulong previous, ulong current) => ApplyVisuals();

        private void ApplyVisuals()
        {
            bool pouring = IsPouring;
            if (stream != null) stream.SetActive(pouring);
            if (handle != null) handle.localRotation = Quaternion.Euler(pouring ? 35f : 0f, 0f, 0f);
        }
    }
}
