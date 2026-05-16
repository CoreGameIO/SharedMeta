using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Per-session <see cref="IBroadcastSender"/> for the multiplexed transport.
    /// Closes over the same SignalR caller proxy that the entire <see cref="MuxHub"/>
    /// physical connection uses, but stamps every outgoing message with this
    /// session's tag so the client-side <see cref="MuxChannel"/> can dispatch it
    /// to the correct <see cref="MuxConnection"/>.
    /// </summary>
    internal sealed class MuxBroadcastSender : IBroadcastSender
    {
        private readonly IMuxMetaHubClient _client;
        private readonly int _sessionTag;
        private readonly ILogger _logger;

        public MuxBroadcastSender(IMuxMetaHubClient client, int sessionTag, ILogger logger)
        {
            _client = client ?? throw new System.ArgumentNullException(nameof(client));
            _sessionTag = sessionTag;
            _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
        }

        public void SendBroadcast(SessionResponse message)
        {
            // Fire-and-forget — mirror SignalRBroadcastSender's behaviour. The catch keeps
            // a hub fault for one tag from poisoning the shared physical connection.
            _ = SendBroadcastAsync(message);
        }

        private async System.Threading.Tasks.Task SendBroadcastAsync(SessionResponse message)
        {
            try
            {
                await _client.ReceiveBroadcast(_sessionTag, message);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "[MuxBroadcastSender] tag={Tag} ReceiveBroadcast failed", _sessionTag);
            }
        }

        public void SendSessionTerminated(string reason)
        {
            _ = _client.SessionTerminated(_sessionTag, reason);
        }

        public void SendEntityDeactivating(string entityId)
        {
            _ = _client.EntityDeactivating(_sessionTag, entityId);
        }
    }
}
