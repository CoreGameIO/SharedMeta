namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Thresholds for client-side connection health monitoring.
    /// Used by <see cref="IConnectionHealthListener"/> via <c>ClientDispatcher</c>.
    /// </summary>
    public class ConnectionHealthOptions
    {
        /// <summary>
        /// Milliseconds before a pending request triggers <see cref="ConnectionHealthStatus.Slow"/>.
        /// UI should show a lightweight spinner. Default: 1000 ms.
        /// </summary>
        public int SoftTimeoutMs { get; set; } = 1000;

        /// <summary>
        /// Milliseconds before a pending request triggers <see cref="ConnectionHealthStatus.Unresponsive"/>.
        /// UI should show a "connection issue" dialog. Default: 5000 ms.
        /// </summary>
        public int HardTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// How often to auto-retry pending requests that exceeded <see cref="SoftTimeoutMs"/>.
        /// Set to 0 to disable auto-retry (rely on server-side STALL notifications instead).
        /// Default: 2000 ms.
        /// </summary>
        public int RetryIntervalMs { get; set; } = 2000;
    }
}
