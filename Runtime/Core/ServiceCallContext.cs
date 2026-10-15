namespace SharedMeta.Core
{
    /// <summary>
    /// Context for framework service calls.
    /// Contains routing information for sending events to entities.
    /// </summary>
    public class ServiceCallContext
    {
        /// <summary>
        /// The player/client ID making the request.
        /// </summary>
        public string PlayerId { get; set; } = "";

        /// <summary>
        /// The entity ID to route the response/event to.
        /// </summary>
        public string EntityId { get; set; } = "";

        /// <summary>
        /// The type of service making the call (for logging/debugging).
        /// </summary>
        public string ServiceType { get; set; } = "";

        /// <summary>
        /// Optional correlation ID for request tracking.
        /// </summary>
        public string? CorrelationId { get; set; }
    }
}
