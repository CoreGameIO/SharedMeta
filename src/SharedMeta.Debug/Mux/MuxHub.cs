using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Multiplexed SignalR hub for stress-testing. One physical hub connection (one
    /// WebSocket) carries many logical client sessions identified by <c>sessionTag</c>.
    /// Each tag has its own <see cref="IMetaConnectionHandler"/> stored in
    /// <see cref="HubCallerContext.Items"/>, so the rest of the server-side pipeline
    /// (sessions, grains, broadcasts, capability negotiation) works exactly the way
    /// it does for a regular per-connection <c>MetaHub</c> client.
    /// <para>
    /// Place this hub on a separate endpoint (e.g. <c>app.MapHub&lt;MuxHub&gt;("/meta-mux")</c>)
    /// alongside the normal <c>MetaHub</c> at <c>/meta</c>. Production traffic uses the
    /// normal hub; debug/load tests opt in to <c>/meta-mux</c>.
    /// </para>
    /// <para>
    /// Lifecycle: when the underlying SignalR connection drops, every per-tag handler
    /// is disposed in <see cref="OnDisconnectedAsync"/>. A single tag can leave via
    /// <see cref="GracefulDisconnect"/> without affecting siblings on the same channel.
    /// </para>
    /// </summary>
    public class MuxHub : Hub<IMuxMetaHubClient>, IMuxMetaHub
    {
        private const string SessionsKey = "MuxSessions";

        private readonly IMetaConnectionHandlerFactory _handlerFactory;
        private readonly IConfigDownloadUrlResolver? _configUrlResolver;
        private readonly MetaTransportOptions? _transportOptions;
        private readonly ILogger<MuxHub> _logger;

        public MuxHub(
            IMetaConnectionHandlerFactory handlerFactory,
            ILogger<MuxHub> logger,
            IConfigDownloadUrlResolver? configUrlResolver = null,
            MetaTransportOptions? transportOptions = null)
        {
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configUrlResolver = configUrlResolver;
            _transportOptions = transportOptions;
        }

        private ConcurrentDictionary<int, IMetaConnectionHandler> GetSessions()
        {
            if (!Context.Items.TryGetValue(SessionsKey, out var bag)
                || bag is not ConcurrentDictionary<int, IMetaConnectionHandler> sessions)
            {
                sessions = new ConcurrentDictionary<int, IMetaConnectionHandler>();
                Context.Items[SessionsKey] = sessions;
            }
            return sessions;
        }

        private IMetaConnectionHandler GetOrCreateHandler(int sessionTag)
        {
            var sessions = GetSessions();
            return sessions.GetOrAdd(sessionTag, tag =>
            {
                // Per-tag handler with its own broadcast sender stamped with the tag — the
                // shared hub-client proxy belongs to the physical connection but routing to
                // a specific logical client happens via the tag stamp.
                var sender = new MuxBroadcastSender(Clients.Caller, tag, _logger);
                // Synthesize a connection id that bakes in the tag — handy in logs and ensures
                // grain-side connection bookkeeping doesn't collapse tags on the same socket.
                var connectionId = $"{Context.ConnectionId}#{tag}";
                return _handlerFactory.Create(connectionId, sender);
            });
        }

        private IMetaConnectionHandler? TryGetHandler(int sessionTag)
        {
            return GetSessions().TryGetValue(sessionTag, out var h) ? h : null;
        }

        public Task<SessionConnectResponse> SessionConnect(int sessionTag, SessionConnectRequest request)
        {
            if (_transportOptions?.RequireAuthentication == true)
                throw new HubException("MuxHub does not implement JWT authentication — disable RequireAuthentication or use the regular MetaHub for prod traffic.");
            var handler = GetOrCreateHandler(sessionTag);
            return handler.SessionConnectAsync(request);
        }

        public Task<RegisterClientSignatureResponse> RegisterClientSignature(int sessionTag, RegisterClientSignatureRequest request)
            => GetOrCreateHandler(sessionTag).RegisterClientSignatureAsync(request);

        public Task<SubscribeResponse> Subscribe(int sessionTag, SubscribeRequest request)
            => GetOrCreateHandler(sessionTag).SubscribeAsync(request);

        public Task<UnsubscribeResponse> Unsubscribe(int sessionTag, UnsubscribeRequest request)
            => GetOrCreateHandler(sessionTag).UnsubscribeAsync(request);

        public Task<SessionResponse> RpcCall(int sessionTag, RpcCallRequest request)
            => GetOrCreateHandler(sessionTag).RpcCallAsync(request);

        public async Task<BatchRpcResponse> BatchRpcCall(BatchRpcRequest request)
        {
            // Dispatch all entries in parallel. Each entry routes to its own per-tag
            // MetaConnectionHandler, which in turn enqueues to its SessionManagerGrain.
            // Net effect: one client→server SignalR frame contains N requests, server
            // processes them concurrently (subject to grain-level serialization per
            // SessionManagerGrain), and one server→client frame carries all N responses.
            var entries = request.Entries;
            var tasks = new Task<SessionResponse>[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                tasks[i] = GetOrCreateHandler(entry.SessionTag).RpcCallAsync(entry.Request);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            var response = new BatchRpcResponse { Results = new System.Collections.Generic.List<BatchRpcResult>(entries.Count) };
            for (int i = 0; i < entries.Count; i++)
            {
                response.Results.Add(new BatchRpcResult
                {
                    CorrelationId = entries[i].CorrelationId,
                    Response = tasks[i].Result,
                });
            }
            return response;
        }

        public Task<QueryCallResponse> QueryCall(int sessionTag, QueryCallRequest request)
            => GetOrCreateHandler(sessionTag).QueryCallAsync(request);

        public Task SignalCall(int sessionTag, SignalCallRequest request)
            => GetOrCreateHandler(sessionTag).SignalCallAsync(request);

        public Task<AcknowledgeResponse> AcknowledgeSequence(int sessionTag, AcknowledgeRequest request)
            => GetOrCreateHandler(sessionTag).AcknowledgeSequenceAsync(request);

        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(int sessionTag, ConfigDownloadUrlRequest request)
        {
            try
            {
                var url = _configUrlResolver?.GetDownloadUrl(
                    request.StateTypeName,
                    new MetaConfigVersion(request.ConfigMajorVersion, request.ConfigMinorVersion));
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = true, DownloadUrl = url });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = false, Error = ex.Message });
            }
        }

        public async Task GracefulDisconnect(int sessionTag)
        {
            var sessions = GetSessions();
            if (sessions.TryRemove(sessionTag, out var handler))
            {
                try { await handler.GracefulDisconnectAsync(); }
                finally { handler.Dispose(); }
            }
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogDebug("[MuxHub] connected: {ConnectionId}", Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // When the physical channel drops, tear down every logical session that was
            // riding it. Each handler runs its own OnDisconnectedAsync first so Orleans
            // observer leases get released cleanly before disposal.
            var sessions = GetSessions();
            foreach (var kv in sessions)
            {
                try { await kv.Value.OnDisconnectedAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[MuxHub] OnDisconnectedAsync(tag={Tag}) failed", kv.Key); }
                finally { kv.Value.Dispose(); }
            }
            sessions.Clear();
            Context.Items.Remove(SessionsKey);
            _logger.LogDebug("[MuxHub] disconnected: {ConnectionId} (tornDown={Count})", Context.ConnectionId, sessions.Count);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
