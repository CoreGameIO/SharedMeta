#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BestHTTP;
using Newtonsoft.Json;
using SharedMeta.Core;
using Newtonsoft.Json.Serialization;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.BestHttp
{
    /// <summary>
    /// Options for configuring <see cref="BestHttpPollingConnection"/>.
    /// </summary>
    public class BestHttpPollingConnectionOptions
    {
        /// <summary>Base URL of the SharedMeta HTTP polling endpoint.</summary>
        public string ServerUrl { get; set; } = "http://localhost:5100/meta";

        /// <summary>JWT access token for authenticated connections. Null for anonymous.</summary>
        public string? AccessToken { get; set; }

        /// <summary>Timeout for normal HTTP requests (seconds).</summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>Timeout for long-poll requests (seconds).</summary>
        public int PollTimeoutSeconds { get; set; } = 35;

        /// <summary>Initial retry delay when poll errors occur.</summary>
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Maximum retry delay for exponential backoff.</summary>
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Client application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Sent to the server during SessionConnect for compatibility checking.
        /// Null to skip version reporting.
        /// </summary>
        public string? ClientVersion { get; set; }
    }

    /// <summary>
    /// BestHTTP-based HTTP long-polling implementation of <see cref="IConnection"/>.
    /// Uses <see cref="HTTPRequest"/> for HTTP and Newtonsoft.Json for JSON serialization.
    /// Compatible with all Unity platforms including WebGL.
    ///
    /// Lifecycle:
    ///   ConnectAsync()        — generates connectionId, no HTTP call
    ///   SessionConnectAsync() — first HTTP call, creates handler on server
    ///   (poll loop starts after successful session connect)
    ///   DisconnectAsync()     — stops poll loop, sends POST /disconnect
    /// </summary>
    public class BestHttpPollingConnection : IConnection
    {
        private const string ConnectionIdHeader = "X-Connection-Id";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly BestHttpPollingConnectionOptions _options;
        private string _connectionId = "";
        private bool _isConnected;
        private bool _isSessionConnected;
        private CancellationTokenSource? _pollCts;

        /// <summary>Access to connection options (URL, auth) for ext-service adapters.</summary>
        protected BestHttpPollingConnectionOptions Options => _options;

        public string ConnectionId => _connectionId;
        public bool IsConnected => _isConnected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        public BestHttpPollingConnection(BestHttpPollingConnectionOptions? options = null)
        {
            _options = options ?? new BestHttpPollingConnectionOptions();
        }

        public Task ConnectAsync()
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            _connectionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            _isConnected = true;

            MetaLog.Info($"[BestHttp] Connected with ID: {_connectionId}");
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected) return;

            _isConnected = false;
            _isSessionConnected = false;
            StopPollLoop();

            try { await PostRawAsync("/disconnect", null); }
            catch { /* best effort */ }
        }

        public async Task GracefulDisconnectAsync()
        {
            if (!_isSessionConnected) return;

            try { await PostRawAsync("/graceful-disconnect", null); }
            catch { /* best effort */ }
        }

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0)
        {
            EnsureConnected();

            var body = new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence,
                ClientVersion = _options.ClientVersion
            };

            var response = await PostAsync<SessionConnectResponse>("/session-connect", body);

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
                MissedPackets = response.MissedPackets ?? new List<SessionResponse>(),
                ServerTimeTicks = response.ServerTimeTicks,
                ResubscribedEntities = response.ResubscribedEntities,
                ServerVersion = response.ServerVersion,
                MinClientVersion = response.MinClientVersion
            };
        }

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            EnsureSessionConnected();

            var response = await PostAsync<SubscribeResponse>(
                "/subscribe",
                new SubscribeRequest { EntityId = entityId, StateTypeName = stateTypeName });

            return new ConnectionSubscribeResult
            {
                Success = response.Success,
                Error = response.Error,
                StateBytes = response.StateBytes ?? Array.Empty<byte>(),
                OptimisticRandomBytes = response.OptimisticRandomBytes,
                NamedRandomsBytes = response.NamedRandomsBytes,
                ConfigVersion = new MetaConfigVersion(response.ConfigMajorVersion, response.ConfigMinorVersion)
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureSessionConnected();

            var response = await PostAsync<UnsubscribeResponse>(
                "/unsubscribe",
                new UnsubscribeRequest { EntityId = entityId });

            return response.Success;
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            EnsureSessionConnected();
            return await PostAsync<SessionResponse>("/rpc", request);
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            EnsureSessionConnected();
            return await PostAsync<QueryCallResponse>("/query", request);
        }

        /// <summary>
        /// Fire-and-forget signal via HTTP POST to <c>/signal</c>. The server responds 202
        /// before executing the signal; we log non-success statuses but never throw.
        /// </summary>
        public async Task SignalCallAsync(SignalCallRequest request)
        {
            EnsureSessionConnected();
            try
            {
                // PostAsync returns a response type but for signals we discard it —
                // the server returns 202 Accepted with no meaningful body.
                await PostAsync<object>("/signal", request);
            }
            catch (Exception ex)
            {
                SharedMeta.Core.Logging.MetaLog.Warning($"[BestHttpPolling] Signal failed: {ex.Message}");
            }
        }

        public async Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            EnsureSessionConnected();
            var response = await PostAsync<DebugOptionsResponse>("/debug-options", request);
            return response.Success;
        }

        public async Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
        {
            EnsureSessionConnected();
            return await PostAsync<DesyncReportResponse>("/desync-report", request);
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureSessionConnected();
            await PostAsync<AcknowledgeResponse>(
                "/ack",
                new AcknowledgeRequest { SequenceNumber = sequenceNumber });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            EnsureSessionConnected();
            var response = await PostAsync<ConfigDownloadUrlResponse>(
                "/config-url",
                new ConfigDownloadUrlRequest { StateTypeName = stateTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor });
            return response?.DownloadUrl;
        }

        #region Poll Loop

        private void StartPollLoop()
        {
            StopPollLoop();
            _pollCts = new CancellationTokenSource();
            RunPollLoop(_pollCts.Token);
        }

        private void StopPollLoop()
        {
            if (_pollCts != null)
            {
                _pollCts.Cancel();
                _pollCts.Dispose();
                _pollCts = null;
            }
        }

        private async void RunPollLoop(CancellationToken ct)
        {
            var retryDelay = _options.InitialRetryDelay;
            var consecutiveErrors = 0;

            while (!ct.IsCancellationRequested && _isConnected)
            {
                try
                {
                    var responseText = await PostRawAsync("/poll", null, _options.PollTimeoutSeconds);
                    if (ct.IsCancellationRequested) return;

                    var pollResponse = JsonConvert.DeserializeObject<BestHttpPollResponse>(responseText, JsonSettings);

                    if (consecutiveErrors > 0)
                    {
                        MetaLog.Info("[BestHttp] Poll recovered after errors");
                        OnReconnected?.Invoke();
                    }

                    consecutiveErrors = 0;
                    retryDelay = _options.InitialRetryDelay;

                    if (pollResponse == null) continue;

                    if (pollResponse.Broadcasts != null)
                    {
                        foreach (var broadcast in pollResponse.Broadcasts)
                        {
                            if (broadcast.Operations != null && broadcast.Operations.Count > 0)
                                OnBatch?.Invoke(broadcast);
                        }
                    }

                    if (pollResponse.SessionTerminated != null)
                    {
                        MetaLog.Warning($"[BestHttp] Session terminated: {pollResponse.SessionTerminated}");
                        OnSessionTerminated?.Invoke(pollResponse.SessionTerminated);
                        return;
                    }
                }
                catch (HttpGoneException)
                {
                    MetaLog.Warning("[BestHttp] Server returned 410 Gone — connection expired");
                    _isConnected = false;
                    _isSessionConnected = false;
                    OnDisconnected?.Invoke(TransportDisconnectReason.ServerDisconnect);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) return;

                    consecutiveErrors++;
                    MetaLog.Warning($"[BestHttp] Poll error ({consecutiveErrors}): {ex.Message}");

                    if (consecutiveErrors == 1)
                        OnReconnecting?.Invoke();

                    try { await Task.Delay(retryDelay, ct); }
                    catch (OperationCanceledException) { return; }

                    retryDelay = TimeSpan.FromMilliseconds(
                        Math.Min(retryDelay.TotalMilliseconds * 2, _options.MaxRetryDelay.TotalMilliseconds));
                }
            }
        }

        #endregion

        #region HTTP Helpers

        protected async Task<T> PostAsync<T>(string path, object? body, int timeoutSeconds = 0)
        {
            var responseText = await PostRawAsync(path, body, timeoutSeconds);
            return JsonConvert.DeserializeObject<T>(responseText, JsonSettings)!;
        }

        protected Task<string> PostRawAsync(string path, object? body, int timeoutSeconds = 0)
        {
            var url = _options.ServerUrl.TrimEnd('/') + path;
            var tcs = new TaskCompletionSource<string>();

            var request = new HTTPRequest(new Uri(url), HTTPMethods.Post, (req, resp) =>
            {
                if (resp == null)
                {
                    tcs.TrySetException(new Exception($"HTTP request failed: no response (request state: {req.State})"));
                    return;
                }

                if (resp.StatusCode == 410)
                {
                    tcs.TrySetException(new HttpGoneException());
                    return;
                }

                if (!resp.IsSuccess)
                {
                    tcs.TrySetException(new Exception($"HTTP {resp.StatusCode}: {resp.Message}"));
                    return;
                }

                tcs.TrySetResult(resp.DataAsText ?? "");
            });

            request.Timeout = TimeSpan.FromSeconds(
                timeoutSeconds > 0 ? timeoutSeconds : _options.RequestTimeoutSeconds);

            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body, JsonSettings);
                request.RawData = Encoding.UTF8.GetBytes(json);
                request.SetHeader("Content-Type", "application/json");
            }

            request.SetHeader(ConnectionIdHeader, _connectionId);
            if (!string.IsNullOrEmpty(_options.AccessToken))
                request.SetHeader("Authorization", "Bearer " + _options.AccessToken);

            HTTPManager.SendRequest(request);
            return tcs.Task;
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
        }
    }

    /// <summary>
    /// Thrown when server returns HTTP 410 Gone (connection expired).
    /// </summary>
    internal class HttpGoneException : Exception
    {
        public HttpGoneException() : base("Server returned 410 Gone") { }
    }

    /// <summary>
    /// Poll response DTO (mirrors server-side PollResponse).
    /// </summary>
    internal class BestHttpPollResponse
    {
        public List<SessionResponse>? Broadcasts { get; set; }
        public string? SessionTerminated { get; set; }
        public List<string>? DeactivatingEntities { get; set; }
    }
}
