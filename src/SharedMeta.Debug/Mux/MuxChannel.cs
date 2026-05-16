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

        public string? ConnectionId => _hub?.ConnectionId;
        public bool IsConnected => _hub?.State == HubConnectionState.Connected;

        /// <param name="serverUrl">Mux hub endpoint URL (e.g. <c>"http://localhost:5050/meta-mux"</c>).</param>
        /// <param name="configureBuilder">Optional callback to attach a binary protocol
        /// (e.g. <c>builder.AddMessagePackProtocol()</c> via the project's MessagePack helper).</param>
        public MuxChannel(string serverUrl, Action<IHubConnectionBuilder>? configureBuilder = null)
        {
            _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
            _configureBuilder = configureBuilder;
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
            MetaLog.Info($"[MuxChannel] connected: {_hub.ConnectionId} url={_serverUrl}");
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
