using BarOwner.Economy;
using BarOwner.Interaction;
using BarOwner.Player;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>One line on the cellar order board: "Order a Lager keg (£55.00)".</summary>
    public class KegOrderButton : MonoBehaviour, IInteractable
    {
        [SerializeField] private DrinkType drink = DrinkType.Lager;

        public string GetPrompt(PlayerInteractor interactor)
        {
            var supplier = KegSupplier.Instance;
            if (supplier == null || !supplier.IsSpawned) return null;
            string pending = supplier.PendingDeliveries > 0 ? $"   ({supplier.PendingDeliveries} on the way)" : "";
            return $"Order a {Drinks.Name(drink)} keg ({Money.Format(KegSupplier.KegPricePence(drink))}){pending}";
        }

        public void Interact(PlayerInteractor interactor) => KegSupplier.Instance?.RequestOrderRpc(drink);
    }
}
