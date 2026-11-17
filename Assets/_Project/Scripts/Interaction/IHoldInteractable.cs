using BarOwner.Player;

namespace BarOwner.Interaction
{
    /// <summary>
    /// An interactable you keep the button held on (beer taps, mopping, wiping tables...).
    /// HoldStart fires when the Interact button goes down while looking at it; HoldEnd fires when
    /// the button is released, you look away, or you free the mouse. Both run on the local client.
    /// </summary>
    public interface IHoldInteractable : IInteractable
    {
        void HoldStart(PlayerInteractor interactor);
        void HoldEnd(PlayerInteractor interactor);
    }

    /// <summary>Optional: lets a held object describe itself in the HUD ("Pint of Lager (80%)").</summary>
    public interface IHeldDescription
    {
        string Describe();
    }
}
