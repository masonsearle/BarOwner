using BarOwner.Player;

namespace BarOwner.Interaction
{
    /// <summary>
    /// Anything the player can "use" with the Interact button while looking at it (taps, switches,
    /// the till, doors...). Implement on a component that has a collider (or a child collider).
    /// Interact() runs on the LOCAL client: for anything that changes shared state, send an
    /// Rpc to the server from inside it.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// Prompt shown under the crosshair, e.g. "Turn lights off". Null/empty = nothing to show.
        /// Start it with '~' for info only (shown without a key hint; pressing Interact does nothing).
        /// </summary>
        string GetPrompt(PlayerInteractor interactor);

        void Interact(PlayerInteractor interactor);
    }
}
