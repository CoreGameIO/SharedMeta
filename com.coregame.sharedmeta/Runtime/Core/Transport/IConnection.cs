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
        Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null, ulong clientSignatureHash = 0, SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0, List<SubscriptionClaim>? claimedSubscriptions = null);

        /// <summary>
        /// 0.22.0+: Phase-2 of the compatibility handshake. Called by the higher-level
        /// client when <see cref="ConnectionSessionConnectResult.NeedsSignatureRegistration"/>
        /// is true on the previous <see cref="SessionConnectAsync"/> reply. Sends the full
        /// <see cref="MetaClientSignature"/> so the server can compute and persist
        /// <see cref="ClientSignatureAnnotated"/> for this client build.
        /// <para>
        /// Default implementation throws — transports add real impls when wired into the
        /// 0.22.0 negotiation pipeline. A connection that doesn't support negotiation can
        /// remain on the default; the client higher layer will only call this when the
        /// server explicitly asked for it.
        /// </para>
        /// </summary>
        Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
            => throw new System.NotSupportedException(
                "This transport does not implement the 0.22.0 compatibility-negotiation handshake.");

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
        /// Resolve the <see cref="MetaConfigVersion"/> a <c>[StatelessMetaService]</c>'s linked
        /// config should use for this client — no entity/subscribe involved. Used by the
        /// generated <c>client.GetI{Iface}Async()</c> extension before materializing the config
        /// through the registered <c>IClientMetaConfigProvider&lt;TConfig&gt;</c>.
        /// <para>
        /// Default implementation throws — transports opt in explicitly. Mirrors the optional-
        /// feature pattern already used by <see cref="SignalCallAsync"/> /
        /// <see cref="RegisterClientSignatureAsync"/>.
        /// </para>
        /// </summary>
        Task<ResolveStatelessConfigVersionResponse> ResolveStatelessConfigVersionAsync(string configTypeName, string clientAppVersion)
            => throw new System.NotSupportedException(
                "This transport does not support stateless config version resolution.");

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
        /// 0.24.0+ Fired when the server reports its handler is unbound for this connection
        /// (typically: SignalR auto-reconnected with a new ConnectionId after a server restart
        /// that wiped session state). The dispatcher responds by running <c>SessionConnect</c>
        /// with the cached <c>SessionId</c> + <c>LastAcknowledgedSequence</c>, which both
        /// re-binds the handler AND re-fetches the signature annotation if the server hash
        /// changed. Required on every transport — implementers without server-side support
        /// declare the event but never raise it.
        /// </summary>
        event Action<string>? OnRequireSessionReconnect;

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
        /// <summary>
        /// 0.24.0+ Per-claim subscription verdicts produced on Resume. Replaces the older
        /// server-driven <c>ResubscribedEntities</c> flow — client now claims subscriptions via
        /// <c>SessionConnectRequest.ClaimedSubscriptions</c>, server returns one
        /// <see cref="SubscriptionResult"/> per claim.
        /// </summary>
        public List<SubscriptionResult>? Subscriptions { get; set; }
        /// <summary>Server version reported during handshake. Null if server does not send version info.</summary>
        public string? ServerVersion { get; set; }
        /// <summary>Minimum client version required. Populated only when connection was rejected due to version mismatch.</summary>
        public string? MinClientVersion { get; set; }
        /// <summary>Maximum client version this server supports. Populated when rejected because client is too new.</summary>
        public string? MaxClientVersion { get; set; }

        /// <summary>
        /// 0.22.0+: True when the server didn't find our <c>ClientSignatureHash</c> in its
        /// registry. The higher-level client must follow up with
        /// <see cref="IConnection.RegisterClientSignatureAsync"/> carrying the full
        /// <see cref="MetaClientSignature"/> before issuing any RPC.
        /// </summary>
        public bool NeedsSignatureRegistration { get; set; }

        /// <summary>
        /// 0.24.0+ Server signature hash, ALWAYS populated when the transport supports the
        /// 0.24.0 handshake. Drives the client-side annotation cache invalidation: client
        /// compares to <c>cachedAnnotated.ServerSignatureHash</c>; mismatch forces a phase-2
        /// re-registration even when the server already knew this clientHash.
        /// </summary>
        public ulong ServerSignatureHash { get; set; }

        /// <summary>
        /// 0.24.0+ Annotated client signature (verdict + id mapping) — supersedes
        /// <see cref="Capabilities"/>. Populated when the server already knew this clientHash
        /// AND its cached annotations are current for the reported
        /// <see cref="ServerSignatureHash"/>. Null when phase-2 is needed.
        /// </summary>
        public ClientSignatureAnnotated? Annotated { get; set; }

        /// <summary>
        /// 0.24.0+ Structured rejection reason when <see cref="Success"/> is false. See
        /// <see cref="SessionConnectFailureReason"/>. Client uses this to decide between
        /// retry-as-StartNew (<see cref="SessionConnectFailureReason.SessionUnknown"/>) and
        /// surfacing the failure to game-level logic.
        /// </summary>
        public SessionConnectFailureReason FailureReason { get; set; }
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
        /// 0.24.0+ Current entity sequence number at subscribe time. Client seeds its per-entity
        /// seq tracker with this value so the next Resume's <c>SubscriptionClaim.LastKnownEntitySequence</c>
        /// is accurate even if no broadcast has yet arrived for this entity (rare timing window
        /// between Subscribe and the first broadcast).
        /// </summary>
        public long EntitySequenceNumber { get; set; }
        /// <summary>
        /// 0.22.0+: structured rejection details when Success=false and the failure was a
        /// compatibility mismatch (e.g. <c>[MetaStateVersion(..., Breaking = true)]</c> gate).
        /// Client-side <c>ClientDispatcher.SubscribeAsync</c> rethrows this as
        /// <see cref="IncompatibleFeatureException"/> instead of a generic <see cref="InvalidOperationException"/>
        /// so game UI can surface a "update required for this feature" notification.
        /// </summary>
        public FeatureRequirement? FeatureRequirement { get; set; }

        /// <summary>
        /// 0.22.0+ Per-entity capability deltas. Server-side <c>EntityGrain</c> computes these
        /// at subscribe time from the entity's resolved config version + its bound config's
        /// <c>[MetaConfigStructureBoundary]</c> declarations. Forwarded to <c>ClientDispatcher</c>
        /// which stashes them on the per-entity <c>DispatcherNetworkAdapter</c> so generated
        /// <c>*ApiClient</c> code can consult both session-level and per-entity caps in its gate.
        /// </summary>
        public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
