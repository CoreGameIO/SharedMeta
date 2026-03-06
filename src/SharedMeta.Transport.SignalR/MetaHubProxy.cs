using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// Typed proxy that implements IMetaHub by forwarding calls to HubConnection.
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

        public Task<SubscribeResponse> Subscribe(SubscribeRequest request)
            => _connection.InvokeAsync<SubscribeResponse>(nameof(Subscribe), request);

        public Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request)
            => _connection.InvokeAsync<UnsubscribeResponse>(nameof(Unsubscribe), request);

        public Task<SessionResponse> RpcCall(RpcCallRequest request)
            => _connection.InvokeAsync<SessionResponse>(nameof(RpcCall), request);

        public Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request)
            => _connection.InvokeAsync<AcknowledgeResponse>(nameof(AcknowledgeSequence), request);

        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request)
            => _connection.InvokeAsync<ConfigDownloadUrlResponse>(nameof(GetConfigDownloadUrl), request);

        public Task GracefulDisconnect()
            => _connection.InvokeAsync(nameof(GracefulDisconnect));
    }
}
