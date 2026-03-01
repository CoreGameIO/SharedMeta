using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// Manages per-connection state for HTTP polling connections.
    /// Each connection is identified by a client-generated connectionId.
    /// Handles inactivity cleanup when clients stop polling.
    /// </summary>
    public class HttpPollingConnectionManager : IDisposable
    {
        private readonly IMetaConnectionHandlerFactory _handlerFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HttpPollingConnectionManager> _logger;
        private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _inactivityTimeout;

        public HttpPollingConnectionManager(
            IMetaConnectionHandlerFactory handlerFactory,
            ILoggerFactory loggerFactory,
            TimeSpan? inactivityTimeout = null)
        {
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = loggerFactory.CreateLogger<HttpPollingConnectionManager>();
            _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(2);

            // Cleanup check every 30 seconds
            _cleanupTimer = new Timer(CleanupInactive, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Get or create connection state. Creates handler on first call.
        /// </summary>
        public ConnectionState GetOrCreateConnection(string connectionId)
        {
            return _connections.GetOrAdd(connectionId, id =>
            {
                var broadcastSender = new HttpPollingBroadcastSender();
                var handler = _handlerFactory.Create(id, broadcastSender);

                _logger.LogInformation("HTTP polling connection created: {ConnectionId}", id);

                return new ConnectionState(id, handler, broadcastSender);
            });
        }

        /// <summary>
        /// Get existing connection or null.
        /// </summary>
        public ConnectionState? GetConnection(string connectionId)
        {
            _connections.TryGetValue(connectionId, out var state);
            return state;
        }

        /// <summary>
        /// Touch connection (update last activity timestamp).
        /// </summary>
        public void Touch(string connectionId)
        {
            if (_connections.TryGetValue(connectionId, out var state))
            {
                state.LastActivity = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Explicitly remove a connection (client disconnect).
        /// </summary>
        public async Task RemoveConnectionAsync(string connectionId)
        {
            if (_connections.TryRemove(connectionId, out var state))
            {
                _logger.LogInformation("HTTP polling connection removed: {ConnectionId} (player={PlayerId})",
                    connectionId, state.Handler.PlayerId);
                await state.Handler.OnDisconnectedAsync();
                state.Handler.Dispose();
            }
        }

        private void CleanupInactive(object? timerState)
        {
            var cutoff = DateTime.UtcNow - _inactivityTimeout;

            foreach (var kvp in _connections)
            {
                if (kvp.Value.LastActivity < cutoff)
                {
                    _logger.LogInformation("HTTP polling connection timed out: {ConnectionId} (player={PlayerId})",
                        kvp.Key, kvp.Value.Handler.PlayerId);
                    _ = RemoveConnectionAsync(kvp.Key);
                }
            }
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();

            foreach (var kvp in _connections)
            {
                kvp.Value.Handler.OnDisconnectedAsync().Wait();
                kvp.Value.Handler.Dispose();
            }
            _connections.Clear();
        }
    }

    /// <summary>
    /// Per-connection state.
    /// </summary>
    public class ConnectionState
    {
        public string ConnectionId { get; }
        public IMetaConnectionHandler Handler { get; }
        internal HttpPollingBroadcastSender BroadcastSender { get; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        internal ConnectionState(string connectionId, IMetaConnectionHandler handler, HttpPollingBroadcastSender broadcastSender)
        {
            ConnectionId = connectionId;
            Handler = handler;
            BroadcastSender = broadcastSender;
        }
    }
}
