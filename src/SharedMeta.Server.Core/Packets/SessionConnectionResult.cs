using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Result of a connection attempt to SessionManager.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class SessionConnectionResult
    {
        /// <summary>Whether the connection was accepted.</summary>
        [Id(0)] public bool Success { get; set; }

        /// <summary>Error message if connection failed.</summary>
        [Id(1)] public string? Error { get; set; }

        /// <summary>The current session ID (may differ from requested if new session created).</summary>
        [Id(2)] public Guid SessionId { get; set; }

        /// <summary>Missed responses since the last acknowledged sequence number.</summary>
        [Id(3)] public List<SessionResponse> MissedPackets { get; set; } = new();

        /// <summary>Whether this is a new session (vs resuming existing).</summary>
        [Id(4)] public bool IsNewSession { get; set; }

        /// <summary>Current server UTC ticks for clock synchronization.</summary>
        [Id(5)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// 0.24.0+ Per-claim subscription verdicts. Replaces the older
        /// <c>ResubscribedEntities</c> server-driven re-subscribe flow — subscriptions are
        /// now owned by the client (sent via <c>SessionConnectRequest.ClaimedSubscriptions</c>),
        /// the server verifies with each entity grain and reports
        /// <see cref="SubscriptionStatus.Continued"/> / <see cref="SubscriptionStatus.Refreshed"/>
        /// / <see cref="SubscriptionStatus.Failed"/> per entity.
        /// </summary>
        [Id(6)] public List<SubscriptionResult>? Subscriptions { get; set; }

        /// <summary>
        /// 0.24.0+ Structured failure reason when <see cref="Success"/> is false. Lets the
        /// transport handler map the grain-level error to a wire-level
        /// <see cref="SharedMeta.Core.Transport.SessionConnectFailureReason"/> the client can
        /// distinguish from generic rejection (no string parsing).
        /// </summary>
        [Id(7)] public SessionConnectFailureReason FailureReason { get; set; }
    }
}
