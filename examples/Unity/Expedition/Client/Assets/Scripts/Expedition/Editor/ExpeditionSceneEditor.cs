using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

/// <summary>
/// Editor menu command: Tools > Expedition > Setup Scene.
/// Creates the full scene layout: camera, light, 3D map grid, player marker,
/// UI overlay, and all MonoBehaviour objects with wired references.
/// Idempotent — safe to run multiple times.
/// </summary>
public static class ExpeditionSceneEditor
{
    private const int DefaultWidth = 15;
    private const int DefaultHeight = 10;
    private const float CellSpacing = 1.05f;

    [MenuItem("Tools/Expedition/Setup Scene")]
    static void SetupScene()
    {
        // 1. Camera
        var cam = SetupCamera();

        // 2. Directional Light
        SetupLight();

        // 3. Map object with cell grid and player marker
        var mapGo = FindOrCreate("ExpeditionMap");
        var mapView = AddOrGet<ExpeditionMapView>(mapGo);

        var cellContainer = FindOrCreateChild(mapGo.transform, "CellContainer");
        CreateCellGrid(cellContainer, DefaultWidth, DefaultHeight);

        var playerMarker = CreatePlayerMarker(mapGo.transform);

        SetField(mapView, "cellContainer", cellContainer);
        SetField(mapView, "playerMarker", playerMarker.gameObject);

        // Assign textures from Assets/Images/
        AssignTexture(mapView, "floorAtlas", "Assets/Images/grass1024.png");
        AssignTexture(mapView, "wallTexture", "Assets/Images/walls512.png");
        AssignTexture(mapView, "obstacleTexture", "Assets/Images/breakable512.png");
        AssignTexture(mapView, "treasureTexture", "Assets/Images/treasures512.png");

        // 4. GameManager
        var managerGo = FindOrCreate("ExpeditionGameManager");
        var manager = AddOrGet<ExpeditionGameManager>(managerGo);

        // 5. UI Generator (creates canvas UI at runtime)
        var uiGo = FindOrCreate("ExpeditionUI");
        var ui = AddOrGet<ExpeditionUIGenerator>(uiGo);

        // 6. Input Controller
        var inputGo = FindOrCreate("ExpeditionInput");
        var input = AddOrGet<ExpeditionInputController>(inputGo);

        // 7. Wire cross-references
        SetField(manager, "ui", ui);
        SetField(manager, "mapView", mapView);
        SetField(ui, "gameManager", manager);
        SetField(ui, "mapView", mapView);
        SetField(input, "gameManager", manager);
        SetField(input, "ui", ui);

        // 8. EventSystem
        if (Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<InputSystemUIInputModule>();
            Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
        }

        // Center camera on grid
        PositionCamera(cam, DefaultWidth, DefaultHeight);

        Selection.activeGameObject = managerGo;
        Debug.Log("Expedition scene setup complete. Start the server and press Play.");
    }

    [MenuItem("Tools/Expedition/Rebuild Map Grid")]
    static void RebuildMapGrid()
    {
        var mapGo = GameObject.Find("ExpeditionMap");
        if (mapGo == null)
        {
            Debug.LogError("Run 'Setup Scene' first.");
            return;
        }

        var cellContainer = mapGo.transform.Find("CellContainer");
        if (cellContainer != null)
            CreateCellGrid(cellContainer, DefaultWidth, DefaultHeight);

        Debug.Log($"Map grid rebuilt: {DefaultWidth}x{DefaultHeight}");
    }

    // ========== Camera ==========

    static Camera SetupCamera()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            go.tag = "MainCamera";
            Undo.RegisterCreatedObjectUndo(go, "Create Camera");
        }

        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.08f, 0.12f);
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 50f;
        return cam;
    }

    static void PositionCamera(Camera cam, int width, int height)
    {
        float cx = (width - 1) * CellSpacing / 2f;
        float cz = (height - 1) * CellSpacing / 2f;
        cam.transform.position = new Vector3(cx, 20f, cz);
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        float aspect = cam.aspect > 0 ? cam.aspect : 16f / 9f;
        float needH = height * CellSpacing / 2f + 1.5f;
        float needW = (width * CellSpacing / 2f + 1.5f) / aspect;
        cam.orthographicSize = Mathf.Max(needH, needW);
    }

    // ========== Light ==========

    static void SetupLight()
    {
        var existing = GameObject.Find("Directional Light");
        if (existing != null) return;

        var go = new GameObject("Directional Light");
        var light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        Undo.RegisterCreatedObjectUndo(go, "Create Light");
    }

    // ========== Cell Grid ==========

    static void CreateCellGrid(Transform container, int width, int height)
    {
        // Clear existing cells
        while (container.childCount > 0)
            Object.DestroyImmediate(container.GetChild(0).gameObject);

        float cellScale = CellSpacing * 0.95f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var cell = GameObject.CreatePrimitive(PrimitiveType.Quad);
                cell.name = $"Cell_{x}_{y}";
                cell.transform.SetParent(container);
                cell.transform.localPosition = new Vector3(
                    x * CellSpacing, 0.01f, (height - 1 - y) * CellSpacing);
                cell.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                cell.transform.localScale = new Vector3(cellScale, cellScale, 1f);

                // Remove collider (not needed, saves performance)
                var col = cell.GetComponent<Collider>();
                if (col) Object.DestroyImmediate(col);
            }
        }
    }

    // ========== Player Marker ==========

    static Transform CreatePlayerMarker(Transform parent)
    {
        var existing = parent.Find("PlayerMarker");
        if (existing != null)
            return existing;

        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "PlayerMarker";
        go.transform.SetParent(parent);
        go.transform.localPosition = new Vector3(0, 0.2f, (DefaultHeight - 1) * CellSpacing);
        go.transform.localScale = new Vector3(0.5f, 0.2f, 0.5f);

        // Remove collider
        var col = go.GetComponent<Collider>();
        if (col) Object.DestroyImmediate(col);

        // Green-ish color
        var renderer = go.GetComponent<Renderer>();
        var mat = new Material(GetShader());
        SetMaterialColor(mat, new Color(0.1f, 0.9f, 0.3f));
        renderer.sharedMaterial = mat;

        return go.transform;
    }

    // ========== Helpers ==========

    static Shader GetShader()
    {
        return Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Standard");
    }

    static void SetMaterialColor(Material mat, Color color)
    {
        mat.SetColor("_BaseColor", color); // URP
        mat.color = color; // Standard
    }

    static GameObject FindOrCreate(string name)
    {
        var go = GameObject.Find(name);
        if (go == null)
        {
            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        }
        return go;
    }

    static Transform FindOrCreateChild(Transform parent, string name)
    {
        var child = parent.Find(name);
        if (child == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            child = go.transform;
        }
        return child;
    }

    static T AddOrGet<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) c = Undo.AddComponent<T>(go);
        return c;
    }

    static void SetField(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop != null)
        {
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            Debug.LogWarning($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
    }

    static void AssignTexture(Component target, string fieldName, string assetPath)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (tex == null)
        {
            Debug.LogWarning($"Texture not found at '{assetPath}' — assign manually in Inspector.");
            return;
        }
        SetField(target, fieldName, tex);
    }
}
