using BarOwner.Player;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BarOwner.Networking
{
    /// <summary>
    /// Temporary dev controls until there's a real main menu:
    ///   H = host, J = join the address below, F8 = disconnect.
    /// Also swaps the scene's lobby camera for the player camera once you've spawned.
    /// </summary>
    public class DevNetworkHUD : MonoBehaviour
    {
        [SerializeField] private Text statusText;
        [SerializeField] private GameObject lobbyCamera;
        [SerializeField, Tooltip("Host's IP. 127.0.0.1 = same PC. Use the host's LAN IP for another PC on your network.")]
        private string joinAddress = "127.0.0.1";
        [SerializeField] private ushort port = 7777;

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            var keyboard = Keyboard.current;

            if (!nm.IsListening)
            {
                SetStatus($"BAROWNER  (dev)\n[H] Host game    [J] Join {joinAddress}:{port}");
                if (keyboard != null && keyboard.hKey.wasPressedThisFrame)
                {
                    // Listen on all interfaces so other PCs on the LAN can join too.
                    Transport(nm)?.SetConnectionData("127.0.0.1", port, "0.0.0.0");
                    nm.StartHost();
                }
                else if (keyboard != null && keyboard.jKey.wasPressedThisFrame)
                {
                    Transport(nm)?.SetConnectionData(joinAddress, port);
                    nm.StartClient();
                }
            }
            else
            {
                string status = nm.IsServer
                    ? $"Hosting · {nm.ConnectedClientsIds.Count} player(s) · [F8] Stop · [Esc] Free mouse"
                    : nm.IsConnectedClient ? "Connected · [F8] Leave · [Esc] Free mouse" : "Connecting...";
                SetStatus(status);
                if (keyboard != null && keyboard.f8Key.wasPressedThisFrame) nm.Shutdown();
            }

            if (lobbyCamera != null)
            {
                bool wantLobby = PlayerMotor.LocalPlayer == null;
                if (lobbyCamera.activeSelf != wantLobby) lobbyCamera.SetActive(wantLobby);
            }
        }

        private static UnityTransport Transport(NetworkManager nm) => nm.GetComponent<UnityTransport>();

        private void SetStatus(string text)
        {
            if (statusText != null && statusText.text != text) statusText.text = text;
        }
    }
}
