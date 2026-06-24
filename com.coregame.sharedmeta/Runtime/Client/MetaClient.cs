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

        /// <summary>
        /// 0.24.0+ Generated <c>MetaClientSignature</c> for compatibility negotiation.
        /// Normally leave this <c>null</c>: the generator auto-publishes the assembly's
        /// <c>GameServiceDiscoveryBase.ClientSignature</c> into
        /// <see cref="SharedMeta.Core.Transport.ClientSignatureDefault.Value"/> (from
        /// <c>RegisterAllServices()</c> and, on net5+, a module initializer), and the client falls
        /// back to it at connect time. Set this explicitly only to pin a specific signature when
        /// more than one signature-bearing assembly is loaded.
        /// To force legacy opt-out (Hash=0, no negotiation) set
        /// <see cref="DisableClientSignatureNegotiation"/> instead — note 0.24.0 servers reject
        /// Hash=0 on dispatch, so opt-out only works against a legacy server or query-only client.
        /// </summary>
        public SharedMeta.Core.Transport.MetaClientSignature? ClientSignature { get; set; }

        /// <summary>
        /// 0.24.0+ Force legacy opt-out: when true the client sends Hash=0 and skips the
        /// signature handshake, ignoring both <see cref="ClientSignature"/> and the auto-default.
        /// Default false. Only useful against a legacy (pre-0.24) server — current servers reject
        /// Hash=0 on RPC dispatch.
        /// </summary>
        public bool DisableClientSignatureNegotiation { get; set; }

        /// <summary>
        /// 0.24.0+ Optional game-level callback invoked when the server reports the session
        /// is lost (typical cause: server restart cleared session state while the transport
        /// remained connected, then a Resume attempt returned
        /// <see cref="SharedMeta.Core.Transport.SessionConnectFailureReason.SessionUnknown"/>).
        /// Default <c>null</c> → dispatcher uses <see cref="DefaultSessionRecoveryHandler"/>
        /// which returns <see cref="SessionRecoveryAction.Reconnect"/> (re-subscribe to known
        /// entities on a fresh session). Override to plug in custom UX (e.g. "you've been
        /// disconnected, tap to reconnect").
        /// </summary>
        public IMetaSessionRecoveryHandler? SessionRecoveryHandler { get; set; }

        /// <summary>
        /// 0.26.7+ When true (default), <see cref="MetaClient"/> auto-subscribes a handler that
        /// routes <see cref="SharedMeta.Core.Transport.IClientDispatcher.OnConnectionStatusChanged"/>
        /// transitions to <see cref="SharedMeta.Core.Logging.MetaLog"/> at sensible levels
        /// (<c>Reconnecting</c>/<c>Disconnected</c> → Warning, <c>Failed</c> → Error, others → Info).
        /// Game-level handlers (UI overlays, reconnect modals) still cleanly subscribe alongside
        /// — <c>OnConnectionStatusChanged</c> is a multicast event. Opt out by setting this to
        /// false if your own handler does its own logging and you want a quiet console.
        /// </summary>
        public bool LogConnectionStatusToMetaLog { get; set; } = true;

        /// <summary>
        /// 0.30.1+ Re-acquirable token source (typically your <c>MetaTokenManager</c>). When set, the
        /// client auto-recovers from a connect the server rejected as unauthenticated — e.g. a cached
        /// access token that's still locally valid but signed with a now-changed JWT key: it calls
        /// <see cref="SharedMeta.Core.Auth.IAccessTokenSource.Invalidate"/> and retries the connect once
        /// (no need to wire <see cref="OnConnectAuthFailedAsync"/> yourself). Recovery only works if the
        /// connection reads its token from the same source's provider, not a fixed string:
        /// <code>
        /// var tokens = new MetaTokenManager(authUrl, deviceId, storage);
        /// var connection = new SignalRConnection(url, tokens.GetTokenAsync);
        /// var client = new MetaClient(connection, serializer, new MetaClientOptions { AccessTokenSource = tokens });
        /// </code>
        /// Also seeds <see cref="PlayerId"/> from the source when <see cref="PlayerId"/> isn't set — so
        /// acquire the first token (e.g. <c>await tokens.GetTokenAsync()</c>) before constructing the client.
        /// </summary>
        public SharedMeta.Core.Auth.IAccessTokenSource? AccessTokenSource { get; set; }

        /// <summary>
        /// 0.30.1+ Invoked when <see cref="MetaClient.ConnectAsync"/> fails (e.g. the server rejected a
        /// cached access token whose signing key changed — surfaced as "Authentication is required").
        /// Gives the host one chance to reacquire credentials: return <c>true</c> if a fresh token was
        /// obtained and the connect should be retried once, <c>false</c> to rethrow the original error.
        /// <para>
        /// Leave this null and set <see cref="AccessTokenSource"/> for the built-in default (invalidate +
        /// retry once on auth-type failures). Set this only to override that policy.
        /// </para>
        /// </summary>
        public Func<Exception, Task<bool>>? OnConnectAuthFailedAsync { get; set; }
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
        private readonly Func<Exception, Task<bool>>? _onConnectAuthFailedAsync;

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
            // Seed PlayerId from the token source when not set explicitly — UserOwned entities are
            // keyed by the player id, so it must be the authenticated id (available once a token has
            // been acquired), not a random fallback. Acquire the token before constructing the client
            // (e.g. await tokens.GetTokenAsync()) so AccessTokenSource.PlayerId is populated.
            PlayerId = options.PlayerId
                ?? options.AccessTokenSource?.PlayerId
                ?? Guid.NewGuid().ToString("N")[..8];
            ClientAppVersion = options.ClientAppVersion;
            // Explicit hook wins; otherwise a configured token source gives the built-in default:
            // on an auth-type connect failure, invalidate the source and retry the connect once.
            _onConnectAuthFailedAsync = options.OnConnectAuthFailedAsync
                ?? (options.AccessTokenSource is { } tokenSource
                    ? ex =>
                    {
                        if (!IsAuthFailure(ex)) return Task.FromResult(false);
                        tokenSource.Invalidate();
                        return Task.FromResult(true);
                    }
                    : (Func<Exception, Task<bool>>?)null);

            var modeProvider = options.ModeProvider ?? new ExecutionModeProvider();
            var diagnostics = options.Diagnostics;

            _dispatcher = new ClientDispatcher(connection)
            {
                SessionHealthListener = options.SessionHealth,
                ConnectionHealthListener = options.ConnectionHealth,
                // Raw passthrough; the dispatcher resolves the MetaClientSignature.Default
                // fallback at connect time (tolerates the auto-publish running post-construction).
                ClientSignature = options.ClientSignature,
                DisableClientSignatureNegotiation = options.DisableClientSignatureNegotiation,
                SessionRecoveryHandler = options.SessionRecoveryHandler ?? new DefaultSessionRecoveryHandler(),
            };
            if (options.ConnectionHealthOptions != null)
                _dispatcher.ConnectionHealthOptions = options.ConnectionHealthOptions;
            _dispatcher.OnSessionSuperseded += reason => OnSessionSuperseded?.Invoke(reason);
            _dispatcher.OnSubscriptionsReclaimed += verdicts => _resolver!.ApplySubscriptionVerdicts(verdicts);

            // 0.26.7+ Default connection-status logger — routes Reconnecting/Disconnected/Failed
            // transitions to MetaLog so noisy boilerplate doesn't show up in every game's startup
            // code. Game-level handlers (UI overlay, reconnect modal) compose via the multicast
            // event. Opt out via MetaClientOptions.LogConnectionStatusToMetaLog = false.
            if (options.LogConnectionStatusToMetaLog)
            {
                _dispatcher.OnConnectionStatusChanged += (status, detail) =>
                {
                    var msg = string.IsNullOrEmpty(detail)
                        ? $"Connection {status}"
                        : $"Connection {status}: {detail}";
                    switch (status)
                    {
                        case SharedMeta.Core.Transport.ConnectionStatus.Failed:
                            SharedMeta.Core.Logging.MetaLog.Error(msg);
                            break;
                        case SharedMeta.Core.Transport.ConnectionStatus.Reconnecting:
                        case SharedMeta.Core.Transport.ConnectionStatus.Disconnected:
                            SharedMeta.Core.Logging.MetaLog.Warning(msg);
                            break;
                        default:
                            SharedMeta.Core.Logging.MetaLog.Info(msg);
                            break;
                    }
                };
            }

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
        /// Connect transport and establish session with the server. On failure, if
        /// <see cref="MetaClientOptions.OnConnectAuthFailedAsync"/> is set it is invoked once to
        /// reacquire credentials (e.g. a cached token rejected because the server's signing key changed)
        /// and the connect is retried a single time.
        /// </summary>
        public async Task ConnectAsync()
        {
            try
            {
                await ConnectCoreAsync();
            }
            catch (Exception ex) when (_onConnectAuthFailedAsync != null)
            {
                bool retry = false;
                try { retry = await _onConnectAuthFailedAsync(ex); }
                catch (Exception hookEx)
                {
                    SharedMeta.Core.Logging.MetaLog.Warning("[MetaClient] OnConnectAuthFailedAsync threw: " + hookEx.Message);
                }
                if (!retry) throw; // rethrow the original connect failure

                SharedMeta.Core.Logging.MetaLog.Info("[MetaClient] Reauthenticated after connect failure — retrying connect once.");
                // Reconnect the transport so a provider-sourced token is re-read with the fresh
                // credentials (SignalR reads the token at the handshake), then retry the session connect.
                try { await Connection.DisconnectAsync(); } catch { /* best effort */ }
                await ConnectCoreAsync();
            }
        }

        private async Task ConnectCoreAsync()
        {
#if SHAREDMETA_CLIENT_TELEMETRY
            SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                new KeyValuePair<string, object?>("from", "disconnected"),
                new KeyValuePair<string, object?>("to", "connecting"));
            using var __connActivity = SharedMeta.Client.Telemetry.SharedMetaClientActivities.Source.StartActivity(
                SharedMeta.Client.Telemetry.SharedMetaClientActivities.SpanClientConnect);
#endif
            try
            {
                await Connection.ConnectAsync();

                _dispatcher.PlayerId = PlayerId;
                // 0.24.0+ Fresh app launch — explicit StartNew; server allocates the SessionId.
                // Pass Guid.Empty + Mode=StartNew so dispatcher's default-mode logic doesn't
                // misclassify the seed Guid as a resume attempt.
                var sessionResult = await _dispatcher.ConnectSessionAsync(Guid.Empty, 0, ClientAppVersion, SessionConnectMode.StartNew);

                if (!sessionResult.Success)
                {
#if SHAREDMETA_CLIENT_TELEMETRY
                    SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                        new KeyValuePair<string, object?>("from", "connecting"),
                        new KeyValuePair<string, object?>("to", "failed"));
#endif
                    throw new InvalidOperationException($"Failed to establish session: {sessionResult.Error}");
                }
#if SHAREDMETA_CLIENT_TELEMETRY
                SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                    new KeyValuePair<string, object?>("from", "connecting"),
                    new KeyValuePair<string, object?>("to", "connected"));
#endif
            }
            catch
            {
#if SHAREDMETA_CLIENT_TELEMETRY
                SharedMeta.Client.Telemetry.SharedMetaClientMeters.ConnectionStateTransition.Add(1,
                    new KeyValuePair<string, object?>("from", "connecting"),
                    new KeyValuePair<string, object?>("to", "failed"));
#endif
                throw;
            }
        }

        // Heuristic: does this connect failure look like the server rejecting the token (vs. a network
        // error)? Used to gate the built-in auto-reauth so a transient outage doesn't trigger a relogin.
        // SignalR surfaces the hub's HubException("Authentication is required") message to the client;
        // HTTP transports include the status code/word in their thrown message.
        private static bool IsAuthFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message;
                if (!string.IsNullOrEmpty(m) &&
                    (m.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0
                     || m.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
                     || m.IndexOf("401", StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
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
        /// Synchronous, allocation-free accessor for an already-resolved API client (true when the
        /// entity is subscribed and the client was already created via <see cref="GetServiceAsync{TApiClient}"/>;
        /// otherwise false). Never subscribes or allocates a Task — use on hot paths.
        /// </summary>
        public bool TryGetService<TApiClient>(string entityId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TApiClient? api) where TApiClient : class
        {
            return _resolver.TryGetService(entityId, out api);
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
        /// Debug/dev command: wipe every registered config provider's cache (e.g. the on-disk
        /// <see cref="FileConfigCache{TConfig}"/>). The next entity subscribe re-downloads the
        /// config. Use after re-publishing a config under the same version during development —
        /// the version-keyed cache would otherwise serve the stale copy. Safe to call anytime;
        /// no-op when no clearable config provider is registered.
        /// </summary>
        public void ClearConfigCaches() => _resolver.ClearConfigCaches();

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
