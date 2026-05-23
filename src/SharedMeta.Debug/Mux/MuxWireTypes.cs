using System.Threading.Tasks;
using SharedMeta.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Multiplexed counterpart to <see cref="IMetaHub"/>. Each method takes a
    /// <c>sessionTag</c> as its first argument — the server-side <see cref="MuxHub"/>
    /// uses the tag to route the call to a per-session <c>IMetaConnectionHandler</c>.
    /// One physical SignalR connection therefore carries many logical client sessions,
    /// which is the point of this transport — stress-test fan-out without burning a
    /// WebSocket per simulated player.
    /// <para>
    /// This is intentionally NOT a subtype of <see cref="IMetaHub"/>: client→server
    /// routing differs (tag-keyed dispatch instead of one-handler-per-connection), and
    /// keeping the surfaces separate avoids accidentally routing a Mux call through a
    /// non-multiplexing hub.
    /// </para>
    /// </summary>
    public interface IMuxMetaHub
    {
        Task<SessionConnectResponse> SessionConnect(int sessionTag, SessionConnectRequest request);
        Task<RegisterClientSignatureResponse> RegisterClientSignature(int sessionTag, RegisterClientSignatureRequest request);
        Task<SubscribeResponse> Subscribe(int sessionTag, SubscribeRequest request);
        Task<UnsubscribeResponse> Unsubscribe(int sessionTag, UnsubscribeRequest request);
        Task<SessionResponse> RpcCall(int sessionTag, RpcCallRequest request);

        /// <summary>
        /// 0.23.0+: batched variant. Server dispatches every entry in parallel through
        /// the per-tag MetaConnectionHandler and returns a matching response per entry.
        /// One SignalR frame round-trip instead of N — eliminates per-frame SignalR
        /// encode/decode overhead and the ThreadPool burst caused by N completion
        /// continuations resolving in lock-step.
        /// </summary>
        Task<BatchRpcResponse> BatchRpcCall(BatchRpcRequest request);

        Task<QueryCallResponse> QueryCall(int sessionTag, QueryCallRequest request);
        Task SignalCall(int sessionTag, SignalCallRequest request);
        Task<AcknowledgeResponse> AcknowledgeSequence(int sessionTag, AcknowledgeRequest request);
        Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(int sessionTag, ConfigDownloadUrlRequest request);
        Task GracefulDisconnect(int sessionTag);
    }

    /// <summary>
    /// Server→client receive interface for <see cref="MuxHub"/>. Mirrors
    /// <see cref="IMetaHubClient"/> but every callback carries the <c>sessionTag</c>
    /// so the client-side <see cref="MuxChannel"/> can route to the right
    /// <see cref="MuxConnection"/>.
    /// </summary>
    public interface IMuxMetaHubClient
    {
        Task ReceiveBroadcast(int sessionTag, SessionResponse message);
        Task SessionTerminated(int sessionTag, string reason);
        Task EntityDeactivating(int sessionTag, string entityId);
    }
}
