using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Interaction
{
    /// <summary>
    /// Example of the standard shared-state interaction pattern:
    /// local Interact() → Rpc to server → server changes a NetworkVariable → every client reacts.
    /// Copy this shape for taps, the till, doors, the jukebox, etc.
    /// </summary>
    public class LightSwitch : NetworkBehaviour, IInteractable
    {
        [SerializeField] private Light[] lights;
        [SerializeField] private Transform toggleVisual;

        private readonly NetworkVariable<bool> _on = new(true);

        public override void OnNetworkSpawn()
        {
            _on.OnValueChanged += OnChanged;
            Apply(_on.Value); // late joiners get the current state
        }

        public override void OnNetworkDespawn() => _on.OnValueChanged -= OnChanged;

        public string GetPrompt(PlayerInteractor interactor) =>
            IsSpawned ? (_on.Value ? "Turn lights off" : "Turn lights on") : null;

        public void Interact(PlayerInteractor interactor) => ToggleRpc();

        [Rpc(SendTo.Server)]
        private void ToggleRpc() => _on.Value = !_on.Value;

        private void OnChanged(bool previous, bool current) => Apply(current);

        private void Apply(bool on)
        {
            foreach (var l in lights)
                if (l != null) l.enabled = on;
            if (toggleVisual != null) toggleVisual.localRotation = Quaternion.Euler(on ? -20f : 20f, 0f, 0f);
        }
    }
}
