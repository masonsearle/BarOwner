using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.UI
{
    /// <summary>Crosshair + interaction prompt for the local player. One per scene.</summary>
    public class PlayerHUD : MonoBehaviour
    {
        private static PlayerHUD _instance;

        [SerializeField] private Text promptText;
        [SerializeField] private Graphic crosshair;

        private void Awake()
        {
            _instance = this;
            SetPrompt(null);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        public static void SetPrompt(string text)
        {
            if (_instance == null || _instance.promptText == null) return;
            bool show = !string.IsNullOrEmpty(text);
            if (_instance.promptText.text != text) _instance.promptText.text = text ?? string.Empty;
            _instance.promptText.enabled = show;
            if (_instance.crosshair != null)
                _instance.crosshair.color = show ? new Color(1f, 0.85f, 0.4f, 1f) : new Color(1f, 1f, 1f, 0.7f);
        }
    }
}
