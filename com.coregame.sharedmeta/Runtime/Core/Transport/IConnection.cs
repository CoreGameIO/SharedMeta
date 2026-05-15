using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Low-level connection interface for raw message transport.
    /// Uses direct return methods for operations and events for broadcasts.
    /// One connection per player/client.
    /// </summary>
    public interface IConnection : IDisposable
    {
        /// <summary>
        /// Unique identifier for this connection.
        /// </summary>
        string ConnectionId { get; }

        /// <summary>
        /// Whether the connection is open.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        Task ConnectAsync();

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Send graceful disconnect to the server before closing.
        /// Tells the server the client is intentionally leaving (no reconnect expected).
        /// </summary>
        Task GracefulDisconnectAsync();

        /// <summary>
        /// Connect to a session on the server.
        /// <paramref name="clientAppVersion"/> (e.g. <c>"1.4.3"</c>) — when set, stamped on
        /// <see cref="SessionConnectRequest.ClientVersion"/> and used server-side as the default
        /// <c>CallerClientVersion</c> for every RPC and subscribe over this connection. Required
        /// for per-client config branch resolution (<c>[MetaConfigVersion]</c> rules) and for
        /// the strict server-side contract that arrives in upcoming releases.
        /// </summary>
        Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null);

        /// <summary>
        /// Subscribe to an entity.
        /// </summary>
        Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName);

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        Task<bool> UnsubscribeAsync(string entityId);

        /// <summary>
        /// Execute an RPC call on an entity.
        /// Returns SessionResponse with Operations list containing
        /// preceding broadcasts and the RPC response.
        /// </summary>
        Task<SessionResponse> RpcCallAsync(RpcCallRequest request);

        /// <summary>
        /// Execute a query call on an entity without subscribing.
        /// Lightweight read-only request/response — no state sync, no broadcasts.
        /// </summary>
        Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request);

        /// <summary>
        /// Fire a signal call on an entity — one-way, void return. Completes as soon as the
        /// message is handed off to the wire; the transport never awaits server execution and
        /// never surfaces a per-call error. Server-side errors are logged, not propagated.
        /// Bypasses the RequestId / auto-retry / connection-health machinery entirely.
        /// Default throws — transports that support signals override.
        /// </summary>
        Task SignalCallAsync(SignalCallRequest request)
            => throw new System.NotSupportedException(
                "This transport does not support fire-and-forget signals.");

        /// <summary>
        /// Set debug options for this session (e.g., enable deep desync detection).
        /// Server may ignore if debug API is disabled in production.
        /// </summary>
        Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request);

        /// <summary>
        /// Send a desync follow-up report (client patch bytes) for server-side analysis.
        /// Server stores the diff in DesyncReportGrain when DesyncReportingEnabled is true.
        /// </summary>
        Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request);

        /// <summary>
        /// Acknowledge received broadcasts up to a sequence number.
        /// </summary>
        Task AcknowledgeSequenceAsync(long sequenceNumber);

        /// <summary>
        /// Request the download URL for a config from the server.
        /// Returns null if no config or no download URL configured.
        /// </summary>
        Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version);

        /// <summary>
        /// Fired when a SessionResponse is received from the server (observer push path).
        /// The response is the atomic unit — one SequenceNumber per response.
        /// </summary>
        event Action<SessionResponse>? OnBatch;

        /// <summary>
        /// Fired when the session is terminated by the server.
        /// </summary>
        event Action<string>? OnSessionTerminated;

        /// <summary>
        /// Fired when the connection is lost.
        /// </summary>
        event Action<TransportDisconnectReason>? OnDisconnected;

        /// <summary>
        /// Fired when the transport is attempting to reconnect (e.g. SignalR auto-reconnect).
        /// </summary>
        event Action? OnReconnecting;

        /// <summary>
        /// Fired when the transport has successfully reconnected.
        /// The session must be re-established by the higher layer (ClientDispatcher).
        /// </summary>
        event Action? OnReconnected;
    }

    /// <summary>
    /// Result of session connect operation.
    /// </summary>
    public class ConnectionSessionConnectResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public Guid SessionId { get; set; }
        public bool IsNewSession { get; set; }
        public List<SessionResponse> MissedPackets { get; set; } = new();
        public long ServerTimeTicks { get; set; }
        public List<ResubscribedEntityInfo>? ResubscribedEntities { get; set; }
        /// <summary>Server version reported during handshake. Null if server does not send version info.</summary>
        public string? ServerVersion { get; set; }
        /// <summary>Minimum client version required. Populated only when connection was rejected due to version mismatch.</summary>
        public string? MinClientVersion { get; set; }
        /// <summary>Maximum client version this server supports. Populated when rejected because client is too new.</summary>
        public string? MaxClientVersion { get; set; }
    }

    /// <summary>
    /// Result of subscribe operation.
    /// </summary>
    public class ConnectionSubscribeResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        public byte[]? OptimisticRandomBytes { get; set; }
        public byte[]? NamedRandomsBytes { get; set; }
        public MetaConfigVersion ConfigVersion { get; set; }
        /// <summary>
        /// 0.22.0+: structured rejection details when Success=false and the failure was a
        /// compatibility mismatch (e.g. <c>[MetaStateVersion(..., Breaking = true)]</c> gate).
        /// Client-side <c>ClientDispatcher.SubscribeAsync</c> rethrows this as
        /// <see cref="IncompatibleFeatureException"/> instead of a generic <see cref="InvalidOperationException"/>
        /// so game UI can surface a "update required for this feature" notification.
        /// </summary>
        public FeatureRequirement? FeatureRequirement { get; set; }
    }
}
