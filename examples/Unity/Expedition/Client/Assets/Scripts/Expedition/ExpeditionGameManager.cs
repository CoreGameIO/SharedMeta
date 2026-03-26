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

            Client = new MetaClient(
                connection, serializer,
                new MetaClientOptions { PlayerId = login.PlayerId }
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

    /// <summary>
    /// Create a new expedition with the chosen generation mode.
    /// ServerReplace: server generates map, client receives full state.
    /// Optimistic: client predicts generation with same deterministic random seed.
    /// </summary>
    public async Task StartNewExpedition(bool useServerReplace)
    {
        try
        {
            ui.SetStatus("Creating expedition...");

            // 1. Create expedition entity on server (via profile service)
            var entityId = await _profileApi.StartNewExpeditionAsync();
            _expeditionEntityId = entityId;

            // 2. Subscribe to the new expedition
            _expApi = await Client.GetServiceAsync<ExpeditionServiceApiClient>(_expeditionEntityId);

            // 3. Generate map with chosen mode
            if (useServerReplace)
            {
                await _expApi.GenerateNewMapAsync();
                ui.SetStatus("New expedition (ServerReplace) — server generated map!");
            }
            else
            {
                await _expApi.GenerateNewMapOptimisticAsync();
                ui.SetStatus("New expedition (Optimistic) — client predicted map!");
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
