using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Utilities;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Session manager grain implementation.
    /// Manages player sessions, entity subscriptions, and message routing.
    ///
    /// Broadcast ordering guarantees:
    /// - Per-entity ordering via EntitySequenceNumber gap detection
    /// - RPC broadcast bundling (all broadcasts queued during active RPC)
    /// - Deferred RPC responses when entity sequence gap is detected
    /// </summary>
    public class SessionManagerGrain : Grain, ISessionManager, ISessionManagerReference
    {
        private readonly IMetaSerializer _serializer;
        private readonly ILogger<SessionManagerGrain> _logger;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly SessionManagerOptions _options;

        // Session state
        private readonly string _playerId;
        private Guid _currentSessionId;
        private readonly List<Guid> _previousSessionIds = [];
        private long _sequenceNumber;

        // Pending responses for reconnection replay — one SequenceNumber per response
        private readonly List<SessionResponse> _pendingPackets = [];
        private const int MaxPendingPackets = 1000;

        // Subscriptions
        private readonly Dictionary<string, EntitySubscriptionInfo> _subscribedEntities = new();

        // Observer (Hub connection) - managed with expiration-based cleanup (Orleans built-in)
        private readonly ObserverManager<ISessionObserver> _observerManager;
        private IDisposable? _observerCleanupTimer;
        private static readonly TimeSpan ObserverCleanupInterval = TimeSpan.FromSeconds(30);

        // Per-entity ordering state
        private readonly Dictionary<string, EntityOrderingState> _entityStates = new();

        // Active RPC state: when true, broadcasts are queued instead of sent
        private bool _inActiveRpc;
        private readonly List<QueuedBroadcast> _rpcBroadcastQueue = [];

        // Deferred responses: RPC results waiting for entity sequence gaps to fill
        private readonly List<DeferredResponse> _deferredResponses = [];

        // Saved subscriptions for reconnect after transport disconnect
        private List<SavedSubscription>? _savedSubscriptions;

        // ── RPC ordering / stash ─────────────────────────────────────────
        // When SessionManagerOptions.EnforceRpcOrder is true, RPC calls that arrive with
        // RequestId > NextExpected are parked in this ring buffer until the gap is filled.
        // The buffer also tracks LastDispatchedRequestId so the gate can classify each
        // incoming call as Stale / NextExpected / OutOfOrder in O(1).
        private readonly RpcOrderingBuffer<StashedRpcCall> _orderingBuffer;

        // Stall diagnostics — no timer, checked lazily on next request or grain deactivation
        private long _stallStartTicks;

        private sealed class StashedRpcCall
        {
            public long RequestId { get; set; }
            public string EntityId { get; set; } = "";
            public RpcCall Call { get; set; } = new();
            public long LastAcknowledgedSequence { get; set; }
        }

        #region Nested Types

        private class EntitySubscriptionInfo
        {
            public EntitySubscriptionInfo(string entityId, string stateTypeName, IEntityGrainBase grainRef)
            {
                EntityId = entityId;
                StateTypeName = stateTypeName;
                GrainRef = grainRef;
            }
            public string EntityId { get; set; }
            public string StateTypeName { get; set; }
            public IEntityGrainBase GrainRef { get; set; }
        }

        private class EntityOrderingState
        {
            public long KnownEntitySequence { get; set; }
            public SortedDictionary<long, EntityBroadcast> HeldBroadcasts { get; } = new();
        }

        private class QueuedBroadcast
        {
            public string EntityId { get; set; } = "";
            public long EntitySequenceNumber { get; set; }
            public EntityBroadcast Broadcast { get; set; } = new();
        }

        private class DeferredResponse
        {
            public long RequestId { get; set; }
            public string EntityId { get; set; } = "";
            public long RequiredEntitySeq { get; set; }
            public EntityCallResult Result { get; set; } = new();
            public RpcCall OriginalCall { get; set; } = new();
        }

        private class SavedSubscription
        {
            public string EntityId { get; set; } = "";
            public string StateTypeName { get; set; } = "";
            public long LastKnownEntitySequence { get; set; }
        }

        #endregion

        public SessionManagerGrain(
            IMetaSerializer serializer,
            ILogger<SessionManagerGrain> logger,
            IEntityGrainResolver entityGrainResolver,
            IOptions<SessionManagerOptions>? options = null)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _options = options?.Value ?? new SessionManagerOptions();
            _observerManager = new ObserverManager<ISessionObserver>(TimeSpan.FromMinutes(2), _logger);
            _playerId = this.GetPrimaryKeyString();
            _orderingBuffer = new RpcOrderingBuffer<StashedRpcCall>(Math.Max(1, _options.StashCapacity));
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.SessionActivated(_playerId);

            // Set up timer for cleaning up expired observers
            _observerCleanupTimer = this.RegisterGrainTimer(
                CleanupExpiredObservers,
                ObserverCleanupInterval,
                ObserverCleanupInterval);

            return base.OnActivateAsync(cancellationToken);
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _logger.SessionDeactivating(_playerId);

            if (_options.EnforceRpcOrder && !_orderingBuffer.IsEmpty)
            {
                var elapsed = _stallStartTicks > 0
                    ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _stallStartTicks)
                    : TimeSpan.Zero;
                _logger.LogWarning(
                    "[Session] Grain deactivating with {Count} stashed RPCs (missing=#{Missing}, stalled={Elapsed}) for player {Player}",
                    _orderingBuffer.Count, _orderingBuffer.NextExpectedRequestId, elapsed, _playerId);
            }

            _observerCleanupTimer?.Dispose();

            // During silo shutdown all grains deactivate concurrently —
            // entity grains are already gone, no point calling them
            if (reason.ReasonCode != DeactivationReasonCode.ShuttingDown)
            {
                foreach (var sub in _subscribedEntities.Values)
                {
                    try
                    {
                        await sub.GrainRef.UnsubscribeAsync(_playerId);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorUnsubscribingOnDeactivate(ex, sub.EntityId);
                    }
                }
            }

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        #region Session Management

        public async Task<SessionConnectionResult> ConnectAsync(Guid sessionId, long lastAcknowledgedSequence)
        {
            // New session for this player
            if (_currentSessionId == Guid.Empty)
            {
                _currentSessionId = sessionId;
                _sequenceNumber = 0;
                _logger.NewSessionStarted(_playerId, sessionId);

                return new SessionConnectionResult
                {
                    Success = true,
                    SessionId = sessionId,
                    IsNewSession = true,
                    MissedPackets = new List<SessionResponse>(),
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };
            }

            // Same session - resume
            if (sessionId == _currentSessionId)
            {
                var missedPackets = _pendingPackets
                    .Where(p => p.SequenceNumber > lastAcknowledgedSequence)
                    .ToList();

                // Re-stamp missed packets with current server time for clock sync
                var now = DateTime.UtcNow.Ticks;
                foreach (var packet in missedPackets)
                    packet.ServerTimeTicks = now;

                // Re-subscribe to saved entities from transport disconnect
                List<ResubscribedEntity>? resubscribedEntities = null;
                if (_savedSubscriptions is { Count: > 0 })
                {
                    resubscribedEntities = await ResubscribeSavedEntitiesAsync();
                    _savedSubscriptions = null;
                }

                _logger.SessionResumed(_playerId, missedPackets.Count);

                return new SessionConnectionResult
                {
                    Success = true,
                    SessionId = sessionId,
                    IsNewSession = false,
                    MissedPackets = missedPackets,
                    ServerTimeTicks = now,
                    ResubscribedEntities = resubscribedEntities
                };
            }

            // Old session (superseded)
            if (_previousSessionIds.Contains(sessionId))
            {
                _logger.OldSessionRejected(sessionId, _playerId);

                return new SessionConnectionResult
                {
                    Success = false,
                    Error = "Session superseded by a newer session",
                    SessionId = _currentSessionId,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };
            }

            // New session - supersede current
            // Notify old observers BEFORE clearing
            await _observerManager.Notify(o => o.OnSessionTerminated("Session superseded by new connection"));
            _observerManager.Clear();

            // Unsubscribe from all entities — new client starts with clean slate
            foreach (var sub in _subscribedEntities.Values)
            {
                try { await sub.GrainRef.UnsubscribeAsync(_playerId); }
                catch { /* best effort */ }
            }
            _subscribedEntities.Clear();

            _previousSessionIds.Add(_currentSessionId);
            _currentSessionId = sessionId;
            _pendingPackets.Clear();
            _entityStates.Clear();
            _deferredResponses.Clear();
            _sequenceNumber = 0;
            ResetRpcOrderingState();

            _logger.SessionSuperseded(sessionId, _playerId);

            return new SessionConnectionResult
            {
                Success = true,
                SessionId = sessionId,
                IsNewSession = true,
                MissedPackets = new List<SessionResponse>(),
                ServerTimeTicks = DateTime.UtcNow.Ticks
            };
        }

        public Task SetObserverAsync(ISessionObserver observer)
        {
            _observerManager.Subscribe(observer, observer);
            _logger.ObserverSubscribed(_playerId, _observerManager.Count);
            return Task.CompletedTask;
        }

        public Task ClearObserverAsync()
        {
            _observerManager.Clear();
            _logger.ObserversCleared(_playerId);
            return Task.CompletedTask;
        }

        public async Task GracefulDisconnectAsync()
        {
            _observerManager.Clear();

            // Unsubscribe from all entities
            foreach (var sub in _subscribedEntities.Values)
            {
                try
                {
                    await sub.GrainRef.UnsubscribeAsync(_playerId);
                }
                catch (Exception ex)
                {
                    _logger.ErrorUnsubscribingOnDeactivate(ex, sub.EntityId);
                }
            }

            // Full cleanup — client explicitly left, cannot resume
            _subscribedEntities.Clear();
            _entityStates.Clear();
            _deferredResponses.Clear();
            _pendingPackets.Clear();
            _savedSubscriptions = null;
            _currentSessionId = Guid.Empty;
            _sequenceNumber = 0;
            ResetRpcOrderingState();

            _logger.GracefulDisconnect(_playerId);
        }

        public async Task OnTransportDisconnectedAsync()
        {
            _observerManager.Clear();

            // Save subscriptions for potential reconnect
            _savedSubscriptions = _subscribedEntities.Values.Select(sub => new SavedSubscription
            {
                EntityId = sub.EntityId,
                StateTypeName = sub.StateTypeName,
                LastKnownEntitySequence = _entityStates.TryGetValue(sub.EntityId, out var es)
                    ? es.KnownEntitySequence : 0
            }).ToList();

            // Unsubscribe from entity grains to stop receiving broadcasts
            foreach (var sub in _subscribedEntities.Values)
            {
                try
                {
                    await sub.GrainRef.UnsubscribeAsync(_playerId);
                }
                catch (Exception ex)
                {
                    _logger.ErrorUnsubscribingOnDeactivate(ex, sub.EntityId);
                }
            }

            // Clear active state but keep session + pending packets + saved subscriptions
            _subscribedEntities.Clear();
            _entityStates.Clear();
            _deferredResponses.Clear();

            _logger.TransportDisconnected(_playerId, _savedSubscriptions.Count);
        }

        #endregion

        #region Entity Subscriptions

        public async Task<EntitySubscriptionResult> SubscribeToEntityAsync(string entityId, string stateTypeName, string? clientVersion = null)
        {
            if (_subscribedEntities.TryGetValue(entityId, out var existing))
            {
                // Already subscribed - return current state (pass clientVersion for per-client config resolution)
                var snapshot = await existing.GrainRef.SubscribeAsync(_playerId, this.AsReference<ISessionManagerReference>(), clientVersion);

                // Update entity ordering state
                _entityStates[entityId] = new EntityOrderingState
                {
                    KnownEntitySequence = snapshot.CurrentSequenceNumber
                };

                return new EntitySubscriptionResult
                {
                    Success = true,
                    StateBytes = snapshot.StateBytes,
                    EntitySequenceNumber = snapshot.CurrentSequenceNumber,
                    OptimisticRandomBytes = snapshot.OptimisticRandomBytes,
                    NamedRandomsBytes = snapshot.NamedRandomsBytes,
                    ConfigVersion = snapshot.ConfigVersion
                };
            }

            try
            {
                // Get entity grain by type
                var entityGrain = GetEntityGrain(entityId, stateTypeName);
                if (entityGrain == null)
                {
                    return new EntitySubscriptionResult
                    {
                        Success = false,
                        Error = $"Could not resolve entity grain for type {stateTypeName}"
                    };
                }

                // Subscribe to entity (pass clientVersion for per-client config resolution)
                var snapshot = await entityGrain.SubscribeAsync(_playerId, this.AsReference<ISessionManagerReference>(), clientVersion);

                _subscribedEntities[entityId] = new EntitySubscriptionInfo(
                    entityId: entityId,
                    stateTypeName: stateTypeName,
                    grainRef: entityGrain
                );

                // Initialize entity ordering state
                _entityStates[entityId] = new EntityOrderingState
                {
                    KnownEntitySequence = snapshot.CurrentSequenceNumber
                };

                _logger.SubscribedToEntity(_playerId, entityId, snapshot.CurrentSequenceNumber);

                return new EntitySubscriptionResult
                {
                    Success = true,
                    StateBytes = snapshot.StateBytes,
                    EntitySequenceNumber = snapshot.CurrentSequenceNumber,
                    OptimisticRandomBytes = snapshot.OptimisticRandomBytes,
                    NamedRandomsBytes = snapshot.NamedRandomsBytes,
                    ConfigVersion = snapshot.ConfigVersion
                };
            }
            catch (SharedMeta.Core.IncompatibleFeatureException incompat)
            {
                // 0.22.0+: surface structured rejection so the client can throw a typed
                // IncompatibleFeatureException rather than a generic Exception(string).
                _logger.ErrorSubscribing(incompat, entityId);
                return new EntitySubscriptionResult
                {
                    Success = false,
                    Error = incompat.Message,
                    FeatureRequirement = incompat.Requirement,
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorSubscribing(ex, entityId);
                return new EntitySubscriptionResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        public async Task UnsubscribeFromEntityAsync(string entityId)
        {
            var playerId = this.GetPrimaryKeyString();

            if (_subscribedEntities.Remove(entityId, out var sub))
            {
                try
                {
                    await sub.GrainRef.UnsubscribeAsync(playerId);
                }
                catch (Exception ex)
                {
                    _logger.ErrorUnsubscribing(ex, entityId);
                }

                // Clean up ordering state
                _entityStates.Remove(entityId);
                _deferredResponses.RemoveAll(d => d.EntityId == entityId);

                _logger.UnsubscribedFromEntity(playerId, entityId);
            }
        }

        #endregion

        #region RPC Handling

        public async Task<SessionResponse> SendToEntityAsync(string entityId, long requestId, RpcCall call, long lastAcknowledgedSequence, Guid sessionId)
        {
            // Reject calls from superseded sessions
            if (sessionId != _currentSessionId)
            {
                _logger.RpcSessionSuperseded(requestId, sessionId, _currentSessionId);
                return SessionResponse.ForError("Session superseded");
            }

            if (lastAcknowledgedSequence > 0)
                CleanupPendingPacketsBySequence(lastAcknowledgedSequence);

            // Idempotency: return cached response for duplicate requests (reconnection)
            var cached = _pendingPackets.FirstOrDefault(p =>
                p.Operations.Any(o => o.RequestId == requestId && o.RequestId > 0));
            if (cached != null)
            {
                _logger.CachedResponseReturned(requestId);
                return cached;
            }

            // ── RPC reordering: stash out-of-order requests ──────────────
            if (_options.EnforceRpcOrder && requestId > 0)
            {
                // Lazy stall diagnostics: if there's an existing gap, push notification
                // on this request arrival instead of using a periodic timer.
                if (_stallStartTicks > 0 && !_orderingBuffer.IsEmpty)
                {
                    var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _stallStartTicks);
                    var stage = elapsed >= _options.HardStallNotifyTimeout
                        ? StallStage.TimeoutPending
                        : StallStage.Stalled;
                    _ = PushStallNotification(stage, elapsed);
                }

                var position = _orderingBuffer.Classify(requestId);
                if (position == RequestPosition.OutOfOrder)
                {
                    var stashedCall = new StashedRpcCall
                    {
                        RequestId = requestId,
                        EntityId = entityId,
                        Call = call,
                        LastAcknowledgedSequence = lastAcknowledgedSequence,
                    };
                    var stashResult = _orderingBuffer.TryStash(requestId, stashedCall);
                    switch (stashResult)
                    {
                        case StashResult.Overflow:
                            // No way to recover the intended ordering — terminate.
                            await TerminateSessionForStashOverflow(requestId);
                            return SessionResponse.ForError($"Request order stash overflow at requestId={requestId}");
                        case StashResult.Duplicate:
                            LogDuplicateStash(requestId);
                            break;
                        case StashResult.Stashed:
                            if (_stallStartTicks == 0)
                                _stallStartTicks = Environment.TickCount64;
                            break;
                    }

                    // Pure ack response — TCS on the client stays pending until the real
                    // result arrives later (bundled with the predecessor's response).
                    return new SessionResponse
                    {
                        SequenceNumber = 0,
                        Operations = new List<SessionOp>(),
                        ServerTimeTicks = DateTime.UtcNow.Ticks
                    };
                }
                if (position == RequestPosition.Stale)
                {
                    // Stale resend. Idempotency cache above should have caught the common
                    // case; this branch handles cases where the response was already
                    // evicted from _pendingPackets. Pass through and let the entity grain
                    // re-execute (it's idempotent for replay-safe operations).
                    _logger.LogDebug("[Session] Stale RPC requestId={ReqId}, lastDispatched={Last}",
                        requestId, _orderingBuffer.LastDispatchedRequestId);
                }
            }

            if (!_subscribedEntities.TryGetValue(entityId, out var sub) || sub.GrainRef == null)
                return SessionResponse.ForError($"Not subscribed to entity {entityId}");

            _logger.SendToEntity(_playerId, entityId, requestId, call.ServiceName, call.MethodName);

            // Accumulator across the in-order call AND any consecutive stashed calls
            // we drain after it.
            var allOps = new List<SessionOp>();
            bool anyDeferred = false;

            try
            {
                anyDeferred |= await ExecuteOneCallAsync(entityId, requestId, call, sub.GrainRef, allOps);
                if (_options.EnforceRpcOrder && requestId > 0)
                {
                    // The slot for this RequestId in the ring was conceptually the head;
                    // since the call came in-order it was never populated, but the head
                    // still needs to advance so subsequent stash lookups address the
                    // correct slot.
                    _orderingBuffer.MarkDispatchedInOrder(requestId);
                }

                // Drain consecutive stash entries inline. Each successful dequeue advances
                // the buffer's LastDispatchedRequestId, so the loop walks forward until
                // either the stash is empty or the next slot is empty (gap remains).
                if (_options.EnforceRpcOrder)
                {
                    while (_orderingBuffer.TryDequeueNext(out _, out var stashed) && stashed != null)
                    {
                        if (!_subscribedEntities.TryGetValue(stashed.EntityId, out var stashedSub) || stashedSub.GrainRef == null)
                        {
                            // Stashed call's entity is gone — surface as an error op.
                            allOps.Add(new SessionOp
                            {
                                EntityId = stashed.EntityId,
                                RequestId = stashed.RequestId,
                                Error = $"Not subscribed to entity {stashed.EntityId}",
                                MainOperation = new OperationResult { Call = stashed.Call, Response = new RpcResponse() }
                            });
                            continue;
                        }

                        anyDeferred |= await ExecuteOneCallAsync(stashed.EntityId, stashed.RequestId, stashed.Call, stashedSub.GrainRef, allOps);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorCallingEntity(ex, entityId);
                return SessionResponse.ForError(ex.Message);
            }
            finally
            {
                _inActiveRpc = false;
                _rpcBroadcastQueue.Clear();
            }

            if (_options.EnforceRpcOrder)
            {
                if (_orderingBuffer.IsEmpty)
                {
                    _stallStartTicks = 0;
                }
            }

            // Build a single SessionResponse from all accumulated ops.
            return FinalizeAccumulatedResponse(allOps);
        }

        /// <summary>
        /// Execute one RPC call against an entity and append its result + preceding broadcasts
        /// to <paramref name="allOps"/>. Returns true if the result was deferred (entity sequence
        /// gap detected) and added to <see cref="_deferredResponses"/> instead of allOps.
        /// </summary>
        private async Task<bool> ExecuteOneCallAsync(
            string entityId, long requestId, RpcCall call, IEntityGrainBase grainRef, List<SessionOp> allOps)
        {
            _inActiveRpc = true;
            _rpcBroadcastQueue.Clear();
            EntityCallResult result;
            try
            {
                result = await grainRef.HandleCallAsync(call);
            }
            finally
            {
                _inActiveRpc = false;
            }

            // CrossOptimistic: advance KnownEntitySequence for cross-entity calls so their
            // own broadcasts (queued in _rpcBroadcastQueue) are silently skipped.
            if (call.IsCrossOptimistic && result.CrossEntityCalls is { Count: > 0 })
            {
                foreach (var crossCall in result.CrossEntityCalls)
                {
                    var crossState = GetOrCreateEntityState(crossCall.EntityId);
                    crossState.KnownEntitySequence = Math.Max(
                        crossState.KnownEntitySequence, crossCall.EntitySequenceNumber);
                }
            }

            var state = GetOrCreateEntityState(entityId);
            _logger.RpcReturned(_playerId, entityId, result.EntitySequenceNumber, state.KnownEntitySequence, _rpcBroadcastQueue.Count, result.HasError);

            // Collect preceding broadcasts queued during RPC + drain held
            var preceding = CollectPrecedingOps(state, entityId);
            if (preceding.Count > 0)
                allOps.AddRange(preceding);

            if (state.KnownEntitySequence >= result.EntitySequenceNumber - 1)
            {
                // Fast path — append result op directly
                state.KnownEntitySequence = Math.Max(state.KnownEntitySequence, result.EntitySequenceNumber);
                allOps.Add(CallResultToSessionOp(entityId, requestId, result));
                DrainHeldBroadcasts(state, entityId);
                MergeOutgoingBatch(allOps);
                return false;
            }

            // Deferred path — register for later resolution, append only preceding ops
            _logger.DeferredPath(_playerId, requestId, result.EntitySequenceNumber, state.KnownEntitySequence, allOps.Count);
            _deferredResponses.Add(new DeferredResponse
            {
                RequestId = requestId,
                EntityId = entityId,
                RequiredEntitySeq = result.EntitySequenceNumber,
                Result = result,
                OriginalCall = call
            });
            MergeOutgoingBatch(allOps);
            return true;
        }

        /// <summary>
        /// Wrap accumulated ops into one SessionResponse with one fresh sequence number,
        /// and add to the replay cache. Returns an empty (seq=0) response if there are no ops.
        /// </summary>
        private SessionResponse FinalizeAccumulatedResponse(List<SessionOp> allOps)
        {
            if (allOps.Count == 0)
            {
                return new SessionResponse
                {
                    SequenceNumber = 0,
                    Operations = new List<SessionOp>(),
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };
            }

            var sessionSeq = ++_sequenceNumber;
            var response = new SessionResponse
            {
                SequenceNumber = sessionSeq,
                Operations = OrderOps(allOps),
                ServerTimeTicks = DateTime.UtcNow.Ticks
            };

            _pendingPackets.Add(response);
            CleanupPendingPacketsByCount();

            _logger.FastPath(_playerId, sessionSeq, allOps.Count, 0);
            return response;
        }

        /// <summary>
        /// Collect preceding operations from RPC broadcast queue and drain held broadcasts.
        /// No sequence numbers assigned yet — just collect SessionOps.
        /// </summary>
        private List<SessionOp> CollectPrecedingOps(EntityOrderingState state, string entityId)
        {
            var ops = new List<SessionOp>();
            var precedingOps = BuildPrecedingOperations();
            if (precedingOps != null)
                ops.AddRange(precedingOps);

            DrainAndResolve(state, entityId);

            return ops;
        }

        // ── RPC ordering stash helpers ───────────────────────────────────

        /// <summary>
        /// Reset all RPC reordering state — invoked when the session is reset (supersede,
        /// graceful disconnect, hard terminate). The next caller starts fresh from
        /// <c>RequestId = 1</c>.
        /// </summary>
        private void ResetRpcOrderingState()
        {
            _orderingBuffer.Reset();
            _stallStartTicks = 0;
        }

        private void LogDuplicateStash(long requestId)
        {
            switch (_options.DuplicateStashLogLevel)
            {
                case StashDuplicateLogLevel.None: return;
                case StashDuplicateLogLevel.Debug:
                    _logger.LogDebug("[Session] Duplicate stashed RPC requestId={ReqId} player={Player}", requestId, _playerId);
                    break;
                case StashDuplicateLogLevel.Information:
                    _logger.LogInformation("[Session] Duplicate stashed RPC requestId={ReqId} player={Player}", requestId, _playerId);
                    break;
                case StashDuplicateLogLevel.Warning:
                    _logger.LogWarning("[Session] Duplicate stashed RPC requestId={ReqId} player={Player}", requestId, _playerId);
                    break;
            }
        }

        // ── Stall diagnostics (lazy, no timer) ────────────────────────────

        private Task PushStallNotification(StallStage stage, TimeSpan elapsed)
        {
            var notification = new StallNotification
            {
                Stage = stage,
                OldestMissingRequestId = _orderingBuffer.NextExpectedRequestId,
                StashedCount = _orderingBuffer.Count,
                ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            };
            var response = new SessionResponse
            {
                SequenceNumber = 0,
                Operations = new List<SessionOp>(),
                ServerTimeTicks = DateTime.UtcNow.Ticks,
                StallNotification = notification,
            };
            return _observerManager.Notify(o => o.OnBatch(response));
        }


        private async Task TerminateSessionForStashOverflow(long requestId)
        {
            await TerminateSessionWithReason(
                $"Request order stash overflow at requestId={requestId} (stashed={_orderingBuffer.Count}, capacity={_orderingBuffer.Capacity})");
        }

        private async Task TerminateSessionForStall(TimeSpan elapsed)
        {
            await TerminateSessionWithReason(
                $"RPC ordering stall exceeded {elapsed.TotalSeconds:F0}s (oldestMissing={_orderingBuffer.NextExpectedRequestId}, stashed={_orderingBuffer.Count})");
        }

        private async Task TerminateSessionWithReason(string reason)
        {
            try
            {
                await _observerManager.Notify(o => o.OnSessionTerminated(reason));
            }
            catch { /* best effort */ }
            _observerManager.Clear();

            // Drop session state — client must reconnect (will get IsNewSession=true).
            ResetRpcOrderingState();
            _previousSessionIds.Add(_currentSessionId);
            _currentSessionId = Guid.Empty;
            _pendingPackets.Clear();
            _entityStates.Clear();
            _deferredResponses.Clear();
            _sequenceNumber = 0;

            _logger.LogWarning("[Session] Session terminated for player {Player}: {Reason}", _playerId, reason);
        }

        /// <summary>
        /// Merge any buffered operations from DrainHeldBroadcasts/ResolveDeferredResponses into ops list.
        /// </summary>
        private void MergeOutgoingBatch(List<SessionOp> ops)
        {
            if (_outgoingBatch.Count > 0)
            {
                ops.AddRange(_outgoingBatch);
                _outgoingBatch.Clear();
            }
        }

        /// <summary>
        /// Order operations by entity for deterministic delivery.
        /// </summary>
        private static List<SessionOp> OrderOps(List<SessionOp> ops)
        {
            if (ops.Count <= 1) return ops;
            return ops.OrderBy(o => o.EntityId).ToList();
        }

        #endregion

        #region Broadcast Ordering

        // Batch buffer: collects operations during a single method call, flushed at the end
        private readonly List<SessionOp> _outgoingBatch = new();

        public async Task ReceiveBroadcastAsync(string entityId, EntityBroadcast broadcast, long entitySequenceNumber)
        {
            var state = GetOrCreateEntityState(entityId);

            _logger.BroadcastReceived(_playerId, entityId, entitySequenceNumber, state.KnownEntitySequence, _inActiveRpc, _observerManager.Count, broadcast.ServiceName, broadcast.MethodName);

            if (_inActiveRpc)
            {
                // During active RPC: queue for bundling (no session seq yet)
                _rpcBroadcastQueue.Add(new QueuedBroadcast
                {
                    EntityId = entityId,
                    EntitySequenceNumber = entitySequenceNumber,
                    Broadcast = broadcast
                });
                _logger.BroadcastQueuedForRpc(_playerId, entitySequenceNumber);
                return;
            }

            var expectedNext = state.KnownEntitySequence + 1;

            if (entitySequenceNumber == expectedNext)
            {
                // In order — buffer for batch delivery
                state.KnownEntitySequence = entitySequenceNumber;
                BufferBroadcast(entityId, broadcast);

                DrainAndResolve(state, entityId);
            }
            else if (entitySequenceNumber > expectedNext)
            {
                // Out of order — hold (no session seq yet)
                _logger.BroadcastOutOfOrder(entityId, entitySequenceNumber, expectedNext);
                state.HeldBroadcasts[entitySequenceNumber] = broadcast;
            }
            else
            {
                _logger.BroadcastDuplicate(_playerId, entitySequenceNumber, expectedNext);
            }

            // Flush all buffered operations as one batch to the client
            await FlushOutgoingBatch();
        }

        /// <summary>
        /// Buffer a broadcast as a SessionOp for batch delivery. No sequence assigned yet.
        /// </summary>
        private void BufferBroadcast(string entityId, EntityBroadcast broadcast)
        {
            _logger.BufferBroadcast(_playerId, entityId);

            // Add to outgoing batch
            _outgoingBatch.Add(BroadcastToSessionOp(entityId, broadcast));
        }

        /// <summary>
        /// Flush all buffered operations as a single SessionResponse with ONE sequence number.
        /// </summary>
        private async Task FlushOutgoingBatch()
        {
            if (_outgoingBatch.Count == 0) return;

            var sessionSeq = ++_sequenceNumber;
            var response = new SessionResponse
            {
                SequenceNumber = sessionSeq,
                Operations = _outgoingBatch.OrderBy(o => o.EntityId).ToList(),
                ServerTimeTicks = DateTime.UtcNow.Ticks
            };

            _logger.FlushBatch(_playerId, sessionSeq, response.Operations.Count);

            // Store for reconnection replay
            _pendingPackets.Add(response);
            CleanupPendingPacketsByCount();

            await _observerManager.Notify(o => o.OnBatch(response));
            _outgoingBatch.Clear();
        }

        private void DrainHeldBroadcasts(EntityOrderingState state, string entityId)
        {
            while (state.HeldBroadcasts.Count > 0)
            {
                var first = state.HeldBroadcasts.First();
                if (first.Key != state.KnownEntitySequence + 1) break;

                state.HeldBroadcasts.Remove(first.Key);
                state.KnownEntitySequence = first.Key;
                BufferBroadcast(entityId, first.Value);
            }
        }

        /// <summary>
        /// Build preceding operations from broadcasts queued during an active RPC.
        /// Applies per-entity ordering: only includes in-order broadcasts, holds out-of-order ones.
        /// No sequence numbers assigned — just returns SessionOps.
        /// </summary>
        private List<SessionOp>? BuildPrecedingOperations()
        {
            if (_rpcBroadcastQueue.Count == 0) return null;

            // Group by entity and sort by entity sequence within each group
            var sorted = _rpcBroadcastQueue
                .OrderBy(b => b.EntityId)
                .ThenBy(b => b.EntitySequenceNumber)
                .ToList();

            var result = new List<SessionOp>();
            foreach (var b in sorted)
            {
                var state = GetOrCreateEntityState(b.EntityId);
                var expectedNext = state.KnownEntitySequence + 1;

                if (b.EntitySequenceNumber == expectedNext)
                {
                    // In order — include in preceding operations
                    state.KnownEntitySequence = b.EntitySequenceNumber;
                    result.Add(BroadcastToSessionOp(b.EntityId, b.Broadcast));
                }
                else if (b.EntitySequenceNumber > expectedNext)
                {
                    // Out of order — hold for later delivery
                    state.HeldBroadcasts[b.EntitySequenceNumber] = b.Broadcast;
                }
                // else: duplicate/old, ignore
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Cascading drain + resolve: resolving deferred responses advances KnownEntitySequence,
        /// which may allow held broadcasts to drain, which may satisfy more deferred responses.
        /// Repeats until no more progress is made.
        /// </summary>
        private void DrainAndResolve(EntityOrderingState state, string entityId)
        {
            while (true)
            {
                var knownBefore = state.KnownEntitySequence;
                DrainHeldBroadcasts(state, entityId);
                ResolveDeferredResponses(entityId);
                if (state.KnownEntitySequence == knownBefore)
                    break;
            }
        }

        /// <summary>
        /// Check if any deferred RPC responses can now be resolved (gap filled).
        /// Resolved responses are buffered to _outgoingBatch.
        /// </summary>
        private void ResolveDeferredResponses(string entityId)
        {
            var state = GetOrCreateEntityState(entityId);

            for (int i = _deferredResponses.Count - 1; i >= 0; i--)
            {
                var deferred = _deferredResponses[i];
                if (deferred.EntityId != entityId) continue;
                if (state.KnownEntitySequence < deferred.RequiredEntitySeq - 1) continue;

                // Gap filled!
                _deferredResponses.RemoveAt(i);
                state.KnownEntitySequence = Math.Max(state.KnownEntitySequence, deferred.RequiredEntitySeq);

                var deferredOp = CallResultToSessionOp(entityId, deferred.RequestId, deferred.Result);

                _logger.DeferredResolved(deferred.RequestId);

                // Buffer to outgoing batch (will be flushed with other operations)
                _outgoingBatch.Add(deferredOp);
            }
        }

        /// <summary>
        /// Convert an EntityBroadcast to a SessionOp (no sequence number).
        /// </summary>
        private SessionOp BroadcastToSessionOp(string entityId, EntityBroadcast broadcast)
        {
            return new SessionOp
            {
                EntityId = entityId,
                RequestId = 0,
                MainOperation = new OperationResult
                {
                    Call = new RpcCall
                    {
                        ServiceName = broadcast.ServiceName,
                        MethodName = broadcast.MethodName,
                        Payload = broadcast.Payload ?? Array.Empty<byte>(),
                        CallerId = broadcast.ExcludePlayerId,
                        ServerTimeTicks = broadcast.ServerTimeTicks
                    },
                    Response = new RpcResponse { ReplayPayload = broadcast.ReplayPayload, RandomScrollDelta = broadcast.RandomScrollDelta, NamedRandomScrollDeltas = broadcast.NamedRandomScrollDeltas, PatchBytes = broadcast.PatchBytes, StateBytes = broadcast.StateBytes }
                },
                TriggerOperations = broadcast.TriggerBroadcasts?.Select(t => new OperationResult
                {
                    Call = new RpcCall
                    {
                        ServiceName = t.ServiceName,
                        MethodName = t.MethodName,
                        Payload = t.Payload ?? Array.Empty<byte>(),
                        ServerTimeTicks = t.ServerTimeTicks
                    },
                    Response = new RpcResponse { ReplayPayload = t.ReplayPayload, RandomScrollDelta = t.RandomScrollDelta, NamedRandomScrollDeltas = t.NamedRandomScrollDeltas, PatchBytes = t.PatchBytes, StateBytes = t.StateBytes }
                }).ToList()
            };
        }

        /// <summary>
        /// Convert an EntityCallResult to a SessionOp (no sequence number).
        /// </summary>
        private SessionOp CallResultToSessionOp(string entityId, long requestId, EntityCallResult result)
        {
            return new SessionOp
            {
                EntityId = entityId,
                RequestId = requestId,
                MainOperation = result.MainOperation,
                TriggerOperations = result.TriggerOperations,
                Error = result.Error,
                CrossEntityOperations = result.CrossEntityCalls?.Select(c => new CrossEntityOperationInfo
                {
                    EntityId = c.EntityId,
                    ServiceName = c.ServiceName,
                    MethodName = c.MethodName,
                    ResultBytes = c.ResultBytes
                }).ToList()
            };
        }

        private EntityOrderingState GetOrCreateEntityState(string entityId)
        {
            if (!_entityStates.TryGetValue(entityId, out var state))
            {
                state = new EntityOrderingState();
                _entityStates[entityId] = state;
            }
            return state;
        }

        #endregion

        #region Entity Notifications

        public Task NotifyEntityDeactivatingAsync(string entityId)
        {
            if (_subscribedEntities.Remove(entityId))
            {
                _entityStates.Remove(entityId);
                _deferredResponses.RemoveAll(d => d.EntityId == entityId);

                _logger.EntityDeactivated(entityId);

                // Notify observers ([OneWay] — fire-and-forget)
                _observerManager.Notify(o => o.OnEntityDeactivating(entityId));
            }
            return Task.CompletedTask;
        }

        #endregion

        #region Query

        public async Task<QueryCallResponse> QueryEntityAsync(string entityId, string serviceName, RpcCall call)
        {
            // Resolve entity grain directly — no subscription required
            var entityGrain = _entityGrainResolver.GetEntityGrainByService(
                GrainFactory, serviceName, entityId);

            if (entityGrain == null)
                return new QueryCallResponse { Error = $"Cannot resolve entity for service '{serviceName}'" };

            return await entityGrain.HandleQueryAsync(call);
        }

        public Task SignalEntityAsync(string entityId, string serviceName, RpcCall call)
        {
            // Resolve entity grain directly — signal goes through the same grain resolver as queries,
            // bypasses subscription and the session sequence. The grain method is [OneWay] so Orleans
            // does not even send an ACK back to this SessionManager; the task completes immediately
            // after the message is handed to the grain runtime.
            var entityGrain = _entityGrainResolver.GetEntityGrainByService(
                GrainFactory, serviceName, entityId);

            if (entityGrain == null)
            {
                _logger.LogWarning(
                    "[SessionManager] Signal on unresolved entity '{ServiceName}/{EntityId}' — dropped",
                    serviceName, entityId);
                return Task.CompletedTask;
            }

            return entityGrain.HandleSignalAsync(call);
        }

        #endregion

        #region Acknowledgment

        public Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            CleanupPendingPacketsBySequence(sequenceNumber);
            return Task.CompletedTask;
        }

        private void CleanupPendingPacketsBySequence(long acknowledgedSequence)
        {
            var countBefore = _pendingPackets.Count;
            _pendingPackets.RemoveAll(p => p.SequenceNumber <= acknowledgedSequence);
            var removed = countBefore - _pendingPackets.Count;

            if (removed > 0)
            {
                _logger.PacketsCleanedBySeq(removed, acknowledgedSequence);
            }
        }

        #endregion

        #region Helpers

        public Task<Guid> GetCurrentSessionIdAsync()
        {
            return Task.FromResult(_currentSessionId);
        }

        /// <summary>
        /// Re-subscribe to entities saved during transport disconnect.
        /// Returns state for all entities (client always gets a fresh state on reconnect).
        /// </summary>
        private async Task<List<ResubscribedEntity>> ResubscribeSavedEntitiesAsync()
        {
            var result = new List<ResubscribedEntity>();

            foreach (var saved in _savedSubscriptions!)
            {
                try
                {
                    var entityGrain = GetEntityGrain(saved.EntityId, saved.StateTypeName);
                    if (entityGrain == null) continue;

                    // Reconnect path: clientVersion not available here; entity uses its default ConfigVersion.
                    var snapshot = await entityGrain.SubscribeAsync(_playerId, this.AsReference<ISessionManagerReference>());

                    _subscribedEntities[saved.EntityId] = new EntitySubscriptionInfo(
                        entityId: saved.EntityId,
                        stateTypeName: saved.StateTypeName,
                        grainRef: entityGrain);

                    _entityStates[saved.EntityId] = new EntityOrderingState
                    {
                        KnownEntitySequence = snapshot.CurrentSequenceNumber
                    };

                    result.Add(new ResubscribedEntity
                    {
                        EntityId = saved.EntityId,
                        StateBytes = snapshot.StateBytes,
                        EntitySequenceNumber = snapshot.CurrentSequenceNumber,
                        OptimisticRandomBytes = snapshot.OptimisticRandomBytes,
                        NamedRandomsBytes = snapshot.NamedRandomsBytes,
                        ConfigVersion = snapshot.ConfigVersion
                    });

                    _logger.SubscribedToEntity(_playerId, saved.EntityId, snapshot.CurrentSequenceNumber);
                }
                catch (Exception ex)
                {
                    _logger.ErrorSubscribing(ex, saved.EntityId);
                }
            }

            return result;
        }

        private IEntityGrainBase? GetEntityGrain(string entityId, string stateTypeName)
        {
            return _entityGrainResolver.GetEntityGrain(GrainFactory, stateTypeName, entityId);
        }

        private Task CleanupExpiredObservers()
        {
            _observerManager.ClearExpired();
            return Task.CompletedTask;
        }

        private void CleanupPendingPacketsByCount()
        {
            if (_pendingPackets.Count > MaxPendingPackets)
            {
                var toRemove = _pendingPackets.Count - MaxPendingPackets / 2;
                _pendingPackets.RemoveRange(0, toRemove);
                _logger.PacketsCleanedByCount(toRemove);
            }
        }

        #endregion
    }
}
