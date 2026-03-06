using System;
#if !NETSTANDARD2_1
using System.Net.Http;
using System.Net.Http.Json;
#endif
using System.Threading.Tasks;
using SharedMeta.Core;
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

        /// <summary>Transformer registry. Default: new TransformerRegistry().</summary>
        public TransformerRegistry? TransformerRegistry { get; set; }

        /// <summary>Player ID. Default: random 8-char hex.</summary>
        public string? PlayerId { get; set; }
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

            var modeProvider = options.ModeProvider ?? new ExecutionModeProvider();
            var diagnostics = options.Diagnostics;

            _dispatcher = new ClientDispatcher(connection);
            _dispatcher.OnSessionSuperseded += reason => OnSessionSuperseded?.Invoke(reason);
            _dispatcher.OnEntitiesResubscribed += entities => _resolver!.RefreshEntityStates(entities);

            _resolver = new MetaServiceResolver(
                async (entityId, stateTypeName) =>
                {
                    var connectResponse = await _dispatcher.SubscribeAsync(entityId, stateTypeName);
                    var network = new DispatcherNetworkAdapter(_dispatcher, Serializer, entityId, () => _dispatcher.ServerTimeTicks, stateTypeName)
                    {
                        PlayerId = PlayerId
                    };
                    return new NetworkSubscribeResult
                    {
                        Network = network,
                        StateBytes = connectResponse.StateBytes,
                        OptimisticRandomBytes = connectResponse.OptimisticRandomBytes,
                        ConfigVersion = new MetaConfigVersion(connectResponse.ConfigMajorVersion, connectResponse.ConfigMinorVersion)
                    };
                },
                Serializer,
                modeProvider,
                diagnostics
            )
            {
                ConfigDownloadUrlFactory = (stateTypeName, version) => connection.GetConfigDownloadUrlAsync(stateTypeName, version)
            };
        }

        /// <summary>
        /// Connect transport and establish session with the server.
        /// </summary>
        public async Task ConnectAsync()
        {
            await Connection.ConnectAsync();

            _dispatcher.PlayerId = PlayerId;
            var sessionResult = await _dispatcher.ConnectSessionAsync(Guid.NewGuid(), 0);

            if (!sessionResult.Success)
            {
                throw new InvalidOperationException($"Failed to establish session: {sessionResult.Error}");
            }
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
            var sessionResult = await _dispatcher.ConnectSessionAsync(Guid.NewGuid(), 0);

            if (!sessionResult.Success)
            {
                throw new InvalidOperationException($"Failed to restart session: {sessionResult.Error}");
            }
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

    /// <summary>
    /// Result of a login operation.
    /// </summary>
    public class MetaLoginResult
    {
        /// <summary>JWT access token.</summary>
        public string Token { get; set; } = "";

        /// <summary>Player identifier.</summary>
        public string PlayerId { get; set; } = "";

        /// <summary>Whether this is a newly created player.</summary>
        public bool IsNewPlayer { get; set; }

        /// <summary>Token expiration time (UTC).</summary>
        public DateTime ExpiresAt { get; set; }
    }
}
