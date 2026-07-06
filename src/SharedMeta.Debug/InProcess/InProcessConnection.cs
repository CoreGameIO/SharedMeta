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
        public event Action<string>? OnRequireSessionReconnect;       // InProcess: server pushes via SendRequireSessionReconnect → wired through InProcessBroadcastSender
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

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null, ulong clientSignatureHash = 0, SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0, List<SubscriptionClaim>? claimedSubscriptions = null)
        {
            EnsureConnected();

            var response = await _server.SessionConnectAsync(_connectionId, new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence,
                ClientVersion = clientAppVersion,
                ClientSignatureHash = clientSignatureHash,
                Mode = mode,
                LastCompletedRequestId = lastCompletedRequestId,
                ClaimedSubscriptions = claimedSubscriptions,
            });

            // Missed packets carry pool-backed payloads that the server retains in
            // _pendingPackets — deep-copy them so the client sees a wire-equivalent
            // independent view (server still holds the originals for further retries).
            List<SessionResponse>? missedCopies = null;
            if (response.MissedPackets != null)
            {
                missedCopies = new List<SessionResponse>(response.MissedPackets.Count);
                foreach (var packet in response.MissedPackets)
                    missedCopies.Add(CopyPooledBytesForWire(packet));
            }

            return new ConnectionSessionConnectResult
            {
                Success = response.Success,
                Error = response.Error,
                SessionId = response.SessionId,
                IsNewSession = response.IsNewSession,
                MissedPackets = missedCopies,
                ServerTimeTicks = response.ServerTimeTicks,
                Subscriptions = response.Subscriptions,
                NeedsSignatureRegistration = response.NeedsSignatureRegistration,
                ServerSignatureHash = response.ServerSignatureHash,
                Annotated = response.Annotated,
                FailureReason = response.FailureReason,
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
                ConfigVersions = response.ConfigVersions,
                EntitySequenceNumber = response.EntitySequenceNumber,
                FeatureRequirement = response.FeatureRequirement,
                AugmentedCapabilities = response.AugmentedCapabilities,
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
            var response = await _server.RpcCallAsync(_connectionId, request).ConfigureAwait(false);
            return CopyPooledBytesForWire(response);
        }

        public async Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
        {
            EnsureConnected();
            return await _server.RegisterClientSignatureAsync(_connectionId, new RegisterClientSignatureRequest
            {
                SessionId = sessionId,
                Signature = signature,
            }).ConfigureAwait(false);
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
                var copy = CopyPooledBytesForWire(message);
                OnBatch?.Invoke(copy);
            }
        }

        // InProcess transport emulates a wire boundary: the client must receive an INDEPENDENT
        // byte[]-backed copy of the SessionOp payloads, not a slice into the server's pool
        // slots (the server will eventually Release those slots when the SessionResponse is
        // acked / evicted, after which a slice-based ROM would point at recycled memory).
        // Real network transports do this implicitly via wire serialization; in-process needs
        // an explicit copy.
        // We deep-copy the SessionResponse (new Operations list, new SessionOps, byte[]-backed
        // OpBytes) instead of mutating the server's shared instance — the server still holds
        // the original (pool-backed) packet in its _pendingPackets and releases pool slots on
        // ack / eviction; the client gets its own independent view.
        private static SessionResponse CopyPooledBytesForWire(SessionResponse src)
        {
            if (src == null) return src!;
            // OpBytes is now plain ROM<byte> over GC byte[]; no pool refs to materialize.
            return src;
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


