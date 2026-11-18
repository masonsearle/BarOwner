using BarOwner.Bar;
using BarOwner.Day;
using BarOwner.Decor;
using BarOwner.Economy;
using BarOwner.Interaction;
using BarOwner.Staff;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Events
{
    /// <summary>
    /// Weekly theme-night schedule (set on the events board in the office) and tonight's event.
    /// When the pub opens, the scheduled night goes ahead if its kit is in the bar room (telly,
    /// mic, amp) and its cost can be paid; otherwise it's a normal night. The bar lights change
    /// colour to suit.
    /// </summary>
    public class EventSchedule : NetworkBehaviour
    {
        public static EventSchedule Instance { get; private set; }

        [SerializeField] private Light[] barLights;

        // 7 weekdays × 3 bits each, packed into one int so it syncs and saves trivially.
        private readonly NetworkVariable<int> _schedule = new(0);
        private readonly NetworkVariable<ThemeNight> _tonight = new(ThemeNight.None);

        private Color[] _originalColours;

        public ThemeNight Tonight => _tonight.Value;
        public int PackedSchedule => _schedule.Value;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (barLights != null)
            {
                _originalColours = new Color[barLights.Length];
                for (int i = 0; i < barLights.Length; i++)
                    if (barLights[i] != null) _originalColours[i] = barLights[i].color;
            }
            _tonight.OnValueChanged += OnTonightChanged;
            ApplyLighting(_tonight.Value);
        }

        public override void OnNetworkDespawn()
        {
            _tonight.OnValueChanged -= OnTonightChanged;
            if (Instance == this) Instance = null;
        }

        public ThemeNight ThemeFor(int weekday) => (ThemeNight)((_schedule.Value >> (weekday * 3)) & 0b111);

        [Rpc(SendTo.Server)]
        public void RequestCycleRpc(int weekday)
        {
            if (weekday < 0 || weekday > 6) return;
            var next = (ThemeNight)(((int)ThemeFor(weekday) + 1) % ThemeNights.Count);
            int mask = ~(0b111 << (weekday * 3));
            _schedule.Value = (_schedule.Value & mask) | ((int)next << (weekday * 3));
        }

        public void ServerSetPackedSchedule(int packed)
        {
            if (IsServer) _schedule.Value = packed;
        }

        // ---------------------------------------------------------------- day hooks (server)

        public void ServerOnDayStarted(int day)
        {
            if (!IsServer) return;
            ThemeNight planned = ThemeFor(StaffNames.WeekdayOf(day));
            if (planned == ThemeNight.None) { _tonight.Value = ThemeNight.None; return; }

            DecorType? needs = ThemeNights.Requires(planned);
            if (needs.HasValue && !HasInBarRoom(needs.Value))
            {
                _tonight.Value = ThemeNight.None;
                DayClock.Instance?.ServerAnnounce($"{ThemeNights.Name(planned)} cancelled: no {DecorCatalog.Name(needs.Value)} in the bar!", 4f);
                return;
            }

            int cost = ThemeNights.CostPence(planned);
            if (cost > 0 && (BarTill.Instance == null || !BarTill.Instance.ServerTrySpend(cost, transform.position)))
            {
                _tonight.Value = ThemeNight.None;
                DayClock.Instance?.ServerAnnounce($"{ThemeNights.Name(planned)} cancelled: can't afford {Money.Format(cost)}", 4f);
                return;
            }

            if (cost > 0) PubLedger.Instance?.ServerRecordEntertainment(cost);
            _tonight.Value = planned;
            DayClock.Instance?.ServerAnnounce($"Tonight: {ThemeNights.Name(planned)}!", 3.5f);
        }

        public void ServerOnDayEnded()
        {
            if (IsServer) _tonight.Value = ThemeNight.None;
        }

        private static bool HasInBarRoom(DecorType type)
        {
            foreach (var item in DecorItem.All)
                if (item != null && item.IsSpawned && item.Type == type && item.IsInBarRoom &&
                    !item.GetComponent<Grabbable>().IsHeld)
                    return true;
            return false;
        }

        // ---------------------------------------------------------------- lighting (everyone)

        private void OnTonightChanged(ThemeNight previous, ThemeNight current) => ApplyLighting(current);

        private void ApplyLighting(ThemeNight night)
        {
            if (barLights == null || _originalColours == null) return;
            for (int i = 0; i < barLights.Length; i++)
            {
                if (barLights[i] == null) continue;
                barLights[i].color = night switch
                {
                    ThemeNight.Karaoke => i % 2 == 0 ? new Color(1f, 0.4f, 0.8f) : new Color(0.5f, 0.4f, 1f),
                    ThemeNight.LiveMusic => i % 2 == 0 ? new Color(1f, 0.5f, 0.3f) : new Color(0.4f, 0.6f, 1f),
                    ThemeNight.Sports => new Color(0.9f, 0.95f, 1f),
                    _ => _originalColours[i],
                };
            }
        }
    }
}
