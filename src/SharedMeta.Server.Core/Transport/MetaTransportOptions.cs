namespace SharedMeta.Server.Core.Transport
{
    /// <summary>
    /// Shared options for all Meta transport layers (SignalR, HTTP Polling).
    /// Registered via DI; transports read these to enforce common behavior.
    /// </summary>
    public class MetaTransportOptions
    {
        /// <summary>
        /// When true, anonymous (unauthenticated) connections are rejected at SessionConnect.
        /// The transport will extract PlayerId from JWT claims ("sub" or NameIdentifier)
        /// and ignore the client-supplied PlayerId.
        ///
        /// When false (default), unauthenticated clients may connect with any PlayerId.
        /// If a client happens to be authenticated, their claim-based PlayerId is still used.
        /// </summary>
        public bool RequireAuthentication { get; set; }

        /// <summary>
        /// When true, clients can enable debug features (deep desync detection) via SetDebugOptions.
        /// Default: false (production safe). Enable during development/testing.
        /// </summary>
        public bool AllowDebugApi { get; set; }
    }
}
