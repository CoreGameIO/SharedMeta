using SharedMeta.Core.Transport;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Receives health notifications from the server-side session manager — currently used
    /// for RPC ordering stalls. Implementations are typically wired into the UI layer
    /// (e.g. show a "syncing…" overlay or a "Connection issue" prompt).
    ///
    /// All callbacks are invoked on the same thread that processes server-pushed batches
    /// (see <c>ClientDispatcher.ProcessServerResponse</c>). Implementations should be cheap
    /// and non-blocking; defer UI work to the main game-loop thread if necessary.
    /// </summary>
    public interface ISessionHealthListener
    {
        /// <summary>
        /// The server has reported an in-progress stall (missing or out-of-order RPC).
        /// Called for both stages: <see cref="StallStage.Stalled"/> (early "syncing…")
        /// and <see cref="StallStage.TimeoutPending"/> (long enough to ask the user).
        /// </summary>
        void OnSessionStalled(StallNotification notification);

        /// <summary>
        /// A previously reported stall has cleared — the missing request arrived and the
        /// server has resumed processing. Implementations should hide any stall UI shown
        /// in response to <see cref="OnSessionStalled"/>.
        /// </summary>
        void OnSessionRecovered(StallNotification notification);
    }
}
