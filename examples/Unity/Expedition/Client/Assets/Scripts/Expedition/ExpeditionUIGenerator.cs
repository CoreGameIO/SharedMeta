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
    private GameObject _completeBanner;

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

        // Controls hint (bottom-left)
        var hintPanel = CreatePanel(canvasGo.transform, "HintPanel",
            new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(10, 10), new Vector2(0, 60));
        _controlsHintText = CreateText(hintPanel.transform, "HintText",
            "WASD/Arrows=Move | Hold R+dir=Remove obstacle | B=Buy energy | U=Update energy",
            new Vector2(0, 0), new Vector2(1, 1), new Color(0.7f, 0.7f, 0.7f));
        _controlsHintText.fontSize = 13;

        // Action buttons (bottom-right)
        var btnPanel = CreatePanel(canvasGo.transform, "ButtonPanel",
            new Vector2(1, 0), new Vector2(1, 0), new Vector2(-320, 10), new Vector2(-10, 50));

        _buyEnergyButton = CreateButton(btnPanel.transform, "BuyEnergyBtn", "Buy Energy",
            new Vector2(0, 0), new Vector2(0.45f, 1), new Color(0.2f, 0.6f, 0.2f));
        _buyEnergyButton.onClick.AddListener(() => _ = gameManager.BuyEnergy());

        _updateEnergyButton = CreateButton(btnPanel.transform, "UpdateEnergyBtn", "Regen Energy",
            new Vector2(0.5f, 0), new Vector2(0.95f, 1), new Color(0.2f, 0.4f, 0.7f));
        _updateEnergyButton.onClick.AddListener(() => _ = gameManager.UpdateEnergy());

        // New expedition button (hidden until complete)
        _newExpeditionButton = CreateButton(btnPanel.transform, "NewExpBtn", "New Expedition",
            new Vector2(0, 0), new Vector2(0.95f, 1), new Color(0.7f, 0.5f, 0.1f));
        _newExpeditionButton.onClick.AddListener(() => _ = gameManager.StartNewExpedition());
        _newExpeditionButton.gameObject.SetActive(false);

        // Completion banner (hidden)
        _completeBanner = CreateCompletionBanner(canvasGo.transform);
        _completeBanner.SetActive(false);

        // D-Pad for touch/click control (right side)
        CreateDPad(canvasGo.transform);
    }

    private void CreateDPad(Transform parent)
    {
        var dpadPanel = new GameObject("DPad");
        dpadPanel.transform.SetParent(parent);
        var rt = dpadPanel.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 0);
        rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-20, 110);
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
