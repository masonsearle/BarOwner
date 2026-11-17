using BarOwner.Bar;
using BarOwner.Interaction;
using BarOwner.Player;
using UnityEngine;

namespace BarOwner.Economy
{
    /// <summary>A − or + button on the price board. Changes the drink's price by <see cref="deltaPence"/>.</summary>
    public class PriceButton : MonoBehaviour, IInteractable
    {
        [SerializeField] private DrinkType drink = DrinkType.Lager;
        [SerializeField] private int deltaPence = 10;

        public string GetPrompt(PlayerInteractor interactor)
        {
            var ledger = PubLedger.Instance;
            if (ledger == null || !ledger.IsSpawned) return null;
            string change = deltaPence > 0 ? $"Raise {Drinks.Name(drink)} by {deltaPence}p" : $"Lower {Drinks.Name(drink)} by {-deltaPence}p";
            return $"{change}  (now {Money.Format(ledger.PricePence(drink))}, standard {Money.Format(Drinks.PricePence(drink))})";
        }

        public void Interact(PlayerInteractor interactor) => PubLedger.Instance?.RequestPriceChangeRpc(drink, deltaPence);
    }
}
