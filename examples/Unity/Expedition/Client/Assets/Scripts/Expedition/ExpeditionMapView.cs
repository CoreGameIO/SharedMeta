using UnityEngine;
using Expedition.Shared;

/// <summary>
/// Renders the expedition map using textured Quads on the XZ plane.
/// Two layers per cell: floor (from 2x2 atlas sub-tiles) + overlay (wall/obstacle/treasure sprite).
/// Falls back to solid colors if textures are not assigned.
/// </summary>
public class ExpeditionMapView : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cellContainer;
    [SerializeField] private GameObject playerMarker;

    [Header("Textures")]
    [SerializeField] private Texture2D floorAtlas;
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D obstacleTexture;
    [SerializeField] private Texture2D treasureTexture;

    [Header("Grid Settings")]
    [SerializeField] private float cellSpacing = 1.05f;

    private Renderer[] _floorRenderers;
    private Renderer[] _overlayRenderers;
    private int[] _cellFloorVariant;
    private float[] _cellRotation;
    private int _gridWidth, _gridHeight;

    // Fallback colors when textures not assigned
    static readonly Color ColEmpty = new(0.15f, 0.4f, 0.15f);
    static readonly Color ColWall = new(0.5f, 0.15f, 0.15f);
    static readonly Color ColObstacle = new(0.6f, 0.45f, 0.1f);
    static readonly Color ColTreasure = new(0.9f, 0.8f, 0.15f);
    static readonly Color ColFog = new(0.12f, 0.12f, 0.15f);

    // Materials
    private Material _floorMat;   // single material with full atlas texture
    private Mesh[] _floorMeshes;  // 16 quad meshes with different UV sub-regions (4x4 atlas)
    private Material _wallMat;
    private Material _obstacleMat;
    private Material _treasureMat;
    private Material _fogMat;
    private MaterialPropertyBlock _mpb;
    private bool _useTextures;

    void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        CreateMaterials();
        if (playerMarker != null)
            playerMarker.SetActive(false);
    }

    void CreateMaterials()
    {
        _useTextures = floorAtlas != null;
        var shader = Shader.Find("Sprites/Default");

        if (_useTextures)
        {
            // One material for all floor tiles — UV selection done via mesh
            _floorMat = new Material(shader) { mainTexture = floorAtlas };

            // 16 quad meshes, each with UVs mapped to one cell of the 4x4 atlas
            _floorMeshes = new Mesh[16];
            for (int i = 0; i < 16; i++)
                _floorMeshes[i] = CreateSubTileQuad(i % 4, i / 4);

            _wallMat = new Material(shader);
            if (wallTexture != null) _wallMat.mainTexture = wallTexture;
            else _wallMat.color = ColWall;

            _obstacleMat = new Material(shader);
            if (obstacleTexture != null) _obstacleMat.mainTexture = obstacleTexture;
            else _obstacleMat.color = ColObstacle;

            _treasureMat = new Material(shader);
            if (treasureTexture != null) _treasureMat.mainTexture = treasureTexture;
            else _treasureMat.color = ColTreasure;
        }

        _fogMat = new Material(shader) { color = ColFog };
    }

    /// <summary>
    /// Creates a quad mesh with UVs mapped to one cell of a 4x4 atlas.
    /// col=0..3, row=0..3 selects which tile.
    /// </summary>
    static Mesh CreateSubTileQuad(int col, int row)
    {
        const float step = 1f / 4f; // 0.25
        float u0 = col * step;
        float v0 = row * step;
        float u1 = u0 + step;
        float v1 = v0 + step;

        var mesh = new Mesh
        {
            name = $"FloorTile_{col}_{row}",
            vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f)
            },
            normals = new[]
            {
                -Vector3.forward, -Vector3.forward,
                -Vector3.forward, -Vector3.forward
            },
            uv = new[]
            {
                new Vector2(u0, v0), new Vector2(u1, v0),
                new Vector2(u0, v1), new Vector2(u1, v1)
            },
            triangles = new[] { 0, 2, 1, 2, 3, 1 }
        };
        return mesh;
    }

    public void RenderMap(ExpeditionState state)
    {
        if (state == null || !state.IsGenerated) return;

        if (_floorRenderers == null || state.Width != _gridWidth || state.Height != _gridHeight)
        {
            RebuildGrid(state.Width, state.Height);
            _gridWidth = state.Width;
            _gridHeight = state.Height;
        }

        for (int y = 0; y < state.Height; y++)
        {
            for (int x = 0; x < state.Width; x++)
            {
                int idx = y * state.Width + x;
                if (idx >= _floorRenderers.Length) continue;

                bool isPlayer = x == state.PlayerX && y == state.PlayerY;
                bool revealed = idx < state.Revealed.Count && state.Revealed[idx];

                if (!revealed && !isPlayer)
                {
                    SetFog(idx);
                }
                else
                {
                    var cellType = idx < state.Cells.Count ? (CellType)state.Cells[idx] : CellType.Empty;
                    if (isPlayer) cellType = CellType.Empty;
                    SetCellAppearance(idx, cellType);
                }
            }
        }

        if (playerMarker != null)
        {
            playerMarker.SetActive(true);
            float px = state.PlayerX * cellSpacing;
            float pz = (state.Height - 1 - state.PlayerY) * cellSpacing;
            playerMarker.transform.localPosition = new Vector3(px, 0.15f, pz);
        }
    }

    void SetFog(int idx)
    {
        var floor = _floorRenderers[idx];
        var overlay = _overlayRenderers[idx];

        if (_useTextures)
        {
            floor.sharedMaterial = _fogMat;
            floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else
        {
            SetColorFallback(floor, ColFog);
        }

        overlay.gameObject.SetActive(false);
    }

    void SetCellAppearance(int idx, CellType type)
    {
        var floor = _floorRenderers[idx];
        var overlay = _overlayRenderers[idx];

        if (_useTextures)
        {
            // Floor is always visible with a random sub-tile mesh
            floor.GetComponent<MeshFilter>().sharedMesh = _floorMeshes[_cellFloorVariant[idx]];
            floor.sharedMaterial = _floorMat;
            floor.transform.localRotation = Quaternion.Euler(90f, _cellRotation[idx], 0f);

            // Overlay for non-empty cell types
            switch (type)
            {
                case CellType.Empty:
                    overlay.gameObject.SetActive(false);
                    break;
                case CellType.Wall:
                    overlay.gameObject.SetActive(true);
                    overlay.sharedMaterial = _wallMat;
                    break;
                case CellType.Obstacle:
                    overlay.gameObject.SetActive(true);
                    overlay.sharedMaterial = _obstacleMat;
                    break;
                case CellType.Treasure:
                    overlay.gameObject.SetActive(true);
                    overlay.sharedMaterial = _treasureMat;
                    break;
                default:
                    floor.sharedMaterial = _fogMat;
                    floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    overlay.gameObject.SetActive(false);
                    break;
            }
        }
        else
        {
            Color c = type switch
            {
                CellType.Empty => ColEmpty,
                CellType.Wall => ColWall,
                CellType.Obstacle => ColObstacle,
                CellType.Treasure => ColTreasure,
                _ => ColFog
            };
            SetColorFallback(floor, c);
            overlay.gameObject.SetActive(false);
        }
    }

    void SetColorFallback(Renderer r, Color c)
    {
        _mpb.SetColor(Shader.PropertyToID("_BaseColor"), c);
        _mpb.SetColor(Shader.PropertyToID("_Color"), c);
        r.SetPropertyBlock(_mpb);
    }

    void RebuildGrid(int width, int height)
    {
        if (cellContainer != null)
        {
            while (cellContainer.childCount > 0)
                DestroyImmediate(cellContainer.GetChild(0).gameObject);
        }
        else
        {
            var go = new GameObject("CellContainer");
            go.transform.SetParent(transform);
            cellContainer = go.transform;
        }

        int total = width * height;
        _floorRenderers = new Renderer[total];
        _overlayRenderers = new Renderer[total];
        _cellFloorVariant = new int[total];
        _cellRotation = new float[total];

        float cellScale = cellSpacing * 0.95f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float px = x * cellSpacing;
                float pz = (height - 1 - y) * cellSpacing;

                // Floor quad (bottom layer)
                var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                floor.name = $"Floor_{x}_{y}";
                floor.transform.SetParent(cellContainer);
                floor.transform.localPosition = new Vector3(px, 0.01f, pz);
                floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                floor.transform.localScale = new Vector3(cellScale, cellScale, 1f);
                var col = floor.GetComponent<Collider>();
                if (col) Destroy(col);

                // Random floor variant and rotation
                _cellFloorVariant[idx] = Random.Range(0, 16);
                _cellRotation[idx] = 90f * Random.Range(0, 4);

                _floorRenderers[idx] = floor.GetComponent<Renderer>();
                if (_useTextures)
                    _floorRenderers[idx].sharedMaterial = _fogMat;
                else
                    SetColorFallback(_floorRenderers[idx], ColFog);

                // Overlay quad (top layer — for wall/obstacle/treasure sprites)
                var overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
                overlay.name = $"Overlay_{x}_{y}";
                overlay.transform.SetParent(cellContainer);
                overlay.transform.localPosition = new Vector3(px, 0.02f, pz);
                overlay.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                overlay.transform.localScale = new Vector3(cellScale, cellScale, 1f);
                col = overlay.GetComponent<Collider>();
                if (col) Destroy(col);

                _overlayRenderers[idx] = overlay.GetComponent<Renderer>();
                overlay.SetActive(false);
            }
        }

        AdjustCamera(width, height);
    }

    void AdjustCamera(int width, int height)
    {
        var cam = Camera.main;
        if (cam == null) return;

        float cx = (width - 1) * cellSpacing / 2f;
        float cz = (height - 1) * cellSpacing / 2f;
        cam.transform.position = new Vector3(cx, 20f, cz);

        if (cam.orthographic)
        {
            float aspect = cam.aspect;
            float needH = height * cellSpacing / 2f + 1.5f;
            float needW = (width * cellSpacing / 2f + 1.5f) / aspect;
            cam.orthographicSize = Mathf.Max(needH, needW);
        }
    }
}
