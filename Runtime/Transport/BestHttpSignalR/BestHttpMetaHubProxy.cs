#nullable enable
using System;
using System.Threading.Tasks;
using BestHTTP.SignalRCore;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.BestHttp
{
    /// <summary>
    /// Typed proxy that implements <see cref="IMetaHub"/> by forwarding calls
    /// to a BestHTTP <see cref="HubConnection"/>.
    /// Wraps BestHTTP's IFuture-based API into Task-based async.
    /// </summary>
    internal class BestHttpMetaHubProxy : IMetaHub
    {
        private readonly HubConnection _hub;

        public BestHttpMetaHubProxy(HubConnection hub)
        {
            _hub = hub;
        }

        public Task<SessionConnectResponse> SessionConnect(SessionConnectRequest request)
            => InvokeAsync<SessionConnectResponse>(nameof(SessionConnect), request);

        public Task<SubscribeResponse> Subscribe(SubscribeRequest request)
            => InvokeAsync<SubscribeResponse>(nameof(Subscribe), request);

        public Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request)
            => InvokeAsync<UnsubscribeResponse>(nameof(Unsubscribe), request);

        public Task<SessionResponse> RpcCall(RpcCallRequest request)
            => InvokeAsync<SessionResponse>(nameof(RpcCall), request);

        public Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request)
            => InvokeAsync<AcknowledgeResponse>(nameof(AcknowledgeSequence), request);

        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request)
            => InvokeAsync<ConfigDownloadUrlResponse>(nameof(GetConfigDownloadUrl), request);

        public Task GracefulDisconnect()
            => InvokeAsync<object>(nameof(GracefulDisconnect));

        private Task<T> InvokeAsync<T>(string method, params object[] args)
        {
            var tcs = new TaskCompletionSource<T>();
            _hub.Invoke<T>(method, args)
                .OnSuccess(result => tcs.TrySetResult(result))
                .OnError(error => tcs.TrySetException(
                    new Exception($"Hub invoke '{method}' failed: {error.Message}", error)));
            return tcs.Task;
        }
    }
}
