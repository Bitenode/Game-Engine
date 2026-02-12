using SN = System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Game_Engine.Core;
using Game_Engine.Core.Rendering.GPU;
using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Platform;
using System.Reflection;
using System.Diagnostics;
using Avalonia.Threading;
using static Game_Engine.Core.TransformUtil;
using Game_Engine.Core.Component;
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
    private FullscreenQuad? _fsQuad;
    private ResourceCache? _cache;


    private ShadowMapGPU? _shadow;

    // Gizmo GL resources (lines + arrowhead cones)
    private uint _gizmoVao;
    private uint _gizmoVbo;
    #endregion

    #region Camera & selection
    float _yaw = -30f * MathF.PI / 180f;
    float _pitch = -20f * MathF.PI / 180f;
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

    GameObject? _selected;

    private readonly Stopwatch _windWatch = Stopwatch.StartNew();
    private double _windPrev = 0.0;

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

    Terrain? _hoverTerrain;
    SN.Vector3 _hoverPointW;
    bool _hasHover;

    const int TerrainToolNone = -1;
    public static Func<Terrain, int>? TerrainToolIndexProvider;
    public static Func<Terrain, float> TerrainBrushRadiusProvider = _ => 8f;
    public static Func<Terrain, float> TerrainBrushStrengthProvider = _ => 0.5f;
    public static Func<Terrain, float> TerrainBrushFalloffProvider = _ => 0.5f;

    int GetTerrainToolIndex(Terrain t)
    => TerrainToolIndexProvider?.Invoke(t) ?? TerrainToolNone;

    Terrain? _terrHover;
    SN.Vector3 _terrHoverHitW;
    bool _terrPainting;
    bool _terrStrokeDirty;

    bool _paintingTerrain;
    Terrain? _paintTarget;
    float _paintSign;
    int _paintToolIndex;
    float _flattenTargetHeight;  // sampled on mouse-down for Flatten tool
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

    public bool SnapEnabled { get; set; } = false;
    public float SnapStep { get; set; } = 0.5f;

    static SceneView()
    {
        ToolProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowGridProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowWireProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowLightProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        Is2DProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        Supersample2xProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowCamerasProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowTerrainGizmosProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowShadowsProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
    }
    #endregion

    #region Translate gizmo state
    const double GizmoScreenLen = 80.0;
    const double GizmoPickPixels = 10.0;
    enum Axis { None, X, Y, Z }

    Axis _gizmoHot = Axis.None;
    Axis _dragAxis = Axis.None;
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
        _target = center;
        float fov = 60f * MathF.PI / 180f;
        float fit = radius / MathF.Tan(fov * 0.5f);
        _distance = MathF.Max(1.5f, fit * 1.15f);
        InvalidateVisual();
    }

    bool HandleFlyKeyDown(Key k)
    {
        if (k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E
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

    void StepFly()
    {
        if (_isDragging) return;
        double dt = _flyWatch.Elapsed.TotalSeconds; _flyWatch.Restart();
        if (dt <= 0) return;
        var dir = new SN.Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Sin(_yaw));
        var up = SN.Vector3.UnitY;
        var right = SN.Vector3.Normalize(SN.Vector3.Cross(dir, up));
        SN.Vector3 move = SN.Vector3.Zero;
        if (_keysDown.Contains(Key.W)) move += dir;
        if (_keysDown.Contains(Key.S)) move -= dir;
        if (_keysDown.Contains(Key.A)) move -= right;
        if (_keysDown.Contains(Key.D)) move += right;
        if (_keysDown.Contains(Key.E)) move += up;
        if (_keysDown.Contains(Key.Q)) move -= up;
        if (move.LengthSquared() < 1e-8f) return;
        move = SN.Vector3.Normalize(move);
        float distScale = Math.Clamp(_distance * 0.35f, 0.5f, 20f);
        float mul = 1f;
        if (_keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift)) mul *= _flyBoostMul;
        if (_keysDown.Contains(Key.LeftCtrl) || _keysDown.Contains(Key.RightCtrl)) mul *= _flySlowMul;
        _target += move * (_flyBaseSpeed * distScale * (float)dt * mul);
        InvalidateVisual();
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
            foreach (var b in go.Behaviors)
                if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTreeLOD(c, cam);
        }
    }

    // ────────────── Cached Scene Queries ──────────────
    void CacheSkyboxAndLight(GameObject go)
    {
        foreach (var b in go.Behaviors)
        {
            if (_cachedSkybox == null && b is Skybox sb && sb.Enabled) _cachedSkybox = sb;
            if (_cachedLight == null && b is Light lt && lt.Enabled) _cachedLight = lt;
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
        SelectionService.Changed += () => { _selected = SelectionService.Current; InvalidateVisual(); };
        SceneService.Changed += () => { _cache?.InvalidateAll(); _sceneQueryDirty = true; InvalidateVisual(); };
        AffectsRender<SceneView>(GizmoLocalProperty);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _flyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flyTimer.Tick += (_, __) => StepFly();

        // FPS display timer - updates text outside of render cycle to avoid layout cascades
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, __) =>
        {
            if (!_fpsWatch.IsRunning || _fpsWatch.ElapsedMilliseconds < 400) return;
            double fps = _fpsFrameCount / _fpsWatch.Elapsed.TotalSeconds;
            _fpsFrameCount = 0;
            _fpsWatch.Restart();
            FpsText = $"{fps:F0} FPS  GL:{_lastFrameMs:F0}ms C:{_lastCompositMs:F0}ms O:{_lastOverlayMs:F0}ms [{_lastSectionTimes}]";
        };
        _fpsTimer.Start();
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

            _fsQuad = new FullscreenQuad(g);
            _cache = new ResourceCache(g);

            // Shadow map — 1024×1024 is a good balance of quality vs. performance.
            // 4096 was far too expensive for integrated GPUs (4× the fillrate).
            _shadow = new ShadowMapGPU(g, 1024, 1024);
            DiagLog("[SceneView] Shadow map OK");

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
        }
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
            return;

        var g = _glCtx.GL;

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
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        var size = Bounds.Size;
        int pxW = Math.Max(1, (int)(size.Width * scaling));
        int pxH = Math.Max(1, (int)(size.Height * scaling));

        // Bind Avalonia's framebuffer
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)pxW, (uint)pxH);

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

        if (light is not null)
        {
            float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
            DiffuseK *= MathF.Max(0.01f, light.Intensity * lum);
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

        _tSetup = _sec.Elapsed.TotalMilliseconds; _sec.Restart();

        // --- SHADOW MAP PASS (skippable via ShowShadows toggle) ---
        SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
        GPUFramebuffer? shadowFBO = null;
        if (ShowShadows && _shadow != null && _depthShader != null)
        {
            // Sun direction: direction sunlight travels (from sun toward scene)
            var sunShineDir = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));

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

        // --- SCENE PASS (GPU draw calls) ---
        if (!ShowWire)
        {
            // sunShineDir was computed for the shadow pass; fall back to a default if not set
            var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            // Update terrain LOD per frame
            UpdateTerrainLOD(camPos);
            // Update tree LOD per frame
            UpdateTreeLOD(camPos);

            SceneRenderer.RenderGPU(g, _standardShader!, _depthShader!, _cache,
                view, proj,
                SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, camPos, sunSD,
                terrainShader: _terrainShader);
        }

        _tScene = _sec.Elapsed.TotalMilliseconds; _sec.Restart();

        // --- GIZMO PASS (GL lines + cones on top of scene) ---
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        RenderGizmoGL(g, view, proj, new Size(W, H));

        // Periodic GPU resource eviction (avoid unbounded cache growth)
        if (++_evictCounter > 300) // roughly every ~5s at 60fps
        {
            _evictCounter = 0;
            _cache.EvictOrphans();
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
    private int _evictCounter;

    #endregion

    #region 2D Overlay (gizmos, wireframes — drawn by Avalonia after GL)
    public override void Render(DrawingContext ctx)
    {
        // Material warm-up runs outside GL context to avoid blocking GPU work
        MaterialRebind.RepairScene();
        if (MaterialRebind.NeedsMoreFrames)
            Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);

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

        // Wireframe overlay (Avalonia 2D lines)
        if (ShowWire)
        {
            foreach (var root in SceneService.Root)
                DrawNodeWire(ctx, vp, size, root, SN.Matrix4x4.Identity, true);
        }

        // Collider gizmos
        if (GizmoLocal)
        {
            foreach (var go in SceneService.Root)
                DrawCollidersRecursive(ctx, vp, size, go);
        }

        // Terrain gizmos
        if (ShowTerrainGizmos && _hasHover && _hoverTerrain != null)
        {
            float radius = TerrainBrushRadiusProvider(_hoverTerrain);
            float strength = Clamp01(TerrainBrushStrengthProvider(_hoverTerrain));
            byte aOuter = (byte)(80 + 160 * strength);
            var outer = Avalonia.Media.Color.FromArgb(aOuter, 255, 255, 255);
            var inner = Avalonia.Media.Color.FromArgb((byte)Math.Max(40, aOuter / 2), 255, 255, 255);
            TerrainGizmos.DrawBrushWithFalloff(ctx, vp, size, _hoverPointW, radius,
                Clamp01(TerrainBrushFalloffProvider(_hoverTerrain)), strength, outer, inner, 64);
        }
        _lastOverlayMs = compSw.Elapsed.TotalMilliseconds;
    }

    // Max triangles for full wireframe display — larger meshes (e.g. terrain) use AABB only
    const int MaxMeshWireTris = 4000;

    void DrawCollidersRecursive(DrawingContext ctx, in SN.Matrix4x4 viewProj, Size sz, GameObject go)
    {
        foreach (var col in go.Behaviors.OfType<Collider>())
        {
            var mainColor = col.IsTrigger ? Colors.OrangeRed : Colors.DeepSkyBlue;
            if (col is CapsuleCollider capCol)
            {
                var W = TransformUtil.WorldFromTransform(go.Transform);
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
                ColliderGizmos.DrawCapsule(ctx, W, viewProj, sz, c + axis * halfCyl, c - axis * halfCyl, axis, rr, mainColor, 1f, 32);
                continue;
            }
            if (col is MeshCollider mc)
            {
                // For large meshes (terrain, etc.), ONLY draw the AABB — full wireframe
                // would draw tens of thousands of Avalonia 2D lines and destroy framerate.
                bool tooLarge = false;
                foreach (var (mesh, _) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.TriIndices != null && mesh.TriIndices.Length / 3 > MaxMeshWireTris)
                    { tooLarge = true; break; }
                }

                if (!tooLarge)
                {
                    foreach (var (mesh, Wm) in mc.EnumerateTargetMeshesWorld())
                        ColliderGizmos.DrawMeshWire(ctx, viewProj, sz, mesh, Wm, mainColor, 1f);
                }

                var aabb = mc.GetWorldAABB();
                var faint = mc.IsTrigger
                    ? Avalonia.Media.Color.FromArgb(64, Colors.OrangeRed.R, Colors.OrangeRed.G, Colors.OrangeRed.B)
                    : Avalonia.Media.Color.FromArgb(64, Colors.DeepSkyBlue.R, Colors.DeepSkyBlue.G, Colors.DeepSkyBlue.B);
                ColliderGizmos.DrawAABB(ctx, viewProj, sz, aabb, faint, 1f);
                continue;
            }
            { var aabb = col.GetWorldAABB(); ColliderGizmos.DrawAABB(ctx, viewProj, sz, aabb, mainColor, 1f); }
        }
        foreach (var child in go.Children) DrawCollidersRecursive(ctx, viewProj, sz, child);
    }
    #endregion

    #region Input: orbit/pan & gizmo drag
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && _selected != null) { FrameSelected(_selected); e.Handled = true; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { UndoService.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { UndoService.Redo(); e.Handled = true; }
        }
        if (HandleFlyKeyDown(e.Key)) { e.Handled = true; return; }
        if (e.Key == Key.C)
        {
            if (!_lookThroughCamera)
            { _lastPreviewCam = FindBestCameraForPreview(); _lookThroughCamera = _lastPreviewCam != null; }
            else { _lookThroughCamera = false; _lastPreviewCam = null; }
            InvalidateVisual(); e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (HandleFlyKeyUp(e.Key)) e.Handled = true;
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus(); _last = e.GetPosition(this);
        UpdateTerrainHover(_last);
        var props = e.GetCurrentPoint(this).Properties;
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
                InvalidateVisual();
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
        if (p.IsLeftButtonPressed || p.IsRightButtonPressed) _orbiting = true;
        if (p.IsMiddleButtonPressed) _panning = true;
        e.Pointer.Capture(this); e.Handled = true;
    }

    void OnPointerMoved(object? s, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        UpdateTerrainHover(pos);
        if (_paintingTerrain && _paintTarget != null && _hasHover && ReferenceEquals(_hoverTerrain, _paintTarget))
        { ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign); InvalidateVisual(); e.Handled = true; return; }
        if (_isDragging && _dragAxis != Axis.None && _selected != null)
        {
            var (view2, proj2) = GetViewProj(Bounds.Size);
            bool axisOnly = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            UpdateAxisDrag(pos, view2, proj2, Bounds.Size, axisOnly); e.Handled = true; return;
        }
        var d = pos - _last; _last = pos;
        if (_orbiting)
        { _yaw += (float)d.X * 0.01f; _pitch -= (float)d.Y * 0.01f; _pitch = Math.Clamp(_pitch, -1.5f, 1.5f); InvalidateVisual(); }
        else if (_panning)
        {
            var (view2, _) = GetViewProj(Bounds.Size);
            var right = SN.Vector3.Normalize(new SN.Vector3(view2.M11, view2.M21, view2.M31));
            var up = SN.Vector3.Normalize(new SN.Vector3(view2.M12, view2.M22, view2.M32));
            _target += (-right * (float)d.X + up * (float)d.Y) * 0.01f * _distance;
            InvalidateVisual();
        }
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
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
        _distance *= (float)Math.Pow(1.1, -e.Delta.Y);
        _distance = Math.Clamp(_distance, 1.5f, 200f);
        UpdateTerrainHover(_last); InvalidateVisual();
    }
    #endregion

    #region Gizmo hit/drag
    bool BeginAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var axis = HitTestTranslateGizmo(mouse, view, proj, sz);
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
        InvalidateVisual(); return true;
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;
        Picking.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!Picking.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;
        float delta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
        if (SnapEnabled && SnapStep > 1e-6f) delta = MathF.Round(delta / SnapStep) * SnapStep;
        switch (Tool)
        {
            case ToolMode.Move:
                SceneGraphUtil.SetPositionWorld(_selected, _dragObjStartW + _dragAxisW * delta);
                break;
            case ToolMode.Rotate:
                float deg = delta * 90f; var start = _dragStartRotation; var r = new CoreVec3(start.X, start.Y, start.Z);
                if (_dragAxis == Axis.X) r.X = start.X + deg; else if (_dragAxis == Axis.Y) r.Y = start.Y + deg; else r.Z = start.Z + deg;
                _selected.Transform.Rotation = r; break;
            case ToolMode.Scale:
                float f = MathF.Pow(2f, delta * 0.25f); f = MathF.Max(0.001f, f); double F = f;
                var sc = _dragStartScale;
                if (axisOnly) { switch (_dragAxis) { case Axis.X: sc.X = Math.Max(0.001, sc.X * F); break; case Axis.Y: sc.Y = Math.Max(0.001, sc.Y * F); break; case Axis.Z: sc.Z = Math.Max(0.001, sc.Z * F); break; } }
                else { sc.X = Math.Max(0.001, sc.X * F); sc.Y = Math.Max(0.001, sc.Y * F); sc.Z = Math.Max(0.001, sc.Z * F); }
                _selected!.Transform.Scale = sc; break;
        }
        SceneService.NotifyChanged(); SelectionService.Touch(); InvalidateVisual();
    }
    #endregion

    #region Projection helper
    (SN.Matrix4x4 View, SN.Matrix4x4 Proj) GetViewProj(Size size)
    {
        var dir = new SN.Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Sin(_yaw));
        var eye = _target - dir * _distance;
        var view = SN.Matrix4x4.CreateLookAt(eye, _target, SN.Vector3.UnitY);
        float aspect = size.Width <= 0 || size.Height <= 0 ? 1f : (float)(size.Width / size.Height);
        SN.Matrix4x4 proj = Is2D
            ? SN.Matrix4x4.CreateOrthographic(12f, 12f / aspect, 0.1f, 1000f)
            : SN.Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, aspect, 0.1f, 1000f);
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
        if (_selected == null || Tool == ToolMode.Hand || _wireShader == null || _gizmoVao == 0) return;

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

    #region Wireframe helper (Avalonia 2D)
    void DrawNodeWire(DrawingContext ctx, in SN.Matrix4x4 vp, Size sz, GameObject go, in SN.Matrix4x4 parentWorld, bool globalWire)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);
        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = Math.Min(filters.Count, renderers.Count);
        for (int i = 0; i < n; i++)
        {
            var mf = filters[i]; var mr = renderers[i];
            if (mf.Mesh != null && (globalWire || mr.Wireframe))
                DrawMeshWire(ctx, mf.Mesh, world, vp, sz, mr.Color, (float)mr.LineWidth);
        }
        foreach (var child in go.Children) DrawNodeWire(ctx, vp, sz, child, world, globalWire);
    }

    void DrawMeshWire(DrawingContext ctx, Mesh mesh, in SN.Matrix4x4 world, in SN.Matrix4x4 vp, Size sz, Avalonia.Media.Color color, float lineWidth)
    {
        if (mesh?.Vertices == null || mesh.TriIndices == null) return;
        var pen = new Pen(new SolidColorBrush(color), lineWidth <= 0 ? 1 : lineWidth);
        var v = mesh.Vertices; var tri = mesh.TriIndices;
        for (int i = 0; i < tri.Length; i += 3)
        {
            var p0w = SN.Vector3.Transform(v[tri[i]], world);
            var p1w = SN.Vector3.Transform(v[tri[i + 1]], world);
            var p2w = SN.Vector3.Transform(v[tri[i + 2]], world);
            if (!Core.Projection.ProjectToScreenVP(p0w, vp, sz, out var s0)) continue;
            if (!Core.Projection.ProjectToScreenVP(p1w, vp, sz, out var s1)) continue;
            if (!Core.Projection.ProjectToScreenVP(p2w, vp, sz, out var s2)) continue;
            ctx.DrawLine(pen, s0, s1); ctx.DrawLine(pen, s1, s2); ctx.DrawLine(pen, s2, s0);
        }
    }
    #endregion
}
