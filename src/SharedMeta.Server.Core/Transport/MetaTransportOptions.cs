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
        /// When true (default), <c>SessionConnect</c> asks the registered
        /// <see cref="IPlayerIdentityValidator"/> whether the token's PlayerId still exists and
        /// rejects it if not. Only takes effect together with <see cref="RequireAuthentication"/>:
        /// without it a PlayerId is client-supplied rather than claim-derived, so there is no auth
        /// store to check it against.
        ///
        /// Set to false when authenticated connections may legitimately carry identities the auth
        /// store doesn't hold (service accounts, bots, externally minted tokens).
        /// </summary>
        public bool ValidatePlayerIdentity { get; set; } = true;

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

        /// <summary>
        /// Current server application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Sent to clients in every SessionConnectResponse when set.
        /// Required for version checking to work — used to enforce major version compatibility.
        /// </summary>
        public string? ServerVersion { get; set; }

        /// <summary>
        /// Minimum client version the server will accept, in "major.minor.patch" format (e.g. "1.2.0").
        /// When set along with <see cref="ServerVersion"/>, clients that send a version below this
        /// or with a different major are rejected with a descriptive error.
        /// Clients that do not send a version at all are allowed through (backward compatibility).
        /// </summary>
        public string? MinClientVersion { get; set; }

        /// <summary>
        /// Maximum client version this server supports, in "major.minor.patch" format (e.g. "2.3.*").
        /// Clients newer than this (e.g. a client built against the next server version while this
        /// server is still on the old version) are rejected with "client too new for this server."
        /// Use <c>*</c> for the patch component (e.g. "2.3.*") to allow any patch of that minor.
        /// Null = no upper bound.
        /// </summary>
        public string? MaxClientVersion { get; set; }

        /// <summary>
        /// 0.24.0+ When <c>true</c>, <c>SessionConnect</c> rejects clients that send
        /// <c>ClientSignatureHash = 0</c> (legacy / no-negotiation opt-out). Required when
        /// the wire packet shape relies on <c>MethodId</c> translation (no string-fallback
        /// resolution on the server). Default <c>false</c> for backward compatibility — flip
        /// on when every supported client ships a generated <c>ClientSignature</c>.
        /// </summary>
        public bool RequireClientSignature { get; set; }

        /// <summary>
        /// Applied when no <see cref="MetaTransportOptions"/> is registered at all — the bound has
        /// to hold for a host that never configured transport options, not only for one that did.
        /// </summary>
        public static readonly TimeSpan DefaultMaxClientTimeSkew = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How far an RPC's client-supplied <c>ServerTimeTicks</c> may sit from the silo clock
        /// before the server overrides it with its own. Default 30 seconds; <see cref="TimeSpan.Zero"/>
        /// disables the check and restores pre-0.41.0 "trust the client" behaviour.
        /// <para>
        /// The value has to travel on the wire because the client runs the same body optimistically
        /// and both sides must see one instant. That makes it attacker-controlled: without a bound,
        /// a modified client sets the clock forward and harvests every cooldown, timer and
        /// regeneration tick the body computes from it.
        /// </para>
        /// <para>
        /// An honest client derives this value from the last server sync plus local elapsed time,
        /// never from its own wall clock, so it tracks the silo within round-trip latency — the
        /// default window is orders of magnitude wider than any legitimate drift. A clamped call
        /// still executes (rejecting it would turn a clock hiccup into a failed purchase); the
        /// divergence surfaces through the normal desync channel.
        /// </para>
        /// </summary>
        public TimeSpan MaxClientTimeSkew { get; set; } = DefaultMaxClientTimeSkew;
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
