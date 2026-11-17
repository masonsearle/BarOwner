using BarOwner.Bar;
using BarOwner.Interaction;
using BarOwner.Player;
using UnityEngine;

namespace BarOwner.Staff
{
    /// <summary>A button on the staff noticeboard: hire an applicant, fire someone, or toggle a rota day.</summary>
    public class StaffButton : MonoBehaviour, IInteractable
    {
        public enum Kind { Hire, Fire, Rota }

        [SerializeField] private Kind kind;
        [SerializeField] private int row;
        [SerializeField] private int weekday;

        public string GetPrompt(PlayerInteractor interactor)
        {
            var m = StaffManager.Instance;
            if (m == null || !m.IsSpawned) return null;

            switch (kind)
            {
                case Kind.Hire:
                    if (row >= m.Applicants.Count) return null;
                    var a = m.Applicants[row];
                    if (m.Hired.Count >= StaffManager.MaxStaff) return $"~Can't hire {a.Name}: you already have {StaffManager.MaxStaff} staff";
                    return $"Hire {a.Name} ({Money.Format(a.WagePence)} per shift)";
                case Kind.Fire:
                    if (row >= m.Hired.Count) return null;
                    return $"Let {m.Hired[row].Name} go";
                default:
                    if (row >= m.Hired.Count) return null;
                    var s = m.Hired[row];
                    string day = StaffNames.Weekdays[weekday];
                    return s.WorksOn(weekday) ? $"Give {s.Name} {day} off" : $"Put {s.Name} on the rota for {day}";
            }
        }

        public void Interact(PlayerInteractor interactor)
        {
            var m = StaffManager.Instance;
            if (m == null) return;
            switch (kind)
            {
                case Kind.Hire when row < m.Applicants.Count: m.RequestHireRpc(m.Applicants[row].Id); break;
                case Kind.Fire when row < m.Hired.Count: m.RequestFireRpc(m.Hired[row].Id); break;
                case Kind.Rota when row < m.Hired.Count: m.RequestToggleRotaRpc(m.Hired[row].Id, weekday); break;
            }
        }
    }
}
