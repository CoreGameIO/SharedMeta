namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Client-side connection health status, driven by pending RPC request age.
    /// </summary>
    public enum ConnectionHealthStatus
    {
        /// <summary>All requests resolved (or none pending long enough). Connection healthy.</summary>
        Healthy,

        /// <summary>
        /// Soft timeout: a request has been pending longer than
        /// <see cref="ConnectionHealthOptions.SoftTimeoutMs"/>.
        /// UI should show a lightweight spinner/indicator.
        /// </summary>
        Slow,

        /// <summary>
        /// Hard timeout: a request has been pending longer than
        /// <see cref="ConnectionHealthOptions.HardTimeoutMs"/>.
        /// UI should show a "connection issue" dialog with retry option.
        /// </summary>
        Unresponsive,
    }

    /// <summary>
    /// Client-side connection health listener. Notified when pending RPC requests
    /// exceed configured timeout thresholds. Complementary to <see cref="ISessionHealthListener"/>
    /// which handles server-side stall detection.
    ///
    /// Callbacks are invoked from <c>ProcessPendingBroadcasts()</c> on the game-loop thread,
    /// so UI updates can be made directly.
    /// </summary>
    public interface IConnectionHealthListener
    {
        /// <summary>
        /// Connection health status has changed. Called once per transition
        /// (Healthy→Slow, Slow→Unresponsive, *→Healthy). Not called repeatedly
        /// while status remains the same.
        /// </summary>
        /// <param name="status">Current health status.</param>
        /// <param name="oldestPendingMs">Age of the oldest pending request in milliseconds,
        /// or 0 if status is <see cref="ConnectionHealthStatus.Healthy"/>.</param>
        /// <param name="pendingCount">Number of currently pending requests.</param>
        void OnConnectionHealthChanged(ConnectionHealthStatus status, long oldestPendingMs, int pendingCount);
    }
}
