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
        bool localPersistToDisk = true)
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

        await ConnectRemoteAsync(serverUrl, deviceId, useHttpPolling, onStatus);
    }

    private async Task ConnectRemoteAsync(string serverUrl, string deviceId, bool useHttpPolling, Action<string> onStatus)
    {
        onStatus?.Invoke("Authenticating...");
        var tokenStorage = new PlayerPrefsTokenStorage();
        var login = await MetaAuth.EnsureAuthenticatedAsync($"{serverUrl}/meta/auth", deviceId, tokenStorage);
        onStatus?.Invoke($"Authenticated: {login.PlayerId}");

        Serializer = new MemoryPackMetaSerializer();

        IConnection connection;
        if (useHttpPolling)
        {
#if HAS_NEWTONSOFT_JSON
            connection = new UnityHttpConnection(new UnityHttpConnectionOptions
            {
                ServerUrl = $"{serverUrl}/meta-http",
                AccessToken = login.Token,
                ClientVersion = Application.version
            });
#else
            Debug.LogError("[MetaGameClient] HTTP polling requires com.unity.nuget.newtonsoft-json. Falling back to SignalR.");
            connection = new SignalRConnection($"{serverUrl}/meta", login.Token, clientVersion: Application.version);
#endif
        }
        else
        {
            connection = new SignalRConnection($"{serverUrl}/meta", login.Token, clientVersion: Application.version);
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

        Client = new MetaClient(connection, Serializer, new MetaClientOptions
        {
            PlayerId = login.PlayerId,
            Diagnostics = _diagnostics,
            ConnectionHealth = _connectionHealth
        });

        var writerCapture = _diagLogWriter;
        if (Client.Dispatcher is ClientDispatcher cd)
            cd.DiagnosticsLog = msg => { try { writerCapture?.WriteLine(msg); } catch { } };

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

        // LocalServer defaults every method to Optimistic. Mirror the [MetaMethod(Mode = ...)]
        // declarations from the service interfaces so ServerReplace, Server, and CrossOptimistic
        // behave identically to a real Orleans server.
        LocalServer.ExecutionModeProvider = new ExecutionModeProvider()
            .SetMode("IExpeditionProfileService", "SpendEnergy", ExecutionMode.Server)
            .SetMode("IExpeditionProfileService", "SpendEnergyUpTo", ExecutionMode.Server)
            .SetMode("IExpeditionProfileService", "AddMoney", ExecutionMode.Server)
            .SetMode("IExpeditionProfileService", "StartNewExpedition", ExecutionMode.Server)
            .SetMode("IExpeditionProfileService", "AbandonExpedition", ExecutionMode.Server)
            .SetMode("IExpeditionProfileService", "SendGift", ExecutionMode.Server)
            .SetMode("ISocialService", "ReceiveGift", ExecutionMode.Server)
            .SetMode("IExpeditionService", "Init", ExecutionMode.Server)
            .SetMode("IExpeditionService", "MarkAbandoned", ExecutionMode.Server)
            .SetMode("IExpeditionService", "Move", ExecutionMode.CrossOptimistic)
            .SetMode("IExpeditionService", "RemoveObstacle", ExecutionMode.CrossOptimistic)
            .SetMode("IExpeditionService", "GenerateNewMap", ExecutionMode.ServerReplace)
            .SetMode("IExpeditionService", "GenerateNewMapOptimistic", ExecutionMode.Optimistic)
            .SetMode("IExpeditionService", "GenerateNewMapBroken", ExecutionMode.Optimistic);

        var tokenStorage = new PlayerPrefsTokenStorage();
        var login = await MetaAuth.EnsureAuthenticatedAsync("local://", deviceId, tokenStorage);
        Connection = LocalServer.CreateConnection();

        Client = new MetaClient(Connection, Serializer, new MetaClientOptions
        {
            PlayerId = login.PlayerId,
            Diagnostics = _diagnostics,
            ConnectionHealth = _connectionHealth
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
