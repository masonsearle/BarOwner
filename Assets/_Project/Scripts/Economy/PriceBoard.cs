using BarOwner.Bar;
using UnityEngine;
using UnityEngine.UI;

namespace BarOwner.Economy
{
    /// <summary>Keeps the chalkboard's price labels in sync with the ledger.</summary>
    public class PriceBoard : MonoBehaviour
    {
        [SerializeField] private DrinkType[] drinks = { DrinkType.Lager, DrinkType.Bitter, DrinkType.Cider };
        [SerializeField] private Text[] labels;

        private void Update()
        {
            var ledger = PubLedger.Instance;
            for (int i = 0; i < drinks.Length && i < labels.Length; i++)
            {
                if (labels[i] == null) continue;
                int price = ledger != null && ledger.IsSpawned ? ledger.PricePence(drinks[i]) : Drinks.PricePence(drinks[i]);
                string text = $"{Drinks.Name(drinks[i]).ToUpperInvariant()}  {Money.Format(price)}";
                if (labels[i].text != text) labels[i].text = text;
            }
        }
    }
}
