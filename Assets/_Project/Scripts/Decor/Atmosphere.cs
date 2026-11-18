using System.Collections.Generic;
using BarOwner.Bar;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Decor
{
    /// <summary>
    /// How nice the bar room feels (0–20), recalculated by the server every couple of seconds:
    /// + decor standing in the bar room (repeats of the same item count half)
    /// − empty glasses left lying around in the bar room
    /// − the lights being off
    /// Higher ambience = more patient customers, more of them, and better tips.
    /// </summary>
    public class Atmosphere : NetworkBehaviour
    {
        public static Atmosphere Instance { get; private set; }

        public const float Max = 20f;

        [SerializeField] private Light[] barLights;
        [SerializeField, Tooltip("Empties tolerated before they start dragging the mood down.")] private int messTolerance = 3;

        private readonly NetworkVariable<float> _ambience = new(0f);
        private float _nextCalc;

        public float Ambience => _ambience.Value;

        /// <summary>0..1 version for scaling effects.</summary>
        public float Normalised => Mathf.Clamp01(_ambience.Value / Max);

        public override void OnNetworkSpawn() => Instance = this;

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsServer || Time.time < _nextCalc) return;
            _nextCalc = Time.time + 2f;
            _ambience.Value = Calculate();
        }

        private float Calculate()
        {
            float score = 0f;
            var seen = new HashSet<DecorType>();
            foreach (var item in DecorItem.All)
            {
                if (item == null || !item.IsSpawned || !item.IsInBarRoom || item.GetComponent<Interaction.Grabbable>().IsHeld) continue;
                float value = DecorCatalog.Ambience(item.Type);
                score += seen.Add(item.Type) ? value : value * 0.5f;
            }

            int mess = 0;
            foreach (var glass in PintGlass.All)
            {
                if (glass == null || !glass.IsSpawned || !glass.IsEmpty || glass.Grabbable.IsHeld) continue;
                Vector3 p = glass.transform.position;
                if (p.z < 2.4f && p.z > -5f && p.y > -0.5f) mess++; // customer side of the bar
            }
            score -= Mathf.Max(0, mess - messTolerance) * 0.75f;

            bool lightsOn = false;
            foreach (var l in barLights)
                if (l != null && l.enabled) { lightsOn = true; break; }
            if (barLights != null && barLights.Length > 0 && !lightsOn) score -= 6f;

            return Mathf.Clamp(score, 0f, Max);
        }
    }
}
