using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// HTTP long-polling implementation of IConnection.
    /// Uses HttpClient for request/response and a background Task for polling broadcasts.
    ///
    /// Lifecycle:
    ///   ConnectAsync()        — generates connectionId, no HTTP call
    ///   SessionConnectAsync() — first HTTP call, creates handler on server
    ///   (poll loop starts after successful session connect)
    ///   DisconnectAsync()     — stops poll loop, sends POST /disconnect
    /// </summary>
    public class HttpPollingConnection : IConnection
    {
        private const string ConnectionIdHeader = "X-Connection-Id";

        private readonly HttpPollingConnectionOptions _options;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _pollClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private string _connectionId = "";
        private bool _isConnected;
        private bool _isSessionConnected;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;

        public string ConnectionId => _connectionId;
        public bool IsConnected => _isConnected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        public HttpPollingConnection(HttpPollingConnectionOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _jsonOptions = MetaJsonContext.Default.Options;

            _httpClient = httpClient ?? new HttpClient { Timeout = options.RequestTimeout };
            _pollClient = new HttpClient { Timeout = options.PollTimeout + TimeSpan.FromSeconds(5) };
        }

        public Task ConnectAsync()
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            _connectionId = Guid.NewGuid().ToString("N")[..12];
            _isConnected = true;

            MetaLog.Info($"[HttpPoll] Connected with ID: {_connectionId}");
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected) return;

            MetaLog.Debug("[HttpPoll] DisconnectAsync called");
            _isConnected = false;
            _isSessionConnected = false;

            StopPollLoop();

            // Notify server (best effort)
            try
            {
                using var request = CreateRequest(HttpMethod.Post, "/disconnect");
                await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                MetaLog.Debug($"[HttpPoll] Disconnect notification failed (ok): {ex.Message}");
            }
        }

        public async Task GracefulDisconnectAsync()
        {
            if (!_isSessionConnected) return;

            MetaLog.Debug("[HttpPoll] GracefulDisconnectAsync called");
            try
            {
                using var request = CreateRequest(HttpMethod.Post, "/graceful-disconnect");
                await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                MetaLog.Debug($"[HttpPoll] GracefulDisconnect failed (ok): {ex.Message}");
            }
        }

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0)
        {
            EnsureConnected();

            var body = new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence
            };

            var response = await PostAsync<SessionConnectResponse>(
                "/session-connect", body, MetaJsonContext.Default.SessionConnectRequest);

            if (response.Success)
            {
                _isSessionConnected = true;
                StartPollLoop();
            }

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

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            EnsureSessionConnected();

            var response = await PostAsync<SubscribeResponse>(
                "/subscribe",
                new SubscribeRequest { EntityId = entityId, StateTypeName = stateTypeName },
                MetaJsonContext.Default.SubscribeRequest);

            return new ConnectionSubscribeResult
            {
                Success = response.Success,
                Error = response.Error,
                StateBytes = response.StateBytes,
                OptimisticRandomBytes = response.OptimisticRandomBytes,
                ConfigVersion = new MetaConfigVersion(response.ConfigMajorVersion, response.ConfigMinorVersion)
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureSessionConnected();

            var response = await PostAsync<UnsubscribeResponse>(
                "/unsubscribe",
                new UnsubscribeRequest { EntityId = entityId },
                MetaJsonContext.Default.UnsubscribeRequest);

            return response.Success;
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            EnsureSessionConnected();
            return await PostAsync<SessionResponse>("/rpc", request, MetaJsonContext.Default.RpcCallRequest);
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureSessionConnected();
            await PostAsync<AcknowledgeResponse>(
                "/ack",
                new AcknowledgeRequest { SequenceNumber = sequenceNumber },
                MetaJsonContext.Default.AcknowledgeRequest);
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            EnsureSessionConnected();
            var response = await PostAsync<ConfigDownloadUrlResponse>(
                "/config-url",
                new ConfigDownloadUrlRequest { StateTypeName = stateTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor },
                MetaJsonContext.Default.ConfigDownloadUrlRequest);
            return response?.DownloadUrl;
        }

        #region Poll Loop

        private void StartPollLoop()
        {
            StopPollLoop();
            _pollCts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }

        private void StopPollLoop()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            var retryDelay = _options.InitialRetryDelay;
            var consecutiveErrors = 0;

            while (!ct.IsCancellationRequested && _isConnected)
            {
                try
                {
                    using var request = CreateRequest(HttpMethod.Post, "/poll");

                    using var httpResponse = await _pollClient.SendAsync(request, ct);

                    // 410 Gone — server lost our connection state
                    if (httpResponse.StatusCode == HttpStatusCode.Gone)
                    {
                        MetaLog.Warning("[HttpPoll] Server returned 410 Gone — connection expired");
                        _isConnected = false;
                        _isSessionConnected = false;
                        OnDisconnected?.Invoke(TransportDisconnectReason.ServerDisconnect);
                        return;
                    }

                    httpResponse.EnsureSuccessStatusCode();

                    var pollResponse = await httpResponse.Content.ReadFromJsonAsync(
                        MetaJsonContext.Default.PollResponse, ct);

                    // Fire OnReconnected if we recovered from errors
                    if (consecutiveErrors > 0)
                    {
                        MetaLog.Info("[HttpPoll] Poll recovered after errors");
                        OnReconnected?.Invoke();
                    }

                    // Reset retry state on success
                    consecutiveErrors = 0;
                    retryDelay = _options.InitialRetryDelay;

                    if (pollResponse == null) continue;

                    // Process broadcasts
                    if (pollResponse.Broadcasts != null)
                    {
                        foreach (var broadcast in pollResponse.Broadcasts)
                        {
                            if (broadcast.Operations is { Count: > 0 })
                            {
                                OnBatch?.Invoke(broadcast);
                            }
                        }
                    }

                    // Process session termination (terminal)
                    if (pollResponse.SessionTerminated != null)
                    {
                        MetaLog.Warning($"[HttpPoll] Session terminated: {pollResponse.SessionTerminated}");
                        OnSessionTerminated?.Invoke(pollResponse.SessionTerminated);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // Clean shutdown
                }
                catch (HttpRequestException ex)
                {
                    consecutiveErrors++;
                    MetaLog.Warning($"[HttpPoll] Poll error ({consecutiveErrors}): {ex.Message}");

                    if (consecutiveErrors == 1)
                    {
                        OnReconnecting?.Invoke();
                    }

                    // Exponential backoff
                    try
                    {
                        await Task.Delay(retryDelay, ct);
                    }
                    catch (OperationCanceledException) { return; }

                    retryDelay = TimeSpan.FromMilliseconds(
                        Math.Min(retryDelay.TotalMilliseconds * 2, _options.MaxRetryDelay.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    MetaLog.Error($"[HttpPoll] Unexpected poll error: {ex.Message}", ex);

                    try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        #endregion

        #region HTTP Helpers

        private async Task<TResponse> PostAsync<TResponse>(
            string path,
            object body,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo requestTypeInfo)
        {
            using var request = CreateRequest(HttpMethod.Post, path);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, requestTypeInfo),
                System.Text.Encoding.UTF8,
                "application/json");

            using var httpResponse = await _httpClient.SendAsync(request);

            // 410 Gone = connection expired
            if (httpResponse.StatusCode == HttpStatusCode.Gone)
            {
                _isConnected = false;
                _isSessionConnected = false;
                StopPollLoop();
                OnDisconnected?.Invoke(TransportDisconnectReason.ServerDisconnect);
                throw new InvalidOperationException("Connection expired on server");
            }

            httpResponse.EnsureSuccessStatusCode();

            var result = await httpResponse.Content.ReadFromJsonAsync<TResponse>(_jsonOptions);
            return result ?? throw new InvalidOperationException($"Null response from server for {path}");
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var url = _options.ServerUrl.TrimEnd('/') + path;
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(ConnectionIdHeader, _connectionId);
            if (!string.IsNullOrEmpty(_options.AccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            return request;
        }

        #endregion

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected");
        }

        private void EnsureSessionConnected()
        {
            EnsureConnected();
            if (!_isSessionConnected)
                throw new InvalidOperationException("Session not connected. Call SessionConnectAsync first.");
        }

        public void Dispose()
        {
            StopPollLoop();
            _httpClient.Dispose();
            _pollClient.Dispose();
        }
    }
}
