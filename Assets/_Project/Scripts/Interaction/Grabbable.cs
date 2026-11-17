using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Interaction
{
    /// <summary>
    /// A physics object players can pick up, carry and throw (bottles, glasses, crates, kegs...).
    ///
    /// Networking model:
    ///  - The server decides who holds it (HolderId) so two players can't grab the same thing.
    ///  - When granted, the server hands *ownership* to the grabbing client. With an Owner-mode
    ///    NetworkTransform + NetworkRigidbody, that client then simulates the physics locally
    ///    (zero-latency feel) and everyone else sees the result. Non-owners keep it kinematic.
    ///  - Ownership stays with the last holder after a throw so the flight looks smooth for them.
    ///
    /// Required on the same GameObject: Rigidbody, NetworkObject, NetworkTransform (Authority Mode = Owner),
    /// NetworkRigidbody. Set NetworkObject "Dont Destroy With Owner" so items survive a player leaving.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Grabbable : NetworkBehaviour
    {
        private const ulong NoHolder = ulong.MaxValue;
        private const float PendingTimeout = 1.5f;

        [SerializeField] private string displayName = "Object";
        [SerializeField, Tooltip("Mass at or above which the item counts as heavy (prompt + slower carrying).")]
        private float heavyMass = 15f;

        private readonly NetworkVariable<ulong> _holderId = new(NoHolder);

        private PlayerInteractor _pendingRequester;
        private float _pendingSince;

        public Rigidbody Body { get; private set; }
        public string DisplayName => displayName;
        public bool IsHeavy => Body != null && Body.mass >= heavyMass;
        public bool IsHeld => _holderId.Value != NoHolder;
        public ulong HolderId => _holderId.Value;
        public bool IsHeldByLocalPlayer => IsSpawned && _holderId.Value == NetworkManager.LocalClientId;

        private void Awake() => Body = GetComponent<Rigidbody>();

        public override void OnNetworkSpawn()
        {
            if (IsServer) NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            _holderId.OnValueChanged += OnHolderChanged;
            OnHolderChanged(NoHolder, _holderId.Value); // spawned straight into our hand?
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            _holderId.OnValueChanged -= OnHolderChanged;
            _pendingRequester = null;

            // Item vanished while we were holding it (e.g. a customer took the pint).
            var local = PlayerInteractor.Local;
            if (local != null && local.Held == this) local.ForceDrop();
        }

        /// <summary>SERVER: hand this item to a client without them asking (e.g. a fresh glass from the rack).</summary>
        public void ServerGrantTo(ulong clientId)
        {
            if (!IsServer || IsHeld) return;
            if (NetworkObject.OwnerClientId != clientId) NetworkObject.ChangeOwnership(clientId);
            _holderId.Value = clientId;
        }

        private void OnHolderChanged(ulong previous, ulong current)
        {
            // The server made us the holder without a request from us: queue it up for the local player.
            if (current == NetworkManager.LocalClientId && _pendingRequester == null && PlayerInteractor.Local != null &&
                PlayerInteractor.Local.Held != this)
            {
                _pendingRequester = PlayerInteractor.Local;
                _pendingSince = Time.time;
            }
        }

        public bool CanBeGrabbed => IsSpawned && !IsHeld && _pendingRequester == null;

        /// <summary>Local player asks to pick this up. PlayerInteractor.BeginHold is called once granted.</summary>
        public void RequestGrab(PlayerInteractor requester)
        {
            if (!CanBeGrabbed) return;
            _pendingRequester = requester;
            _pendingSince = Time.time;
            RequestGrabRpc();
        }

        /// <summary>Local holder lets go (drop or throw). Call after applying any throw velocity.</summary>
        public void NotifyReleased()
        {
            if (IsSpawned) ReleaseRpc();
        }

        private void Update()
        {
            if (_pendingRequester == null) return;

            // Granted once the server says we're the holder AND ownership has arrived (they can land in either order).
            if (IsHeldByLocalPlayer && IsOwner)
            {
                var requester = _pendingRequester;
                _pendingRequester = null;
                if (requester == null || !requester.BeginHold(this))
                    NotifyReleased(); // hands were full: let go of the claim so others can take it
            }
            else if (IsHeld && !IsHeldByLocalPlayer)
            {
                _pendingRequester = null; // someone else got it
            }
            else if (Time.time - _pendingSince > PendingTimeout)
            {
                _pendingRequester = null; // request or ownership got lost; don't leave a stale claim behind
                if (IsHeldByLocalPlayer) NotifyReleased();
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestGrabRpc(RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (IsHeld) return;

            if (NetworkObject.OwnerClientId != sender)
                NetworkObject.ChangeOwnership(sender);
            _holderId.Value = sender;
        }

        [Rpc(SendTo.Server)]
        private void ReleaseRpc(RpcParams rpcParams = default)
        {
            if (_holderId.Value == rpcParams.Receive.SenderClientId)
                _holderId.Value = NoHolder;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_holderId.Value == clientId) _holderId.Value = NoHolder;
        }
    }
}
