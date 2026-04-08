using System;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Options controlling SessionManagerGrain behaviour for RPC ordering and stall handling.
    /// Registered via DI (<c>services.Configure&lt;SessionManagerOptions&gt;(...)</c>) and read once
    /// per grain activation.
    /// </summary>
    public class SessionManagerOptions
    {
        // ── RPC reordering ────────────────────────────────────────────────

        /// <summary>
        /// When true, the session manager reorders out-of-order RPC calls so they reach the
        /// entity grain in monotonically increasing <c>RequestId</c> order. Required for
        /// transports that do not preserve submission order at the wire level (HTTP polling,
        /// custom UDP, any pipeline with intermediate <c>Task.Run</c>).
        ///
        /// SignalR over a single hub connection already preserves order at the wire level,
        /// but enabling this option costs nothing in the in-order case (one extra integer
        /// comparison) and provides defense-in-depth.
        ///
        /// Default: <c>false</c>.
        /// </summary>
        public bool EnforceRpcOrder { get; set; }

        /// <summary>
        /// Maximum number of out-of-order RPC calls that can be stashed for one session
        /// while waiting for missing predecessors. When the buffer overflows the session is
        /// terminated with a stall error — the client must reconnect and start a new session.
        /// Default: 256.
        /// </summary>
        public int StashCapacity { get; set; } = 256;

        // ── Stall notifications ───────────────────────────────────────────

        /// <summary>
        /// First stall notification — sent when a gap in <c>RequestId</c> ordering has been
        /// open for at least this long. Lets the client show a "syncing…" indicator.
        /// Default: 500 ms.
        /// </summary>
        public TimeSpan SoftStallNotifyTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Second stall notification — sent when the gap has been open for this long.
        /// Lets the client show a "Connection issue. Wait or reconnect?" prompt to the user.
        /// Default: 10 seconds.
        /// </summary>
        public TimeSpan HardStallNotifyTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Absolute upper bound on stall duration. After this the session is terminated and
        /// the client must reconnect — at this point we assume the missing request was lost
        /// permanently and continuing would risk desync.
        /// Default: 5 minutes.
        /// </summary>
        public TimeSpan MaxStallDuration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How often the stall timer ticks while a gap is open. Smaller values yield more
        /// responsive notifications at the cost of more grain timer wake-ups.
        /// Default: 1 second.
        /// </summary>
        public TimeSpan StallTickInterval { get; set; } = TimeSpan.FromSeconds(1);

        // ── Logging ───────────────────────────────────────────────────────

        /// <summary>
        /// Verbosity for logging duplicate stashed RPC calls (same <c>RequestId</c> arriving
        /// twice while parked). Defaults to <see cref="StashDuplicateLogLevel.Debug"/>.
        /// </summary>
        public StashDuplicateLogLevel DuplicateStashLogLevel { get; set; } = StashDuplicateLogLevel.Debug;
    }

    /// <summary>
    /// Verbosity levels for duplicate stash diagnostics.
    /// </summary>
    public enum StashDuplicateLogLevel
    {
        None = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
    }
}
