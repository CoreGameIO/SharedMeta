using System;
using System.Collections.Generic;
#if !NETSTANDARD2_1
using System.Net.Http;
using System.Net.Http.Json;
#endif
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Network;
using SharedMeta.Core.Transport;
using SharedMeta.Client.Network;

namespace SharedMeta.Client
{
    /// <summary>
    /// Options for configuring MetaClient.
    /// </summary>
    public class MetaClientOptions
    {
        /// <summary>Execution mode provider. Default: ExecutionModeProvider (all Server mode).</summary>
        public IExecutionModeProvider? ModeProvider { get; set; }

        /// <summary>Diagnostics handler for desync detection. Default: null.</summary>
        public IDesyncDiagnostics? Diagnostics { get; set; }

        /// <summary>
        /// Optional listener for server-side session health notifications (RPC ordering stalls,
        /// recovery). Implementations typically drive a UI overlay. Default: null (no-op).
        /// </summary>
        public ISessionHealthListener? SessionHealth { get; set; }

        /// <summary>
        /// Optional listener for client-side connection health (pending request timeouts).
        /// Notified when requests exceed <see cref="ConnectionHealthOptions"/> thresholds.
        /// Default: null (no monitoring).
        /// </summary>
        public IConnectionHealthListener? ConnectionHealth { get; set; }

        /// <summary>
        /// Timeout thresholds for client-side connection health monitoring.
        /// Only used when <see cref="ConnectionHealth"/> is set. Default: 1s soft, 5s hard.
        /// </summary>
        public ConnectionHealthOptions? ConnectionHealthOptions { get; set; }

        /// <summary>Transformer registry. Default: new TransformerRegistry().</summary>
        public TransformerRegistry? TransformerRegistry { get; set; }

        /// <summary>Player ID. Default: random 8-char hex.</summary>
        public string? PlayerId { get; set; }

        /// <summary>
        /// Client app version (e.g. <c>"1.4.3"</c>) — stamped on
        /// <see cref="SessionConnectRequest.ClientVersion"/> at <see cref="MetaClient.ConnectAsync"/>
        /// and used server-side as the default <c>CallerClientVersion</c> for every RPC and
        /// subscribe over this connection. Drives <c>[MetaConfigVersion]</c> per-client config
        /// branch resolution. Default: null — the connection's own client version (passed to the
        /// transport constructor / options) is used instead.
        /// </summary>
        public string? ClientAppVersion { get; set; }
    }

    /// <summary>
    /// Unified client entry point for SharedMeta.
    /// Handles dispatcher creation, session management, and service resolution.
    /// Eliminates the need to write per-project setup classes.
    /// </summary>
    public class MetaClient : IAsyncDisposable, IDisposable
    {
        private readonly ClientDispatcher _dispatcher;
        private readonly MetaServiceResolver _resolver;

        /// <summary>The underlying connection.</summary>
        public IConnection Connection { get; }

        /// <summary>The client dispatcher for entity multiplexing.</summary>
        public IClientDispatcher Dispatcher => _dispatcher;

        /// <summary>The service resolver for getting typed API clients.</summary>
        public IMetaServiceResolver Resolver => _resolver;

        /// <summary>The serializer.</summary>
        public IMetaSerializer Serializer { get; }

        /// <summary>The transformer registry.</summary>
        public TransformerRegistry TransformerRegistry { get; }

        /// <summary>Player ID for this client session.</summary>
        public string PlayerId { get; set; }

        /// <summary>
        /// Client app version stamped on <see cref="SessionConnectRequest.ClientVersion"/> at
        /// <see cref="ConnectAsync"/>. Drives server-side <c>[MetaConfigVersion]</c> per-client
        /// resolution and (in upcoming releases) the strict-throw contract. Null = fall back to
        /// the transport's own client version, if any.
        /// </summary>
        public string? ClientAppVersion { get; set; }

        /// <summary>The connection ID. Available after ConnectAsync.</summary>
        public string ConnectionId => Connection.ConnectionId;

        /// <summary>Whether the session is connected.</summary>
        public bool IsSessionConnected => _dispatcher.IsSessionConnected;

        /// <summary>
        /// Fired when session is superseded by a new connection with the same player ID.
        /// </summary>
        public event Action<string>? OnSessionSuperseded;

        public MetaClient(
            IConnection connection,
            IMetaSerializer serializer,
            MetaClientOptions? options = null)
        {
            options ??= new MetaClientOptions();

            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            TransformerRegistry = options.TransformerRegistry ?? new TransformerRegistry();
            PlayerId = options.PlayerId ?? Guid.NewGuid().ToString("N")[..8];
            ClientAppVersion = options.ClientAppVersion;

            var modeProvider = options.ModeProvider ?? new ExecutionModeProvider();
            var diagnostics = options.Diagnostics;

            _dispatcher = new ClientDispatcher(connection)
            {
                SessionHealthListener = options.SessionHealth,
                ConnectionHealthListener = options.ConnectionHealth
            };
            if (options.ConnectionHealthOptions != null)
                _dispatcher.ConnectionHealthOptions = options.ConnectionHealthOptions;
            _dispatcher.OnSessionSuperseded += reason => OnSessionSuperseded?.Invoke(reason);
            _dispatcher.OnEntitiesResubscribed += entities => _resolver!.RefreshEntityStates(entities);

            _resolver = new MetaServiceResolver(
                async (entityId, stateTypeName) =>
                {
                    var connectResponse = await _dispatcher.SubscribeAsync(entityId, stateTypeName);
                    var network = new DispatcherNetworkAdapter(_dispatcher, Serializer, entityId, () => _dispatcher.ServerTimeTicks, stateTypeName)
                    {
                        PlayerId = PlayerId,
                        // 0.22.0 Per-entity capability overlay from SubscribeResponse →
                        // ConnectResponse.AugmentedCapabilities. CapabilitiesGate consults this
                        // alongside session-level Capabilities at the generated *ApiClient gate.
                        EntityCapabilities = connectResponse.AugmentedCapabilities,
                    };
                    return new NetworkSubscribeResult
                    {
                        Network = network,
                        StateBytes = connectResponse.StateBytes,
                        OptimisticRandomBytes = connectResponse.OptimisticRandomBytes,
                        NamedRandomsBytes = connectResponse.NamedRandomsBytes,
                        ConfigVersion = new MetaConfigVersion(connectResponse.ConfigMajorVersion, connectResponse.ConfigMinorVersion, connectResponse.ConfigPatchVersion)
                    };
                },
                Serializer,
                modeProvider,
                diagnostics
            );
        }

        /// <summary>
        /// Convenience helper for <see cref="DownloadingConfigProvider{TConfig}"/>: maps a
        /// <see cref="MetaConfigVersion"/> to the server-issued download URL by calling
        /// <see cref="IConnection.GetConfigDownloadUrlAsync"/>. The <paramref name="stateTypeName"/>
        /// must match the state that owns the config (the server keys URLs by state type).
        /// </summary>
        public Func<MetaConfigVersion, Task<string?>> ConfigDownloadUrlResolver(string stateTypeName) =>
            version => Connection.GetConfigDownloadUrlAsync(stateTypeName, version);

        /// <summary>
        /// Connect transport and establish session with the server.
        /// </summary>
        public async Task ConnectAsync()
        {
            // 0.23.0+ Telemetry: state transition disconnected → connecting → connected/failed.
            SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                new KeyValuePair<string, object?>("from", "disconnected"),
                new KeyValuePair<string, object?>("to", "connecting"));
            using var __connActivity = SharedMeta.Client.Telemetry.SharedMetaClientActivities.Source.StartActivity(
                SharedMeta.Client.Telemetry.SharedMetaClientActivities.SpanClientConnect);
            try
            {
                await Connection.ConnectAsync();

                _dispatcher.PlayerId = PlayerId;
                var sessionResult = await _dispatcher.ConnectSessionAsync(Guid.NewGuid(), 0, ClientAppVersion);

                if (!sessionResult.Success)
                {
                    SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                        new KeyValuePair<string, object?>("from", "connecting"),
                        new KeyValuePair<string, object?>("to", "failed"));
                    throw new InvalidOperationException($"Failed to establish session: {sessionResult.Error}");
                }
                SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                    new KeyValuePair<string, object?>("from", "connecting"),
                    new KeyValuePair<string, object?>("to", "connected"));
            }
            catch
            {
                SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                    new KeyValuePair<string, object?>("from", "connecting"),
                    new KeyValuePair<string, object?>("to", "failed"));
                throw;
            }
        }

        /// <summary>
        /// Attempt to resume the current session after a connection issue.
        /// Reconnects the transport if needed, then re-establishes the session
        /// with the same sessionId — server returns missed packets and re-subscribes entities.
        /// Use this for "try again" scenarios (metro/tunnel, temporary network loss).
        /// Throws on failure — caller can fall back to <see cref="RestartSessionAsync"/> if needed.
        /// </summary>
        public async Task ResumeSessionAsync()
        {
            if (!Connection.IsConnected)
            {
                await Connection.ConnectAsync();
            }

            await _dispatcher.ResumeSessionAsync();
        }

        /// <summary>
        /// Restart session after supersede. Clears all state, reconnects session.
        /// After this, re-subscribe to entities as if connecting from scratch.
        /// </summary>
        public async Task RestartSessionAsync()
        {
            _resolver.ClearAllConnections();
            _dispatcher.ResetForRestart();

            if (!Connection.IsConnected)
            {
                await Connection.ConnectAsync();
            }

            _dispatcher.PlayerId = PlayerId;
            var sessionResult = await _dispatcher.ConnectSessionAsync(Guid.NewGuid(), 0, ClientAppVersion);

            if (!sessionResult.Success)
            {
                throw new InvalidOperationException($"Failed to restart session: {sessionResult.Error}");
            }
        }

        /// <summary>
        /// Enable or disable deep desync detection for this session.
        /// Requires server to have AllowDebugApi = true in MetaTransportOptions.
        /// </summary>
        public async Task<bool> SetDeepDesyncAsync(bool enabled)
        {
            var result = await Connection.SetDebugOptionsAsync(new Core.Transport.DebugOptionsRequest
            {
                DeepDesyncEnabled = enabled
            });
            return result;
        }

        /// <summary>
        /// Get a typed API client for an entity.
        /// </summary>
        public Task<TApiClient> GetServiceAsync<TApiClient>(string entityId) where TApiClient : class
        {
            return _resolver.GetServiceAsync<TApiClient>(entityId);
        }

        /// <summary>
        /// Get current state for a connected entity.
        /// </summary>
        public TState GetState<TState>(string entityId) where TState : class, ISharedState
        {
            return _resolver.GetState<TState>(entityId);
        }

        /// <summary>
        /// Get the resolved config for a connected entity.
        /// Returns null if the entity is not connected or has no config.
        /// </summary>
        public TConfig? GetEntityConfig<TConfig>(string entityId) where TConfig : class
        {
            return _resolver.GetEntityConfig<TConfig>(entityId);
        }

        /// <summary>
        /// 0.20.3: read-only snapshot of every currently subscribed entity for debug
        /// inspection. See <see cref="MetaServiceResolver.GetSubscribedEntities"/>.
        /// </summary>
        public IReadOnlyList<SubscribedEntityInfo> GetSubscribedEntities()
            => _resolver.GetSubscribedEntities();

        /// <summary>
        /// 0.20.3: human-readable one-liner per subscribed entity (id, state type, pinned
        /// config, locally-registered services). Format is debug-only; do not parse.
        /// </summary>
        public string DescribeSubscriptions() => _resolver.DescribeSubscriptions();

        public async ValueTask DisposeAsync()
        {
            _resolver.Dispose();
            _dispatcher.Dispose();

            // Graceful disconnect tells the server to clean up immediately (no saved subscriptions)
            try
            {
                if (Connection.IsConnected)
                    await Connection.GracefulDisconnectAsync();
            }
            catch
            {
                // Best-effort — connection may already be lost
            }

            await Connection.DisconnectAsync();
            Connection.Dispose();
        }

        public void Dispose()
        {
            _resolver.Dispose();
            _dispatcher.Dispose();
            Connection.Dispose();
        }

#if !NETSTANDARD2_1
        /// <summary>
        /// Authenticate with the server using a DeviceId.
        /// Returns a login result with JWT token and PlayerId.
        /// Not available on netstandard2.1 (Unity) — use platform-specific HTTP instead.
        /// </summary>
        /// <param name="authUrl">Auth endpoint URL (e.g., "http://localhost:5100/meta/auth").</param>
        /// <param name="deviceId">Unique device identifier.</param>
        public static async Task<MetaLoginResult> LoginAsync(string authUrl, string deviceId)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(
                $"{authUrl.TrimEnd('/')}/login",
                new { DeviceId = deviceId });
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>();
            return result ?? throw new InvalidOperationException("Login returned null response");
        }
#endif
    }

}
