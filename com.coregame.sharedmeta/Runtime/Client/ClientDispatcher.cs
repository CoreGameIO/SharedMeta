using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client
{
    /// <summary>
    /// Pending RPC request awaiting response.
    /// </summary>
    internal class PendingRequest
    {
        public long RequestId { get; init; }
        public string EntityId { get; init; } = "";
        public ushort MethodId { get; init; }  // 0.24.0+: stamped from RpcCall.MethodId
        public ReadOnlyMemory<byte> Payload { get; init; }
        public bool IsCrossOptimistic { get; init; }
        public long ServerTimeTicks { get; init; }
        // 0.26.6+ piggybacked debug-channel data (PayloadDebug); carries deep-state CRCs
        // for methods annotated [MetaMethod(DeepStateCheck = SnapshotTiming.X)].
        public SharedMeta.Core.PayloadDebug? Debug { get; init; }
        public TaskCompletionSource<SessionOp> Tcs { get; } = new();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Client-side dispatcher for managing entity subscriptions and broadcasts.
    /// Wraps IConnection to provide broadcast subscription pattern and frame-based processing.
    ///
    /// All shared state is protected by a single reentrant lock (_lock).
    /// Client-side traffic is low, so contention is negligible and simplicity wins.
    ///
    /// IMPORTANT: Callbacks (broadcast handlers, TCS completions, events) are always
    /// invoked OUTSIDE _lock to prevent deadlocks when handlers make reentrant calls
    /// (e.g., firing RPCs from a broadcast handler on a background thread).
    ///
    /// Two processing modes:
    /// - Frame-based (default): call ProcessPendingBroadcasts() once per frame from the game loop
    /// - Immediate: set ImmediateMode = true for console apps or tests
    /// </summary>
    public class ClientDispatcher : IClientDispatcher
    {
        private readonly IConnection _connection;
        private readonly object _lock = new();

        private readonly Dictionary<string, List<Action<SessionOp>>> _broadcastHandlers = new();
        private readonly Dictionary<string, string> _subscribedEntities = new(); // entityId → stateTypeName
        // 0.24.0+ Highest per-entity broadcast sequence we've observed. Used to populate
        // SubscriptionClaim.LastKnownEntitySequence on next Resume so the server's entity grain
        // can return Continued (no gap, no state shipped) instead of Refreshed (full snapshot).
        private readonly Dictionary<string, long> _lastKnownEntitySeq = new();
        private long _nextRequestId;

        // Pending RPC requests awaiting response
        private readonly Dictionary<long, PendingRequest> _pendingRequests = new();

        // Last request ID that received a real entity response (TCS completed with result)
        private long _lastCompletedRequestId;

        // Session management
        private Guid _sessionId;
        private long _lastAcknowledgedSequence;
        // 0.21.0: stamped from MetaClientOptions.ClientAppVersion at ConnectSessionAsync.
        // Reused on resume / restart so reconnects identify the same client version.
        private string? _clientAppVersion;

        // Ordering: all seq>0 SessionResponses drain through this dispatcher in sequence
        // order. It absorbs the reassembly logic, re-entrant handler detection, and
        // single-thread dispatch invariant that the client cares about.
        private readonly OrderedDispatcher _ordering;

        // Broadcast suppression: prevents ProcessPendingBroadcasts from draining
        // during the window between receiving an RPC response and completing the local replay.
        // Without this, broadcasts that arrive between MarkDirectResponse and the local replay
        // can modify state, causing desyncs.
        private int _broadcastSuppressCount;

        // Guards against duplicate HandleSessionTerminated calls
        // (transport event + RPC error can both detect supersede)
        private bool _terminated;

        // 0.26.3+: Tracks Reconnecting → ? transition so HandleDisconnected can tell whether
        // the transport gave up retrying (Reconnecting → Disconnected with reason != ClientRequested)
        // versus a clean close (Disconnected with no prior Reconnecting). The former gets an
        // extra Failed event so UI can show a permanent Reconnect button.
        private bool _wasReconnecting;

        // Server time synchronization
        private long _lastServerTimeTicks;
        private long _localTimeAtLastSync;

        public event Action<ConnectionStatus, string?>? OnConnectionStatusChanged;
        public event Action<string>? OnSessionSuperseded;

        public IConnection Connection => _connection;

        /// <summary>Current session ID.</summary>
        public Guid SessionId => _sessionId;

        /// <summary>Current client-side request ID counter (last sent).</summary>
        public long CurrentRequestId { get { lock (_lock) return _nextRequestId; } }

        /// <summary>Last request ID that received a real entity response.</summary>
        public long LastCompletedRequestId => _lastCompletedRequestId;

        /// <summary>Last acknowledged sequence number.</summary>
        public long LastAcknowledgedSequence => _lastAcknowledgedSequence;

        /// <summary>
        /// Approximate current server time (UTC ticks).
        /// Computed from last received server time + local elapsed delta.
        /// No lock — monotonic approximation, torn read is harmless.
        /// </summary>
        public long ServerTimeTicks =>
            _lastServerTimeTicks == 0
                ? DateTime.UtcNow.Ticks
                : _lastServerTimeTicks + (DateTime.UtcNow.Ticks - _localTimeAtLastSync);

        /// <summary>
        /// When true, broadcasts are processed immediately when they arrive in order.
        /// Set to false for frame-based processing, then call ProcessPendingBroadcasts() manually.
        /// </summary>
        public bool ImmediateMode { get; set; }

        /// <summary>
        /// Optional listener for server-side stall notifications. Set by <c>MetaClient</c>
        /// from <c>MetaClientOptions.SessionHealth</c>; null = drop notifications silently.
        /// </summary>
        public ISessionHealthListener? SessionHealthListener { get; set; }

        /// <summary>
        /// Optional listener for client-side connection health (pending request timeouts).
        /// Set by <c>MetaClient</c> from <c>MetaClientOptions.ConnectionHealth</c>.
        /// </summary>
        public IConnectionHealthListener? ConnectionHealthListener { get; set; }

        /// <summary>
        /// Timeout thresholds for client-side connection health monitoring.
        /// </summary>
        public ConnectionHealthOptions ConnectionHealthOptions { get; set; } = new();

        private ConnectionHealthStatus _lastHealthStatus = ConnectionHealthStatus.Healthy;
        private DateTime _lastRetryTime;

        /// <summary>
        /// Optional diagnostics log for request lifecycle tracing. When set, all send/receive/resend/stall
        /// events are logged via this delegate. Typically writes to a file for post-mortem analysis.
        /// </summary>
        public Action<string>? DiagnosticsLog { get; set; }

        private void LogDiag(string msg)
        {
            var log = DiagnosticsLog;
            if (log == null) return;
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            log($"[{ts}] {msg}");
        }

        /// <summary>
        /// Next expected broadcast sequence number.
        /// </summary>
        public long NextExpectedSequence => _ordering.Head;

        /// <summary>
        /// True if there's a gap in received broadcasts.
        /// </summary>
        public bool HasSequenceGap => _ordering.HasGap;

        public ClientDispatcher(IConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _ordering = new OrderedDispatcher(DispatchResponseOps);
            _connection.OnBatch += HandleBatch;
            _connection.OnDisconnected += HandleDisconnected;
            _connection.OnSessionTerminated += HandleSessionTerminated;
            _connection.OnRequireSessionReconnect += HandleRequireSessionReconnect;
            _connection.OnReconnecting += HandleReconnecting;
            _connection.OnReconnected += HandleTransportReconnected;

            // Default to frame-based mode: broadcasts queue until ProcessPendingBroadcasts() is called.
            // For game engines (Unity), call ProcessPendingBroadcasts() from the game loop.
            // Set ImmediateMode = true for console apps or tests where threading is not a concern.
            ImmediateMode = false;
        }

        public async Task<ConnectResponse> SubscribeAsync(string entityId, string? stateTypeName = null)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));

            var result = await _connection.SubscribeAsync(entityId, stateTypeName ?? "");

            if (!result.Success)
            {
                // 0.22.0+: structured rejection (Breaking schema gate, RejectedMethods, …)
                // surfaces as IncompatibleFeatureException with the full FeatureRequirement
                // so game UI can render a "feature requires app update" non-blocking notification
                // instead of a generic error dialog. Generic transport / business errors keep
                // the legacy InvalidOperationException for backward-compat with existing catch
                // blocks in user code.
                if (result.FeatureRequirement != null)
                    throw new IncompatibleFeatureException(result.FeatureRequirement);
                throw new InvalidOperationException($"Failed to subscribe to entity '{entityId}': {result.Error}");
            }

            lock (_lock)
            {
                _subscribedEntities[entityId] = stateTypeName ?? "";
                // 0.24.0+ Seed per-entity seq tracker from the Subscribe snapshot so the next
                // Resume claim is accurate even if no broadcast arrives between Subscribe and
                // Resume (rare timing window — usually broadcasts start flowing immediately).
                if (result.EntitySequenceNumber > 0)
                    _lastKnownEntitySeq[entityId] = result.EntitySequenceNumber;
            }

            return new ConnectResponse
            {
                StateBytes = result.StateBytes,
                CurrentSequenceNumber = result.EntitySequenceNumber,
                OptimisticRandomBytes = result.OptimisticRandomBytes,
                NamedRandomsBytes = result.NamedRandomsBytes,
                ConfigMajorVersion = result.ConfigVersion.Major,
                ConfigMinorVersion = result.ConfigVersion.Minor,
                ConfigPatchVersion = result.ConfigVersion.Patch,
                AugmentedCapabilities = result.AugmentedCapabilities,
            };
        }

        public async Task UnsubscribeAsync(string entityId)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));

            await _connection.UnsubscribeAsync(entityId);

            lock (_lock)
            {
                _subscribedEntities.Remove(entityId);
                _lastKnownEntitySeq.Remove(entityId);
                _broadcastHandlers.Remove(entityId);
            }
        }

        public Task<SessionOp> SendAsync(string entityId, RpcCall call, string? stateTypeName = null)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));
            if (call == null)
                throw new ArgumentNullException(nameof(call));

            var payloadBytes = call.Payload;

            PendingRequest pending;
            lock (_lock)
            {
                var requestId = ++_nextRequestId;
                pending = new PendingRequest
                {
                    RequestId = requestId,
                    EntityId = entityId,
                    MethodId = call.MethodId,
                    Payload = payloadBytes,
                    IsCrossOptimistic = call.IsCrossOptimistic,
                    ServerTimeTicks = call.ServerTimeTicks,
                    Debug = call.Debug
                };
                _pendingRequests[requestId] = pending;
            }

            // Start RPC call without blocking - completion handled separately.
            _ = SendAndCompleteAsync(pending);

            LogDiag($"SEND reqId={pending.RequestId} methodId={pending.MethodId} entity={pending.EntityId}");

            return pending.Tcs.Task;
        }

        private async Task SendAndCompleteAsync(PendingRequest pending)
        {
            try
            {
                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                LogDiag($"RECV reqId={pending.RequestId} seq={response.SequenceNumber} ops={response.Operations?.Count ?? 0} err={response.HasError}");

                if (response.HasError)
                {
                    LogDiag($"ERROR reqId={pending.RequestId} {response.Error}");

                    // "re-handshake" marker: server-side session handler is unbound (e.g. fresh
                    // transport reconnect before SessionConnect ran, server restart in flight).
                    // This is a transient transport-level state, NOT a call failure — the server
                    // also pushes RequireSessionReconnect, the client's recovery flow will re-run
                    // SessionConnect, and ResendPendingRequestsAsync will replay this request.
                    // Removing from pending here would drop the request silently AND leave a gap
                    // in the RequestId sequence that breaks the server-side ordering buffer on
                    // the next Resume (client reports a stale LastCompletedRequestId because the
                    // skipped id was never completed and never bumped the counter).
                    if (response.Error != null && response.Error.Contains("re-handshake", StringComparison.OrdinalIgnoreCase))
                    {
                        LogDiag($"REHANDSHAKE_PENDING reqId={pending.RequestId} (kept for replay after re-handshake)");
                        return;
                    }

                    lock (_lock) { _pendingRequests.Remove(pending.RequestId); }

                    if (response.Error != null && response.Error.Contains("superseded", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Tcs.TrySetException(new InvalidOperationException(response.Error));
                        HandleSessionTerminated(response.Error);
                        return;
                    }

                    pending.Tcs.TrySetException(
                        new InvalidOperationException($"RPC call failed: {response.Error}"));
                    return;
                }

                ProcessServerResponse(response);
            }
            catch (Exception ex)
            {
                LogDiag($"TRANSPORT_ERROR reqId={pending.RequestId} {ex.GetType().Name}: {ex.Message} (kept pending)");
                MetaLog.Warning($"[ClientDispatcher] RPC transport error (keeping pending for reconnect): {ex.Message}");
            }
        }

        public IDisposable OnBroadcast(string entityId, Action<SessionOp> handler)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                if (!_broadcastHandlers.TryGetValue(entityId, out var handlers))
                {
                    handlers = new List<Action<SessionOp>>();
                    _broadcastHandlers[entityId] = handlers;
                }
                handlers.Add(handler);
            }

            return new BroadcastSubscription(this, entityId, handler);
        }

        public Task<List<RpcCall>?> GetMissingBroadcastsAsync(string entityId, long fromSequence)
        {
            // Gap recovery is now handled by session reconnect with lastAcknowledgedSequence
            // This method is kept for API compatibility but returns null
            return Task.FromResult<List<RpcCall>?>(null);
        }

        public bool IsSubscribed(string entityId)
        {
            lock (_lock)
            {
                return _subscribedEntities.ContainsKey(entityId);
            }
        }

        /// <summary>
        /// Player ID for this session. Must be set before calling ConnectSessionAsync.
        /// </summary>
        public string? PlayerId { get; set; }

        /// <inheritdoc />
        public MetaClientSignature? ClientSignature { get; set; }

        /// <summary>
        /// 0.24.0+ When true, skip the auto-default fallback and connect with Hash=0 (legacy
        /// opt-out). Set by <c>MetaClient</c> from <c>MetaClientOptions.DisableClientSignatureNegotiation</c>.
        /// </summary>
        public bool DisableClientSignatureNegotiation { get; set; }

        /// <inheritdoc />
        public ClientSignatureAnnotated? Annotated { get; private set; }

        /// <summary>
        /// 0.24.0+ Game-level recovery decision callback (see <see cref="IMetaSessionRecoveryHandler"/>).
        /// Set by <c>MetaClient</c> from <c>MetaClientOptions.SessionRecoveryHandler</c>;
        /// defaults to <see cref="DefaultSessionRecoveryHandler"/> (returns
        /// <see cref="SessionRecoveryAction.Reconnect"/>) when the host doesn't override.
        /// Invoked when the server returns
        /// <see cref="SessionConnectFailureReason.SessionUnknown"/> on a Resume attempt.
        /// </summary>
        public IMetaSessionRecoveryHandler? SessionRecoveryHandler { get; set; }

        /// <summary>
        /// True if session has been established with server.
        /// </summary>
        public bool IsSessionConnected { get; private set; }

        public async Task<SessionConnectResult> ConnectSessionAsync(Guid sessionId, long lastAcknowledgedSequence, string? clientAppVersion = null, SessionConnectMode? mode = null)
        {
            if (string.IsNullOrEmpty(PlayerId))
                throw new InvalidOperationException("PlayerId must be set before connecting session");

            lock (_lock)
            {
                _sessionId = sessionId;
                _lastAcknowledgedSequence = lastAcknowledgedSequence;
                _clientAppVersion = clientAppVersion;
            }

            // 0.24.0+ Auto-default: when the consumer didn't pin a signature, fall back to the one
            // the generator published into MetaClientSignature.Default (from RegisterAllServices /
            // module initializer). Resolved here at connect time — not in the ctor — so it tolerates
            // the publish running after MetaClient construction. Idempotent across reconnects.
            if (ClientSignature == null && !DisableClientSignatureNegotiation)
                ClientSignature = ClientSignatureDefault.Value;

            // 0.22.0: phase-1 of the compatibility handshake. Transmit the generated
            // ClientSignature.SignatureHash when negotiation is enabled (consumer assigned
            // ClientSignature); otherwise pass 0 so the server treats us as legacy/opt-out.
            var signatureHash = ClientSignature?.SignatureHash ?? 0UL;
            // 0.24.0+ Explicit mode: caller picks (Resume on transport-reconnect path,
            // StartNew on cold-app-start / Reconnect recovery action). Default fallback:
            // Resume when we have a non-empty sessionId, StartNew otherwise.
            var effectiveMode = mode ?? (sessionId != Guid.Empty ? SessionConnectMode.Resume : SessionConnectMode.StartNew);

            // 0.24.0+ Build subscription claims from the locally-tracked _subscribedEntities.
            // Only meaningful on Resume — StartNew explicitly discards the prior session, so
            // claims are skipped to avoid double-subscribe noise. LastKnownEntitySequence comes
            // from _lastKnownEntitySeq (running max of per-entity seq across all incoming
            // SessionOps); zero means we haven't seen any broadcast for this entity yet, which
            // makes the entity grain ship a Refreshed snapshot (correct fallback).
            List<SubscriptionClaim>? claims = null;
            if (effectiveMode == SessionConnectMode.Resume)
            {
                lock (_lock)
                {
                    if (_subscribedEntities.Count > 0)
                    {
                        claims = new List<SubscriptionClaim>(_subscribedEntities.Count);
                        foreach (var (entityId, stateTypeName) in _subscribedEntities)
                        {
                            _lastKnownEntitySeq.TryGetValue(entityId, out var lastSeq);
                            claims.Add(new SubscriptionClaim
                            {
                                EntityId = entityId,
                                StateTypeName = stateTypeName,
                                LastKnownEntitySequence = lastSeq,
                            });
                        }
                    }
                }
            }

            // 0.24.0+ Gap-fix: report our highest fully-completed RequestId so the server can
            // advance its RPC ordering baseline past responses it may have lost (eviction/crash
            // before persistence flush). Without this, next-new RequestIds would be classified
            // OutOfOrder against a stale _lastDispatchedRequestId and stashed forever.
            var result = await _connection.SessionConnectAsync(PlayerId, sessionId == Guid.Empty ? null : sessionId, lastAcknowledgedSequence, clientAppVersion, signatureHash, effectiveMode, _lastCompletedRequestId, claims);

            if (!result.Success)
            {
                IsSessionConnected = false;
                // If session connect fails completely, fail all pending requests
                if (result.IsNewSession)
                {
                    FailAllPendingRequests($"Session connect failed: {result.Error}");
                }
                return new SessionConnectResult
                {
                    Success = false,
                    Error = result.Error,
                    FailureReason = result.FailureReason,
                };
            }

            // 0.24.0+ pick up server-supplied annotation OR fall through to phase-2 if the
            // server didn't recognize our signature hash. Phase-2 is only performed when the
            // consumer wired a ClientSignature — opted-out clients stay annotation-less.
            Annotated = result.Annotated;

            // 0.24.0+ Mirror the server-side handshake trace so a developer running
            // client + server can verify they understood each other from each side's logs.
            if (signatureHash != 0)
            {
                if (result.Annotated != null)
                {
                    var (rej, fp) = CountStatuses(result.Annotated.Statuses);
                    MetaLog.Info($"[ClientDispatcher] Handshake phase-1 HIT: clientHash=0x{signatureHash:X16}, serverHash=0x{result.ServerSignatureHash:X16}, annotation: {result.Annotated.Statuses.Length} methods ({rej} rejected, {fp} force-patch)");
                }
                else if (result.NeedsSignatureRegistration)
                {
                    MetaLog.Info($"[ClientDispatcher] Handshake phase-1 MISS: clientHash=0x{signatureHash:X16}, serverHash=0x{result.ServerSignatureHash:X16}; sending phase-2 RegisterClientSignature");
                }
                else
                {
                    MetaLog.Info($"[ClientDispatcher] Handshake: no annotation returned and no registration needed — negotiation likely disabled server-side (clientHash=0x{signatureHash:X16}, serverHash=0x{result.ServerSignatureHash:X16})");
                }
            }

            if (result.NeedsSignatureRegistration && ClientSignature != null)
            {
                // Fail-loud: if phase-2 errors out, the server has NO signature for this
                // client, and every subsequent RPC/Query fails with the misleading
                // "out of range for client signature." Surface the real cause at
                // ConnectAsync instead of letting the user hunt it on the first RPC.
                // Common cause: a Unity transport implementation that hasn't overridden
                // RegisterClientSignatureAsync, so IConnection's DIM throws
                // NotSupportedException.
                RegisterClientSignatureResponse phase2;
                try
                {
                    phase2 = await _connection.RegisterClientSignatureAsync(result.SessionId, ClientSignature);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidOperationException(
                        $"Phase-2 signature registration failed: the connection of type " +
                        $"'{_connection.GetType().FullName}' does not implement " +
                        $"IConnection.RegisterClientSignatureAsync. Either override that method " +
                        $"on the transport, or clear MetaClientOptions.ClientSignature to opt out " +
                        $"of compatibility negotiation. Original message: {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Phase-2 signature registration threw on transport " +
                        $"'{_connection.GetType().Name}': {ex.Message}", ex);
                }
                if (!phase2.Success)
                {
                    throw new InvalidOperationException(
                        $"Phase-2 signature registration was rejected by the server: " +
                        $"{phase2.Error ?? "<no error message>"}");
                }
                Annotated = phase2.Annotated;
                if (phase2.Annotated != null)
                {
                    var (rej, fp) = CountStatuses(phase2.Annotated.Statuses);
                    MetaLog.Info($"[ClientDispatcher] Handshake phase-2 REGISTERED: clientHash=0x{signatureHash:X16} ({ClientSignature!.KnownMethods.Count} methods) -> serverHash=0x{phase2.Annotated.ServerSignatureHash:X16}, annotation: {rej} rejected, {fp} force-patch");
                }
                else
                {
                    MetaLog.Warning($"[ClientDispatcher] Handshake phase-2 returned success but null annotation — server may have negotiation disabled (clientHash=0x{signatureHash:X16})");
                }
            }

            lock (_lock)
            {
                _sessionId = result.SessionId;
                IsSessionConnected = true;
                _terminated = false;

                // Sync clock from connect response
                if (result.ServerTimeTicks > 0)
                {
                    _lastServerTimeTicks = result.ServerTimeTicks;
                    _localTimeAtLastSync = DateTime.UtcNow.Ticks;
                }

                // Server couldn't resume the old session — it created a fresh one and will
                // number broadcasts from 1. Rewind ordering head to match; otherwise
                // seq-1 packets would be discarded as duplicates of our stale _nextExpected.
                if (result.IsNewSession)
                {
                    _ordering.Reset(1);
                    _lastAcknowledgedSequence = 0;
                }
            }

            // Process missed packets — ProcessServerResponse handles its own locking
            if (result.MissedPackets is { Count: > 0 })
            {
                MetaLog.Info($"[ClientDispatcher] Processing {result.MissedPackets.Count} missed packets");
                foreach (var packet in result.MissedPackets)
                {
                    ProcessServerResponse(packet);
                }
            }

            // 0.24.0+ Apply server's per-claim subscription verdicts BEFORE re-sending pending
            // RPCs. Order matters: Refreshed verdicts ship a fresh state snapshot that must be
            // installed into the shared state container before any pending RPC's response lands
            // (RPC responses may depend on the post-Refreshed state, and UI must not flicker
            // back to old state when a stale-pending RPC completes against new state).
            if (result.Subscriptions is { Count: > 0 })
            {
                MetaLog.Info($"[ClientDispatcher] Server returned {result.Subscriptions.Count} subscription verdict(s)");
                // Advance per-entity seq tracker to the verdict's reported seq so the next
                // Resume claim is accurate even if no broadcast arrives between Resume and
                // next Resume. Continued: seq matches ours, no-op. Refreshed: adopt server's
                // current seq.
                lock (_lock)
                {
                    foreach (var v in result.Subscriptions)
                    {
                        _lastKnownEntitySeq.TryGetValue(v.EntityId,  out var known);
                        MetaLog.Info($"[ClientDispatcher] Subscription {v.EntityId} EntitySequenceNumber = {v.EntitySequenceNumber} Known = {known}");
                        if (v.Status == SubscriptionStatus.Failed) continue;
                        if (v.EntitySequenceNumber > 0)
                            _lastKnownEntitySeq[v.EntityId] = v.EntitySequenceNumber;
                    }
                }
                OnSubscriptionsReclaimed?.Invoke(result.Subscriptions);
            }

            // Re-send remaining pending requests (those not resolved by missed packets). Runs
            // AFTER verdict application so resent RPCs hit the server with the post-Refreshed
            // state already installed locally.
            await ResendPendingRequestsAsync();

            return new SessionConnectResult
            {
                Success = true,
                SessionId = result.SessionId,
                IsNewSession = result.IsNewSession,
                MissedPackets = result.MissedPackets,
                ServerTimeTicks = result.ServerTimeTicks,
                Subscriptions = result.Subscriptions,
                FailureReason = result.FailureReason,
            };
        }

        /// <summary>
        /// Re-send all pending requests after reconnect.
        /// </summary>
        private async Task ResendPendingRequestsAsync()
        {
            List<PendingRequest> pendingList;
            lock (_lock)
            {
                pendingList = _pendingRequests.Values.ToList();
            }

            if (pendingList.Count == 0)
                return;

            MetaLog.Info($"[ClientDispatcher] Re-sending {pendingList.Count} pending requests after reconnect");
            LogDiag($"RESEND_ALL count={pendingList.Count} ids=[{string.Join(",", pendingList.Select(p => p.RequestId))}]");

            // Await all re-sends so the caller (ReconnectAsync → OnConnectionStatusChanged)
            // doesn't signal "Reconnected" until every pending RPC has either received its
            // server response or failed. Before this was fire-and-forget and the connection
            // status callback fired while server was still draining the re-sent queue —
            // game code (or the user) could start NEW actions on stale optimistic state
            // before re-sends settled, producing false-positive desyncs and state drift.
            var resends = new Task[pendingList.Count];
            for (int i = 0; i < pendingList.Count; i++)
                resends[i] = ResendRequestAsync(pendingList[i]);
            try { await Task.WhenAll(resends); }
            catch
            {
                // ResendRequestAsync swallows its own per-pending errors into the PendingRequest's
                // TCS; an exception escaping here would be unexpected. Don't let one bad re-send
                // tank the whole reconnect — log via the diag channel and continue.
                LogDiag("RESEND_ALL one or more re-sends threw; per-RPC errors are surfaced via TCS");
            }
        }

        private async Task ResendRequestAsync(PendingRequest pending)
        {
            try
            {
                LogDiag($"RESEND_START reqId={pending.RequestId} methodId={pending.MethodId}");
                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                // Top-level transport error
                if (response.HasError)
                {
                    // See SendAndCompleteAsync — same "re-handshake" transient path. Keep pending
                    // so the post-SessionConnect ResendPendingRequestsAsync can replay it.
                    if (response.Error != null && response.Error.Contains("re-handshake", StringComparison.OrdinalIgnoreCase))
                    {
                        LogDiag($"REHANDSHAKE_RESEND reqId={pending.RequestId} (kept for replay after re-handshake)");
                        return;
                    }

                    lock (_lock) { _pendingRequests.Remove(pending.RequestId); }

                    if (response.Error != null && response.Error.Contains("superseded", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Tcs.TrySetException(new InvalidOperationException(response.Error));
                        HandleSessionTerminated(response.Error);
                        return;
                    }

                    pending.Tcs.TrySetException(
                        new InvalidOperationException($"RPC call failed: {response.Error}"));
                    return;
                }

                // ProcessServerResponse handles its own locking
                ProcessServerResponse(response);
            }
            catch (Exception ex)
            {
                // Transport error on re-send — keep pending for next reconnect attempt.
                LogDiag($"RESEND_ERROR reqId={pending.RequestId} {ex.GetType().Name}: {ex.Message} (kept pending)");
                MetaLog.Warning($"[ClientDispatcher] Re-send transport error (keeping pending): {ex.Message}");
            }
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            lock (_lock)
            {
                _lastAcknowledgedSequence = sequenceNumber;
            }
            await _connection.AcknowledgeSequenceAsync(sequenceNumber);
        }

        /// <inheritdoc/>
        public void SuppressBroadcasts()
        {
            lock (_lock)
            {
                _broadcastSuppressCount++;
            }
        }

        /// <inheritdoc/>
        public void ResumeBroadcasts()
        {
            bool shouldDrain;
            lock (_lock)
            {
                _broadcastSuppressCount--;
                shouldDrain = _broadcastSuppressCount == 0;
            }

            if (shouldDrain)
            {
                ProcessPendingBroadcasts();
            }
        }

        /// <summary>
        /// Process pending broadcasts. Call this once per frame from game loop.
        /// Drains all ready broadcasts (those without gaps before them) and delivers to handlers.
        /// Respects broadcast suppression: returns without draining while inside a local replay
        /// window (see <see cref="SuppressBroadcasts"/>).
        /// </summary>
        /// <returns>Unused — retained for binary compat with older frame loops. Always 0.</returns>
        public int ProcessPendingBroadcasts()
        {
            // Check client-side response timeouts FIRST — must run every frame regardless
            // of broadcast suppression, otherwise auto-retry stops while waiting for RPC replay.
            CheckConnectionHealth();

            if (Volatile.Read(ref _broadcastSuppressCount) > 0) return 0;
            _ordering.Drain();
            return 0;
        }

        /// <summary>
        /// Check pending request ages against timeout thresholds and notify listener on transitions.
        /// Called from ProcessPendingBroadcasts (game-loop thread). Reads _pendingRequests under lock,
        /// computes status and calls listener outside lock (same pattern as broadcast delivery).
        /// </summary>
        private void CheckConnectionHealth()
        {
            long oldestMs = 0;
            int count;
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                count = _pendingRequests.Count;
                foreach (var pr in _pendingRequests.Values)
                {
                    var age = (long)(now - pr.CreatedAt).TotalMilliseconds;
                    if (age > oldestMs) oldestMs = age;
                }
            }

            ConnectionHealthStatus newStatus;
            if (count == 0)
                newStatus = ConnectionHealthStatus.Healthy;
            else if (oldestMs >= ConnectionHealthOptions.HardTimeoutMs)
                newStatus = ConnectionHealthStatus.Unresponsive;
            else if (oldestMs >= ConnectionHealthOptions.SoftTimeoutMs)
                newStatus = ConnectionHealthStatus.Slow;
            else
                newStatus = ConnectionHealthStatus.Healthy;

            // Auto-retry: periodically resend all pending requests that exceeded soft timeout.
            // This is the primary recovery mechanism — client-side, no server dependency.
            var retryMs = ConnectionHealthOptions.RetryIntervalMs;
            if (retryMs > 0 && count > 0 && oldestMs >= ConnectionHealthOptions.SoftTimeoutMs)
            {
                if ((now - _lastRetryTime).TotalMilliseconds >= retryMs)
                {
                    _lastRetryTime = now;
                    List<long> ids;
                    lock (_lock) { ids = _pendingRequests.Keys.ToList(); }
                    LogDiag($"AUTO_RETRY {count} pending, oldest={oldestMs}ms, ids=[{string.Join(",", ids)}]");
                    _ = ResendPendingRequestsAsync();
                }
            }
            else if (count > 0 && oldestMs < ConnectionHealthOptions.SoftTimeoutMs)
            {
                // Pending but not old enough — no retry yet
            }
            else if (count == 0 && _lastHealthStatus != ConnectionHealthStatus.Healthy)
            {
                // Was unhealthy, now all resolved
                LogDiag("HEALTH_CLEAR all pending resolved");
            }

            // Notify listener on status transitions
            var listener = ConnectionHealthListener;
            if (listener != null && newStatus != _lastHealthStatus)
            {
                _lastHealthStatus = newStatus;
                try
                {
                    listener.OnConnectionHealthChanged(newStatus, oldestMs, count);
                }
                catch (Exception ex)
                {
                    MetaLog.Error($"[ClientDispatcher] ConnectionHealthListener threw: {ex.Message}", ex);
                }
            }
        }

        private void HandleBatch(SessionResponse response)
        {
            LogDiag($"BATCH seq={response.SequenceNumber} ops={response.Operations?.Count ?? 0} stall={response.StallNotification?.Stage}");
            // ProcessServerResponse handles its own locking
            ProcessServerResponse(response);
        }

        /// <summary>
        /// Unified processing for all server responses (RPC results, broadcasts, bundles of both).
        ///
        /// Every seq>0 SessionResponse is pushed into <see cref="_ordering"/> keyed by
        /// SequenceNumber — responses are drained in strict sequence order regardless of which
        /// transport channel delivered them. This is required because transports without wire
        /// FIFO (HTTP polling, InProcess, anything with a separate RPC-reply channel plus a
        /// broadcast observer channel) can deliver responses out of order. By unifying both
        /// channels through the buffer, the client always observes server state transitions in
        /// the same order the SessionManager grain committed them: broadcasts that were
        /// produced before an RPC reply are applied before that reply's pending TCS is
        /// resolved.
        ///
        /// Drain handles ops by RequestId: RequestId>0 matches a pending request and resolves
        /// its TCS; RequestId==0 is a pure broadcast dispatched to its entity handlers. A
        /// single SessionResponse can carry a mix (server bundles preceding broadcasts with
        /// an RPC reply during active RPC) — all ops inside it share one SequenceNumber, and
        /// drain delivers them in the order they appear in Operations.
        ///
        /// State mutations happen under _lock; callbacks (handlers, TCS) happen outside _lock.
        /// </summary>
        private void ProcessServerResponse(SessionResponse response)
        {
            // Stall notifications are out-of-band: pure informational, no ops to dispatch,
            // SequenceNumber = 0 (no replay caching). Route directly to the health listener
            // and return — bypasses the broadcast buffer and request matching entirely.
            if (response.StallNotification is { } stall && (response.Operations == null || response.Operations.Count == 0))
            {
                // Server-side stall info — informational. Client auto-retry handles resending.
                LogDiag($"STALL stage={stall.Stage} missing=#{stall.OldestMissingRequestId} stashed={stall.StashedCount} elapsed={stall.ElapsedMilliseconds}ms");

                try
                {
                    if (stall.Stage == Core.Transport.StallStage.Recovered)
                        SessionHealthListener?.OnSessionRecovered(stall);
                    else
                        SessionHealthListener?.OnSessionStalled(stall);
                }
                catch (Exception ex)
                {
                    MetaLog.Error($"[ClientDispatcher] SessionHealthListener threw: {ex.Message}", ex);
                }
                return;
            }

            // Update server clock from every response.
            if (response.ServerTimeTicks > 0)
            {
                lock (_lock)
                {
                    _lastServerTimeTicks = response.ServerTimeTicks;
                    _localTimeAtLastSync = DateTime.UtcNow.Ticks;
                }
            }

            // Seq==0 responses (errors, empty acks, stall-less empty responses) don't
            // participate in sequence ordering — dispatch ops directly, if any. Seq>0
            // responses are handed to the ordered dispatcher; it reassembles by seq and
            // calls DispatchResponseOps in order.
            if (response.SequenceNumber <= 0)
            {
                if (response.Operations is { Count: > 0 })
                    DispatchResponseOps(response);
                return;
            }

            _ordering.Push(response);
            _ordering.Drain();
            var ackTo = _ordering.Head - 1;
            if (ackTo > 0)
            {
                lock (_lock)
                {
                    if (ackTo > _lastAcknowledgedSequence)
                        _lastAcknowledgedSequence = ackTo;
                }
            }
        }

        /// <summary>
        /// Dispatch every op in a drained response in order. RPC results (RequestId > 0)
        /// are matched against <see cref="_pendingRequests"/> and their TCS resolved;
        /// broadcast ops (RequestId == 0) are routed to the entity's handlers.
        ///
        /// Ordering within the response is preserved: the server bundles preceding broadcasts
        /// BEFORE the RPC result op inside <see cref="SessionResponse.Operations"/>, so the
        /// client's local state reflects those broadcasts before the RPC awaiter's continuation
        /// gets to run.
        /// </summary>
        private void DispatchResponseOps(SessionResponse response)
        {
            if (response.Operations == null) return;

            foreach (var op in response.Operations)
            {
                // 0.24.0+ Track highest per-entity sequence number for SubscriptionClaim on next
                // Resume. Server stamps each SessionOp with the entity-grain's seq at the moment
                // it was produced — we keep the running max. Zero means the op had no entity
                // association (e.g. session-level transient errors), don't track that.
                if (op.EntitySequenceNumber > 0 && !string.IsNullOrEmpty(op.EntityId))
                {
                    lock (_lock)
                    {
                        if (!_lastKnownEntitySeq.TryGetValue(op.EntityId, out var prev) || op.EntitySequenceNumber > prev)
                            _lastKnownEntitySeq[op.EntityId] = op.EntitySequenceNumber;
                    }
                }

                // Cross-entity ops carry the TARGET entity's post-call seq. The target entity
                // advances on the server through this cross-call, but its own broadcast to us
                // is suppressed (we're the originator — the effect is inlined in the outer op's
                // replay payload). Without recording these seqs the next Resume claim would
                // report stale LastKnownEntitySequence for the target, server would see a gap
                // and ship a fresh state snapshot (Refreshed verdict), clobbering local state.
                if (op.CrossEntityOperations is { Count: > 0 } crossOps)
                {
                    lock (_lock)
                    {
                        for (int i = 0; i < crossOps.Count; i++)
                        {
                            var ce = crossOps[i];
                            if (ce.EntitySequenceNumber > 0 && !string.IsNullOrEmpty(ce.EntityId))
                            {
                                if (!_lastKnownEntitySeq.TryGetValue(ce.EntityId, out var cePrev) || ce.EntitySequenceNumber > cePrev)
                                    _lastKnownEntitySeq[ce.EntityId] = ce.EntitySequenceNumber;
                            }
                        }
                    }
                }

                if (op.RequestId > 0)
                {
                    ResolvePending(op);
                }
                else
                {
                    InvokeBroadcastHandlers(op);
                }
            }
        }

        private void ResolvePending(SessionOp op)
        {
            PendingRequest? pending;
            lock (_lock)
            {
                _pendingRequests.Remove(op.RequestId, out pending);
            }

            if (pending == null) return; // duplicate / stale / RequestId with no matching pending

            LogDiag($"CONFIRMED reqId={pending.RequestId} methodId={pending.MethodId}");
            if (pending.RequestId > _lastCompletedRequestId)
                _lastCompletedRequestId = pending.RequestId;
            pending.Tcs.TrySetResult(op);
        }

        private void InvokeBroadcastHandlers(SessionOp op)
        {
            List<Action<SessionOp>>? handlersCopy;
            lock (_lock)
            {
                if (!_broadcastHandlers.TryGetValue(op.EntityId, out var handlers))
                {
                    MetaLog.Warning($"[ClientDispatcher] No handlers for entityId={op.EntityId}, registered entities: {string.Join(", ", _broadcastHandlers.Keys)}");
                    return;
                }

                // Copy — handlers may unsubscribe during delivery (Dispose → RemoveBroadcastHandler)
                handlersCopy = new List<Action<SessionOp>>(handlers);
            }

            // EnterHandlerScope marks the current async context as nested inside a handler
            // invocation. The flag propagates via ExecutionContext into any RPC the handler
            // fires (including through Task.Run) — see
            // CounterServiceTests.BroadcastHandler_RpcFromBackgroundThread_ShouldNotDeadlock.
            // The re-entrant RPC reply's Drain call then bypasses dispatcher ownership.
            using var _ = _ordering.EnterHandlerScope();
            foreach (var handler in handlersCopy)
            {
                try
                {
                    handler(op);
                }
                catch (Exception ex)
                {
                    MetaLog.Error($"[ClientDispatcher] Broadcast handler error: {ex.Message}");
                }
            }
        }

        private void HandleDisconnected(TransportDisconnectReason reason)
        {
            bool transportGaveUp;
            lock (_lock)
            {
                MetaLog.Info($"[ClientDispatcher] Disconnected: {reason}, keeping {_pendingRequests.Count} pending requests for reconnect");

                // Keep _subscribedEntities — needed for re-subscribing after reconnect.
                // Clear (not Reset) preserves _nextExpected so resumed-session missed packets
                // (which continue numbering from lastAcknowledgedSequence+1) drain correctly.
                _ordering.Clear();
                IsSessionConnected = false;

                // 0.26.3+: Capture-and-reset under lock so a Reconnecting → Disconnected
                // race can't be seen by two threads simultaneously.
                transportGaveUp = _wasReconnecting && reason != TransportDisconnectReason.ClientRequested;
                _wasReconnecting = false;
            }

            // Keep pending requests - they will be re-sent on reconnect
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Disconnected, reason.ToString());

            // 0.26.3+: Transport ran out of reconnect attempts (e.g. BestHTTP
            // "No more reconnect attempt!" after Negotiation/transport errors). Emit Failed
            // as a follow-up signal so UI can show a permanent Reconnect button instead of
            // waiting for an event that the dispatcher would only emit from its own recovery
            // path (ResumeSessionAsync / RestartSessionAsync). Clean closes — server-initiated
            // or ClientRequested via DisconnectAsync — stay Disconnected only.
            if (transportGaveUp)
            {
                OnConnectionStatusChanged?.Invoke(
                    ConnectionStatus.Failed,
                    $"Transport reconnect exhausted ({reason})");
            }
        }

        private void HandleReconnecting()
        {
            MetaLog.Info("[ClientDispatcher] Transport reconnecting...");
            IsSessionConnected = false;
            lock (_lock) { _wasReconnecting = true; }
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Reconnecting, null);
        }

        private void HandleTransportReconnected()
        {
            MetaLog.Info("[ClientDispatcher] Transport reconnected, re-establishing session...");
            lock (_lock) { _wasReconnecting = false; }
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Reconnected, "Re-establishing session...");
            TriggerRecovery("transport-reconnected");
        }

        // 0.24.0+ Debounce flag so a burst of failing calls in a tight window doesn't
        // queue multiple parallel ReconnectAsync runs. ConnectSessionAsync itself is
        // idempotent (the server will happily re-bind the session on each call) but
        // multiple concurrent runs would step on each other's pending-request drain.
        private int _recoverInFlight;

        /// <summary>
        /// 0.24.0+ Single entry point for recovery — both transport-reconnect and the
        /// server-pushed RequireSessionReconnect flow into here. Interlocked debounce so
        /// concurrent triggers (which is the normal case: app-level RPC arrives between
        /// transport reconnect and SessionConnect completion → server pushes recovery
        /// while we're already recovering) don't fire parallel ConnectSessionAsync runs.
        /// </summary>
        private void TriggerRecovery(string reason)
        {
            if (System.Threading.Interlocked.Exchange(ref _recoverInFlight, 1) == 1)
            {
                MetaLog.Info($"[ClientDispatcher] Recovery already in flight ({reason}) — ignoring");
                return;
            }
            _ = RecoverAndClearFlagAsync();

            async Task RecoverAndClearFlagAsync()
            {
                try { await ReconnectAsync(); }
                finally { System.Threading.Interlocked.Exchange(ref _recoverInFlight, 0); }
            }
        }

        private void HandleRequireSessionReconnect(string reason)
        {
            // Server pushed a "session lost on server, please re-handshake" notification —
            // typical cause: SignalR auto-reconnected onto a brand-new server-side handler
            // (after server restart) that never saw SessionConnect. Run the same recovery
            // flow as a normal transport reconnect.
            MetaLog.Info($"[ClientDispatcher] RequireSessionReconnect from server: {reason}");
            IsSessionConnected = false;
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Reconnecting, reason);
            TriggerRecovery("server-push");
        }

        /// <summary>
        /// Re-establish session and re-subscribe to all entities after transport reconnect.
        /// </summary>
        public event Action<List<SubscriptionResult>>? OnSubscriptionsReclaimed;

        /// <summary>
        /// Resume the current session — re-establish with the same sessionId and
        /// recover missed packets. Called internally on transport reconnect, and
        /// can be called manually via <see cref="MetaClient.ResumeSessionAsync"/>
        /// for user-initiated "try again" after connection health timeout.
        /// </summary>
        public async Task ResumeSessionAsync()
        {
            if (!_connection.IsConnected)
                throw new InvalidOperationException("Transport is not connected. Call Connection.ConnectAsync() first.");

            await ReconnectAsync();
        }

        private async Task ReconnectAsync()
        {
            try
            {
                // Re-establish session with server, passing last known sequence for missed packet recovery.
                // Carry the ClientAppVersion captured on first connect — auto-reconnect must identify
                // the same client version, otherwise per-call config resolution would drift between
                // before / after the transport blip. Mode defaults to Resume when _sessionId is set.
                var result = await ConnectSessionAsync(_sessionId, _lastAcknowledgedSequence, _clientAppVersion);

                if (!result.Success)
                {
                    // 0.24.0+ SessionUnknown: server doesn't recognize our sessionId (typical cause:
                    // server restart without persistent state). Fire IMetaSessionRecoveryHandler so
                    // game-level code picks Reconnect / Restart / Disconnect. Default handler picks
                    // Reconnect — issue a fresh StartNew SessionConnect and re-subscribe known entities.
                    // 0.24.0+ SessionUnknown and SubscriptionReclaimFailed both route through
                    // the same recovery flow — server can't safely continue, client falls back
                    // to fresh session via IMetaSessionRecoveryHandler. Game-level callback
                    // decides Reconnect / Restart / Disconnect.
                    if (result.FailureReason == SessionConnectFailureReason.SessionUnknown
                        || result.FailureReason == SessionConnectFailureReason.SubscriptionReclaimFailed)
                    {
                        await HandleSessionLostAsync(result.Error ?? $"server reported {result.FailureReason}");
                        return;
                    }
                    MetaLog.Error($"[ClientDispatcher] Session reconnect failed: {result.Error}");
                    OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, result.Error);
                    return;
                }

                // 0.24.0+ Subscription verdicts already applied inside ConnectSessionAsync
                // (BEFORE ResendPendingRequestsAsync), so the state container is current by the
                // time pending RPCs come back. Nothing to do here — just confirm reconnect.

                MetaLog.Info("[ClientDispatcher] Reconnection complete");
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Connected, "Reconnected");
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[ClientDispatcher] Reconnection failed: {ex.Message}");
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, ex.Message);
            }
        }

        /// <summary>
        /// 0.24.0+ Server returned SessionUnknown on a Resume attempt. The previous session
        /// is gone on the server side; fail pending RPCs, ask the game what to do, and
        /// execute the chosen action (Reconnect / Restart / Disconnect).
        /// </summary>
        private async Task HandleSessionLostAsync(string reason)
        {
            MetaLog.Info($"[ClientDispatcher] Session lost on server: {reason}");

            // Capture the old sessionId + subscribed entities BEFORE we reset state.
            Guid oldSessionId;
            List<string> knownEntityIds;
            lock (_lock)
            {
                oldSessionId = _sessionId;
                knownEntityIds = new List<string>(_subscribedEntities.Keys);
                _sessionId = Guid.Empty;
                _lastAcknowledgedSequence = 0;
                IsSessionConnected = false;
            }
            Annotated = null;

            // Drop pending RPCs — they were bound to the old session and cannot be retried
            // there. Game logic catches SessionLostException on the call sites that care.
            FailAllPendingRequests($"Session lost: {reason}");

            var handler = SessionRecoveryHandler ?? new DefaultSessionRecoveryHandler();
            SessionRecoveryAction action;
            try
            {
                action = await handler.OnSessionLostAsync(new SessionLostInfo
                {
                    OldSessionId = oldSessionId,
                    KnownEntityIds = knownEntityIds,
                    Reason = reason,
                });
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[ClientDispatcher] SessionRecoveryHandler threw — defaulting to Reconnect: {ex.Message}");
                action = SessionRecoveryAction.Reconnect;
            }

            MetaLog.Info($"[ClientDispatcher] SessionRecoveryAction: {action} (oldSessionId={oldSessionId}, knownEntities={knownEntityIds.Count})");

            switch (action)
            {
                case SessionRecoveryAction.Reconnect:
                    await RecoverViaReconnectAsync(knownEntityIds);
                    break;
                case SessionRecoveryAction.Restart:
                    OnConnectionStatusChanged?.Invoke(ConnectionStatus.Disconnected, "Session restart requested");
                    try { await _connection.DisconnectAsync(); } catch { /* best effort */ }
                    // Game-side startup is expected to re-init via MetaClient.ConnectAsync.
                    break;
                case SessionRecoveryAction.Disconnect:
                    OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, "Session lost — recovery disabled");
                    try { await _connection.DisconnectAsync(); } catch { /* best effort */ }
                    break;
            }
        }

        private async Task RecoverViaReconnectAsync(List<string> knownEntityIds)
        {
            try
            {
                // Explicit StartNew: server allocates a fresh SessionId and binds.
                var fresh = await ConnectSessionAsync(Guid.Empty, 0, _clientAppVersion, SessionConnectMode.StartNew);
                if (!fresh.Success)
                {
                    MetaLog.Error($"[ClientDispatcher] StartNew after session-loss failed: {fresh.Error}");
                    OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, fresh.Error);
                    return;
                }

                // Re-subscribe to entities the player had open. Server returns fresh state
                // through Subscribe — local optimistic mutations that didn't reach the old
                // server are dropped (matches the SessionLostException already raised on
                // pending RPCs).
                Dictionary<string, string> entitiesToResubscribe;
                lock (_lock)
                    entitiesToResubscribe = new Dictionary<string, string>(_subscribedEntities);

                foreach (var entityId in knownEntityIds)
                {
                    if (!entitiesToResubscribe.TryGetValue(entityId, out var stateTypeName)) continue;
                    try
                    {
                        await _connection.SubscribeAsync(entityId, stateTypeName);
                        MetaLog.Info($"[ClientDispatcher] Re-subscribed to {entityId} on fresh session");
                    }
                    catch (Exception ex)
                    {
                        MetaLog.Error($"[ClientDispatcher] Failed to re-subscribe to {entityId}: {ex.Message}");
                    }
                }

                MetaLog.Info("[ClientDispatcher] Session recovery via Reconnect complete");
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Connected, "Reconnected (new session)");
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[ClientDispatcher] Reconnect recovery failed: {ex.Message}");
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, ex.Message);
            }
        }

        private void HandleSessionTerminated(string reason)
        {
            List<PendingRequest> pendingToFail;
            lock (_lock)
            {
                if (_terminated) return; // Already handled (transport event + RPC error can both fire)
                _terminated = true;

                MetaLog.Warning($"[ClientDispatcher] Session terminated: {reason}");
                _subscribedEntities.Clear();
                _lastKnownEntitySeq.Clear();
                _ordering.Reset(1);
                IsSessionConnected = false;

                pendingToFail = _pendingRequests.Values.ToList();
                _pendingRequests.Clear();
            }

            // Fail TCS outside lock
            foreach (var pending in pendingToFail)
            {
                pending.Tcs.TrySetException(new InvalidOperationException($"Session terminated: {reason}"));
            }

            if (reason.Contains("superseded", StringComparison.OrdinalIgnoreCase))
            {
                OnSessionSuperseded?.Invoke(reason);
            }
            else
            {
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, reason);
            }
        }

        private RpcCallRequest BuildRequest(PendingRequest pending) => new()
        {
            EntityId = pending.EntityId,
            RequestId = pending.RequestId,
            MethodId = pending.MethodId,
            Payload = pending.Payload,
            LastAcknowledgedSequence = _lastAcknowledgedSequence,
            IsCrossOptimistic = pending.IsCrossOptimistic,
            ServerTimeTicks = pending.ServerTimeTicks,
            Debug = pending.Debug
        };

        /// <summary>
        /// Fail all pending requests. Acquires lock internally.
        /// </summary>
        private void FailAllPendingRequests(string reason)
        {
            List<PendingRequest> pendingToFail;
            lock (_lock)
            {
                pendingToFail = _pendingRequests.Values.ToList();
                _pendingRequests.Clear();
            }

            foreach (var pending in pendingToFail)
            {
                pending.Tcs.TrySetException(new InvalidOperationException(reason));
            }
        }

        private void RemoveBroadcastHandler(string entityId, Action<SessionOp> handler)
        {
            lock (_lock)
            {
                if (_broadcastHandlers.TryGetValue(entityId, out var handlers))
                {
                    handlers.Remove(handler);
                }
            }
        }

        /// <summary>
        /// Number of pending requests awaiting response.
        /// </summary>
        public int PendingRequestCount { get { lock (_lock) return _pendingRequests.Count; } }

        /// <summary>
        /// 0.24.0+ Highest per-entity broadcast sequence the client has observed for the given
        /// entity. Used by generated <c>*ApiClient</c> desync diagnostics to compare against the
        /// server-stamped seq in <c>response.Debug</c>. Returns 0 when no broadcast has been
        /// observed yet (cold subscribe + no broadcast since).
        /// </summary>
        public long GetLastKnownEntitySequence(string? entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return 0;
            lock (_lock)
            {
                return _lastKnownEntitySeq.TryGetValue(entityId, out var seq) ? seq : 0;
            }
        }

        /// <summary>
        /// Reset dispatcher state for session restart (after supersede).
        /// Clears all subscriptions, handlers, buffer, and pending requests.
        /// </summary>
        public void ResetForRestart()
        {
            List<PendingRequest> pendingToFail;
            lock (_lock)
            {
                _subscribedEntities.Clear();
                _lastKnownEntitySeq.Clear();
                _broadcastHandlers.Clear();
                _ordering.Reset(1);
                pendingToFail = _pendingRequests.Values.ToList();
                _pendingRequests.Clear();
                _lastAcknowledgedSequence = 0;
                _sessionId = Guid.Empty;
                _nextRequestId = 0;
                _terminated = false;
                IsSessionConnected = false;
                _lastHealthStatus = ConnectionHealthStatus.Healthy;
            }

            foreach (var pending in pendingToFail)
            {
                pending.Tcs.TrySetException(new InvalidOperationException("Session restart"));
            }
        }

        public void Dispose()
        {
            _connection.OnBatch -= HandleBatch;
            _connection.OnDisconnected -= HandleDisconnected;
            _connection.OnSessionTerminated -= HandleSessionTerminated;
            _connection.OnReconnecting -= HandleReconnecting;
            _connection.OnReconnected -= HandleTransportReconnected;

            List<PendingRequest> pendingToFail;
            lock (_lock)
            {
                pendingToFail = _pendingRequests.Values.ToList();
                _pendingRequests.Clear();
                _broadcastHandlers.Clear();
                _ordering.Reset(1);
                _lastHealthStatus = ConnectionHealthStatus.Healthy;
            }

            foreach (var pending in pendingToFail)
            {
                pending.Tcs.TrySetException(new InvalidOperationException("ClientDispatcher disposed"));
            }
        }

        private class BroadcastSubscription : IDisposable
        {
            private readonly ClientDispatcher _dispatcher;
            private readonly string _entityId;
            private readonly Action<SessionOp> _handler;
            private bool _disposed;

            public BroadcastSubscription(ClientDispatcher dispatcher, string entityId, Action<SessionOp> handler)
            {
                _dispatcher = dispatcher;
                _entityId = entityId;
                _handler = handler;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _dispatcher.RemoveBroadcastHandler(_entityId, _handler);
                }
            }
        }

        // 0.24.0+ Handshake-tracing helper: scan the Statuses array once and report
        // (rejected, force-patch) counts for the connect-time log lines.
        private static (int rejected, int forcePatch) CountStatuses(MethodStatus[] statuses)
        {
            int rej = 0, fp = 0;
            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] == MethodStatus.Rejected) rej++;
                else if (statuses[i] == MethodStatus.ForceServerPatch) fp++;
            }
            return (rej, fp);
        }
    }
}
