using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Debug.InProcess
{
    /// <summary>
    /// In-process client connection for testing.
    /// Implements IConnection to provide the same interface as SignalRConnection.
    ///
    /// InProcess does not provide wire-level FIFO between RPC-reply delivery and
    /// broadcast observer delivery — responses can arrive in any order. Ordering
    /// is reconstructed on the client side by ClientDispatcher / OrderedDispatcher
    /// via SessionResponse.SequenceNumber (see docs/ORDERING-GUARANTEES.md).
    /// </summary>
    public class InProcessConnection : IConnection
    {
        private readonly InProcessServer _server;
        private readonly string _connectionId;
        private InProcessBroadcastSender? _broadcastSender;
        private bool _isConnected;

        public string ConnectionId => _connectionId;
        public bool IsConnected => _isConnected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        #pragma warning disable 67 // Event is never used
        public event Action? OnReconnecting;
        public event Action? OnReconnected;
        #pragma warning restore 67

        internal InProcessConnection(InProcessServer server, string connectionId)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        }

        public Task ConnectAsync()
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            _broadcastSender = new InProcessBroadcastSender(this, _server.FailureSettings);
            _server.OnClientConnected(this, _broadcastSender);
            _isConnected = true;

            MetaLog.Debug($"[InProcess] Connected: {_connectionId}");
            return Task.CompletedTask;
        }

        public async Task GracefulDisconnectAsync()
        {
            if (!_isConnected) return;
            await _server.GracefulDisconnectAsync(_connectionId);
            MetaLog.Debug($"[InProcess] Graceful disconnect: {_connectionId}");
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected)
                return;

            _isConnected = false;
            await _server.OnClientDisconnectedAsync(_connectionId);
            MetaLog.Debug($"[InProcess] Disconnected: {_connectionId}");
        }

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0)
        {
            EnsureConnected();

            var response = await _server.SessionConnectAsync(_connectionId, new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence
            });

            return new ConnectionSessionConnectResult
            {
                Success = response.Success,
                Error = response.Error,
                SessionId = response.SessionId,
                IsNewSession = response.IsNewSession,
                MissedPackets = response.MissedPackets,
                ServerTimeTicks = response.ServerTimeTicks,
                ResubscribedEntities = response.ResubscribedEntities
            };
        }

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            EnsureConnected();

            var response = await _server.SubscribeAsync(_connectionId, new SubscribeRequest
            {
                EntityId = entityId,
                StateTypeName = stateTypeName
            });

            return new ConnectionSubscribeResult
            {
                Success = response.Success,
                Error = response.Error,
                StateBytes = response.StateBytes,
                OptimisticRandomBytes = response.OptimisticRandomBytes,
                NamedRandomsBytes = response.NamedRandomsBytes,
                ConfigVersion = new MetaConfigVersion(response.ConfigMajorVersion, response.ConfigMinorVersion, response.ConfigPatchVersion)
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureConnected();

            var response = await _server.UnsubscribeAsync(_connectionId, new UnsubscribeRequest
            {
                EntityId = entityId
            });

            return response.Success;
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            EnsureConnected();
            return await _server.RpcCallAsync(_connectionId, request).ConfigureAwait(false);
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            EnsureConnected();
            return await _server.QueryCallAsync(_connectionId, request);
        }

        public Task SignalCallAsync(SignalCallRequest request)
        {
            EnsureConnected();
            return _server.SignalCallAsync(_connectionId, request);
        }

        public async Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            EnsureConnected();
            var response = await _server.SetDebugOptionsAsync(_connectionId, request);
            return response.Success;
        }

        public async Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
        {
            EnsureConnected();
            return await _server.SendDesyncReportAsync(_connectionId, request);
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureConnected();

            await _server.AcknowledgeSequenceAsync(_connectionId, new AcknowledgeRequest
            {
                SequenceNumber = sequenceNumber
            });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            EnsureConnected();
            var response = await _server.GetConfigDownloadUrlAsync(_connectionId, new ConfigDownloadUrlRequest { StateTypeName = stateTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor, ConfigPatchVersion = version.Patch });
            return response.DownloadUrl;
        }

        /// <summary>
        /// Internal: Called by InProcessBroadcastSender to deliver broadcasts.
        /// </summary>
        internal void DeliverBroadcast(SessionResponse message)
        {
            if (!_isConnected) return;

            if (message.Operations != null && message.Operations.Count > 0)
            {
                OnBatch?.Invoke(message);
            }
        }

        /// <summary>
        /// Internal: Called by InProcessBroadcastSender when session is terminated.
        /// </summary>
        internal void DeliverSessionTerminated(string reason)
        {
            if (!_isConnected) return;
            OnSessionTerminated?.Invoke(reason);
        }

        /// <summary>
        /// Internal: Simulate a connection drop (for testing).
        /// </summary>
        internal void SimulateDisconnect()
        {
            if (!_isConnected) return;

            _isConnected = false;
            OnDisconnected?.Invoke(TransportDisconnectReason.NetworkError);
        }

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected");
        }

        public void Dispose()
        {
            if (_isConnected)
            {
                DisconnectAsync().Wait();
            }
        }
    }
}
