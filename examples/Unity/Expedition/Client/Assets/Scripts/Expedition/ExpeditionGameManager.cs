using System;
using System.Threading.Tasks;
using UnityEngine;
using SharedMeta.Client;
using SharedMeta.Client.Network;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Reactive;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;
using SharedMeta.Core.Network;
using Expedition.Shared;
using Expedition.Shared.Client;

/// <summary>
/// Main game manager: authenticates, connects to server, manages expedition lifecycle.
/// Demonstrates Query calls (check expedition without subscribing) and generation mode choice
/// (ServerReplace vs Optimistic).
/// UI updates are driven by [Tracked] field subscriptions.
/// </summary>
public class ExpeditionGameManager : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private string serverUrl = "http://localhost:5000";
    [SerializeField] private string deviceId = "";

#if SHAREDMETA_BACKEND_LOCAL
    [Header("Local Backend")]
    [Tooltip("Run entirely in-process via SharedMeta.Backend.Local — no network, no server. Install the com.coregame.sharedmeta.backend.local UPM package to enable this section.")]
    [SerializeField] private bool useLocalBackend = false;
    [Tooltip("Persist local state to disk under Application.persistentDataPath/expedition-saves.")]
    [SerializeField] private bool localPersistToDisk = true;
#endif

    [Header("References")]
    [SerializeField] private ExpeditionUIGenerator ui;
    [SerializeField] private ExpeditionMapView mapView;

    public MetaClient Client => _metaClient?.Client;
    public ProfileState ProfileState => Client?.GetProfileState();
    public ExpeditionState ExpeditionState => _expeditionEntityId != null ? Client?.GetState<ExpeditionState>(_expeditionEntityId) : null;
    public ExpeditionConfig Config => Client?.GetEntityConfig<ExpeditionConfig>(Client.PlayerId) ?? _defaultConfig;
    private static readonly ExpeditionConfig _defaultConfig = new();

    /// <summary>Fired when any tracked field changes or a broadcast is processed.</summary>
    public event Action OnStateUpdated;

    private MetaGameClient _metaClient;
    private string _expeditionEntityId;
    private ExpeditionProfileServiceApiClient _profileApi;
    private ExpeditionServiceApiClient _expApi;
    private ExpeditionServiceQueryApi _expeditionQuery;
    private bool _pendingRender;
    private ExpeditionDesyncDiagnostics _diagnostics;
    private ExpeditionConnectionHealth _connectionHealth;
    private bool _trackedRegistered;

    // Debug network simulation (editor/dev builds only) — forwarded from _metaClient
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public DebugConnectionSettings DebugNetworkSettings => _metaClient?.DebugNetworkSettings;
    public DebugConnectionWrapper DebugConnection => _metaClient?.DebugConnection;
#endif

    public ExpeditionProfileServiceApiClient ProfileApi => _profileApi;
    public ExpeditionServiceApiClient ExpeditionApi => _expApi;
    public bool IsConnected => Client != null && _expeditionEntityId != null && !(_connectionHealth?.IsBlocked == true);

    private bool _useHttpPolling;

    /// <summary>
    /// Override for the version reported to the server during SessionConnect.
    /// Set by the version-picker UI to demonstrate the cluster gate (1.0 → reject),
    /// per-client config delivery (1.2 = lean, 2.0 = boosted), and per-PlayerId downgrade gate.
    /// Null/empty falls back to <c>Application.version</c>.
    /// </summary>
    public string ClientVersionOverride { get; set; }

    void Start()
    {
        MetaLog.SetLogger(new UnityConsoleMetaLogger());

        if (string.IsNullOrEmpty(deviceId))
            deviceId = SystemInfo.deviceUniqueIdentifier;

#if SHAREDMETA_BACKEND_LOCAL
        if (useLocalBackend)
        {
            // Local backend has no network transport — skip the SignalR/HTTP choice UI
            // and go straight to ConnectAsync, which branches on useLocalBackend.
            _ = ConnectAsync();
            return;
        }
#endif

        // Show transport choice — ConnectWithTransport will be called from UI
        ui.ShowTransportChoice();
    }

    float _pingTimer = 5f;

    void Update()
    {
        if (Client == null) return;

        // Process pending server broadcasts
        if (_metaClient.ProcessPendingBroadcasts() > 0)
            _pendingRender = true;

        if (_pendingRender)
        {
            _pendingRender = false;
            OnStateUpdated?.Invoke();
        }

        // Drain pending desync messages from background threads
        var desyncMsg = _diagnostics?.DrainPendingMessage();
        if (!string.IsNullOrEmpty(desyncMsg))
            ui?.SetStatus(desyncMsg);

        // Drain connection health updates
        var healthMsg = _connectionHealth?.DrainPendingMessage();
        if (healthMsg != null)
            ui?.SetConnectionHealth(healthMsg, modal: _connectionHealth.IsBlocked);

        // Update request tracking display
        if (Client.Dispatcher is SharedMeta.Client.ClientDispatcher cd)
            ui?.UpdateRequestTracking(cd.CurrentRequestId, cd.LastCompletedRequestId, cd.PendingRequestCount);

        _pingTimer -= Time.deltaTime;
        if (_pingTimer <= 0 && ProfileApi != null)
        {
            _pingTimer = 5f;
            ProfileApi.PingSignal($"Current time: {Time.time}");
        }
    }

#if SHAREDMETA_BACKEND_LOCAL
    async void OnDestroy()
#else
    void OnDestroy()
#endif
    {
        if (_trackedRegistered)
        {
            TrackedProfileState.Unregister();
            TrackedExpeditionState.Unregister();
        }
#if SHAREDMETA_BACKEND_LOCAL
        if (_metaClient?.LocalServer != null)
        {
            try { await _metaClient.LocalServer.SaveAllAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
#endif
        _metaClient?.Dispose();
    }

    /// <summary>
    /// Called from UI when transport is chosen. Initiates authentication and connection.
    /// </summary>
    public async Task ConnectWithTransport(bool useHttpPolling)
    {
        _useHttpPolling = useHttpPolling;
        await ConnectAsync();
    }

    /// <summary>
    /// Try to resume the current session after connection health goes Unresponsive.
    /// Attempts session resume first (same sessionId, missed packet recovery).
    /// Falls back to full session restart if resume fails.
    /// </summary>
    public async Task ReconnectAsync()
    {
        // No client yet — initial ConnectAsync failed. Replay it with the last transport choice.
        if (Client == null)
        {
            ui.SetConnectionHealth("");
            await ConnectAsync();
            return;
        }

        try
        {
            ui.SetStatus("Resuming session...");
            ui.SetConnectionHealth("");

            try
            {
                // Try resume — same sessionId, server returns missed packets
                await Client.ResumeSessionAsync();
                ui.SetStatus("Session resumed!");
                _pendingRender = true;
                return;
            }
            catch (Exception resumeEx)
            {
                Debug.LogWarning($"[Expedition] Session resume failed, restarting: {resumeEx.Message}");
            }

            // Fallback: full restart (new session, re-subscribe from scratch)
            ui.SetStatus("Restarting session...");
            await Client.RestartSessionAsync();
            ui.SetStatus("Reconnected! Reloading...");

            _profileApi = await Client.GetExpeditionProfileServiceAsync();
            await _profileApi.UpdateEnergyAsync();

            var currentExpId = ProfileState?.CurrentExpeditionEntityId;
            if (!string.IsNullOrEmpty(currentExpId))
            {
                _expeditionEntityId = currentExpId;
                _expApi = await Client.GetServiceAsync<ExpeditionServiceApiClient>(_expeditionEntityId);
                ui.SetStatus("Reconnected!");
            }
            else
            {
                ui.ShowGenerationModeChoice();
            }

            _pendingRender = true;
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Reconnect failed: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            // Dispose any previous client before creating a new one (e.g. retry after failure)
            _metaClient?.Dispose();

            _diagnostics = new ExpeditionDesyncDiagnostics(ui);
            _connectionHealth = new ExpeditionConnectionHealth(ui);
            _metaClient = new MetaGameClient(_diagnostics, _connectionHealth);

            var useLocalBackendOpt = false;
            var localPersistToDiskOpt = true;
#if SHAREDMETA_BACKEND_LOCAL
            useLocalBackendOpt = useLocalBackend;
            localPersistToDiskOpt = localPersistToDisk;
#endif
            await _metaClient.ConnectAsync(serverUrl, deviceId, _useHttpPolling, ui.SetStatus, useLocalBackendOpt, localPersistToDiskOpt, ClientVersionOverride);


            // Register tracked field subscriptions once — static, survive reconnects
            if (!_trackedRegistered)
            {
                TrackedProfileState.Register();
                TrackedProfileState.OnChanged += OnProfileTracked;
                TrackedExpeditionState.Register();
                TrackedExpeditionState.OnChanged += OnExpeditionTracked;
                _trackedRegistered = true;
            }

            Client.Dispatcher.OnConnectionStatusChanged += OnConnectionStatusChanged;

            // Create query API using the established connection and serializer
            _expeditionQuery = new ExpeditionServiceQueryApi(_metaClient.Connection, _metaClient.Serializer);

            ui.SetStatus("Connected! Loading profile...");
            _profileApi = await Client.GetExpeditionProfileServiceAsync();
            await _profileApi.UpdateEnergyAsync();

            // Check if there's an existing expedition via Query (no subscription)
            var currentExpId = ProfileState?.CurrentExpeditionEntityId;
            if (!string.IsNullOrEmpty(currentExpId))
            {
                ui.SetStatus("Checking expedition status...");
                var active = await _expeditionQuery
                    .EntityApi(currentExpId)
                    .IsActiveAsync();

                if (active)
                {
                    // Resume — subscribe to existing expedition
                    _expeditionEntityId = currentExpId;
                    _expApi = await Client.GetServiceAsync<ExpeditionServiceApiClient>(_expeditionEntityId);
                    ui.SetStatus("Expedition resumed!");
                    _pendingRender = true;
                    return;
                }

                // Expedition is complete — prompt for new one
                ui.ShowGenerationModeChoice();
                return;
            }

            // No expedition yet — prompt for generation mode
            ui.ShowGenerationModeChoice();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);

            // Tear down the half-initialised client so the retry starts clean.
            if (_metaClient != null)
            {
                try { _metaClient.Dispose(); } catch { }
                _metaClient = null;
            }

            // Extract the most informative message — version-gate rejections include the
            // server's verdict in the message, so surface it directly. Strip the
            // "Failed to establish session:" prefix from MetaClient.ConnectAsync.
            string detailMsg = ExtractInnermostMessage(ex);
            const string sessionPrefix = "Failed to establish session: ";
            if (detailMsg.StartsWith(sessionPrefix))
                detailMsg = detailMsg.Substring(sessionPrefix.Length);

            var friendly = IsConnectionError(ex)
                ? $"Server unreachable at {serverUrl}.\nMake sure the server is running, then press Reconnect."
                : LooksLikeVersionGate(detailMsg)
                    ? $"⚠ Version gate\n\n{detailMsg}\n\nChange the client version selector and press Reconnect."
                    : $"Connection failed: {detailMsg}";
            ui.SetStatus("");
            // Modal overlay with the existing Reconnect button — click routes back into
            // ReconnectAsync, which detects Client == null and replays ConnectAsync.
            ui.SetConnectionHealth(friendly, modal: true);
        }
    }

    private static bool IsConnectionError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException) return true;
            if (e is System.Net.WebException) return true;
            if (e is System.Net.Http.HttpRequestException) return true;
        }
        return false;
    }

    /// <summary>Walk the inner-exception chain and return the deepest non-empty message.</summary>
    private static string ExtractInnermostMessage(Exception ex)
    {
        string msg = ex.Message ?? "";
        for (var e = ex.InnerException; e != null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message)) msg = e.Message;
        return msg;
    }

    /// <summary>Heuristic: does this look like a server-side version-gate rejection?</summary>
    private static bool LooksLikeVersionGate(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("Client version", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("client version", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("MinClientVersion", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("MaxClientVersion", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("profile was last used", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("app version is too old", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("incompatible major version", StringComparison.OrdinalIgnoreCase);
    }

    private void OnProfileTracked(ChangeTreeArgs args)
    {
        _pendingRender = true;

        if (args.HasChange((int)TrackingProperty.ProfileState_Energy))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ProfileState_Energy);
            if (leaf != null)
                Debug.Log($"[Tracked] Energy: {leaf.Value.OldValue.IntValue} -> {leaf.Value.NewValue.IntValue}");
        }

        if (args.HasChange((int)TrackingProperty.ProfileState_Money))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ProfileState_Money);
            if (leaf != null)
                Debug.Log($"[Tracked] Money: {leaf.Value.OldValue.IntValue} -> {leaf.Value.NewValue.IntValue}");
        }
    }

    private void OnExpeditionTracked(ChangeTreeArgs args)
    {
        _pendingRender = true;

        if (args.HasChange((int)TrackingProperty.ExpeditionState_TreasuresCollected))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ExpeditionState_TreasuresCollected);
            if (leaf != null)
                Debug.Log($"[Tracked] Treasures: {leaf.Value.OldValue.IntValue} -> {leaf.Value.NewValue.IntValue}");
        }

        if (args.HasChange((int)TrackingProperty.ExpeditionState_IsComplete))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ExpeditionState_IsComplete);
            if (leaf != null && leaf.Value.NewValue.IntValue != 0)
                Debug.Log("[Tracked] Expedition complete!");
        }
    }

    private void OnConnectionStatusChanged(ConnectionStatus status, string detail)
    {
        switch (status)
        {
            case ConnectionStatus.Reconnecting:
                ui.SetStatus("Reconnecting...");
                break;
            case ConnectionStatus.Reconnected:
                ui.SetStatus("Reconnected!");
                _pendingRender = true;
                break;
            case ConnectionStatus.Connected:
                ui.SetStatus("");
                break;
            case ConnectionStatus.Failed:
                ui.SetStatus($"Connection failed: {detail}");
                break;
        }
    }

    public enum GenerationMode { ServerReplace, Optimistic, Broken }

    /// <summary>
    /// Create a new expedition with the chosen generation mode.
    /// </summary>
    public async Task StartNewExpedition(GenerationMode mode)
    {
        try
        {
            ui.SetStatus("Creating expedition...");

            var entityId = await _profileApi.StartNewExpeditionAsync();
            _expeditionEntityId = entityId;
            _expApi = await Client.GetServiceAsync<ExpeditionServiceApiClient>(_expeditionEntityId);

            switch (mode)
            {
                case GenerationMode.ServerReplace:
                    await _expApi.GenerateNewMapAsync();
                    ui.SetStatus("New expedition (ServerReplace) — server generated map!");
                    break;
                case GenerationMode.Optimistic:
                    await _expApi.GenerateNewMapOptimisticAsync();
                    ui.SetStatus("New expedition (Optimistic) — client predicted map!");
                    break;
                case GenerationMode.Broken:
                    await _expApi.GenerateNewMapBrokenAsync();
                    ui.SetStatus("New expedition (Broken) — System.Random, desync expected!");
                    break;
            }

            _pendingRender = true;
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    public async Task<MoveResult> Move(int dx, int dy)
    {
        try
        {
            var result = await _expApi.MoveAsync(dx, dy);
            _pendingRender = true; // map needs re-render (PlayerX/Y, Revealed not tracked)
            return result;
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
            return MoveResult.Blocked;
        }
    }

    public async Task<bool> RemoveObstacle(int dx, int dy)
    {
        try
        {
            var result = await _expApi.RemoveObstacleAsync(dx, dy);
            _pendingRender = true; // map needs re-render (Cells changed)
            return result;
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Abandon the current expedition (e.g. stuck in a dead end). Server marks it complete
    /// and clears the profile reference; the UI returns to the new-expedition prompt.
    /// </summary>
    public async Task AbandonExpedition()
    {
        if (_expApi == null)
            return;

        try
        {
            ui.SetStatus("Abandoning expedition...");
            bool abandoned = await _profileApi.AbandonExpeditionAsync();
            if (!abandoned)
            {
                ui.SetStatus("No active expedition to abandon.");
                return;
            }

            _expeditionEntityId = null;
            _expApi = null;
            _pendingRender = true;
            ui.SetStatus("Expedition abandoned.");
            ui.ShowGenerationModeChoice();
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    public async Task BuyEnergy()
    {
        try
        {
            bool bought = await _profileApi.BuyEnergyAsync();
            ui.SetStatus(bought ? "Bought energy!" : "Not enough money!");
            // Energy/Money update via TrackedProfileState subscription
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
        }
    }

    public async Task UpdateEnergy()
    {
        try
        {
            await _profileApi.UpdateEnergyAsync();
            ui.SetStatus("Energy updated.");
            // Energy update via TrackedProfileState subscription
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends <paramref name="amount"/> coins to <paramref name="targetPlayerId"/>'s profile.
    /// Demonstrates the 0.14.0 foreign-service-replay path: the server-side mutation runs
    /// through ISocialService, which the receiver's client typically does NOT subscribe to.
    /// The receiver's profile UI updates live via the [Tracked] Money setter, applied by
    /// the entity-level handler in MetaServiceResolver.
    /// </summary>
    public async Task SendGift(string targetPlayerId, int amount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetPlayerId))
            {
                ui.SetStatus("Enter a target player ID.");
                return;
            }
            if (amount <= 0)
            {
                ui.SetStatus("Amount must be positive.");
                return;
            }

            ui.SetStatus($"Sending {amount} to {targetPlayerId}...");
            bool ok = await _profileApi.SendGiftAsync(targetPlayerId, amount);
            ui.SetStatus(ok ? $"Sent {amount} to {targetPlayerId}." : "Not enough money or invalid target.");
        }
        catch (Exception ex)
        {
            ui.SetStatus($"SendGift failed: {ex.Message}");
            Debug.LogException(ex);
        }
    }
}

/// <summary>
/// Diagnostics handler for the Expedition client.
/// Logs desyncs to console and shows them in the UI status.
/// </summary>
internal class ExpeditionDesyncDiagnostics : SharedMeta.Core.Diagnostics.IDesyncDiagnostics
{
    private readonly ExpeditionUIGenerator _ui;
    // Pending desync message from background thread (ContinueWith); drained on main thread.
    private string _pendingMessage;
    public string DrainPendingMessage()
    {
        var m = _pendingMessage;
        _pendingMessage = null;
        return m;
    }

    public ExpeditionDesyncDiagnostics(ExpeditionUIGenerator ui)
    {
        _ui = ui;
    }

    public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
    {
        var msg = $"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}";
        Debug.LogError(msg);
        _pendingMessage = msg;
    }

    public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[] resultBytes) { }

    public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
    {
        var msg = $"[RANDOM DESYNC] {serviceName}.{methodName}: server={serverDelta}, local={localDelta}";
        Debug.LogError(msg);
        _pendingMessage = msg;
    }

    public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
    {
        var msg = $"[PATCH DESYNC] {serviceName}.{methodName}: serverCrc=0x{serverCrc:X8}, localCrc=0x{localCrc:X8}";
        Debug.LogError(msg);
        _pendingMessage = msg;
    }

    public Task<SharedMeta.Core.Diagnostics.StateComparisonResult> CompareFullStateAsync(string entityId)
        => Task.FromResult(new SharedMeta.Core.Diagnostics.StateComparisonResult { IsMatch = true });
}

/// <summary>
/// Connection health listener for the Expedition client.
/// Converts health status changes to UI messages, drained on main thread.
/// </summary>
internal class ExpeditionConnectionHealth : IConnectionHealthListener
{
    private readonly ExpeditionUIGenerator _ui;
    private string _pendingMessage;
    private bool _hasPending;

    /// <summary>True when connection is Unresponsive — game should block input.</summary>
    public bool IsBlocked { get; private set; }

    public ExpeditionConnectionHealth(ExpeditionUIGenerator ui) => _ui = ui;

    /// <summary>
    /// Returns the pending health message (null = no update, "" = clear overlay).
    /// </summary>
    public string DrainPendingMessage()
    {
        if (!_hasPending) return null;
        _hasPending = false;
        return _pendingMessage;
    }

    public void OnConnectionHealthChanged(ConnectionHealthStatus status, long oldestPendingMs, int pendingCount)
    {
        switch (status)
        {
            case ConnectionHealthStatus.Slow:
                _pendingMessage = $"Syncing... ({pendingCount} pending, {oldestPendingMs}ms)";
                IsBlocked = false;
                break;
            case ConnectionHealthStatus.Unresponsive:
                _pendingMessage = $"Connection issue! ({pendingCount} pending, {oldestPendingMs / 1000}s)";
                IsBlocked = true;
                break;
            case ConnectionHealthStatus.Healthy:
                _pendingMessage = "";
                IsBlocked = false;
                break;
        }
        _hasPending = true;
        Debug.Log($"[ConnectionHealth] {status}: {pendingCount} pending, oldest={oldestPendingMs}ms");
    }
}

internal sealed class UnityConsoleMetaLogger : IMetaLogger
{
    public bool IsEnabled(MetaLogLevel level) => true;

    public void Log(MetaLogLevel level, string message)
    {
        switch (level)
        {
            case MetaLogLevel.Error:   Debug.LogError($"[SharedMeta] {message}");   break;
            case MetaLogLevel.Warning: Debug.LogWarning($"[SharedMeta] {message}"); break;
            default:                   Debug.Log($"[SharedMeta] {message}");        break;
        }
    }

    public void Log(MetaLogLevel level, string message, Exception exception)
    {
        switch (level)
        {
            case MetaLogLevel.Error:   Debug.LogError($"[SharedMeta] {message}\n{exception}");   break;
            case MetaLogLevel.Warning: Debug.LogWarning($"[SharedMeta] {message}\n{exception}"); break;
            default:                   Debug.Log($"[SharedMeta] {message}\n{exception}");        break;
        }
    }
}
