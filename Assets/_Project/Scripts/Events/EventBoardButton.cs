using BarOwner.Interaction;
using BarOwner.Player;
using BarOwner.Staff;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.Events
{
    /// <summary>One weekday on the events board. Interact to cycle that day's theme night.</summary>
    public class EventBoardButton : MonoBehaviour, IInteractable
    {
        [SerializeField] private int weekday;
        [SerializeField] private Text label;

        public string GetPrompt(PlayerInteractor interactor)
        {
            var schedule = EventSchedule.Instance;
            if (schedule == null || !schedule.IsSpawned) return null;
            var current = schedule.ThemeFor(weekday);
            var next = (ThemeNight)(((int)current + 1) % ThemeNights.Count);
            return $"{StaffNames.Weekdays[weekday]}: change {ThemeNights.Name(current)} to {ThemeNights.Name(next)}";
        }

        public void Interact(PlayerInteractor interactor) => EventSchedule.Instance?.RequestCycleRpc(weekday);

        private void Update()
        {
            if (label == null) return;
            var schedule = EventSchedule.Instance;
            var night = schedule != null && schedule.IsSpawned ? schedule.ThemeFor(weekday) : ThemeNight.None;
            string text = $"{StaffNames.Weekdays[weekday]}   {(night == ThemeNight.None ? "-" : ThemeNights.Name(night))}";
            var needs = ThemeNights.Requires(night);
            int cost = ThemeNights.CostPence(night);
            if (needs.HasValue || cost > 0)
                text += $"<size=20>   ({(needs.HasValue ? $"needs {Decor.DecorCatalog.Name(needs.Value)}" : "")}{(needs.HasValue && cost > 0 ? ", " : "")}{(cost > 0 ? Bar.Money.Format(cost) : "")})</size>";
            if (label.text != text) label.text = text;
        }
    }
}
