using Microsoft.AspNetCore.SignalR.Client;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// Client-only SignalR implementation of <see cref="IConnection"/>.
    /// Connects to a MetaHub on the server for bidirectional communication.
    /// Uses JSON protocol by default (no extra dependencies).
    /// Optionally configure the HubConnectionBuilder for MessagePack or other protocols.
    /// </summary>
    public class SignalRConnection : IConnection
    {
        private readonly string _serverUrl;
        private readonly string? _accessToken;
        private readonly Action<IHubConnectionBuilder>? _configureBuilder;
        private readonly string? _clientVersion;
        private HubConnection? _hubConnection;
        private IMetaHub? _hub;
        private string _connectionId = "";

        public string ConnectionId => _connectionId;
        public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        /// <param name="serverUrl">SignalR hub URL (e.g., "http://localhost:5000/meta").</param>
        /// <param name="accessToken">Optional JWT access token for authentication.</param>
        /// <param name="configureBuilder">Optional callback to configure HubConnectionBuilder (e.g., add MessagePack protocol).</param>
        /// <param name="clientVersion">Optional client version in "major.minor.patch" format for server-side compatibility checking.</param>
        public SignalRConnection(string serverUrl, string? accessToken = null, Action<IHubConnectionBuilder>? configureBuilder = null, string? clientVersion = null)
        {
            _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
            _accessToken = accessToken;
            _configureBuilder = configureBuilder;
            _clientVersion = clientVersion;
        }

        public async Task ConnectAsync()
        {
            var builder = new HubConnectionBuilder()
                .WithUrl(_serverUrl, options =>
                {
                    if (_accessToken != null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
                    }
                })
                .WithAutomaticReconnect(new SignalRRetryPolicy());

            _configureBuilder?.Invoke(builder);

            _hubConnection = builder.Build();

            // Register for typed broadcast events from server
            _hubConnection.On<SessionResponse>(nameof(IMetaHubClient.ReceiveBroadcast), OnReceiveBroadcast);
            _hubConnection.On<string>(nameof(IMetaHubClient.SessionTerminated), msg => OnSessionTerminated?.Invoke(msg));
            _hubConnection.On<string>(nameof(IMetaHubClient.EntityDeactivating), OnEntityDeactivating);

            // Connection lifecycle events
            _hubConnection.Closed += OnConnectionClosed;
            _hubConnection.Reconnecting += HandleReconnecting;
            _hubConnection.Reconnected += HandleReconnected;

#if DEBUG
            _hubConnection.ServerTimeout = TimeSpan.FromMinutes(30);
            _hubConnection.KeepAliveInterval = TimeSpan.FromMinutes(15);
#endif

            await _hubConnection.StartAsync();
            _connectionId = _hubConnection.ConnectionId ?? Guid.NewGuid().ToString("N")[..8];

            _hub = new MetaHubProxy(_hubConnection);

            MetaLog.Info($"[SignalR] Connected with ID: {_connectionId}");
        }

        public async Task DisconnectAsync()
        {
            MetaLog.Debug("[SignalR] DisconnectAsync called");
            if (_hubConnection != null)
            {
                await _hubConnection.StopAsync();
                await _hubConnection.DisposeAsync();
                _hubConnection = null;
                _hub = null;
            }
        }

        public async Task GracefulDisconnectAsync()
        {
            MetaLog.Debug("[SignalR] GracefulDisconnectAsync called");
            if (_hub != null && IsConnected)
            {
                try
                {
                    await _hub.GracefulDisconnect();
                }
                catch (Exception ex)
                {
                    MetaLog.Debug($"[SignalR] GracefulDisconnect failed (ok): {ex.Message}");
                }
            }
        }

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0)
        {
            EnsureConnected();

            var response = await _hub!.SessionConnect(new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence,
                ClientVersion = _clientVersion
            });

            return new ConnectionSessionConnectResult
            {
                Success = response.Success,
                Error = response.Error,
                SessionId = response.SessionId,
                IsNewSession = response.IsNewSession,
                MissedPackets = response.MissedPackets,
                ServerTimeTicks = response.ServerTimeTicks,
                ResubscribedEntities = response.ResubscribedEntities,
                ServerVersion = response.ServerVersion,
                MinClientVersion = response.MinClientVersion,
                MaxClientVersion = response.MaxClientVersion
            };
        }

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            EnsureConnected();

            var response = await _hub!.Subscribe(new SubscribeRequest
            {
                EntityId = entityId,
                StateTypeName = stateTypeName
            });

            return new ConnectionSubscribeResult
            {
                Success = response.Success,
                Error = response.Error,
                StateBytes = response.StateBytes,
                OptimisticRandomBytes = response.OptimisticRandomBytes,
                NamedRandomsBytes = response.NamedRandomsBytes,
                ConfigVersion = new MetaConfigVersion(response.ConfigMajorVersion, response.ConfigMinorVersion, response.ConfigPatchVersion)
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureConnected();

            var response = await _hub!.Unsubscribe(new UnsubscribeRequest
            {
                EntityId = entityId
            });

            return response.Success;
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            EnsureConnected();
            return await _hub!.RpcCall(request);
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            EnsureConnected();
            return await _hub!.QueryCall(request);
        }

        public Task SignalCallAsync(SignalCallRequest request)
        {
            EnsureConnected();
            return _hub!.SignalCall(request);
        }

        public async Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            EnsureConnected();
            var response = await _hub!.SetDebugOptions(request);
            return response.Success;
        }

        public async Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
        {
            EnsureConnected();
            return await _hub!.SendDesyncReport(request);
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureConnected();

            await _hub!.AcknowledgeSequence(new AcknowledgeRequest
            {
                SequenceNumber = sequenceNumber
            });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            EnsureConnected();
            var response = await _hub!.GetConfigDownloadUrl(new ConfigDownloadUrlRequest { StateTypeName = stateTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor, ConfigPatchVersion = version.Patch });
            return response.DownloadUrl;
        }

        #region Event Handlers

        private void OnReceiveBroadcast(SessionResponse message)
        {
            if (message.Operations is { Count: > 0 })
            {
                OnBatch?.Invoke(message);
            }
        }

        private void OnEntityDeactivating(string entityId)
        {
            MetaLog.Info($"[SignalR] Entity deactivating: {entityId}");
        }

        private Task OnConnectionClosed(Exception? exception)
        {
            var reason = exception != null
                ? TransportDisconnectReason.NetworkError
                : TransportDisconnectReason.ClientRequested;

            if (exception != null)
                MetaLog.Error($"[SignalR] Connection closed: {reason}", exception);
            else
                MetaLog.Info($"[SignalR] Connection closed: {reason}");

            OnDisconnected?.Invoke(reason);
            return Task.CompletedTask;
        }

        private Task HandleReconnecting(Exception? exception)
        {
            MetaLog.Info($"[SignalR] Reconnecting... ({exception?.Message})");
            OnReconnecting?.Invoke();
            return Task.CompletedTask;
        }

        private Task HandleReconnected(string? connectionId)
        {
            _connectionId = connectionId ?? _connectionId;
            MetaLog.Info($"[SignalR] Reconnected with ID: {_connectionId}");
            OnReconnected?.Invoke();
            return Task.CompletedTask;
        }

        #endregion

        private void EnsureConnected()
        {
            if (_hubConnection == null || !IsConnected || _hub == null)
                throw new InvalidOperationException("Not connected");
        }

        public void Dispose()
        {
            _hubConnection?.DisposeAsync().AsTask().Wait();
        }
    }

    /// <summary>
    /// Retry policy for automatic reconnection with exponential backoff.
    /// </summary>
    internal class SignalRRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
            return Delays[index];
        }
    }
}
