using Microsoft.Extensions.Logging;

namespace SharedMeta.Server.Core;

internal static partial class Log
{
    // ── SessionManagerGrain ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Activated for player: {PlayerId}")]
    public static partial void SessionActivated(this ILogger logger, string playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deactivating for player: {PlayerId}")]
    public static partial void SessionDeactivating(this ILogger logger, string playerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error unsubscribing from {EntityId} during deactivation")]
    public static partial void ErrorUnsubscribingOnDeactivate(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "New session started for {PlayerId}: {SessionId}")]
    public static partial void NewSessionStarted(this ILogger logger, string playerId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resumed session for {PlayerId}, {MissedCount} missed packets")]
    public static partial void SessionResumed(this ILogger logger, string playerId, int missedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected old session {SessionId} for {PlayerId}")]
    public static partial void OldSessionRejected(this ILogger logger, Guid sessionId, string playerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "New session {SessionId} supersedes previous for {PlayerId}")]
    public static partial void SessionSuperseded(this ILogger logger, Guid sessionId, string playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Observer subscribed for {PlayerId} (count={Count})")]
    public static partial void ObserverSubscribed(this ILogger logger, string playerId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Observers cleared for {PlayerId}")]
    public static partial void ObserversCleared(this ILogger logger, string playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{PlayerId} subscribed to {EntityId} (entitySeq={EntitySeq})")]
    public static partial void SubscribedToEntity(this ILogger logger, string playerId, string entityId, long entitySeq);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error subscribing to {EntityId}")]
    public static partial void ErrorSubscribing(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error unsubscribing from {EntityId}")]
    public static partial void ErrorUnsubscribing(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{PlayerId} unsubscribed from {EntityId}")]
    public static partial void UnsubscribedFromEntity(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Returning cached response for requestId {RequestId}")]
    public static partial void CachedResponseReturned(this ILogger logger, long requestId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "SendToEntityAsync: player={PlayerId}, entity={EntityId}, reqId={RequestId}, method={ServiceName}.{MethodName}")]
    public static partial void SendToEntity(this ILogger logger, string playerId, string entityId, long requestId, string serviceName, string methodName);

    [LoggerMessage(Level = LogLevel.Trace, Message = "RPC returned: player={PlayerId}, entity={EntityId}, entitySeq={EntitySeq}, knownSeq={KnownSeq}, queuedBroadcasts={QueuedCount}, hasError={HasError}")]
    public static partial void RpcReturned(this ILogger logger, string playerId, string entityId, long entitySeq, long knownSeq, int queuedCount, bool hasError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error calling entity {EntityId}")]
    public static partial void ErrorCallingEntity(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "FAST PATH: player={PlayerId}, sessionSeq={SessionSeq}, totalOps={TotalOps}, knownSeq={KnownSeq}")]
    public static partial void FastPath(this ILogger logger, string playerId, long sessionSeq, int totalOps, long knownSeq);

    [LoggerMessage(Level = LogLevel.Trace, Message = "DEFERRED PATH: player={PlayerId}, requestId={RequestId}, entitySeq={EntitySeq}, knownSeq={KnownSeq}, precedingCount={PrecedingCount}")]
    public static partial void DeferredPath(this ILogger logger, string playerId, long requestId, long entitySeq, long knownSeq, int precedingCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Queued broadcast for RPC bundling: player={PlayerId}, entitySeq={EntitySeq}")]
    public static partial void BroadcastQueuedForRpc(this ILogger logger, string playerId, long entitySeq);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Holding out-of-order broadcast for {EntityId}: entitySeq={EntitySeq}, expected={Expected}")]
    public static partial void BroadcastOutOfOrder(this ILogger logger, string entityId, long entitySeq, long expected);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Ignoring duplicate/old broadcast: player={PlayerId}, entitySeq={EntitySeq}, expected={Expected}")]
    public static partial void BroadcastDuplicate(this ILogger logger, string playerId, long entitySeq, long expected);

    [LoggerMessage(Level = LogLevel.Trace, Message = "BufferBroadcast: player={PlayerId}, entityId={EntityId}")]
    public static partial void BufferBroadcast(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "FlushOutgoingBatch: player={PlayerId}, sessionSeq={SessionSeq}, count={Count}")]
    public static partial void FlushBatch(this ILogger logger, string playerId, long sessionSeq, int count);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Resolved deferred response for requestId {RequestId}")]
    public static partial void DeferredResolved(this ILogger logger, long requestId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entity {EntityId} deactivated, removed subscription")]
    public static partial void EntityDeactivated(this ILogger logger, string entityId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Cleaned up {Removed} packets (ack seq={AcknowledgedSeq})")]
    public static partial void PacketsCleanedBySeq(this ILogger logger, int removed, long acknowledgedSeq);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Cleaned up {Removed} packets by count limit")]
    public static partial void PacketsCleanedByCount(this ILogger logger, int removed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Graceful disconnect for {PlayerId}: session reset")]
    public static partial void GracefulDisconnect(this ILogger logger, string playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Transport disconnected for {PlayerId}: saved {Count} subscriptions")]
    public static partial void TransportDisconnected(this ILogger logger, string playerId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Graceful disconnect: {ConnectionId} (player={PlayerId})")]
    public static partial void HandlerGracefulDisconnect(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "GracefulDisconnect error")]
    public static partial void HandlerGracefulDisconnectError(this ILogger logger, Exception ex);

    // ── EntityGrain ──────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Activated: {StateType}/{EntityId}")]
    public static partial void EntityGrainActivated(this ILogger logger, string stateType, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deactivating: {StateType}/{EntityId}")]
    public static partial void EntityGrainDeactivating(this ILogger logger, string stateType, string entityId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EntityId}: access denied for {PlayerId} (policy={Policy})")]
    public static partial void EntityAccessDenied(this ILogger logger, string entityId, string playerId, string policy);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EntityId}: {PlayerId} subscribed")]
    public static partial void PlayerSubscribed(this ILogger logger, string entityId, string playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EntityId}: {PlayerId} unsubscribed")]
    public static partial void PlayerUnsubscribed(this ILogger logger, string entityId, string playerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling call")]
    public static partial void ErrorHandlingCall(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling cross-entity call")]
    public static partial void ErrorHandlingCrossEntityCall(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling external event")]
    public static partial void ErrorHandlingExternalEvent(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "DistributeBroadcasts: entity={EntityId}, entitySeq={EntitySeq}, broadcastCount={BroadcastCount}, subscribers={Subscribers}, exclude={Exclude}")]
    public static partial void DistributingBroadcasts(this ILogger logger, string entityId, long entitySeq, int broadcastCount, int subscribers, string exclude);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Skipping excluded subscriber: {PlayerId}")]
    public static partial void SkippingExcludedSubscriber(this ILogger logger, string playerId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Sending broadcast to {PlayerId}: entity={EntityId}, entitySeq={EntitySeq}, method={ServiceName}.{MethodName}")]
    public static partial void SendingBroadcast(this ILogger logger, string playerId, string entityId, long entitySeq, string serviceName, string methodName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error broadcasting to {PlayerId}")]
    public static partial void ErrorBroadcasting(this ILogger logger, Exception ex, string playerId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Broadcast sent to {SentCount}/{TotalCount} subscribers (method={ServiceName}.{MethodName})")]
    public static partial void BroadcastSent(this ILogger logger, int sentCount, int totalCount, string serviceName, string methodName);

    // ── EntityGrain — Persistence ──────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "State loaded: {StateType}/{EntityId}, subscribers={SubscriberCount}, entitySeq={SequenceNumber}")]
    public static partial void EntityStateLoaded(this ILogger logger, string stateType, string entityId, int subscriberCount, long sequenceNumber);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pruned {Count} expired subscriber(s) from {EntityId}")]
    public static partial void ExpiredSubscribersPruned(this ILogger logger, string entityId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "State initialized: {StateType}/{EntityId}, version={Version}")]
    public static partial void EntityStateInitialized(this ILogger logger, string stateType, string entityId, int version);

    [LoggerMessage(Level = LogLevel.Trace, Message = "State persisted: {EntityId}")]
    public static partial void EntityStatePersisted(this ILogger logger, string entityId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Persistence deferred: {EntityId}, requests={Requests}, policy={Policy}")]
    public static partial void PersistenceDeferred(this ILogger logger, string entityId, int requests, string policy);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Persistence forced: {EntityId}, requests={Requests}")]
    public static partial void PersistenceForced(this ILogger logger, string entityId, int requests);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Restored subscriber {PlayerId} for {EntityId}")]
    public static partial void SubscriberRestored(this ILogger logger, string entityId, string playerId);

    // ── MetaConnectionHandler ────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "SessionConnect: {PlayerId} (success={Success}, isNew={IsNew})")]
    public static partial void HandlerSessionConnect(this ILogger logger, string playerId, bool success, bool isNew);

    // 0.24.0+ Signature-handshake tracing. Three transition points covering every path
    // a client takes through phase-1 / phase-2. Read alongside ClientDispatcher's mirror
    // log entries on the client side — together they trace the complete negotiation flow.
    [LoggerMessage(Level = LogLevel.Information, Message = "Handshake[{PlayerId}] phase-1 HIT: clientHash=0x{ClientHash:X16} known, serverHash=0x{ServerHash:X16}, annotation: {StatusesCount} methods ({RejectedCount} rejected, {ForcePatchCount} force-patch)")]
    public static partial void HandshakeAnnotationFromCache(this ILogger logger, string playerId, ulong clientHash, ulong serverHash, int statusesCount, int rejectedCount, int forcePatchCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handshake[{PlayerId}] phase-1 MISS: clientHash=0x{ClientHash:X16} unknown, serverHash=0x{ServerHash:X16}; needs phase-2 registration")]
    public static partial void HandshakeNeedsRegistration(this ILogger logger, string playerId, ulong clientHash, ulong serverHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handshake[{PlayerId}] phase-2 REGISTER: clientHash=0x{ClientHash:X16} ({KnownMethodsCount} methods) -> serverHash=0x{ServerHash:X16}, annotation: {RejectedCount} rejected, {ForcePatchCount} force-patch, {ServerOnlyCount} server-only (client doesn't know)")]
    public static partial void HandshakeSignatureRegistered(this ILogger logger, string playerId, ulong clientHash, int knownMethodsCount, ulong serverHash, int rejectedCount, int forcePatchCount, int serverOnlyCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session not connected on connection {ConnectionId} ({Operation}) — pushed RequireSessionReconnect, expecting client to re-handshake")]
    public static partial void HandshakeSessionRecoveryPrompted(this ILogger logger, string connectionId, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "SessionConnect error")]
    public static partial void HandlerSessionConnectError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subscribe: {PlayerId} -> {EntityId} (success={Success})")]
    public static partial void HandlerSubscribe(this ILogger logger, string playerId, string entityId, bool success);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscribe error")]
    public static partial void HandlerSubscribeError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unsubscribe: {PlayerId} -> {EntityId}")]
    public static partial void HandlerUnsubscribe(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unsubscribe error")]
    public static partial void HandlerUnsubscribeError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "RpcCall error")]
    public static partial void HandlerRpcCallError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected RPC reqId={RequestId}: session {SessionId} was superseded (current={CurrentSessionId})")]
    public static partial void RpcSessionSuperseded(this ILogger logger, long requestId, Guid sessionId, Guid currentSessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "AcknowledgeSequence error")]
    public static partial void HandlerAcknowledgeError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disconnected: {ConnectionId} (player={PlayerId})")]
    public static partial void HandlerDisconnected(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error clearing observer")]
    public static partial void ErrorClearingObserver(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Observer renewal failed")]
    public static partial void ObserverRenewalFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "OnBatch: player={PlayerId}, seq={Seq}, count={Count}")]
    public static partial void ObserverOnBatch(this ILogger logger, string? playerId, long seq, int count);

    // ── MetaProviderBase ─────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in methodId={MethodId}")]
    public static partial void ProviderCallError(this ILogger logger, Exception ex, ushort methodId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event error in {SubscriberInterface}.{MethodName}")]
    public static partial void ProviderEventError(this ILogger logger, Exception ex, string subscriberInterface, string methodName);
}
