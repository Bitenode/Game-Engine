using SN = System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Game_Engine.Core;
using Game_Engine.Core.Rendering.GPU;
using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Platform;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using static Game_Engine.Core.TransformUtil;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Silk.NET.OpenGL;


namespace Game_Engine.Views;


public class SceneView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    // OpenGlControlBase renders via composition, not Avalonia visuals, so the control
    // isn't hit-testable by default.  Implement ICustomHitTest so pointer events work
    // over the entire surface.
    public bool HitTest(Point point) => true;

    #region GPU Resources
    private GLContext? _glCtx;
    private ShaderProgram? _standardShader;
    private ShaderProgram? _depthShader;
    private ShaderProgram? _skyShader;
    private ShaderProgram? _gridShader;
    private ShaderProgram? _wireShader;
    private ShaderProgram? _terrainShader;
    private ShaderProgram? _particleShader;
    private ShaderProgram? _waterShader;
    private ShaderProgram? _planetTerrainShader;
    private ShaderProgram? _planetWaterShader;
    private ShaderProgram? _planetAtmosphereShader;
    private ShaderProgram? _planetCloudShader;
    private ShaderProgram? _postProcessShader;
    private ShaderProgram? _volFogShader;
    private FullscreenQuad? _fsQuad;
    private ResourceCache? _cache;
    private GPUFramebuffer? _sceneFBO;
    private GPUFramebuffer? _volFogFBO;
    private int _sceneFBO_W, _sceneFBO_H;


    private ShadowMapGPU? _shadow;

    // Canvas UI renderer
    private Core.Rendering.UI.CanvasRenderer? _canvasRenderer;

    // Gizmo GL resources (lines + arrowhead cones)
    private uint _gizmoVao;
    private uint _gizmoVbo;

    // Collider gizmo GL resources (dynamic line buffer)
    private uint _colliderVao;
    private uint _colliderVbo;
    private int _colliderVboCapacity;   // current VBO capacity in floats
    // Reusable per-frame line buffers (avoid GC pressure)
    private readonly List<float> _colLinesNormal = new();
    private readonly List<float> _colLinesTriggerNeutral = new();
    private readonly List<float> _colLinesTriggerDamage = new();
    private readonly List<float> _colLinesTriggerCheckpoint = new();
    private readonly List<float> _colLinesFaintN = new();
    private readonly List<float> _colLinesFaintNeutral = new();
    private readonly List<float> _colLinesFaintDamage = new();
    private readonly List<float> _colLinesFaintCheckpoint = new();
    // Terrain brush gizmo line buffers
    private readonly List<float> _terrainOuter = new();
    private readonly List<float> _terrainInner = new();
    private readonly List<float> _terrainCross = new();
    #endregion

    #region Camera & selection
    float _yaw = -30f * MathF.PI / 180f;
    float _pitch = -20f * MathF.PI / 180f;
    float _roll = 0f;
    float _distance = 8f;
    SN.Vector3 _target = SN.Vector3.Zero;

    bool _lookThroughCamera = false;
    Camera? _lastPreviewCam;

    readonly HashSet<Key> _keysDown = new();
    DispatcherTimer _flyTimer;
    readonly Stopwatch _flyWatch = new();

    float _flyBaseSpeed = 5f;
    float _flyBoostMul = 4f;
    float _flySlowMul = 0.25f;

    Point _last;
    bool _orbiting, _panning;
    private readonly CameraBookmark?[] _cameraBookmarks = new CameraBookmark?[5];
    private readonly DispatcherTimer _frameLerpTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _frameLerpWatch = new();
    private SN.Vector3 _frameStartTarget;
    private SN.Vector3 _frameEndTarget;
    private float _frameStartDistance;
    private float _frameEndDistance;
    private const float FrameLerpDurationSec = 0.2f;

    GameObject? _selected;

    private readonly Stopwatch _windWatch = Stopwatch.StartNew();
    private double _windPrev = 0.0;

    const double SceneLodIntervalSec = 0.12;
    const double ScenePlanetLodIntervalSec = 0.12;
    const float SceneLodMoveThreshold = 2.5f;
    const float ScenePlanetLodMoveThreshold = 3.5f;
    double _sceneLodAccumSec;
    double _scenePlanetLodAccumSec;
    SN.Vector3 _lastSceneLodCamPos = new(float.NaN);
    SN.Vector3 _lastScenePlanetLodCamPos = new(float.NaN);

    // Cached scene query results (refreshed on SceneService.Changed, not per-frame)
    private Skybox? _cachedSkybox;
    private Light? _cachedLight;
    private bool _sceneQueryDirty = true;

    // FPS tracking
    private readonly Stopwatch _fpsWatch = new Stopwatch();
    private readonly Stopwatch _frameTimer = new Stopwatch();
    private int _fpsFrameCount;
    private double _lastFrameMs;
    private string _fpsText = "0 FPS";
    private string _lastSectionTimes = "";
    private double _lastCompositMs;  // Avalonia compositing time (base.Render minus OnOpenGlRender)
    private double _lastOverlayMs;   // 2D overlay time (colliders, gizmos, wireframes)

    public static readonly DirectProperty<SceneView, string> FpsTextProperty =
        AvaloniaProperty.RegisterDirect<SceneView, string>(nameof(FpsText), o => o.FpsText);

    public string FpsText
    {
        get => _fpsText;
        private set => SetAndRaise(FpsTextProperty, ref _fpsText, value);
    }

    private DispatcherTimer? _fpsTimer;
    private readonly DispatcherTimer _playModePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private long _lastPlayPreviewTicks;
    private volatile bool _renderInFlight;

    private readonly struct CameraBookmark
    {
        public CameraBookmark(SN.Vector3 target, float yaw, float pitch, float roll, float distance)
        {
            Target = target;
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
            Distance = distance;
        }

        public SN.Vector3 Target { get; }
        public float Yaw { get; }
        public float Pitch { get; }
        public float Roll { get; }
        public float Distance { get; }
    }

    Terrain? _hoverTerrain;
    SN.Vector3 _hoverPointW;
    bool _hasHover;

    PlanetTerrain? _hoverPlanet;
    SN.Vector3 _hoverPlanetPointW;
    bool _hasPlanetHover;

    const int TerrainToolNone = -1;
    const int PlanetToolNone = -1;
    public static Func<Terrain, int>? TerrainToolIndexProvider;
    public static Func<Terrain, float> TerrainBrushRadiusProvider = _ => 8f;
    public static Func<Terrain, float> TerrainBrushStrengthProvider = _ => 0.5f;
    public static Func<Terrain, float> TerrainBrushFalloffProvider = _ => 0.5f;

    public static Func<PlanetTerrain, int>? PlanetToolIndexProvider;
    public static Func<PlanetTerrain, float> PlanetBrushRadiusProvider = _ => 12f;
    public static Func<PlanetTerrain, float> PlanetBrushStrengthProvider = _ => 0.5f;
    public static Func<PlanetTerrain, float> PlanetBrushFalloffProvider = _ => 0.6f;

    int GetTerrainToolIndex(Terrain t)
    => TerrainToolIndexProvider?.Invoke(t) ?? TerrainToolNone;

    int GetPlanetToolIndex(PlanetTerrain p)
    => PlanetToolIndexProvider?.Invoke(p) ?? PlanetToolNone;

    Terrain? _terrHover;
    SN.Vector3 _terrHoverHitW;
    bool _terrPainting;
    bool _terrStrokeDirty;

    bool _paintingTerrain;
    Terrain? _paintTarget;
    float _paintSign;
    int _paintToolIndex;
    float _flattenTargetHeight;  // sampled on mouse-down for Flatten tool

    bool _paintingPlanet;
    PlanetTerrain? _planetPaintTarget;
    int _planetPaintToolIndex;
    float _planetPaintSign;
    float _planetFlattenTargetRadius;
    bool _planetStrokeDirty;
    SN.Vector3 _planetPaintLastHit;
    bool _planetPaintHasLastHit;
    bool _playModePlanetPaint;
    bool _playModePlanetBuild;
    public static Func<Terrain, int>? TerrainActivePaintLayerProvider; // active splatmap layer for Paint Layers tool

    private (SN.Matrix4x4 View, SN.Matrix4x4 Proj, Camera? Cam, bool UsingComponent)
    GetActiveViewProj(Size size)
    {
        Camera? cam = null;
        if (_lookThroughCamera)
        {
            cam = _lastPreviewCam;
            cam ??= SelectionService.Current?.Behaviors.OfType<Camera>().FirstOrDefault(b => b.Enabled);
            cam ??= SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain);
        }
        if (cam != null)
        {
            _lastPreviewCam = cam;
            return (cam.GetViewMatrix(), cam.GetProjectionMatrix(size), cam, true);
        }
        var (v, p) = GetViewProj(size);
        _lastPreviewCam = null;
        return (v, p, null, false);
    }

    Camera? FindBestCameraForPreview()
    {
        if (_selected != null)
        {
            var selCam = _selected.Behaviors.OfType<Camera>().FirstOrDefault(c => c.Enabled);
            if (selCam != null) return selCam;
        }
        var main = SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain);
        return main ?? SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
    }
    #endregion

    #region Tooling (Move/Rotate/Scale)
    public enum ToolMode { Hand, Move, Rotate, Scale }

    public static readonly StyledProperty<ToolMode> ToolProperty =
        AvaloniaProperty.Register<SceneView, ToolMode>(nameof(Tool), ToolMode.Hand);

    public ToolMode Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowGrid), true);
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public static readonly StyledProperty<bool> ShowWireProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowWire), false);
    public bool ShowWire { get => GetValue(ShowWireProperty); set => SetValue(ShowWireProperty, value); }

    public static readonly StyledProperty<bool> ShowLightProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowLight), true);
    public bool ShowLight { get => GetValue(ShowLightProperty); set => SetValue(ShowLightProperty, value); }

    public static readonly StyledProperty<bool> Is2DProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(Is2D), false);
    public bool Is2D { get => GetValue(Is2DProperty); set => SetValue(Is2DProperty, value); }

    public static readonly StyledProperty<bool> Supersample2xProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(Supersample2x), false);
    public bool Supersample2x { get => GetValue(Supersample2xProperty); set => SetValue(Supersample2xProperty, value); }

    public static readonly StyledProperty<bool> GizmoLocalProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(GizmoLocal), true);
    public bool GizmoLocal { get => GetValue(GizmoLocalProperty); set => SetValue(GizmoLocalProperty, value); }

    public static readonly StyledProperty<bool> ShowTerrainGizmosProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowTerrainGizmos), true);
    public bool ShowTerrainGizmos { get => GetValue(ShowTerrainGizmosProperty); set => SetValue(ShowTerrainGizmosProperty, value); }

    public static readonly StyledProperty<bool> ShowShadowsProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowShadows), defaultValue: true);
    public bool ShowShadows { get => GetValue(ShowShadowsProperty); set => SetValue(ShowShadowsProperty, value); }

    public static readonly StyledProperty<bool> ShowCamerasProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowCameras), true);
    public bool ShowCameras { get => GetValue(ShowCamerasProperty); set => SetValue(ShowCamerasProperty, value); }

    public static readonly StyledProperty<bool> ShowSelectionOutlineProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowSelectionOutline), true);
    public bool ShowSelectionOutline { get => GetValue(ShowSelectionOutlineProperty); set => SetValue(ShowSelectionOutlineProperty, value); }

    public static readonly StyledProperty<bool> ShowStatsOverlayProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowStatsOverlay), true);
    public bool ShowStatsOverlay { get => GetValue(ShowStatsOverlayProperty); set => SetValue(ShowStatsOverlayProperty, value); }

    public bool SnapEnabled { get; set; } = false;
    public float SnapStep { get; set; } = 0.5f;
    Point _pickCyclePixel;
    List<GameObject> _pickCycleHits = new();
    int _pickCycleIndex;
    DateTime _pickCycleUtc = DateTime.MinValue;

    #region View Settings Persistence

    private sealed class ViewSettingsDTO
    {
        public bool ShowGrid { get; set; } = true;
        public bool ShowWire { get; set; } = false;
        public bool ShowLight { get; set; } = true;
        public bool Is2D { get; set; } = false;
        public bool Supersample2x { get; set; } = false;
        public bool GizmoLocal { get; set; } = true;
        public bool ShowTerrainGizmos { get; set; } = true;
        public bool ShowShadows { get; set; } = true;
        public bool ShowCameras { get; set; } = true;
        public bool ShowSelectionOutline { get; set; } = true;
        public bool ShowStatsOverlay { get; set; } = true;
    }

    private static readonly JsonSerializerOptions s_viewJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private bool _isLoadingViewSettings;

    private static string? GetViewSettingsPath()
    {
        var cur = ProjectService.Current;
        if (cur == null) return null;
        var dir = Path.Combine(cur.RootPath, "ProjectSettings");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "viewsettings.json");
    }

    /// <summary>Save current view toggle states to ProjectSettings/viewsettings.json.</summary>
    public void SaveViewSettings()
    {
        if (_isLoadingViewSettings) return;
        var path = GetViewSettingsPath();
        if (path == null) return;

        try
        {
            var dto = new ViewSettingsDTO
            {
                ShowGrid = ShowGrid,
                ShowWire = ShowWire,
                ShowLight = ShowLight,
                Is2D = Is2D,
                Supersample2x = Supersample2x,
                GizmoLocal = GizmoLocal,
                ShowTerrainGizmos = ShowTerrainGizmos,
                ShowShadows = ShowShadows,
                ShowCameras = ShowCameras,
                ShowSelectionOutline = ShowSelectionOutline,
                ShowStatsOverlay = ShowStatsOverlay
            };

            var json = JsonSerializer.Serialize(dto, s_viewJson);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ViewSettings] Failed to save: {ex.Message}");
        }
    }

    /// <summary>Load view toggle states from ProjectSettings/viewsettings.json if it exists.</summary>
    public void LoadViewSettings()
    {
        var path = GetViewSettingsPath();
        if (path == null || !File.Exists(path)) return;

        try
        {
            _isLoadingViewSettings = true;

            var text = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ViewSettingsDTO>(text, s_viewJson);
            if (dto == null) return;

            ShowGrid = dto.ShowGrid;
            ShowWire = dto.ShowWire;
            ShowLight = dto.ShowLight;
            Is2D = dto.Is2D;
            Supersample2x = dto.Supersample2x;
            GizmoLocal = dto.GizmoLocal;
            ShowTerrainGizmos = dto.ShowTerrainGizmos;
            ShowShadows = dto.ShowShadows;
            ShowCameras = dto.ShowCameras;
            ShowSelectionOutline = dto.ShowSelectionOutline;
            ShowStatsOverlay = dto.ShowStatsOverlay;
        }
        catch (Exception ex)
        {
            Log.Warning($"[ViewSettings] Failed to load: {ex.Message}");
        }
        finally
        {
            _isLoadingViewSettings = false;
        }
    }

    #endregion

    static SceneView()
    {
        ToolProperty.Changed.AddClassHandler<SceneView>((s, _) => s.RequestNextFrameRendering());
        ShowGridProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowWireProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowLightProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        Is2DProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        Supersample2xProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        GizmoLocalProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowCamerasProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowTerrainGizmosProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowShadowsProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowSelectionOutlineProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
        ShowStatsOverlayProperty.Changed.AddClassHandler<SceneView>((s, _) => { s.RequestNextFrameRendering(); s.SaveViewSettings(); });
    }
    #endregion

    #region Translate gizmo state
    const double GizmoScreenLen = 80.0;
    const double GizmoPickPixels = 10.0;
    enum Axis { None, X, Y, Z }

    Axis _gizmoHot = Axis.None;
    Axis _dragAxis = Axis.None;
    Axis _axisLock = Axis.None;
    bool _isDragging;

    SN.Vector3 _dragAxisW;
    SN.Vector3 _dragAnchorW;
    SN.Vector3 _dragObjStartW;
    CoreVec3 _dragObjStartLocal;
    SN.Vector3 _dragPlaneN;
    CoreVec3 _dragStartRotation;
    CoreVec3 _dragStartScale;
    #endregion

    #region Constants & terrain tools
    void FrameSelected(GameObject go)
    {
        var (min, max) = SceneGraphUtil.ComputeWorldAABB(go);
        var center = (min + max) * 0.5f;
        float radius = (max - center).Length();
        float fov = 60f * MathF.PI / 180f;
        float fit = radius / MathF.Tan(fov * 0.5f);
        float desiredDistance = MathF.Max(1.5f, fit * 1.15f);

        if (SN.Vector3.Distance(_target, center) < 0.01f && MathF.Abs(_distance - desiredDistance) < 0.01f)
        {
            _target = center;
            _distance = desiredDistance;
            RequestNextFrameRendering();
            return;
        }

        _frameStartTarget = _target;
        _frameEndTarget = center;
        _frameStartDistance = _distance;
        _frameEndDistance = desiredDistance;
        _frameLerpWatch.Restart();
        _frameLerpTimer.Start();
    }

    bool HandleFlyKeyDown(Key k)
    {
        if (k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.R or Key.F
              or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            if (_keysDown.Add(k))
            {
                if (!_flyTimer.IsEnabled) { _flyWatch.Restart(); _flyTimer.Start(); }
            }
            return true;
        }
        return false;
    }

    bool HandleFlyKeyUp(Key k)
    {
        if (_keysDown.Remove(k))
        {
            if (_keysDown.Count == 0 && _flyTimer.IsEnabled) _flyTimer.Stop();
            return true;
        }
        return false;
    }

    void GetCameraAxes(out SN.Vector3 forward, out SN.Vector3 right, out SN.Vector3 up)
    {
        forward = new SN.Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw));
        var worldUp = SN.Vector3.UnitY;
        right = SN.Vector3.Cross(forward, worldUp);
        if (right.LengthSquared() < 1e-8f)
            right = SN.Vector3.Cross(forward, SN.Vector3.UnitZ);
        right = SN.Vector3.Normalize(right);
        up = SN.Vector3.Normalize(SN.Vector3.Cross(right, forward));
        float c = MathF.Cos(_roll);
        float s = MathF.Sin(_roll);
        var rolledRight = right * c + up * s;
        var rolledUp = -right * s + up * c;
        right = rolledRight;
        up = rolledUp;
    }

    void StepFly()
    {
        if (_isDragging) return;
        double dt = _flyWatch.Elapsed.TotalSeconds; _flyWatch.Restart();
        if (dt <= 0) return;

        GetCameraAxes(out var dir, out var right, out var up);
        float mul = 1f;
        if (_keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift)) mul *= _flyBoostMul;
        if (_keysDown.Contains(Key.LeftCtrl) || _keysDown.Contains(Key.RightCtrl)) mul *= _flySlowMul;

        bool rolled = false;
        const float rollSpeed = 1.6f;
        if (_keysDown.Contains(Key.Q)) { _roll -= rollSpeed * (float)dt * mul; rolled = true; }
        if (_keysDown.Contains(Key.E)) { _roll += rollSpeed * (float)dt * mul; rolled = true; }
        if (_roll > MathF.PI) _roll -= MathF.Tau;
        if (_roll < -MathF.PI) _roll += MathF.Tau;

        SN.Vector3 move = SN.Vector3.Zero;
        if (_keysDown.Contains(Key.W)) move += dir;
        if (_keysDown.Contains(Key.S)) move -= dir;
        if (_keysDown.Contains(Key.A)) move -= right;
        if (_keysDown.Contains(Key.D)) move += right;
        if (_keysDown.Contains(Key.R)) move += up;
        if (_keysDown.Contains(Key.F)) move -= up;
        if (move.LengthSquared() >= 1e-8f)
        {
            move = SN.Vector3.Normalize(move);
            float distScale = Math.Clamp(_distance * 0.35f, 0.5f, 20f);
            _target += move * (_flyBaseSpeed * distScale * (float)dt * mul);
            rolled = true;
        }
        if (rolled)
            RequestNextFrameRendering();
    }

    // Height clamp range: allows digging below the initial flatland
    const float MinTerrainH = -1f;
    const float MaxTerrainH = 1f;

    void ApplyRaiseLowerBrush(Terrain t, SN.Vector3 centerW, float sign)
    {
        if (!ComputeBrushParams(t, centerW, out var bp)) return;
        float baseDelta01 = 0.02f * bp.Strength;
        for (int z = bp.MinVz; z <= bp.MaxVz; z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = bp.MinVx; x <= bp.MaxVx; x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                int idx = z * bp.Nx + x;
                float h = t.Heights[idx] + sign * baseDelta01 * w;
                t.Heights[idx] = Math.Clamp(h, MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    void ApplyTerrainToolUnified(Terrain t, SN.Vector3 hitW, int toolIndex, float sign)
    {
        if (toolIndex == TerrainToolNone) return;
        switch (toolIndex)
        {
            case 0: ApplyRaiseLowerBrush(t, hitW, sign); break;
            case 1: ApplyPaintHolesBrush(t, hitW, sign); break;
            case 2: ApplyNoiseBrush(t, hitW, sign); break;
            case 3: ApplyStitchBlendBrush(t, hitW); break;
            case 4: ApplySculptBrush(t, hitW, sign); break;
            case 5: ApplyFlattenBrush(t, hitW); break;
            case 6: ApplyErodeBrush(t, hitW); break;
            case 7: ApplyPaintLayersBrush(t, hitW, sign); break;
            case 8: ApplySmoothBrush(t, hitW); break;
            case 9: ApplyPaintTreesBrush(t, hitW, sign); break;
            default: break;
        }
        _terrStrokeDirty = true;
    }

    // ────────────── Brush Common Helper ──────────────
    struct BrushParams
    {
        public int Nx, Nz;
        public int Cx, Cz, Rx, Rz;
        public float Hx, Hz, Dx, Dz;
        public float RLx, RLz;
        public SN.Vector3 CenterLocal;
        public float Strength, Falloff, InnerBand;
        // Affected vertex range for partial rebuild
        public int MinVx => Math.Max(0, Cx - Rx);
        public int MinVz => Math.Max(0, Cz - Rz);
        public int MaxVx => Math.Min(Nx - 1, Cx + Rx);
        public int MaxVz => Math.Min(Nz - 1, Cz + Rz);
    }

    bool ComputeBrushParams(Terrain t, SN.Vector3 hitW, out BrushParams bp)
    {
        bp = default;
        if (t == null || t.Heights == null || t.ResX <= 1 || t.ResZ <= 1) return false;
        float radiusW = TerrainBrushRadiusProvider != null ? Math.Max(0.001f, TerrainBrushRadiusProvider(t)) : 5f;
        bp.Strength = TerrainBrushStrengthProvider != null ? Math.Clamp(TerrainBrushStrengthProvider(t), 0f, 1f) : 0.5f;
        bp.Falloff = TerrainBrushFalloffProvider != null ? Math.Clamp(TerrainBrushFalloffProvider(t), 0f, 1f) : 0.5f;
        var W = TransformUtil.WorldFromTransform(t.gameObject!.Transform);
        if (!SN.Matrix4x4.Invert(W, out var invW)) return false;
        bp.CenterLocal = SN.Vector3.Transform(hitW, invW);
        bp.Hx = t.SizeX * 0.5f; bp.Hz = t.SizeZ * 0.5f;
        float sx = new SN.Vector3(W.M11, W.M21, W.M31).Length();
        float sz = new SN.Vector3(W.M13, W.M23, W.M33).Length();
        bp.RLx = radiusW / Math.Max(1e-6f, sx); bp.RLz = radiusW / Math.Max(1e-6f, sz);
        bp.Nx = t.ResX; bp.Nz = t.ResZ;
        bp.Dx = t.SizeX / (bp.Nx - 1); bp.Dz = t.SizeZ / (bp.Nz - 1);
        float tx = (bp.CenterLocal.X + bp.Hx) / t.SizeX;
        float tz = (bp.CenterLocal.Z + bp.Hz) / t.SizeZ;
        bp.Cx = (int)Math.Round(tx * (bp.Nx - 1)); bp.Cz = (int)Math.Round(tz * (bp.Nz - 1));
        bp.Rx = (int)Math.Ceiling(bp.RLx / bp.Dx); bp.Rz = (int)Math.Ceiling(bp.RLz / bp.Dz);
        bp.InnerBand = Math.Max(0f, 1f - bp.Falloff);
        return true;
    }

    float BrushWeight(in BrushParams bp, float xL, float zL)
    {
        float nxr = (xL - bp.CenterLocal.X) / Math.Max(1e-6f, bp.RLx);
        float nzr = (zL - bp.CenterLocal.Z) / Math.Max(1e-6f, bp.RLz);
        float rNorm = MathF.Sqrt(nxr * nxr + nzr * nzr);
        if (rNorm > 1f) return 0f;
        float w = rNorm <= bp.InnerBand ? 1f : 1f - Math.Clamp((rNorm - bp.InnerBand) / Math.Max(1e-6f, 1f - bp.InnerBand), 0f, 1f);
        return w * w * (3f - 2f * w); // smoothstep
    }

    // ────────────── Tool 1: Paint Holes ──────────────
    void ApplyPaintHolesBrush(Terrain t, SN.Vector3 hitW, float sign)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        int total = bp.Nx * bp.Nz;
        if (t.Holes == null || t.Holes.Length != total)
            t.Holes = new bool[total];
        bool setHole = sign > 0; // left click = add hole, right click = remove hole
        for (int z = Math.Max(0, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 1, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(0, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 1, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                if (BrushWeight(bp, xL, zL) > 0.1f)
                    t.Holes[z * bp.Nx + x] = setHole;
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Tool 2: Noise ──────────────
    void ApplyNoiseBrush(Terrain t, SN.Vector3 hitW, float sign)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        float baseDelta = 0.02f * bp.Strength;
        // Use a random seed offset each stroke so repeated clicks give varied results
        float seedX = hitW.X * 7.13f + hitW.Z * 3.71f;
        float seedZ = hitW.X * 2.37f + hitW.Z * 11.29f;
        for (int z = Math.Max(0, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 1, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(0, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 1, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                float noise = PerlinNoise2D(x * 0.15f + seedX, z * 0.15f + seedZ);
                int idx = z * bp.Nx + x;
                float h = t.Heights[idx] + sign * baseDelta * w * noise;
                t.Heights[idx] = Math.Clamp(h, MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // Simple 2D Perlin-like gradient noise (returns -1..1)
    static float PerlinNoise2D(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3f - 2f * xf);
        float v = yf * yf * (3f - 2f * yf);
        float n00 = Grad(xi, yi, xf, yf);
        float n10 = Grad(xi + 1, yi, xf - 1f, yf);
        float n01 = Grad(xi, yi + 1, xf, yf - 1f);
        float n11 = Grad(xi + 1, yi + 1, xf - 1f, yf - 1f);
        float nx0 = n00 + u * (n10 - n00);
        float nx1 = n01 + u * (n11 - n01);
        return nx0 + v * (nx1 - nx0);

        static float Grad(int ix, int iy, float dx, float dy)
        {
            int h = ix * 374761393 + iy * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            h = h ^ (h >> 16);
            return (h & 3) switch
            {
                0 => dx + dy,
                1 => dx - dy,
                2 => -dx + dy,
                _ => -dx - dy,
            };
        }
    }

    // ────────────── Tool 3: Stitch/Blend ──────────────
    void ApplyStitchBlendBrush(Terrain t, SN.Vector3 hitW)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        // Smooth heights at the brush area by averaging with neighbors (edge-blending)
        var copy = new float[bp.Nx * bp.Nz];
        Array.Copy(t.Heights, copy, copy.Length);
        for (int z = Math.Max(1, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 2, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(1, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 2, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL) * bp.Strength;
                if (w <= 0f) continue;
                int idx = z * bp.Nx + x;
                // Average of 8 neighbors + self
                float sum = 0f; int cnt = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    { sum += copy[(z + dz) * bp.Nx + (x + dx)]; cnt++; }
                float avg = sum / cnt;
                t.Heights[idx] = Math.Clamp(copy[idx] + (avg - copy[idx]) * w, MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Tool 4: Sculpt ──────────────
    void ApplySculptBrush(Terrain t, SN.Vector3 hitW, float sign)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        // Push/pull along Y axis with a softer, more sculpty curve than raise/lower
        float baseDelta = 0.015f * bp.Strength;
        for (int z = Math.Max(0, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 1, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(0, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 1, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                // Sculpt uses a sharper center, gentler edges (w^2)
                float sculpt = w * w;
                int idx = z * bp.Nx + x;
                float h = t.Heights[idx] + sign * baseDelta * sculpt;
                t.Heights[idx] = Math.Clamp(h, MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Tool 5: Flatten ──────────────
    void ApplyFlattenBrush(Terrain t, SN.Vector3 hitW)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        float lerpRate = 0.1f * bp.Strength; // how fast to lerp toward target
        for (int z = Math.Max(0, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 1, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(0, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 1, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                int idx = z * bp.Nx + x;
                float current = t.Heights[idx];
                // Lerp toward the flatten target height sampled on mouse-down
                t.Heights[idx] = Math.Clamp(current + ((_flattenTargetHeight - current) * lerpRate * w), MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Tool 6: Erode ──────────────
    void ApplyErodeBrush(Terrain t, SN.Vector3 hitW)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        // Simple hydraulic erosion: move material downhill
        float rate = 0.005f * bp.Strength;
        var copy = new float[bp.Nx * bp.Nz];
        Array.Copy(t.Heights, copy, copy.Length);
        for (int z = Math.Max(1, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 2, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(1, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 2, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                int idx = z * bp.Nx + x;
                float h = copy[idx];
                // Find steepest descent neighbor
                float minH = h; int minIdx = idx;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int ni = (z + dz) * bp.Nx + (x + dx);
                        if (copy[ni] < minH) { minH = copy[ni]; minIdx = ni; }
                    }
                if (minIdx != idx)
                {
                    float diff = h - minH;
                    float transfer = Math.Min(diff * 0.5f, rate * w);
                    t.Heights[idx] = Math.Clamp(t.Heights[idx] - transfer, MinTerrainH, MaxTerrainH);
                    t.Heights[minIdx] = Math.Clamp(t.Heights[minIdx] + transfer, MinTerrainH, MaxTerrainH);
                }
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Tool 7: Paint Layers (splatmap) ──────────────
    // Left-click (sign>0): paint the active layer. Right-click (sign<0): erase the active layer (restore layer 0).
    void ApplyPaintLayersBrush(Terrain t, SN.Vector3 hitW, float sign)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        int activeLayer = TerrainActivePaintLayerProvider?.Invoke(t) ?? 0;
        if (activeLayer < 0 || activeLayer >= 8) return;

        // Ensure splatmaps exist (use the terrain's own method to guarantee correct size)
        t.EnsureSplatmaps();
        int total = bp.Nx * bp.Nz;
        if (t.Splatmap0 == null || t.Splatmap0.Length != total * 4) return; // safety

        float paintRate = 0.15f * bp.Strength;
        // When erasing (sign<0), we'll push weight toward layer 0 instead
        int targetLayer = sign >= 0 ? activeLayer : 0;

        for (int z = Math.Max(0, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 1, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(0, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 1, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL);
                if (w <= 0f) continue;
                int vi = z * bp.Nx + x;
                float amount = paintRate * w;
                // Read all 8 weights
                float[] weights = new float[8];
                for (int c = 0; c < 4; c++) weights[c] = t.Splatmap0[vi * 4 + c];
                for (int c = 0; c < 4; c++) weights[4 + c] = t.Splatmap1[vi * 4 + c];
                // Increase target layer weight
                weights[targetLayer] = Math.Min(1f, weights[targetLayer] + amount);
                // Normalize so all sum to 1
                float sum = 0f;
                for (int i = 0; i < 8; i++) sum += weights[i];
                if (sum > 1e-6f)
                    for (int i = 0; i < 8; i++) weights[i] /= sum;
                // Write back
                for (int c = 0; c < 4; c++) t.Splatmap0[vi * 4 + c] = weights[c];
                for (int c = 0; c < 4; c++) t.Splatmap1[vi * 4 + c] = weights[4 + c];
            }
        }
        t.MarkSplatmapDirty();
    }

    // ────────────── Tool 8: Smooth ──────────────
    void ApplySmoothBrush(Terrain t, SN.Vector3 hitW)
    {
        if (!ComputeBrushParams(t, hitW, out var bp)) return;
        var copy = new float[bp.Nx * bp.Nz];
        Array.Copy(t.Heights, copy, copy.Length);
        for (int z = Math.Max(1, bp.Cz - bp.Rz); z <= Math.Min(bp.Nz - 2, bp.Cz + bp.Rz); z++)
        {
            float zL = -bp.Hz + z * bp.Dz;
            for (int x = Math.Max(1, bp.Cx - bp.Rx); x <= Math.Min(bp.Nx - 2, bp.Cx + bp.Rx); x++)
            {
                float xL = -bp.Hx + x * bp.Dx;
                float w = BrushWeight(bp, xL, zL) * bp.Strength;
                if (w <= 0f) continue;
                int idx = z * bp.Nx + x;
                // Gaussian-like weighted average: center=4, adjacent=2, diagonal=1
                float sum = copy[idx] * 4f;
                sum += copy[(z - 1) * bp.Nx + x] * 2f;
                sum += copy[(z + 1) * bp.Nx + x] * 2f;
                sum += copy[z * bp.Nx + (x - 1)] * 2f;
                sum += copy[z * bp.Nx + (x + 1)] * 2f;
                sum += copy[(z - 1) * bp.Nx + (x - 1)] * 1f;
                sum += copy[(z - 1) * bp.Nx + (x + 1)] * 1f;
                sum += copy[(z + 1) * bp.Nx + (x - 1)] * 1f;
                sum += copy[(z + 1) * bp.Nx + (x + 1)] * 1f;
                float avg = sum / 16f;
                t.Heights[idx] = Math.Clamp(copy[idx] + (avg - copy[idx]) * w, MinTerrainH, MaxTerrainH);
            }
        }
        t.RebuildArea(bp.MinVx, bp.MinVz, bp.MaxVx, bp.MaxVz);
    }

    // ────────────── Paint Trees Tool ──────────────
    // Provider delegates set by InspectorPanel
    public static Func<Terrain, int>? TerrainTreeDensityProvider;      // trees per stroke (1-20)
    public static Func<Terrain, float>? TerrainTreeMinScaleProvider;   // minimum random scale
    public static Func<Terrain, float>? TerrainTreeMaxScaleProvider;   // maximum random scale
    public static Func<Terrain, bool>? TerrainTreeRandomRotProvider;   // random Y rotation
    public static Func<Terrain, string?>? TerrainTreeModelPathProvider; // model path for imported tree asset (null = procedural)
    static readonly Random _treeRng = new();

    void ApplyPaintTreesBrush(Terrain t, SN.Vector3 hitW, float sign)
    {
        if (t.gameObject == null) return;

        if (sign < 0f)
        {
            // Right-click: erase trees within brush radius
            float radius = TerrainBrushRadiusProvider?.Invoke(t) ?? 5f;
            float radius2 = radius * radius;
            var toRemove = new System.Collections.Generic.List<GameObject>();
            foreach (var child in t.gameObject.Children)
            {
                bool isTree = false;
                foreach (var b in child.Behaviors)
                    if (b is Tree) { isTree = true; break; }
                if (!isTree) continue;

                // Compute child world position (child is parented to terrain)
                var parentW = TransformUtil.WorldFromTransform(t.gameObject.Transform);
                var childLocal = new SN.Vector3((float)child.Transform.Position.X, (float)child.Transform.Position.Y, (float)child.Transform.Position.Z);
                var childPos = SN.Vector3.Transform(childLocal, parentW);
                float dist2 = (childPos - hitW).LengthSquared();
                if (dist2 <= radius2)
                    toRemove.Add(child);
            }
            foreach (var go in toRemove)
                go.RemoveFromParent();
            if (toRemove.Count > 0) SceneService.NotifyChanged();
            return;
        }

        // Left-click: scatter trees
        int density = TerrainTreeDensityProvider?.Invoke(t) ?? 3;
        float minScale = TerrainTreeMinScaleProvider?.Invoke(t) ?? 0.8f;
        float maxScale = TerrainTreeMaxScaleProvider?.Invoke(t) ?? 1.2f;
        bool randomRot = TerrainTreeRandomRotProvider?.Invoke(t) ?? true;
        string? modelPath = TerrainTreeModelPathProvider?.Invoke(t);
        float radius_w = TerrainBrushRadiusProvider?.Invoke(t) ?? 5f;

        var terrainW = TransformUtil.WorldFromTransform(t.gameObject.Transform);
        SN.Matrix4x4.Invert(terrainW, out var invTerrainW);

        for (int i = 0; i < density; i++)
        {
            // Random point within brush circle
            float angle = (float)(_treeRng.NextDouble() * Math.PI * 2);
            float dist = (float)Math.Sqrt(_treeRng.NextDouble()) * radius_w;
            float ox = MathF.Cos(angle) * dist;
            float oz = MathF.Sin(angle) * dist;
            var worldPt = new SN.Vector3(hitW.X + ox, hitW.Y, hitW.Z + oz);

            // Convert to terrain local space to sample height
            var localPt = SN.Vector3.Transform(worldPt, invTerrainW);
            float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
            float tx = (localPt.X + hx) / t.SizeX;
            float tz = (localPt.Z + hz) / t.SizeZ;
            if (tx < 0 || tx > 1 || tz < 0 || tz > 1) continue;
            int cx = (int)Math.Round(tx * (t.ResX - 1));
            int cz = (int)Math.Round(tz * (t.ResZ - 1));
            cx = Math.Clamp(cx, 0, t.ResX - 1);
            cz = Math.Clamp(cz, 0, t.ResZ - 1);
            int idx = cz * t.ResX + cx;

            // Skip if there's a hole
            if (t.Holes != null && idx < t.Holes.Length && t.Holes[idx]) continue;

            float h01 = t.Heights[idx];
            float localY = h01 * t.HeightScale;

            // Create tree GameObject as child of terrain
            var treeGo = new GameObject($"Tree_{_treeRng.Next(10000)}");
            treeGo.Transform.Position = new Vector3(localPt.X, localY, localPt.Z);

            float scale = minScale + (float)_treeRng.NextDouble() * (maxScale - minScale);
            treeGo.Transform.Scale = new Vector3(scale, scale, scale);

            if (randomRot)
                treeGo.Transform.Rotation = new Vector3(0, (float)(_treeRng.NextDouble() * 360), 0);

            var treeComp = new Tree();
            // If a model path is set, use imported model instead of procedural
            if (!string.IsNullOrEmpty(modelPath))
                treeComp.ModelPath = modelPath;

            treeGo.AddBehavior(treeComp);
            // Tree [Require] auto-adds MeshFilter, MeshRenderer, TreeLOD

            // Trigger tree mesh generation
            treeComp.RebuildTree();

            t.gameObject.AddChild(treeGo);
        }
        SceneService.NotifyChanged();
    }

    // ────────────── Terrain Height Sampling ──────────────
    float SampleTerrainHeight01(Terrain t, SN.Vector3 worldPos)
    {
        if (t == null || t.Heights == null || t.ResX <= 1 || t.ResZ <= 1) return 0f;
        var W = TransformUtil.WorldFromTransform(t.gameObject!.Transform);
        if (!SN.Matrix4x4.Invert(W, out var invW)) return 0f;
        var local = SN.Vector3.Transform(worldPos, invW);
        float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
        float tx = (local.X + hx) / t.SizeX;
        float tz = (local.Z + hz) / t.SizeZ;
        int cx = (int)Math.Round(tx * (t.ResX - 1));
        int cz = (int)Math.Round(tz * (t.ResZ - 1));
        cx = Math.Clamp(cx, 0, t.ResX - 1);
        cz = Math.Clamp(cz, 0, t.ResZ - 1);
        return t.Heights[cz * t.ResX + cx];
    }

    static float Smooth01(float x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3f - 2f * x);
    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    // ────────────── Terrain LOD Update ──────────────
    void UpdateTerrainLOD(SN.Vector3 camPos)
    {
        foreach (var root in SceneService.Root) WalkLOD(root, camPos);
        static void WalkLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkLOD(c, cam);
        }
    }

    // ────────────── Tree LOD Update ──────────────
    void UpdateTreeLOD(SN.Vector3 camPos)
    {
        foreach (var root in SceneService.Root) WalkTreeLOD(root, camPos);
        static void WalkTreeLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            if (go.HideInHierarchy) return;
            foreach (var b in go.Behaviors)
                if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTreeLOD(c, cam);
        }
    }

    // ────────────── Mesh LOD Group (static meshes) ──────────────
    void UpdateMeshLodGroups(SN.Vector3 camPos)
    {
        foreach (var root in SceneService.Root) WalkMeshLod(root, camPos);
        static void WalkMeshLod(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is MeshLodGroup mg && mg.Enabled) mg.UpdateLOD(cam);
            foreach (var c in go.Children) WalkMeshLod(c, cam);
        }
    }

    // ────────────── Planet LOD Update ──────────────
    static void UpdatePlanetLOD(SN.Vector3 camPos, bool force = false)
    {
        // GameView owns planet LOD split/merge during play; SceneView only renders the current state.
        if (GameView.IsAnyViewPlaying)
            return;

        foreach (var planet in PlanetTerrain.ActivePlanets)
        {
            if (planet == null) continue;
            var cm = planet.ChunkManager;
            bool pending = cm != null && (cm.PendingEditCommands > 0 || cm.PendingCompletedJobs > 0 || cm.ActiveJobs > 0);
            if (!force && !pending)
                continue;

            planet.LastCameraPosition = camPos;
            if (force || pending)
                planet.RefreshLodAroundCamera(camPos);
        }
    }

    bool ShouldRefreshScenePlanetLod(SN.Vector3 camPos, float dt)
    {
        _scenePlanetLodAccumSec += Math.Max(0.0, dt);
        bool refresh = _scenePlanetLodAccumSec >= ScenePlanetLodIntervalSec;
        if (!float.IsNaN(_lastScenePlanetLodCamPos.X))
        {
            var d = camPos - _lastScenePlanetLodCamPos;
            if (d.LengthSquared() >= ScenePlanetLodMoveThreshold * ScenePlanetLodMoveThreshold)
                refresh = true;
        }
        else
        {
            refresh = true;
        }

        foreach (var planet in PlanetTerrain.ActivePlanets)
        {
            var cm = planet?.ChunkManager;
            if (cm != null && (cm.PendingEditCommands > 0 || cm.PendingCompletedJobs > 0 || cm.ActiveJobs > 0))
                refresh = true;
        }

        if (!refresh)
            return false;

        _scenePlanetLodAccumSec = 0.0;
        _lastScenePlanetLodCamPos = camPos;
        return true;
    }

    bool ShouldRefreshSceneMeshLod(SN.Vector3 camPos, float dt)
    {
        _sceneLodAccumSec += Math.Max(0.0, dt);
        bool refresh = _sceneLodAccumSec >= SceneLodIntervalSec;
        if (!float.IsNaN(_lastSceneLodCamPos.X))
        {
            var d = camPos - _lastSceneLodCamPos;
            if (d.LengthSquared() >= SceneLodMoveThreshold * SceneLodMoveThreshold)
                refresh = true;
        }
        else
        {
            refresh = true;
        }

        if (!refresh)
            return false;

        _sceneLodAccumSec = 0.0;
        _lastSceneLodCamPos = camPos;
        return true;
    }

    static SN.Vector3 GetPlayCameraWorldPosition()
    {
        var cam = SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain)
                  ?? SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
        if (cam == null)
            return default;
        return cam.TryGetWorldLookRay(out var origin, out _) ? origin : default;
    }

    // ────────────── Cached Scene Queries ──────────────
    void CacheSkyboxAndLight(GameObject go)
    {
        if (!go.Enabled) return;
        foreach (var b in go.Behaviors)
        {
            if (_cachedSkybox == null && b is Skybox sb && sb.IsActiveAndEnabled) _cachedSkybox = sb;
            if (_cachedLight == null && b is Light lt && lt.IsActiveAndEnabled) _cachedLight = lt;
        }
        if (_cachedSkybox != null && _cachedLight != null) return; // found both
        foreach (var c in go.Children) CacheSkyboxAndLight(c);
    }

    void UpdateTerrainHover(Point mouse)
    {
        var size = Bounds.Size;
        var (view, proj, _, _) = GetActiveViewProj(size);
        Picking.BuildPickRay(mouse, view, proj, size, out var ro, out var rd);
        _hasHover = TryFindClosestTerrainHit(ro, rd, out _hoverTerrain, out _hoverPointW);
        _hasPlanetHover = TryFindClosestPlanetHit(ro, rd, out _hoverPlanet, out _hoverPlanetPointW);
    }

    bool TryFindClosestPlanetHit(in SN.Vector3 ro, in SN.Vector3 rd, out PlanetTerrain? planet, out SN.Vector3 hitW)
    {
        planet = null;
        hitW = default;
        float bestD = float.PositiveInfinity;
        var planets = PlanetTerrain.ActivePlanets;
        for (int i = 0; i < planets.Count; i++)
        {
            var p = planets[i];
            if (p == null || !p.IsActiveAndEnabled) continue;
            float maxDist = Math.Max(50000f, p.Radius * 8f);
            if (!p.Raycast(ro, rd, maxDist, out var hit)) continue;
            if (hit.Distance < bestD)
            {
                bestD = hit.Distance;
                planet = p;
                hitW = hit.Point;
            }
        }
        return planet != null;
    }

    float MapPlanetBrushStrength(PlanetTerrain p, float ui01)
    {
        float s = Math.Clamp(ui01, 0f, 1f);
        float def = Math.Max(0.01f, p.DefaultManipulationStrength);
        return Math.Max(0.01f, s * def);
    }

    bool IsPlanetBrushActive()
        => _hasPlanetHover && _hoverPlanet != null && GetPlanetToolIndex(_hoverPlanet) != PlanetToolNone;

    bool TryBeginPlayModePlanetPaint(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (!GameView.IsAnyViewPlaying || Tool != ToolMode.Hand || IsPlanetBrushActive()) return false;
        if (!_hasPlanetHover || _hoverPlanet == null) return false;

        bool dig = props.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool build = props.IsRightButtonPressed
                     || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (!dig && !build) return false;

        _playModePlanetPaint = true;
        _playModePlanetBuild = build && !dig;
        PlanetTool.ApplyStrokeAt(_hoverPlanetPointW, dig, build);
        e.Pointer.Capture(this);
        e.Handled = true;
        RequestNextFrameRendering();
        return true;
    }

    void ApplyPlanetToolUnified(PlanetTerrain planet, SN.Vector3 hitW, int toolIndex, float sign)
    {
        if (toolIndex == PlanetToolNone) return;
        var localHit = planet.WorldToLocal(hitW);
        float hitR = localHit.Length();
        if (!GameView.IsAnyViewPlaying && hitR < planet.Radius * 0.45f)
            return;

        float radius = Math.Max(0.05f, PlanetBrushRadiusProvider(planet));
        radius = Math.Min(radius, Math.Max(8f, planet.Radius * 0.08f));
        if (_planetPaintHasLastHit)
        {
            float minStep = radius * 0.28f;
            if (SN.Vector3.DistanceSquared(hitW, _planetPaintLastHit) < minStep * minStep)
                return;
        }
        _planetPaintLastHit = hitW;
        _planetPaintHasLastHit = true;
        float strength = MapPlanetBrushStrength(planet, PlanetBrushStrengthProvider(planet));
        strength = Math.Min(strength, 6f);
        float falloff = Math.Clamp(PlanetBrushFalloffProvider(planet), 0.35f, 1f);
        switch (toolIndex)
        {
            case 0: // Dig (right-click / shift → build)
                if (sign < 0f) planet.BuildSphere(hitW, radius, strength, falloff);
                else planet.DigSphere(hitW, radius, strength, falloff);
                break;
            case 1: // Build (right-click / shift → dig)
                if (sign < 0f) planet.DigSphere(hitW, radius, strength, falloff);
                else planet.BuildSphere(hitW, radius, strength, falloff);
                break;
            case 2:
                planet.SmoothSphere(hitW, radius, strength, falloff);
                break;
            case 3:
                planet.FlattenSphere(hitW, radius, strength, falloff, _planetFlattenTargetRadius);
                break;
        }
        _planetStrokeDirty = true;
    }

    bool TryFindClosestTerrainHit(in SN.Vector3 ro, in SN.Vector3 rd, out Terrain? terrain, out SN.Vector3 hitW)
    {
        var roL = ro; var rdL = rd;
        Terrain? bestT = null; SN.Vector3 bestH = default; float bestD = float.PositiveInfinity;
        foreach (var root in SceneService.Root) Walk(root);
        terrain = bestT; hitW = bestH; return terrain != null;

        void Walk(GameObject go)
        {
            Terrain? t = null; MeshFilter? mf = null;
            foreach (var b in go.Behaviors) { if (t == null && b is Terrain tt) t = tt; if (mf == null && b is MeshFilter mm) mf = mm; }
            if (t != null && mf?.Mesh != null)
            {
                var W = TransformUtil.WorldFromTransform(go.Transform);
                if (!SN.Matrix4x4.Invert(W, out var invW)) goto NEXT;
                var rL = SN.Vector3.Transform(roL, invW);
                var dL = SN.Vector3.Normalize(SN.Vector3.TransformNormal(rdL, invW));

                // First try exact triangle raycast
                for (int i = 0; i < mf.Mesh.TriIndices.Length; i += 3)
                {
                    var v = mf.Mesh.Vertices; var tri = mf.Mesh.TriIndices;
                    if (RayTriMT(rL, dL, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]], out float tH) && tH > 1e-6f && tH < bestD)
                    {
                        bestD = tH; bestT = t;
                        bestH = SN.Vector3.Transform(rL + dL * tH, W);
                    }
                }

                // Fallback: ray-AABB intersection so brushes work over holes
                // Intersect the terrain's local bounding box (covers full extent including holes)
                if (bestT != t)
                {
                    float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
                    float maxY = t.HeightScale;
                    // Simple ray vs Y-slab: find where ray hits the Y range [-maxY, maxY]
                    // Then check if XZ is within terrain bounds
                    if (MathF.Abs(dL.Y) > 1e-7f)
                    {
                        // Try intersecting a horizontal plane at Y=0 (the default flat height)
                        float avgY = 0f;
                        float tPlane = (avgY - rL.Y) / dL.Y;
                        if (tPlane > 1e-6f && tPlane < bestD)
                        {
                            var hitLocal = rL + dL * tPlane;
                            if (hitLocal.X >= -hx && hitLocal.X <= hx && hitLocal.Z >= -hz && hitLocal.Z <= hz)
                            {
                                bestD = tPlane; bestT = t;
                                bestH = SN.Vector3.Transform(hitLocal, W);
                            }
                        }
                    }
                }
            }
            NEXT: foreach (var c in go.Children) Walk(c);
        }
    }

    static bool RayTriMT(in SN.Vector3 ro, in SN.Vector3 rd, in SN.Vector3 v0, in SN.Vector3 v1, in SN.Vector3 v2, out float t)
    {
        t = 0; const float E = 1e-7f;
        var e1 = v1 - v0; var e2 = v2 - v0;
        var p = SN.Vector3.Cross(rd, e2); float det = SN.Vector3.Dot(e1, p);
        if (det > -E && det < E) return false; float inv = 1f / det;
        var tv = ro - v0; float u = SN.Vector3.Dot(tv, p) * inv; if (u < 0 || u > 1) return false;
        var q = SN.Vector3.Cross(tv, e1); float v = SN.Vector3.Dot(rd, q) * inv; if (v < 0 || u + v > 1) return false;
        t = SN.Vector3.Dot(e2, q) * inv; return t > E;
    }
    #endregion

    #region Ctor & event hookup
    public SceneView()
    {
        Focusable = true;
        ClipToBounds = true;
        _selected = SelectionService.Current;
        SelectionService.Changed += () =>
        {
            _selected = SelectionService.Current;
            _multiSelected.Clear();
            _multiSelected.AddRange(SelectionService.Selected);
            RequestNextFrameRendering();
        };
        SelectionService.FrameRequested += go =>
        {
            if (go == null) return;
            _selected = go;
            FrameSelected(go);
        };
        SceneService.Changed += () =>
        {
            _sceneQueryDirty = true;
            if (GameView.IsAnyViewPlaying)
                return;
            _cache?.InvalidateAll();
            RequestNextFrameRendering();
        };

        // Full scene replacement (e.g. File > Load Scene) needs a heavier reset
        // than the incremental Changed handler above.
        SceneService.SceneReplaced += () =>
        {
            // Request a full GPU cache flush on the next GL render pass
            // (GL resources must be disposed inside the GL context).
            if (_cache != null) _cache.FlushRequested = true;
            _sceneQueryDirty = true;
            _cachedSkybox = null;
            _cachedLight = null;

            // Clear selection to avoid stale references to old scene objects
            SelectionService.Clear();
            _selected = null;
            _multiSelected.Clear();

            // Request the next GL frame at Render priority after the scene is committed.
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering,
                Avalonia.Threading.DispatcherPriority.Render);
        };

        AffectsRender<SceneView>(GizmoLocalProperty);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _flyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flyTimer.Tick += (_, __) => StepFly();
        _frameLerpTimer.Tick += (_, __) => StepFrameLerp();

        // FPS display timer - updates text outside of render cycle to avoid layout cascades
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, __) =>
        {
            if (!_fpsWatch.IsRunning || _fpsWatch.ElapsedMilliseconds < 400) return;
            double fps = _fpsFrameCount / _fpsWatch.Elapsed.TotalSeconds;
            _fpsFrameCount = 0;
            _fpsWatch.Restart();
            if (!ShowStatsOverlay)
            {
                FpsText = "";
                return;
            }

            var objectCount = CountSceneObjects();
            var selectedCount = _multiSelected.Count > 0 ? _multiSelected.Count : (_selected != null ? 1 : 0);
            FpsText = $"{fps:F0} FPS  Obj:{objectCount} Sel:{selectedCount} GL:{_lastFrameMs:F0}ms C:{_lastCompositMs:F0}ms O:{_lastOverlayMs:F0}ms [{_lastSectionTimes}]";
        };
        _fpsTimer.Start();

        _playModePreviewTimer.Tick += (_, __) =>
        {
            if (!GameView.IsAnyViewPlaying || _renderInFlight) return;
            _renderInFlight = true;
            RequestNextFrameRendering();
        };
        _playModePreviewTimer.Start();

        GameView.AnyPlayingStateChanged += OnAnyPlayingStateChanged;
        AttachedToVisualTree += (_, __) => OnAnyPlayingStateChanged();
        DetachedFromVisualTree += (_, __) => GameView.AnyPlayingStateChanged -= OnAnyPlayingStateChanged;
    }

    private void OnAnyPlayingStateChanged()
    {
        if (!GameView.IsAnyViewPlaying)
        {
            RequestNextFrameRendering();
            return;
        }

        // Let Game view take the shared GL lock for the first frames of Play.
        _lastPlayPreviewTicks = Stopwatch.GetTimestamp();
        if (_renderInFlight) return;
        _renderInFlight = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering,
            Avalonia.Threading.DispatcherPriority.Render);
    }
    #endregion

    #region OpenGL Lifecycle

    private static readonly string _glDiagLog = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "GE_GL_Diag.log");

    private static void DiagLog(string msg)
    {
        try { System.IO.File.AppendAllText(_glDiagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        Debug.WriteLine(msg);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try { System.IO.File.WriteAllText(_glDiagLog, ""); } catch { }
        try
        {
            _glCtx = new GLContext(name => gl.GetProcAddress(name));
            var g = _glCtx.GL;
            bool es = _glCtx.IsES;

            DiagLog($"[SceneView] GL version string: '{_glCtx.VersionString}'");
            DiagLog($"[SceneView] IsES={es}");

            unsafe
            {
                var renderer = g.GetStringS(Silk.NET.OpenGL.StringName.Renderer) ?? "?";
                var vendor = g.GetStringS(Silk.NET.OpenGL.StringName.Vendor) ?? "?";
                var slVer = g.GetStringS(Silk.NET.OpenGL.StringName.ShadingLanguageVersion) ?? "?";
                DiagLog($"[SceneView] Renderer: {renderer}");
                DiagLog($"[SceneView] Vendor: {vendor}");
                DiagLog($"[SceneView] GLSL version: {slVer}");
            }

            DiagLog("[SceneView] Compiling standard shader...");
            _standardShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.StandardVert, es),
                ShaderSources.Adapt(ShaderSources.StandardFrag, es));
            DiagLog("[SceneView] Standard shader OK");

            DiagLog("[SceneView] Compiling depth shader...");
            _depthShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.DepthOnlyVert, es),
                ShaderSources.Adapt(ShaderSources.DepthOnlyFrag, es));
            DiagLog("[SceneView] Depth shader OK");

            DiagLog("[SceneView] Compiling sky shader...");
            _skyShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.SkyVert, es),
                ShaderSources.Adapt(ShaderSources.SkyFrag, es));
            DiagLog("[SceneView] Sky shader OK");

            DiagLog("[SceneView] Compiling grid shader...");
            _gridShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.GridVert, es),
                ShaderSources.Adapt(ShaderSources.GridFrag, es));
            DiagLog("[SceneView] Grid shader OK");

            DiagLog("[SceneView] Compiling wire shader...");
            _wireShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.WireframeVert, es),
                ShaderSources.Adapt(ShaderSources.WireframeFrag, es));
            DiagLog("[SceneView] Wire shader OK");

            DiagLog("[SceneView] Compiling terrain shader...");
            _terrainShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.TerrainVert, es),
                ShaderSources.Adapt(ShaderSources.TerrainFrag, es));
            DiagLog("[SceneView] Terrain shader OK");

            DiagLog("[SceneView] Compiling particle shader...");
            _particleShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.ParticleVert, es),
                ShaderSources.Adapt(ShaderSources.ParticleFrag, es));
            DiagLog("[SceneView] Particle shader OK");

            DiagLog("[SceneView] Compiling water shader...");
            _waterShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.WaterVert, es),
                ShaderSources.Adapt(ShaderSources.WaterFrag, es));
            DiagLog("[SceneView] Water shader OK");

            DiagLog("[SceneView] Compiling planet terrain shader...");
            _planetTerrainShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PlanetTerrainVert, es),
                ShaderSources.Adapt(ShaderSources.PlanetTerrainFrag, es));
            DiagLog("[SceneView] Planet terrain shader OK");

            DiagLog("[SceneView] Compiling planet water shader...");
            _planetWaterShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PlanetWaterVert, es),
                ShaderSources.Adapt(ShaderSources.PlanetWaterFrag, es));
            DiagLog("[SceneView] Planet water shader OK");

            DiagLog("[SceneView] Compiling planet atmosphere shader...");
            _planetAtmosphereShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PlanetAtmosphereVert, es),
                ShaderSources.Adapt(ShaderSources.PlanetAtmosphereFrag, es));
            DiagLog("[SceneView] Planet atmosphere shader OK");

            DiagLog("[SceneView] Compiling planet cloud shader...");
            _planetCloudShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PlanetCloudsVert, es),
                ShaderSources.Adapt(ShaderSources.PlanetCloudsFrag, es));
            DiagLog("[SceneView] Planet cloud shader OK");

            DiagLog("[SceneView] Compiling post-process shader...");
            _postProcessShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PostProcessVert, es),
                ShaderSources.Adapt(ShaderSources.PostProcessFrag, es));
            DiagLog("[SceneView] Post-process shader OK");

            DiagLog("[SceneView] Compiling volumetric fog shader...");
            _volFogShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.VolumetricFogVert, es),
                ShaderSources.Adapt(ShaderSources.VolumetricFogFrag, es));
            DiagLog("[SceneView] Volumetric fog shader OK");

            _fsQuad = new FullscreenQuad(g);
            _cache = new ResourceCache(g);
            GpuCompressionCaps.Initialize(g);

            // Shadow map — 1024×1024 is a good balance of quality vs. performance.
            // 4096 was far too expensive for integrated GPUs (4× the fillrate).
            _shadow = new ShadowMapGPU(g, 1024, 1024);
            DiagLog("[SceneView] Shadow map OK");

            _canvasRenderer = new Core.Rendering.UI.CanvasRenderer(g, es);
            DiagLog("[SceneView] Canvas UI renderer OK");

            // Gizmo VAO/VBO – 24 vertices (3 lines + 3 arrowheads à 2 tris)
            _gizmoVao = g.GenVertexArray();
            _gizmoVbo = g.GenBuffer();
            g.BindVertexArray(_gizmoVao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, _gizmoVbo);
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(24 * 3 * sizeof(float)),
                         ReadOnlySpan<byte>.Empty, BufferUsageARB.DynamicDraw);
            g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            g.EnableVertexAttribArray(0);
            g.BindVertexArray(0);
            DiagLog("[SceneView] Gizmo VAO/VBO OK");

            // Collider gizmo VAO/VBO – dynamic buffer for all collider wireframes
            _colliderVao = g.GenVertexArray();
            _colliderVbo = g.GenBuffer();
            g.BindVertexArray(_colliderVao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, _colliderVbo);
            _colliderVboCapacity = 12000; // initial: ~2000 line segments
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_colliderVboCapacity * sizeof(float)),
                         ReadOnlySpan<byte>.Empty, BufferUsageARB.DynamicDraw);
            g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            g.EnableVertexAttribArray(0);
            g.BindVertexArray(0);
            DiagLog("[SceneView] Collider gizmo VAO/VBO OK");

            DiagLog("[SceneView] All GPU resources created successfully.");
        }
        catch (Exception ex)
        {
            DiagLog($"[SceneView] GL init FAILED: {ex}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        var g = _glCtx?.GL;
        if (g != null)
        {
            if (_gizmoVao != 0) { g.DeleteVertexArray(_gizmoVao); _gizmoVao = 0; }
            if (_gizmoVbo != 0) { g.DeleteBuffer(_gizmoVbo); _gizmoVbo = 0; }
            if (_colliderVao != 0) { g.DeleteVertexArray(_colliderVao); _colliderVao = 0; }
            if (_colliderVbo != 0) { g.DeleteBuffer(_colliderVbo); _colliderVbo = 0; }
        }
        _canvasRenderer?.Dispose(); _canvasRenderer = null;
        _sceneFBO?.Dispose(); _sceneFBO = null; _sceneFBO_W = 0; _sceneFBO_H = 0;
        _volFogFBO?.Dispose(); _volFogFBO = null;
        _postProcessShader?.Dispose(); _postProcessShader = null;
        _planetCloudShader?.Dispose(); _planetCloudShader = null;
        _planetAtmosphereShader?.Dispose(); _planetAtmosphereShader = null;
        _volFogShader?.Dispose(); _volFogShader = null;
        _waterShader?.Dispose(); _waterShader = null;
        _particleShader?.Dispose(); _particleShader = null;
        _shadow?.Dispose();
        _cache?.Dispose();
        _fsQuad?.Dispose();
        _terrainShader?.Dispose();
        _wireShader?.Dispose();
        _gridShader?.Dispose();
        _skyShader?.Dispose();
        _depthShader?.Dispose();
        _standardShader?.Dispose();
        _glCtx?.Dispose();
        _glCtx = null;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_glCtx == null || _standardShader == null || _skyShader == null || _gridShader == null || _fsQuad == null || _cache == null)
        {
            _renderInFlight = false;
            return;
        }

        if (GameView.IsAnyViewPlaying)
        {
            long now = Stopwatch.GetTimestamp();
            double since = (now - _lastPlayPreviewTicks) / (double)Stopwatch.Frequency;
            if (since < 0.18)
            {
                _renderInFlight = false;
                return;
            }
        }

        if (!SceneRenderer.TryBeginViewRender())
        {
            _renderInFlight = false;
            if (!GameView.IsAnyViewPlaying)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering,
                    Avalonia.Threading.DispatcherPriority.Render);
            }
            return;
        }
        _lastPlayPreviewTicks = Stopwatch.GetTimestamp();
        try
        {
            // Skip only weather particles during Play; trees/grass stay visible in Scene View.
            SceneRenderer.SkipPlanetVegetationDraws = GameView.IsAnyViewPlaying;
            var g = _glCtx.GL;

        // Flush any GL errors accumulated by the other view's rendering.
        // Both views share the same GL context; stale errors can confuse drivers.
        while (g.GetError() != GLEnum.NoError) { }

        // Full GPU cache flush requested (e.g. after loading a new scene).
        // Must happen inside the GL context to safely dispose GPU resources.
        if (_cache.FlushRequested)
        {
            _cache.FlushAll();
        }

        _frameTimer.Restart();
        double _tSetup = 0, _tShadow = 0, _tScene = 0, _tGizmo = 0;
        var _sec = Stopwatch.StartNew();

        // FPS frame count (display update happens via _fpsTimer)
        if (!_fpsWatch.IsRunning) _fpsWatch.Start();
        _fpsFrameCount++;

        // Wind update
        var now = _windWatch.Elapsed.TotalSeconds;
        var dt = now - _windPrev; _windPrev = now;
        if (dt < 0) dt = 0; if (dt > 0.1) dt = 0.1;
        WindSystem.Update((float)dt);

        // Use physical pixel size for the GL viewport (accounting for DPI)
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var size = Bounds.Size;
        int pxW = Math.Max(1, (int)(size.Width * scaling));
        int pxH = Math.Max(1, (int)(size.Height * scaling));

        // Bind Avalonia's framebuffer and reset essential GL state.
        // The other view may have left the context in an unexpected state.
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)pxW, (uint)pxH);
        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Less);
        g.Disable(EnableCap.Blend);
        g.ColorMask(true, true, true, true);
        g.DepthMask(true);

        // Use DIP size for projection (aspect ratio is the same, but cameras may use it)
        int W = Math.Max(1, (int)size.Width);
        int H = Math.Max(1, (int)size.Height);

        // Active view/proj
        var active = GetActiveViewProj(new Size(W, H));
        var view = active.View;
        var proj = active.Proj;

        // Camera position
        SN.Matrix4x4.Invert(view, out var invView);
        var camPos = new SN.Vector3(invView.M41, invView.M42, invView.M43);

        // Skybox / Light — cached lookup, refreshed on scene change
        if (_sceneQueryDirty)
        {
            _cachedSkybox = null;
            _cachedLight = null;
            foreach (var root in SceneService.Root)
                CacheSkyboxAndLight(root);
            _sceneQueryDirty = false;
        }

        // Skybox settings
        var sky = _cachedSkybox;
        var skyTop = sky?.Top ?? Avalonia.Media.Color.Parse("#1f1f1f");
        var skyBot = sky?.Bottom ?? Avalonia.Media.Color.Parse("#1f1f1f");
        Texture2D? skyTex = sky?.Texture;
        float skyBlend = sky?.TextureBlend ?? 0f;
        float skyYaw = sky?.Yaw ?? 0f;

        // If camera is inside a planet atmosphere shell, suppress skybox stars/textures.
        foreach (var planet in PlanetTerrain.ActivePlanets)
        {
            if (planet?.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled) continue;
            var atmo = planet.Atmosphere;
            if (atmo == null || !atmo.Enabled) continue;

            var p = planet.gameObject.Transform.Position;
            var center = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
            float groundR = atmo.GroundRadiusOverride > 0.01f ? atmo.GroundRadiusOverride : planet.Config.Radius;
            float atmoR = groundR + Math.Max(1f, atmo.AtmosphereHeight);
            if ((camPos - center).LengthSquared() <= atmoR * atmoR)
            {
                skyTex = null;
                skyBlend = 0f;
                var top = atmo.ZenithTint;
                var bot = atmo.HorizonTint;
                skyTop = Avalonia.Media.Color.FromRgb((byte)Math.Clamp((int)(top.X * 255f), 0, 255), (byte)Math.Clamp((int)(top.Y * 255f), 0, 255), (byte)Math.Clamp((int)(top.Z * 255f), 0, 255));
                skyBot = Avalonia.Media.Color.FromRgb((byte)Math.Clamp((int)(bot.X * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Y * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Z * 255f), 0, 255));
                break;
            }
        }

        // Sun direction (toward the sun): computed from Yaw + SunElevation
        // Elevation 0° = horizon (light goes deep through windows)
        // Elevation 90° = overhead (straight down, no window penetration)
        float skyElev = sky?.SunElevation ?? 45f;
        SN.Vector3? sunDir = null;
        {
            float elevRad = Math.Clamp(skyElev, 1f, 89f) * MathF.PI / 180f;
            float yawRad  = skyYaw * MathF.PI / 180f;
            // Direction FROM scene TO sun (spherical coords, Y-up)
            var baseSun = new SN.Vector3(0f, MathF.Sin(elevRad), MathF.Cos(elevRad));
            var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, yawRad);
            sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
        }

        // Clear
        g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // --- SKY PASS ---
        Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
            skyTop, skyBot, sunDir, skyTex, skyBlend, skyYaw);

        // --- GRID PASS ---
        if (ShowGrid && !active.UsingComponent)
        {
            Game_Engine.Core.Grid.RenderGPU(g, _gridShader, _fsQuad, view, proj, step: 1f, majorEvery: 5);
        }

        // --- LIGHTING ---
        var light = _cachedLight;
        SN.Vector3 L = SN.Vector3.Normalize(new SN.Vector3(0.35f, 0.9f, 0.45f));
        float Ambient = Math.Clamp(sky?.Ambient ?? 0f, 0f, 1f);
        float DiffuseK = ShowLight ? 1f : 0f;
        bool lightIsPoint = false;
        SN.Vector3 lightPosW = SN.Vector3.Zero;
        float lightRange = 10f;

        SN.Vector3 lightColorNorm = new SN.Vector3(1f, 1f, 1f);
        if (light is not null)
        {
            float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
            DiffuseK *= MathF.Max(0.01f, light.Intensity * lum);
            lightColorNorm = new SN.Vector3(light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f);
            if (light.Type == LightType.Directional && light.gameObject is { } lt)
                L = -ForwardFrom(lt.Transform);
            else if (light.Type == LightType.Point && light.gameObject is { } go)
            {
                lightIsPoint = true;
                var lw = SceneGraphUtil.AccumulateWorld(go);
                lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, lw);
                lightRange = Math.Max(0.001f, light.Range);
            }
        }

        // Match planet terrain: inside an atmosphere shell, use PlanetAtmosphere ambient for forward meshes.
        SceneRenderer.TryApplyPlanetAtmosphereAmbient(camPos, light, ref Ambient);

        var fallbackPlanetSunDir = SN.Vector3.Normalize(-L);
        if (fallbackPlanetSunDir.LengthSquared() < 1e-5f)
            fallbackPlanetSunDir = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));

        _tSetup = _sec.Elapsed.TotalMilliseconds; _sec.Restart();

        // --- SHADOW MAP PASS (skippable via ShowShadows toggle) ---
        SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
        GPUFramebuffer? shadowFBO = null;
        if (ShowShadows && _shadow != null && _depthShader != null)
        {
            // Sun direction: direction sunlight travels (from sun toward scene)
            var sunShineDir = fallbackPlanetSunDir;

            // Shadow frustum centered on orbit target
            var sceneCenter = _target;
            float sceneRadius = Math.Max(20f, _distance * 1.5f);
            shadowVP = ShadowMapGPU.BuildDirectionalLightVP(sunShineDir, sceneCenter, sceneRadius);
            _shadow.LightVP = shadowVP;

            // Render depth from the sun's perspective
            _shadow.Begin(g);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            SceneRenderer.RenderShadowPass(g, _depthShader, _cache!, shadowVP);
            _shadow.End(g, (uint)fb);

            // Restore viewport for main pass
            g.Viewport(0, 0, (uint)pxW, (uint)pxH);
            shadowFBO = _shadow.FBO;
        }

        _tShadow = _sec.Elapsed.TotalMilliseconds; _sec.Restart();

        // --- UNDERWATER DETECTION ---
        var underwater = UnderwaterQuery.GetState(camPos);

        // --- POST-PROCESSING FBO setup ---
        // If a PostProcessVolume is active, render the scene to an offscreen FBO
        // so we can apply screen-space effects before blitting to Avalonia's FB.
        var postVolume = PostProcessVolume.GetActive();
        bool usePostFX = (postVolume != null || underwater != null) && _postProcessShader != null && !ShowWire;

        if (usePostFX)
        {
            // Create / resize the scene FBO as needed
            if (_sceneFBO == null) _sceneFBO = new GPUFramebuffer(g);
            if (_sceneFBO_W != pxW || _sceneFBO_H != pxH)
            {
                _sceneFBO.SetupColorDepth(pxW, pxH);
                _sceneFBO_W = pxW; _sceneFBO_H = pxH;
            }

            _sceneFBO.Bind();
            g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Re-render sky into the FBO
            Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
                skyTop, skyBot, sunDir, skyTex, skyBlend, skyYaw);
        }

        // --- SCENE PASS (GPU draw calls) ---
        if (!ShowWire)
        {
            // sunShineDir was computed for the shadow pass; fall back to a default if not set
            var sunSD = fallbackPlanetSunDir;
            float sceneDt = (float)dt;
            TerrainStreamer.SyncAll(camPos);
            if (ShouldRefreshSceneMeshLod(camPos, sceneDt))
            {
                UpdateTerrainLOD(camPos);
                UpdateTreeLOD(camPos);
                UpdateMeshLodGroups(camPos);
            }
            if (ShouldRefreshScenePlanetLod(camPos, sceneDt))
                UpdatePlanetLOD(camPos, force: true);

            SceneRenderer.RenderGPU(g, _standardShader!, _depthShader!, _cache,
                view, proj,
                SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, camPos, sunSD,
                terrainShader: _terrainShader,
                lightColor: lightColorNorm);

            // --- PLANET TERRAIN ---
            if (_planetTerrainShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetTerrain(g, _planetTerrainShader, _cache,
                        view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                        pc, shadowFBO, shadowVP);
                }
            }

            // --- WATER ---
            if (_waterShader != null)
            {
                var skyC = sky != null
                    ? new SN.Vector3(sky.Top.R / 255f, sky.Top.G / 255f, sky.Top.B / 255f)
                    : new SN.Vector3(0.5f, 0.6f, 0.8f);
                SceneRenderer.RenderWater(g, _waterShader, _cache, view, proj,
                    SN.Vector3.Normalize(-L), Ambient, DiffuseK, camPos, skyC);
            }

            if (_planetAtmosphereShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetAtmosphere(g, _planetAtmosphereShader, _cache,
                        view, proj, planet, atmo, camPos, pc);
                }
            }

            if (_planetCloudShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetClouds(g, _planetCloudShader, _cache,
                        view, proj, planet, atmo, camPos, pc, (float)Core.Time.time);
                }
            }

            // Planet water after atmosphere/cloud shells so haze does not cover the surface.
            if (_planetWaterShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetWater(g, _planetWaterShader, _cache,
                        view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                        pc, planet.Config.SeaLevel);
                }
            }

            // --- PARTICLES ---
            if (_particleShader != null)
                SceneRenderer.RenderParticles(g, _particleShader, _cache, view, proj);

            // --- WORLD-SPACE UI CANVASES ---
            if (_canvasRenderer != null)
            {
                var viewProj = view * proj;
                foreach (var wc in Core.Component.UI.Canvas.All)
                {
                    if (wc.IsActiveAndEnabled && wc.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
                        _canvasRenderer.RenderWorldCanvas(wc, in viewProj, _cache);
                }
            }
        }

        // --- VOLUMETRIC FOG PASS ---
        GPUTexture? postInputTex = _sceneFBO?.ColorTexture;
        if (_volFogShader != null && postVolume?.VolumetricFogEnabled == true
            && _sceneFBO?.ColorTexture != null && _sceneFBO?.DepthTexture != null)
        {
            if (_volFogFBO == null) _volFogFBO = new GPUFramebuffer(g);
            if (_volFogFBO.Width != pxW || _volFogFBO.Height != pxH)
                _volFogFBO.SetupColorDepth(pxW, pxH);

            _volFogFBO.Bind();
            g.ClearColor(0f, 0f, 0f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit);

            var volFogSunDir = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));

            g.BindVertexArray(_fsQuad!.VAO);
            SceneRenderer.RenderVolumetricFog(g, _volFogShader, _fsQuad!,
                _sceneFBO.ColorTexture, _sceneFBO.DepthTexture,
                view, proj, camPos,
                volFogSunDir, lightColorNorm,
                shadowFBO, shadowVP, postVolume,
                (float)Core.Time.time);
            g.BindVertexArray(0);

            postInputTex = _volFogFBO.ColorTexture;
        }

        // --- POST-PROCESSING BLIT ---
        if (usePostFX && postInputTex != null)
        {
            // Bind Avalonia's framebuffer as the output target
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)pxW, (uint)pxH);
            g.Disable(EnableCap.DepthTest);

            g.BindVertexArray(_fsQuad!.VAO);
            SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, postInputTex, pxW, pxH, postVolume, underwater, (float)Core.Time.time);
            g.BindVertexArray(0);

            g.Enable(EnableCap.DepthTest);

            // Blit depth from scene FBO so gizmos occlude correctly
            g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFBO.Handle);
            g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
            g.BlitFramebuffer(0, 0, pxW, pxH, 0, 0, pxW, pxH,
                ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        }

        // --- CANVAS UI OVERLAY (screen-space canvases on top of the scene) ---
        if (_canvasRenderer != null && _cache != null)
        {
            _canvasRenderer.RenderOverlays(pxW, pxH, _cache);
        }

        _tScene = _sec.Elapsed.TotalMilliseconds; _sec.Restart();

        // --- GIZMO PASS (GL lines + cones on top of scene) ---
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        RenderGizmoGL(g, view, proj, new Size(W, H));
        RenderSelectionOutlineGL(g, view, proj);

        // --- WIREFRAME PASS (GL lines — always visible on top) ---
        RenderWireframeGL(g, view, proj);

        // --- COLLIDER GIZMO PASS (GL lines — always visible on top) ---
        RenderColliderGizmosGL(g, view, proj);

        // --- TERRAIN BRUSH GIZMO PASS (GL lines — independent of collider toggle) ---
        if (ShowTerrainGizmos)
            RenderTerrainGizmosGL(g, view, proj);

        // --- CANVAS UI RECTRANSFORM GIZMO PASS (GL lines — shows UI element bounds) ---
        RenderRectTransformGizmosGL(g, view, proj);

        // Periodic GPU resource eviction (avoid unbounded cache growth)
        if (++_evictCounter > 300) // roughly every ~5s at 60fps
        {
            _evictCounter = 0;
            _cache.Maintain(maxEntries: 512, maxReleasesPerFrame: 96);
        }

        _tGizmo = _sec.Elapsed.TotalMilliseconds;

        // Flush GPU command queue so that when Avalonia does its readback (glReadPixels),
        // the GPU has already started or finished the work, reducing stall time.
        g.Flush();

        _lastFrameMs = _frameTimer.Elapsed.TotalMilliseconds;
        _lastSectionTimes = $"S:{_tSetup:F0} Sh:{_tShadow:F0} M:{_tScene:F0} G:{_tGizmo:F0}";

        // Restore GL state for Avalonia compositing
        g.UseProgram(0);
        g.BindVertexArray(0);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Disable(EnableCap.Blend);
        g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, 0);
        }
        finally
        {
            SceneRenderer.SkipPlanetVegetationDraws = false;
            SceneRenderer.EndViewRender();
            _renderInFlight = false;
        }
    }
    private int _evictCounter;

    #endregion

    #region 2D Overlay (gizmos, wireframes — drawn by Avalonia after GL)
    public override void Render(DrawingContext ctx)
    {
        // Material warm-up runs outside GL context to avoid blocking GPU work
        MaterialRebind.RepairScene();
        if (MaterialRebind.NeedsMoreFrames)
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering, Avalonia.Threading.DispatcherPriority.Render);

        // This calls OnOpenGlRender internally, then Avalonia reads back the FBO (glReadPixels).
        // Both GL rendering AND compositing time are included in this call.
        var compSw = Stopwatch.StartNew();
        base.Render(ctx);
        _lastCompositMs = compSw.Elapsed.TotalMilliseconds;

        // Now draw 2D overlays on top
        compSw.Restart();
        var size = Bounds.Size;
        var active = GetActiveViewProj(size);
        var view = active.View;
        var proj = active.Proj;
        var vp = view * proj;

        // Camera frustum overlays
        if (ShowCameras)
            CameraOverlay.DrawCameraFrustums(ctx, view, proj, size, active.Cam);

        // Wireframe, collider gizmos, and terrain gizmos are now rendered in the GL pass
        // (RenderWireframeGL / RenderColliderGizmosGL / RenderTerrainGizmosGL).
        _lastOverlayMs = compSw.Elapsed.TotalMilliseconds;
    }

    #endregion

    // ── Multi-select support ──
    readonly List<GameObject> _multiSelected = new();
    public IReadOnlyList<GameObject> MultiSelected => _multiSelected;

    // ── Clipboard for copy/paste ──
    private static string? _clipboardJson;

    #region Input: orbit/pan & gizmo drag
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (TryGetBookmarkSlot(e.Key, out var slot))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SaveCameraBookmark(slot);
                e.Handled = true;
                return;
            }

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                RecallCameraBookmark(slot);
                e.Handled = true;
                return;
            }
        }

        // F = Focus on selected
        if (e.Key == Key.F && _selected != null) { FrameSelected(_selected); e.Handled = true; }

        // L = Local/World gizmo toggle
        if (e.Key == Key.L && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            GizmoLocal = !GizmoLocal;
            RequestNextFrameRendering();
            e.Handled = true;
            return;
        }

        // X/Y/Z axis lock for transform tools
        if ((e.Key == Key.X || e.Key == Key.Y || e.Key == Key.Z) && Tool != ToolMode.Hand)
        {
            var requested = e.Key switch
            {
                Key.X => Axis.X,
                Key.Y => Axis.Y,
                Key.Z => Axis.Z,
                _ => Axis.None
            };
            _axisLock = _axisLock == requested ? Axis.None : requested;
            Log.Info($"[SceneView] Axis lock: {_axisLock}");
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+T: precise transform entry
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selected != null)
        {
            _ = OpenPreciseTransformDialogAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+Z = Undo
            if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { UndoService.Undo(); e.Handled = true; }
            // Ctrl+Y / Ctrl+Shift+Z = Redo
            else if (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { UndoService.Redo(); e.Handled = true; }

            // Ctrl+C = Copy selected GameObject(s)
            else if (e.Key == Key.C && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selected != null)
            {
                CopySelected(); e.Handled = true;
            }
            // Ctrl+V = Paste
            else if (e.Key == Key.V && _clipboardJson != null)
            {
                PasteFromClipboard(); e.Handled = true;
            }
            // Ctrl+D = Duplicate
            else if (e.Key == Key.D && _selected != null)
            {
                DuplicateSelected(); e.Handled = true;
            }
        }

        // Delete / Backspace = Delete selected
        if ((e.Key == Key.Delete || e.Key == Key.Back) && _selected != null)
        {
            DeleteSelected(); e.Handled = true;
        }

        // Ctrl+G = toggle snap (translate uses SnapStep; rotate uses SnapAngleDegrees)
        if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SnapEnabled = !SnapEnabled;
            Log.Info(SnapEnabled ? $"[SceneView] Snap ON (move grid {SnapStep} world units)" : "[SceneView] Snap OFF");
            RequestNextFrameRendering();
            e.Handled = true;
        }

        if (GameView.IsAnyViewPlaying)
        {
            Input.FeedKeyDown(KeyMap.FromAvalonia(e.Key));
        }
        else if (HandleFlyKeyDown(e.Key)) { e.Handled = true; return; }

        // C = toggle camera preview (only when NOT using Ctrl+C for copy)
        if (e.Key == Key.C && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_lookThroughCamera)
            { _lastPreviewCam = FindBestCameraForPreview(); _lookThroughCamera = _lastPreviewCam != null; }
            else { _lookThroughCamera = false; _lastPreviewCam = null; }
            RequestNextFrameRendering(); e.Handled = true;
        }
    }

    // ── Copy/Paste/Duplicate/Delete helpers (multi-object aware) ──

    private static List<string>? _clipboardJsonList;

    void CopySelected()
    {
        var targets = _multiSelected.Count > 0 ? _multiSelected : (_selected != null ? new List<GameObject> { _selected } : new List<GameObject>());
        if (targets.Count == 0) return;
        try
        {
            _clipboardJsonList = new List<string>();
            foreach (var go in targets)
            {
                _clipboardJsonList.Add(System.Text.Json.JsonSerializer.Serialize(go, SceneSerialization.JsonOptions));
            }
            // Keep backward-compat single clipboard for simple paste
            _clipboardJson = _clipboardJsonList.Count > 0 ? _clipboardJsonList[0] : null;
            Log.Info($"[SceneView] Copied {targets.Count} object(s)");
        }
        catch (Exception ex) { Log.Error($"[SceneView] Copy failed: {ex.Message}"); }
    }

    void PasteFromClipboard()
    {
        var jsonList = _clipboardJsonList ?? (_clipboardJson != null ? new List<string> { _clipboardJson } : null);
        if (jsonList == null || jsonList.Count == 0) return;
        try
        {
            var pasted = new List<GameObject>();
            foreach (var json in jsonList)
            {
                var go = System.Text.Json.JsonSerializer.Deserialize<GameObject>(json, SceneSerialization.JsonOptions);
                if (go == null) continue;
                go.Name += " (Copy)";
                go.Transform.Position = new Vector3(
                    go.Transform.Position.X + 1f,
                    go.Transform.Position.Y,
                    go.Transform.Position.Z + 1f);
                SceneService.Add(go);
                pasted.Add(go);
            }
            if (pasted.Count > 0)
            {
                SelectionService.SetMultiple(pasted);
                _selected = pasted[^1];
            }
            SceneService.NotifyChanged();
            Log.Info($"[SceneView] Pasted {pasted.Count} object(s)");
        }
        catch (Exception ex) { Log.Error($"[SceneView] Paste failed: {ex.Message}"); }
    }

    void DuplicateSelected()
    {
        var targets = _multiSelected.Count > 0 ? _multiSelected : (_selected != null ? new List<GameObject> { _selected } : new List<GameObject>());
        if (targets.Count == 0) return;
        try
        {
            var duplicated = new List<GameObject>();
            foreach (var src in targets)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(src, SceneSerialization.JsonOptions);
                var go = System.Text.Json.JsonSerializer.Deserialize<GameObject>(json, SceneSerialization.JsonOptions);
                if (go == null) continue;
                go.Name += " (Dup)";
                go.Transform.Position = new Vector3(
                    go.Transform.Position.X + 0.5f,
                    go.Transform.Position.Y,
                    go.Transform.Position.Z + 0.5f);

                if (src.Parent != null)
                    src.Parent.AddChild(go);
                else
                    SceneService.Add(go);

                duplicated.Add(go);
            }
            if (duplicated.Count > 0)
            {
                SelectionService.SetMultiple(duplicated);
                _selected = duplicated[^1];
            }
            SceneService.NotifyChanged();
            Log.Info($"[SceneView] Duplicated {duplicated.Count} object(s)");
        }
        catch (Exception ex) { Log.Error($"[SceneView] Duplicate failed: {ex.Message}"); }
    }

    void DeleteSelected()
    {
        var targets = _multiSelected.Count > 0
            ? new List<GameObject>(_multiSelected)
            : (_selected != null ? new List<GameObject> { _selected } : new List<GameObject>());
        if (targets.Count == 0) return;

        SelectionService.Clear();
        _selected = null;

        foreach (var go in targets)
        {
            if (go.Parent != null)
                go.Parent.Children.Remove(go);
            else
                SceneService.Remove(go);
        }

        SceneService.NotifyChanged();
        RequestNextFrameRendering();
        Log.Info($"[SceneView] Deleted {targets.Count} object(s)");
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (GameView.IsAnyViewPlaying)
            Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
        else if (HandleFlyKeyUp(e.Key)) e.Handled = true;
    }

    private void SaveCameraBookmark(int slot)
    {
        _cameraBookmarks[slot] = new CameraBookmark(_target, _yaw, _pitch, _roll, _distance);
        Log.Info($"[SceneView] Saved camera bookmark {slot + 1}");
    }

    private void RecallCameraBookmark(int slot)
    {
        var bookmark = _cameraBookmarks[slot];
        if (bookmark is null) return;

        _target = bookmark.Value.Target;
        _yaw = bookmark.Value.Yaw;
        _pitch = bookmark.Value.Pitch;
        _roll = bookmark.Value.Roll;
        _distance = bookmark.Value.Distance;
        RequestNextFrameRendering();
        Log.Info($"[SceneView] Recalled camera bookmark {slot + 1}");
    }

    private static bool TryGetBookmarkSlot(Key key, out int slot)
    {
        slot = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            _ => -1
        };
        return slot >= 0;
    }

    private static int CountSceneObjects()
    {
        int count = 0;
        foreach (var root in SceneService.Root)
            CountRecursive(root, ref count);
        return count;
    }

    private static void CountRecursive(GameObject go, ref int count)
    {
        count++;
        foreach (var child in go.Children)
            CountRecursive(child, ref count);
    }

    private async Task OpenPreciseTransformDialogAsync()
    {
        if (_selected == null) return;
        var selectedObjects = _multiSelected.Count > 0 ? _multiSelected : new List<GameObject> { _selected };
        var baseValue = Tool switch
        {
            ToolMode.Rotate => _selected.Transform.Rotation,
            ToolMode.Scale => _selected.Transform.Scale,
            _ => _selected.Transform.Position
        };

        var xBox = new TextBox { Text = baseValue.X.ToString("0.###") };
        var yBox = new TextBox { Text = baseValue.Y.ToString("0.###") };
        var zBox = new TextBox { Text = baseValue.Z.ToString("0.###") };
        var apply = new Button { Content = "Apply", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var result = false;
        var dlg = new Window
        {
            Title = $"Precise {(Tool == ToolMode.Rotate ? "Rotation" : Tool == ToolMode.Scale ? "Scale" : "Position")}",
            Width = 340,
            Height = 220,
            CanResize = false
        };

        apply.Click += (_, __) => { result = true; dlg.Close(); };
        cancel.Click += (_, __) => dlg.Close();

        dlg.Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "X" }, xBox,
                new TextBlock { Text = "Y" }, yBox,
                new TextBlock { Text = "Z" }, zBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { apply, cancel }
                }
            }
        };

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;
        await dlg.ShowDialog(host);
        if (!result) return;

        if (!float.TryParse(xBox.Text, out var x) ||
            !float.TryParse(yBox.Text, out var y) ||
            !float.TryParse(zBox.Text, out var z))
            return;

        foreach (var go in selectedObjects)
        {
            switch (Tool)
            {
                case ToolMode.Rotate:
                    go.Transform.Rotation = new CoreVec3(x, y, z);
                    break;
                case ToolMode.Scale:
                    go.Transform.Scale = new CoreVec3(Math.Max(0.001f, x), Math.Max(0.001f, y), Math.Max(0.001f, z));
                    break;
                default:
                    go.Transform.Position = new CoreVec3(x, y, z);
                    break;
            }
        }

        SceneService.NotifyChanged();
        SelectionService.Touch();
        RequestNextFrameRendering();
    }

    private void StepFrameLerp()
    {
        var t = (float)(_frameLerpWatch.Elapsed.TotalSeconds / FrameLerpDurationSec);
        if (t >= 1f)
        {
            _target = _frameEndTarget;
            _distance = _frameEndDistance;
            _frameLerpTimer.Stop();
            RequestNextFrameRendering();
            return;
        }

        // Smoothstep for soft ease-in/out camera motion.
        var s = t * t * (3f - 2f * t);
        _target = SN.Vector3.Lerp(_frameStartTarget, _frameEndTarget, s);
        _distance = _frameStartDistance + (_frameEndDistance - _frameStartDistance) * s;
        RequestNextFrameRendering();
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus(); _last = e.GetPosition(this);
        UpdateTerrainHover(_last);
        var props = e.GetCurrentPoint(this).Properties;
        if (TryBeginPlayModePlanetPaint(e, props))
            return;
        if (ShowTerrainGizmos && _hasPlanetHover && _hoverPlanet != null && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            int planetTool = GetPlanetToolIndex(_hoverPlanet);
            if (planetTool != PlanetToolNone)
            {
                _paintingPlanet = true;
                _planetPaintTarget = _hoverPlanet;
                _planetPaintToolIndex = planetTool;
                _planetPaintSign = (props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ? -1f : +1f;
                _planetFlattenTargetRadius = _hoverPlanetPointW.Length() > 0.01f
                    ? _hoverPlanet.WorldToLocal(_hoverPlanetPointW).Length()
                    : _hoverPlanet.Radius;
                _planetPaintHasLastHit = false;
                ApplyPlanetToolUnified(_planetPaintTarget, _hoverPlanetPointW, _planetPaintToolIndex, _planetPaintSign);
                RequestNextFrameRendering();
                e.Pointer.Capture(this); e.Handled = true; return;
            }
        }
        if (ShowTerrainGizmos && _hasHover && _hoverTerrain != null && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            int toolIndex = GetTerrainToolIndex(_hoverTerrain);
            if (toolIndex != TerrainToolNone)
            {
                _paintingTerrain = true; _paintTarget = _hoverTerrain; _paintToolIndex = toolIndex;
                _paintSign = (props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ? -1f : +1f;
                // Flatten tool: sample center height on mouse-down
                if (toolIndex == 5) _flattenTargetHeight = SampleTerrainHeight01(_hoverTerrain, _hoverPointW);
                ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign);
                RequestNextFrameRendering();
                e.Pointer.Capture(this); e.Handled = true; return;
            }
        }
        var p = e.GetCurrentPoint(this).Properties;
        if (Tool != ToolMode.Hand && _selected != null)
        {
            var (view2, proj2) = GetViewProj(Bounds.Size);
            if (BeginAxisDrag(_last, view2, proj2, Bounds.Size))
            { e.Pointer.Capture(this); e.Handled = true; return; }
        }
        // Left-click: try to pick a scene object before falling through to orbit
        if (p.IsLeftButtonPressed && !p.IsRightButtonPressed)
        {
            var (pickView, pickProj) = GetViewProj(Bounds.Size);
            var hits = PickAllSceneObjectsSorted(_last, pickView, pickProj, Bounds.Size);
            if (hits.Count > 0)
            {
                const double pixEps = 5;
                var now = DateTime.UtcNow;
                bool nearPixel = Math.Abs(_last.X - _pickCyclePixel.X) < pixEps &&
                                 Math.Abs(_last.Y - _pickCyclePixel.Y) < pixEps;
                bool sameOrder = hits.Count == _pickCycleHits.Count;
                if (sameOrder)
                {
                    for (int i = 0; i < hits.Count; i++)
                    {
                        if (!ReferenceEquals(hits[i], _pickCycleHits[i])) { sameOrder = false; break; }
                    }
                }
                bool quickRepeat = (now - _pickCycleUtc).TotalMilliseconds < 700;
                GameObject picked;
                if (nearPixel && sameOrder && quickRepeat && _pickCycleHits.Count > 1)
                {
                    _pickCycleIndex = (_pickCycleIndex + 1) % _pickCycleHits.Count;
                    picked = _pickCycleHits[_pickCycleIndex];
                }
                else
                {
                    _pickCycleHits = new List<GameObject>(hits);
                    _pickCycleIndex = 0;
                    _pickCyclePixel = _last;
                    picked = hits[0];
                }
                _pickCycleUtc = now;

                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    SelectionService.Toggle(picked);
                else
                    SelectionService.Set(picked);
                _selected = SelectionService.Current;
                RequestNextFrameRendering();
                e.Handled = true;
                return;
            }
        }

        bool altPan = p.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool leftOrbit = Tool == ToolMode.Hand &&
                         p.IsLeftButtonPressed &&
                         !p.IsRightButtonPressed &&
                         !p.IsMiddleButtonPressed &&
                         !altPan;
        _orbiting = p.IsRightButtonPressed || leftOrbit;
        _panning = p.IsMiddleButtonPressed || altPan;

        if (_orbiting || _panning)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Pick the nearest scene object at the given screen position using bounding sphere tests.
    /// Returns null if nothing was hit.
    /// </summary>
    GameObject? PickSceneObject(Point screenPos, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var list = PickAllSceneObjectsSorted(screenPos, view, proj, sz);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>All ray hits sorted by distance (near to far). Used for selection cycling.</summary>
    List<GameObject> PickAllSceneObjectsSorted(Point screenPos, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        Picking.BuildPickRay(screenPos, view, proj, sz, out var ro, out var rd);
        var hits = new List<(GameObject go, float t)>();
        foreach (var root in SceneService.Root)
            PickCollectRecursive(root, SN.Matrix4x4.Identity, ro, rd, hits);
        hits.Sort((a, b) => a.t.CompareTo(b.t));
        var seen = new HashSet<GameObject>();
        var ordered = new List<GameObject>();
        foreach (var (go, _) in hits)
        {
            if (seen.Add(go))
                ordered.Add(go);
        }
        return ordered;
    }

    void PickCollectRecursive(GameObject go, SN.Matrix4x4 parentWorld, SN.Vector3 ro, SN.Vector3 rd,
        List<(GameObject go, float t)> hits)
    {
        var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;
        bool hasMesh = false;
        foreach (var b in go.Behaviors)
        {
            if (b is MeshFilter mf && mf.Mesh != null)
            {
                hasMesh = true;
                var mesh = mf.Mesh;
                var verts = mesh.Vertices;
                if (verts != null && verts.Length > 0)
                {
                    SN.Vector3 center = SN.Vector3.Zero;
                    for (int i = 0; i < verts.Length; i++)
                        center += verts[i];
                    center /= verts.Length;
                    float r2 = 0f;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var d = verts[i] - center;
                        float d2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                        if (d2 > r2) r2 = d2;
                    }
                    float radius = MathF.Sqrt(r2);
                    var worldCenter = SN.Vector3.Transform(center, world);
                    float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                    float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                    float sz2 = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                    float worldRadius = radius * MathF.Max(sx, MathF.Max(sy, sz2));
                    float t = Picking.RayIntersectSphere(ro, rd, worldCenter, worldRadius);
                    if (t < float.MaxValue && t > 1e-4f)
                        hits.Add((go, t));
                }
                break;
            }
        }
        if (!hasMesh)
        {
            var worldPos = new SN.Vector3(world.M41, world.M42, world.M43);
            const float defaultPickRadius = 0.5f;
            float t = Picking.RayIntersectSphere(ro, rd, worldPos, defaultPickRadius);
            if (t < float.MaxValue && t > 1e-4f)
                hits.Add((go, t));
        }
        foreach (var child in go.Children)
            PickCollectRecursive(child, world, ro, rd, hits);
    }

    void PickRecursive(GameObject go, SN.Matrix4x4 parentWorld, SN.Vector3 ro, SN.Vector3 rd,
        ref GameObject? closest, ref float closestDist)
    {
        var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

        bool hasMesh = false;

        // Check MeshFilter for bounds
        foreach (var b in go.Behaviors)
        {
            if (b is MeshFilter mf && mf.Mesh != null)
            {
                hasMesh = true;
                var mesh = mf.Mesh;
                var verts = mesh.Vertices;
                if (verts != null && verts.Length > 0)
                {
                    // Compute bounding sphere in local space
                    SN.Vector3 center = SN.Vector3.Zero;
                    for (int i = 0; i < verts.Length; i++)
                        center += verts[i];
                    center /= verts.Length;

                    float r2 = 0f;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var d = verts[i] - center;
                        float d2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                        if (d2 > r2) r2 = d2;
                    }
                    float radius = MathF.Sqrt(r2);

                    // Transform center to world space
                    var worldCenter = SN.Vector3.Transform(center, world);
                    // Scale radius by max scale axis
                    float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                    float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                    float sz2 = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                    float worldRadius = radius * MathF.Max(sx, MathF.Max(sy, sz2));

                    float t = Picking.RayIntersectSphere(ro, rd, worldCenter, worldRadius);
                    if (t < closestDist)
                    {
                        closestDist = t;
                        closest = go;
                    }
                }
                break; // only test first mesh per GO
            }
        }

        // Fallback: if no mesh, use a small sphere at the object's world position
        // so lights, cameras, and empty objects are still pickable
        if (!hasMesh)
        {
            var worldPos = new SN.Vector3(world.M41, world.M42, world.M43);
            const float defaultPickRadius = 0.5f;
            float t = Picking.RayIntersectSphere(ro, rd, worldPos, defaultPickRadius);
            if (t < closestDist)
            {
                closestDist = t;
                closest = go;
            }
        }

        foreach (var child in go.Children)
            PickRecursive(child, world, ro, rd, ref closest, ref closestDist);
    }

    void OnPointerMoved(object? s, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        UpdateTerrainHover(pos);
        if (_playModePlanetPaint && _hasPlanetHover)
        {
            PlanetTool.ApplyStrokeAt(_hoverPlanetPointW, !_playModePlanetBuild, _playModePlanetBuild);
            RequestNextFrameRendering();
            e.Handled = true;
            return;
        }
        if (_paintingPlanet && _planetPaintTarget != null && _hasPlanetHover && ReferenceEquals(_hoverPlanet, _planetPaintTarget))
        { ApplyPlanetToolUnified(_planetPaintTarget, _hoverPlanetPointW, _planetPaintToolIndex, _planetPaintSign); RequestNextFrameRendering(); e.Handled = true; return; }
        if (_paintingTerrain && _paintTarget != null && _hasHover && ReferenceEquals(_hoverTerrain, _paintTarget))
        { ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign); RequestNextFrameRendering(); e.Handled = true; return; }
        if (_isDragging && _dragAxis != Axis.None && _selected != null)
        {
            var (view2, proj2) = GetViewProj(Bounds.Size);
            bool axisOnly = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            UpdateAxisDrag(pos, view2, proj2, Bounds.Size, axisOnly); e.Handled = true; return;
        }
        var d = pos - _last; _last = pos;
        if (_orbiting)
        {
            const float orbitSensitivity = 0.008f;
            _yaw += (float)d.X * orbitSensitivity;
            _pitch -= (float)d.Y * orbitSensitivity;
            _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
            RequestNextFrameRendering();
        }
        else if (_panning)
        {
            var (view2, _) = GetViewProj(Bounds.Size);
            var right = SN.Vector3.Normalize(new SN.Vector3(view2.M11, view2.M21, view2.M31));
            var up = SN.Vector3.Normalize(new SN.Vector3(view2.M12, view2.M22, view2.M32));
            float panSpeed = Math.Clamp(_distance * 0.0035f, 0.005f, 1.25f);
            _target += (-right * (float)d.X + up * (float)d.Y) * panSpeed;
            RequestNextFrameRendering();
        }
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (_playModePlanetPaint)
        {
            _playModePlanetPaint = false;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_paintingPlanet)
        {
            _paintingPlanet = false;
            if (_planetStrokeDirty && _planetPaintTarget != null)
            {
                _planetPaintTarget.SaveVoxelEdits();
                _planetStrokeDirty = false;
                SceneService.NotifyChanged();
            }
            _planetPaintTarget = null;
            _planetPaintHasLastHit = false;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null); e.Handled = true; return;
        }
        if (_paintingTerrain)
        {
            _paintingTerrain = false;
            if (_terrStrokeDirty && _paintTarget != null)
            {
                // Finalize: rebuild collision mesh + notify scene (expensive, but only once per stroke)
                _paintTarget.FinalizeStroke();
                // Auto-save terrain data to .terrain.json so it stays in sync with scene
                _paintTarget.Save();
                _terrStrokeDirty = false;
                SceneService.NotifyChanged();
            }
            _paintTarget = null;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null); e.Handled = true; return;
        }
        _orbiting = _panning = false;
        if (_isDragging)
        {
            _isDragging = false;
            if (_selected != null && _dragAxis != Axis.None)
            {
                var t = _selected.Transform;
                var cmd = new SetTransformPositionCmd(t, _dragObjStartLocal, t.Position);
                UndoService.Exec(cmd);
            }
            _dragAxis = Axis.None; SceneService.NotifyChanged();
        }
        if (e.Pointer.Captured == this) e.Pointer.Capture(null); e.Handled = true;
    }

    void OnWheel(object? s, PointerWheelEventArgs e)
    {
        float wheelStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1.03f : 1.08f;
        _distance *= (float)Math.Pow(wheelStep, -e.Delta.Y);
        _distance = Math.Clamp(_distance, 1.5f, 200f);
        UpdateTerrainHover(_last); RequestNextFrameRendering();
    }
    #endregion

    #region Gizmo hit/drag
    bool BeginAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var axis = _axisLock != Axis.None ? _axisLock : HitTestTranslateGizmo(mouse, view, proj, sz);
        if (axis == Axis.None) return false;
        _dragAxis = axis; _isDragging = true;
        var W = SceneGraphUtil.AccumulateWorld(_selected!);
        _dragStartScale = _selected!.Transform.Scale;
        _dragStartRotation = _selected!.Transform.Rotation;
        _dragAnchorW = SN.Vector3.Transform(SN.Vector3.Zero, W);
        _dragObjStartW = _dragAnchorW;
        _dragObjStartLocal = _selected!.Transform.Position;
        _dragAxisW = axis switch
        {
            Axis.X => new SN.Vector3(W.M11, W.M21, W.M31),
            Axis.Y => new SN.Vector3(W.M12, W.M22, W.M32),
            Axis.Z => new SN.Vector3(W.M13, W.M23, W.M33),
            _ => SN.Vector3.UnitX
        };
        if (_dragAxisW.LengthSquared() < 1e-8f) _dragAxisW = axis == Axis.X ? SN.Vector3.UnitX : axis == Axis.Y ? SN.Vector3.UnitY : SN.Vector3.UnitZ;
        _dragAxisW = SN.Vector3.Normalize(_dragAxisW);
        var camFwd = new SN.Vector3(view.M13, view.M23, view.M33);
        var tmp = SN.Vector3.Cross(camFwd, _dragAxisW);
        var n = SN.Vector3.Cross(_dragAxisW, tmp);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitY);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitX);
        _dragPlaneN = SN.Vector3.Normalize(n);
        Picking.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (Picking.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hit)) _dragAnchorW = hit;
        RequestNextFrameRendering(); return true;
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;
        Picking.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!Picking.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;
        float delta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
        if (SnapEnabled && SnapStep > 1e-6f) delta = MathF.Round(delta / SnapStep) * SnapStep;

        // Apply transform to primary selection
        ApplyGizmoDelta(_selected, delta, axisOnly);

        // Apply same delta to all multi-selected objects
        foreach (var go in _multiSelected)
        {
            if (go == _selected) continue;
            ApplyGizmoDelta(go, delta, axisOnly);
        }

        // Keep drag interaction lightweight; commit scene-change notification on pointer release.
        RequestNextFrameRendering();
    }

    void ApplyGizmoDelta(GameObject go, float delta, bool axisOnly)
    {
        switch (Tool)
        {
            case ToolMode.Move:
                if (go == _selected)
                    SceneGraphUtil.SetPositionWorld(go, _dragObjStartW + _dragAxisW * delta);
                else
                {
                    // For multi-select: apply the same world-space delta as the primary
                    var worldDelta = _dragAxisW * delta;
                    go.Transform.Position = new CoreVec3(
                        go.Transform.Position.X + worldDelta.X,
                        go.Transform.Position.Y + worldDelta.Y,
                        go.Transform.Position.Z + worldDelta.Z);
                }
                break;
            case ToolMode.Rotate:
                float deg = delta * 90f;
                var start = go == _selected ? _dragStartRotation : go.Transform.Rotation;
                var r = new CoreVec3(start.X, start.Y, start.Z);
                if (_dragAxis == Axis.X) r.X = start.X + deg;
                else if (_dragAxis == Axis.Y) r.Y = start.Y + deg;
                else r.Z = start.Z + deg;
                go.Transform.Rotation = r;
                break;
            case ToolMode.Scale:
                float f = MathF.Pow(2f, delta * 0.25f); f = MathF.Max(0.001f, f); double F = f;
                var sc = go == _selected ? _dragStartScale : go.Transform.Scale;
                if (axisOnly)
                {
                    switch (_dragAxis)
                    {
                        case Axis.X: sc.X = Math.Max(0.001, sc.X * F); break;
                        case Axis.Y: sc.Y = Math.Max(0.001, sc.Y * F); break;
                        case Axis.Z: sc.Z = Math.Max(0.001, sc.Z * F); break;
                    }
                }
                else { sc.X = Math.Max(0.001, sc.X * F); sc.Y = Math.Max(0.001, sc.Y * F); sc.Z = Math.Max(0.001, sc.Z * F); }
                go.Transform.Scale = sc;
                break;
        }
    }
    #endregion

    #region Projection helper
    (SN.Matrix4x4 View, SN.Matrix4x4 Proj) GetViewProj(Size size)
    {
        GetCameraAxes(out var dir, out _, out var up);
        var eye = _target - dir * _distance;
        var view = SN.Matrix4x4.CreateLookAt(eye, _target, up);
        float aspect = size.Width <= 0 || size.Height <= 0 ? 1f : (float)(size.Width / size.Height);
        float farPlane = PlanetTerrain.ActivePlanets.Count > 0 ? 50000f : 1000f;
        SN.Matrix4x4 proj = Is2D
            ? SN.Matrix4x4.CreateOrthographic(12f, 12f / aspect, 0.1f, farPlane)
            : SN.Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, aspect, 0.5f, farPlane);
        return (view, proj);
    }
    #endregion

    #region Gizmo drawing & hit test

    /// <summary>
    /// Renders the Move/Rotate/Scale gizmo axes and arrowheads using GL lines + triangles.
    /// Drawn inside OnOpenGlRender so it is part of the GL composition surface (always visible).
    /// </summary>
    unsafe void RenderGizmoGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size dipSize)
    {
        if (_selected == null || _wireShader == null || _gizmoVao == 0) return;

        var W = SceneGraphUtil.AccumulateWorld(_selected);
        var pos = SN.Vector3.Transform(SN.Vector3.Zero, W);

        // Calculate world-space gizmo length for constant screen size
        if (!Core.Projection.ProjectWorldToScreen(pos, view, proj, dipSize, out var pAnchor, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(pos + SN.Vector3.UnitX, view, proj, dipSize, out var pX1, out _)) return;
        double pixelsPerUnit = Math.Max(1e-4, Dist(pX1, pAnchor));
        float L = (float)(GizmoScreenLen / pixelsPerUnit);
        if (L < 1e-6f) return;

        float baseOff = 0.80f * L;   // where the arrow cone base starts
        float tipW    = 0.045f * L;  // arrow cone half-width
        float px = pos.X, py = pos.Y, pz = pos.Z;

        // 24 vertices: 6 for 3 line segments, 18 for 3 arrowheads (2 tris each)
        float* verts = stackalloc float[72]
        {
            // === Lines (indices 0-5, draw as PrimitiveType.Lines) ===
            px, py, pz,   px + baseOff, py, pz,         // X line  [0-1]
            px, py, pz,   px, py + baseOff, pz,         // Y line  [2-3]
            px, py, pz,   px, py, pz + baseOff,         // Z line  [4-5]

            // === X arrowhead (indices 6-11, draw as PrimitiveType.Triangles) ===
            px + baseOff, py + tipW, pz,       px + baseOff, py - tipW, pz,       px + L, py, pz,     // tri XY
            px + baseOff, py, pz + tipW,       px + baseOff, py, pz - tipW,       px + L, py, pz,     // tri XZ

            // === Y arrowhead (indices 12-17, draw as PrimitiveType.Triangles) ===
            px + tipW, py + baseOff, pz,       px - tipW, py + baseOff, pz,       px, py + L, pz,     // tri YX
            px, py + baseOff, pz + tipW,       px, py + baseOff, pz - tipW,       px, py + L, pz,     // tri YZ

            // === Z arrowhead (indices 18-23, draw as PrimitiveType.Triangles) ===
            px + tipW, py, pz + baseOff,       px - tipW, py, pz + baseOff,       px, py, pz + L,     // tri ZX
            px, py + tipW, pz + baseOff,       px, py - tipW, pz + baseOff,       px, py, pz + L,     // tri ZY
        };

        // Upload
        g.BindVertexArray(_gizmoVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _gizmoVbo);
        g.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(72 * sizeof(float)), verts);

        // Set up MVP (vertices are in world space, so MVP = view * proj)
        var mvp = view * proj;
        _wireShader.Use();
        _wireShader.SetMatrix4("uMVP", mvp);

        // Gizmo always on top, no depth test, no face culling
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);

        // --- X axis (Red) ---
        float xr = 1f, xg = 0.2f, xb = 0.2f;
        if (_gizmoHot == Axis.X) { xr = 1f; xg = 1f; xb = 0.3f; } // highlight yellow
        _wireShader.SetVector4("uColor", xr, xg, xb, 1f);
        g.LineWidth(_gizmoHot == Axis.X ? 5f : 3f);
        g.DrawArrays(PrimitiveType.Lines, 0, 2);
        _wireShader.SetVector4("uColor", xr, xg, xb, 1f);
        g.DrawArrays(PrimitiveType.Triangles, 6, 6);

        // --- Y axis (Green) ---
        float yr = 0.2f, yg = 1f, yb = 0.2f;
        if (_gizmoHot == Axis.Y) { yr = 1f; yg = 1f; yb = 0.3f; }
        _wireShader.SetVector4("uColor", yr, yg, yb, 1f);
        g.LineWidth(_gizmoHot == Axis.Y ? 5f : 3f);
        g.DrawArrays(PrimitiveType.Lines, 2, 2);
        _wireShader.SetVector4("uColor", yr, yg, yb, 1f);
        g.DrawArrays(PrimitiveType.Triangles, 12, 6);

        // --- Z axis (Blue) ---
        float zr = 0.2f, zg = 0.6f, zb = 1f;
        if (_gizmoHot == Axis.Z) { zr = 1f; zg = 1f; zb = 0.3f; }
        _wireShader.SetVector4("uColor", zr, zg, zb, 1f);
        g.LineWidth(_gizmoHot == Axis.Z ? 5f : 3f);
        g.DrawArrays(PrimitiveType.Lines, 4, 2);
        _wireShader.SetVector4("uColor", zr, zg, zb, 1f);
        g.DrawArrays(PrimitiveType.Triangles, 18, 6);

        g.BindVertexArray(0);
    }

    /// <summary>
    /// Renders collider gizmos (box, capsule, mesh wireframes) using GL lines.
    /// Drawn inside OnOpenGlRender so they are part of the GL surface and always visible.
    /// </summary>
    unsafe void RenderColliderGizmosGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj)
    {
        if (!GizmoLocal || _wireShader == null || _colliderVao == 0) return;

        // Clear per-frame buffers
        _colLinesNormal.Clear();
        _colLinesTriggerNeutral.Clear();
        _colLinesTriggerDamage.Clear();
        _colLinesTriggerCheckpoint.Clear();
        _colLinesFaintN.Clear();
        _colLinesFaintNeutral.Clear();
        _colLinesFaintDamage.Clear();
        _colLinesFaintCheckpoint.Clear();

        // Gather all collider line segments from the scene hierarchy
        foreach (var go in SceneService.Root)
            GatherColliderLinesRecursive(go);

        bool hasAny = _colLinesNormal.Count > 0 || _colLinesTriggerNeutral.Count > 0 ||
                      _colLinesTriggerDamage.Count > 0 || _colLinesTriggerCheckpoint.Count > 0 ||
                      _colLinesFaintN.Count > 0 || _colLinesFaintNeutral.Count > 0 ||
                      _colLinesFaintDamage.Count > 0 || _colLinesFaintCheckpoint.Count > 0;
        if (!hasAny) return;

        // Set up shared GL state
        var mvp = view * proj;
        _wireShader.Use();
        _wireShader.SetMatrix4("uMVP", mvp);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        g.BindVertexArray(_colliderVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _colliderVbo);

        // DeepSkyBlue (#00BFFF) - regular colliders
        g.LineWidth(1.5f);
        DrawColliderLineBatch(g, _colLinesNormal, 0f, 191f / 255f, 1f, 1f);
        // Trigger volumes: neutral / damage / checkpoint (semi-transparent)
        DrawColliderLineBatch(g, _colLinesTriggerNeutral, 0.15f, 0.55f, 1f, 0.55f);
        DrawColliderLineBatch(g, _colLinesTriggerDamage, 1f, 0.25f, 0.2f, 0.65f);
        DrawColliderLineBatch(g, _colLinesTriggerCheckpoint, 0.2f, 1f, 0.35f, 0.65f);
        // Faint AABB overlays for mesh colliders
        DrawColliderLineBatch(g, _colLinesFaintN, 0f, 191f / 255f, 1f, 0.25f);
        DrawColliderLineBatch(g, _colLinesFaintNeutral, 0.15f, 0.55f, 1f, 0.22f);
        DrawColliderLineBatch(g, _colLinesFaintDamage, 1f, 0.25f, 0.2f, 0.22f);
        DrawColliderLineBatch(g, _colLinesFaintCheckpoint, 0.2f, 1f, 0.35f, 0.22f);

        g.BindVertexArray(0);
        g.Disable(EnableCap.Blend);
    }

    unsafe void RenderSelectionOutlineGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj)
    {
        if (!ShowSelectionOutline || _wireShader == null || _colliderVao == 0) return;

        var selectedObjects = _multiSelected.Count > 0 ? _multiSelected : (_selected != null ? new List<GameObject> { _selected } : new List<GameObject>());
        if (selectedObjects.Count == 0) return;

        var mvp = view * proj;
        _wireShader.Use();
        _wireShader.SetMatrix4("uMVP", mvp);

        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(_colliderVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _colliderVbo);
        g.LineWidth(2f);
        _wireShader.SetVector4("uColor", 1f, 0.85f, 0.2f, 1f);

        foreach (var go in selectedObjects)
        {
            var (min, max) = SceneGraphUtil.ComputeWorldAABB(go);
            DrawAabbLines(g, min, max);
        }

        g.BindVertexArray(0);
    }

    unsafe void DrawAabbLines(GL g, SN.Vector3 min, SN.Vector3 max)
    {
        float* verts = stackalloc float[72]
        {
            min.X,min.Y,min.Z,  max.X,min.Y,min.Z,
            max.X,min.Y,min.Z,  max.X,max.Y,min.Z,
            max.X,max.Y,min.Z,  min.X,max.Y,min.Z,
            min.X,max.Y,min.Z,  min.X,min.Y,min.Z,

            min.X,min.Y,max.Z,  max.X,min.Y,max.Z,
            max.X,min.Y,max.Z,  max.X,max.Y,max.Z,
            max.X,max.Y,max.Z,  min.X,max.Y,max.Z,
            min.X,max.Y,max.Z,  min.X,min.Y,max.Z,

            min.X,min.Y,min.Z,  min.X,min.Y,max.Z,
            max.X,min.Y,min.Z,  max.X,min.Y,max.Z,
            max.X,max.Y,min.Z,  max.X,max.Y,max.Z,
            min.X,max.Y,min.Z,  min.X,max.Y,max.Z
        };

        g.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(72 * sizeof(float)), verts);
        g.DrawArrays(PrimitiveType.Lines, 0, 24);
    }

    /// <summary>
    /// Uploads a batch of line vertices to the VBO and draws them.
    /// </summary>
    unsafe void DrawColliderLineBatch(GL g, List<float> verts, float r, float gr, float b, float a)
    {
        if (verts.Count == 0) return;

        // Grow VBO if needed
        if (verts.Count > _colliderVboCapacity)
        {
            _colliderVboCapacity = verts.Count * 2;
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_colliderVboCapacity * sizeof(float)),
                         ReadOnlySpan<byte>.Empty, BufferUsageARB.DynamicDraw);
        }

        // Upload vertex data
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts);
        fixed (float* ptr = span)
        {
            g.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(verts.Count * sizeof(float)), ptr);
        }

        _wireShader!.SetVector4("uColor", r, gr, b, a);
        g.DrawArrays(PrimitiveType.Lines, 0, (uint)(verts.Count / 3));
    }

    // Max triangles for full wireframe display — larger meshes use AABB only
    const int MaxColliderWireTris = 4000;

    void GetTriggerGizmoBuffers(GameObject go, out List<float> mainTrig, out List<float> faintTrig)
    {
        var tv = go.Behaviors.OfType<TriggerVolume>().FirstOrDefault();
        if (tv?.Preset == TriggerVolumePreset.DamageZone)
        {
            mainTrig = _colLinesTriggerDamage;
            faintTrig = _colLinesFaintDamage;
        }
        else if (tv?.Preset == TriggerVolumePreset.Checkpoint)
        {
            mainTrig = _colLinesTriggerCheckpoint;
            faintTrig = _colLinesFaintCheckpoint;
        }
        else
        {
            mainTrig = _colLinesTriggerNeutral;
            faintTrig = _colLinesFaintNeutral;
        }
    }

    /// <summary>
    /// Recursively gathers line segments for all colliders in the scene graph.
    /// </summary>
    void GatherColliderLinesRecursive(GameObject go)
    {
        foreach (var col in go.Behaviors.OfType<Collider>())
        {
            bool isTrigger = col.IsTrigger;
            List<float> mainBuf;
            List<float> faintTrigBuf;
            if (isTrigger)
                GetTriggerGizmoBuffers(go, out mainBuf, out faintTrigBuf);
            else
            {
                mainBuf = _colLinesNormal;
                faintTrigBuf = _colLinesFaintN;
            }

            if (col is PlanetCollider planetCol)
            {
                var pc = planetCol.WorldCenter;
                var pt = go.Behaviors.OfType<PlanetTerrain>().FirstOrDefault();
                if (pt != null && pt.Config != null)
                {
                    ColliderGizmos.CollectPlanetTerrain(mainBuf, pc, pt, 96);
                }
                else
                {
                    ColliderGizmos.CollectSphere(mainBuf, pc, planetCol.MaxRadius, 64);
                }
                var faintBuf = isTrigger ? faintTrigBuf : _colLinesFaintN;
                ColliderGizmos.CollectSphere(faintBuf, pc, planetCol.BaseRadius, 48);
                continue;
            }

            if (col is CapsuleCollider capCol)
            {
                var W = SceneGraphUtil.AccumulateWorld(go);
                var c = new SN.Vector3((float)capCol.Center.X, (float)capCol.Center.Y, (float)capCol.Center.Z);
                var rr = Math.Max(0.0001f, capCol.Radius);
                var hh = Math.Max(2f * rr, capCol.Height);
                var halfCyl = 0.5f * (hh - 2f * rr);
                SN.Vector3 axis = capCol.Direction switch
                {
                    CapsuleCollider.Axis.X => new SN.Vector3(1, 0, 0),
                    CapsuleCollider.Axis.Z => new SN.Vector3(0, 0, 1),
                    _ => new SN.Vector3(0, 1, 0)
                };
                ColliderGizmos.CollectCapsule(mainBuf, W, c + axis * halfCyl, c - axis * halfCyl, axis, rr, 32);
                continue;
            }

            if (col is BoxCollider boxCol)
            {
                var W = SceneGraphUtil.AccumulateWorld(go);
                var center = new SN.Vector3((float)boxCol.Center.X, (float)boxCol.Center.Y, (float)boxCol.Center.Z);
                var size = new SN.Vector3(
                    (float)Math.Max(1e-6, boxCol.Size.X),
                    (float)Math.Max(1e-6, boxCol.Size.Y),
                    (float)Math.Max(1e-6, boxCol.Size.Z));
                ColliderGizmos.CollectOBB(mainBuf, center, size, W);
                continue;
            }

            if (col is MeshCollider mc)
            {
                // Check if any mesh is too large for full wireframe
                bool tooLarge = false;
                foreach (var (mesh, _) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.TriIndices != null && mesh.TriIndices.Length / 3 > MaxColliderWireTris)
                    { tooLarge = true; break; }
                }

                if (!tooLarge)
                {
                    foreach (var (mesh, Wm) in mc.EnumerateTargetMeshesWorld())
                        ColliderGizmos.CollectMeshWire(mainBuf, mesh, Wm);
                }

                // Always show faint AABB overlay for mesh colliders
                var faintBuf = isTrigger ? faintTrigBuf : _colLinesFaintN;
                var aabb = mc.GetWorldAABB();
                ColliderGizmos.CollectAABB(faintBuf, aabb);
                continue;
            }

            // Fallback for any other collider type: draw world AABB
            {
                var aabb = col.GetWorldAABB();
                ColliderGizmos.CollectAABB(mainBuf, aabb);
            }
        }

        foreach (var child in go.Children)
            GatherColliderLinesRecursive(child);
    }

    /// <summary>
    /// Renders terrain brush gizmos (outer ring, inner falloff ring, center crosshair)
    /// using GL lines. Independent of the collider gizmo toggle — controlled by ShowTerrainGizmos.
    /// </summary>
    unsafe void RenderTerrainGizmosGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj)
    {
        bool planetBrush = _hasPlanetHover && _hoverPlanet != null && GetPlanetToolIndex(_hoverPlanet) != PlanetToolNone;
        bool terrainBrush = _hasHover && _hoverTerrain != null;
        if ((!planetBrush && !terrainBrush) || _wireShader == null || _colliderVao == 0) return;

        _terrainOuter.Clear();
        _terrainInner.Clear();
        _terrainCross.Clear();

        SN.Vector3 gizmoCenter;
        float radius, falloff, strength;
        if (planetBrush)
        {
            gizmoCenter = _hoverPlanetPointW;
            radius = PlanetBrushRadiusProvider(_hoverPlanet!);
            falloff = Clamp01(PlanetBrushFalloffProvider(_hoverPlanet!));
            strength = Clamp01(PlanetBrushStrengthProvider(_hoverPlanet!));
        }
        else
        {
            gizmoCenter = _hoverPointW;
            radius = TerrainBrushRadiusProvider(_hoverTerrain!);
            falloff = Clamp01(TerrainBrushFalloffProvider(_hoverTerrain!));
            strength = Clamp01(TerrainBrushStrengthProvider(_hoverTerrain!));
        }

        TerrainGizmos.CollectBrushWithFalloff(
            _terrainOuter, _terrainInner, _terrainCross,
            gizmoCenter, radius, falloff, 64);

        if (_terrainOuter.Count == 0 && _terrainInner.Count == 0 && _terrainCross.Count == 0) return;

        var mvp = view * proj;
        _wireShader.Use();
        _wireShader.SetMatrix4("uMVP", mvp);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        g.BindVertexArray(_colliderVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _colliderVbo);

        // Outer ring — bright white, opacity scales with strength
        byte aOuter = (byte)(80 + 160 * strength);
        g.LineWidth(2f);
        DrawColliderLineBatch(g, _terrainOuter,
            1f, 1f, 1f, aOuter / 255f);

        // Inner ring — softer
        byte aInner = (byte)Math.Max(40, aOuter / 2);
        g.LineWidth(1.5f);
        DrawColliderLineBatch(g, _terrainInner,
            1f, 1f, 1f, aInner / 255f);

        // Crosshair — white, opacity scales with strength
        float aCross = (40 + 180 * strength) / 255f;
        g.LineWidth(1f);
        DrawColliderLineBatch(g, _terrainCross,
            1f, 1f, 1f, aCross);

        g.BindVertexArray(0);
        g.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// Renders RectTransform gizmo outlines for Canvas UI elements in the scene.
    /// Shows green dashed rectangles around each RectTransform to help visualize UI layout.
    /// </summary>
    unsafe void RenderRectTransformGizmosGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj)
    {
        if (_wireShader == null || _colliderVao == 0) return;

        var canvases = Core.Component.UI.Canvas.All;
        if (canvases.Count == 0) return;

        var lines = new List<float>(256);

        foreach (var canvas in canvases)
        {
            if (!canvas.IsActiveAndEnabled) continue;

            if (canvas.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
            {
                // World-space canvases: draw outlines in 3D space
                var go = canvas.gameObject;
                if (go == null) continue;

                var tr = go.Transform;
                float worldW = canvas.WorldSizeX;
                float worldH = canvas.WorldSizeY;
                float canvasPixelsW = canvas.ReferenceResolutionX;
                float canvasPixelsH = canvas.ReferenceResolutionY;
                var canvasRect = new Core.Component.UI.RectTransform.Rect(0, 0, canvasPixelsW, canvasPixelsH);

                float scaleX = worldW / canvasPixelsW;
                float scaleY = worldH / canvasPixelsH;

                static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);
                var model = SN.Matrix4x4.CreateScale(scaleX, scaleY, 1f)
                          * SN.Matrix4x4.CreateFromYawPitchRoll(
                                Deg2Rad(tr.Rotation.Y), Deg2Rad(tr.Rotation.X), Deg2Rad(tr.Rotation.Z))
                          * SN.Matrix4x4.CreateTranslation(
                                (float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

                GatherRectTransformLines(canvas.gameObject!, canvasRect, model, lines);
            }
        }

        if (lines.Count == 0) return;

        var mvp = view * proj;
        _wireShader.Use();
        _wireShader.SetMatrix4("uMVP", mvp);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        g.BindVertexArray(_colliderVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _colliderVbo);
        g.LineWidth(1.5f);

        // Green color for UI rect gizmos
        DrawColliderLineBatch(g, lines, 0.2f, 0.9f, 0.3f, 0.8f);

        g.BindVertexArray(0);
        g.Disable(EnableCap.Blend);
    }

    void GatherRectTransformLines(GameObject go, Core.Component.UI.RectTransform.Rect canvasRect,
        SN.Matrix4x4 worldTransform, List<float> lines)
    {
        foreach (var b in go.Behaviors)
        {
            if (b is Core.Component.UI.RectTransform rt && rt.Enabled)
            {
                var rect = rt.GetWorldRect(in canvasRect);

                // Convert rect corners to world space via the world transform
                var corners = new SN.Vector2[4];
                rt.GetWorldCorners(in canvasRect, corners);

                for (int i = 0; i < 4; i++)
                {
                    int next = (i + 1) % 4;
                    // Transform 2D corners through world matrix (Z=0 plane)
                    var p0 = SN.Vector4.Transform(new SN.Vector4(corners[i].X, corners[i].Y, 0, 1), worldTransform);
                    var p1 = SN.Vector4.Transform(new SN.Vector4(corners[next].X, corners[next].Y, 0, 1), worldTransform);

                    lines.Add(p0.X); lines.Add(p0.Y); lines.Add(p0.Z);
                    lines.Add(p1.X); lines.Add(p1.Y); lines.Add(p1.Z);
                }
                break; // only one RectTransform per GameObject
            }
        }

        foreach (var child in go.Children)
            GatherRectTransformLines(child, canvasRect, worldTransform, lines);
    }

    /// <summary>
    /// Renders mesh wireframes using the GPU wireframe shader + GPUMesh.DrawWireframe().
    /// Handles both the global ShowWire toggle and per-object MeshRenderer.Wireframe.
    /// Drawn inside OnOpenGlRender so wireframes are always visible (no alpha compositing issues).
    /// </summary>
    void RenderWireframeGL(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj)
    {
        if (_wireShader == null || _cache == null) return;

        bool globalWire = ShowWire;

        _wireShader.Use();
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.LineWidth(1f);

        foreach (var go in SceneService.Root)
            RenderWireframeRecursive(g, view, proj, go, SN.Matrix4x4.Identity, globalWire);
    }

    void RenderWireframeRecursive(GL g, SN.Matrix4x4 view, SN.Matrix4x4 proj,
        GameObject go, SN.Matrix4x4 parentWorld, bool globalWire)
    {
        var world = WorldFromTransform(go.Transform) * parentWorld;

        // Pair MeshFilters with MeshRenderers in order (same pairing logic as SceneRenderer)
        var behaviors = go.Behaviors;
        int nextMR = 0;
        for (int i = 0; i < behaviors.Count; i++)
        {
            if (behaviors[i] is MeshFilter mf && mf.Enabled && mf.Mesh != null)
            {
                // Find the matching MeshRenderer
                MeshRenderer? mr = null;
                for (int j = nextMR; j < behaviors.Count; j++)
                {
                    if (behaviors[j] is MeshRenderer r && r.Enabled)
                    {
                        mr = r;
                        nextMR = j + 1;
                        break;
                    }
                }
                if (mr == null) continue;
                if (!globalWire && !mr.Wireframe) continue;

                var mesh = mf.Mesh;

                // Lazily generate line indices for meshes that don't have them
                // (imported models, terrain chunks, vegetation, etc.)
                mesh.EnsureLineIndices();

                var gpuMesh = _cache!.GetMesh(mesh);

                // If GPUMesh was cached before line indices were generated, re-upload
                if (gpuMesh.LineIndexCount <= 0 && mesh.LineIndices.Length > 0)
                    gpuMesh.Upload(mesh);

                if (gpuMesh.LineIndexCount <= 0) continue;

                var mvp = world * view * proj;
                _wireShader!.SetMatrix4("uMVP", mvp);

                var c = mr.Color;
                _wireShader.SetVector4("uColor", c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

                gpuMesh.DrawWireframe();
            }
        }

        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
            RenderWireframeRecursive(g, view, proj, children[i], world, globalWire);
    }

    void DrawTranslateGizmo(DrawingContext ctx, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return;
        var W = SceneGraphUtil.AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!Core.Projection.ProjectWorldToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;
        var endX = anchor + SN.Vector3.UnitX * (float)worldLen;
        var endY = anchor + SN.Vector3.UnitY * (float)worldLen;
        var endZ = anchor + SN.Vector3.UnitZ * (float)worldLen;
        if (!Core.Projection.ProjectWorldToScreen(endX, view, proj, sz, out var pX, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(endY, view, proj, sz, out var pY, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(endZ, view, proj, sz, out var pZ, out _)) return;
        void DrawAxis(Point a, Point b, Avalonia.Media.Color c, bool hot)
        {
            var pen = new Pen(new SolidColorBrush(c), hot ? 5 : 3);
            ctx.DrawLine(pen, a, b);
            double dx = b.X - a.X, dy = b.Y - a.Y; double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return; double nx = dx / len, ny = dy / len; double lx = -ny, ly = nx;
            var tip = b; var t1 = new Point(tip.X - nx * 10 + lx * 5, tip.Y - ny * 10 + ly * 5);
            var t2 = new Point(tip.X - nx * 10 - lx * 5, tip.Y - ny * 10 - ly * 5);
            var g = new StreamGeometry(); using (var sg = g.Open()) { sg.BeginFigure(tip, true); sg.LineTo(t1); sg.LineTo(t2); sg.EndFigure(true); }
            ctx.DrawGeometry(new SolidColorBrush(c), null, g);
        }
        DrawAxis(pAnchor, pX, Colors.Red, _gizmoHot == Axis.X);
        DrawAxis(pAnchor, pY, Colors.Lime, _gizmoHot == Axis.Y);
        DrawAxis(pAnchor, pZ, Colors.DeepSkyBlue, _gizmoHot == Axis.Z);
    }

    Axis HitTestTranslateGizmo(Point mouse, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return Axis.None;
        var W = SceneGraphUtil.AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!Core.Projection.ProjectWorldToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return Axis.None;
        if (!Core.Projection.ProjectWorldToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return Axis.None;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;
        bool TryAxis(SN.Vector3 axis, out double d)
        {
            d = double.MaxValue; var end = anchor + axis * (float)worldLen;
            if (!Core.Projection.ProjectWorldToScreen(end, view, proj, sz, out var pEnd, out _)) return false;
            d = DistToSegment(mouse, pAnchor, pEnd); return true;
        }
        Axis bestAxis = Axis.None; double best = GizmoPickPixels;
        if (TryAxis(SN.Vector3.UnitX, out var dx) && dx <= best) { best = dx; bestAxis = Axis.X; }
        if (TryAxis(SN.Vector3.UnitY, out var dy) && dy <= best) { best = dy; bestAxis = Axis.Y; }
        if (TryAxis(SN.Vector3.UnitZ, out var dz) && dz <= best) { best = dz; bestAxis = Axis.Z; }
        _gizmoHot = bestAxis; return bestAxis;
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double DistToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, apx = p.X - a.X, apy = p.Y - a.Y;
        double denom = abx * abx + aby * aby;
        double t = denom > 1e-9 ? Math.Clamp((apx * abx + apy * aby) / denom, 0.0, 1.0) : 0.0;
        double cx = a.X + abx * t, cy = a.Y + aby * t;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
    #endregion
}
