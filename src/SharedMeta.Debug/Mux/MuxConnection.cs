using System;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Logical <see cref="IConnection"/> riding on a shared <see cref="MuxChannel"/>.
    /// One MuxConnection per simulated player; many of them share the same SignalR
    /// socket. Every call forwards to the channel's hub proxy with this connection's
    /// <c>SessionTag</c>; receive events are raised by the channel after it routes
    /// inbound tag-keyed messages back to us.
    /// <para>
    /// <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/> are no-ops at the
    /// physical-channel level — the channel is started/stopped by the test runner.
    /// The "graceful disconnect" path still informs the server-side
    /// <see cref="MuxHub"/> so it can tear down the per-tag handler explicitly.
    /// </para>
    /// </summary>
    public sealed class MuxConnection : IConnection
    {
        private readonly MuxChannel _channel;
        private readonly IMuxMetaHub _hub;
        private readonly int _sessionTag;
        private readonly string _connectionId;
        private bool _disposed;

        public int SessionTag => _sessionTag;
        public string ConnectionId => _connectionId;
        public bool IsConnected => _channel.IsConnected && !_disposed;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<string>? OnRequireSessionReconnect;       // Mux: not raised yet (no server-restart simulation)
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        internal MuxConnection(MuxChannel channel, IMuxMetaHub hub, int sessionTag, string physicalConnectionId)
        {
            _channel = channel;
            _hub = hub;
            _sessionTag = sessionTag;
            // Composite id mirrors server-side handler key — useful in logs and avoids two
            // tags on the same socket collapsing onto the same connection identifier.
            _connectionId = $"{physicalConnectionId}#{sessionTag}";
        }

        public Task ConnectAsync() => Task.CompletedTask;

        public Task DisconnectAsync()
        {
            // Best-effort: most tests dispose the whole channel; this path covers a single
            // player closing while siblings keep going.
            if (!_disposed && _channel.IsConnected)
                return _hub.GracefulDisconnect(_sessionTag);
            return Task.CompletedTask;
        }

        public Task GracefulDisconnectAsync()
            => _channel.IsConnected ? _hub.GracefulDisconnect(_sessionTag) : Task.CompletedTask;

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null, ulong clientSignatureHash = 0, SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0, List<SubscriptionClaim>? claimedSubscriptions = null)
        {
            var resp = await _hub.SessionConnect(_sessionTag, new SessionConnectRequest
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
            return new ConnectionSessionConnectResult
            {
                Success = resp.Success,
                Error = resp.Error,
                SessionId = resp.SessionId,
                IsNewSession = resp.IsNewSession,
                MissedPackets = resp.MissedPackets ?? new(),
                ServerTimeTicks = resp.ServerTimeTicks,
                Subscriptions = resp.Subscriptions,
                ServerVersion = resp.ServerVersion,
                MinClientVersion = resp.MinClientVersion,
                MaxClientVersion = resp.MaxClientVersion,
                NeedsSignatureRegistration = resp.NeedsSignatureRegistration,
                ServerSignatureHash = resp.ServerSignatureHash,
                Annotated = resp.Annotated,
                FailureReason = resp.FailureReason,
            };
        }

        public Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
            => _hub.RegisterClientSignature(_sessionTag, new RegisterClientSignatureRequest
            {
                SessionId = sessionId,
                Signature = signature,
            });

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            var resp = await _hub.Subscribe(_sessionTag, new SubscribeRequest
            {
                EntityId = entityId,
                StateTypeName = stateTypeName,
            });
            return new ConnectionSubscribeResult
            {
                Success = resp.Success,
                Error = resp.Error,
                StateBytes = resp.StateBytes ?? Array.Empty<byte>(),
                OptimisticRandomBytes = resp.OptimisticRandomBytes,
                NamedRandomsBytes = resp.NamedRandomsBytes,
                ConfigVersions = resp.ConfigVersions,
                FeatureRequirement = resp.FeatureRequirement,
                AugmentedCapabilities = resp.AugmentedCapabilities,
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            var resp = await _hub.Unsubscribe(_sessionTag, new UnsubscribeRequest { EntityId = entityId });
            return resp.Success;
        }

        public Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
            => _channel.BatchingEnabled
                ? _channel.SubmitBatchedRpcCall(_sessionTag, request)
                : _hub.RpcCall(_sessionTag, request);

        public Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
            => _hub.QueryCall(_sessionTag, request);

        public Task SignalCallAsync(SignalCallRequest request)
            => _hub.SignalCall(_sessionTag, request);

        public Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            // MuxHub intentionally does not bridge SetDebugOptions / DesyncReport — they are
            // debug-time features on the SAME path the load test exercises. Tests that need
            // these should use the regular MetaHub.
            return Task.FromResult(false);
        }

        public Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
            => Task.FromResult(new DesyncReportResponse { Status = "disabled" });

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            await _hub.AcknowledgeSequence(_sessionTag, new AcknowledgeRequest { SequenceNumber = sequenceNumber });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            var resp = await _hub.GetConfigDownloadUrl(_sessionTag, new ConfigDownloadUrlRequest
            {
                StateTypeName = stateTypeName,
                ConfigMajorVersion = version.Major,
                ConfigMinorVersion = version.Minor,
            });
            return resp.DownloadUrl;
        }

        // ── Channel-callback hooks (invoked by MuxChannel on inbound dispatch) ───────

        internal void RaiseBatch(SessionResponse msg) => OnBatch?.Invoke(msg);
        internal void RaiseSessionTerminated(string reason) => OnSessionTerminated?.Invoke(reason);
        internal void RaiseEntityDeactivating(string entityId) { /* no IConnection event for this — kept hook for parity if added later */ }
        internal void RaiseDisconnected(TransportDisconnectReason reason) => OnDisconnected?.Invoke(reason);
        internal void RaiseReconnecting() => OnReconnecting?.Invoke();
        internal void RaiseReconnected() => OnReconnected?.Invoke();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.RemoveConnection(_sessionTag);
        }
    }
}


