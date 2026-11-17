using BarOwner.Bar;
using BarOwner.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.UI
{
    /// <summary>Crosshair, interaction prompt and the pub's money for the local player. One per scene.</summary>
    public class PlayerHUD : MonoBehaviour
    {
        private static PlayerHUD _instance;

        [SerializeField] private Text promptText;
        [SerializeField] private Graphic crosshair;
        [SerializeField] private Text moneyText;
        [SerializeField] private Text moneyGainText;

        private float _gainVisibleUntil;

        private void Awake()
        {
            _instance = this;
            SetPrompt(null);
            if (moneyText != null) moneyText.text = "";
            if (moneyGainText != null) moneyGainText.text = "";
        }

        private void OnEnable()
        {
            BarTill.BalanceChanged += OnBalanceChanged;
            BarTill.MoneyReceived += OnMoneyReceived;
            if (BarTill.Instance != null && BarTill.Instance.IsSpawned) OnBalanceChanged(BarTill.Instance.Pence);
        }

        private void OnDisable()
        {
            BarTill.BalanceChanged -= OnBalanceChanged;
            BarTill.MoneyReceived -= OnMoneyReceived;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (moneyGainText == null || _gainVisibleUntil <= 0f) return;
            float remaining = _gainVisibleUntil - Time.time;
            if (remaining <= 0f)
            {
                moneyGainText.text = "";
                _gainVisibleUntil = 0f;
                return;
            }
            var c = moneyGainText.color;
            moneyGainText.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(remaining));
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

        private void OnBalanceChanged(int pence)
        {
            if (moneyText != null) moneyText.text = Money.Format(pence);
        }

        private void OnMoneyReceived(int pence, Vector3 where)
        {
            if (moneyGainText == null) return;
            moneyGainText.text = $"+{Money.Format(pence)}";
            _gainVisibleUntil = Time.time + 2f;
        }
    }
}
