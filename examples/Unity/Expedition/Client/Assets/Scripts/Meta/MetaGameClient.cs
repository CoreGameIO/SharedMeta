using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SharedMeta.Client;
using SharedMeta.Client.Auth;
using SharedMeta.Client.Network;
using SharedMeta.Core;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Network;
using SharedMeta.Core.Transport;
using SharedMeta.Serialization.MemoryPack;
using Expedition.Shared;
using Expedition.Shared.Client;
#if SHAREDMETA_BACKEND_LOCAL
using SharedMeta.Backend.Local;
#endif

/// <summary>
/// Owns MetaClient lifecycle for the Expedition example: auth, transport selection, connection,
/// broadcast pumping, and local-backend setup. Single ConnectAsync entry point — the
/// useLocalBackend flag picks between the real server and the in-process backend.
/// Plain C# class, no MonoBehaviour dependency.
/// </summary>
public class MetaGameClient : IDisposable
{
    public MetaClient Client { get; private set; }
    public IMetaSerializer Serializer { get; private set; }
    public IConnection Connection { get; private set; }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public DebugConnectionSettings DebugNetworkSettings { get; private set; }
    public DebugConnectionWrapper DebugConnection { get; private set; }
#endif

#if SHAREDMETA_BACKEND_LOCAL
    public LocalServer LocalServer { get; private set; }
#endif

    private readonly IDesyncDiagnostics _diagnostics;
    private readonly IConnectionHealthListener _connectionHealth;
    private StreamWriter _diagLogWriter;

    public MetaGameClient(IDesyncDiagnostics diagnostics = null, IConnectionHealthListener connectionHealth = null)
    {
        _diagnostics = diagnostics;
        _connectionHealth = connectionHealth;
    }

    public async Task ConnectAsync(
        string serverUrl,
        string deviceId,
        bool useHttpPolling,
        Action<string> onStatus = null,
        bool useLocalBackend = false,
        bool localPersistToDisk = true,
        string clientVersionOverride = null)
    {
        if (useLocalBackend)
        {
#if SHAREDMETA_BACKEND_LOCAL
            await ConnectLocalAsync(deviceId, localPersistToDisk, onStatus);
            return;
#else
            throw new InvalidOperationException(
                "Local backend is not available — install com.coregame.sharedmeta.backend.local " +
                "to enable in-process play.");
#endif
        }

        await ConnectRemoteAsync(serverUrl, deviceId, useHttpPolling, onStatus, clientVersionOverride);
    }

    private async Task ConnectRemoteAsync(string serverUrl, string deviceId, bool useHttpPolling, Action<string> onStatus, string clientVersionOverride = null)
    {
        onStatus?.Invoke("Authenticating...");
        // Scope token cache by deviceId so dev builds with random/per-instance deviceIds
        // each get their own JWT slot and can't pick up a token cached for a different PlayerId.
        var tokenStorage = new PlayerPrefsTokenStorage(deviceId);
        var tokens = new MetaTokenManager($"{serverUrl}/meta/auth", deviceId, tokenStorage);
        // Acquire the token up front (on the main thread) so we learn our PlayerId — UserOwned
        // entities are keyed by it, so MetaClientOptions.PlayerId MUST be the authenticated id, not
        // the random default. The token is cached; the connection's provider then just reuses it.
        await tokens.GetTokenAsync();
        onStatus?.Invoke($"Authenticated: {tokens.PlayerId}");

        Serializer = new MemoryPackMetaSerializer();

        // Allow the demo UI to override the client version reported to the server.
        // This lets a single Unity build act as 1.0 / 1.2 / 2.0 to exercise the cluster gate,
        // per-client config delivery, per-PlayerId downgrade gate, and per-entity migration gate.
        var effectiveClientVersion = string.IsNullOrEmpty(clientVersionOverride)
            ? Application.version
            : clientVersionOverride;

        IConnection connection;
        if (useHttpPolling)
        {
#if HAS_NEWTONSOFT_JSON
            connection = new UnityHttpConnection(new UnityHttpConnectionOptions
            {
                ServerUrl = $"{serverUrl}/meta-http",
                AccessTokenProvider = tokens.GetTokenAsync,
                ClientVersion = effectiveClientVersion
            });
#else
            Debug.LogError("[MetaGameClient] HTTP polling requires com.unity.nuget.newtonsoft-json. Falling back to SignalR.");
            connection = new SignalRConnection($"{serverUrl}/meta", tokens.GetTokenAsync, clientVersion: effectiveClientVersion);
#endif
        }
        else
        {
            connection = new SignalRConnection($"{serverUrl}/meta", tokens.GetTokenAsync, clientVersion: effectiveClientVersion);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        DebugNetworkSettings = new DebugConnectionSettings
        {
            Enabled = false,
            LossMode = useHttpPolling ? PacketLossMode.RequestHang : PacketLossMode.ConnectionDrop
        };
        DebugConnection = new DebugConnectionWrapper(connection, DebugNetworkSettings);
        connection = DebugConnection;
#endif

        Connection = connection;
        RotateDiagnosticsLog();

        Client = new MetaClient(connection, Serializer, new MetaClientOptions {
            // PlayerId is seeded from AccessTokenSource (tokens.PlayerId, known after the acquire above)
            // — required for UserOwned entities, which are keyed by the authenticated player id.
            Diagnostics = _diagnostics,
            ConnectionHealth = _connectionHealth,
            ClientSignature = GameServiceDiscoveryBase.ClientSignature,
            AccessTokenSource = tokens
        });

        var writerCapture = _diagLogWriter;
        if (Client.Dispatcher is ClientDispatcher cd)
            cd.DiagnosticsLog = msg => { try { writerCapture?.WriteLine(msg); } catch { } };

        // Pull ExpeditionConfig from the server's /meta/config/{major}/{minor} endpoint so the
        // client receives the actual branch configured for its clientVersion (lean v1 vs boosted v2)
        // — without this the auto-registered StaticConfigProvider<ExpeditionConfig>(new()) would
        // hand out the C# field defaults and the v1/v2 difference wouldn't surface in gameplay.
        // RegisterConfigProvider clobbers, so order vs. RegisterAllServices doesn't matter.
        Client.Resolver.RegisterConfigProvider<ExpeditionConfig>(
            new DownloadingConfigProvider<ExpeditionConfig>(
                urlResolver: Client.ConfigDownloadUrlResolver(typeof(ExpeditionState).FullName!),
                downloader:  UnityConfigDownloader.DownloadAsync,
                serializer:  Client.Serializer));

        Client.Resolver.RegisterAllServices();

        onStatus?.Invoke("Connecting...");
        await Client.ConnectAsync();
    }

#if SHAREDMETA_BACKEND_LOCAL
    private async Task ConnectLocalAsync(string deviceId, bool persistToDisk, Action<string> onStatus)
    {
        onStatus?.Invoke("Initializing local backend...");
        LocalMetaAuthProvider.Install();

        Serializer = new MemoryPackMetaSerializer();

        ILocalBackend backend = persistToDisk
            ? (ILocalBackend)new FileLocalBackend(
                Path.Combine(Application.persistentDataPath, "expedition-saves"))
            : new InMemoryLocalBackend();

        LocalServer = new LocalServer(Serializer, backend);

        // Generated by SharedMeta.Backend.Local source generator — wires up dispatchers,
        // [MetaInit] handlers, and service-name → state-type mapping for every [MetaServiceImpl]
        // it discovers in this assembly's references.
        LocalServer.RegisterAllEntityTypes();

        // Static configs — LocalServer doesn't have IMetaConfigProvider auto-discovery,
        // so register the same config object the real server's IMetaConfigProvider returns.
        var config = new ExpeditionConfig();
        LocalServer.RegisterConfig<ProfileState>(config);
        LocalServer.RegisterConfig<ExpeditionState>(config);

        var tokenStorage = new PlayerPrefsTokenStorage(deviceId);
        var login = await MetaAuth.EnsureAuthenticatedAsync("local://", deviceId, tokenStorage);
        Connection = LocalServer.CreateConnection();

        Client = new MetaClient(Connection, Serializer, new MetaClientOptions
        {
            PlayerId = login.PlayerId,
            Diagnostics = _diagnostics,
            ConnectionHealth = _connectionHealth,
            ClientSignature = Expedition.Shared.GameServiceDiscoveryBase.ClientSignature,
        });
        Client.Resolver.RegisterAllServices();

        onStatus?.Invoke($"Local backend ready: {login.PlayerId}");
        await Client.ConnectAsync();
    }
#endif

    public int ProcessPendingBroadcasts() =>
        Client?.Dispatcher?.ProcessPendingBroadcasts() ?? 0;

    public void Dispose()
    {
        Client?.Dispose();
        if (_diagLogWriter != null)
        {
            try { _diagLogWriter.Dispose(); } catch { }
            _diagLogWriter = null;
        }
    }

    private void RotateDiagnosticsLog()
    {
        if (_diagLogWriter != null)
        {
            try { _diagLogWriter.Dispose(); } catch { }
            _diagLogWriter = null;
        }
        // Per-process log file — running two Unity instances side by side (the gift demo) means
        // two writers want the same path; without a unique suffix the second one hits a sharing
        // violation. ProcessId keeps it deterministic across reconnects within the same instance.
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var logPath = Path.Combine(Application.persistentDataPath, $"connection_diag-{pid}.log");
        Debug.Log($"[Expedition] Diagnostics log: {logPath}");
        try
        {
            _diagLogWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        }
        catch (IOException ex)
        {
            // Fallback for the rare case where even the per-pid path is locked (e.g. recycled pid).
            // Diagnostics-only signal — never fail Connect because of it.
            Debug.LogWarning($"[Expedition] Diagnostics log unavailable: {ex.Message}");
            _diagLogWriter = null;
        }
    }
}
