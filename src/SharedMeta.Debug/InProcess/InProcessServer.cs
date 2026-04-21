using SharedMeta.Core.Transport;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Debug.InProcess
{
    /// <summary>
    /// In-process server for testing without network transport.
    /// Manages connections and routes messages to IMetaConnectionHandler.
    /// </summary>
    public class InProcessServer
    {
        private readonly IMetaConnectionHandlerFactory _handlerFactory;
        private readonly IConfigDownloadUrlResolver? _configUrlResolver;
        private readonly Dictionary<string, InProcessConnectionState> _connections = new();
        private readonly object _lock = new();

        /// <summary>
        /// Settings for failure simulation.
        /// </summary>
        public FailureSimulationSettings FailureSettings { get; } = new();

        public InProcessServer(IMetaConnectionHandlerFactory handlerFactory, IConfigDownloadUrlResolver? configUrlResolver = null)
        {
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _configUrlResolver = configUrlResolver;
        }

        /// <summary>
        /// Create a new client connection to this server.
        /// </summary>
        public InProcessConnection CreateConnection()
        {
            var connectionId = Guid.NewGuid().ToString("N")[..8];
            var connection = new InProcessConnection(this, connectionId);
            return connection;
        }

        /// <summary>
        /// Internal: Called when client connects.
        /// </summary>
        internal void OnClientConnected(InProcessConnection connection, InProcessBroadcastSender broadcastSender)
        {
            lock (_lock)
            {
                var handler = _handlerFactory.Create(connection.ConnectionId, broadcastSender);
                _connections[connection.ConnectionId] = new InProcessConnectionState
                {
                    Connection = connection,
                    Handler = handler,
                    BroadcastSender = broadcastSender
                };
            }
        }

        /// <summary>
        /// Internal: Called when client disconnects.
        /// </summary>
        internal async Task OnClientDisconnectedAsync(string connectionId)
        {
            InProcessConnectionState? state;
            lock (_lock)
            {
                if (!_connections.TryGetValue(connectionId, out state))
                    return;
                _connections.Remove(connectionId);
            }

            await state.Handler.OnDisconnectedAsync();
            state.Handler.Dispose();
        }

        /// <summary>
        /// Internal: Handle session connect request.
        /// </summary>
        internal Task<SessionConnectResponse> SessionConnectAsync(string connectionId, SessionConnectRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.SessionConnectAsync(request);
        }

        /// <summary>
        /// Internal: Handle subscribe request.
        /// </summary>
        internal Task<SubscribeResponse> SubscribeAsync(string connectionId, SubscribeRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.SubscribeAsync(request);
        }

        /// <summary>
        /// Internal: Handle unsubscribe request.
        /// </summary>
        internal Task<UnsubscribeResponse> UnsubscribeAsync(string connectionId, UnsubscribeRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.UnsubscribeAsync(request);
        }

        /// <summary>
        /// Internal: Handle RPC call request.
        /// Returns SessionResponse with result and session-level sequence number.
        /// </summary>
        internal Task<SessionResponse> RpcCallAsync(string connectionId, RpcCallRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.RpcCallAsync(request);
        }

        /// <summary>
        /// Internal: Handle query call request (no subscription required).
        /// </summary>
        internal Task<QueryCallResponse> QueryCallAsync(string connectionId, QueryCallRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.QueryCallAsync(request);
        }

        /// <summary>
        /// Internal: Handle signal call request (fire-and-forget, no response).
        /// Completes as soon as the handler accepts the call; server execution is tracked
        /// independently on the entity grain side.
        /// </summary>
        internal Task SignalCallAsync(string connectionId, SignalCallRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.SignalCallAsync(request);
        }

        internal Task<DesyncReportResponse> SendDesyncReportAsync(string connectionId, DesyncReportRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.SendDesyncReportAsync(request);
        }

        internal Task<DebugOptionsResponse> SetDebugOptionsAsync(string connectionId, DebugOptionsRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.SetDebugOptionsAsync(request);
        }

        /// <summary>
        /// Internal: Handle graceful disconnect request.
        /// </summary>
        internal async Task GracefulDisconnectAsync(string connectionId)
        {
            var handler = GetHandler(connectionId);
            await handler.GracefulDisconnectAsync();
        }

        /// <summary>
        /// Internal: Handle acknowledge sequence request.
        /// </summary>
        internal Task<AcknowledgeResponse> AcknowledgeSequenceAsync(string connectionId, AcknowledgeRequest request)
        {
            var handler = GetHandler(connectionId);
            return handler.AcknowledgeSequenceAsync(request);
        }

        /// <summary>
        /// Internal: Handle get config download URL request.
        /// Resolved via IConfigDownloadUrlResolver (DI) — does not go through the grain chain.
        /// </summary>
        internal Task<ConfigDownloadUrlResponse> GetConfigDownloadUrlAsync(string connectionId, ConfigDownloadUrlRequest request)
        {
            try
            {
                var url = _configUrlResolver?.GetDownloadUrl(request.StateTypeName, new Core.MetaConfigVersion(request.ConfigMajorVersion, request.ConfigMinorVersion));
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = true, DownloadUrl = url });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = false, Error = ex.Message });
            }
        }

        private IMetaConnectionHandler GetHandler(string connectionId)
        {
            lock (_lock)
            {
                if (!_connections.TryGetValue(connectionId, out var state))
                    throw new InvalidOperationException($"Connection {connectionId} not found");
                return state.Handler;
            }
        }

        /// <summary>
        /// Force disconnect a specific client (for testing).
        /// </summary>
        public async Task ForceDisconnectAsync(string connectionId)
        {
            InProcessConnectionState? state;
            lock (_lock)
            {
                if (!_connections.TryGetValue(connectionId, out state))
                    return;
            }

            state.Connection.SimulateDisconnect();
            await OnClientDisconnectedAsync(connectionId);
        }

        /// <summary>
        /// Get all active connection IDs.
        /// </summary>
        public IReadOnlyList<string> GetActiveConnections()
        {
            lock (_lock)
            {
                return _connections.Keys.ToList();
            }
        }

        private class InProcessConnectionState
        {
            public required InProcessConnection Connection { get; init; }
            public required IMetaConnectionHandler Handler { get; init; }
            public required InProcessBroadcastSender BroadcastSender { get; init; }
        }
    }

    /// <summary>
    /// Settings for simulating network failures.
    /// </summary>
    public class FailureSimulationSettings
    {
        /// <summary>
        /// Probability (0-1) that a broadcast will be "lost".
        /// </summary>
        public double BroadcastLossProbability { get; set; }

        /// <summary>
        /// Probability (0-1) that the connection will be "dropped" on each operation.
        /// </summary>
        public double DisconnectProbability { get; set; }

        /// <summary>
        /// Random generator for failure simulation.
        /// </summary>
        public Random Random { get; set; } = new();

        /// <summary>
        /// Check if a broadcast should be dropped.
        /// </summary>
        public bool ShouldDropBroadcast()
        {
            return BroadcastLossProbability > 0 && Random.NextDouble() < BroadcastLossProbability;
        }

        /// <summary>
        /// Check if the connection should be dropped.
        /// </summary>
        public bool ShouldDisconnect()
        {
            return DisconnectProbability > 0 && Random.NextDouble() < DisconnectProbability;
        }
    }
}
