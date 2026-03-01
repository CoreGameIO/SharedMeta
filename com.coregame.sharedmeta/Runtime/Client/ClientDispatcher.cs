using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core;
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
    /// Two processing modes:
    /// - Frame-based (default): call ProcessPendingBroadcasts() once per frame from the game loop
    /// - Immediate: set ImmediateMode = true for console apps or tests
    /// </summary>
    public class ClientDispatcher : IClientDispatcher
    {
        private readonly IConnection _connection;
        private readonly ConcurrentDictionary<string, List<Action<SessionOp>>> _broadcastHandlers = new();
        private readonly Dictionary<string, string> _subscribedEntities = new(); // entityId → stateTypeName
        private readonly object _subscriptionLock = new();
        private long _nextRequestId;

        // Pending RPC requests awaiting response
        private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();

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

        // Server time synchronization
        private long _lastServerTimeTicks;
        private long _localTimeAtLastSync;

        public event Action<ConnectionStatus, string?>? OnConnectionStatusChanged;
        public event Action<string>? OnSessionSuperseded;

        public IConnection Connection => _connection;

        /// <summary>Current session ID.</summary>
        public Guid SessionId => _sessionId;

        /// <summary>Current client-side request ID counter.</summary>
        public long CurrentRequestId => Interlocked.Read(ref _nextRequestId);

        /// <summary>Last acknowledged sequence number.</summary>
        public long LastAcknowledgedSequence => _lastAcknowledgedSequence;

        /// <summary>
        /// Approximate current server time (UTC ticks).
        /// Computed from last received server time + local elapsed delta.
        /// </summary>
        public long ServerTimeTicks =>
            _lastServerTimeTicks == 0
                ? DateTime.UtcNow.Ticks
                : _lastServerTimeTicks + (DateTime.UtcNow.Ticks - _localTimeAtLastSync);

        /// <summary>
        /// When true (default), broadcasts are processed immediately when they arrive in order.
        /// Set to false for frame-based processing, then call ProcessPendingBroadcasts() manually.
        /// </summary>
        public bool ImmediateMode
        {
            get => _broadcastBuffer.OnReadyToDrain != null;
            set => _broadcastBuffer.OnReadyToDrain = value ? () => ProcessPendingBroadcasts() : null;
        }

        /// <summary>
        /// Next expected broadcast sequence number.
        /// </summary>
        public long NextExpectedSequence => _broadcastBuffer.Head;

        /// <summary>
        /// True if there's a gap in received broadcasts.
        /// </summary>
        public bool HasSequenceGap => _broadcastBuffer.HasGap;

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

            lock (_subscriptionLock)
            {
                _subscribedEntities[entityId] = stateTypeName ?? "";
            }

            return new ConnectResponse
            {
                StateBytes = result.StateBytes,
                CurrentSequenceNumber = 0,
                OptimisticRandomBytes = result.OptimisticRandomBytes
            };
        }

        public async Task UnsubscribeAsync(string entityId)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));

            await _connection.UnsubscribeAsync(entityId);

            lock (_subscriptionLock)
            {
                _subscribedEntities.Remove(entityId);
            }


            _broadcastHandlers.TryRemove(entityId, out _);
        }

        public Task<SessionOp> SendAsync(string entityId, RpcCall call, string? stateTypeName = null)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));
            if (call == null)
                throw new ArgumentNullException(nameof(call));

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var payloadBytes = call.Payload ?? Array.Empty<byte>();

            // Create pending request with TCS
            var pending = new PendingRequest
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

            // Start SignalR call without blocking - completion handled separately
            _ = SendAndCompleteAsync(pending);

            return pending.Tcs.Task;
        }

        private async Task SendAndCompleteAsync(PendingRequest pending)
        {
            try
            {
                MetaLog.Debug($"[ClientDispatcher] SendAndCompleteAsync: reqId={pending.RequestId}, " +
                    $"entity={pending.EntityId}, method={pending.ServiceName}.{pending.MethodName}");

                // Include piggybacked acknowledgment with each request
                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                MetaLog.Debug($"[ClientDispatcher] RPC response: reqId={pending.RequestId}, " +
                    $"seq={response.SequenceNumber}, opsCount={response.Operations?.Count ?? 0}, hasError={response.HasError}");

                // Top-level transport error (server rejected before producing any ops)
                if (response.HasError)
                {
                    if (_pendingRequests.TryRemove(pending.RequestId, out _))
                    {
                        pending.Tcs.TrySetException(
                            new InvalidOperationException($"RPC call failed: {response.Error}"));
                    }
                    return;
                }

                // Unified processing: resolves any matching pending requests, pushes broadcasts
                ProcessServerResponse(response);
            }
            catch (Exception ex)
            {
                // Connection error - don't fail TCS immediately, keep for reconnect
                // Only fail if session is terminated or timeout exceeded
                MetaLog.Warning($"[ClientDispatcher] RPC call failed, keeping pending for reconnect: {ex.Message}");
            }
        }

        public IDisposable OnBroadcast(string entityId, Action<SessionOp> handler)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentNullException(nameof(entityId));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var handlers = _broadcastHandlers.GetOrAdd(entityId, _ => new List<Action<SessionOp>>());
            lock (handlers)
            {
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
            lock (_subscriptionLock)
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

            _sessionId = sessionId;
            _lastAcknowledgedSequence = lastAcknowledgedSequence;

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

            _sessionId = result.SessionId;
            IsSessionConnected = true;

            // Sync clock from connect response
            if (result.ServerTimeTicks > 0)
            {
                _lastServerTimeTicks = result.ServerTimeTicks;
                _localTimeAtLastSync = DateTime.UtcNow.Ticks;
            }

            // Process missed packets first — resolves pending requests from cached responses
            // and delivers missed broadcasts to maintain ordering
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
            var pendingList = _pendingRequests.Values.ToList();
            if (pendingList.Count == 0)
                return Task.CompletedTask;

            MetaLog.Info($"[ClientDispatcher] Re-sending {pendingList.Count} pending requests after reconnect");

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
                // Include piggybacked acknowledgment with each request
                var response = await _connection.RpcCallAsync(BuildRequest(pending));

                // Top-level transport error
                if (response.HasError)
                {
                    if (_pendingRequests.TryRemove(pending.RequestId, out _))
                    {
                        pending.Tcs.TrySetException(
                            new InvalidOperationException($"RPC call failed: {response.Error}"));
                    }
                    return;
                }

                // Unified processing: resolves any matching pending requests, pushes broadcasts
                ProcessServerResponse(response);
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[ClientDispatcher] Re-send failed for request {pending.RequestId}: {ex.Message}");
                // Still keep pending for next reconnect attempt
            }
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            _lastAcknowledgedSequence = sequenceNumber;
            await _connection.AcknowledgeSequenceAsync(sequenceNumber);
        }

        /// <inheritdoc/>
        public void SuppressBroadcasts()
        {
            Interlocked.Increment(ref _broadcastSuppressCount);
        }

        /// <inheritdoc/>
        public void ResumeBroadcasts()
        {
            if (Interlocked.Decrement(ref _broadcastSuppressCount) == 0)
            {
                ProcessPendingBroadcasts();
            }
        }

        /// <summary>
        /// Process pending broadcasts. Call this once per frame from game loop.
        /// Drains all ready broadcasts (those without gaps before them) and delivers to handlers.
        /// Thread-safe: uses a local drain buffer so concurrent and reentrant calls are safe.
        /// </summary>
        /// <returns>Number of broadcasts processed.</returns>
        public int ProcessPendingBroadcasts()
        {
            // While broadcast processing is suppressed, don't drain.
            // This prevents broadcasts from modifying state between receiving an RPC response
            // and completing the local replay (which would cause desyncs).
            if (_broadcastSuppressCount > 0)
                return 0;

            // Use a local buffer to avoid thread-safety issues.
            // In InProcess testing, observer callbacks fire synchronously on the grain thread
            // while RPC continuations run on thread pool threads — both can trigger this via
            // OnReadyToDrain. Reentrant calls (handler → Push → OnReadyToDrain) are also possible.
            // Each call gets its own buffer; DrainReady is internally synchronized via spin lock,
            // so concurrent calls get disjoint message sets.
            var buffer = new List<SessionResponse>();
            int totalCount = 0;

            // Loop to pick up messages that handlers may have pushed during delivery
            while (true)
            {
                buffer.Clear();
                var count = _broadcastBuffer.DrainReady(buffer);
                if (count == 0) break;

                totalCount += count;
                foreach (var response in buffer)
                {
                    DeliverResponseBroadcasts(response);
                }
            }

            return totalCount;
        }

        private void HandleBatch(SessionResponse response)
        {
            MetaLog.Debug($"[ClientDispatcher] HandleBatch: seq={response.SequenceNumber}, opsCount={response.Operations?.Count ?? 0}");
            ProcessServerResponse(response);
        }

        /// <summary>
        /// Unified processing for all server responses (RPC results, deferred results, broadcasts).
        /// Categorizes ops by RequestId: matches pending RPC requests or collects as broadcasts.
        /// A single response can contain a mix of RPC results for different RequestIds and broadcasts.
        /// </summary>
        private void ProcessServerResponse(SessionResponse response)
        {
            // Update server clock from every response
            if (response.ServerTimeTicks > 0)
            {
                _lastServerTimeTicks = response.ServerTimeTicks;
                _localTimeAtLastSync = DateTime.UtcNow.Ticks;
            }

            // Categorize ops: match pending requests by RequestId, collect broadcasts
            List<(SessionOp op, PendingRequest pending)>? resolvedRequests = null;
            List<SessionOp>? broadcastOps = null;

            if (response.Operations != null)
            {
                foreach (var op in response.Operations)
                {
                    if (op.RequestId > 0 && _pendingRequests.TryRemove(op.RequestId, out var pending))
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

                // 1. Push any accompanying broadcast ops to the buffer
                if (broadcastOps is { Count: > 0 })
                {
                    _broadcastBuffer.Push(new SessionResponse
                    {
                        SequenceNumber = response.SequenceNumber,
                        Operations = broadcastOps
                    });
                }

                // 2. Force-drain preceding broadcasts. MUST happen BEFORE MarkDirectResponse:
                //    if seq == head, MarkDirectResponse calls AdvancePastDirectResponses which
                //    shifts the buffer and advances head — destroying any broadcasts we just
                //    pushed at that sequence. Draining first delivers them and advances head,
                //    making the subsequent MarkDirectResponse a safe no-op (seq < head).
                ForceDrainPendingBroadcasts();

                // 3. Mark this sequence as direct (won't arrive again as broadcast)
                if (response.SequenceNumber > 0)
                {
                    _broadcastBuffer.MarkDirectResponse(response.SequenceNumber);
                }

                // 4. Drain again: MarkDirectResponse may have advanced past a gap,
                //    unblocking broadcasts that were waiting after the direct sequence.
                ForceDrainPendingBroadcasts();

                // 5. Complete the TCS(es) — caller continues with local replay
                foreach (var (op, pending) in resolvedRequests)
                {
                    MetaLog.Debug($"[ClientDispatcher] Resolved reqId={pending.RequestId}, method={pending.ServiceName}.{pending.MethodName}");
                    pending.Tcs.TrySetResult(op);
                }
            }
            else
            {
                // Pure broadcast — push whole response to buffer for ordered delivery
                _broadcastBuffer.Push(response);
            }
        }

        /// <summary>
        /// Drain and deliver all ready broadcasts, bypassing the suppression check.
        /// Used for PrecedingBroadcasts that must be applied before local replay.
        /// </summary>
        private void ForceDrainPendingBroadcasts()
        {
            var buffer = new List<SessionResponse>();
            while (true)
            {
                buffer.Clear();
                var count = _broadcastBuffer.DrainReady(buffer);
                if (count == 0) break;

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

        private void DeliverBroadcast(string entityId, SessionOp op)
        {
            MetaLog.Debug($"[ClientDispatcher] DeliverBroadcast: entityId={entityId}, method={op.ServiceName}.{op.MethodName}");

            if (!_broadcastHandlers.TryGetValue(entityId, out var handlers))
            {
                MetaLog.Warning($"[ClientDispatcher] No handlers for entityId={entityId}, registered entities: {string.Join(", ", _broadcastHandlers.Keys)}");
                return;
            }

            List<Action<SessionOp>> handlersCopy;
            lock (handlers)
            {
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
            MetaLog.Info($"[ClientDispatcher] Disconnected: {reason}, keeping {_pendingRequests.Count} pending requests for reconnect");

            // Keep _subscribedEntities — needed for re-subscribing after reconnect
            _broadcastBuffer.Clear();
            IsSessionConnected = false;

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
        /// <summary>
        /// Fired when server re-subscribed entities on reconnect.
        /// MetaClient uses this to refresh local entity states.
        /// </summary>
        public event Action<List<ResubscribedEntityInfo>>? OnEntitiesResubscribed;

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
                    lock (_subscriptionLock)
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
            MetaLog.Warning($"[ClientDispatcher] Session terminated: {reason}");
            lock (_subscriptionLock)
            {
                _subscribedEntities.Clear();
            }

            _broadcastBuffer.Clear();
            IsSessionConnected = false;

            // Session terminated - fail all pending requests
            FailAllPendingRequests($"Session terminated: {reason}");

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

        private void FailAllPendingRequests(string reason)
        {
            var pendingIds = _pendingRequests.Keys.ToList();
            foreach (var requestId in pendingIds)
            {
                if (_pendingRequests.TryRemove(requestId, out var pending))
                {
                    pending.Tcs.TrySetException(new InvalidOperationException(reason));
                }
            }
        }

        private void RemoveBroadcastHandler(string entityId, Action<SessionOp> handler)
        {
            if (_broadcastHandlers.TryGetValue(entityId, out var handlers))
            {
                lock (handlers)
                {
                    handlers.Remove(handler);
                }
            }
        }

        /// <summary>
        /// Number of pending requests awaiting response.
        /// </summary>
        public int PendingRequestCount => _pendingRequests.Count;

        /// <summary>
        /// Reset dispatcher state for session restart (after supersede).
        /// Clears all subscriptions, handlers, buffer, and pending requests.
        /// </summary>
        public void ResetForRestart()
        {
            lock (_subscriptionLock)
            {
                _subscribedEntities.Clear();
            }
            _broadcastHandlers.Clear();
            _broadcastBuffer.Reset(1);
            FailAllPendingRequests("Session restart");
            _lastAcknowledgedSequence = 0;
            _sessionId = Guid.Empty;
            Interlocked.Exchange(ref _nextRequestId, 0);
            IsSessionConnected = false;
        }

        public void Dispose()
        {
            _connection.OnBatch -= HandleBatch;
            _connection.OnDisconnected -= HandleDisconnected;
            _connection.OnSessionTerminated -= HandleSessionTerminated;
            _connection.OnReconnecting -= HandleReconnecting;
            _connection.OnReconnected -= HandleTransportReconnected;

            // Fail all pending requests on dispose
            FailAllPendingRequests("ClientDispatcher disposed");

            _broadcastHandlers.Clear();
            _broadcastBuffer.Clear();
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
