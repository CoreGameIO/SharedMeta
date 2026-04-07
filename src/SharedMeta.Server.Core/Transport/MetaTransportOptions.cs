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

        /// <summary>
        /// When true, server caches recent server-side patches per connection and accepts
        /// client desync reports via /meta/desync-report. Reports are stored in DesyncReportGrain.
        /// Default: false (production safe).
        /// </summary>
        public bool DesyncReportingEnabled { get; set; }

        /// <summary>
        /// Size of the per-connection ring buffer that caches server patches for desync reporting.
        /// Older entries are evicted. Default: 16.
        /// </summary>
        public int DesyncReportPatchCacheSize { get; set; } = 16;

        /// <summary>
        /// Logging verbosity for desync reports. Default: Warning.
        /// - None: do not log
        /// - Warning: one-line summary per report
        /// - Information: summary + field count
        /// - Debug: full text diff with field paths and values
        /// </summary>
        public DesyncLogLevel DesyncLogLevel { get; set; } = DesyncLogLevel.Warning;
    }

    /// <summary>
    /// Verbosity levels for server-side desync logging.
    /// </summary>
    public enum DesyncLogLevel
    {
        None = 0,
        Warning = 1,
        Information = 2,
        Debug = 3
    }
}
