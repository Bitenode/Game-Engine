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

    // FPS tracking
    private readonly Stopwatch _fpsWatch = new Stopwatch();
    private int _fpsFrameCount;
    private string _fpsText = "0 FPS";

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

    void ApplyRaiseLowerBrush(Terrain t, SN.Vector3 centerW, float sign)
    {
        if (t == null || t.Heights == null || t.ResX <= 1 || t.ResZ <= 1) return;
        float radiusW = TerrainBrushRadiusProvider != null ? Math.Max(0.001f, TerrainBrushRadiusProvider(t)) : 5f;
        float strength = TerrainBrushStrengthProvider != null ? Math.Clamp(TerrainBrushStrengthProvider(t), 0f, 1f) : 0.5f;
        float falloff = TerrainBrushFalloffProvider != null ? Math.Clamp(TerrainBrushFalloffProvider(t), 0f, 1f) : 0.5f;
        var W = TransformUtil.WorldFromTransform(t.gameObject!.Transform);
        if (!SN.Matrix4x4.Invert(W, out var invW)) return;
        var cL = SN.Vector3.Transform(centerW, invW);
        float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
        float sx = new SN.Vector3(W.M11, W.M21, W.M31).Length();
        float sz = new SN.Vector3(W.M13, W.M23, W.M33).Length();
        float rLx = radiusW / Math.Max(1e-6f, sx), rLz = radiusW / Math.Max(1e-6f, sz);
        int nx = t.ResX, nz = t.ResZ;
        float dx = t.SizeX / (nx - 1), dz = t.SizeZ / (nz - 1);
        float tx = (cL.X + hx) / t.SizeX, tz = (cL.Z + hz) / t.SizeZ;
        int cx = (int)Math.Round(tx * (nx - 1)), cz = (int)Math.Round(tz * (nz - 1));
        int rx = (int)Math.Ceiling(rLx / dx), rz = (int)Math.Ceiling(rLz / dz);
        float baseDelta01 = 0.02f * strength;
        float innerBand = Math.Max(0f, 1f - falloff);
        for (int z = Math.Max(0, cz - rz); z <= Math.Min(nz - 1, cz + rz); z++)
        {
            float zL = -hz + z * dz;
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(nx - 1, cx + rx); x++)
            {
                float xL = -hx + x * dx;
                float nxr = (xL - cL.X) / Math.Max(1e-6f, rLx);
                float nzr = (zL - cL.Z) / Math.Max(1e-6f, rLz);
                float rNorm = MathF.Sqrt(nxr * nxr + nzr * nzr);
                if (rNorm > 1f) continue;
                float w = rNorm <= innerBand ? 1f : 1f - Math.Clamp((rNorm - innerBand) / Math.Max(1e-6f, 1f - innerBand), 0f, 1f);
                w = w * w * (3f - 2f * w); // smoothstep
                int idx = z * nx + x;
                float h = t.Heights[idx] + sign * baseDelta01 * w;
                t.Heights[idx] = Math.Clamp(h, 0f, 1f);
            }
        }
        t.RebuildMesh();
    }

    void ApplyTerrainToolUnified(Terrain t, SN.Vector3 hitW, int toolIndex, float sign)
    {
        if (toolIndex == TerrainToolNone) return;
        switch (toolIndex)
        {
            case 0: ApplyRaiseLowerBrush(t, hitW, sign); break;
            default: break;
        }
    }

    static float Smooth01(float x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3f - 2f * x);
    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

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
                for (int i = 0; i < mf.Mesh.TriIndices.Length; i += 3)
                {
                    var v = mf.Mesh.Vertices; var tri = mf.Mesh.TriIndices;
                    if (RayTriMT(rL, dL, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]], out float tH) && tH > 1e-6f && tH < bestD)
                    {
                        bestD = tH; bestT = t;
                        bestH = SN.Vector3.Transform(rL + dL * tH, W);
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
        SceneService.Changed += () => { _cache?.InvalidateAll(); InvalidateVisual(); };
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
            FpsText = $"{fps:F0} FPS";
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

            _fsQuad = new FullscreenQuad(g);
            _cache = new ResourceCache(g);

            // Shadow map (4096×4096 depth texture for sharp window shadows)
            _shadow = new ShadowMapGPU(g, 4096, 4096);
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

        // Warm-up pass: ensure every MeshRenderer has its Material textures loaded.
        // This mirrors what GameView already does and fixes the "second layer has no
        // material until you open the inspector" issue.
        MaterialRebind.RepairScene();
        // If there are more probing frames, schedule another render so hysteresis completes.
        if (MaterialRebind.NeedsMoreFrames)
            Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);

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

        // Skybox settings
        var sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
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
        var light = SceneQuery.FindBehaviors<Light>().FirstOrDefault();
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

        // --- SHADOW MAP PASS (always uses sun/sky direction for global shadows) ---
        SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
        GPUFramebuffer? shadowFBO = null;
        if (_shadow != null && _depthShader != null)
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

        // --- SCENE PASS (GPU draw calls) ---
        if (!ShowWire)
        {
            // sunShineDir was computed for the shadow pass; fall back to a default if not set
            var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            SceneRenderer.RenderGPU(g, _standardShader!, _depthShader!, _cache,
                view, proj,
                SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, camPos, sunSD);
        }

        // --- GIZMO PASS (GL lines + cones on top of scene) ---
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        RenderGizmoGL(g, view, proj, new Size(W, H));

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

    #endregion

    #region 2D Overlay (gizmos, wireframes — drawn by Avalonia after GL)
    public override void Render(DrawingContext ctx)
    {
        // This calls OnOpenGlRender internally, then draws the result
        base.Render(ctx);

        // Now draw 2D overlays on top
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

        // Translate/Rotate/Scale gizmo — now rendered in GL (RenderGizmoGL)
        // so it is part of the composition surface and always visible.
        // The 2D DrawTranslateGizmo is kept for reference but no longer called;
        // axis hit-testing still uses HitTestTranslateGizmo (unchanged).
    }

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
                foreach (var (mesh, Wm) in mc.EnumerateTargetMeshesWorld())
                    ColliderGizmos.DrawMeshWire(ctx, viewProj, sz, mesh, Wm, mainColor, 1f);
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
                ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign);
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
        { ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign); e.Handled = true; return; }
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
            if (_terrStrokeDirty && _paintTarget != null) { _paintTarget.RebuildMesh(); _terrStrokeDirty = false; SceneService.NotifyChanged(); }
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
