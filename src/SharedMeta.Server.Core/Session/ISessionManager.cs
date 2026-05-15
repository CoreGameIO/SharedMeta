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
        /// If sessionId matches current session, returns missed packets.
        /// If sessionId is old (superseded), returns error.
        /// If sessionId is new, starts a new session.
        /// </summary>
        /// <param name="sessionId">The client's session ID.</param>
        /// <param name="lastAcknowledgedSequence">The last sequence number the client received.</param>
        /// <returns>Connection result with missed packets or error.</returns>
        Task<SessionConnectionResult> ConnectAsync(Guid sessionId, long lastAcknowledgedSequence);

        /// <summary>
        /// Set the observer for receiving notifications.
        /// Called after successful ConnectAsync.
        /// </summary>
        /// <param name="observer">The observer (Hub connection).</param>
        Task SetObserverAsync(ISessionObserver observer);

        /// <summary>
        /// 0.22.0+ Push the player's compatibility capabilities to the grain. Called by
        /// <c>MetaConnectionHandler</c> immediately after SessionConnect (phase-1) or
        /// RegisterClientSignature (phase-2) resolves. The grain stores the snapshot and
        /// uses it for two purposes:
        /// <list type="bullet">
        ///   <item>During <see cref="SubscribeToEntityAsync"/>: it forwards the subset of
        ///     <see cref="ClientCapabilities.ForceServerPatchMethods"/> applicable to the target
        ///     entity to the <c>EntityGrain</c>, so the entity can aggregate "does any
        ///     subscriber need patch tracking for this method?" cheaply at dispatch time.</item>
        ///   <item>During broadcast fan-out: <c>BroadcastToSessionOp</c> tailors each
        ///     <see cref="SessionResponse"/> per this player — modern subscribers receive
        ///     replay payload only, force-patch subscribers receive patch bytes only.</item>
        /// </list>
        /// Null caps (negotiation disabled or pending) means the grain stays in pass-through
        /// mode and broadcasts go out untouched.
        /// </summary>
        Task SetClientCapabilitiesAsync(ClientCapabilities? capabilities);

        /// <summary>
        /// Clear the observer when disconnecting.
        /// </summary>
        Task ClearObserverAsync();

        /// <summary>
        /// Graceful disconnect: client explicitly leaves.
        /// Full cleanup — clear observer, unsubscribe from all entities, reset session.
        /// Client cannot resume this session.
        /// </summary>
        Task GracefulDisconnectAsync();

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
        /// <returns>Subscription result with current state.</returns>
        Task<EntitySubscriptionResult> SubscribeToEntityAsync(string entityId, string stateTypeName, string? clientVersion = null);

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
        Task SignalEntityAsync(string entityId, string serviceName, RpcCall call);

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
