using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client.Network
{
    /// <summary>
    /// Typed proxy that implements <see cref="IMetaHub"/> by forwarding calls to <see cref="HubConnection"/>.
    /// Uses nameof() for method names to ensure compile-time safety.
    /// </summary>
    public class MetaHubProxy : IMetaHub
    {
        private readonly HubConnection _connection;

        public MetaHubProxy(HubConnection connection)
        {
            _connection = connection;
        }

        public Task<SessionConnectResponse> SessionConnect(SessionConnectRequest request)
            => _connection.InvokeAsync<SessionConnectResponse>(nameof(SessionConnect), request);

        // 0.22.0+ phase-2 compatibility handshake. Without this override the IMetaHub DIM
        // returns a synthetic "success + empty caps" result that NEVER reaches the server —
        // the registry stays empty and every subsequent RPC fails server-side with
        // "method id N is out of range for client signature."
        public Task<RegisterClientSignatureResponse> RegisterClientSignature(RegisterClientSignatureRequest request)
            => _connection.InvokeAsync<RegisterClientSignatureResponse>(nameof(RegisterClientSignature), request);

        public Task<SubscribeResponse> Subscribe(SubscribeRequest request)
            => _connection.InvokeAsync<SubscribeResponse>(nameof(Subscribe), request);

        public Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request)
            => _connection.InvokeAsync<UnsubscribeResponse>(nameof(Unsubscribe), request);

        public Task<SessionResponse> RpcCall(RpcCallRequest request)
            => _connection.InvokeAsync<SessionResponse>(nameof(RpcCall), request);

        public Task<QueryCallResponse> QueryCall(QueryCallRequest request)
            => _connection.InvokeAsync<QueryCallResponse>(nameof(QueryCall), request);

        /// <summary>
        /// Fire-and-forget signal. Uses <c>SendAsync</c> instead of <c>InvokeAsync</c> so the
        /// client's SignalR pipeline does not await a server response.
        /// </summary>
        public Task SignalCall(SignalCallRequest request)
            => _connection.SendAsync(nameof(SignalCall), request);

        public Task<DebugOptionsResponse> SetDebugOptions(DebugOptionsRequest request)
            => _connection.InvokeAsync<DebugOptionsResponse>(nameof(SetDebugOptions), request);

        public Task<DesyncReportResponse> SendDesyncReport(DesyncReportRequest request)
            => _connection.InvokeAsync<DesyncReportResponse>(nameof(SendDesyncReport), request);

        public Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request)
            => _connection.InvokeAsync<AcknowledgeResponse>(nameof(AcknowledgeSequence), request);

        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request)
            => _connection.InvokeAsync<ConfigDownloadUrlResponse>(nameof(GetConfigDownloadUrl), request);

        public Task GracefulDisconnect()
            => _connection.InvokeAsync(nameof(GracefulDisconnect));
    }
}
