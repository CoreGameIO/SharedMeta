using System;
using System.Collections.Generic;
using System.Linq;
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
        public string ServiceName { get; init; } = "";
        public string MethodName { get; init; } = "";
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public bool IsCrossOptimistic { get; init; }
        public long ServerTimeTicks { get; init; }
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
        private long _nextRequestId;

        // Pending RPC requests awaiting response
        private readonly Dictionary<long, PendingRequest> _pendingRequests = new();

        // Last request ID that received a real entity response (TCS completed with result)
        private long _lastCompletedRequestId;

        // Session management
        private Guid _sessionId;
        private long _lastAcknowledgedSequence;

        // Broadcast buffering with ordering
        private readonly MessageBuffer _broadcastBuffer = new();

        // Broadcast suppression: prevents ProcessPendingBroadcasts from draining
        // during the window between receiving an RPC response and completing the local replay.
        // Without this, broadcasts that arrive between MarkDirectResponse and the local replay
        // can modify state, causing desyncs.
        private int _broadcastSuppressCount;

        // Guards against duplicate HandleSessionTerminated calls
        // (transport event + RPC error can both detect supersede)
        private bool _terminated;

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
        public long NextExpectedSequence { get { lock (_lock) return _broadcastBuffer.Head; } }

        /// <summary>
        /// True if there's a gap in received broadcasts.
        /// </summary>
        public bool HasSequenceGap { get { lock (_lock) return _broadcastBuffer.HasGap; } }

        public ClientDispatcher(IConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _connection.OnBatch += HandleBatch;
            _connection.OnDisconnected += HandleDisconnected;
            _connection.OnSessionTerminated += HandleSessionTerminated;
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
                throw new InvalidOperationException($"Failed to subscribe to entity '{entityId}': {result.Error}");

            lock (_lock)
            {
                _subscribedEntities[entityId] = stateTypeName ?? "";
            }

            return new ConnectResponse
            {
                StateBytes = result.StateBytes,
                CurrentSequenceNumber = 0,
                OptimisticRandomBytes = result.OptimisticRandomBytes,
                NamedRandomsBytes = result.NamedRandomsBytes,
                ConfigMajorVersion = result.ConfigVersion.Major,
                ConfigMinorVersion = result.ConfigVersion.Minor
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
                _broadcastHandlers.Remove(entityId);
            }
        }

        public Task<SessionOp> SendAsync(string entityId, RpcCall call, string? stateTypeName = null)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));
            if (call == null)
                throw new ArgumentNullException(nameof(call));

            var payloadBytes = call.Payload ?? Array.Empty<byte>();

            PendingRequest pending;
            lock (_lock)
            {
                var requestId = ++_nextRequestId;
                pending = new PendingRequest
                {
                    RequestId = requestId,
                    EntityId = entityId,
                    ServiceName = call.ServiceName,
                    MethodName = call.MethodName,
                    Payload = payloadBytes,
                    IsCrossOptimistic = call.IsCrossOptimistic,
                    ServerTimeTicks = call.ServerTimeTicks
                };
                _pendingRequests[requestId] = pending;
            }

            // Start RPC call without blocking - completion handled separately.
            _ = SendAndCompleteAsync(pending);

            LogDiag($"SEND reqId={pending.RequestId} {pending.ServiceName}.{pending.MethodName} entity={pending.EntityId}");

            return pending.Tcs.Task;
        }

        private async Task SendAndCompleteAsync(PendingRequest pending)
        {
            try
            {
                MetaLog.Debug($"[ClientDispatcher] SendAndCompleteAsync: reqId={pending.RequestId}, " +
                    $"entity={pending.EntityId}, method={pending.ServiceName}.{pending.MethodName}");

                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                LogDiag($"RECV reqId={pending.RequestId} seq={response.SequenceNumber} ops={response.Operations?.Count ?? 0} err={response.HasError}");

                MetaLog.Debug($"[ClientDispatcher] RPC response: reqId={pending.RequestId}, " +
                    $"seq={response.SequenceNumber}, opsCount={response.Operations?.Count ?? 0}, hasError={response.HasError}");

                if (response.HasError)
                {
                    LogDiag($"ERROR reqId={pending.RequestId} {response.Error}");
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

        /// <summary>
        /// True if session has been established with server.
        /// </summary>
        public bool IsSessionConnected { get; private set; }

        public async Task<SessionConnectResult> ConnectSessionAsync(Guid sessionId, long lastAcknowledgedSequence)
        {
            if (string.IsNullOrEmpty(PlayerId))
                throw new InvalidOperationException("PlayerId must be set before connecting session");

            lock (_lock)
            {
                _sessionId = sessionId;
                _lastAcknowledgedSequence = lastAcknowledgedSequence;
            }

            var result = await _connection.SessionConnectAsync(PlayerId, sessionId == Guid.Empty ? null : sessionId, lastAcknowledgedSequence);

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
                    Error = result.Error
                };
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

            // Re-send remaining pending requests (those not resolved by missed packets)
            await ResendPendingRequestsAsync();

            return new SessionConnectResult
            {
                Success = true,
                SessionId = result.SessionId,
                IsNewSession = result.IsNewSession,
                MissedPackets = result.MissedPackets,
                ServerTimeTicks = result.ServerTimeTicks,
                ResubscribedEntities = result.ResubscribedEntities
            };
        }

        /// <summary>
        /// Re-send all pending requests after reconnect.
        /// </summary>
        private Task ResendPendingRequestsAsync()
        {
            List<PendingRequest> pendingList;
            lock (_lock)
            {
                pendingList = _pendingRequests.Values.ToList();
            }

            if (pendingList.Count == 0)
                return Task.CompletedTask;

            MetaLog.Info($"[ClientDispatcher] Re-sending {pendingList.Count} pending requests after reconnect");
            LogDiag($"RESEND_ALL count={pendingList.Count} ids=[{string.Join(",", pendingList.Select(p => p.RequestId))}]");

            foreach (var pending in pendingList)
            {
                // Re-send without creating new TCS - reuse existing
                _ = ResendRequestAsync(pending);
            }

            return Task.CompletedTask;
        }

        private async Task ResendRequestAsync(PendingRequest pending)
        {
            try
            {
                LogDiag($"RESEND_START reqId={pending.RequestId} {pending.ServiceName}.{pending.MethodName}");
                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                // Top-level transport error
                if (response.HasError)
                {
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
        /// Lock is held only during drain; handlers are invoked outside lock.
        /// </summary>
        /// <returns>Number of broadcasts processed.</returns>
        public int ProcessPendingBroadcasts()
        {
            // Check client-side response timeouts FIRST — must run every frame regardless
            // of broadcast suppression, otherwise auto-retry stops while waiting for RPC replay.
            CheckConnectionHealth();

            int totalCount = 0;

            // Loop to pick up messages that handlers may have pushed during delivery
            while (true)
            {
                var buffer = new List<SessionResponse>();
                lock (_lock)
                {
                    // While broadcast processing is suppressed, don't drain.
                    // This prevents broadcasts from modifying state between receiving an RPC response
                    // and completing the local replay (which would cause desyncs).
                    if (_broadcastSuppressCount > 0)
                        return totalCount;

                    var count = _broadcastBuffer.DrainReady(buffer);
                    if (count == 0) break;
                    totalCount += count;
                }

                // Outside lock: deliver to handlers
                foreach (var response in buffer)
                {
                    DeliverResponseBroadcasts(response);
                }
            }

            return totalCount;
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
            MetaLog.Debug($"[ClientDispatcher] HandleBatch: seq={response.SequenceNumber}, opsCount={response.Operations?.Count ?? 0}");
            // ProcessServerResponse handles its own locking
            ProcessServerResponse(response);
        }

        /// <summary>
        /// Unified processing for all server responses (RPC results, deferred results, broadcasts).
        /// Categorizes ops by RequestId: matches pending RPC requests or collects as broadcasts.
        /// A single response can contain a mix of RPC results for different RequestIds and broadcasts.
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

            List<(SessionOp op, PendingRequest pending)>? resolvedRequests = null;
            List<SessionOp>? broadcastOps = null;
            bool isPureBroadcast;

            // Phase 1: Under lock — state mutations only
            lock (_lock)
            {
                // Update server clock from every response
                if (response.ServerTimeTicks > 0)
                {
                    _lastServerTimeTicks = response.ServerTimeTicks;
                    _localTimeAtLastSync = DateTime.UtcNow.Ticks;
                }

                // Categorize ops: match pending requests by RequestId, collect broadcasts
                if (response.Operations != null)
                {
                    foreach (var op in response.Operations)
                    {
                        if (op.RequestId > 0 && _pendingRequests.Remove(op.RequestId, out var pending))
                        {
                            resolvedRequests ??= new();
                            resolvedRequests.Add((op, pending));
                        }
                        else if (op.RequestId == 0)
                        {
                            broadcastOps ??= new();
                            broadcastOps.Add(op);
                        }
                        // else: RequestId > 0 but no matching pending request — ignore (duplicate or stale)
                    }
                }

                if (resolvedRequests != null)
                {
                    // Response contains RPC result(s).

                    // 1. Mark this sequence as direct FIRST.
                    //    The server bundles broadcasts with the RPC response (fast path) and
                    //    does NOT send a separate observer notification for this sequence.
                    //    Marking as direct tells the buffer to skip this sequence if a stale
                    //    broadcast duplicate arrives later.
                    if (response.SequenceNumber > 0)
                    {
                        _broadcastBuffer.MarkDirectResponse(response.SequenceNumber);
                    }

                    isPureBroadcast = false;
                }
                else
                {
                    // Pure broadcast — push whole response to buffer for ordered delivery
                    _broadcastBuffer.Push(response);
                    isPureBroadcast = true;
                }
            }

            // Phase 2: Outside lock — callbacks
            if (!isPureBroadcast)
            {
                // 2. Force-drain preceding broadcasts that are ready.
                //    MarkDirectResponse may have advanced past a gap, unblocking
                //    broadcasts that were waiting after the direct sequence.
                ForceDrainPendingBroadcasts();

                // 3. Deliver accompanying broadcast ops DIRECTLY to handlers.
                //    These ops arrived bundled with the RPC response (server fast-path
                //    bundles all broadcasts received during the RPC into one SessionResponse).
                //    They will NOT arrive again as a separate broadcast.
                //
                //    Previously, these were pushed to the buffer at the response's sequence
                //    number and then MarkDirectResponse would cause them to be skipped when
                //    a gap existed — losing broadcast ops (e.g., operations from other clients
                //    that happened during this RPC).
                if (broadcastOps is { Count: > 0 })
                {
                    foreach (var op in broadcastOps)
                    {
                        DeliverBroadcast(op.EntityId, op);
                    }
                }

                // 4. Complete the TCS(es) — caller continues with local replay.
                foreach (var (op, pending) in resolvedRequests!)
                {
                    LogDiag($"CONFIRMED reqId={pending.RequestId} {pending.ServiceName}.{pending.MethodName}");
                    MetaLog.Debug($"[ClientDispatcher] Resolved reqId={pending.RequestId}, method={pending.ServiceName}.{pending.MethodName}");
                    if (pending.RequestId > _lastCompletedRequestId)
                        _lastCompletedRequestId = pending.RequestId;
                    pending.Tcs.TrySetResult(op);
                }
            }
            else if (ImmediateMode)
            {
                // In immediate mode, process broadcasts right away
                ProcessPendingBroadcasts();
            }
        }

        /// <summary>
        /// Drain and deliver all ready broadcasts, bypassing the suppression check.
        /// Used for PrecedingBroadcasts that must be applied before local replay.
        /// Lock is held only during drain; handlers are invoked outside lock.
        /// </summary>
        private void ForceDrainPendingBroadcasts()
        {
            while (true)
            {
                var buffer = new List<SessionResponse>();
                lock (_lock)
                {
                    var count = _broadcastBuffer.DrainReady(buffer);
                    if (count == 0) break;
                }

                foreach (var response in buffer)
                {
                    DeliverResponseBroadcasts(response);
                }
            }
        }

        /// <summary>
        /// Deliver all broadcast ops from a SessionResponse to handlers by entityId.
        /// </summary>
        private void DeliverResponseBroadcasts(SessionResponse response)
        {
            if (response.Operations == null) return;

            foreach (var op in response.Operations)
            {
                DeliverBroadcast(op.EntityId, op);
            }
        }

        /// <summary>
        /// Deliver a single broadcast op to registered handlers.
        /// Snapshots handlers under lock, invokes them outside lock.
        /// </summary>
        private void DeliverBroadcast(string entityId, SessionOp op)
        {
            MetaLog.Debug($"[ClientDispatcher] DeliverBroadcast: entityId={entityId}, method={op.ServiceName}.{op.MethodName}");

            List<Action<SessionOp>>? handlersCopy;
            lock (_lock)
            {
                if (!_broadcastHandlers.TryGetValue(entityId, out var handlers))
                {
                    MetaLog.Warning($"[ClientDispatcher] No handlers for entityId={entityId}, registered entities: {string.Join(", ", _broadcastHandlers.Keys)}");
                    return;
                }

                // Copy — handlers may unsubscribe during delivery (Dispose → RemoveBroadcastHandler)
                handlersCopy = new List<Action<SessionOp>>(handlers);
            }

            MetaLog.Debug($"[ClientDispatcher] Calling {handlersCopy.Count} handlers for {entityId}");

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
            lock (_lock)
            {
                MetaLog.Info($"[ClientDispatcher] Disconnected: {reason}, keeping {_pendingRequests.Count} pending requests for reconnect");

                // Keep _subscribedEntities — needed for re-subscribing after reconnect
                _broadcastBuffer.Clear();
                IsSessionConnected = false;
            }

            // Keep pending requests - they will be re-sent on reconnect
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Disconnected, reason.ToString());
        }

        private void HandleReconnecting()
        {
            MetaLog.Info("[ClientDispatcher] Transport reconnecting...");
            IsSessionConnected = false;
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Reconnecting, null);
        }

        private void HandleTransportReconnected()
        {
            MetaLog.Info("[ClientDispatcher] Transport reconnected, re-establishing session...");
            OnConnectionStatusChanged?.Invoke(ConnectionStatus.Reconnected, "Re-establishing session...");
            _ = ReconnectAsync();
        }

        /// <summary>
        /// Re-establish session and re-subscribe to all entities after transport reconnect.
        /// </summary>
        public event Action<List<ResubscribedEntityInfo>>? OnEntitiesResubscribed;

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
                // Re-establish session with server, passing last known sequence for missed packet recovery
                var result = await ConnectSessionAsync(_sessionId, _lastAcknowledgedSequence);

                if (!result.Success)
                {
                    MetaLog.Error($"[ClientDispatcher] Session reconnect failed: {result.Error}");
                    OnConnectionStatusChanged?.Invoke(ConnectionStatus.Failed, result.Error);
                    return;
                }

                // If server already re-subscribed entities, notify listeners and skip manual re-subscribe
                if (result.ResubscribedEntities is { Count: > 0 })
                {
                    MetaLog.Info($"[ClientDispatcher] Server re-subscribed {result.ResubscribedEntities.Count} entities");
                    OnEntitiesResubscribed?.Invoke(result.ResubscribedEntities);
                }
                else
                {
                    // Server didn't re-subscribe — do it manually
                    Dictionary<string, string> entitiesToResubscribe;
                    lock (_lock)
                    {
                        entitiesToResubscribe = new Dictionary<string, string>(_subscribedEntities);
                    }

                    foreach (var (entityId, stateTypeName) in entitiesToResubscribe)
                    {
                        try
                        {
                            await _connection.SubscribeAsync(entityId, stateTypeName);
                            MetaLog.Info($"[ClientDispatcher] Re-subscribed to entity: {entityId}");
                        }
                        catch (Exception ex)
                        {
                            MetaLog.Error($"[ClientDispatcher] Failed to re-subscribe to entity {entityId}: {ex.Message}");
                        }
                    }
                }

                MetaLog.Info("[ClientDispatcher] Reconnection complete");
                OnConnectionStatusChanged?.Invoke(ConnectionStatus.Connected, "Reconnected");
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[ClientDispatcher] Reconnection failed: {ex.Message}");
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
                _broadcastBuffer.Clear();
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
            ServiceName = pending.ServiceName,
            MethodName = pending.MethodName,
            Payload = pending.Payload,
            LastAcknowledgedSequence = _lastAcknowledgedSequence,
            IsCrossOptimistic = pending.IsCrossOptimistic,
            ServerTimeTicks = pending.ServerTimeTicks
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
        /// Reset dispatcher state for session restart (after supersede).
        /// Clears all subscriptions, handlers, buffer, and pending requests.
        /// </summary>
        public void ResetForRestart()
        {
            List<PendingRequest> pendingToFail;
            lock (_lock)
            {
                _subscribedEntities.Clear();
                _broadcastHandlers.Clear();
                _broadcastBuffer.Reset(1);
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
                _broadcastBuffer.Clear();
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
    }
}
