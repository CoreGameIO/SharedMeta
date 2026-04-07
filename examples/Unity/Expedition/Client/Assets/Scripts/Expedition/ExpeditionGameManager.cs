using System;
using System.Threading.Tasks;
using UnityEngine;
using SharedMeta.Client;
using SharedMeta.Client.Auth;
using SharedMeta.Client.Network;
using SharedMeta.Core;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Reactive;
using SharedMeta.Core.Transport;
using SharedMeta.Serialization.MemoryPack;
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

    [Header("References")]
    [SerializeField] private ExpeditionUIGenerator ui;
    [SerializeField] private ExpeditionMapView mapView;

    public MetaClient Client { get; private set; }
    public ProfileState ProfileState => Client?.GetProfileState();
    public ExpeditionState ExpeditionState => _expeditionEntityId != null ? Client?.GetState<ExpeditionState>(_expeditionEntityId) : null;
    public ExpeditionConfig Config => Client?.GetEntityConfig<ExpeditionConfig>(Client.PlayerId) ?? _defaultConfig;
    private static readonly ExpeditionConfig _defaultConfig = new();

    /// <summary>Fired when any tracked field changes or a broadcast is processed.</summary>
    public event Action OnStateUpdated;

    private string _expeditionEntityId;
    private ExpeditionProfileServiceApiClient _profileApi;
    private ExpeditionServiceApiClient _expApi;
    private ExpeditionServiceQueryApi _expeditionQuery;
    private bool _pendingRender;
    private ExpeditionDesyncDiagnostics _diagnostics;

    public ExpeditionProfileServiceApiClient ProfileApi => _profileApi;
    public ExpeditionServiceApiClient ExpeditionApi => _expApi;
    public bool IsConnected => Client != null && _expeditionEntityId != null;

    async void Start()
    {
        if (string.IsNullOrEmpty(deviceId))
            deviceId = SystemInfo.deviceUniqueIdentifier;

        await ConnectAsync();
    }

    void Update()
    {
        if (Client == null) return;

        // Process pending server broadcasts
        if (Client.Dispatcher.ProcessPendingBroadcasts() > 0)
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
    }

    void OnDestroy()
    {
        TrackedProfileState.Unregister();
        TrackedExpeditionState.Unregister();
        Client?.Dispose();
    }

    private async Task ConnectAsync()
    {
        try
        {
            ui.SetStatus("Authenticating...");

            var tokenStorage = new PlayerPrefsTokenStorage();
            var login = await MetaAuth.EnsureAuthenticatedAsync(
                $"{serverUrl}/meta/auth", deviceId, tokenStorage);
            ui.SetStatus($"Authenticated: {login.PlayerId}");

            var metaUrl = $"{serverUrl}/meta";
            var serializer = new MemoryPackMetaSerializer();
            var connection = new SignalRConnection(metaUrl, login.Token);

            _diagnostics = new ExpeditionDesyncDiagnostics(ui);
            Client = new MetaClient(
                connection, serializer,
                new MetaClientOptions
                {
                    PlayerId = login.PlayerId,
                    Diagnostics = _diagnostics
                }
            );

            Client.Resolver.RegisterAllServices();

            // Create query API (no subscription needed)
            _expeditionQuery = new ExpeditionServiceQueryApi(connection, serializer);

            // Register tracked field subscriptions BEFORE connecting
            TrackedProfileState.Register();
            TrackedProfileState.OnChanged += OnProfileTracked;

            TrackedExpeditionState.Register();
            TrackedExpeditionState.OnChanged += OnExpeditionTracked;

            Client.Dispatcher.OnConnectionStatusChanged += OnConnectionStatusChanged;

            ui.SetStatus("Connecting...");
            await Client.ConnectAsync();
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

                if (active) {
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
            ui.SetStatus($"Error: {ex.Message}");
            Debug.LogException(ex);
        }
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
