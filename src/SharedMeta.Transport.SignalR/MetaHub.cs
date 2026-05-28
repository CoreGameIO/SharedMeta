using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// SignalR Hub for SharedMeta server communication.
    ///
    /// Hub instances are transient (created per method call), but Context.Items
    /// persists for the entire WebSocket connection lifetime.
    /// Handler and its Orleans observer reference are stored in Context.Items,
    /// ensuring they stay alive as long as the connection is active.
    /// </summary>
    public class MetaHub : Hub<IMetaHubClient>, IMetaHub
    {
        private const string HandlerKey = "MetaHandler";

        private readonly IMetaConnectionHandlerFactory _handlerFactory;
        private readonly IConfigDownloadUrlResolver? _configUrlResolver;
        private readonly MetaTransportOptions? _transportOptions;
        private readonly ILogger<MetaHub> _logger;

        public MetaHub(
            IMetaConnectionHandlerFactory handlerFactory,
            ILogger<MetaHub> logger,
            IConfigDownloadUrlResolver? configUrlResolver = null,
            MetaTransportOptions? transportOptions = null)
        {
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configUrlResolver = configUrlResolver;
            _transportOptions = transportOptions;
        }

        /// <summary>
        /// Get or create handler for this connection.
        /// Stored in Context.Items which persists for the WebSocket lifetime.
        /// </summary>
        protected IMetaConnectionHandler GetOrCreateHandler()
        {
            if (!Context.Items.TryGetValue(HandlerKey, out var handlerObj) || handlerObj is not IMetaConnectionHandler handler)
            {
                var broadcastSender = new SignalRBroadcastSender(Clients.Caller, _logger);
                handler = _handlerFactory.Create(Context.ConnectionId, broadcastSender);
                Context.Items[HandlerKey] = handler;
            }
            return handler;
        }

        /// <summary>
        /// Get existing handler or null.
        /// </summary>
        protected IMetaConnectionHandler? GetHandler()
        {
            if (Context.Items.TryGetValue(HandlerKey, out var handlerObj) && handlerObj is IMetaConnectionHandler handler)
            {
                return handler;
            }
            return null;
        }

        #region IMetaHub Implementation

        /// <summary>
        /// Connect to a session. Must be called first.
        /// </summary>
        public virtual async Task<SessionConnectResponse> SessionConnect(SessionConnectRequest request)
        {
            // If authenticated via JWT, use PlayerId from token claims (trusted)
            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                var claimPlayerId = Context.User.FindFirst("sub")?.Value
                                    ?? Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(claimPlayerId))
                    throw new HubException("Authenticated user has no 'sub' or NameIdentifier claim");
                request.PlayerId = claimPlayerId;
            }
            else if (_transportOptions?.RequireAuthentication == true)
            {
                throw new HubException("Authentication is required");
            }

            _logger.SessionConnectStart(Context.ConnectionId, request.PlayerId);
            try
            {
                var handler = GetOrCreateHandler();
                var response = await handler.SessionConnectAsync(request);
                _logger.SessionConnectResult(response.Success, response.SessionId);
                return response;
            }
            catch (Exception ex)
            {
                _logger.SessionConnectError(ex);
                throw;
            }
        }

        /// <summary>
        /// 0.22.0+ phase-2 compatibility handshake. Routes to the per-connection handler so
        /// the upcoming registry-backed implementation (Stage 4+) can stamp capabilities and
        /// persist the signature.
        /// </summary>
        public Task<RegisterClientSignatureResponse> RegisterClientSignature(RegisterClientSignatureRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.RegisterClientSignatureAsync(request);
        }

        /// <summary>
        /// Subscribe to an entity.
        /// </summary>
        public Task<SubscribeResponse> Subscribe(SubscribeRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.SubscribeAsync(request);
        }

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        public Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.UnsubscribeAsync(request);
        }

        /// <summary>
        /// Execute an RPC call on an entity.
        /// Returns SessionResponse with result and session-level sequence number.
        /// </summary>
        public Task<SessionResponse> RpcCall(RpcCallRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.RpcCallAsync(request);
        }

        /// <summary>
        /// Execute a query call on an entity without subscribing.
        /// </summary>
        public Task<QueryCallResponse> QueryCall(QueryCallRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.QueryCallAsync(request);
        }

        /// <summary>
        /// Fire a signal call — one-way, void. The client invoked us via <c>SendAsync</c>,
        /// so SignalR does not expect a response on the wire. We still return <see cref="Task"/>
        /// so the inner handler's async work is observable in the Hub's task scheduler; any
        /// exception is logged by the handler and never surfaces to the client.
        /// </summary>
        public Task SignalCall(SignalCallRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.SignalCallAsync(request);
        }

        /// <summary>
        /// Acknowledge received broadcasts up to a sequence number.
        /// </summary>
        public Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.AcknowledgeSequenceAsync(request);
        }

        /// <summary>
        /// Get the download URL for an entity's config.
        /// Resolved via IConfigDownloadUrlResolver (DI) — does not go through the grain chain.
        /// </summary>
        public Task<ConfigDownloadUrlResponse> GetConfigDownloadUrl(ConfigDownloadUrlRequest request)
        {
            try
            {
                var url = _configUrlResolver?.GetDownloadUrl(request.StateTypeName, new Core.MetaConfigVersion(request.ConfigMajorVersion, request.ConfigMinorVersion));
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = true, DownloadUrl = url });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ConfigDownloadUrlResponse { Success = false, Error = ex.Message });
            }
        }

        public Task<DebugOptionsResponse> SetDebugOptions(DebugOptionsRequest request)
        {
            if (_transportOptions != null && !_transportOptions.AllowDebugApi)
            {
                _logger.LogWarning("[MetaHub] SetDebugOptions rejected: AllowDebugApi=false. Run server with debug API enabled.");
                return Task.FromResult(new DebugOptionsResponse { Success = false, Error = "Debug API disabled" });
            }

            var handler = GetHandler();
            if (handler == null)
            {
                _logger.LogWarning("[MetaHub] SetDebugOptions rejected: handler not found (session not connected)");
                return Task.FromResult(new DebugOptionsResponse { Success = false, Error = "Not connected" });
            }

            _logger.LogInformation("[MetaHub] SetDebugOptions: deepDesync={DeepDesync}", request.DeepDesyncEnabled);
            return handler.SetDebugOptionsAsync(request);
        }

        public Task<DesyncReportResponse> SendDesyncReport(DesyncReportRequest request)
        {
            if (_transportOptions?.DesyncReportingEnabled != true)
                return Task.FromResult(new DesyncReportResponse { Status = "disabled" });

            var handler = GetHandler();
            if (handler == null)
                return Task.FromResult(new DesyncReportResponse { Status = "error", Error = "Not connected" });

            return handler.SendDesyncReportAsync(request);
        }

        /// <summary>
        /// Client explicitly leaves — full cleanup, session cannot be resumed.
        /// </summary>
        public async Task GracefulDisconnect()
        {
            var handler = GetHandler();
            if (handler != null)
            {
                await handler.GracefulDisconnectAsync();
            }
        }

        #endregion

        public override Task OnConnectedAsync()
        {
            _logger.HubConnected(Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var handler = GetHandler();
            if (handler != null)
            {
                _logger.HubDisconnected(Context.ConnectionId, handler.PlayerId);
                await handler.OnDisconnectedAsync();
                handler.Dispose();
                Context.Items.Remove(HandlerKey);
            }
            else
            {
                _logger.HubDisconnectedNoHandler(Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>
    /// IBroadcastSender implementation for SignalR.
    /// Sends broadcasts to the client via strongly-typed IMetaHubClient.
    /// </summary>
    internal class SignalRBroadcastSender : IBroadcastSender
    {
        private readonly IMetaHubClient _client;
        private readonly ILogger _logger;

        public SignalRBroadcastSender(IMetaHubClient client, ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void SendBroadcast(SessionResponse message)
        {
            _logger.SendBroadcast(message.Operations?.Count ?? 0);
            // Fire-and-forget, but capture errors to avoid silent failures
            _ = SendBroadcastAsync(message);
        }

        private async Task SendBroadcastAsync(SessionResponse message)
        {
            try
            {
                await _client.ReceiveBroadcast(message);
            }
            catch (Exception ex)
            {
                _logger.ErrorSendingBroadcast(ex);
            }
        }

        public void SendSessionTerminated(string reason)
        {
            _ = _client.SessionTerminated(reason);
        }

        public void SendEntityDeactivating(string entityId)
        {
            _ = _client.EntityDeactivating(entityId);
        }

        public void SendRequireSessionReconnect(string reason)
        {
            _ = _client.RequireSessionReconnect(reason);
        }
    }
}
