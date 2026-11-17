using BarOwner.Bar;
using BarOwner.Day;
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
        [SerializeField] private Text clockText;
        [SerializeField] private Text announcementText;

        private float _gainVisibleUntil;
        private float _announcementUntil;
        private string _lastClock;

        private void Awake()
        {
            _instance = this;
            SetPrompt(null);
            if (moneyText != null) moneyText.text = "";
            if (moneyGainText != null) moneyGainText.text = "";
            if (clockText != null) clockText.text = "";
            if (announcementText != null) announcementText.text = "";
        }

        private void OnEnable()
        {
            BarTill.BalanceChanged += OnBalanceChanged;
            BarTill.MoneyReceived += OnMoneyReceived;
            DayClock.Announcement += OnAnnouncement;
            if (BarTill.Instance != null && BarTill.Instance.IsSpawned) OnBalanceChanged(BarTill.Instance.Pence);
        }

        private void OnDisable()
        {
            BarTill.BalanceChanged -= OnBalanceChanged;
            BarTill.MoneyReceived -= OnMoneyReceived;
            DayClock.Announcement -= OnAnnouncement;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            UpdateClock();
            UpdateAnnouncement();

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

        private void UpdateClock()
        {
            if (clockText == null) return;
            var clock = DayClock.Instance;
            string text = "";
            if (clock != null && clock.IsSpawned)
            {
                string phase = clock.Phase switch
                {
                    PubPhase.Closed => "CLOSED · flip the sign by the door to open",
                    PubPhase.Open => "OPEN",
                    PubPhase.LastOrders => "LAST ORDERS",
                    _ => "CLOSING",
                };
                text = clock.Phase == PubPhase.Closed ? $"Day {clock.Day}  ·  {phase}" : $"Day {clock.Day}  ·  {clock.ClockText}  ·  {phase}";
            }
            if (text != _lastClock)
            {
                _lastClock = text;
                clockText.text = text;
            }
        }

        private void UpdateAnnouncement()
        {
            if (announcementText == null || _announcementUntil <= 0f) return;
            float remaining = _announcementUntil - Time.time;
            if (remaining <= 0f)
            {
                announcementText.text = "";
                _announcementUntil = 0f;
                return;
            }
            var c = announcementText.color;
            announcementText.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(remaining * 2f));
        }

        private void OnAnnouncement(string text, float seconds)
        {
            if (announcementText == null) return;
            announcementText.text = text;
            _announcementUntil = Time.time + seconds;
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
            moneyGainText.text = pence >= 0 ? $"+{Money.Format(pence)}" : Money.Format(pence);
            moneyGainText.color = pence >= 0 ? new Color(1f, 0.9f, 0.4f) : new Color(1f, 0.45f, 0.4f);
            _gainVisibleUntil = Time.time + 2f;
        }
    }
}
