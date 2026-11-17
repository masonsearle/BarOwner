using BarOwner.Bar;
using BarOwner.Day;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.Staff
{
    /// <summary>
    /// The staff noticeboard: today's applicants on the left, your staff and their weekly rota on
    /// the right. Purely visual; the buttons on it are <see cref="StaffButton"/>s.
    /// </summary>
    public class StaffBoard : MonoBehaviour
    {
        [SerializeField] private Text[] applicantTexts;
        [SerializeField] private GameObject[] hireButtons;
        [SerializeField] private Text[] staffTexts;
        [SerializeField] private GameObject[] fireButtons;
        [SerializeField, Tooltip("Row-major: staff row * 7 + weekday.")] private Renderer[] rotaButtons;
        [SerializeField] private Text[] weekdayHeaders;

        private static readonly Color On = new(0.25f, 0.75f, 0.3f);
        private static readonly Color Off = new(0.25f, 0.22f, 0.2f);
        private MaterialPropertyBlock _props;
        private float _nextRefresh;

        private void Awake() => _props = new MaterialPropertyBlock();

        private void Update()
        {
            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + 0.2f;

            var m = StaffManager.Instance;
            bool live = m != null && m.IsSpawned;

            for (int i = 0; i < applicantTexts.Length; i++)
            {
                bool has = live && i < m.Applicants.Count;
                if (applicantTexts[i] != null) applicantTexts[i].text = has ? Describe(m.Applicants[i], true) : (i == 0 && live ? "No more applicants today" : "");
                if (i < hireButtons.Length && hireButtons[i] != null && hireButtons[i].activeSelf != has) hireButtons[i].SetActive(has);
            }

            for (int r = 0; r < staffTexts.Length; r++)
            {
                bool has = live && r < m.Hired.Count;
                if (staffTexts[r] != null) staffTexts[r].text = has ? Describe(m.Hired[r], false) : (r == 0 && live && m.Hired.Count == 0 ? "No staff yet: hire someone!" : "");
                if (r < fireButtons.Length && fireButtons[r] != null && fireButtons[r].activeSelf != has) fireButtons[r].SetActive(has);

                for (int d = 0; d < 7; d++)
                {
                    int index = r * 7 + d;
                    if (index >= rotaButtons.Length || rotaButtons[index] == null) continue;
                    var go = rotaButtons[index].gameObject;
                    if (go.activeSelf != has) go.SetActive(has);
                    if (!has) continue;
                    rotaButtons[index].GetPropertyBlock(_props);
                    _props.SetColor("_BaseColor", m.Hired[r].WorksOn(d) ? On : Off);
                    rotaButtons[index].SetPropertyBlock(_props);
                }
            }

            // Highlight today's column.
            int today = DayClock.Instance != null && DayClock.Instance.IsSpawned ? StaffNames.WeekdayOf(DayClock.Instance.Day) : -1;
            for (int d = 0; d < weekdayHeaders.Length && d < 7; d++)
            {
                if (weekdayHeaders[d] == null) continue;
                weekdayHeaders[d].text = StaffNames.Weekdays[d].Substring(0, 2);
                weekdayHeaders[d].color = d == today ? new Color(1f, 0.85f, 0.3f) : Color.white;
            }
        }

        private static string Describe(StaffData s, bool applicant)
        {
            string stats = $"Speed {s.Speed} · Charm {s.Charm} · Honesty {s.Honesty}";
            return applicant
                ? $"<b>{s.Name}</b>  {Money.Format(s.WagePence)}/shift\n<size=22>{stats}</size>"
                : $"<b>{s.Name}</b>  {Money.Format(s.WagePence)}\n<size=20>Spd {s.Speed} · Chm {s.Charm} · Hon {s.Honesty}</size>";
        }
    }
}
