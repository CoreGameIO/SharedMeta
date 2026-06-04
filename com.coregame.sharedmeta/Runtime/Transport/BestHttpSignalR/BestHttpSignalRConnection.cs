#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BestHTTP.SignalRCore;
using BestHTTP.SignalRCore.Encoders;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.BestHttp
{
    /// <summary>
    /// Options for configuring <see cref="BestHttpSignalRConnection"/>.
    /// </summary>
    public class BestHttpSignalRConnectionOptions
    {
        /// <summary>SignalR hub URL.</summary>
        public string ServerUrl { get; set; } = "http://localhost:5000/meta-hub";

        /// <summary>JWT access token for authenticated connections. Null for anonymous.</summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Custom IProtocol for the SignalR connection.
        /// Defaults to JsonProtocol with LitJsonEncoder if null.
        /// </summary>
        public IProtocol? Protocol { get; set; }

        /// <summary>
        /// How often the client sends ping messages to the server.
        /// Must be less than the server's ClientTimeoutInterval (default 30s).
        /// Set to match the server's KeepAliveInterval.
        /// </summary>
        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Client application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Sent to the server during SessionConnect for compatibility checking.
        /// Null to skip version reporting.
        /// </summary>
        public string? ClientVersion { get; set; }

        /// <summary>
        /// Maximum number of automatic reconnect attempts. 0 = no reconnect.
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 5;

        /// <summary>
        /// Delays between reconnect attempts. BestHTTP cycles through this array.
        /// A null entry stops reconnecting (used as the final element).
        /// </summary>
        public TimeSpan?[] ReconnectDelays { get; set; } = new TimeSpan?[]
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            null
        };
    }

    /// <summary>
    /// BestHTTP SignalR Core implementation of <see cref="IConnection"/>.
    /// Uses <see cref="HubConnection"/> from BestHTTP.SignalRCore for bidirectional communication.
    /// Compatible with all Unity platforms including WebGL.
    ///
    /// BestHTTP SignalR Core uses IFuture-based API — this class wraps it
    /// with <see cref="TaskCompletionSource{T}"/> for async/await compatibility.
    /// </summary>
    public class BestHttpSignalRConnection : IConnection
    {
        static BestHttpSignalRConnection()
        {
            // LitJson doesn't know how to serialize Guid — register string conversion
            BestHTTP.JSON.LitJson.JsonMapper.RegisterExporter<Guid>((guid, writer) => writer.Write(guid.ToString()));
            BestHTTP.JSON.LitJson.JsonMapper.RegisterImporter<string, Guid>(input => Guid.Parse(input));

            // LitJson doesn't know how to serialize byte[] — register base64 conversion
            // (server-side System.Text.Json expects byte[] as base64 string)
            BestHTTP.JSON.LitJson.JsonMapper.RegisterExporter<byte[]>((bytes, writer) => writer.Write(Convert.ToBase64String(bytes)));
            BestHTTP.JSON.LitJson.JsonMapper.RegisterImporter<string, byte[]>(input => Convert.FromBase64String(input));

            // Wire payloads are ReadOnlyMemory<byte> (post-0.23.1 memory optimization). Without a
            // converter LitJson recurses into the struct's properties until it hits max object depth.
            // Encode as base64 to match server-side System.Text.Json, which serializes
            // ReadOnlyMemory<byte> as a base64 string (built-in since .NET 7).
            BestHTTP.JSON.LitJson.JsonMapper.RegisterExporter<ReadOnlyMemory<byte>>((rom, writer) => writer.Write(Convert.ToBase64String(rom.ToArray())));
            BestHTTP.JSON.LitJson.JsonMapper.RegisterImporter<string, ReadOnlyMemory<byte>>(input => Convert.FromBase64String(input));

            // ulong wire fields (signature hashes: ServerSignatureHash, ClientSignatureHash, ArgHash).
            // LitJson's lexer reads a JSON integer as int, then long, then ulong by magnitude. It has
            // base importers for int->ulong and (huge) ulong->ulong, but NOT long->ulong — so a hash
            // in (int.MaxValue, long.MaxValue] is read as long and can't be assigned to a ulong field.
            // Register the missing rung. Write side already works: ulong is exported as a JSON number
            // and server-side System.Text.Json reads the full ulong range.
            BestHTTP.JSON.LitJson.JsonMapper.RegisterImporter<long, ulong>(input => (ulong)input);
        }

        private readonly BestHttpSignalRConnectionOptions _options;
        private HubConnection? _hub;
        private IMetaHub? _proxy;

        /// <summary>Access to the underlying BestHTTP HubConnection for ext-service adapters.</summary>
        protected HubConnection? Hub => _hub;
        private string _connectionId = "";

        // 0.26.3+: Tracks whether DisconnectAsync was called. BestHTTP fires _hub.OnClosed
        // both on user-requested close AND when its internal reconnect-retry budget runs out
        // ("No more reconnect attempt!"); without this flag the two cases are indistinguishable
        // at the SharedMeta surface and TransportDisconnectReason was hardcoded to ClientRequested.
        private bool _disconnectRequested;

        // 0.26.3+: Dedupes the OnDisconnected emission. Some BestHTTP versions DON'T fire
        // _hub.OnClosed after retry-budget exhaustion — they leave the hub in ConnectionStates.Closed
        // and only fire _hub.OnError. We use OnError as a safety net (see ConnectAsync), and this
        // flag prevents double-emit if a later OnClosed does fire.
        private bool _disconnectedEmitted;

        public string ConnectionId => _connectionId;
        public bool IsConnected => _hub != null && _hub.State == ConnectionStates.Connected;

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<string>? OnRequireSessionReconnect;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        public BestHttpSignalRConnection(string serverUrl, string? accessToken = null)
            : this(new BestHttpSignalRConnectionOptions { ServerUrl = serverUrl, AccessToken = accessToken })
        {
        }

        public BestHttpSignalRConnection(BestHttpSignalRConnectionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public Task ConnectAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            var hubOptions = new HubOptions
            {
                SkipNegotiation = false,
                PingInterval = _options.PingInterval
            };

            var protocol = _options.Protocol ?? new JsonProtocol(new LitJsonEncoder());

            _hub = new HubConnection(new Uri(_options.ServerUrl), protocol, hubOptions);

            // Configure automatic reconnect with delays
            if (_options.MaxReconnectAttempts > 0)
            {
                _hub.ReconnectPolicy = new DefaultRetryPolicy(_options.ReconnectDelays);
            }

            if (!string.IsNullOrEmpty(_options.AccessToken))
            {
                _hub.AuthenticationProvider = new BestHttpTokenAuthProvider(_options.AccessToken!);
            }

            // Subscribe to hub events from server
            _hub.On<SessionResponse>(nameof(IMetaHubClient.ReceiveBroadcast), OnReceiveBroadcast);
            _hub.On<string>(nameof(IMetaHubClient.SessionTerminated), msg => OnSessionTerminated?.Invoke(msg));
            _hub.On<string>(nameof(IMetaHubClient.EntityDeactivating), OnEntityDeactivating);
            _hub.On<string>(nameof(IMetaHubClient.RequireSessionReconnect), msg => OnRequireSessionReconnect?.Invoke(msg));

            // Lifecycle events
            _hub.OnConnected += hub =>
            {
                _connectionId = hub.NegotiationResult?.ConnectionId
                    ?? Guid.NewGuid().ToString("N").Substring(0, 8);
                _proxy = new BestHttpMetaHubProxy(hub);
                MetaLog.Info($"[BestHttpSignalR] Connected with ID: {_connectionId}");
                tcs.TrySetResult(true);
            };

            _hub.OnError += (hub, error) =>
            {
                MetaLog.Error($"[BestHttpSignalR] Error: {error}");
                tcs.TrySetException(new Exception($"SignalR connection failed: {error}"));

                // 0.26.3+: Safety net — BestHTTP's "No more reconnect attempt!" path doesn't
                // always fire OnClosed; the hub transitions to ConnectionStates.Closed and only
                // OnError runs. Without this check the dispatcher never sees Disconnected/Failed
                // and the UI hangs in Reconnecting forever.
                if (hub.State == ConnectionStates.Closed && !_disconnectedEmitted && !_disconnectRequested)
                {
                    _disconnectedEmitted = true;
                    MetaLog.Info("[BestHttpSignalR] Hub state == Closed after error — emitting NetworkError (BestHTTP did not fire OnClosed)");
                    OnDisconnected?.Invoke(TransportDisconnectReason.NetworkError);
                }
            };

            _hub.OnClosed += hub =>
            {
                if (_disconnectedEmitted) return;     // safety-net path already fired from OnError
                _disconnectedEmitted = true;
                // 0.26.3+: BestHTTP fires OnClosed for both user-initiated DisconnectAsync
                // and transport-give-up. Only the _disconnectRequested flag distinguishes them.
                var reason = _disconnectRequested
                    ? TransportDisconnectReason.ClientRequested
                    : TransportDisconnectReason.NetworkError;
                MetaLog.Info($"[BestHttpSignalR] Connection closed: {reason}");
                OnDisconnected?.Invoke(reason);
            };

            _hub.OnReconnecting += (hub, error) =>
            {
                MetaLog.Info($"[BestHttpSignalR] Reconnecting... ({error})");
                OnReconnecting?.Invoke();
            };

            _hub.OnReconnected += hub =>
            {
                _connectionId = hub.NegotiationResult?.ConnectionId ?? _connectionId;
                MetaLog.Info($"[BestHttpSignalR] Reconnected with ID: {_connectionId}");
                OnReconnected?.Invoke();
            };

            _hub.StartConnect();
            return tcs.Task;
        }

        public Task DisconnectAsync()
        {
            if (_hub == null) return Task.CompletedTask;

            _disconnectRequested = true;   // ← so OnClosed handler reports ClientRequested

            var tcs = new TaskCompletionSource<bool>();

            _hub.OnClosed += _ => tcs.TrySetResult(true);
            _hub.OnError += (_, error) => tcs.TrySetException(new Exception($"Disconnect error: {error}"));

            MetaLog.Debug("[BestHttpSignalR] DisconnectAsync called");
            _hub.StartClose();

            // Timeout in case the close callback never fires
            Task.Delay(5000).ContinueWith(_ => tcs.TrySetResult(true));

            return tcs.Task;
        }

        public async Task GracefulDisconnectAsync()
        {
            MetaLog.Debug("[BestHttpSignalR] GracefulDisconnectAsync called");
            if (_proxy != null && IsConnected)
            {
                try
                {
                    await _proxy.GracefulDisconnect();
                }
                catch (Exception ex)
                {
                    MetaLog.Debug($"[BestHttpSignalR] GracefulDisconnect failed (ok): {ex.Message}");
                }
            }
        }

        public async Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null, ulong clientSignatureHash = 0, SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0, List<SubscriptionClaim>? claimedSubscriptions = null)
        {
            EnsureConnected();

            var response = await _proxy!.SessionConnect(new SessionConnectRequest
            {
                PlayerId = playerId,
                SessionId = sessionId,
                LastAcknowledgedSequence = lastAcknowledgedSequence,
                ClientVersion = clientAppVersion ?? _options.ClientVersion,
                ClientSignatureHash = clientSignatureHash,
                Mode = mode,
                LastCompletedRequestId = lastCompletedRequestId,
                ClaimedSubscriptions = claimedSubscriptions,
            });

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

        // 0.22.0+ phase-2 of the compatibility handshake. Without this override IConnection's
        // default-interface implementation throws NotSupportedException, so a server that replies
        // NeedsSignatureRegistration leaves the client unable to register → every RPC is rejected.
        public async Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
        {
            EnsureConnected();
            return await _proxy!.RegisterClientSignature(new RegisterClientSignatureRequest
            {
                SessionId = sessionId,
                Signature = signature,
            });
        }

        public async Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
        {
            EnsureConnected();

            var response = await _proxy!.Subscribe(new SubscribeRequest
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
                ConfigVersion = new MetaConfigVersion(response.ConfigMajorVersion, response.ConfigMinorVersion, response.ConfigPatchVersion),
                EntitySequenceNumber = response.EntitySequenceNumber,
                FeatureRequirement = response.FeatureRequirement,
                AugmentedCapabilities = response.AugmentedCapabilities,
            };
        }

        public async Task<bool> UnsubscribeAsync(string entityId)
        {
            EnsureConnected();

            var response = await _proxy!.Unsubscribe(new UnsubscribeRequest
            {
                EntityId = entityId
            });

            return response.Success;
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            EnsureConnected();
            return await _proxy!.RpcCall(request);
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            EnsureConnected();
            return await _proxy!.QueryCall(request);
        }

        public Task SignalCallAsync(SignalCallRequest request)
        {
            EnsureConnected();
            return _proxy!.SignalCall(request);
        }

        public async Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            EnsureConnected();
            var response = await _proxy!.SetDebugOptions(request);
            return response.Success;
        }

        public async Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
        {
            EnsureConnected();
            return await _proxy!.SendDesyncReport(request);
        }

        public async Task AcknowledgeSequenceAsync(long sequenceNumber)
        {
            EnsureConnected();

            await _proxy!.AcknowledgeSequence(new AcknowledgeRequest
            {
                SequenceNumber = sequenceNumber
            });
        }

        public async Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version)
        {
            EnsureConnected();
            var response = await _proxy!.GetConfigDownloadUrl(new ConfigDownloadUrlRequest { StateTypeName = stateTypeName, ConfigMajorVersion = version.Major, ConfigMinorVersion = version.Minor, ConfigPatchVersion = version.Patch });
            return response.DownloadUrl;
        }

        #region Event Handlers

        private void OnReceiveBroadcast(SessionResponse message)
        {
            if (message.Operations != null && message.Operations.Count > 0)
            {
                OnBatch?.Invoke(message);
            }
        }

        private void OnEntityDeactivating(string entityId)
        {
            MetaLog.Info($"[BestHttpSignalR] Entity deactivating: {entityId}");
        }

        #endregion

        private void EnsureConnected()
        {
            if (_hub == null || !IsConnected || _proxy == null)
                throw new InvalidOperationException("Not connected");
        }

        public void Dispose()
        {
            if (_hub != null)
            {
                _hub.StartClose();
                _hub = null;
                _proxy = null;
            }
        }
    }

    /// <summary>
    /// Simple Bearer token auth provider for BestHTTP SignalR.
    /// </summary>
    internal class BestHttpTokenAuthProvider : IAuthenticationProvider
    {
        private readonly string _token;

        public bool IsPreAuthRequired => true;

#pragma warning disable CS0067 // event never used directly — required by IAuthenticationProvider
        public event OnAuthenticationSuccededDelegate? OnAuthenticationSucceded;
        public event OnAuthenticationFailedDelegate? OnAuthenticationFailed;
#pragma warning restore CS0067

        public BestHttpTokenAuthProvider(string token)
        {
            _token = token;
        }

        public void StartAuthentication()
        {
            OnAuthenticationSucceded?.Invoke(this);
        }

        public void PrepareRequest(BestHTTP.HTTPRequest request)
        {
            request.SetHeader("Authorization", "Bearer " + _token);
        }

        public Uri PrepareUri(Uri uri)
        {
            return uri;
        }

        public void Cancel()
        {
        }
    }
}


