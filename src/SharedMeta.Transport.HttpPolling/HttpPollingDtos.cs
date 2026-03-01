using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// Request body for the long-poll endpoint.
    /// </summary>
    public class PollRequest
    {
        /// <summary>
        /// Piggybacked acknowledgment of last processed sequence.
        /// Avoids a separate POST /ack call in the common case.
        /// </summary>
        public long LastAcknowledgedSequence { get; set; }
    }

    /// <summary>
    /// Response from the long-poll endpoint.
    /// Contains batched messages accumulated since the last poll.
    /// </summary>
    public class PollResponse
    {
        /// <summary>
        /// Broadcast messages queued since last poll.
        /// Each SessionResponse is one atomic delivery unit with its own SequenceNumber.
        /// </summary>
        public List<SessionResponse>? Broadcasts { get; set; }

        /// <summary>
        /// If non-null, the session was terminated with this reason.
        /// </summary>
        public string? SessionTerminated { get; set; }

        /// <summary>
        /// Entity IDs that are deactivating.
        /// </summary>
        public List<string>? DeactivatingEntities { get; set; }
    }
}
