using BarOwner.Bar;
using BarOwner.Interaction;
using BarOwner.Player;
using UnityEngine;

namespace BarOwner.Decor
{
    /// <summary>One line in the furniture catalogue.</summary>
    public class DecorOrderButton : MonoBehaviour, IInteractable
    {
        [SerializeField] private DecorType type;

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (DecorShop.Instance == null || !DecorShop.Instance.IsSpawned) return null;
            string unlocks = DecorCatalog.Unlocks(type);
            return $"Buy {DecorCatalog.Name(type)} ({Money.Format(DecorCatalog.PricePence(type))}, +{DecorCatalog.Ambience(type):0} ambience" +
                   (unlocks != null ? $", {unlocks})" : ")");
        }

        public void Interact(PlayerInteractor interactor) => DecorShop.Instance?.RequestBuyRpc(type);
    }
}
