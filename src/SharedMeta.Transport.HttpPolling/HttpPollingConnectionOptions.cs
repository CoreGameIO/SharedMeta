namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// Configuration options for HttpPollingConnection (client side).
    /// </summary>
    public class HttpPollingConnectionOptions
    {
        /// <summary>
        /// Base URL of the server (e.g., "https://localhost:5001/meta-http").
        /// </summary>
        public string ServerUrl { get; set; } = "";

        /// <summary>
        /// Timeout for the long-poll HTTP request.
        /// Should be slightly longer than the server's poll hold time (30s) to account for network latency.
        /// </summary>
        public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(35);

        /// <summary>
        /// Timeout for regular (non-poll) HTTP requests.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum retry delay for exponential backoff during reconnection.
        /// </summary>
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Initial retry delay for exponential backoff.
        /// </summary>
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Optional JWT access token for authentication.
        /// When set, adds Authorization: Bearer header to all requests.
        /// </summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Optional access-token provider, resolved fresh on every request (and reconnect). Takes
        /// precedence over <see cref="AccessToken"/> when set — pair with
        /// <see cref="SharedMeta.Client.MetaTokenManager.GetTokenAsync()"/> so a refreshed token is
        /// picked up automatically without rebuilding the connection. The provider's fast path returns
        /// the cached token without a network call, so per-request resolution is cheap.
        /// </summary>
        public System.Func<System.Threading.Tasks.Task<string?>>? AccessTokenProvider { get; set; }

        /// <summary>
        /// Client application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Sent to the server during SessionConnect for compatibility checking.
        /// Null to skip version reporting.
        /// </summary>
        public string? ClientVersion { get; set; }
    }
}
