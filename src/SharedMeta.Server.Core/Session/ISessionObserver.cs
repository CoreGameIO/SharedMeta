using Orleans;
using Orleans.Concurrency;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Observer interface for receiving notifications from SessionManager to Hub.
    /// Implemented by the SignalR Hub connection handler.
    ///
    /// All methods use [OneWay] — the grain sends the message fire-and-forget
    /// without waiting for a response. This is the recommended Orleans pattern
    /// for observer notifications.
    /// </summary>
    public interface ISessionObserver : IGrainObserver
    {
        /// <summary>
        /// Called when a SessionResponse should be sent to the client.
        /// The response is the atomic unit of delivery — one SequenceNumber per response.
        /// </summary>
        [OneWay]
        Task OnBatch(SessionResponse response);

        /// <summary>
        /// Called when an entity the client is subscribed to is deactivating.
        /// Client should be notified to resubscribe if needed.
        /// </summary>
        [OneWay]
        Task OnEntityDeactivating(string entityId);

        /// <summary>
        /// Called when the session is being terminated (e.g., superseded by another session).
        /// </summary>
        [OneWay]
        Task OnSessionTerminated(string reason);
    }
}
