using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Transport;
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
        private readonly ILogger<MetaHub> _logger;

        public MetaHub(IMetaConnectionHandlerFactory handlerFactory, ILogger<MetaHub> logger)
        {
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Get or create handler for this connection.
        /// Stored in Context.Items which persists for the WebSocket lifetime.
        /// </summary>
        private IMetaConnectionHandler GetOrCreateHandler()
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
        private IMetaConnectionHandler? GetHandler()
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
        public async Task<SessionConnectResponse> SessionConnect(SessionConnectRequest request)
        {
            // If authenticated via JWT, use PlayerId from token claims (trusted)
            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                var claimPlayerId = Context.User.FindFirst("sub")?.Value;
                if (!string.IsNullOrEmpty(claimPlayerId))
                    request.PlayerId = claimPlayerId;
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
        /// Acknowledge received broadcasts up to a sequence number.
        /// </summary>
        public Task<AcknowledgeResponse> AcknowledgeSequence(AcknowledgeRequest request)
        {
            var handler = GetOrCreateHandler();
            return handler.AcknowledgeSequenceAsync(request);
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
    }
}
