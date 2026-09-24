using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Debug.InProcess
{
    /// <summary>
    /// In-process broadcast sender for testing.
    /// Delivers broadcasts directly to InProcessConnection.
    /// Supports failure simulation (packet loss, disconnects).
    /// </summary>
    public class InProcessBroadcastSender : IBroadcastSender
    {
        private readonly InProcessConnection _connection;
        private readonly FailureSimulationSettings _failureSettings;
        private readonly ILogger _logger;

        public InProcessBroadcastSender(InProcessConnection connection, FailureSimulationSettings failureSettings, ILogger? logger = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _failureSettings = failureSettings ?? throw new ArgumentNullException(nameof(failureSettings));
            _logger = logger ?? NullLogger.Instance;
        }

        public void SendBroadcast(SessionResponse message)
        {
            // Check for disconnect simulation
            if (_failureSettings.ShouldDisconnect())
            {
                _logger.SimulatingDisconnect(_connection.ConnectionId);
                _connection.SimulateDisconnect();
                return;
            }

            // Check for packet loss simulation
            if (_failureSettings.ShouldDropBroadcast())
            {
                _logger.DroppingBroadcast(_connection.ConnectionId);
                return;
            }

            // Deliver the broadcast
            _connection.DeliverBroadcast(message);
        }

        // Not subject to the packet-loss simulation: that models the sequenced stream, which the
        // client recovers by resend. A notice has no such recovery, so dropping one would only test
        // a loss real transports do not produce on a live connection.
        public void SendNotice(SessionNotice notice)
        {
            _connection.DeliverNotice(notice);
        }

        public void SendSessionTerminated(string reason)
        {
            _connection.DeliverSessionTerminated(reason);
        }

        public void SendEntityDeactivating(string entityId)
        {
            // Not used in tests currently
            _logger.EntityDeactivating(entityId);
        }
    }
}
