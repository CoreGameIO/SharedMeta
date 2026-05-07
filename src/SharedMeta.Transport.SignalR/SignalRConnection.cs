using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// SignalR implementation of IConnection.
    /// Connects to a MetaHub on the server for bidirectional communication.
    /// Uses typed proxy (MetaHubProxy) for Hub method calls.
    /// SignalR handles serialization (JSON by default).
    /// </summary>
    public class SignalRConnection : IConnection
    {
        private readonly string _serverUrl;
        private readonly string? _accessToken;
        private HubConnection? _hubConnection;
        private IMetaHub? _hub;
        private string _connectionId = "";

        public string ConnectionId => _connectionId;
        public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

        // Events for async operations (broadcasts from server)
        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        /// <param name="serverUrl">SignalR hub URL.</param>
        /// <param name="accessToken">Optional JWT access token for authentication.</param>
        public SignalRConnection(string serverUrl, string? accessToken = null)
        {
            _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
            _accessToken = accessToken;
        }

        public async Task ConnectAsync()
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(_serverUrl, options =>
                {
                    if (_accessToken != null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
                    }
                })
                .WithAutomaticReconnect(new SignalRRetryPolicy())
                .AddMetaMessagePackProtocol()
                .Build();

            // Register for typed broadcast events from server
            _hubConnection.On<SessionResponse>(nameof(IMetaHubClient.ReceiveBroadcast), OnReceiveBroadcast);
            _hubConnection.On<string>(nameof(IMetaHubClient.SessionTerminated), msg => OnSessionTerminated?.Invoke(msg));
            _hubConnection.On<string>(nameof(IMetaHubClient.EntityDeactivating), OnEntityDeactivating);

            // Connection lifecycle events
            _hubConnection.Closed += OnConnectionClosed;
            _hubConnection.Reconnecting += HandleReconnecting;
            _hubConnection.Reconnected += HandleReconnected;

#if DEBUG
            {
                _hubConnection.ServerTimeout = TimeSpan.FromMinutes(30);
                _hubConnection.KeepAliveInterval = TimeSpan.FromMinutes(15);
            }
#endif

            await _hubConnection.StartAsync();
            _connectionId = _hubConnection.ConnectionId ?? Guid.NewGuid().ToString("N")[..8];

            // Create typed proxy for Hub methods
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

        /// <summary>
        /// Connect to a session on the server.
        /// </summary>
        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0)
        {
            MetaLog.Debug($"[SignalR] SessionConnectAsync: playerId={playerId}");
            EnsureConnected();

            var response = await _hub!.SessionConnect(new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence
            });
            MetaLog.Debug($"[SignalR] SessionConnectAsync response: Success={response.Success}");

            return new ConnectionSessionConnectResult
            {
                Success = response.Success,
                Error = response.Error,
                SessionId = response.SessionId,
                IsNewSession = response.IsNewSession,
                MissedPackets = response.MissedPackets,
                ServerTimeTicks = response.ServerTimeTicks,
                ResubscribedEntities = response.ResubscribedEntities
            };
        }

        /// <summary>
        /// Subscribe to an entity.
        /// </summary>
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

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureConnected();

            var response = await _hub!.Unsubscribe(new UnsubscribeRequest
            {
                EntityId = entityId
            });

            return response.Success;
        }

        /// <summary>
        /// Execute an RPC call on an entity.
        /// Returns SessionResponse with result and session-level sequence number.
        /// </summary>
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
            // _hub.SignalCall uses HubConnection.SendAsync internally — fire-and-forget
            // from SignalR's perspective, completes when the frame leaves the client.
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

        /// <summary>
        /// Acknowledge received broadcasts.
        /// </summary>
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
            MetaLog.Debug($"[SignalR] OnReceiveBroadcast: seq={message.SequenceNumber}, opsCount={message.Operations?.Count ?? 0}");

            if (message.Operations != null && message.Operations.Count > 0)
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
            {
                MetaLog.Error($"[SignalR] Connection closed: {reason}", exception);
            }
            else
            {
                MetaLog.Info($"[SignalR] Connection closed: {reason}");
            }
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
    /// Retry policy for automatic reconnection.
    /// </summary>
    internal class SignalRRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] _delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, _delays.Length - 1);
            return _delays[index];
        }
    }
}
