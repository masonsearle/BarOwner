using BarOwner.Interaction;
using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// A keg coupler in the cellar that feeds one tap line. Holds a keg's contents as synced state
    /// (no physics while plugged in). Interact while carrying a keg of the right drink to plug it in;
    /// interact empty-handed to unplug the current one (it pops out as a carryable Keg).
    /// </summary>
    public class KegSocket : NetworkBehaviour, IInteractable
    {
        [SerializeField, Tooltip("The drink this line is plumbed for.")] private DrinkType line = DrinkType.Lager;
        [SerializeField] private NetworkObject kegPrefab;
        [SerializeField] private GameObject connectedVisual;
        [SerializeField] private Renderer connectedBand;
        [SerializeField] private Transform popOutPoint;
        [SerializeField] private float connectRange = 3f;
        [SerializeField] private bool startConnectedAndFull = true;

        /// <summary>Every spawned socket (saving/loading).</summary>
        public static readonly System.Collections.Generic.List<KegSocket> All = new();

        private readonly NetworkVariable<bool> _connected = new(false);
        private readonly NetworkVariable<float> _pints = new(0f);

        private MaterialPropertyBlock _props;

        public DrinkType Line => line;
        public bool IsConnected => _connected.Value;
        public float Pints => _pints.Value;
        public bool HasBeer => _connected.Value && _pints.Value > 0.01f;

        private void Awake() => _props = new MaterialPropertyBlock();

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            _connected.OnValueChanged += OnConnectedChanged;
            if (IsServer && startConnectedAndFull)
            {
                _connected.Value = true;
                _pints.Value = Keg.Capacity;
            }
            Refresh();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            _connected.OnValueChanged -= OnConnectedChanged;
        }

        /// <summary>SERVER (loading a save): set what's plugged in.</summary>
        public void ServerSetState(bool connected, float pints)
        {
            if (!IsServer) return;
            _connected.Value = connected;
            _pints.Value = connected ? Mathf.Clamp(pints, 0f, Keg.Capacity) : 0f;
        }

        /// <summary>SERVER: take up to <paramref name="amount"/> pints from the keg. Returns what came out.</summary>
        public float ServerDrain(float amount)
        {
            if (!IsServer || !_connected.Value) return 0f;
            float taken = Mathf.Min(amount, _pints.Value);
            _pints.Value -= taken;
            return taken;
        }

        // ---------------------------------------------------------------- IInteractable (local)

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!IsSpawned) return null;
            string name = Drinks.Name(line);
            var heldKeg = interactor.Held != null ? interactor.Held.GetComponent<Keg>() : null;

            if (_connected.Value)
            {
                string status = HasBeer ? $"{Mathf.FloorToInt(_pints.Value)}/{Keg.Capacity:0} pints" : "EMPTY";
                return interactor.Held == null ? $"Unplug {name} keg ({status})" : $"~{name} line: {status}";
            }

            if (heldKeg == null) return $"~{name} line: no keg plugged in";
            if (heldKeg.Drink != line) return $"~This line is for {name}, not {Drinks.Name(heldKeg.Drink)}";
            return $"Plug in {heldKeg.Describe()}";
        }

        public void Interact(PlayerInteractor interactor)
        {
            var heldKeg = interactor.Held != null ? interactor.Held.GetComponent<Keg>() : null;
            if (_connected.Value && interactor.Held == null) UnplugRpc();
            else if (!_connected.Value && heldKeg != null) PlugInRpc(heldKeg.NetworkObject);
        }

        // ---------------------------------------------------------------- server

        [Rpc(SendTo.Server)]
        private void PlugInRpc(NetworkObjectReference kegRef, RpcParams rpcParams = default)
        {
            if (_connected.Value || !kegRef.TryGet(out NetworkObject kegObject)) return;
            var keg = kegObject.GetComponent<Keg>();
            if (keg == null || keg.Drink != line) return;
            if (keg.Grabbable.HolderId != rpcParams.Receive.SenderClientId) return;
            if (Vector3.Distance(keg.transform.position, transform.position) > connectRange) return;

            _pints.Value = keg.Pints;
            _connected.Value = true;
            kegObject.Despawn();
        }

        [Rpc(SendTo.Server)]
        private void UnplugRpc(RpcParams rpcParams = default)
        {
            if (!_connected.Value || kegPrefab == null) return;
            ulong sender = rpcParams.Receive.SenderClientId;

            Transform at = popOutPoint != null ? popOutPoint : transform;
            NetworkObject kegObject = Instantiate(kegPrefab, at.position, at.rotation);
            kegObject.DontDestroyWithOwner = true;
            kegObject.Spawn();
            kegObject.GetComponent<Keg>().ServerSetContents(line, _pints.Value);
            kegObject.GetComponent<Grabbable>().ServerGrantTo(sender); // straight into their arms

            _connected.Value = false;
            _pints.Value = 0f;
        }

        // ---------------------------------------------------------------- visuals

        private void OnConnectedChanged(bool previous, bool current) => Refresh();

        private void Refresh()
        {
            if (connectedVisual != null) connectedVisual.SetActive(_connected.Value);
            KegVisuals.Paint(connectedBand, line, _props);
        }
    }
}
