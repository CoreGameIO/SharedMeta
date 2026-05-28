using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Session manager grain interface.
    /// One instance per player, manages all their sessions and entity subscriptions.
    /// </summary>
    public interface ISessionManager : IGrainWithStringKey
    {
        /// <summary>
        /// Connect to the session manager.
        /// <para>
        /// 0.24.0+ Explicit mode:
        /// <list type="bullet">
        ///   <item><see cref="SessionConnectMode.StartNew"/> — always create a fresh session.
        ///     Supersedes any current session (clears observers, unsubscribes from entities,
        ///     resets sequence numbers). <paramref name="sessionId"/> is the new id to assign
        ///     (handler allocates via <c>Guid.NewGuid()</c>). <c>IsNewSession=true</c>.</item>
        ///   <item><see cref="SessionConnectMode.Resume"/> — re-bind to an existing session.
        ///     If <paramref name="sessionId"/> matches the current one, replays missed packets
        ///     and returns <c>IsNewSession=false</c>. Otherwise returns
        ///     <c>Success=false</c> with
        ///     <see cref="SessionConnectionResult.FailureReason"/> =
        ///     <see cref="SessionConnectFailureReason.SessionUnknown"/> — the client decides
        ///     whether to retry with StartNew.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="sessionId">The client's session ID (Resume) or the allocated new id (StartNew).</param>
        /// <param name="lastAcknowledgedSequence">The last sequence number the client received.</param>
        /// <param name="mode">0.24.0+ Connect intent.</param>
        /// <param name="lastCompletedRequestId">0.24.0+ The highest RequestId the client has fully completed (response delivered + resolved).
        /// Used on Resume to advance the server's RPC ordering baseline past any responses the server lost (eviction/crash before persistence flush)
        /// but the client did receive — prevents the next-new RequestId from being misclassified as OutOfOrder and stashed forever.</param>
        /// <param name="claimedSubscriptions">0.24.0+ Client-owned subscription list to reclaim on Resume.
        /// Each claim names an entity (id + state type) and the client's last-known entity sequence number;
        /// the grain calls <c>EntityGrain.ReclaimSubscriptionAsync</c> per claim to verify (Continued / Refreshed / Failed).
        /// Null/empty for StartNew or for first-ever connect. If any single claim returns Failed,
        /// the whole Resume aborts with <c>SubscriptionReclaimFailed</c>.</param>
        /// <param name="clientVersion">0.24.0+ Forwarded to <c>ReclaimSubscriptionAsync</c> so the entity grain
        /// can drive per-client config branch resolution + force-patch refcount setup on the Refreshed path.</param>
        /// <param name="clientSignatureHash">0.24.0+ Forwarded to <c>ReclaimSubscriptionAsync</c> for signature-aware
        /// reclaim. Mismatch with the entity grain's stored hash triggers Refreshed (force-patch refs recompute).</param>
        /// <returns>Connection result with missed packets, subscription verdicts, new id, or structured failure.</returns>
        Task<SessionConnectionResult> ConnectAsync(
            Guid sessionId,
            long lastAcknowledgedSequence,
            SessionConnectMode mode,
            long lastCompletedRequestId,
            IReadOnlyList<SubscriptionClaim>? claimedSubscriptions,
            string? clientVersion,
            ulong clientSignatureHash);

        /// <summary>
        /// Set the observer for receiving notifications.
        /// Called after successful ConnectAsync.
        /// </summary>
        /// <param name="observer">The observer (Hub connection).</param>
        Task SetObserverAsync(ISessionObserver observer);

        /// <summary>
        /// Clear the observer when disconnecting.
        /// </summary>
        Task ClearObserverAsync();

        /// <summary>
        /// Graceful disconnect: client explicitly leaves.
        /// Full cleanup — clear observer, unsubscribe from all entities, reset session.
        /// Client cannot resume this session.
        /// <para>
        /// 0.24.0+ The caller supplies <paramref name="sessionId"/> so the grain can guard
        /// against stale graceful-disconnect messages from a superseded handler (typical
        /// cause: old SignalR connection's tear-down arrives at the grain after a fresh
        /// session has been established on a new connection). Mismatched sessionId →
        /// the grain logs and skips cleanup, leaving the current session intact.
        /// </para>
        /// </summary>
        Task GracefulDisconnectAsync(Guid sessionId);

        /// <summary>
        /// Transport disconnected (timeout/network loss).
        /// Saves subscriptions for reconnect, unsubscribes from entity grains,
        /// but keeps session alive so client can resume with the same sessionId.
        /// </summary>
        Task OnTransportDisconnectedAsync();

        /// <summary>
        /// Subscribe to an entity.
        /// Returns current state and registers for broadcasts.
        /// </summary>
        /// <param name="entityId">The entity to subscribe to.</param>
        /// <param name="stateTypeName">The state type name for auto-creation.</param>
        /// <param name="clientVersion">Caller's app version for per-client config resolution.</param>
        /// <param name="clientSignatureHash">Negotiated signature hash. EntityGrain resolves
        ///     <see cref="ClientSignatureAnnotated"/> locally via <c>IClientSignatureRegistry</c>.
        ///     0 = no negotiation (no force-patch contributions).</param>
        /// <returns>Subscription result with current state.</returns>
        Task<EntitySubscriptionResult> SubscribeToEntityAsync(string entityId, string stateTypeName, string? clientVersion = null, ulong clientSignatureHash = 0);

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        /// <param name="entityId">The entity to unsubscribe from.</param>
        Task UnsubscribeFromEntityAsync(string entityId);

        /// <summary>
        /// Send a request to an entity.
        /// If requestId was already processed, returns cached response (idempotency).
        /// Returns SessionResponse with a list of SessionOp operations.
        /// Rejects calls where sessionId doesn't match the current session (superseded).
        /// </summary>
        /// <param name="entityId">The target entity.</param>
        /// <param name="requestId">Client-managed request ID for idempotency.</param>
        /// <param name="call">The RPC call.</param>
        /// <param name="lastAcknowledgedSequence">Piggybacked ack: highest sequence client has processed.</param>
        /// <param name="sessionId">Caller's session ID — rejected if it doesn't match the current session.</param>
        /// <returns>SessionResponse with result and sequence numbers.</returns>
        Task<SessionResponse> SendToEntityAsync(string entityId, long requestId, RpcCall call, long lastAcknowledgedSequence, Guid sessionId);

        /// <summary>
        /// Execute a query call on an entity without subscribing.
        /// Resolves the entity grain and forwards the call. No subscription check,
        /// no sequence numbers, no broadcasts.
        /// </summary>
        Task<QueryCallResponse> QueryEntityAsync(string entityId, string serviceName, RpcCall call);

        /// <summary>
        /// Fire a signal call on an entity — fire-and-forget, void return.
        /// Resolves the entity grain and forwards the call via the grain's <c>[OneWay]</c>
        /// <c>HandleSignalAsync</c>. No subscription check, no sequence numbers, no broadcasts,
        /// no response. Server-side errors are logged, not propagated.
        /// </summary>
        Task SignalEntityAsync(string entityId, string serviceName, [Immutable]RpcCall call);

        /// <summary>
        /// Acknowledge that all packets up to and including this sequence have been received.
        /// Allows SessionManager to clean up stored packets.
        /// </summary>
        /// <param name="sequenceNumber">The highest received sequence number.</param>
        Task AcknowledgeSequenceAsync(long sequenceNumber);

        /// <summary>
        /// Get the current session ID.
        /// </summary>
        Task<Guid> GetCurrentSessionIdAsync();
    }
}
