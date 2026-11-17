using BarOwner.Interaction;
using BarOwner.Player;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.Day
{
    /// <summary>The OPEN/CLOSED sign by the door. Flip it to start the day; flip it again to call last orders early.</summary>
    public class OpenSign : MonoBehaviour, IInteractable
    {
        [SerializeField] private Text label;
        [SerializeField] private Renderer board;

        private PubPhase? _shown;

        public string GetPrompt(PlayerInteractor interactor)
        {
            var clock = DayClock.Instance;
            if (clock == null || !clock.IsSpawned) return null;
            return clock.Phase switch
            {
                PubPhase.Closed => "Flip the sign: open the pub",
                PubPhase.Open => "Call last orders early",
                _ => null,
            };
        }

        public void Interact(PlayerInteractor interactor)
        {
            var clock = DayClock.Instance;
            if (clock == null) return;
            if (clock.Phase == PubPhase.Closed) clock.RequestOpenRpc();
            else if (clock.Phase == PubPhase.Open) clock.RequestLastOrdersRpc();
        }

        private void Update()
        {
            var clock = DayClock.Instance;
            PubPhase phase = clock != null && clock.IsSpawned ? clock.Phase : PubPhase.Closed;
            if (_shown == phase) return;
            _shown = phase;

            bool open = phase == PubPhase.Open || phase == PubPhase.LastOrders;
            if (label != null) label.text = open ? "OPEN" : "CLOSED";
            if (board != null)
            {
                var props = new MaterialPropertyBlock();
                board.GetPropertyBlock(props);
                props.SetColor("_BaseColor", open ? new Color(0.2f, 0.7f, 0.25f) : new Color(0.7f, 0.15f, 0.12f));
                board.SetPropertyBlock(props);
            }
        }
    }
}
