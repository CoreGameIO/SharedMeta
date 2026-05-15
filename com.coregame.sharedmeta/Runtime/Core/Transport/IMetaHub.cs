using System.Threading.Tasks;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Contract for MetaHub methods.
    /// Shared between server (MetaHub : Hub, IMetaHub) and client (typed proxy).
    /// </summary>
    public interface IMetaHub
    {
        Task<SessionConnectResponse> SessionConnect(SessionConnectRequest request);

        /// <summary>
        /// 0.22.0+: Phase-2 of the compatibility-negotiation handshake. Called when
        /// <see cref="SessionConnectResponse.NeedsSignatureRegistration"/> is true — the
        /// client follows up with the full <see cref="MetaClientSignature"/>, the server
        /// computes <see cref="ClientCapabilities"/> from it, stores in the signature
        /// registry keyed by <see cref="MetaClientSignature.SignatureHash"/>, and returns
        /// the verdict.
        /// <para>
        /// Default no-op implementation returns success with empty capabilities — used by
        /// transports that haven't been updated to the 0.22.0 handshake yet and by tests
        /// that don't exercise negotiation.
        /// </para>
        /// </summary>
        Task<RegisterClientSignatureResponse> RegisterClientSignature(RegisterClientSignatureRequest request)
            => Task.FromResult(new RegisterClientSignatureResponse { Success = true, Capabilities = new ClientCapabilities() });

        Task<SubscribeResponse> Subscribe(SubscribeRequest request);
        Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request);

        /// <summary>
        /// Execute an RPC call on an entity.
        /// Returns SessionResponse containing the unified result.
        /// </summary>
        Task<SessionResponse> RpcCall(RpcCallRequest request);

        /// <summary>
        /// Execute a query call on an entity without subscribing.
        /// Lightweight read-only request/response — no state sync, no broadcasts.
        /// </summary>
        Task<QueryCallResponse> QueryCall(QueryCallRequest request);

        /// <summary>
        /// Fire a signal call — one-way, void return. Server executes read-only on the target
        /// entity grain; no response is sent back. Clients typically invoke this via
        /// <c>HubConnection.SendAsync</c> rather than <c>InvokeAsync</c> to avoid awaiting
        /// any ack from the SignalR pipeline.
        /// The server-side hub method still returns <c>Task</c> so both calling paths are supported.
        /// </summary>
        Task SignalCall(SignalCallRequest request);

        Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request);

        /// <summary>
        /// Get the download URL for an entity's config.
        /// </summary>
        Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request);

        /// <summary>
        /// Set debug options for this session (deep desync, etc.).
        /// Server may ignore if debug API is disabled.
        /// </summary>
        Task<DebugOptionsResponse> SetDebugOptions(DebugOptionsRequest request);

        /// <summary>
        /// Send a deep desync follow-up report (client patch bytes).
        /// Requires server-side DesyncReportingEnabled.
        /// </summary>
        Task<DesyncReportResponse> SendDesyncReport(DesyncReportRequest request);

        /// <summary>
        /// Graceful disconnect: client explicitly leaves. Session cannot be resumed.
        /// </summary>
        Task GracefulDisconnect();
    }

    /// <summary>
    /// Contract for client-side Hub events (server -> client).
    /// </summary>
    public interface IMetaHubClient
    {
        /// <summary>
        /// Receive a broadcast (state change) from the server.
        /// Uses same SessionResponse structure as direct RPC responses.
        /// For broadcasts, SessionSequenceNumber = -1.
        /// </summary>
        Task ReceiveBroadcast(SessionResponse message);

        Task SessionTerminated(string reason);
        Task EntityDeactivating(string entityId);
    }
}
