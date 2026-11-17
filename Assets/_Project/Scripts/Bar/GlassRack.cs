using BarOwner.Interaction;
using BarOwner.Player;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// Interact to take a clean pint glass. The server spawns one straight into your hand
    /// (ownership + holder set to you). Capped so nobody can fill the pub with glasses.
    /// </summary>
    public class GlassRack : NetworkBehaviour, IInteractable
    {
        [SerializeField] private NetworkObject glassPrefab;
        [SerializeField] private Transform spawnPoint;
        [SerializeField] private int maxGlassesInPub = 16;

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!IsSpawned || glassPrefab == null) return null;
            if (interactor.Held != null) return "Glass rack (hands full)";
            return "Take a pint glass";
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor.Held == null) TakeGlassRpc();
        }

        [Rpc(SendTo.Server)]
        private void TakeGlassRpc(RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (PintGlass.All.Count >= maxGlassesInPub)
            {
                Debug.Log("[GlassRack] Too many glasses out. Collect some empties!");
                return;
            }

            Transform at = spawnPoint != null ? spawnPoint : transform;
            NetworkObject glass = Instantiate(glassPrefab, at.position, at.rotation);
            glass.DontDestroyWithOwner = true;
            glass.SpawnWithOwnership(sender);
            glass.GetComponent<Grabbable>().ServerGrantTo(sender);
        }
    }
}
