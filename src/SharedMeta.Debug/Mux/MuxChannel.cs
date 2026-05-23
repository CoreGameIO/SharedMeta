using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Client-side shared physical channel for the multiplexed transport. Owns a single
    /// SignalR <see cref="HubConnection"/> to a <see cref="MuxHub"/> endpoint and fans
    /// the incoming tag-keyed callbacks out to per-session <see cref="MuxConnection"/>
    /// instances created via <see cref="CreateConnection"/>.
    /// <para>
    /// Lifecycle: build channels up front (e.g. 10 channels for 1000 simulators), call
    /// <see cref="StartAsync"/> on each, then assign each simulated player to a channel
    /// and call <c>channel.CreateConnection(tag)</c>. Tag uniqueness is the caller's
    /// responsibility — typically <c>tag = playerIndex</c>.
    /// </para>
    /// </summary>
    public sealed class MuxChannel : IAsyncDisposable
    {
        private readonly string _serverUrl;
        private readonly Action<IHubConnectionBuilder>? _configureBuilder;
        private readonly ConcurrentDictionary<int, MuxConnection> _connections = new();
        private HubConnection? _hub;
        private IMuxMetaHub? _proxy;
        private int _nextTag = -1;

        // ── Batching (opt-in via EnableBatching = true in ctor) ──────────────────
        // When enabled, all per-MuxConnection RpcCallAsync's are routed to a per-channel
        // queue and shipped as one BatchRpcCall to the server every <FlushIntervalMs> ms
        // or when the queue reaches <MaxBatchSize>. The server dispatches each entry in
        // parallel and returns N responses in one frame. This reduces per-call SignalR
        // hub overhead and ThreadPool burst when many responses complete simultaneously.
        private readonly bool _batchingEnabled;
        private readonly int _maxBatchSize;
        private readonly int _flushIntervalMs;
        private readonly ConcurrentQueue<PendingBatchEntry> _pendingQueue = new();
        private int _correlationCounter;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<SessionResponse>> _pendingByCorrelation = new();
        private CancellationTokenSource? _batchPumpCts;
        private Task? _batchPumpTask;

        private sealed class PendingBatchEntry
        {
            public int CorrelationId;
            public int SessionTag;
            public RpcCallRequest Request = null!;
        }

        public string? ConnectionId => _hub?.ConnectionId;
        public bool IsConnected => _hub?.State == HubConnectionState.Connected;
        public bool BatchingEnabled => _batchingEnabled;

        /// <param name="serverUrl">Mux hub endpoint URL (e.g. <c>"http://localhost:5050/meta-mux"</c>).</param>
        /// <param name="configureBuilder">Optional callback to attach a binary protocol
        /// (e.g. <c>builder.AddMessagePackProtocol()</c> via the project's MessagePack helper).</param>
        /// <param name="enableBatching">If true, RPC calls accumulate per-channel and ship
        /// as one <c>BatchRpcCall</c> on a poll interval. Default false (call-per-call).</param>
        /// <param name="maxBatchSize">Hard cap on entries per batch frame. Larger = better
        /// amortization, but larger frames → more memory pressure. Default 64.</param>
        /// <param name="flushIntervalMs">How often the pump drains the queue and sends a
        /// batch. Smaller = lower added latency, larger = better batch density. Default 1ms.</param>
        public MuxChannel(string serverUrl, Action<IHubConnectionBuilder>? configureBuilder = null,
            bool enableBatching = false, int maxBatchSize = 64, int flushIntervalMs = 1)
        {
            _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
            _configureBuilder = configureBuilder;
            _batchingEnabled = enableBatching;
            _maxBatchSize = maxBatchSize;
            _flushIntervalMs = Math.Max(0, flushIntervalMs);
        }

        /// <summary>
        /// Send an RPC call through the batched path. Returns a task that completes when
        /// the per-entry result is unpacked from the batch response. Throws if batching is
        /// not enabled on this channel.
        /// </summary>
        internal Task<SessionResponse> SubmitBatchedRpcCall(int sessionTag, RpcCallRequest request)
        {
            if (!_batchingEnabled) throw new InvalidOperationException("Batching not enabled on this channel.");
            var tcs = new TaskCompletionSource<SessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var corr = Interlocked.Increment(ref _correlationCounter);
            _pendingByCorrelation[corr] = tcs;
            _pendingQueue.Enqueue(new PendingBatchEntry { CorrelationId = corr, SessionTag = sessionTag, Request = request });
            return tcs.Task;
        }

        private async Task BatchPumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_pendingQueue.IsEmpty)
                    {
                        if (_flushIntervalMs > 0)
                            await Task.Delay(_flushIntervalMs, ct).ConfigureAwait(false);
                        else
                            await Task.Yield();
                        continue;
                    }
                    // Drain up to MaxBatchSize entries.
                    var batch = new List<BatchRpcEntry>(Math.Min(_maxBatchSize, 32));
                    while (batch.Count < _maxBatchSize && _pendingQueue.TryDequeue(out var pe))
                    {
                        batch.Add(new BatchRpcEntry { CorrelationId = pe.CorrelationId, SessionTag = pe.SessionTag, Request = pe.Request });
                    }
                    if (batch.Count == 0) continue;
                    var batchReq = new BatchRpcRequest { Entries = batch };
                    // Fire-and-collect — do not await sequentially; allow multiple batches to be in-flight.
                    _ = SendBatchAsync(batchReq);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    MetaLog.Warning($"[MuxChannel] batch pump error: {ex.Message}");
                }
            }
        }

        private async Task SendBatchAsync(BatchRpcRequest batchReq)
        {
            try
            {
                if (_proxy == null) throw new InvalidOperationException("Channel not started");
                var resp = await _proxy.BatchRpcCall(batchReq).ConfigureAwait(false);
                foreach (var r in resp.Results)
                {
                    if (_pendingByCorrelation.TryRemove(r.CorrelationId, out var tcs))
                        tcs.TrySetResult(r.Response);
                }
            }
            catch (Exception ex)
            {
                // Fail every entry of this batch with the same exception.
                foreach (var entry in batchReq.Entries)
                {
                    if (_pendingByCorrelation.TryRemove(entry.CorrelationId, out var tcs))
                        tcs.TrySetException(ex);
                }
            }
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (_hub != null) throw new InvalidOperationException("Channel already started.");

            var builder = new HubConnectionBuilder()
                .WithUrl(_serverUrl)
                .WithAutomaticReconnect();
            _configureBuilder?.Invoke(builder);
            _hub = builder.Build();

            // Tag-keyed receive routing. Lookup is lock-free; each MuxConnection wires up its
            // own event subscribers via the helper hooks below — we just dispatch by tag.
            _hub.On<int, SessionResponse>(nameof(IMuxMetaHubClient.ReceiveBroadcast), (tag, msg) =>
            {
                if (_connections.TryGetValue(tag, out var c)) c.RaiseBatch(msg);
            });
            _hub.On<int, string>(nameof(IMuxMetaHubClient.SessionTerminated), (tag, reason) =>
            {
                if (_connections.TryGetValue(tag, out var c)) c.RaiseSessionTerminated(reason);
            });
            _hub.On<int, string>(nameof(IMuxMetaHubClient.EntityDeactivating), (tag, entityId) =>
            {
                if (_connections.TryGetValue(tag, out var c)) c.RaiseEntityDeactivating(entityId);
            });

            _hub.Closed += OnHubClosed;
            _hub.Reconnecting += OnHubReconnecting;
            _hub.Reconnected += OnHubReconnected;

#if DEBUG
            _hub.ServerTimeout = TimeSpan.FromMinutes(30);
            _hub.KeepAliveInterval = TimeSpan.FromMinutes(15);
#endif
            await _hub.StartAsync(ct);
            _proxy = new MuxHubProxy(_hub);
            MetaLog.Info($"[MuxChannel] connected: {_hub.ConnectionId} url={_serverUrl}{(_batchingEnabled ? $" batching=on (maxBatch={_maxBatchSize}, flushMs={_flushIntervalMs})" : string.Empty)}");

            if (_batchingEnabled)
            {
                _batchPumpCts = new CancellationTokenSource();
                _batchPumpTask = Task.Run(() => BatchPumpAsync(_batchPumpCts.Token));
            }
        }

        /// <summary>
        /// Create a logical client connection on this channel. Each gets its own tag and
        /// its own <see cref="IConnection"/> event surface, but they share the underlying
        /// SignalR socket. Pass an explicit <paramref name="tag"/> to keep deterministic
        /// session identifiers; omit for auto-incrementing.
        /// </summary>
        public MuxConnection CreateConnection(int? tag = null)
        {
            if (_proxy == null || _hub == null)
                throw new InvalidOperationException("Channel not started. Call StartAsync first.");
            var t = tag ?? Interlocked.Increment(ref _nextTag);
            var conn = new MuxConnection(this, _proxy, t, _hub.ConnectionId ?? string.Empty);
            if (!_connections.TryAdd(t, conn))
                throw new InvalidOperationException($"Tag {t} already in use on this channel.");
            return conn;
        }

        internal void RemoveConnection(int tag) => _connections.TryRemove(tag, out _);

        private Task OnHubClosed(Exception? ex)
        {
            var reason = ex == null ? TransportDisconnectReason.Unknown : TransportDisconnectReason.NetworkError;
            foreach (var c in _connections.Values) c.RaiseDisconnected(reason);
            return Task.CompletedTask;
        }

        private Task OnHubReconnecting(Exception? _)
        {
            foreach (var c in _connections.Values) c.RaiseReconnecting();
            return Task.CompletedTask;
        }

        private Task OnHubReconnected(string? _)
        {
            foreach (var c in _connections.Values) c.RaiseReconnected();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_batchPumpCts != null)
            {
                try { _batchPumpCts.Cancel(); } catch { }
                try { if (_batchPumpTask != null) await _batchPumpTask.ConfigureAwait(false); }
                catch { /* expected on cancel */ }
                _batchPumpCts.Dispose();
                _batchPumpCts = null;
                _batchPumpTask = null;
            }
            // Fail any pending entries so awaiters don't hang.
            foreach (var kv in _pendingByCorrelation)
            {
                if (_pendingByCorrelation.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new ObjectDisposedException(nameof(MuxChannel)));
            }
            if (_hub != null)
            {
                try { await _hub.StopAsync(); } catch { /* swallow on shutdown */ }
                await _hub.DisposeAsync();
                _hub = null;
            }
            _connections.Clear();
        }
    }

    /// <summary>
    /// Thin client-side proxy for <see cref="IMuxMetaHub"/> — pure InvokeAsync forwarder.
    /// </summary>
    internal sealed class MuxHubProxy : IMuxMetaHub
    {
        private readonly HubConnection _hub;
        public MuxHubProxy(HubConnection hub) => _hub = hub;

        public Task<SessionConnectResponse> SessionConnect(int t, SessionConnectRequest r)
            => _hub.InvokeAsync<SessionConnectResponse>(nameof(IMuxMetaHub.SessionConnect), t, r);

        public Task<RegisterClientSignatureResponse> RegisterClientSignature(int t, RegisterClientSignatureRequest r)
            => _hub.InvokeAsync<RegisterClientSignatureResponse>(nameof(IMuxMetaHub.RegisterClientSignature), t, r);

        public Task<SubscribeResponse> Subscribe(int t, SubscribeRequest r)
            => _hub.InvokeAsync<SubscribeResponse>(nameof(IMuxMetaHub.Subscribe), t, r);

        public Task<UnsubscribeResponse> Unsubscribe(int t, UnsubscribeRequest r)
            => _hub.InvokeAsync<UnsubscribeResponse>(nameof(IMuxMetaHub.Unsubscribe), t, r);

        public Task<SessionResponse> RpcCall(int t, RpcCallRequest r)
            => _hub.InvokeAsync<SessionResponse>(nameof(IMuxMetaHub.RpcCall), t, r);

        public Task<BatchRpcResponse> BatchRpcCall(BatchRpcRequest r)
            => _hub.InvokeAsync<BatchRpcResponse>(nameof(IMuxMetaHub.BatchRpcCall), r);

        public Task<QueryCallResponse> QueryCall(int t, QueryCallRequest r)
            => _hub.InvokeAsync<QueryCallResponse>(nameof(IMuxMetaHub.QueryCall), t, r);

        public Task SignalCall(int t, SignalCallRequest r)
            => _hub.SendAsync(nameof(IMuxMetaHub.SignalCall), t, r);

        public Task<AcknowledgeResponse> AcknowledgeSequence(int t, AcknowledgeRequest r)
            => _hub.InvokeAsync<AcknowledgeResponse>(nameof(IMuxMetaHub.AcknowledgeSequence), t, r);

        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(int t, ConfigDownloadUrlRequest r)
            => _hub.InvokeAsync<ConfigDownloadUrlResponse>(nameof(IMuxMetaHub.GetConfigDownloadUrl), t, r);

        public Task GracefulDisconnect(int t)
            => _hub.SendAsync(nameof(IMuxMetaHub.GracefulDisconnect), t);
    }
}
