using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;
using UnityEngine.Networking;

namespace SharedMeta.Client.Network
{
    /// <summary>
    /// Options for configuring <see cref="UnityHttpConnection"/>.
    /// </summary>
    public class UnityHttpConnectionOptions
    {
        /// <summary>Base URL of the SharedMeta HTTP polling endpoint.</summary>
        public string ServerUrl { get; set; } = "http://localhost:5100/meta";

        /// <summary>JWT access token for authenticated connections. Null for anonymous.</summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Optional access-token provider, resolved fresh on every request. Takes precedence over
        /// <see cref="AccessToken"/> when set — pair with
        /// <see cref="SharedMeta.Client.MetaTokenManager.GetTokenAsync()"/> so a refreshed token is
        /// picked up automatically. The provider's fast path returns the cached token without a network call.
        /// </summary>
        public System.Func<System.Threading.Tasks.Task<string?>>? AccessTokenProvider { get; set; }

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
    /// Unity-compatible HTTP long-polling implementation of <see cref="IConnection"/>.
    /// Uses <see cref="UnityWebRequest"/> for HTTP and Newtonsoft.Json for JSON serialization.
    /// Compatible with all Unity platforms including WebGL.
    ///
    /// Lifecycle:
    ///   ConnectAsync()        — generates connectionId, no HTTP call
    ///   SessionConnectAsync() — first HTTP call, creates handler on server
    ///   (poll loop starts after successful session connect)
    ///   DisconnectAsync()     — stops poll loop, sends POST /disconnect
    /// </summary>
    public class UnityHttpConnection : IConnection
    {
        private const string ConnectionIdHeader = "X-Connection-Id";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            // ROM<byte> ↔ base64 string. Wire DTOs use ReadOnlyMemory<byte> after 0.23.0;
            // without this converter Newtonsoft serializes the struct's fields and the
            // server's System.Text.Json deserializer (which expects base64) throws JsonException.
            Converters = { new RomByteJsonConverter() },
        };

        private readonly UnityHttpConnectionOptions _options;
        private readonly SynchronizationContext? _mainThread;
        private string _connectionId = "";
        private bool _isConnected;
        private bool _isSessionConnected;
        private CancellationTokenSource? _pollCts;

        public string ConnectionId => _connectionId;
        public bool IsConnected => _isConnected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<string>? OnRequireSessionReconnect;       // HTTP polling: not raised yet
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        public UnityHttpConnection(UnityHttpConnectionOptions? options = null)
        {
            _options = options ?? new UnityHttpConnectionOptions();
            // UnityWebRequest construction requires the main thread. Capture the
            // creating thread's sync context so background continuations
            // (e.g. fire-and-forget desync reports from .ContinueWith) can marshal onto it.
            _mainThread = SynchronizationContext.Current;
        }

        public Task ConnectAsync()
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            _connectionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            _isConnected = true;

            MetaLog.Info($"[UnityHttp] Connected with ID: {_connectionId}");
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
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null, ulong clientSignatureHash = 0, SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0, List<SubscriptionClaim>? claimedSubscriptions = null)
        {
            EnsureConnected();

            var body = new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence,
                ClientVersion = clientAppVersion ?? _options.ClientVersion,
                ClientSignatureHash = clientSignatureHash,
                Mode = mode,
                LastCompletedRequestId = lastCompletedRequestId,
                ClaimedSubscriptions = claimedSubscriptions,
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
                Subscriptions = response.Subscriptions,
                ServerVersion = response.ServerVersion,
                MinClientVersion = response.MinClientVersion,
                MaxClientVersion = response.MaxClientVersion,
                NeedsSignatureRegistration = response.NeedsSignatureRegistration,
                ServerSignatureHash = response.ServerSignatureHash,
                Annotated = response.Annotated,
                FailureReason = response.FailureReason,
            };
        }

        public async Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
        {
            EnsureConnected();
            return await PostAsync<RegisterClientSignatureResponse>(
                "/register-client-signature",
                new RegisterClientSignatureRequest { SessionId = sessionId, Signature = signature });
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
                ConfigVersions = response.ConfigVersions,
                EntitySequenceNumber = response.EntitySequenceNumber,
                FeatureRequirement = response.FeatureRequirement,
                AugmentedCapabilities = response.AugmentedCapabilities,
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
        /// Fire-and-forget signal — POST to <c>/signal</c>, server returns 202, we ignore the body.
        /// Non-success statuses are logged but never surface as exceptions.
        /// </summary>
        public async Task SignalCallAsync(SignalCallRequest request)
        {
            EnsureSessionConnected();
            try
            {
                await PostAsync<object>("/signal", request);
            }
            catch (Exception ex)
            {
                SharedMeta.Core.Logging.MetaLog.Warning($"[UnityHttp] Signal failed: {ex.Message}");
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
            MetaLog.Debug($"[UnityHttp] SendDesyncReport enter: kind={request.MismatchKind} entity={request.EntityId} {request.ServiceName}.{request.MethodName} clientPatch={request.ClientPatchBytes?.Length ?? 0}B");
            try
            {
                EnsureSessionConnected();
                var resp = await PostAsync<DesyncReportResponse>("/desync-report", request);
                MetaLog.Debug($"[UnityHttp] SendDesyncReport response: status={resp.Status} error={resp.Error}");
                return resp;
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[UnityHttp] SendDesyncReport FAILED: {ex.GetType().Name}: {ex.Message}", ex);
                throw;
            }
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureSessionConnected();
            await PostAsync<AcknowledgeResponse>(
                "/ack",
                new AcknowledgeRequest { SequenceNumber = sequenceNumber });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string configTypeName, MetaConfigVersion version)
        {
            EnsureSessionConnected();
            var response = await PostAsync<ConfigDownloadUrlResponse>(
                "/config-url",
                new ConfigDownloadUrlRequest { ConfigTypeName = configTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor, ConfigPatchVersion = version.Patch });
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

                    var pollResponse = JsonConvert.DeserializeObject<UnityPollResponse>(responseText, JsonSettings);

                    if (consecutiveErrors > 0)
                    {
                        MetaLog.Info("[UnityHttp] Poll recovered after errors");
                        OnReconnected?.Invoke();
                    }

                    consecutiveErrors = 0;
                    retryDelay = _options.InitialRetryDelay;

                    if (pollResponse == null) continue;

                    if (pollResponse.Broadcasts != null)
                    {
                        foreach (var broadcast in pollResponse.Broadcasts)
                        {
                            // Deliver broadcasts with operations AND out-of-band notifications
                            // (StallNotification has Operations.Count == 0 but must still be delivered)
                            if (broadcast.Operations is { Count: > 0 } || broadcast.StallNotification != null)
                                OnBatch?.Invoke(broadcast);
                        }
                    }

                    if (pollResponse.SessionTerminated != null)
                    {
                        MetaLog.Warning($"[UnityHttp] Session terminated: {pollResponse.SessionTerminated}");
                        OnSessionTerminated?.Invoke(pollResponse.SessionTerminated);
                        return;
                    }
                }
                catch (HttpGoneException)
                {
                    MetaLog.Warning("[UnityHttp] Server returned 410 Gone — connection expired");
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
                    MetaLog.Warning($"[UnityHttp] Poll error ({consecutiveErrors}): {ex.Message}");

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

        private async Task<T> PostAsync<T>(string path, object? body, int timeoutSeconds = 0)
        {
            var responseText = await PostRawAsync(path, body, timeoutSeconds);
            return JsonConvert.DeserializeObject<T>(responseText, JsonSettings)!;
        }

        private Task<string> PostRawAsync(string path, object? body, int timeoutSeconds = 0)
        {
            // Serialize body off the main thread (json work is CPU-bound), then
            // marshal UnityWebRequest construction onto the main thread.
            var url = _options.ServerUrl.TrimEnd('/') + path;
            byte[]? bodyBytes = null;
            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body, JsonSettings);
                bodyBytes = Encoding.UTF8.GetBytes(json);
            }
            var effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : _options.RequestTimeoutSeconds;

            if (_mainThread == null || SynchronizationContext.Current == _mainThread)
            {
                return BuildAndSend(url, bodyBytes, effectiveTimeout);
            }

            var tcs = new TaskCompletionSource<string>();
            _mainThread.Post(_ =>
            {
                try
                {
                    var task = BuildAndSend(url, bodyBytes, effectiveTimeout);
                    task.ContinueWith(t =>
                    {
                        if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                        else if (t.IsCanceled) tcs.TrySetCanceled();
                        else tcs.TrySetResult(t.Result);
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return tcs.Task;
        }

        private async Task<string> BuildAndSend(string url, byte[]? bodyBytes, int timeout)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = timeout;

            if (bodyBytes != null)
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            request.SetRequestHeader(ConnectionIdHeader, _connectionId);

            // Resolve the token fresh per request: the provider (when set) returns a currently-valid
            // token, refreshing transparently if the previous one expired. Falls back to the static token.
            var token = _options.AccessTokenProvider != null
                ? await _options.AccessTokenProvider()
                : _options.AccessToken;
            if (!string.IsNullOrEmpty(token))
                request.SetRequestHeader("Authorization", "Bearer " + token);

            return await SendAsync(request);
        }

        private static Task<string> SendAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<string>();
            request.SendWebRequest().completed += _ =>
            {
                var responseCode = request.responseCode;
                var result = request.result;
                var error = request.error;
                string? text = null;

                if (result == UnityWebRequest.Result.Success)
                    text = request.downloadHandler?.text;

                request.Dispose();

                if (responseCode == 410)
                    tcs.TrySetException(new HttpGoneException());
                else if (result != UnityWebRequest.Result.Success)
                    tcs.TrySetException(new Exception($"HTTP {responseCode}: {error}"));
                else
                    tcs.TrySetResult(text ?? "");
            };
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
    internal class UnityPollResponse
    {
        public List<SessionResponse>? Broadcasts { get; set; }
        public string? SessionTerminated { get; set; }
        public List<string>? DeactivatingEntities { get; set; }
    }
}


