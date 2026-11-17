using BarOwner.Bar;
using BarOwner.Economy;
using BarOwner.Interaction;
using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.Customers
{
    /// <summary>
    /// M2 stand-in customer: stands at the bar, orders a random pint, and pays when you hand them
    /// one (Interact while holding a glass). Full pint = full price (+ maybe a tip); a short pint
    /// gets paid pro-rata with a moan; the wrong drink, slops or a mostly-empty glass gets refused.
    /// Real customers who walk in, queue and leave arrive in M3; their ordering will reuse this logic.
    /// </summary>
    public class TestCustomer : NetworkBehaviour, IInteractable
    {
        [SerializeField] private Text speech;
        [SerializeField] private float newOrderDelay = 4f;
        [SerializeField] private float serveRange = 3f;

        private readonly NetworkVariable<DrinkType> _wants = new(DrinkType.None);

        private float _nextOrderAt;
        private float _reactionUntil;

        public override void OnNetworkSpawn()
        {
            _wants.OnValueChanged += OnWantsChanged;
            if (IsServer) PickNewOrder();
            ShowOrder();
        }

        public override void OnNetworkDespawn() => _wants.OnValueChanged -= OnWantsChanged;

        // ---------------------------------------------------------------- IInteractable (local)

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!IsSpawned || _wants.Value == DrinkType.None) return null;
            var glass = interactor.Held != null ? interactor.Held.GetComponent<PintGlass>() : null;
            return glass != null ? $"Serve {glass.Describe()}" : null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            var glass = interactor.Held != null ? interactor.Held.GetComponent<PintGlass>() : null;
            if (glass != null) ServeRpc(glass.NetworkObject);
        }

        // ---------------------------------------------------------------- server

        [Rpc(SendTo.Server)]
        private void ServeRpc(NetworkObjectReference glassRef, RpcParams rpcParams = default)
        {
            if (_wants.Value == DrinkType.None) return;
            if (!glassRef.TryGet(out NetworkObject glassObject)) return;
            var glass = glassObject.GetComponent<PintGlass>();
            if (glass == null) return;

            // Validate: the sender really is holding it, and it's actually here.
            if (glass.Grabbable.HolderId != rpcParams.Receive.SenderClientId) return;
            if (Vector3.Distance(glass.transform.position, transform.position) > serveRange) return;

            DrinkType wanted = _wants.Value;
            string wantedName = Drinks.Name(wanted);

            if (glass.IsEmpty) { ReactRpc("It's empty, mate."); return; }
            if (glass.Drink == DrinkType.Slops) { ReactRpc("What on earth is THAT?"); return; }
            if (glass.Drink != wanted) { ReactRpc($"I asked for {wantedName}!"); return; }
            if (glass.Fill < 0.6f) { ReactRpc("Half a pint? Top it up!"); return; }

            int price = Drinks.PricePence(wanted);
            int paid;
            string line;
            if (glass.Fill >= 0.95f)
            {
                paid = price;
                bool tip = Random.value < 0.3f;
                if (tip) paid += Mathf.RoundToInt(price * 0.1f / 10f) * 10;
                line = tip ? "Lovely pint! Keep the change." : "Cheers!";
            }
            else
            {
                paid = Mathf.RoundToInt(price * glass.Fill / 10f) * 10;
                line = "Bit short, that...";
            }

            if (BarTill.Instance != null) BarTill.Instance.ServerDeposit(paid, transform.position + Vector3.up * 2f);
            glassObject.Despawn(); // they drink it (empties come back in M3)
            _wants.Value = DrinkType.None;
            _nextOrderAt = Time.time + newOrderDelay;
            ReactRpc($"{line}  (+{Money.Format(paid)})");
        }

        private void Update()
        {
            if (IsServer && IsSpawned && _wants.Value == DrinkType.None && Time.time >= _nextOrderAt)
                PickNewOrder();

            if (_reactionUntil > 0f && Time.time >= _reactionUntil)
            {
                _reactionUntil = 0f;
                ShowOrder();
            }
        }

        private void PickNewOrder() => _wants.Value = Drinks.OnTap[Random.Range(0, Drinks.OnTap.Length)];

        // ---------------------------------------------------------------- speech bubble (everyone)

        [Rpc(SendTo.Everyone)]
        private void ReactRpc(string line)
        {
            SetSpeech(line);
            _reactionUntil = Time.time + 2.5f;
        }

        private void OnWantsChanged(DrinkType previous, DrinkType current)
        {
            if (_reactionUntil <= 0f) ShowOrder();
        }

        private void ShowOrder()
        {
            SetSpeech(_wants.Value == DrinkType.None ? "..." : $"Pint of {Drinks.Name(_wants.Value)}, please!");
        }

        private void SetSpeech(string text)
        {
            if (speech != null) speech.text = text;
        }
    }
}
