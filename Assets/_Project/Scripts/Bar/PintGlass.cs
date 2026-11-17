using System.Collections.Generic;
using BarOwner.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// A pint glass that can hold one drink. Contents are server-authoritative (taps fill it on the
    /// server); every client just renders the liquid level. Lives on the PintGlass prefab together
    /// with Grabbable.
    /// </summary>
    [RequireComponent(typeof(Grabbable))]
    public class PintGlass : NetworkBehaviour, IHeldDescription
    {
        /// <summary>Every spawned glass, for cheap server-side lookups (taps, customers, glass limit).</summary>
        public static readonly List<PintGlass> All = new();

        [SerializeField] private Transform liquid;
        [SerializeField] private Transform head;
        [SerializeField] private float emptyMass = 0.35f;
        [SerializeField] private float fullDrinkMass = 0.57f;

        private readonly NetworkVariable<float> _fill = new(0f);
        private readonly NetworkVariable<DrinkType> _drink = new(DrinkType.None);

        private Grabbable _grabbable;
        private Renderer _liquidRenderer;
        private MaterialPropertyBlock _props;

        /// <summary>0 = empty, 1 = brim full.</summary>
        public float Fill => _fill.Value;
        public DrinkType Drink => _drink.Value;
        public bool IsEmpty => _fill.Value < 0.01f;
        public Grabbable Grabbable => _grabbable;

        private void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            if (liquid != null) _liquidRenderer = liquid.GetComponent<Renderer>();
            _props = new MaterialPropertyBlock();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            _fill.OnValueChanged += OnFillChanged;
            _drink.OnValueChanged += OnDrinkChanged;
            Refresh();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            _fill.OnValueChanged -= OnFillChanged;
            _drink.OnValueChanged -= OnDrinkChanged;
        }

        /// <summary>SERVER: pour some drink in. Returns how much spilled over the top.</summary>
        public float ServerAddDrink(DrinkType type, float amount)
        {
            if (!IsServer || amount <= 0f) return 0f;

            if (IsEmpty) _drink.Value = type;
            else if (_drink.Value != type) _drink.Value = DrinkType.Slops;

            float newFill = _fill.Value + amount;
            float overflow = Mathf.Max(0f, newFill - 1f);
            _fill.Value = Mathf.Min(1f, newFill);
            return overflow;
        }

        /// <summary>SERVER: tip it all out.</summary>
        public void ServerEmpty()
        {
            if (!IsServer) return;
            _fill.Value = 0f;
            _drink.Value = DrinkType.None;
        }

        public string Describe()
        {
            if (IsEmpty) return "Empty pint glass";
            if (_drink.Value == DrinkType.Slops) return "A glass of slops";
            int percent = Mathf.RoundToInt(_fill.Value * 100f);
            return percent >= 97 ? $"Pint of {Drinks.Name(_drink.Value)}" : $"Pint of {Drinks.Name(_drink.Value)} ({percent}% full)";
        }

        private void OnFillChanged(float previous, float current) => Refresh();
        private void OnDrinkChanged(DrinkType previous, DrinkType current) => Refresh();

        private void Refresh()
        {
            float fill = _fill.Value;
            DrinkType drink = _drink.Value;

            if (_grabbable != null && _grabbable.Body != null)
                _grabbable.Body.mass = emptyMass + fullDrinkMass * fill;

            // Glass mesh is a unit cylinder: local Y runs -1 (bottom) to +1 (rim).
            float headFraction = fill > 0.05f ? Drinks.HeadAmount(drink) : 0f;
            float liquidHeight = 2f * Mathf.Max(0f, fill - headFraction) * 0.96f;
            float headHeight = 2f * headFraction * 0.96f;

            if (liquid != null)
            {
                bool show = fill > 0.005f;
                liquid.gameObject.SetActive(show);
                if (show)
                {
                    liquid.localScale = new Vector3(0.86f, Mathf.Max(0.001f, liquidHeight * 0.5f), 0.86f);
                    liquid.localPosition = new Vector3(0f, -0.98f + liquidHeight * 0.5f, 0f);
                    if (_liquidRenderer != null)
                    {
                        _liquidRenderer.GetPropertyBlock(_props);
                        _props.SetColor("_BaseColor", Drinks.LiquidColour(drink));
                        _liquidRenderer.SetPropertyBlock(_props);
                    }
                }
            }

            if (head != null)
            {
                bool show = headHeight > 0.001f;
                head.gameObject.SetActive(show);
                if (show)
                {
                    head.localScale = new Vector3(0.87f, headHeight * 0.5f, 0.87f);
                    head.localPosition = new Vector3(0f, -0.98f + liquidHeight + headHeight * 0.5f, 0f);
                }
            }
        }
    }
}
