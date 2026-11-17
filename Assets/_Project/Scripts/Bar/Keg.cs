using BarOwner.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// A loose keg you can carry (heavy when full). Plugging it into a KegSocket despawns it and the
    /// socket takes over its contents; unplugging spawns a fresh Keg with whatever's left.
    /// Lives on the Keg prefab next to Grabbable.
    /// </summary>
    [RequireComponent(typeof(Grabbable))]
    public class Keg : NetworkBehaviour, IHeldDescription
    {
        public const float Capacity = 20f; // pints

        [SerializeField, Tooltip("Contents for kegs placed in the scene (runtime kegs are set by the server).")]
        private DrinkType startingDrink = DrinkType.Lager;
        [SerializeField] private Renderer band;
        [SerializeField] private float emptyMass = 8f;
        [SerializeField] private float fullBeerMass = 22f;

        private readonly NetworkVariable<DrinkType> _drink = new(DrinkType.None);
        private readonly NetworkVariable<float> _pints = new(-1f); // -1 = not initialised yet

        private Grabbable _grabbable;
        private MaterialPropertyBlock _props;

        public DrinkType Drink => _drink.Value;
        public float Pints => Mathf.Max(0f, _pints.Value);
        public bool IsEmpty => Pints < 0.05f;
        public Grabbable Grabbable => _grabbable;

        private void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            _props = new MaterialPropertyBlock();
        }

        public override void OnNetworkSpawn()
        {
            _drink.OnValueChanged += OnDrinkChanged;
            _pints.OnValueChanged += OnPintsChanged;
            if (IsServer && _pints.Value < 0f) ServerSetContents(startingDrink, Capacity);
            Refresh();
        }

        public override void OnNetworkDespawn()
        {
            _drink.OnValueChanged -= OnDrinkChanged;
            _pints.OnValueChanged -= OnPintsChanged;
        }

        public void ServerSetContents(DrinkType drink, float pints)
        {
            if (!IsServer) return;
            _drink.Value = drink;
            _pints.Value = Mathf.Clamp(pints, 0f, Capacity);
        }

        public string Describe() =>
            IsEmpty ? $"Empty {Drinks.Name(Drink)} keg" : $"{Drinks.Name(Drink)} keg ({Mathf.FloorToInt(Pints)}/{Capacity:0} pints)";

        private void OnDrinkChanged(DrinkType previous, DrinkType current) => Refresh();
        private void OnPintsChanged(float previous, float current) => Refresh();

        private void Refresh()
        {
            if (_grabbable != null && _grabbable.Body != null)
                _grabbable.Body.mass = emptyMass + fullBeerMass * Pints / Capacity;
            KegVisuals.Paint(band, _drink.Value, _props);
        }
    }

    /// <summary>Shared keg colouring so loose kegs and plugged-in kegs look the same.</summary>
    public static class KegVisuals
    {
        public static void Paint(Renderer band, DrinkType drink, MaterialPropertyBlock props)
        {
            if (band == null) return;
            Color c = drink switch
            {
                DrinkType.Lager => new Color(0.95f, 0.75f, 0.15f),
                DrinkType.Bitter => new Color(0.5f, 0.22f, 0.08f),
                DrinkType.Cider => new Color(0.35f, 0.65f, 0.2f),
                _ => Color.grey,
            };
            band.GetPropertyBlock(props);
            props.SetColor("_BaseColor", c);
            band.SetPropertyBlock(props);
        }
    }
}
