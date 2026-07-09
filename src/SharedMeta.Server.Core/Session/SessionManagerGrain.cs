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

        // Pending responses for reconnection replay — one SequenceNumber per response.
        // The persisted list IS the working list (no duplicate field, no defensive copy on persist).
        // Initialized to non-null once in OnActivateAsync; the property is a thin field read after JIT.
        private List<SessionResponse> _pendingPackets => _persistentState.State.PendingPackets!;
        private const int MaxPendingPackets = 1000;

        // Cached delegate for ObserverManager.Notify in FlushOutgoingBatch. Constructed once per
        // grain activation from the instance method group `NotifyOnBatch` — that allocates one
        // Func<ISessionObserver, Task> at construction time and reuses it for every flush.
        // Replaces an in-place lambda `o => o.OnBatch(response)` which used to allocate both the
        // Func and a compiler-generated DisplayClass closure on every flush (4.2 MB/s combined in
        // Run #9 alloc profile).
        // The shared `response` instance is parked in _pendingNotifyResponse before each Notify
        // call and read inside NotifyOnBatch. Safe because Orleans grains are single-threaded:
        // ObserverManager.Notify invokes the delegate synchronously per observer before the first
        // await (it gathers all Tasks then WhenAll's them), so no other grain message can clobber
        // the field mid-iteration.
        private SessionResponse? _pendingNotifyResponse;
        private readonly Func<ISessionObserver, Task> _onBatchInvoker;

        // Subscriptions. Keyed by (entityId, stateTypeName) — NOT entityId alone. The server
        // addresses entities by (state type, entityId) (an Orleans grain identity), so the same
        // entityId string is a valid, independent identity across different state types (e.g.
        // Inventory:playerId, Profile:playerId, Wallet:playerId under the UserOwned convention).
        // Outer key is entityId, inner key is stateTypeName. Use the TryGetSubscription/
        // SetSubscription/RemoveSubscription/AllSubscriptions helpers instead of touching directly.
        private readonly Dictionary<string, Dictionary<string, EntitySubscriptionInfo>> _subscribedEntities = new();

        // Observer (Hub connection) - managed with expiration-based cleanup (Orleans built-in)
        private readonly ObserverManager<ISessionObserver> _observerManager;
        private IDisposable? _observerCleanupTimer;
        private static readonly TimeSpan ObserverCleanupInterval = TimeSpan.FromSeconds(30);

        // Per-entity ordering state. Keyed by (entityId, stateTypeName) — same rationale as
        // _subscribedEntities above; a conflated ordering state for two state types sharing an
        // entityId could let one grain's advancing sequence spuriously satisfy the other's
        // gap-detection check, bypassing held-broadcast ordering guarantees.
        private readonly Dictionary<string, Dictionary<string, EntityOrderingState>> _entityStates = new();

        // Sentinel slot-reservation marker for HeldBroadcasts. Used by the CrossOptimistic path
        // to claim the target entity's seq slot for a cross-call whose effect is already inlined
        // in the outer call's replay payload — so when the intermediate (third-party) broadcasts
        // arrive and the drain reaches this slot, the slot advances KnownEntitySequence without
        // emitting anything to the client. Reference-equality-checked.
        private static readonly EntityBroadcast CrossCallSlotMarker = new();

        // Active RPC state: when true, broadcasts are queued instead of sent
        private bool _inActiveRpc;
        private readonly List<QueuedBroadcast> _rpcBroadcastQueue = [];

        // Deferred responses: RPC results waiting for entity sequence gaps to fill
        private readonly List<DeferredResponse> _deferredResponses = [];

        // 0.24.0+ Subscriptions are NOT held server-side across transport drops or grain
        // deactivation — the client owns the subscription list (single source of truth) and
        // claims it via SessionConnectRequest.ClaimedSubscriptions on Resume. SessionManager
        // verifies each claim with the entity grain via EntityGrain.ReclaimSubscriptionAsync.

        // Per-player ClientCapabilities used to live here as a pushed cache. Replaced by
        // the signature-hash flow: MetaConnectionHandler passes the hash on Subscribe and
        // EntityGrain resolves caps locally via IClientSignatureRegistry — single source
        // of truth, no stale push-cache.

        // ── RPC ordering / stash ─────────────────────────────────────────
        // When SessionManagerOptions.EnforceRpcOrder is true, RPC calls that arrive with
        // RequestId > NextExpected are parked in this ring buffer until the gap is filled.
        // The buffer also tracks LastDispatchedRequestId so the gate can classify each
        // incoming call as Stale / NextExpected / OutOfOrder in O(1).
        private readonly RpcOrderingBuffer<StashedRpcCall> _orderingBuffer;

        // Stall diagnostics — no timer, checked lazily on next request or grain deactivation
        private long _stallStartTicks;

        private struct StashedRpcCall
        {
            public long RequestId;
            public string EntityId;
            public string StateTypeName;
            public RpcCall Call;
            public long LastAcknowledgedSequence;
        }

        #region Nested Types

        private struct EntitySubscriptionInfo
        {
            public EntitySubscriptionInfo(string entityId, string stateTypeName, IEntityGrainBase grainRef,
                string? clientVersion = null, ulong clientSignatureHash = 0)
            {
                EntityId = entityId;
                StateTypeName = stateTypeName;
                GrainRef = grainRef;
                ClientVersion = clientVersion;
                ClientSignatureHash = clientSignatureHash;
            }
            public string EntityId;
            public string StateTypeName;
            public IEntityGrainBase GrainRef;
            // Captured on the original SubscribeToEntityAsync so transport-disconnect
            // resubscribe can replay the per-client config branch + signature mapping. Without
            // these the entity falls back to "no client version" and IMetaConfigProvider's
            // ResolveForClient throws "clientAppVersion is required".
            public string? ClientVersion;
            public ulong ClientSignatureHash;
        }

        private class EntityOrderingState
        {
            public long KnownEntitySequence { get; set; }
            public SortedDictionary<long, EntityBroadcast> HeldBroadcasts { get; } = new();
        }

        private struct QueuedBroadcast
        {
            public string EntityId;
            public string StateTypeName;
            public long EntitySequenceNumber;
            public EntityBroadcast Broadcast;
        }

        private struct DeferredResponse
        {
            public long RequestId;
            public string EntityId;
            public string StateTypeName;
            public long RequiredEntitySeq;
            public EntityCallResult Result;
            public RpcCall OriginalCall;
        }

        #endregion

        // --- Composite-key (entityId, stateTypeName) subscription/ordering-state helpers ----
        // Mirrors the client-side MetaServiceResolver pattern. Orleans grains are single-
        // threaded, so no locking is needed here (unlike the client's _lock-guarded helpers).

        private bool TryGetSubscription(string entityId, string stateTypeName, out EntitySubscriptionInfo info)
        {
            info = default;
            return _subscribedEntities.TryGetValue(entityId, out var byType) && byType.TryGetValue(stateTypeName, out info);
        }

        private void SetSubscription(string entityId, string stateTypeName, EntitySubscriptionInfo info)
        {
            if (!_subscribedEntities.TryGetValue(entityId, out var byType))
            {
                byType = new Dictionary<string, EntitySubscriptionInfo>();
                _subscribedEntities[entityId] = byType;
            }
            byType[stateTypeName] = info;
        }

        private bool RemoveSubscription(string entityId, string stateTypeName, out EntitySubscriptionInfo info)
        {
            info = default;
            if (_subscribedEntities.TryGetValue(entityId, out var byType) && byType.TryGetValue(stateTypeName, out info))
            {
                byType.Remove(stateTypeName);
                if (byType.Count == 0) _subscribedEntities.Remove(entityId);
                return true;
            }
            return false;
        }

        private IEnumerable<EntitySubscriptionInfo> AllSubscriptions()
        {
            foreach (var byType in _subscribedEntities.Values)
                foreach (var info in byType.Values)
                    yield return info;
        }

        private int SubscriptionCount()
        {
            int count = 0;
            foreach (var byType in _subscribedEntities.Values) count += byType.Count;
            return count;
        }

        // 0.24.0+ Persistent state — see SessionManagerGrainState. Written on
        // graceful disconnect + OnDeactivateAsync; read on OnActivateAsync. Hot path doesn't write.
        private readonly IPersistentState<SessionManagerGrainState> _persistentState;

        public SessionManagerGrain(
            IMetaSerializer serializer,
            ILogger<SessionManagerGrain> logger,
            IEntityGrainResolver entityGrainResolver,
            [PersistentState("sessionMgr", "Default")] IPersistentState<SessionManagerGrainState> persistentState,
            IOptions<SessionManagerOptions>? options = null)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _persistentState = persistentState ?? throw new ArgumentNullException(nameof(persistentState));
            _options = options?.Value ?? new SessionManagerOptions();
            _observerManager = new ObserverManager<ISessionObserver>(TimeSpan.FromMinutes(2), _logger);
            _playerId = this.GetPrimaryKeyString();
            _orderingBuffer = new RpcOrderingBuffer<StashedRpcCall>(Math.Max(1, _options.StashCapacity));
            _onBatchInvoker = NotifyOnBatch;  // one delegate alloc per grain lifetime
        }

        // Delegate target for ObserverManager.Notify — reads _pendingNotifyResponse set by
        // FlushOutgoingBatch immediately before the Notify call. See _onBatchInvoker doc.
        private Task NotifyOnBatch(ISessionObserver observer) => observer.OnBatch(_pendingNotifyResponse!);

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.SessionActivated(_playerId);
            _persistentState.State.PendingPackets ??= new List<SessionResponse>();

            // 0.24.0+ Restore persisted session state (if any). Observer registration is
            // ALWAYS transient — _observerManager stays empty until the first SessionConnect
            // from MetaConnectionHandler re-establishes it via SetObserverAsync.
            if (_persistentState.State.Populated)
            {
                var s = _persistentState.State;
                _currentSessionId = s.CurrentSessionId;
                _sequenceNumber = s.SequenceNumber;

                // 0.24.0+ Subscriptions are no longer persisted — the client owns the
                // subscription list and claims it via SessionConnectRequest.ClaimedSubscriptions
                // on Resume. State.SubscribedEntities is read but ignored here (legacy field
                // tolerated for cross-version state on disk; will be empty going forward).

                // 0.24.0+ Restore RPC ordering baseline so the next Resume's RequestId stream
                // doesn't look like a gap from zero. Critical: without this, client-side
                // re-sends of pending RPCs get stashed and stall the ordering buffer forever.
                if (s.LastDispatchedRequestId > 0)
                {
                    _orderingBuffer.AdvanceLastDispatched(s.LastDispatchedRequestId);
                }

                _logger.LogInformation(
                    "[SessionManager:{Player}] Reactivated with persisted state: sessionId={Session}, seq={Seq}, pendingPackets={PktCount}, lastDispatched={LastDispatched}",
                    _playerId, _currentSessionId, _sequenceNumber, _pendingPackets.Count, _orderingBuffer.LastDispatchedRequestId);
            }

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
                foreach (var sub in AllSubscriptions())
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

            // 0.24.0+ Persist enough state to make Resume work after reactivation. Pending
            // packets are saved before being released; the saved copy holds byte[]-backed
            // SessionResponse instances (Orleans serializer deep-copies on write), so the
            // pool-payload release below doesn't invalidate the persisted version.
            await PersistSessionStateAsync(reason);

            // Return any still-pending pool slots to the registry. Without this they'd live
            // until the registry is itself disposed (process exit) since SessionManagerGrain
            // is per-player and may be activated/deactivated many times during a silo's life.
            ReleaseAndClearPendingPackets();

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        /// <summary>
        /// 0.24.0+ Snapshot current session state into persistent storage. Called on
        /// graceful disconnect (which then clears state) and on grain deactivation
        /// (which keeps it). On <see cref="DeactivationReasonCode.ShuttingDown"/> we
        /// best-effort still persist — if the silo is shutting down the state may
        /// already be flushed by Orleans, but a write attempt costs little and survives
        /// rolling-restart scenarios where the silo comes back.
        /// </summary>
        private async Task PersistSessionStateAsync(DeactivationReason? deactivationReason)
        {
            if (_currentSessionId == Guid.Empty)
            {
                // No active session — only persist if we had something before (to clear).
                if (_persistentState.State.Populated)
                {
                    _persistentState.State.Populated = false;
                    _persistentState.State.CurrentSessionId = Guid.Empty;
                    _pendingPackets.Clear();   // shared ref with State.PendingPackets
                    _persistentState.State.LastDispatchedRequestId = 0;
                    try { await _persistentState.WriteStateAsync(); } catch { /* best effort */ }
                }
                return;
            }

            // 0.24.0+ Subscriptions are NOT persisted — the client owns the subscription list
            // and re-claims via ClaimedSubscriptions on Resume. Only the bits that the client
            // cannot reconstruct on its own are kept: sessionId (recovery handshake gate),
            // sequenceNumber (broadcast ordering), pendingPackets tail (missed-broadcast replay),
            // LastDispatchedRequestId (RPC ordering baseline).
            _persistentState.State.Populated = true;
            _persistentState.State.CurrentSessionId = _currentSessionId;
            _persistentState.State.SequenceNumber = _sequenceNumber;
            // PendingPackets — _pendingPackets aliases State.PendingPackets; no assignment needed.
            _persistentState.State.LastDispatchedRequestId = _orderingBuffer.LastDispatchedRequestId;

            try { await _persistentState.WriteStateAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionManager:{Player}] Failed to persist session state on deactivate", _playerId);
            }
        }

        #region Session Management

        public async Task<SessionConnectionResult> ConnectAsync(Guid sessionId, long lastAcknowledgedSequence, SessionConnectMode mode, long lastCompletedRequestId, IReadOnlyList<SubscriptionClaim>? claimedSubscriptions, string? clientVersion, ulong clientSignatureHash)
        {
            // 0.24.0+ Resume path: sessionId must match the currently-known one. Otherwise the
            // server-side state is gone (grain reactivated without persistent state, or this is
            // a stale id from another device / older app launch) — return SessionUnknown so the
            // client surfaces the loss to its IMetaSessionRecoveryHandler instead of silently
            // landing on a fresh session.
            if (mode == SessionConnectMode.Resume)
            {
                if (_currentSessionId != Guid.Empty && sessionId == _currentSessionId)
                {
                    // ── Happy resume ─────────────────────────────────────────────
                    // Gap-fix: advance the RPC ordering baseline past anything the client has
                    // already completed. Without this, if the server lost cached responses (eviction
                    // or crash before persistence flush) but the client did receive them, the
                    // client's next-new RequestId would be classified OutOfOrder against a stale
                    // _lastDispatchedRequestId and stashed forever.
                    var baselineBefore = _orderingBuffer.LastDispatchedRequestId;
                    if (lastCompletedRequestId > 0)
                        _orderingBuffer.AdvanceLastDispatched(lastCompletedRequestId);
                    _logger.LogInformation(
                        "[SessionManager:{Player}] Resume baseline check: persisted={Before}, clientReported={Reported}, after={After}",
                        _playerId, baselineBefore, lastCompletedRequestId, _orderingBuffer.LastDispatchedRequestId);

                    var missedPackets = _pendingPackets
                        .Where(p => p.SequenceNumber > lastAcknowledgedSequence)
                        .ToList();

                    var now = DateTime.UtcNow.Ticks;
                    foreach (var packet in missedPackets)
                        packet.ServerTimeTicks = now;

                    // 0.24.0+ Client-driven subscription reclaim. For each claimed entity, ask
                    // the entity grain whether it still has us as subscriber AND our seq numbers
                    // agree (→ Continued, no state shipped) or whether we need a fresh snapshot
                    // (→ Refreshed). On any Failed → abort whole Resume so the client falls back
                    // to fresh-session via the recovery handler (cannot safely continue with a
                    // partial subscription set).
                    List<SubscriptionResult>? subscriptionResults = null;
                    if (claimedSubscriptions is { Count: > 0 })
                    {
                        subscriptionResults = await ReclaimClaimedSubscriptionsAsync(
                            claimedSubscriptions, clientVersion, clientSignatureHash);

                        var failed = subscriptionResults.Where(r => r.Status == SubscriptionStatus.Failed).ToList();
                        if (failed.Count > 0)
                        {
                            _logger.LogWarning(
                                "[SessionManager:{Player}] Resume aborted — {FailedCount} of {Total} subscriptions failed reclaim",
                                _playerId, failed.Count, subscriptionResults.Count);
                            return new SessionConnectionResult
                            {
                                Success = false,
                                Error = $"Subscription reclaim failed for {failed.Count} entity(ies)",
                                SessionId = _currentSessionId,
                                ServerTimeTicks = now,
                                Subscriptions = subscriptionResults,
                                FailureReason = SessionConnectFailureReason.SubscriptionReclaimFailed,
                            };
                        }
                    }

                    _logger.SessionResumed(_playerId, missedPackets.Count);

                    return new SessionConnectionResult
                    {
                        Success = true,
                        SessionId = sessionId,
                        IsNewSession = false,
                        MissedPackets = missedPackets,
                        ServerTimeTicks = now,
                        Subscriptions = subscriptionResults,
                        FailureReason = SessionConnectFailureReason.None,
                    };
                }

                // Resume requested but server doesn't recognize this sessionId. Return structured
                // failure so the handler maps to SessionConnectFailureReason.SessionUnknown on the
                // wire. Client fires its recovery handler and (typical default) retries StartNew.
                _logger.OldSessionRejected(sessionId, _playerId);
                return new SessionConnectionResult
                {
                    Success = false,
                    Error = "Session unknown to server (resume failed)",
                    SessionId = _currentSessionId,
                    ServerTimeTicks = DateTime.UtcNow.Ticks,
                    FailureReason = SessionConnectFailureReason.SessionUnknown,
                };
            }

            // ── Mode = StartNew ─────────────────────────────────────────────────
            // Always allocate a clean session. Supersedes the current one if any.
            if (_currentSessionId != Guid.Empty && sessionId != _currentSessionId)
            {
                // Notify old observers BEFORE clearing
                await _observerManager.Notify(o => o.OnSessionTerminated("Session superseded by new connection"));
                _observerManager.Clear();

                foreach (var sub in AllSubscriptions())
                {
                    try { await sub.GrainRef.UnsubscribeAsync(_playerId); }
                    catch { /* best effort */ }
                }
                _subscribedEntities.Clear();

                _previousSessionIds.Add(_currentSessionId);
                ReleaseAndClearPendingPackets();
                ReleaseAndClearEntityStates();
                ReleaseAndClearDeferredResponses();
                ReleaseAndClearRpcBroadcastQueue();
                ResetRpcOrderingState();

                _logger.SessionSuperseded(sessionId, _playerId);
            }
            else
            {
                _logger.NewSessionStarted(_playerId, sessionId);
            }

            _currentSessionId = sessionId;
            _sequenceNumber = 0;

            return new SessionConnectionResult
            {
                Success = true,
                SessionId = sessionId,
                IsNewSession = true,
                MissedPackets = new List<SessionResponse>(),
                ServerTimeTicks = DateTime.UtcNow.Ticks,
                FailureReason = SessionConnectFailureReason.None,
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

        public async Task GracefulDisconnectAsync(Guid sessionId)
        {
            // 0.24.0+ Guard against stale graceful disconnect from a superseded session.
            // Typical race: old SignalR connection's GracefulDisconnect arrives at the grain
            // after a fresh session has bound here. Without this check, the old call would
            // wipe the live session's state.
            if (sessionId != Guid.Empty && sessionId != _currentSessionId)
            {
                _logger.LogDebug(
                    "[SessionManager:{PlayerId}] Ignoring graceful disconnect for stale sessionId={Stale} (current={Current})",
                    _playerId, sessionId, _currentSessionId);
                return;
            }

            _observerManager.Clear();

            // Unsubscribe from all entities
            foreach (var sub in AllSubscriptions())
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
            ReleaseAndClearEntityStates();
            ReleaseAndClearDeferredResponses();
            ReleaseAndClearRpcBroadcastQueue();
            ReleaseAndClearPendingPackets();
            _currentSessionId = Guid.Empty;
            _sequenceNumber = 0;
            ResetRpcOrderingState();

            // 0.24.0+ Clear persistent state too — graceful disconnect explicitly closes
            // the session; client cannot resume it, so leaving CurrentSessionId on disk
            // would create misleading "we know this session" responses on reactivation.
            if (_persistentState.State.Populated)
            {
                _persistentState.State.Populated = false;
                _persistentState.State.CurrentSessionId = Guid.Empty;
                _persistentState.State.PendingPackets = null;
                _persistentState.State.LastDispatchedRequestId = 0;
                try { await _persistentState.WriteStateAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SessionManager:{Player}] Failed to clear persistent state on graceful disconnect", _playerId);
                }
            }

            _logger.GracefulDisconnect(_playerId);
        }

        public async Task OnTransportDisconnectedAsync()
        {
            _observerManager.Clear();

            // 0.24.0+ Subscriptions live on the client now — no server-side stash. We just
            // unsubscribe from entity grains to stop broadcasts flowing into a dead session.
            // On Resume the client re-claims via ClaimedSubscriptions; entity grains see this
            // as a fresh subscriber and return Refreshed (which is correct — there may be a
            // gap during the disconnect window). The Continued cheap-path only kicks in after
            // silo-shutdown reactivation, where OnDeactivateAsync(reason=ShuttingDown) skips
            // the unsubscribe loop and state.Subscribers survives.
            var subscribers = AllSubscriptions().ToList();
            foreach (var sub in subscribers)
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

            _subscribedEntities.Clear();
            ReleaseAndClearEntityStates();
            ReleaseAndClearDeferredResponses();
            ReleaseAndClearRpcBroadcastQueue();

            _logger.TransportDisconnected(_playerId, subscribers.Count);
        }

        #endregion

        #region Entity Subscriptions

        public async Task<EntitySubscriptionResult> SubscribeToEntityAsync(string entityId, string stateTypeName, string? clientVersion = null, ulong clientSignatureHash = 0)
        {
            // Must match on (entityId, stateTypeName) — matching on entityId alone here was a
            // confirmed bug: a second Subscribe under a different state type sharing this
            // entityId (e.g. Wallet after Inventory, both keyed by playerId) would silently take
            // this "already subscribed" fast path and return the FIRST state type's snapshot for
            // the second subscribe call, with no error.
            if (TryGetSubscription(entityId, stateTypeName, out var existing))
            {
                // Already subscribed - return current state (pass clientVersion for per-client config resolution)
                var snapshot = await existing.GrainRef.SubscribeAsync(_playerId, this.AsReference<ISessionManagerReference>(), clientVersion, clientSignatureHash);

                // Update entity ordering state
                SetEntityState(entityId, stateTypeName, new EntityOrderingState
                {
                    KnownEntitySequence = snapshot.CurrentSequenceNumber
                });

                return new EntitySubscriptionResult
                {
                    Success = true,
                    StateBytes = snapshot.StateBytes,
                    EntitySequenceNumber = snapshot.CurrentSequenceNumber,
                    OptimisticRandomBytes = snapshot.OptimisticRandomBytes,
                    NamedRandomsBytes = snapshot.NamedRandomsBytes,
                    ConfigVersions = snapshot.ConfigVersions
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

                // Pass the negotiated signature hash; EntityGrain resolves caps locally via
                // IClientSignatureRegistry. Zero hash = no negotiation, no caps.
                // The per-entity capability overlay (snapshot.AugmentedCapabilities) is
                // forwarded to the client unchanged. Broadcast tailoring lives on EntityGrain.
                var snapshot = await entityGrain.SubscribeAsync(_playerId, this.AsReference<ISessionManagerReference>(), clientVersion, clientSignatureHash);

                SetSubscription(entityId, stateTypeName, new EntitySubscriptionInfo(
                    entityId: entityId,
                    stateTypeName: stateTypeName,
                    grainRef: entityGrain,
                    clientVersion: clientVersion,
                    clientSignatureHash: clientSignatureHash
                ));

                // Initialize entity ordering state
                SetEntityState(entityId, stateTypeName, new EntityOrderingState
                {
                    KnownEntitySequence = snapshot.CurrentSequenceNumber
                });

                _logger.SubscribedToEntity(_playerId, entityId, snapshot.CurrentSequenceNumber);

                return new EntitySubscriptionResult
                {
                    Success = true,
                    StateBytes = snapshot.StateBytes,
                    EntitySequenceNumber = snapshot.CurrentSequenceNumber,
                    OptimisticRandomBytes = snapshot.OptimisticRandomBytes,
                    NamedRandomsBytes = snapshot.NamedRandomsBytes,
                    ConfigVersions = snapshot.ConfigVersions,
                    AugmentedCapabilities = snapshot.AugmentedCapabilities,
                };
            }
            catch (SharedMeta.Core.IncompatibleFeatureException incompat)
            {
                // Structured rejection so the client throws a typed exception rather than
                // a generic Exception(string).
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

        public async Task UnsubscribeFromEntityAsync(string entityId, string stateTypeName)
        {
            var playerId = this.GetPrimaryKeyString();

            if (RemoveSubscription(entityId, stateTypeName, out var sub))
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
                RemoveEntityState(entityId, stateTypeName);
                _deferredResponses.RemoveAll(d => d.EntityId == entityId && d.StateTypeName == stateTypeName);

                _logger.UnsubscribedFromEntity(playerId, entityId);
            }
        }

        #endregion

        #region RPC Handling

        public async Task<SessionResponse> SendToEntityAsync(string entityId, string stateTypeName, long requestId, RpcCall call, long lastAcknowledgedSequence, Guid sessionId)
        {
            // Reject calls from superseded sessions
            if (sessionId != _currentSessionId)
            {
                _logger.RpcSessionSuperseded(requestId, sessionId, _currentSessionId);
                return SessionResponse.ForError("Session superseded");
            }

            if (lastAcknowledgedSequence > 0)
                CleanupPendingPacketsBySequence(lastAcknowledgedSequence);

            // Idempotency: return cached response for duplicate requests (reconnection).
            // Inlined nested loops — LINQ Any/FirstOrDefault on List<T> boxes the struct
            // enumerator, and this is a hot path (per-RPC, ~3K calls/sec): per-stack alloc
            // profile traced 32 MB/s to Enumerator[SessionOp] here.
            if (requestId > 0)
            {
                SessionResponse? cached = null;
                for (int i = 0; i < _pendingPackets.Count; i++)
                {
                    var packet = _pendingPackets[i];
                    var ops = packet.Operations;
                    for (int j = 0; j < ops.Count; j++)
                    {
                        if (ops[j].RequestId == requestId)
                        {
                            cached = packet;
                            break;
                        }
                    }
                    if (cached != null) break;
                }
                if (cached != null)
                {
                    _logger.CachedResponseReturned(requestId);
                    return cached;
                }
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
                        StateTypeName = stateTypeName,
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
                    // Stale + cache-miss: the idempotency cache check above missed (response was
                    // evicted from _pendingPackets, or grain reactivated without it), but the
                    // ordering baseline says this RequestId was already dispatched. We must NOT
                    // re-execute (could violate at-most-once for non-idempotent operations and,
                    // worse, mutate state that the client may have ack'd on a prior response).
                    // Surface an error-SessionOp keyed to the RequestId so the client can resolve
                    // its pending TCS and stop the retry loop. The client treats this as
                    // operation-failed-by-server-loss, not network/retryable.
                    _logger.LogWarning(
                        "[Session:{Player}] Stale RPC requestId={ReqId} with cache miss, lastDispatched={Last} — returning error to unstick client",
                        _playerId, requestId, _orderingBuffer.LastDispatchedRequestId);
                    return new SessionResponse
                    {
                        SequenceNumber = 0,
                        Operations = new List<SessionOp>
                        {
                            new SessionOp
                            {
                                EntityId = entityId,
                                RequestId = requestId,
                                Error = "Request was processed by a previous server instance — result no longer available",
                                OpBytes = ReadOnlyMemory<byte>.Empty,
                            }
                        },
                        ServerTimeTicks = DateTime.UtcNow.Ticks,
                    };
                }
            }

            if (!TryGetSubscription(entityId, stateTypeName, out var sub) || sub.GrainRef == null)
                return SessionResponse.ForError($"Not subscribed to entity {entityId}");

            _logger.SendToEntity(_playerId, entityId, requestId, "", "");

            // Accumulator across the in-order call AND any consecutive stashed calls
            // we drain after it.
            var allOps = new List<SessionOp>();
            bool anyDeferred = false;

            try
            {
                anyDeferred |= await ExecuteOneCallAsync(entityId, stateTypeName, requestId, call, sub.GrainRef, allOps);
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
                    while (_orderingBuffer.TryDequeueNext(out _, out var stashed))
                    {
                        if (!TryGetSubscription(stashed.EntityId, stashed.StateTypeName, out var stashedSub) || stashedSub.GrainRef == null)
                        {
                            // Stashed call's entity is gone — surface as an error op.
                            // Error-only SessionOp: OpBytes empty, Error set. Client sees error,
                            // doesn't try to deserialize OpBytes.
                            allOps.Add(new SessionOp
                            {
                                EntityId = stashed.EntityId,
                                RequestId = stashed.RequestId,
                                Error = $"Not subscribed to entity {stashed.EntityId}",
                                OpBytes = ReadOnlyMemory<byte>.Empty,
                            });
                            continue;
                        }

                        anyDeferred |= await ExecuteOneCallAsync(stashed.EntityId, stashed.StateTypeName, stashed.RequestId, stashed.Call, stashedSub.GrainRef, allOps);
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
            string entityId, string stateTypeName, long requestId, RpcCall call, IEntityGrainBase grainRef, List<SessionOp> allOps)
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

            // CrossOptimistic: reserve the target's sequence slot for this cross-call. The
            // target's own broadcast no longer reaches us — EntityGrain.HandleCallFromEntityAsync
            // now excludes the originating caller from DistributeBroadcasts — but we still
            // need to account for the slot so concurrent third-party broadcasts (server-side
            // timers, background jobs writing the target) don't get dropped by a Math.Max-style
            // overshoot, and so the slot doesn't leave a permanent gap blocking later broadcasts.
            //
            //   * known == seq - 1 — no gap from concurrent writers: bump directly.
            //   * known <  seq - 1 — a third-party mutator incremented the target between our
            //     known and this cross-call. Reserve the cross-call slot with a NOP marker; when
            //     the missing intermediate broadcast(s) arrive, DrainHeldBroadcasts advances
            //     through our slot without emitting (effect is already inlined in this outer
            //     call's replay payload).
            if (call.IsCrossOptimistic && result.CrossEntityCalls is { Count: > 0 })
            {
                foreach (var crossCall in result.CrossEntityCalls)
                {
                    // Known residual limitation: CrossEntityOperationInfo carries MethodId but no
                    // state-type name, and there's no server-side MethodId->StateType registry
                    // (unlike the client's _configsByMethodId) to resolve one. Cross-call ordering
                    // bookkeeping is parked under a dedicated empty-string bucket, isolated from
                    // this player's own (entityId, stateTypeName)-keyed subscriptions above — but
                    // two cross-called sibling entities that happen to share an entityId with each
                    // other would still conflate here. Narrow edge case, not fixed this pass.
                    var crossState = GetOrCreateEntityState(crossCall.EntityId, "");
                    if (crossState.KnownEntitySequence == crossCall.EntitySequenceNumber - 1)
                    {
                        crossState.KnownEntitySequence = crossCall.EntitySequenceNumber;
                    }
                    else if (crossState.KnownEntitySequence < crossCall.EntitySequenceNumber - 1)
                    {
                        crossState.HeldBroadcasts[crossCall.EntitySequenceNumber] = CrossCallSlotMarker;
                    }
                    // crossState.KnownEntitySequence >= crossCall.EntitySequenceNumber: already
                    // applied (shouldn't happen for a fresh cross-call result, but harmless).
                }
            }

            var state = GetOrCreateEntityState(entityId, stateTypeName);
            _logger.RpcReturned(_playerId, entityId, result.EntitySequenceNumber, state.KnownEntitySequence, _rpcBroadcastQueue.Count, result.HasError);

            // Collect preceding broadcasts queued during RPC + drain held.
            // Append directly to allOps to avoid an intermediate List<SessionOp> per RPC.
            AppendPrecedingOps(state, entityId, stateTypeName, allOps);

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
                StateTypeName = stateTypeName,
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
        /// Append preceding operations (broadcasts queued during the RPC + drained held)
        /// directly onto <paramref name="allOps"/>. Replaces the prior pattern that allocated
        /// a fresh List per RPC just to merge into <paramref name="allOps"/>.
        /// </summary>
        private void AppendPrecedingOps(EntityOrderingState state, string entityId, string stateTypeName, List<SessionOp> allOps)
        {
            AppendBuiltPrecedingOps(allOps);
            DrainAndResolve(state, entityId, stateTypeName);
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
            ReleaseAndClearPendingPackets();
            ReleaseAndClearEntityStates();
            ReleaseAndClearDeferredResponses();
            ReleaseAndClearRpcBroadcastQueue();
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
        /// Order operations by entity for deterministic delivery. Sorted in place using a
        /// cached ordinal comparer — Enumerable.OrderBy + ToList allocates a sort buffer,
        /// an OrderedEnumerable, an EnumerableSorter, and walks via Comparer&lt;string&gt;.Default
        /// (CultureAwareComparer). All visible in the per-stack alloc profile.
        /// </summary>
        private static List<SessionOp> OrderOps(List<SessionOp> ops)
        {
            if (ops.Count > 1)
                ops.Sort(SessionOpByEntityIdOrdinal);
            return ops;
        }

        // Cached ordinal comparers — avoid the OrderBy + CultureAwareComparer allocation graph.
        private static readonly Comparison<SessionOp> SessionOpByEntityIdOrdinal =
            static (a, b) => string.CompareOrdinal(a.EntityId, b.EntityId);

        private static readonly Comparison<QueuedBroadcast> QueuedBroadcastByEntityThenSequence =
            static (a, b) =>
            {
                var c = string.CompareOrdinal(a.EntityId, b.EntityId);
                return c != 0 ? c : a.EntitySequenceNumber.CompareTo(b.EntitySequenceNumber);
            };

        #endregion

        #region Broadcast Ordering

        // Batch buffer: collects operations during a single method call, flushed at the end
        private readonly List<SessionOp> _outgoingBatch = new();

        public async Task ReceiveBroadcastAsync(string entityId, string stateTypeName, EntityBroadcast broadcast, long entitySequenceNumber)
        {
            var state = GetOrCreateEntityState(entityId, stateTypeName);

            if (_inActiveRpc)
            {
                // During active RPC: queue for bundling (no session seq yet)
                _rpcBroadcastQueue.Add(new QueuedBroadcast
                {
                    EntityId = entityId,
                    StateTypeName = stateTypeName,
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
                BufferBroadcast(entityId, broadcast, entitySequenceNumber);

                DrainAndResolve(state, entityId, stateTypeName);
            }
            else if (entitySequenceNumber > expectedNext)
            {
                // Out of order — hold (no session seq yet). Overwrites any prior entry at this
                // seq (rare resubscribe-replay overlap); the displaced broadcast is GC-managed.
                _logger.BroadcastOutOfOrder(entityId, entitySequenceNumber, expectedNext);
                state.HeldBroadcasts[entitySequenceNumber] = broadcast;
            }
            else
            {
                _logger.BroadcastDuplicate(_playerId, entitySequenceNumber, expectedNext);
                // Drop the duplicate — bytes are GC-managed.
            }

            // Flush all buffered operations as one batch to the client
            await FlushOutgoingBatch();
        }

        /// <summary>
        /// Buffer a broadcast as a SessionOp for batch delivery. No session-level sequence assigned
        /// yet (FlushOutgoingBatch stamps it); per-entity sequence flows through as-is so the
        /// client can track its highest seen seq per entity.
        /// </summary>
        private void BufferBroadcast(string entityId, EntityBroadcast broadcast, long entitySequenceNumber)
        {
            _logger.BufferBroadcast(_playerId, entityId);

            // Add to outgoing batch
            _outgoingBatch.Add(BroadcastToSessionOp(entityId, broadcast, entitySequenceNumber));
        }

        /// <summary>
        /// Flush all buffered operations as a single SessionResponse with ONE sequence number.
        /// </summary>
        private async Task FlushOutgoingBatch()
        {
            if (_outgoingBatch.Count == 0) return;

            var sessionSeq = ++_sequenceNumber;
            // Sort the in-flight batch in place, then snapshot into a new list for the response
            // (caller `_outgoingBatch` is cleared at the end of this method; the response keeps
            // its own list since it lives on in _pendingPackets).
            if (_outgoingBatch.Count > 1)
                _outgoingBatch.Sort(SessionOpByEntityIdOrdinal);
            var response = new SessionResponse
            {
                SequenceNumber = sessionSeq,
                Operations = new List<SessionOp>(_outgoingBatch),
                ServerTimeTicks = DateTime.UtcNow.Ticks
            };

            _logger.FlushBatch(_playerId, sessionSeq, response.Operations.Count);

            // Store for reconnection replay
            _pendingPackets.Add(response);
            CleanupPendingPacketsByCount();

            // Park response on the grain instance so the cached _onBatchInvoker delegate can read
            // it without allocating a closure. Cleared in finally so a thrown OnBatch doesn't leave
            // a dangling ref pinning the SessionResponse (and its byte[] payloads).
            _pendingNotifyResponse = response;
            try
            {
                await _observerManager.Notify(_onBatchInvoker);
            }
            finally
            {
                _pendingNotifyResponse = null;
            }
            _outgoingBatch.Clear();
        }

        private void DrainHeldBroadcasts(EntityOrderingState state, string entityId)
        {
            while (state.HeldBroadcasts.Count > 0)
            {
                // SortedDictionary.GetEnumerator() returns a struct (no alloc). .First()
                // extension boxes via IEnumerable<KVP>.GetEnumerator — heap alloc per drain step.
                long firstKey;
                EntityBroadcast firstValue;
                using (var e = state.HeldBroadcasts.GetEnumerator())
                {
                    if (!e.MoveNext()) break;
                    var cur = e.Current;
                    firstKey = cur.Key;
                    firstValue = cur.Value;
                }
                if (firstKey != state.KnownEntitySequence + 1) break;

                state.HeldBroadcasts.Remove(firstKey);
                state.KnownEntitySequence = firstKey;
                // CrossCallSlotMarker is a slot reservation, not a real broadcast — its effect
                // is already inlined in the cross-call's replay payload. Skip emission; only
                // advance the sequence counter so subsequent broadcasts aren't held forever.
                if (!ReferenceEquals(firstValue, CrossCallSlotMarker))
                    BufferBroadcast(entityId, firstValue, firstKey);
            }
        }

        /// <summary>
        /// Append preceding operations (broadcasts queued during active RPC) directly onto
        /// <paramref name="allOps"/>. Applies per-entity ordering: in-order broadcasts append,
        /// out-of-order ones go to state.HeldBroadcasts, duplicates drop.
        /// </summary>
        private void AppendBuiltPrecedingOps(List<SessionOp> allOps)
        {
            if (_rpcBroadcastQueue.Count == 0) return;

            // Sort the queue in place — _rpcBroadcastQueue is cleared by the outer caller
            // after this method runs (see ExecuteOneCallAsync / finally block in the RPC handler).
            if (_rpcBroadcastQueue.Count > 1)
                _rpcBroadcastQueue.Sort(QueuedBroadcastByEntityThenSequence);

            foreach (var b in _rpcBroadcastQueue)
            {
                var state = GetOrCreateEntityState(b.EntityId, b.StateTypeName);
                var expectedNext = state.KnownEntitySequence + 1;

                if (b.EntitySequenceNumber == expectedNext)
                {
                    state.KnownEntitySequence = b.EntitySequenceNumber;
                    allOps.Add(BroadcastToSessionOp(b.EntityId, b.Broadcast, b.EntitySequenceNumber));
                }
                else if (b.EntitySequenceNumber > expectedNext)
                {
                    state.HeldBroadcasts[b.EntitySequenceNumber] = b.Broadcast;
                }
                // else: duplicate / old — drop, GC reclaims.
            }
        }

        /// <summary>
        /// Cascading drain + resolve: resolving deferred responses advances KnownEntitySequence,
        /// which may allow held broadcasts to drain, which may satisfy more deferred responses.
        /// Repeats until no more progress is made.
        /// </summary>
        private void DrainAndResolve(EntityOrderingState state, string entityId, string stateTypeName)
        {
            while (true)
            {
                var knownBefore = state.KnownEntitySequence;
                DrainHeldBroadcasts(state, entityId);
                ResolveDeferredResponses(entityId, stateTypeName);
                if (state.KnownEntitySequence == knownBefore)
                    break;
            }
        }

        /// <summary>
        /// Check if any deferred RPC responses can now be resolved (gap filled).
        /// Resolved responses are buffered to _outgoingBatch.
        /// </summary>
        private void ResolveDeferredResponses(string entityId, string stateTypeName)
        {
            var state = GetOrCreateEntityState(entityId, stateTypeName);

            for (int i = _deferredResponses.Count - 1; i >= 0; i--)
            {
                var deferred = _deferredResponses[i];
                if (deferred.EntityId != entityId || deferred.StateTypeName != stateTypeName) continue;
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
        /// Convert an already-per-subscriber-tailored <see cref="EntityBroadcast"/> to a
        /// <see cref="SessionOp"/> for delivery to the client. By contract, the broadcast
        /// arriving here has been stripped per-recipient by <c>EntityGrain.DistributeBroadcasts</c>
        /// → <c>BroadcastTailor.TailorForSubscriber</c> (recursively, including nested trigger
        /// ops in <c>Op.Triggers</c>): exactly one of <c>ReplayPayload</c>/<c>PatchBytes</c> is
        /// populated based on this player's force-patch contributions, and <c>StateBytes</c> is
        /// preserved. SessionManager does NO capability decisioning — it only reshapes the
        /// payload from broadcast-frame to wire-frame.
        /// <para>
        /// 0.24 unification: the broadcast's <c>Op</c> already IS the canonical
        /// <c>MetaOperation</c>, so no field-by-field reconstruction is needed. The pre-refactor
        /// behaviour of repurposing <c>broadcast.ExcludePlayerId</c> into <c>Call.CallerId</c> on
        /// the wire is DROPPED — clients no longer learn the originator id from the broadcast op,
        /// and the server-side caller exclusion in <c>EntityGrain.DistributeBroadcasts</c>
        /// already filters those subscribers out anyway.
        /// </para>
        /// </summary>
        private SessionOp BroadcastToSessionOp(string entityId, EntityBroadcast broadcast, long entitySequenceNumber)
        {
            return new SessionOp {
                EntityId = entityId,
                RequestId = 0,
                OpBytes = broadcast.OpBytes,
                EntitySequenceNumber = entitySequenceNumber,
            };
        }

        /// <summary>
        /// Convert an EntityCallResult to a SessionOp. The entity sequence number from
        /// <see cref="EntityCallResult.EntitySequenceNumber"/> is carried through to the wire so
        /// the client can populate <see cref="SubscriptionClaim.LastKnownEntitySequence"/> on
        /// next Resume.
        /// </summary>
        private SessionOp CallResultToSessionOp(string entityId, long requestId, EntityCallResult result)
        {
            return new SessionOp {
                EntityId = entityId,
                RequestId = requestId,
                OpBytes = result.OpBytes,
                Error = result.Error,
                CrossEntityOperations = result.CrossEntityCalls,
                EntitySequenceNumber = result.EntitySequenceNumber,
            };
        }

        private EntityOrderingState GetOrCreateEntityState(string entityId, string stateTypeName)
        {
            if (!_entityStates.TryGetValue(entityId, out var byType))
            {
                byType = new Dictionary<string, EntityOrderingState>();
                _entityStates[entityId] = byType;
            }
            if (!byType.TryGetValue(stateTypeName, out var state))
            {
                state = new EntityOrderingState();
                byType[stateTypeName] = state;
            }
            return state;
        }

        private void SetEntityState(string entityId, string stateTypeName, EntityOrderingState state)
        {
            if (!_entityStates.TryGetValue(entityId, out var byType))
            {
                byType = new Dictionary<string, EntityOrderingState>();
                _entityStates[entityId] = byType;
            }
            byType[stateTypeName] = state;
        }

        private void RemoveEntityState(string entityId, string stateTypeName)
        {
            if (_entityStates.TryGetValue(entityId, out var byType))
            {
                byType.Remove(stateTypeName);
                if (byType.Count == 0) _entityStates.Remove(entityId);
            }
        }

        #endregion

        #region Entity Notifications

        public Task NotifyEntityDeactivatingAsync(string entityId, string stateTypeName)
        {
            if (RemoveSubscription(entityId, stateTypeName, out _))
            {
                RemoveEntityState(entityId, stateTypeName);
                _deferredResponses.RemoveAll(d => d.EntityId == entityId && d.StateTypeName == stateTypeName);

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
            // _pendingPackets is ordered by SequenceNumber (Add happens with ++_sequenceNumber).
            // Find the first index with seq > ack — everything before is removable as a single
            // O(N-K) RemoveRange, vs N×O(N) on per-element RemoveAt walk.
            int removeUpTo = 0;
            int count = _pendingPackets.Count;
            while (removeUpTo < count && _pendingPackets[removeUpTo].SequenceNumber <= acknowledgedSequence)
                removeUpTo++;
            if (removeUpTo > 0)
            {
                _pendingPackets.RemoveRange(0, removeUpTo);
                _logger.PacketsCleanedBySeq(removeUpTo, acknowledgedSequence);
            }
        }

        // Pool ref-count machinery was removed in the PooledPayload → ROM<byte> refactor.
        // OpBytes is now GC-backed; eviction just drops references and GC reclaims naturally.
        private void ReleaseAndClearPendingPackets() => _pendingPackets.Clear();
        private void ReleaseAndClearDeferredResponses() => _deferredResponses.Clear();
        private void ReleaseAndClearRpcBroadcastQueue() => _rpcBroadcastQueue.Clear();
        private void ReleaseAndClearEntityStates()
        {
            foreach (var byType in _entityStates.Values)
                foreach (var state in byType.Values)
                    state.HeldBroadcasts.Clear();
            _entityStates.Clear();
        }

        #endregion

        #region Helpers

        public Task<Guid> GetCurrentSessionIdAsync()
        {
            return Task.FromResult(_currentSessionId);
        }

        /// <summary>
        /// 0.24.0+ Client-driven subscription reclaim. Per claim, ask the entity grain for a
        /// verdict; populate local <see cref="_subscribedEntities"/> + <see cref="_entityStates"/>
        /// from the successful results. Failed results are returned in the list so the caller
        /// can decide whether to abort the whole Resume.
        /// </summary>
        private async Task<List<SubscriptionResult>> ReclaimClaimedSubscriptionsAsync(
            IReadOnlyList<SubscriptionClaim> claims,
            string? clientVersion,
            ulong clientSignatureHash)
        {
            var results = new List<SubscriptionResult>(claims.Count);
            var sessionRef = this.AsReference<ISessionManagerReference>();

            foreach (var claim in claims)
            {
                try
                {
                    var entityGrain = GetEntityGrain(claim.EntityId, claim.StateTypeName);
                    if (entityGrain == null)
                    {
                        results.Add(new SubscriptionResult
                        {
                            EntityId = claim.EntityId,
                            Status = SubscriptionStatus.Failed,
                            FailureReason = $"Unknown state type '{claim.StateTypeName}' on server",
                            StateTypeName = claim.StateTypeName,
                        });
                        continue;
                    }

                    var verdict = await entityGrain.ReclaimSubscriptionAsync(
                        _playerId, sessionRef, claim.LastKnownEntitySequence,
                        clientVersion, clientSignatureHash);

                    if (verdict.Status != SubscriptionStatus.Failed)
                    {
                        SetSubscription(claim.EntityId, claim.StateTypeName, new EntitySubscriptionInfo(
                            entityId: claim.EntityId,
                            stateTypeName: claim.StateTypeName,
                            grainRef: entityGrain,
                            clientVersion: clientVersion,
                            clientSignatureHash: clientSignatureHash));

                        SetEntityState(claim.EntityId, claim.StateTypeName, new EntityOrderingState
                        {
                            KnownEntitySequence = verdict.EntitySequenceNumber
                        });

                        _logger.SubscribedToEntity(_playerId, claim.EntityId, verdict.EntitySequenceNumber);
                    }

                    results.Add(verdict);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SessionManager:{Player}] Reclaim threw for entity {EntityId}", _playerId, claim.EntityId);
                    results.Add(new SubscriptionResult
                    {
                        EntityId = claim.EntityId,
                        Status = SubscriptionStatus.Failed,
                        FailureReason = ex.Message,
                        StateTypeName = claim.StateTypeName,
                    });
                }
            }

            return results;
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
                // ROM-backed by GC byte[] — eviction just drops the reference; no per-element work.
                _pendingPackets.RemoveRange(0, toRemove);
                _logger.PacketsCleanedByCount(toRemove);
            }
        }

        #endregion
    }
}
