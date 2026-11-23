using Microsoft.Extensions.Logging;

namespace SharedMeta.Server.Core;

internal static partial class Log
{
    // ── SessionManagerGrain ──────────────────────────────────────────

    [LoggerMessage(EventId = 1001, EventName = "SessionActivated", Level = LogLevel.Debug, Message = "Activated for player: {PlayerId}")]
    public static partial void SessionActivated(this ILogger logger, string playerId);

    [LoggerMessage(EventId = 1002, EventName = "SessionDeactivating", Level = LogLevel.Debug, Message = "Deactivating for player: {PlayerId}")]
    public static partial void SessionDeactivating(this ILogger logger, string playerId);

    [LoggerMessage(EventId = 1003, EventName = "ErrorUnsubscribingOnDeactivate", Level = LogLevel.Error, Message = "Error unsubscribing from {EntityId} during deactivation")]
    public static partial void ErrorUnsubscribingOnDeactivate(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(EventId = 1004, EventName = "NewSessionStarted", Level = LogLevel.Debug, Message = "New session started for {PlayerId}: {SessionId}")]
    public static partial void NewSessionStarted(this ILogger logger, string playerId, Guid sessionId);

    [LoggerMessage(EventId = 1005, EventName = "SessionResumed", Level = LogLevel.Information, Message = "Resumed session for {PlayerId}, {MissedCount} missed packets")]
    public static partial void SessionResumed(this ILogger logger, string playerId, int missedCount);

    [LoggerMessage(EventId = 1006, EventName = "OldSessionRejected", Level = LogLevel.Warning, Message = "Rejected old session {SessionId} for {PlayerId}")]
    public static partial void OldSessionRejected(this ILogger logger, Guid sessionId, string playerId);

    [LoggerMessage(EventId = 1007, EventName = "SessionSuperseded", Level = LogLevel.Information, Message = "New session {SessionId} supersedes previous for {PlayerId}")]
    public static partial void SessionSuperseded(this ILogger logger, Guid sessionId, string playerId);

    [LoggerMessage(EventId = 1008, EventName = "ObserverSubscribed", Level = LogLevel.Debug, Message = "Observer subscribed for {PlayerId} (count={Count})")]
    public static partial void ObserverSubscribed(this ILogger logger, string playerId, int count);

    [LoggerMessage(EventId = 1009, EventName = "ObserversCleared", Level = LogLevel.Debug, Message = "Observers cleared for {PlayerId}")]
    public static partial void ObserversCleared(this ILogger logger, string playerId);

    [LoggerMessage(EventId = 1010, EventName = "SubscribedToEntity", Level = LogLevel.Debug, Message = "{PlayerId} subscribed to {EntityId} (entitySeq={EntitySeq})")]
    public static partial void SubscribedToEntity(this ILogger logger, string playerId, string entityId, long entitySeq);

    [LoggerMessage(EventId = 1011, EventName = "ErrorSubscribing", Level = LogLevel.Error, Message = "Error subscribing to {EntityId}")]
    public static partial void ErrorSubscribing(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(EventId = 1012, EventName = "ErrorUnsubscribing", Level = LogLevel.Error, Message = "Error unsubscribing from {EntityId}")]
    public static partial void ErrorUnsubscribing(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(EventId = 1013, EventName = "UnsubscribedFromEntity", Level = LogLevel.Debug, Message = "{PlayerId} unsubscribed from {EntityId}")]
    public static partial void UnsubscribedFromEntity(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(EventId = 1014, EventName = "CachedResponseReturned", Level = LogLevel.Trace, Message = "Returning cached response for requestId {RequestId}")]
    public static partial void CachedResponseReturned(this ILogger logger, long requestId);

    [LoggerMessage(EventId = 1015, EventName = "SendToEntity", Level = LogLevel.Trace, Message = "SendToEntityAsync: player={PlayerId}, entity={EntityId}, reqId={RequestId}, method={ServiceName}.{MethodName}")]
    public static partial void SendToEntity(this ILogger logger, string playerId, string entityId, long requestId, string serviceName, string methodName);

    [LoggerMessage(EventId = 1016, EventName = "RpcReturned", Level = LogLevel.Trace, Message = "RPC returned: player={PlayerId}, entity={EntityId}, entitySeq={EntitySeq}, knownSeq={KnownSeq}, queuedBroadcasts={QueuedCount}, hasError={HasError}")]
    public static partial void RpcReturned(this ILogger logger, string playerId, string entityId, long entitySeq, long knownSeq, int queuedCount, bool hasError);

    [LoggerMessage(EventId = 1017, EventName = "ErrorCallingEntity", Level = LogLevel.Error, Message = "Error calling entity {EntityId}")]
    public static partial void ErrorCallingEntity(this ILogger logger, Exception ex, string entityId);

    [LoggerMessage(EventId = 1018, EventName = "FastPath", Level = LogLevel.Trace, Message = "FAST PATH: player={PlayerId}, sessionSeq={SessionSeq}, totalOps={TotalOps}, knownSeq={KnownSeq}")]
    public static partial void FastPath(this ILogger logger, string playerId, long sessionSeq, int totalOps, long knownSeq);

    [LoggerMessage(EventId = 1019, EventName = "DeferredPath", Level = LogLevel.Trace, Message = "DEFERRED PATH: player={PlayerId}, requestId={RequestId}, entitySeq={EntitySeq}, knownSeq={KnownSeq}, precedingCount={PrecedingCount}")]
    public static partial void DeferredPath(this ILogger logger, string playerId, long requestId, long entitySeq, long knownSeq, int precedingCount);

    [LoggerMessage(EventId = 1020, EventName = "BroadcastQueuedForRpc", Level = LogLevel.Trace, Message = "Queued broadcast for RPC bundling: player={PlayerId}, entitySeq={EntitySeq}")]
    public static partial void BroadcastQueuedForRpc(this ILogger logger, string playerId, long entitySeq);

    [LoggerMessage(EventId = 1021, EventName = "BroadcastOutOfOrder", Level = LogLevel.Debug, Message = "Holding out-of-order broadcast for {EntityId}: entitySeq={EntitySeq}, expected={Expected}")]
    public static partial void BroadcastOutOfOrder(this ILogger logger, string entityId, long entitySeq, long expected);

    [LoggerMessage(EventId = 1022, EventName = "BroadcastDuplicate", Level = LogLevel.Trace, Message = "Ignoring duplicate/old broadcast: player={PlayerId}, entitySeq={EntitySeq}, expected={Expected}")]
    public static partial void BroadcastDuplicate(this ILogger logger, string playerId, long entitySeq, long expected);

    [LoggerMessage(EventId = 1023, EventName = "BufferBroadcast", Level = LogLevel.Trace, Message = "BufferBroadcast: player={PlayerId}, entityId={EntityId}")]
    public static partial void BufferBroadcast(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(EventId = 1024, EventName = "FlushBatch", Level = LogLevel.Trace, Message = "FlushOutgoingBatch: player={PlayerId}, sessionSeq={SessionSeq}, count={Count}")]
    public static partial void FlushBatch(this ILogger logger, string playerId, long sessionSeq, int count);

    [LoggerMessage(EventId = 1025, EventName = "DeferredResolved", Level = LogLevel.Trace, Message = "Resolved deferred response for requestId {RequestId}")]
    public static partial void DeferredResolved(this ILogger logger, long requestId);

    [LoggerMessage(EventId = 1026, EventName = "EntityDeactivated", Level = LogLevel.Debug, Message = "Entity {EntityId} deactivated, removed subscription")]
    public static partial void EntityDeactivated(this ILogger logger, string entityId);

    [LoggerMessage(EventId = 1027, EventName = "PacketsCleanedBySeq", Level = LogLevel.Trace, Message = "Cleaned up {Removed} packets (ack seq={AcknowledgedSeq})")]
    public static partial void PacketsCleanedBySeq(this ILogger logger, int removed, long acknowledgedSeq);

    [LoggerMessage(EventId = 1028, EventName = "PacketsCleanedByCount", Level = LogLevel.Trace, Message = "Cleaned up {Removed} packets by count limit")]
    public static partial void PacketsCleanedByCount(this ILogger logger, int removed);

    [LoggerMessage(EventId = 1029, EventName = "GracefulDisconnect", Level = LogLevel.Debug, Message = "Graceful disconnect for {PlayerId}: session reset")]
    public static partial void GracefulDisconnect(this ILogger logger, string playerId);

    [LoggerMessage(EventId = 1030, EventName = "TransportDisconnected", Level = LogLevel.Debug, Message = "Transport disconnected for {PlayerId}: saved {Count} subscriptions")]
    public static partial void TransportDisconnected(this ILogger logger, string playerId, int count);

    [LoggerMessage(EventId = 1031, EventName = "HandlerGracefulDisconnect", Level = LogLevel.Debug, Message = "Graceful disconnect: {ConnectionId} (player={PlayerId})")]
    public static partial void HandlerGracefulDisconnect(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(EventId = 1032, EventName = "HandlerGracefulDisconnectError", Level = LogLevel.Error, Message = "GracefulDisconnect error")]
    public static partial void HandlerGracefulDisconnectError(this ILogger logger, Exception ex);

    // ── EntityGrain ──────────────────────────────────────────────────

    [LoggerMessage(EventId = 2001, EventName = "EntityGrainActivated", Level = LogLevel.Debug, Message = "Activated: {StateType}/{EntityId}")]
    public static partial void EntityGrainActivated(this ILogger logger, string stateType, string entityId);

    [LoggerMessage(EventId = 2002, EventName = "EntityGrainDeactivating", Level = LogLevel.Debug, Message = "Deactivating: {StateType}/{EntityId}")]
    public static partial void EntityGrainDeactivating(this ILogger logger, string stateType, string entityId);

    [LoggerMessage(EventId = 2003, EventName = "EntityAccessDenied", Level = LogLevel.Warning, Message = "{EntityId}: access denied for {PlayerId} (policy={Policy})")]
    public static partial void EntityAccessDenied(this ILogger logger, string entityId, string playerId, string policy);

    [LoggerMessage(EventId = 2004, EventName = "PlayerSubscribed", Level = LogLevel.Debug, Message = "{EntityId}: {PlayerId} subscribed")]
    public static partial void PlayerSubscribed(this ILogger logger, string entityId, string playerId);

    [LoggerMessage(EventId = 2005, EventName = "PlayerUnsubscribed", Level = LogLevel.Debug, Message = "{EntityId}: {PlayerId} unsubscribed")]
    public static partial void PlayerUnsubscribed(this ILogger logger, string entityId, string playerId);

    [LoggerMessage(EventId = 2006, EventName = "ErrorHandlingCall", Level = LogLevel.Error, Message = "Error handling call")]
    public static partial void ErrorHandlingCall(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2007, EventName = "ErrorHandlingCrossEntityCall", Level = LogLevel.Error, Message = "Error handling cross-entity call")]
    public static partial void ErrorHandlingCrossEntityCall(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2008, EventName = "ErrorHandlingExternalEvent", Level = LogLevel.Error, Message = "Error handling external event")]
    public static partial void ErrorHandlingExternalEvent(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2009, EventName = "DistributingBroadcasts", Level = LogLevel.Trace, Message = "DistributeBroadcasts: entity={EntityId}, entitySeq={EntitySeq}, broadcastCount={BroadcastCount}, subscribers={Subscribers}, exclude={Exclude}")]
    public static partial void DistributingBroadcasts(this ILogger logger, string entityId, long entitySeq, int broadcastCount, int subscribers, string exclude);

    [LoggerMessage(EventId = 2010, EventName = "SkippingExcludedSubscriber", Level = LogLevel.Trace, Message = "Skipping excluded subscriber: {PlayerId}")]
    public static partial void SkippingExcludedSubscriber(this ILogger logger, string playerId);

    [LoggerMessage(EventId = 2011, EventName = "SendingBroadcast", Level = LogLevel.Trace, Message = "Sending broadcast to {PlayerId}: entity={EntityId}, entitySeq={EntitySeq}, method={ServiceName}.{MethodName}")]
    public static partial void SendingBroadcast(this ILogger logger, string playerId, string entityId, long entitySeq, string serviceName, string methodName);

    [LoggerMessage(EventId = 2012, EventName = "ErrorBroadcasting", Level = LogLevel.Error, Message = "Error broadcasting to {PlayerId}")]
    public static partial void ErrorBroadcasting(this ILogger logger, Exception ex, string playerId);

    [LoggerMessage(EventId = 2013, EventName = "BroadcastSent", Level = LogLevel.Trace, Message = "Broadcast sent to {SentCount}/{TotalCount} subscribers (method={ServiceName}.{MethodName})")]
    public static partial void BroadcastSent(this ILogger logger, int sentCount, int totalCount, string serviceName, string methodName);

    // ── EntityGrain — Persistence ──────────────────────────────────

    [LoggerMessage(EventId = 3001, EventName = "EntityStateLoaded", Level = LogLevel.Debug, Message = "State loaded: {StateType}/{EntityId}, subscribers={SubscriberCount}, entitySeq={SequenceNumber}")]
    public static partial void EntityStateLoaded(this ILogger logger, string stateType, string entityId, int subscriberCount, long sequenceNumber);

    [LoggerMessage(EventId = 3002, EventName = "ExpiredSubscribersPruned", Level = LogLevel.Debug, Message = "Pruned {Count} expired subscriber(s) from {EntityId}")]
    public static partial void ExpiredSubscribersPruned(this ILogger logger, string entityId, int count);

    [LoggerMessage(EventId = 3003, EventName = "EntityStateInitialized", Level = LogLevel.Debug, Message = "State initialized: {StateType}/{EntityId}, version={Version}")]
    public static partial void EntityStateInitialized(this ILogger logger, string stateType, string entityId, int version);

    [LoggerMessage(EventId = 3004, EventName = "EntityStatePersisted", Level = LogLevel.Trace, Message = "State persisted: {EntityId}")]
    public static partial void EntityStatePersisted(this ILogger logger, string entityId);

    [LoggerMessage(EventId = 3005, EventName = "PersistenceDeferred", Level = LogLevel.Trace, Message = "Persistence deferred: {EntityId}, requests={Requests}, policy={Policy}")]
    public static partial void PersistenceDeferred(this ILogger logger, string entityId, int requests, string policy);

    [LoggerMessage(EventId = 3006, EventName = "PersistenceForced", Level = LogLevel.Trace, Message = "Persistence forced: {EntityId}, requests={Requests}")]
    public static partial void PersistenceForced(this ILogger logger, string entityId, int requests);

    [LoggerMessage(EventId = 3007, EventName = "SubscriberRestored", Level = LogLevel.Trace, Message = "Restored subscriber {PlayerId} for {EntityId}")]
    public static partial void SubscriberRestored(this ILogger logger, string entityId, string playerId);

    // ── MetaConnectionHandler ────────────────────────────────────────

    [LoggerMessage(EventId = 4001, EventName = "HandlerSessionConnect", Level = LogLevel.Debug, Message = "SessionConnect: {PlayerId} (success={Success}, isNew={IsNew})")]
    public static partial void HandlerSessionConnect(this ILogger logger, string playerId, bool success, bool isNew);

    // 0.24.0+ Signature-handshake tracing. Three transition points covering every path
    // a client takes through phase-1 / phase-2. Read alongside ClientDispatcher's mirror
    // log entries on the client side — together they trace the complete negotiation flow.
    [LoggerMessage(EventId = 4002, EventName = "HandshakeAnnotationFromCache", Level = LogLevel.Debug, Message = "Handshake[{PlayerId}] phase-1 HIT: clientHash=0x{ClientHash:X16} known, serverHash=0x{ServerHash:X16}, annotation: {StatusesCount} methods ({RejectedCount} rejected, {ForcePatchCount} force-patch)")]
    public static partial void HandshakeAnnotationFromCache(this ILogger logger, string playerId, ulong clientHash, ulong serverHash, int statusesCount, int rejectedCount, int forcePatchCount);

    [LoggerMessage(EventId = 4003, EventName = "HandshakeNeedsRegistration", Level = LogLevel.Debug, Message = "Handshake[{PlayerId}] phase-1 MISS: clientHash=0x{ClientHash:X16} unknown, serverHash=0x{ServerHash:X16}; needs phase-2 registration")]
    public static partial void HandshakeNeedsRegistration(this ILogger logger, string playerId, ulong clientHash, ulong serverHash);

    [LoggerMessage(EventId = 4004, EventName = "HandshakeSignatureRegistered", Level = LogLevel.Debug, Message = "Handshake[{PlayerId}] phase-2 REGISTER: clientHash=0x{ClientHash:X16} ({KnownMethodsCount} methods) -> serverHash=0x{ServerHash:X16}, annotation: {RejectedCount} rejected, {ForcePatchCount} force-patch, {ServerOnlyCount} server-only (client doesn't know)")]
    public static partial void HandshakeSignatureRegistered(this ILogger logger, string playerId, ulong clientHash, int knownMethodsCount, ulong serverHash, int rejectedCount, int forcePatchCount, int serverOnlyCount);

    [LoggerMessage(EventId = 4005, EventName = "HandshakeSessionRecoveryPrompted", Level = LogLevel.Debug, Message = "Session not connected on connection {ConnectionId} ({Operation}) — pushed RequireSessionReconnect, expecting client to re-handshake")]
    public static partial void HandshakeSessionRecoveryPrompted(this ILogger logger, string connectionId, string operation);

    [LoggerMessage(EventId = 4006, EventName = "ClientTimeClamped", Level = LogLevel.Warning, Message = "Client time for {PlayerId} on methodId={MethodId} was off by {DeltaSeconds:F1}s (window {WindowSeconds:F0}s) — overridden with the silo clock")]
    public static partial void ClientTimeClamped(this ILogger logger, string playerId, ushort methodId, double deltaSeconds, double windowSeconds);

    [LoggerMessage(EventId = 4007, EventName = "HandlerSessionConnectError", Level = LogLevel.Error, Message = "SessionConnect error")]
    public static partial void HandlerSessionConnectError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4008, EventName = "HandlerSubscribe", Level = LogLevel.Debug, Message = "Subscribe: {PlayerId} -> {EntityId} (success={Success})")]
    public static partial void HandlerSubscribe(this ILogger logger, string playerId, string entityId, bool success);

    [LoggerMessage(EventId = 4009, EventName = "HandlerSubscribeError", Level = LogLevel.Error, Message = "Subscribe error")]
    public static partial void HandlerSubscribeError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4010, EventName = "HandlerUnsubscribe", Level = LogLevel.Debug, Message = "Unsubscribe: {PlayerId} -> {EntityId}")]
    public static partial void HandlerUnsubscribe(this ILogger logger, string playerId, string entityId);

    [LoggerMessage(EventId = 4011, EventName = "HandlerUnsubscribeError", Level = LogLevel.Error, Message = "Unsubscribe error")]
    public static partial void HandlerUnsubscribeError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4012, EventName = "HandlerRpcCallError", Level = LogLevel.Error, Message = "RpcCall error")]
    public static partial void HandlerRpcCallError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4013, EventName = "RpcSessionSuperseded", Level = LogLevel.Warning, Message = "Rejected RPC reqId={RequestId}: session {SessionId} was superseded (current={CurrentSessionId})")]
    public static partial void RpcSessionSuperseded(this ILogger logger, long requestId, Guid sessionId, Guid currentSessionId);

    [LoggerMessage(EventId = 4014, EventName = "HandlerAcknowledgeError", Level = LogLevel.Error, Message = "AcknowledgeSequence error")]
    public static partial void HandlerAcknowledgeError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4015, EventName = "HandlerDisconnected", Level = LogLevel.Debug, Message = "Disconnected: {ConnectionId} (player={PlayerId})")]
    public static partial void HandlerDisconnected(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(EventId = 4016, EventName = "ErrorClearingObserver", Level = LogLevel.Error, Message = "Error clearing observer")]
    public static partial void ErrorClearingObserver(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4017, EventName = "ObserverRenewalFailed", Level = LogLevel.Error, Message = "Observer renewal failed")]
    public static partial void ObserverRenewalFailed(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4018, EventName = "ObserverOnBatch", Level = LogLevel.Trace, Message = "OnBatch: player={PlayerId}, seq={Seq}, count={Count}")]
    public static partial void ObserverOnBatch(this ILogger logger, string? playerId, long seq, int count);

    // ── MetaProviderBase ─────────────────────────────────────────────

    [LoggerMessage(EventId = 2501, EventName = "ProviderCallError", Level = LogLevel.Error, Message = "Error in methodId={MethodId}")]
    public static partial void ProviderCallError(this ILogger logger, Exception ex, ushort methodId);

    [LoggerMessage(EventId = 2502, EventName = "ProviderEventError", Level = LogLevel.Error, Message = "Event error in {SubscriberInterface}.{MethodName}")]
    public static partial void ProviderEventError(this ILogger logger, Exception ex, string subscriberInterface, string methodName);

    [LoggerMessage(EventId = 2503, EventName = "CallTapError", Level = LogLevel.Warning, Message = "Call tap failed in {Stage} for methodId={MethodId}; the call itself is unaffected")]
    public static partial void CallTapError(this ILogger logger, Exception ex, string stage, ushort methodId);
}
