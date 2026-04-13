using UnityEngine;
using UnityEngine.UI;
using Expedition.Shared;

/// <summary>
/// Generates all UI elements at runtime: energy bar, money display, treasure counter,
/// status text, and control buttons. Updates reactively via ExpeditionGameManager.OnStateUpdated
/// which is triggered by [Tracked] field change subscriptions.
/// </summary>
public class ExpeditionUIGenerator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ExpeditionGameManager gameManager;
    [SerializeField] private ExpeditionMapView mapView;

    // Generated UI elements
    private Canvas _canvas;
    private Text _energyText;
    private Image _energyBarFill;
    private Text _moneyText;
    private Text _treasureText;
    private Text _statusText;
    private Text _controlsHintText;
    private Button _buyEnergyButton;
    private Button _updateEnergyButton;
    private Button _newExpeditionButton;
    private Button _abandonButton;
    private GameObject _completeBanner;

    // Generation mode choice
    private GameObject _generationChoicePanel;
    private Button _serverReplaceButton;
    private Button _optimisticButton;

    // Transport choice (shown at startup)
    private GameObject _transportChoicePanel;

    // Connection health overlay
    private GameObject _connectionHealthPanel;
    private Text _connectionHealthText;
    private Button _reconnectButton;
    private GameObject _modalBlocker; // full-screen blocker behind health panel

    // Request tracking (bottom status area)
    private Text _requestTrackingText;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Debug network controls
    private GameObject _debugNetPanel;
    private Text _debugLatencyLabel;
    private Text _debugLossLabel;
#endif

    // D-Pad buttons
    private Button _btnUp;
    private Button _btnDown;
    private Button _btnLeft;
    private Button _btnRight;
    private Toggle _removeObstacleToggle;

    void Awake()
    {
        GenerateUI();
    }

    void OnEnable()
    {
        if (gameManager != null)
            gameManager.OnStateUpdated += RefreshUI;
    }

    void OnDisable()
    {
        if (gameManager != null)
            gameManager.OnStateUpdated -= RefreshUI;
    }

    public void SetStatus(string message)
    {
        if (_statusText != null)
            _statusText.text = message;
    }

    /// <summary>
    /// Show or hide the connection health overlay. Empty string hides it.
    /// </summary>
    public void SetConnectionHealth(string message, bool modal = false)
    {
        if (_connectionHealthPanel == null) return;
        if (string.IsNullOrEmpty(message))
        {
            _connectionHealthPanel.SetActive(false);
            if (_modalBlocker != null) _modalBlocker.SetActive(false);
        }
        else
        {
            _connectionHealthText.text = message;
            _connectionHealthPanel.SetActive(true);
            if (_modalBlocker != null) _modalBlocker.SetActive(modal);
        }
    }

    private void GenerateUI()
    {
        // Canvas
        var canvasGo = new GameObject("ExpeditionCanvas");
        canvasGo.transform.SetParent(transform);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 10;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
        canvasGo.AddComponent<GraphicRaycaster>();

        // Top panel: Energy, Money, Treasures
        var topPanel = CreatePanel(canvasGo.transform, "TopPanel",
            new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -60), new Vector2(0, -10));

        _energyText = CreateText(topPanel.transform, "EnergyText", "Energy: --/--",
            new Vector2(0, 0), new Vector2(0.25f, 1), Color.green);
        _energyBarFill = CreateEnergyBar(topPanel.transform);
        _moneyText = CreateText(topPanel.transform, "MoneyText", "Money: --",
            new Vector2(0.35f, 0), new Vector2(0.55f, 1), Color.yellow);
        _treasureText = CreateText(topPanel.transform, "TreasureText", "Treasures: --/--",
            new Vector2(0.6f, 0), new Vector2(0.85f, 1), new Color(1f, 0.7f, 0f));

        // Status text (center-bottom)
        var statusPanel = CreatePanel(canvasGo.transform, "StatusPanel",
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 70), new Vector2(0, 100));
        _statusText = CreateText(statusPanel.transform, "StatusText", "Connecting...",
            new Vector2(0, 0), new Vector2(1, 1), Color.cyan);
        _statusText.alignment = TextAnchor.MiddleCenter;
        _statusText.fontSize = 18;

        // Request tracking (right of status, shows sent/confirmed request IDs)
        var trackingPanel = CreatePanel(canvasGo.transform, "TrackingPanel",
            new Vector2(0.5f, 0), new Vector2(1, 0), new Vector2(0, 100), new Vector2(0, 120));
        trackingPanel.GetComponent<Image>().color = new Color(0, 0, 0, 0.4f);
        _requestTrackingText = CreateText(trackingPanel.transform, "TrackingText", "",
            new Vector2(0, 0), new Vector2(1, 1), new Color(0.6f, 0.8f, 0.6f));
        _requestTrackingText.alignment = TextAnchor.MiddleRight;
        _requestTrackingText.fontSize = 12;

        // Deep desync toggle (top-right)
        var desyncToggleBtn = CreateButton(canvasGo.transform, "DesyncToggle", "Desync: OFF",
            new Vector2(0.85f, 1), new Vector2(1, 1), new Color(0.3f, 0.3f, 0.3f));
        var desyncToggleBtnRt = desyncToggleBtn.GetComponent<RectTransform>();
        desyncToggleBtnRt.offsetMin = new Vector2(-10, -35);
        desyncToggleBtnRt.offsetMax = new Vector2(-10, -5);
        var desyncToggleText = desyncToggleBtn.GetComponentInChildren<Text>();
        bool desyncOn = false;
        desyncToggleBtn.onClick.AddListener(() =>
        {
            desyncOn = !desyncOn;
            desyncToggleText.text = desyncOn ? "Desync: ON" : "Desync: OFF";
            desyncToggleBtn.GetComponent<Image>().color = desyncOn
                ? new Color(0.7f, 0.2f, 0.2f) : new Color(0.3f, 0.3f, 0.3f);
            _ = ToggleDeepDesync(desyncOn);
        });

        // Controls hint (bottom-left)
        var hintPanel = CreatePanel(canvasGo.transform, "HintPanel",
            new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(10, 10), new Vector2(0, 60));
        _controlsHintText = CreateText(hintPanel.transform, "HintText",
            "WASD/Arrows=Move | Hold R+dir=Remove obstacle | B=Buy energy | U=Update energy",
            new Vector2(0, 0), new Vector2(1, 1), new Color(0.7f, 0.7f, 0.7f));
        _controlsHintText.fontSize = 13;

        // Action buttons (bottom-right) — three columns: Buy / Regen / Abandon
        var btnPanel = CreatePanel(canvasGo.transform, "ButtonPanel",
            new Vector2(1, 0), new Vector2(1, 0), new Vector2(-460, 10), new Vector2(-10, 50));

        _buyEnergyButton = CreateButton(btnPanel.transform, "BuyEnergyBtn", "Buy Energy",
            new Vector2(0, 0), new Vector2(0.32f, 1), new Color(0.2f, 0.6f, 0.2f));
        _buyEnergyButton.onClick.AddListener(() => _ = gameManager.BuyEnergy());

        _updateEnergyButton = CreateButton(btnPanel.transform, "UpdateEnergyBtn", "Regen Energy",
            new Vector2(0.34f, 0), new Vector2(0.66f, 1), new Color(0.2f, 0.4f, 0.7f));
        _updateEnergyButton.onClick.AddListener(() => _ = gameManager.UpdateEnergy());

        _abandonButton = CreateButton(btnPanel.transform, "AbandonBtn", "Abandon",
            new Vector2(0.68f, 0), new Vector2(1f, 1), new Color(0.6f, 0.2f, 0.2f));
        _abandonButton.onClick.AddListener(() => _ = gameManager.AbandonExpedition());

        // New expedition button (hidden until complete) — overlays the row, shows generation mode choice
        _newExpeditionButton = CreateButton(btnPanel.transform, "NewExpBtn", "New Expedition",
            new Vector2(0, 0), new Vector2(1f, 1), new Color(0.7f, 0.5f, 0.1f));
        _newExpeditionButton.onClick.AddListener(ShowGenerationModeChoice);
        _newExpeditionButton.gameObject.SetActive(false);

        // Completion banner (hidden)
        _completeBanner = CreateCompletionBanner(canvasGo.transform);
        _completeBanner.SetActive(false);

        // Generation mode choice panel (hidden until needed)
        _generationChoicePanel = CreatePanel(canvasGo.transform, "GenerationChoicePanel",
            new Vector2(0.2f, 0.35f), new Vector2(0.8f, 0.65f), Vector2.zero, Vector2.zero);
        _generationChoicePanel.GetComponent<Image>().color = new Color(0.1f, 0.15f, 0.3f, 0.95f);

        var choiceTitle = CreateText(_generationChoicePanel.transform, "ChoiceTitle",
            "Choose map generation mode:",
            new Vector2(0, 0.6f), new Vector2(1, 1), Color.white);
        choiceTitle.alignment = TextAnchor.MiddleCenter;
        choiceTitle.fontSize = 20;

        var choiceDesc = CreateText(_generationChoicePanel.transform, "ChoiceDesc",
            "ServerReplace: server generates, client receives full state\n" +
            "Optimistic: client predicts with deterministic random seed\n" +
            "Broken: uses System.Random — will desync! (run server with --desync to detect)",
            new Vector2(0.05f, 0.25f), new Vector2(0.95f, 0.6f), new Color(0.8f, 0.8f, 0.8f));
        choiceDesc.alignment = TextAnchor.MiddleCenter;
        choiceDesc.fontSize = 13;

        _serverReplaceButton = CreateButton(_generationChoicePanel.transform, "ServerReplaceBtn",
            "ServerReplace", new Vector2(0.03f, 0.03f), new Vector2(0.33f, 0.25f), new Color(0.6f, 0.3f, 0.1f));
        _serverReplaceButton.onClick.AddListener(() => OnGenerationModeChosen(ExpeditionGameManager.GenerationMode.ServerReplace));

        _optimisticButton = CreateButton(_generationChoicePanel.transform, "OptimisticBtn",
            "Optimistic", new Vector2(0.35f, 0.03f), new Vector2(0.65f, 0.25f), new Color(0.1f, 0.5f, 0.6f));
        _optimisticButton.onClick.AddListener(() => OnGenerationModeChosen(ExpeditionGameManager.GenerationMode.Optimistic));

        var brokenButton = CreateButton(_generationChoicePanel.transform, "BrokenBtn",
            "Broken (desync)", new Vector2(0.67f, 0.03f), new Vector2(0.97f, 0.25f), new Color(0.7f, 0.1f, 0.1f));
        brokenButton.onClick.AddListener(() => OnGenerationModeChosen(ExpeditionGameManager.GenerationMode.Broken));

        _generationChoicePanel.SetActive(false);

        // D-Pad for touch/click control (right side)
        CreateDPad(canvasGo.transform);

        // Transport choice panel (shown at startup, before connecting)
        _transportChoicePanel = CreatePanel(canvasGo.transform, "TransportChoicePanel",
            new Vector2(0.2f, 0.35f), new Vector2(0.8f, 0.65f), Vector2.zero, Vector2.zero);
        _transportChoicePanel.GetComponent<Image>().color = new Color(0.1f, 0.2f, 0.3f, 0.95f);

        var transportTitle = CreateText(_transportChoicePanel.transform, "TransportTitle",
            "Choose transport:",
            new Vector2(0, 0.6f), new Vector2(1, 1), Color.white);
        transportTitle.alignment = TextAnchor.MiddleCenter;
        transportTitle.fontSize = 20;

        var transportDesc = CreateText(_transportChoicePanel.transform, "TransportDesc",
            "SignalR: WebSocket, real-time, FIFO guaranteed\n" +
            "HTTP Polling: individual HTTP requests, can lose packets",
            new Vector2(0.05f, 0.3f), new Vector2(0.95f, 0.6f), new Color(0.8f, 0.8f, 0.8f));
        transportDesc.alignment = TextAnchor.MiddleCenter;
        transportDesc.fontSize = 13;

        var signalRBtn = CreateButton(_transportChoicePanel.transform, "SignalRBtn",
            "SignalR", new Vector2(0.1f, 0.05f), new Vector2(0.48f, 0.28f), new Color(0.2f, 0.5f, 0.6f));
        signalRBtn.onClick.AddListener(() => OnTransportChosen(false));

        var httpBtn = CreateButton(_transportChoicePanel.transform, "HttpBtn",
            "HTTP Polling", new Vector2(0.52f, 0.05f), new Vector2(0.9f, 0.28f), new Color(0.5f, 0.4f, 0.1f));
        httpBtn.onClick.AddListener(() => OnTransportChosen(true));

        // Modal blocker — full-screen semi-transparent overlay behind health panel,
        // blocks all input when connection is Unresponsive
        _modalBlocker = new GameObject("ModalBlocker");
        _modalBlocker.transform.SetParent(canvasGo.transform);
        var blockerRt = _modalBlocker.AddComponent<RectTransform>();
        blockerRt.anchorMin = Vector2.zero;
        blockerRt.anchorMax = Vector2.one;
        blockerRt.offsetMin = Vector2.zero;
        blockerRt.offsetMax = Vector2.zero;
        var blockerImg = _modalBlocker.AddComponent<Image>();
        blockerImg.color = new Color(0, 0, 0, 0.5f);
        blockerImg.raycastTarget = true; // blocks clicks
        _modalBlocker.SetActive(false);

        // Connection health overlay (top-center, hidden by default)
        _connectionHealthPanel = CreatePanel(canvasGo.transform, "ConnectionHealthPanel",
            new Vector2(0.2f, 1), new Vector2(0.8f, 1), new Vector2(0, -100), new Vector2(0, -65));
        _connectionHealthPanel.GetComponent<Image>().color = new Color(0.8f, 0.2f, 0.1f, 0.9f);
        _connectionHealthText = CreateText(_connectionHealthPanel.transform, "HealthText", "",
            new Vector2(0, 0), new Vector2(0.7f, 1), Color.white);
        _connectionHealthText.alignment = TextAnchor.MiddleCenter;
        _connectionHealthText.fontSize = 15;
        _reconnectButton = CreateButton(_connectionHealthPanel.transform, "ReconnectBtn",
            "Reconnect", new Vector2(0.72f, 0.1f), new Vector2(0.98f, 0.9f), new Color(0.2f, 0.5f, 0.2f));
        _reconnectButton.onClick.AddListener(() => _ = gameManager.ReconnectAsync());
        _connectionHealthPanel.SetActive(false);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Debug network simulation panel (left side, below controls hint)
        CreateDebugNetworkPanel(canvasGo.transform);
#endif
    }

    private async System.Threading.Tasks.Task ToggleDeepDesync(bool enabled)
    {
        var result = await gameManager.Client.SetDeepDesyncAsync(enabled);
        SetStatus(result
            ? $"Deep desync {(enabled ? "ENABLED" : "DISABLED")}"
            : "Deep desync toggle failed (server may have AllowDebugApi=false)");
    }

    /// <summary>Update request tracking display.</summary>
    public void UpdateRequestTracking(long lastSent, long lastCompleted, int pendingCount)
    {
        if (_requestTrackingText != null)
            _requestTrackingText.text = $"Sent: #{lastSent}  Confirmed: #{lastCompleted}  Pending: {pendingCount}";
    }

    /// <summary>Show the transport choice panel.</summary>
    public void ShowTransportChoice()
    {
        _transportChoicePanel.SetActive(true);
    }

    private void OnTransportChosen(bool useHttpPolling)
    {
        _transportChoicePanel.SetActive(false);
        _ = gameManager.ConnectWithTransport(useHttpPolling);
    }

    /// <summary>Show the generation mode choice panel.</summary>
    public void ShowGenerationModeChoice()
    {
        _generationChoicePanel.SetActive(true);
    }

    private async void OnGenerationModeChosen(ExpeditionGameManager.GenerationMode mode)
    {
        _generationChoicePanel.SetActive(false);
        await gameManager.StartNewExpedition(mode);
    }

    private void CreateDPad(Transform parent)
    {
        var dpadPanel = new GameObject("DPad");
        dpadPanel.transform.SetParent(parent);
        var rt = dpadPanel.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 0);
        rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-40, 110);
        rt.sizeDelta = new Vector2(160, 160);

        float btnSize = 48;
        float center = 80;

        _btnUp = CreateDPadButton(dpadPanel.transform, "Up", "^",
            new Vector2(center - btnSize / 2, center + btnSize / 2), btnSize);
        _btnDown = CreateDPadButton(dpadPanel.transform, "Down", "v",
            new Vector2(center - btnSize / 2, center - btnSize * 1.5f), btnSize);
        _btnLeft = CreateDPadButton(dpadPanel.transform, "Left", "<",
            new Vector2(center - btnSize * 1.5f, center - btnSize / 2), btnSize);
        _btnRight = CreateDPadButton(dpadPanel.transform, "Right", ">",
            new Vector2(center + btnSize / 2, center - btnSize / 2), btnSize);

        _btnUp.onClick.AddListener(() => OnDPad(0, 1));
        _btnDown.onClick.AddListener(() => OnDPad(0, -1));
        _btnLeft.onClick.AddListener(() => OnDPad(-1, 0));
        _btnRight.onClick.AddListener(() => OnDPad(1, 0));

        // Remove obstacle toggle
        var toggleGo = new GameObject("RemoveToggle");
        toggleGo.transform.SetParent(dpadPanel.transform);
        var trt = toggleGo.AddComponent<RectTransform>();
        trt.anchoredPosition = new Vector2(center - 30, center - btnSize / 2);
        trt.sizeDelta = new Vector2(60, 30);
        var toggleBg = toggleGo.AddComponent<Image>();
        toggleBg.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
        _removeObstacleToggle = toggleGo.AddComponent<Toggle>();
        var toggleLabel = CreateText(toggleGo.transform, "Label", "R",
            new Vector2(0, 0), new Vector2(1, 1), Color.red);
        toggleLabel.alignment = TextAnchor.MiddleCenter;
        toggleLabel.fontSize = 14;
    }

    private async void OnDPad(int dx, int dy)
    {
        if (!gameManager.IsConnected) return;

        bool removeMode = _removeObstacleToggle != null && _removeObstacleToggle.isOn;
        if (removeMode)
        {
            bool removed = await gameManager.RemoveObstacle(dx, dy);
            SetStatus(removed ? "Obstacle removed! (-5 energy)" : "Cannot remove obstacle.");
        }
        else
        {
            var result = await gameManager.Move(dx, -dy); // Unity Y is inverted vs grid Y
            SetStatus(MoveResultToString(result));
        }
    }

    private void RefreshUI()
    {
        var profile = gameManager.ProfileState;
        var expedition = gameManager.ExpeditionState;
        var config = gameManager.Config;

        // Update buy button label with live config values
        _buyEnergyButton.GetComponentInChildren<Text>().text = $"Buy +{config.BuyEnergyAmount}E ({config.BuyEnergyCost}$)";

        if (profile != null)
        {
            _energyText.text = $"Energy: {profile.Energy}/{config.MaxEnergy}";
            _moneyText.text = $"Money: {profile.Money}";

            // Update energy bar fill
            float ratio = config.MaxEnergy > 0 ? (float)profile.Energy / config.MaxEnergy : 0f;
            _energyBarFill.fillAmount = Mathf.Clamp01(ratio);
            _energyBarFill.color = ratio > 0.3f ? Color.green : Color.red;
        }

        if (expedition != null)
        {
            _treasureText.text = $"Treasures: {expedition.TreasuresCollected}/{expedition.TotalTreasures}";

            bool isComplete = expedition.IsComplete;
            _completeBanner.SetActive(isComplete);
            _newExpeditionButton.gameObject.SetActive(isComplete);
            _buyEnergyButton.gameObject.SetActive(!isComplete);
            _updateEnergyButton.gameObject.SetActive(!isComplete);
            _abandonButton.gameObject.SetActive(!isComplete);

            // Update map
            if (mapView != null)
                mapView.RenderMap(expedition);
        }
    }

    public static string MoveResultToString(MoveResult result)
    {
        return result switch
        {
            MoveResult.Ok => "",
            MoveResult.Treasure => "Found treasure! +25 money",
            MoveResult.NoEnergy => "No energy! Buy or wait for regen.",
            MoveResult.Blocked => "Blocked!",
            MoveResult.OutOfBounds => "Edge of map!",
            MoveResult.Complete => "All treasures found!",
            _ => ""
        };
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void CreateDebugNetworkPanel(Transform parent)
    {
        // Narrow vertical panel — buttons stacked, doesn't overlap the game field
        _debugNetPanel = CreatePanel(parent, "DebugNetPanel",
            new Vector2(0, 0), new Vector2(0, 0), new Vector2(10, 65), new Vector2(160, 280));
        _debugNetPanel.GetComponent<Image>().color = new Color(0.15f, 0.1f, 0.2f, 0.9f);

        var title = CreateText(_debugNetPanel.transform, "DebugTitle", "Net Debug",
            new Vector2(0, 0.92f), new Vector2(1, 1), new Color(1f, 0.6f, 0.3f));
        title.alignment = TextAnchor.MiddleCenter;
        title.fontSize = 12;

        // Enable toggle
        var enableBtn = CreateButton(_debugNetPanel.transform, "EnableBtn", "Sim: OFF",
            new Vector2(0.05f, 0.8f), new Vector2(0.95f, 0.91f), new Color(0.3f, 0.3f, 0.3f));
        var enableText = enableBtn.GetComponentInChildren<Text>();
        enableBtn.onClick.AddListener(() =>
        {
            var settings = gameManager.DebugNetworkSettings;
            if (settings == null) return;
            settings.Enabled = !settings.Enabled;
            enableText.text = settings.Enabled ? "Sim: ON" : "Sim: OFF";
            enableBtn.GetComponent<Image>().color = settings.Enabled
                ? new Color(0.7f, 0.3f, 0.1f) : new Color(0.3f, 0.3f, 0.3f);
        });

        // Permanent disconnect button
        var disconnectBtn = CreateButton(_debugNetPanel.transform, "DisconnectBtn", "Drop",
            new Vector2(0.05f, 0.69f), new Vector2(0.95f, 0.79f), new Color(0.6f, 0.1f, 0.1f));
        disconnectBtn.onClick.AddListener(() => gameManager.DebugConnection?.SimulateDisconnect());

        // Temporary disconnect (metro/tunnel) button
        var metroBtn = CreateButton(_debugNetPanel.transform, "MetroBtn", "Metro 3s",
            new Vector2(0.05f, 0.58f), new Vector2(0.95f, 0.68f), new Color(0.5f, 0.3f, 0.1f));
        metroBtn.onClick.AddListener(() => _ = gameManager.DebugConnection?.SimulateTemporaryDisconnectAsync(3000));

        // Latency: label + buttons
        _debugLatencyLabel = CreateText(_debugNetPanel.transform, "LatencyLabel", "Latency: 0ms",
            new Vector2(0, 0.42f), new Vector2(1, 0.55f), Color.white);
        _debugLatencyLabel.fontSize = 11;

        var latencyUp = CreateButton(_debugNetPanel.transform, "LatUp", "+",
            new Vector2(0.05f, 0.31f), new Vector2(0.48f, 0.42f), new Color(0.2f, 0.5f, 0.2f));
        latencyUp.onClick.AddListener(() => AdjustLatency(500));
        var latencyDown = CreateButton(_debugNetPanel.transform, "LatDown", "-",
            new Vector2(0.52f, 0.31f), new Vector2(0.95f, 0.42f), new Color(0.5f, 0.2f, 0.2f));
        latencyDown.onClick.AddListener(() => AdjustLatency(-500));

        // Loss: label + buttons
        _debugLossLabel = CreateText(_debugNetPanel.transform, "LossLabel", "Loss: 0%",
            new Vector2(0, 0.16f), new Vector2(1, 0.29f), Color.white);
        _debugLossLabel.fontSize = 11;

        var lossUp = CreateButton(_debugNetPanel.transform, "LossUp", "+",
            new Vector2(0.05f, 0.04f), new Vector2(0.48f, 0.15f), new Color(0.2f, 0.5f, 0.2f));
        lossUp.onClick.AddListener(() => AdjustLoss(10f));
        var lossDown = CreateButton(_debugNetPanel.transform, "LossDown", "-",
            new Vector2(0.52f, 0.04f), new Vector2(0.95f, 0.15f), new Color(0.5f, 0.2f, 0.2f));
        lossDown.onClick.AddListener(() => AdjustLoss(-10f));
    }

    private void AdjustLatency(int delta)
    {
        var settings = gameManager.DebugNetworkSettings;
        if (settings == null) return;
        settings.MinLatencyMs = Mathf.Max(0, settings.MinLatencyMs + delta);
        settings.MaxLatencyMs = Mathf.Max(settings.MinLatencyMs, settings.MaxLatencyMs + delta);
        _debugLatencyLabel.text = $"Latency: {settings.MinLatencyMs}-{settings.MaxLatencyMs}ms";
    }

    private void AdjustLoss(float delta)
    {
        var settings = gameManager.DebugNetworkSettings;
        if (settings == null) return;
        settings.PacketLossPercent = Mathf.Clamp(settings.PacketLossPercent + delta, 0f, 100f);
        _debugLossLabel.text = $"Loss: {settings.PacketLossPercent:0}%";
    }
#endif

    // ========================
    // UI Factory Helpers
    // ========================

    private static Font _cachedFont;
    private static Font GetFont()
    {
        if (_cachedFont != null) return _cachedFont;
        _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_cachedFont == null)
            _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return _cachedFont;
    }

    private static GameObject CreatePanel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        var img = go.AddComponent<Image>();
        img.color = new Color(0, 0, 0, 0.6f);
        return go;
    }

    private static Text CreateText(Transform parent, string name, string text,
        Vector2 anchorMin, Vector2 anchorMax, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(8, 2);
        rt.offsetMax = new Vector2(-2, -2);
        var t = go.AddComponent<Text>();
        t.text = text;
        t.color = color;
        t.font = GetFont();
        t.fontSize = 16;
        t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    private static Image CreateEnergyBar(Transform parent)
    {
        var bg = new GameObject("EnergyBarBg");
        bg.transform.SetParent(parent);
        var bgRt = bg.AddComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.15f, 0.25f);
        bgRt.anchorMax = new Vector2(0.33f, 0.75f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.2f, 0.2f, 0.2f);

        var fill = new GameObject("EnergyBarFill");
        fill.transform.SetParent(bg.transform);
        var fillRt = fill.AddComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = Color.green;
        fillImg.type = Image.Type.Filled;
        fillImg.fillMethod = Image.FillMethod.Horizontal;
        fillImg.fillAmount = 1f;
        return fillImg;
    }

    private static Button CreateButton(Transform parent, string name, string label,
        Vector2 anchorMin, Vector2 anchorMax, Color bgColor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(2, 2);
        rt.offsetMax = new Vector2(-2, -2);
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform);
        var txtRt = txtGo.AddComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = Vector2.zero;
        txtRt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<Text>();
        txt.text = label;
        txt.color = Color.white;
        txt.font = GetFont();
        txt.fontSize = 14;
        txt.alignment = TextAnchor.MiddleCenter;
        return btn;
    }

    private static Button CreateDPadButton(Transform parent, string name, string label,
        Vector2 position, float size)
    {
        var go = new GameObject($"DPad_{name}");
        go.transform.SetParent(parent);
        var rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(size, size);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform);
        var txtRt = txtGo.AddComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = Vector2.zero;
        txtRt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<Text>();
        txt.text = label;
        txt.color = Color.white;
        txt.font = GetFont();
        txt.fontSize = 20;
        txt.alignment = TextAnchor.MiddleCenter;
        return btn;
    }

    private static GameObject CreateCompletionBanner(Transform parent)
    {
        var go = CreatePanel(parent, "CompleteBanner",
            new Vector2(0.2f, 0.4f), new Vector2(0.8f, 0.6f), Vector2.zero, Vector2.zero);
        go.GetComponent<Image>().color = new Color(0.1f, 0.4f, 0.1f, 0.9f);
        var txt = CreateText(go.transform, "CompleteText", "EXPEDITION COMPLETE!\nAll treasures collected!",
            new Vector2(0, 0), new Vector2(1, 1), Color.yellow);
        txt.alignment = TextAnchor.MiddleCenter;
        txt.fontSize = 24;
        return go;
    }
}
