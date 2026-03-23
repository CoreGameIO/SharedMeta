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

        Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request);

        /// <summary>
        /// Get the download URL for an entity's config.
        /// </summary>
        Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request);

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
