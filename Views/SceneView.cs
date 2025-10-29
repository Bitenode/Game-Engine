using SN = System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Game_Engine.Core;
using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Platform;
using System.Reflection;
using System.Diagnostics;
using Avalonia.Threading;
using static Game_Engine.Core.TransformUtil;
using Game_Engine.Core.Component; 


namespace Game_Engine.Views;


public class SceneView : Control
{
    #region Camera & selection
    float _yaw = -30f * MathF.PI / 180f;
    float _pitch = -20f * MathF.PI / 180f;
    float _distance = 8f;
    SN.Vector3 _target = SN.Vector3.Zero;

    bool _lookThroughCamera = false;   // toggle via UI or hotkey
    Camera? _lastPreviewCam;

    // --- Free-fly state ---
    readonly HashSet<Key> _keysDown = new();
    DispatcherTimer _flyTimer;
    readonly Stopwatch _flyWatch = new();

    
    float _flyBaseSpeed = 5f;   // units/sec at distance≈1
    float _flyBoostMul = 4f;   // Shift multiplier
    float _flySlowMul = 0.25f; // Ctrl multiplier

    Point _last;
    bool _orbiting, _panning;

    bool _logNextRender;            // log once on next render

    GameObject? _selected;

    private readonly System.Diagnostics.Stopwatch _windWatch = System.Diagnostics.Stopwatch.StartNew();
    private double _windPrev = 0.0;

    // --- Terrain hover (for brush ring) -----------------------------------------
    Terrain? _hoverTerrain;
    SN.Vector3 _hoverPointW;
    bool _hasHover;

    // ===== Terrain gizmos + painting ===========================================
    const int TerrainToolNone = -1;
    public static Func<Terrain, int>? TerrainToolIndexProvider;
    public static Func<Terrain, float> TerrainBrushRadiusProvider = _ => 8f;   // world units
    public static Func<Terrain, float> TerrainBrushStrengthProvider = _ => 0.5f; // 0..1
    public static Func<Terrain, float> TerrainBrushFalloffProvider = _ => 0.5f; // 0..1

    int GetTerrainToolIndex(Terrain t)
    => TerrainToolIndexProvider?.Invoke(t) ?? TerrainToolNone;

    Terrain? _terrHover;
    SN.Vector3 _terrHoverHitW;
    bool _terrPainting;      // is LMB down on terrain?
    bool _terrStrokeDirty;   // rebuild once per stroke

    

    // painting session state
    bool _paintingTerrain;
    Terrain? _paintTarget;
    float _paintSign;                 // +1 raise, -1 lower
    int _paintToolIndex;




    private (SN.Matrix4x4 View, SN.Matrix4x4 Proj, Camera? Cam, bool UsingComponent)
    GetActiveViewProj(Size size)
    {
        Camera? cam = null;

        if (_lookThroughCamera)
        {
            cam = _lastPreviewCam;

            // If none remembered, prefer selected camera, else main
            cam ??= SelectionService.Current?
                        .Behaviors.OfType<Camera>()
                        .FirstOrDefault(b => b.Enabled);

            cam ??= SceneQuery.FindBehaviors<Camera>()
                        .FirstOrDefault(c => c.Enabled && c.IsMain);
        }

        if (cam != null)
        {
            _lastPreviewCam = cam;
            return (cam.GetViewMatrix(), cam.GetProjectionMatrix(size), cam, true);
        }

        // Editor orbit camera
        var (v, p) = GetViewProj(size);
        _lastPreviewCam = null;
        return (v, p, null, false);
    }


    Camera? FindBestCameraForPreview()
    {
        // Prefer selected object's Camera
        if (_selected != null)
        {
            var selCam = _selected.Behaviors.OfType<Camera>()
                             .FirstOrDefault(c => c.Enabled);
            if (selCam != null) return selCam;
        }

        // Then a marked “main” camera
        var main = SceneQuery.FindBehaviors<Camera>()
                   .FirstOrDefault(c => c.Enabled && c.IsMain);
        if (main != null) return main;

        // Else any enabled camera
        return SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
    }




    #endregion

    #region Tooling (Move/Rotate/Scale)
    public enum ToolMode { Hand, Move, Rotate, Scale }

    public static readonly StyledProperty<ToolMode> ToolProperty =
        AvaloniaProperty.Register<SceneView, ToolMode>(nameof(Tool), ToolMode.Hand);


    public ToolMode Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    // Global grid / wire / light / 2D toggles
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowGrid), true);
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public static readonly StyledProperty<bool> ShowWireProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowWire), false);
    public bool ShowWire
    {
        get => GetValue(ShowWireProperty);
        set => SetValue(ShowWireProperty, value);
    }

    

    public static readonly StyledProperty<bool> ShowLightProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowLight), true);
    public bool ShowLight
    {
        get => GetValue(ShowLightProperty);
        set => SetValue(ShowLightProperty, value);
    }

    public static readonly StyledProperty<bool> Is2DProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(Is2D), false);
    public bool Is2D
    {
        get => GetValue(Is2DProperty);
        set => SetValue(Is2DProperty, value);
    }

    public static readonly StyledProperty<bool> Supersample2xProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(Supersample2x), false);

    public bool Supersample2x
    {
        get => GetValue(Supersample2xProperty);
        set => SetValue(Supersample2xProperty, value);
    }

    public static readonly StyledProperty<bool> GizmoLocalProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(GizmoLocal), true);
    public bool GizmoLocal { get => GetValue(GizmoLocalProperty); set => SetValue(GizmoLocalProperty, value); }

    public static readonly StyledProperty<bool> ShowTerrainGizmosProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(ShowTerrainGizmos), true);

    public bool ShowTerrainGizmos
    {
        get => GetValue(ShowTerrainGizmosProperty);
        set => SetValue(ShowTerrainGizmosProperty, value);
    }


    public static readonly StyledProperty<bool> ShowCamerasProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(ShowCameras), true);

    public bool ShowCameras
    {
        get => GetValue(ShowCamerasProperty);
        set => SetValue(ShowCamerasProperty, value);
    }

    // Snap
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
    const double GizmoScreenLen = 80.0; // pixels
    const double GizmoPickPixels = 10.0; // hit slop
    enum Axis { None, X, Y, Z }

    Axis _gizmoHot = Axis.None;

    // drag session
    Axis _dragAxis = Axis.None;
    bool _isDragging;

    SN.Vector3 _dragAxisW; // chosen axis in WORLD space
    SN.Vector3 _dragAnchorW; // world-space anchor at drag start
    SN.Vector3 _dragObjStartW; // object's origin world position at start
    CoreVec3 _dragObjStartLocal; // object's local position at start
    SN.Vector3 _dragPlaneN; // plane normal for screen-plane intersection
    CoreVec3 _dragStartRotation; // captured at BeginAxisDrag
    CoreVec3 _dragStartScale; // captured at BeginAxisDrag
    #endregion

    #region Constants & helpers

    void FrameSelected(GameObject go)
    {
        var (min, max) = SceneGraphUtil.ComputeWorldAABB(go);
        var center = (min + max) * 0.5f;
        float radius = (max - center).Length(); // sphere that encloses the AABB corners
        _target = center;
        // Fit to vertical FOV
        float fov = 60f * MathF.PI / 180f; // keep in sync with GetViewProj
        float fit = radius / MathF.Tan(fov * 0.5f); // distance to fit vertically
        _distance = MathF.Max(1.5f, fit * 1.15f); // a little padding
        InvalidateVisual();
    }

    bool HandleFlyKeyDown(Key k)
    {
        // movement + modifiers
        if (k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E
              or Key.LeftShift or Key.RightShift
              or Key.LeftCtrl or Key.RightCtrl)
        {
            if (_keysDown.Add(k))
            {
                if (!_flyTimer.IsEnabled)
                {
                    _flyWatch.Restart();
                    _flyTimer.Start();
                }
            }
            return true;
        }
        return false;
    }

    bool HandleFlyKeyUp(Key k)
    {
        if (_keysDown.Remove(k))
        {
            // stop when nothing is held
            if (_keysDown.Count == 0 && _flyTimer.IsEnabled)
                _flyTimer.Stop();
            return true;
        }
        return false;
    }

    void StepFly()
    {
        // don’t fight gizmo drags
        if (_isDragging) return;
        double dt = _flyWatch.Elapsed.TotalSeconds;
        _flyWatch.Restart();
        if (dt <= 0) return;
        // camera basis from yaw/pitch (same as GetViewProj)
        var dir = new SN.Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw));
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
        // speed scales a bit with zoom distance (feels nice when far/near)
        float distScale = Math.Clamp(_distance * 0.35f, 0.5f, 20f);
        float mul = 1f;
        if (_keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift)) mul *= _flyBoostMul;
        if (_keysDown.Contains(Key.LeftCtrl) || _keysDown.Contains(Key.RightCtrl)) mul *= _flySlowMul;
        float speed = _flyBaseSpeed * distScale * (float)dt * mul;
        // move the camera’s look-target; the eye tracks it via GetViewProj
        _target += move * speed;
        InvalidateVisual();
    }

    // --- Tools (Raise/Lower, Smooth, Flatten) -----------------------------------
    

    void PaintFlatten(Terrain t, SN.Vector3 hitW)
    {
        var world = TransformUtil.WorldFromTransform(t.gameObject.Transform);
        SN.Matrix4x4.Invert(world, out var invW);
        var pL = SN.Vector3.Transform(hitW, invW);

        float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
        float u = (pL.X + hx) / t.SizeX;
        float v = (pL.Z + hz) / t.SizeZ;
        int cx = (int)Math.Round(u * (t.ResX - 1));
        int cz = (int)Math.Round(v * (t.ResZ - 1));
        cx = Math.Clamp(cx, 0, t.ResX - 1);
        cz = Math.Clamp(cz, 0, t.ResZ - 1);
        float target = t.GetHeight(cx, cz); // flatten to center height

        float rWorld = TerrainBrushRadiusProvider(t);
        float fall = TerrainBrushFalloffProvider(t);
        float str = TerrainBrushStrengthProvider(t);

        float r = rWorld;
        int rx = (int)Math.Ceiling(r * (t.ResX - 1) / Math.Max(1e-6f, t.SizeX));
        int rz = (int)Math.Ceiling(r * (t.ResZ - 1) / Math.Max(1e-6f, t.SizeZ));

        for (int z = Math.Max(0, cz - rz); z <= Math.Min(t.ResZ - 1, cz + rz); z++)
        {
            float sz = -hz + (float)z / (t.ResZ - 1) * t.SizeZ;
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(t.ResX - 1, cx + rx); x++)
            {
                float sx = -hx + (float)x / (t.ResX - 1) * t.SizeX;
                float dist = (float)Math.Sqrt((sx - pL.X) * (sx - pL.X) + (sz - pL.Z) * (sz - pL.Z));
                if (dist > r) continue;

                float inner = r * Math.Max(0f, 1f - fall);
                float w = dist <= inner
                            ? 1f
                            : (r <= inner ? 0f : (r - dist) / (r - inner));

                int i = z * t.ResX + x;
                float h = t.Heights[i];
                t.Heights[i] = Clamp01(h + (target - h) * (str * Smooth01(w)));
            }
        }
    }

    void PaintSmooth(Terrain t, SN.Vector3 hitW)
    {
        var world = TransformUtil.WorldFromTransform(t.gameObject.Transform);
        SN.Matrix4x4.Invert(world, out var invW);
        var pL = SN.Vector3.Transform(hitW, invW);

        float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
        float u = (pL.X + hx) / t.SizeX;
        float v = (pL.Z + hz) / t.SizeZ;
        int cx = (int)Math.Round(u * (t.ResX - 1));
        int cz = (int)Math.Round(v * (t.ResZ - 1));

        float rWorld = TerrainBrushRadiusProvider(t);
        float fall = TerrainBrushFalloffProvider(t);
        float str = TerrainBrushStrengthProvider(t);

        float r = rWorld;
        int rx = (int)Math.Ceiling(r * (t.ResX - 1) / Math.Max(1e-6f, t.SizeX));
        int rz = (int)Math.Ceiling(r * (t.ResZ - 1) / Math.Max(1e-6f, t.SizeZ));

        // temp copy
        var copy = (float[])t.Heights.Clone();

        for (int z = Math.Max(0, cz - rz); z <= Math.Min(t.ResZ - 1, cz + rz); z++)
        {
            float sz = -hz + (float)z / (t.ResZ - 1) * t.SizeZ;
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(t.ResX - 1, cx + rx); x++)
            {
                float sx = -hx + (float)x / (t.ResX - 1) * t.SizeX;
                float dist = (float)Math.Sqrt((sx - pL.X) * (sx - pL.X) + (sz - pL.Z) * (sz - pL.Z));
                if (dist > r) continue;

                float inner = r * Math.Max(0f, 1f - fall);
                float w = dist <= inner
                            ? 1f
                            : (r <= inner ? 0f : (r - dist) / (r - inner));

                // simple 3x3 average
                float sum = 0f; int cnt = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= t.ResX || zz >= t.ResZ) continue;
                        sum += copy[zz * t.ResX + xx];
                        cnt++;
                    }
                float avg = (cnt > 0) ? (sum / cnt) : copy[z * t.ResX + x];
                int i = z * t.ResX + x;
                t.Heights[i] = Clamp01(copy[i] + (avg - copy[i]) * (str * Smooth01(w)));
            }
        }
    }

    void ApplyRaiseLowerBrush(Terrain t, SN.Vector3 centerW, float sign)
    {
        if (t == null || t.Heights == null || t.ResX <= 1 || t.ResZ <= 1) return;

        // Brush params from Inspector (with safe defaults)
        float radiusW = TerrainBrushRadiusProvider != null ? Math.Max(0.001f, TerrainBrushRadiusProvider(t)) : 5f;
        float strength = TerrainBrushStrengthProvider != null ? Math.Clamp(TerrainBrushStrengthProvider(t), 0f, 1f) : 0.5f;
        float falloff = TerrainBrushFalloffProvider != null ? Math.Clamp(TerrainBrushFalloffProvider(t), 0f, 1f) : 0.5f;

        // World->Local (terrain space)
        var W = TransformUtil.WorldFromTransform(t.gameObject!.Transform);
        if (!SN.Matrix4x4.Invert(W, out var invW)) return;

        var cL = SN.Vector3.Transform(centerW, invW);  // local center
        float hx = t.SizeX * 0.5f;
        float hz = t.SizeZ * 0.5f;

        // Account for non-uniform scale: convert world radius to local X/Z radii
        float sx = new SN.Vector3(W.M11, W.M21, W.M31).Length();
        float sz = new SN.Vector3(W.M13, W.M23, W.M33).Length();
        float rLx = radiusW / Math.Max(1e-6f, sx);
        float rLz = radiusW / Math.Max(1e-6f, sz);

        // Grid spacing in local units
        int nx = t.ResX, nz = t.ResZ;
        float dx = t.SizeX / (nx - 1);
        float dz = t.SizeZ / (nz - 1);

        // Center sample indices
        float tx = (cL.X + hx) / t.SizeX;   // 0..1
        float tz = (cL.Z + hz) / t.SizeZ;   // 0..1
        int cx = (int)Math.Round(tx * (nx - 1));
        int cz = (int)Math.Round(tz * (nz - 1));

        // Brush radius in samples (bounds)
        int rx = (int)Math.Ceiling(rLx / dx);
        int rz = (int)Math.Ceiling(rLz / dz);

        // Strength -> height delta in 0..1 units per dab/step
        // (t.HeightScale converts 0..1 to world meters when the mesh is rebuilt)
        float baseDelta01 = 0.02f * strength;  // tweak to taste
        float innerBand = Math.Max(0f, 1f - falloff); // full-strength core (normalized)

        // Iterate affected samples
        int x0 = Math.Max(0, cx - rx), x1 = Math.Min(nx - 1, cx + rx);
        int z0 = Math.Max(0, cz - rz), z1 = Math.Min(nz - 1, cz + rz);

        for (int z = z0; z <= z1; z++)
        {
            float zL = -hz + z * dz;
            for (int x = x0; x <= x1; x++)
            {
                float xL = -hx + x * dx;

                // Elliptical distance in local-space radii
                float nxr = (xL - cL.X) / Math.Max(1e-6f, rLx);
                float nzr = (zL - cL.Z) / Math.Max(1e-6f, rLz);
                float rNorm = MathF.Sqrt(nxr * nxr + nzr * nzr); // 0 at center, 1 at outer ring

                if (rNorm > 1f) continue;

                // Falloff: 1 inside inner core, then smooth to 0 towards the edge
                float w;
                if (rNorm <= innerBand) w = 1f;
                else
                {
                    float tEdge = (rNorm - innerBand) / Math.Max(1e-6f, 1f - innerBand);
                    tEdge = Math.Clamp(tEdge, 0f, 1f);
                    // smoothstep(1 - tEdge)
                    float s = tEdge * tEdge * (3f - 2f * tEdge);
                    w = 1f - s;
                }

                int idx = z * nx + x;
                float h = t.Heights[idx];
                h += sign * baseDelta01 * w;
                // clamp 0..1
                if (h < 0f) h = 0f; else if (h > 1f) h = 1f;
                t.Heights[idx] = h;
            }
        }

        // Rebuild the mesh from updated heights and notify
        t.RebuildMesh(); // also calls SceneService.NotifyChanged()
    }

    // Compute grid window and per-sample weight inside a round brush in LOCAL space.
    struct BrushWindow
    {
        public int x0, x1, z0, z1;
        public float hx, hz, dx, dz, r, inner;
    }

    BrushWindow MakeBrushWindow(Terrain t, in SN.Vector3 hitW, out SN.Vector3 hitL)
    {
        var W = TransformUtil.WorldFromTransform(t.gameObject.Transform);
        SN.Matrix4x4.Invert(W, out var invW);
        hitL = SN.Vector3.Transform(hitW, invW);

        float rWorld = TerrainBrushRadiusProvider(t);
        float fall = Clamp01(TerrainBrushFalloffProvider(t));

        // world->local scale on XZ
        float sx = new SN.Vector3(W.M11, W.M21, W.M31).Length();
        float sz = new SN.Vector3(W.M13, W.M23, W.M33).Length();
        float rLx = rWorld / Math.Max(1e-6f, sx);
        float rLz = rWorld / Math.Max(1e-6f, sz);

        float hx = t.SizeX * 0.5f, hz = t.SizeZ * 0.5f;
        int nx = t.ResX, nz = t.ResZ;
        float dx = t.SizeX / (nx - 1), dz = t.SizeZ / (nz - 1);

        int cx = (int)Math.Round(((hitL.X + hx) / t.SizeX) * (nx - 1));
        int cz = (int)Math.Round(((hitL.Z + hz) / t.SizeZ) * (nz - 1));

        int rx = (int)Math.Ceiling(rLx / dx);
        int rz = (int)Math.Ceiling(rLz / dz);

        return new BrushWindow
        {
            x0 = Math.Max(0, cx - rx),
            x1 = Math.Min(nx - 1, cx + rx),
            z0 = Math.Max(0, cz - rz),
            z1 = Math.Min(nz - 1, cz + rz),
            hx = hx,
            hz = hz,
            dx = dx,
            dz = dz,
            r = MathF.Sqrt(rLx * rLx + rLz * rLz) * 0.70710678f, // isotropic radius used for weight
            inner = Math.Max(0f, 1f - fall)                       // normalized inner band
        };
    }

    float BrushWeight(in BrushWindow bw, float xL, float zL, in SN.Vector3 cL)
    {
        // isotropic radial distance for smooth, pleasant falloff
        float rNorm = MathF.Sqrt((xL - cL.X) * (xL - cL.X) + (zL - cL.Z) * (zL - cL.Z)) / Math.Max(1e-6f, bw.r);
        if (rNorm > 1f) return 0f;
        if (rNorm <= bw.inner) return 1f;
        float t = (rNorm - bw.inner) / Math.Max(1e-6f, 1f - bw.inner);
        float s = t * t * (3f - 2f * t);
        return 1f - s;
    }

    // --- Noise (ridged-ish Perlin-lite) ------------------------------------------

    static int Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (int)(h ^ (h >> 16));
    }
    static float Val(int x, int y) => (Hash(x, y) & 1023) / 1023f; // 0..1

    static float ValueNoise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float tx = x - xi, ty = y - yi;
        // cosine/smoothstep blend
        float sx = tx * tx * (3f - 2f * tx);
        float sy = ty * ty * (3f - 2f * ty);
        float v00 = Val(xi, yi), v10 = Val(xi + 1, yi);
        float v01 = Val(xi, yi + 1), v11 = Val(xi + 1, yi + 1);
        float a = v00 + (v10 - v00) * sx;
        float b = v01 + (v11 - v01) * sx;
        return a + (b - a) * sy;
    }

    void PaintNoise(Terrain t, SN.Vector3 hitW)
    {
        var bw = MakeBrushWindow(t, hitW, out var cL);
        var H = t.Heights;
        int nx = t.ResX, nz = t.ResZ;

        float strength = Clamp01(TerrainBrushStrengthProvider(t));
        // freq tied to brush size; higher strength → more amplitude
        float freq = 0.35f / Math.Max(1e-4f, TerrainBrushRadiusProvider(t)); // cycles per local unit
        float amp = 0.06f * strength; // 0..~0.06 in height units (0..1 scale)

        for (int z = bw.z0; z <= bw.z1; z++)
        {
            float zL = -bw.hz + z * bw.dz;
            for (int x = bw.x0; x <= bw.x1; x++)
            {
                float xL = -bw.hx + x * bw.dx;
                float w = BrushWeight(bw, xL, zL, cL);
                if (w <= 0f) continue;

                // 3-octave value noise (ridged look by folding around 0.5)
                float u = (xL + 1000f) * freq;
                float v = (zL + 1000f) * freq;
                float n = 0f, a = 1f, f = 1f;
                for (int o = 0; o < 3; o++)
                {
                    float s = ValueNoise(u * f, v * f);
                    s = 1f - MathF.Abs(2f * s - 1f); // ridged fold
                    n += s * a;
                    a *= 0.5f;
                    f *= 2f;
                }
                n = Clamp01(n / 1.5f);              // normalize a bit
                int i = z * nx + x;
                H[i] = Clamp01(H[i] + (n - 0.5f) * amp * Smooth01(w));
            }
        }
    }

    // --- Sculpt (peak/pit) --------------------------------------------------------

    void PaintSculpt(Terrain t, SN.Vector3 hitW, float sign)
    {
        var bw = MakeBrushWindow(t, hitW, out var cL);
        var H = t.Heights;
        int nx = t.ResX, nz = t.ResZ;

        float strength = Clamp01(TerrainBrushStrengthProvider(t));
        // cone-like profile with tight center; sign controls up/down
        float k = 0.06f * strength; // step size in 0..1 units

        for (int z = bw.z0; z <= bw.z1; z++)
        {
            float zL = -bw.hz + z * bw.dz;
            for (int x = bw.x0; x <= bw.x1; x++)
            {
                float xL = -bw.hx + x * bw.dx;
                float w = BrushWeight(bw, xL, zL, cL);
                if (w <= 0f) continue;

                float r = MathF.Sqrt((xL - cL.X) * (xL - cL.X) + (zL - cL.Z) * (zL - cL.Z)) / Math.Max(1e-6f, bw.r);
                float profile = (1f - r);          // linear cone
                profile = profile * profile;        // tighter center
                int i = z * nx + x;
                H[i] = Clamp01(H[i] + sign * k * Smooth01(w) * profile);
            }
        }
    }

    // --- Thermal erosion (very light & stable) ------------------------------------

    void PaintErodeThermal(Terrain t, SN.Vector3 hitW)
    {
        var bw = MakeBrushWindow(t, hitW, out var cL);
        int nx = t.ResX, nz = t.ResZ;
        var H = t.Heights;
        var src = (float[])H.Clone();      // work from a snapshot
        var delta = new float[nx * nz];    // accumulate transfers

        float strength = Clamp01(TerrainBrushStrengthProvider(t));
        // talus angle in height-units (0..1). Larger → fewer slides.
        float talus = 0.01f;                       // ~1% of full height
        float rate = 0.25f * strength;            // how much to move if over talus

        for (int z = bw.z0; z <= bw.z1; z++)
        {
            float zL = -bw.hz + z * bw.dz;
            for (int x = bw.x0; x <= bw.x1; x++)
            {
                float xL = -bw.hx + x * bw.dx;
                float w = BrushWeight(bw, xL, zL, cL);
                if (w <= 0f) continue;

                int i = z * nx + x;
                float h = src[i];

                // 4-neighborhood (fast & stable)
                void TryNeighbor(int xx, int zz)
                {
                    if (xx < 0 || xx >= nx || zz < 0 || zz >= nz) return;
                    int j = zz * nx + xx;
                    float diff = h - src[j];
                    if (diff > talus)
                    {
                        float move = (diff - talus) * rate * Smooth01(w) * 0.5f;
                        delta[i] -= move;
                        delta[j] += move;
                    }
                }

                TryNeighbor(x + 1, z);
                TryNeighbor(x - 1, z);
                TryNeighbor(x, z + 1);
                TryNeighbor(x, z - 1);
            }
        }

        for (int z = bw.z0; z <= bw.z1; z++)
            for (int x = bw.x0; x <= bw.x1; x++)
            {
                int i = z * nx + x;
                H[i] = Clamp01(src[i] + delta[i]);
            }
    }

    void ApplyTerrainToolUnified(Terrain t, SN.Vector3 hitW, int toolIndex, float sign)
    {
        if (toolIndex == TerrainToolNone) return;

        switch (toolIndex)
        {
            case 0: // Raise/Lower (LMB raise, RMB/Shift lower)
                ApplyRaiseLowerBrush(t, hitW, sign);
                _terrStrokeDirty = false; // already rebuilds
                break;

            case 2: // Noise
                PaintNoise(t, hitW);
                _terrStrokeDirty = true;
                SceneService.NotifyChanged();
                break;

            case 4: // Sculpt (peak/pit with tight falloff; sign controls direction)
                PaintSculpt(t, hitW, sign);
                _terrStrokeDirty = true;
                SceneService.NotifyChanged();
                break;

            case 5: // Flatten
                PaintFlatten(t, hitW);
                _terrStrokeDirty = true;
                SceneService.NotifyChanged();
                break;

            case 6: // Erode (simple thermal erosion)
                PaintErodeThermal(t, hitW);
                _terrStrokeDirty = true;
                SceneService.NotifyChanged();
                break;

            case 8: // Smooth
                PaintSmooth(t, hitW);
                _terrStrokeDirty = true;
                SceneService.NotifyChanged();
                break;

            default:
                break;
        }
    }

    // --- Helpers for all brush tools ---------------------------------------------

    static float Smooth01(float x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3f - 2f * x);
    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);


    void UpdateTerrainHover(Point mouse)
    {
        var size = Bounds.Size;
        var (view, proj, _, _) = GetActiveViewProj(size);
        Picking.BuildPickRay(mouse, view, proj, size, out var ro, out var rd);

        _hasHover = TryFindClosestTerrainHit(ro, rd, out _hoverTerrain, out _hoverPointW);
    }

    bool TryFindClosestTerrainHit(in SN.Vector3 ro, in SN.Vector3 rd,
                              out Terrain terrain, out SN.Vector3 hitW)
    {
        // Copy 'in' params to regular locals so a local function can use them
        var roLocal = ro;
        var rdLocal = rd;

        // "best so far" accumulators (locals, not out-params)
        Terrain bestTerrain = null;
        SN.Vector3 bestHit = default(SN.Vector3);
        float bestDist = float.PositiveInfinity;

        // Walk the scene graph
        foreach (var root in SceneService.Root)
            Walk(root);

        // assign the out-params once, after traversal
        terrain = bestTerrain;
        hitW = bestHit;
        return terrain != null;

        void Walk(GameObject go)
        {
            Terrain t = null;
            MeshFilter mf = null;
            foreach (var b in go.Behaviors)
            {
                if (t == null && b is Terrain tt) t = tt;
                if (mf == null && b is MeshFilter mm) mf = mm;
                if (t != null && mf != null) break;
            }

            if (t != null && mf != null && mf.Mesh != null)
            {
                var W = TransformUtil.WorldFromTransform(go.Transform);

                // quick AABB reject in world space
                var aabb = SceneGraphUtil.ComputeWorldAABB(go);
                var bbMin = aabb.Item1;
                var bbMax = aabb.Item2;
                float _;
                if (!RayAabb(roLocal, rdLocal, bbMin, bbMax, out _))
                    goto NEXT;

                // triangle test (ray in world, mesh transformed)
                float tHit;
                SN.Vector3 hit;
                if (RaycastMesh_World(roLocal, rdLocal, mf.Mesh, W, out tHit, out hit))
                {
                    if (tHit < bestDist)
                    {
                        bestDist = tHit;
                        bestTerrain = t;
                        bestHit = hit;
                    }
                }
            }

        NEXT:
            foreach (var c in go.Children) Walk(c);
        }
    }

    // Robust slab test; returns near distance along ray if hit
    static bool RayAabb(in SN.Vector3 ro, in SN.Vector3 rd,
                        in SN.Vector3 bmin, in SN.Vector3 bmax,
                        out float tmin)
    {
        tmin = 0f;
        float tmax = float.PositiveInfinity;

        for (int i = 0; i < 3; i++)
        {
            float roi = i == 0 ? ro.X : i == 1 ? ro.Y : ro.Z;
            float rdi = i == 0 ? rd.X : i == 1 ? rd.Y : rd.Z;
            float min = i == 0 ? bmin.X : i == 1 ? bmin.Y : bmin.Z;
            float max = i == 0 ? bmax.X : i == 1 ? bmax.Y : bmax.Z;

            if (Math.Abs(rdi) < 1e-8f)
            {
                if (roi < min || roi > max) return false;
            }
            else
            {
                float ood = 1f / rdi;
                float t1 = (min - roi) * ood;
                float t2 = (max - roi) * ood;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
        }
        return true;
    }

    // Ray vs mesh triangles (mesh in LOCAL space, W gives local->world).
    // Returns distance in world units from 'ro' to the hit point.
    static bool RaycastMesh_World(in SN.Vector3 roW, in SN.Vector3 rdW, Mesh mesh, in SN.Matrix4x4 W,
                                  out float distW, out SN.Vector3 hitW)
    {
        distW = float.PositiveInfinity;
        hitW = default;

        if (!SN.Matrix4x4.Invert(W, out var invW))
            return false;

        // Transform ray to LOCAL space for cheap triangle tests
        var roL = SN.Vector3.Transform(roW, invW);
        var rdL = SN.Vector3.Normalize(SN.Vector3.TransformNormal(rdW, invW)); // ignore scale

        bool hit = false;
        var v = mesh.Vertices;
        var tri = mesh.TriIndices;

        float bestTL = float.PositiveInfinity;
        SN.Vector3 bestLocal = default;

        for (int i = 0; i < tri.Length; i += 3)
        {
            var a = v[tri[i]];
            var b = v[tri[i + 1]];
            var c = v[tri[i + 2]];

            if (RayTriangle_MollerTrumbore(roL, rdL, a, b, c, out float t))
            {
                if (t > 1e-6f && t < bestTL)
                {
                    bestTL = t;
                    bestLocal = roL + rdL * t;
                    hit = true;
                }
            }
        }

        if (!hit) return false;

        hitW = SN.Vector3.Transform(bestLocal, W);
        distW = (hitW - roW).Length();
        return true;
    }

    static bool RayTriangle_MollerTrumbore(in SN.Vector3 ro, in SN.Vector3 rd,
                                           in SN.Vector3 v0, in SN.Vector3 v1, in SN.Vector3 v2,
                                           out float t)
    {
        t = 0f;
        const float EPS = 1e-7f;

        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = SN.Vector3.Cross(rd, e2);
        float det = SN.Vector3.Dot(e1, p);
        if (det > -EPS && det < EPS) return false;
        float invDet = 1f / det;
        var tv = ro - v0;
        float u = SN.Vector3.Dot(tv, p) * invDet;
        if (u < 0 || u > 1) return false;
        var q = SN.Vector3.Cross(tv, e1);
        float v = SN.Vector3.Dot(rd, q) * invDet;
        if (v < 0 || u + v > 1) return false;
        t = SN.Vector3.Dot(e2, q) * invDet;
        return t > EPS;
    }

    #endregion


    #region Ctor & event hookup
    public SceneView()
    {
        Focusable = true;
        ClipToBounds = true;
        // current selection snapshot
        _selected = SelectionService.Current;
        // selection changes
        SelectionService.Changed += () =>
        {
            _selected = SelectionService.Current;
            _logNextRender = true; // log when selection changes
            InvalidateVisual();
        };
        // scene graph/material changes
        SceneService.Changed += () =>
        {
            _logNextRender = true; // log when material list changes, etc.
            InvalidateVisual();
        };
        // scene graph changes
        SceneService.Changed += () => InvalidateVisual();
        AffectsRender<SceneView>(GizmoLocalProperty);
        // input
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _flyTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flyTimer.Tick += (_, __) => StepFly();

        
    }
    #endregion

    #region Input: orbit/pan & gizmo drag
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && _selected != null)
        {
            FrameSelected(_selected);
            e.Handled = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Game_Engine.Core.UndoService.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { Game_Engine.Core.UndoService.Redo(); e.Handled = true; }
        }
        if (HandleFlyKeyDown(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C)
        {
            if (!_lookThroughCamera)
            {
                // Enter look-through: prefer selected camera, then main, then any
                _lastPreviewCam = FindBestCameraForPreview();
                _lookThroughCamera = _lastPreviewCam != null;
            }
            else
            {
                // Exit look-through back to editor camera
                _lookThroughCamera = false;
                _lastPreviewCam = null;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }


    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (HandleFlyKeyUp(e.Key))
            e.Handled = true;
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus();
        _last = e.GetPosition(this);
        UpdateTerrainHover(_last);

        // --- Terrain paint start --------------------------------
        var props = e.GetCurrentPoint(this).Properties;

        if (ShowTerrainGizmos && _hasHover && _hoverTerrain != null &&
            (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            int toolIndex = GetTerrainToolIndex(_hoverTerrain);
            if (toolIndex != TerrainToolNone)
            {
                _paintingTerrain = true;
                _paintTarget = _hoverTerrain;
                _paintToolIndex = toolIndex;
                _paintSign = (props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ? -1f : +1f;

                ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign);

                e.Pointer.Capture(this);
                e.Handled = true;
                return; // don't start orbit/gizmo when painting
            }
        }
        // --- Terrain paint end -----------------------------------


        var p = e.GetCurrentPoint(this).Properties;
        // Try translate gizmo first when in Move/Rotate/Scale
        if (Tool != ToolMode.Hand && _selected != null)
        {
            var (view, proj) = GetViewProj(Bounds.Size);
            if (BeginAxisDrag(_last, view, proj, Bounds.Size))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }
        // Orbit / Pan fallback
        if (p.IsLeftButtonPressed || p.IsRightButtonPressed) _orbiting = true;
        if (p.IsMiddleButtonPressed) _panning = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    void OnPointerMoved(object? s, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        // Always update the terrain hover for brush ring
        UpdateTerrainHover(e.GetPosition(this));

        if (_paintingTerrain && _paintTarget != null && _hasHover && ReferenceEquals(_hoverTerrain, _paintTarget))
        {
            ApplyTerrainToolUnified(_paintTarget, _hoverPointW, _paintToolIndex, _paintSign);
            e.Handled = true;
            return;
        }

        // Continue gizmo drag
        if (_isDragging && _dragAxis != Axis.None && _selected != null)
        {
            var (view, proj) = GetViewProj(Bounds.Size);
            bool axisOnly = e.KeyModifiers.HasFlag(KeyModifiers.Alt); // hold Alt = per-axis
            UpdateAxisDrag(pos, view, proj, Bounds.Size, axisOnly);
            e.Handled = true;
            return;
        }
        // Orbit/Pan
        var d = pos - _last;
        _last = pos;
        if (_orbiting)
        {
            _yaw += (float)d.X * 0.01f;
            _pitch -= (float)d.Y * 0.01f;
            _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
            InvalidateVisual();
        }
        else if (_panning)
        {
            var (view, _) = GetViewProj(Bounds.Size);
            var right = SN.Vector3.Normalize(new SN.Vector3(view.M11, view.M21, view.M31));
            var up = SN.Vector3.Normalize(new SN.Vector3(view.M12, view.M22, view.M32));
            float sxy = 0.01f * _distance;
            _target += (-right * (float)d.X + up * (float)d.Y) * sxy;
            InvalidateVisual();
        }
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        // stop terrain painting
        if (_paintingTerrain)
        {
            _paintingTerrain = false;

            if (_terrStrokeDirty && _paintTarget != null)
            {
                _paintTarget.RebuildMesh();
                _terrStrokeDirty = false;
                SceneService.NotifyChanged();
            }

            _paintTarget = null;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        _orbiting = _panning = false;
        if (_isDragging)
        {
            _isDragging = false;
            if (_selected != null && _dragAxis != Axis.None)
            {
                var t = _selected.Transform;
                var cmd = new SetTransformPositionCmd(t, _dragObjStartLocal, t.Position);
                Game_Engine.Core.UndoService.Exec(cmd);
            }
            _dragAxis = Axis.None;
            SceneService.NotifyChanged(); // refresh inspector bindings
        }
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        e.Handled = true;
    }

    void OnWheel(object? s, PointerWheelEventArgs e)
    {
        _distance *= (float)Math.Pow(1.1, -e.Delta.Y);
        _distance = Math.Clamp(_distance, 1.5f, 200f);
        UpdateTerrainHover(_last);
        InvalidateVisual();
    }
    #endregion

    #region Gizmo hit/drag
    bool BeginAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var axis = HitTestTranslateGizmo(mouse, view, proj, sz);
        if (axis == Axis.None) return false;
        BeginAxisDrag(mouse, axis, view, proj, sz);
        return true;
    }

    void BeginAxisDrag(Point mouse, Axis axis, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        _dragAxis = axis;
        _isDragging = true;
        // capture selected transforms at start
        var W = SceneGraphUtil.AccumulateWorld(_selected!);
        _dragStartScale = _selected!.Transform.Scale;
        _dragStartRotation = _selected!.Transform.Rotation;
        // object origin in world space
        _dragAnchorW = SN.Vector3.Transform(SN.Vector3.Zero, W);
        _dragObjStartW = _dragAnchorW;
        _dragObjStartLocal = _selected!.Transform.Position;
        // axis in world space (normalize to remove scaling)
        _dragAxisW = axis switch
        {
            Axis.X => new SN.Vector3(W.M11, W.M21, W.M31),
            Axis.Y => new SN.Vector3(W.M12, W.M22, W.M32),
            Axis.Z => new SN.Vector3(W.M13, W.M23, W.M33),
            _ => SN.Vector3.UnitX
        };
        if (_dragAxisW.LengthSquared() < 1e-8f)
            _dragAxisW = axis == Axis.X ? SN.Vector3.UnitX :
                         axis == Axis.Y ? SN.Vector3.UnitY : SN.Vector3.UnitZ;
        _dragAxisW = SN.Vector3.Normalize(_dragAxisW);
        // screen-aligned plane (stable)
        var camFwd = new SN.Vector3(view.M13, view.M23, view.M33);
        var tmp = SN.Vector3.Cross(camFwd, _dragAxisW);
        var n = SN.Vector3.Cross(_dragAxisW, tmp);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitY);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitX);
        _dragPlaneN = SN.Vector3.Normalize(n);
        // anchor point snapped onto the plane
        Picking.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (Picking.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hit))
            _dragAnchorW = hit;
        InvalidateVisual();
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;
        Picking.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!Picking.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;
        float delta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
        if (SnapEnabled && SnapStep > 1e-6f)
            delta = MathF.Round(delta / SnapStep) * SnapStep;
        switch (Tool)
        {
            case ToolMode.Move:
                {
                    var newWorld = _dragObjStartW + _dragAxisW * delta;
                    SceneGraphUtil.SetPositionWorld(_selected, newWorld);
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
            case ToolMode.Rotate:
                {
                    float deg = delta * 90f;
                    var start = _dragStartRotation; // captured at BeginAxisDrag
                    var r = new CoreVec3(start.X, start.Y, start.Z);
                    if (_dragAxis == Axis.X) r.X = start.X + deg;
                    else if (_dragAxis == Axis.Y) r.Y = start.Y + deg;
                    else r.Z = start.Z + deg;
                    _selected.Transform.Rotation = r;
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
            case ToolMode.Scale:
                {
                    // distance moved along the picked axis in world units
                    float axisDelta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
                    // sensitivity 
                    const float scaleK = 0.18f;
                    // with a mild exponential response:
                    const float sens = 0.25f;            // smaller = slower
                    float f = MathF.Pow(2f, axisDelta * sens);
                    f = MathF.Max(0.001f, f);
                    double F = f; // promote to double for CoreVec3
                    var s = _dragStartScale; // CoreVec3 captured at BeginAxisDrag
                    if (axisOnly)
                    {
                        // Alt held: scale only along the picked axis
                        switch (_dragAxis)
                        {
                            case Axis.X: s.X = Math.Max(0.001, s.X * F); break;
                            case Axis.Y: s.Y = Math.Max(0.001, s.Y * F); break;
                            case Axis.Z: s.Z = Math.Max(0.001, s.Z * F); break;
                        }
                    }
                    else
                    {
                        // Default: uniform scale on all sides
                        s.X = Math.Max(0.001, s.X * F);
                        s.Y = Math.Max(0.001, s.Y * F);
                        s.Z = Math.Max(0.001, s.Z * F);
                    }
                    _selected!.Transform.Scale = s;
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
        }
        SceneService.NotifyChanged();
        SelectionService.Touch();
        InvalidateVisual();
    }

    
    #endregion

    #region Projection helper
    (SN.Matrix4x4 View, SN.Matrix4x4 Proj) GetViewProj(Size size)
    {
        var dir = new SN.Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw));
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
    void DrawTranslateGizmo(DrawingContext ctx, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return;
        var W = SceneGraphUtil.AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!Core.Projection.ProjectWorldToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return;
        // Determine world length -> ≈pixels
        if (!Core.Projection.ProjectWorldToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;
        var endX = anchor + SN.Vector3.UnitX * (float)worldLen;
        var endY = anchor + SN.Vector3.UnitY * (float)worldLen;
        var endZ = anchor + SN.Vector3.UnitZ * (float)worldLen;
        if (!Core.Projection.ProjectWorldToScreen(endX, view, proj, sz, out var pX, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(endY, view, proj, sz, out var pY, out _)) return;
        if (!Core.Projection.ProjectWorldToScreen(endZ, view, proj, sz, out var pZ, out _)) return;
        void DrawAxis(Point a, Point b, Color c, bool hot)
        {
            var pen = new Pen(new SolidColorBrush(c), hot ? 5 : 3);
            ctx.DrawLine(pen, a, b);
            // arrow head
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            double nx = dx / len, ny = dy / len;
            double lx = -ny, ly = nx;
            var tip = b;
            var t1 = new Point(tip.X - nx * 10 + lx * 5, tip.Y - ny * 10 + ly * 5);
            var t2 = new Point(tip.X - nx * 10 - lx * 5, tip.Y - ny * 10 - ly * 5);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(tip, true);
                s.LineTo(t1);
                s.LineTo(t2);
                s.EndFigure(true);
            }
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
            d = double.MaxValue;
            var end = anchor + axis * (float)worldLen;
            if (!Core.Projection.ProjectWorldToScreen(end, view, proj, sz, out var pEnd, out _)) return false;
            d = DistToSegment(mouse, pAnchor, pEnd);
            return true;
        }
        Axis bestAxis = Axis.None;
        double best = GizmoPickPixels;
        if (TryAxis(SN.Vector3.UnitX, out var dx) && dx <= best) { best = dx; bestAxis = Axis.X; }
        if (TryAxis(SN.Vector3.UnitY, out var dy) && dy <= best) { best = dy; bestAxis = Axis.Y; }
        if (TryAxis(SN.Vector3.UnitZ, out var dz) && dz <= best) { best = dz; bestAxis = Axis.Z; }
        _gizmoHot = bestAxis;
        return bestAxis;
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static double DistToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double apx = p.X - a.X, apy = p.Y - a.Y;
        double denom = abx * abx + aby * aby;
        double t = denom > 1e-9 ? Math.Clamp((apx * abx + apy * aby) / denom, 0.0, 1.0) : 0.0;
        double cx = a.X + abx * t, cy = a.Y + aby * t;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
    #endregion



    #region Render pipeline
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var now = _windWatch.Elapsed.TotalSeconds;
        var dt = now - _windPrev;
        _windPrev = now;
        if (dt < 0) dt = 0;
        if (dt > 0.1) dt = 0.1; // clamp to avoid huge jumps on stalls
        WindSystem.Update((float)dt);

        var size = Bounds.Size;
        int W = Math.Max(1, (int)size.Width);
        int H = Math.Max(1, (int)size.Height);
        int SS = Supersample2x ? 2 : 1;
        int RW = W * SS, RH = H * SS;

        var color = new uint[RW * RH];
        var zbuf = new float[RW * RH];

        // --- Skybox (scene settings) ----------------------------------------------
        var sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
        var skyTop = sky?.Top ?? Color.Parse("#1f1f1f");
        var skyBot = sky?.Bottom ?? Color.Parse("#1f1f1f");

        Texture2D? skyTex = null;
        float skyBlend = 0f;
        if (sky != null)
        {
            var st = sky.GetType();

            var pTex = st.GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var raw = pTex?.GetValue(sky);
            var coerced = raw as Texture2D ?? TextureBridge.EnsureEngineTexture2D(raw);
            if (coerced != null)
            {
                skyTex = coerced;
                if (!ReferenceEquals(raw, coerced) && pTex?.CanWrite == true)
                    pTex.SetValue(sky, coerced);
            }

            var pBlend = st.GetProperty("TextureBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pBlend != null)
            {
                var bv = pBlend.GetValue(sky);
                skyBlend = bv is float f ? Math.Clamp(f, 0f, 1f)
                         : bv is double d ? (float)Math.Clamp(d, 0.0, 1.0)
                         : 0f;
            }
        }

        // --- Active view/proj ------------------------------------------------------
        var active = GetActiveViewProj(new Size(RW, RH));
        var view = active.View;
        var proj = active.Proj;
        var usingCam = active.UsingComponent && active.Cam is not null;

        // --- Skybox knobs (NO dependency on Light) --------------------------------
        float skyYaw = sky?.Yaw ?? 0f;
        float seamFeather = sky?.SeamFeather ?? 0.01f;
        bool keyOut = sky?.KeyOutNearBlack ?? true;
        float keyLuma = sky?.KeyLuma ?? 0.08f;

        // Independent “sun” highlight purely from sky yaw (or set to null to disable hotspot)
        SN.Vector3? sunDir = null;
        {
            var baseSun = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));
            var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, skyYaw);
            sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
            // If you prefer no hotspot: sunDir = null;
        }

        // Clear background with the sky (unaffected by scene lights)
        Sky.FillWorldUp(color, zbuf, RW, RH, view, proj,
            skyTop, skyBot, sunDir, skyTex, skyBlend,
            skyYaw, seamFeather, keyOut, keyLuma,
            zWriteNdc: 1f - 1e-6f);

        // --- Lighting (single active light; no shadows) ---------------------------
        var light = SceneQuery.FindBehaviors<Light>().FirstOrDefault();

        // WORLD-space travel direction for directional lights (rays move along this).
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
            {
                // Light rays travel along -forward in WORLD space
                L = -ForwardFrom(lt.Transform);
            }
            else if (light.Type == LightType.Point && light.gameObject is { } go)
            {
                lightIsPoint = true;
                var lw = SceneGraphUtil.AccumulateWorld(go);
                lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, lw);
                lightRange = Math.Max(0.001f, light.Range);
            }
        }

        // --- NO shadow map ---------------------------------------------------------
        ShadowMap? shadow = null;

        if (_logNextRender)
        {
            _logNextRender = false;
            DumpSelectedMaterialDebug();
            if (ShowWire)
                Debug.WriteLine("[SceneView] ShowWire is enabled — solid pass is skipped.");
        }

        if (ShowCameras)
            CameraOverlay.DrawCameraFrustums(ctx, view, proj, size, active.Cam);

        // --- Render path -----------------------------------------------------------
        if (!usingCam)
        {
            if (ShowGrid)
                Core.Grid.OverlayInfiniteGrid(view, proj, color, zbuf, RW, RH, step: 1f, majorEvery: 5);

            if (!ShowWire)
            {
                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ(root, view, proj, SN.Matrix4x4.Identity,
                        color, zbuf, RW, RH,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange, shadow);

                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, view, proj, SN.Matrix4x4.Identity,
                        color, zbuf, RW, RH,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange, shadow);
            }
        }
        else
        {
            var cam = active.Cam!;
            var (vx, vy, vw, vh) = SceneGraphUtil.ViewportPx(cam, RW, RH);

            var vColor = new uint[vw * vh];
            var vZ = new float[vw * vh];

            var vView = cam.GetViewMatrix();
            var vProj = cam.GetProjectionMatrix(new Avalonia.Size(vw, vh));

            CameraClear.ClearForCamera(cam, vColor, vZ, vw, vh,
                vView, vProj,
                skyTop, skyBot, sunDir,
                skyTex, skyBlend,
                skyYaw, seamFeather, keyOut, keyLuma);

            if (ShowGrid)
                Core.Grid.OverlayInfiniteGrid(vView, vProj, vColor, vZ, vw, vh, step: 1f, majorEvery: 5);

            if (!ShowWire)
            {
                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ(root, vView, vProj, SN.Matrix4x4.Identity,
                        vColor, vZ, vw, vh,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange, shadow);

                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                        vColor, vZ, vw, vh,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange, shadow);
            }

            ImageUtil.Blit(vColor, vw, vh, color, RW, RH, vx, vy);
            view = vView; proj = vProj;
        }

        // --- Copy to WriteableBitmap ----------------------------------------------
        var wb = new WriteableBitmap(new PixelSize(W, H), new Avalonia.Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var fb = wb.Lock())
            unsafe
            {
                byte* dst = (byte*)fb.Address;
                int rowB = fb.RowBytes;

                if (SS == 2)
                {
                    var lo = new uint[W * H];
                    ImageUtil.Downsample2x(color, RW, RH, lo, W, H);
                    fixed (uint* src = lo)
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * W, dst + y * rowB, rowB, W * 4);
                }
                else
                {
                    fixed (uint* src = color)
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * RW, dst + y * rowB, rowB, W * 4);
                }
            }

        ctx.DrawImage(wb, new Rect(0, 0, W, H));

        var vp = view * proj;
        foreach (var root in SceneService.Root)
            DrawNodeWire(ctx, vp, size, root, SN.Matrix4x4.Identity, ShowWire);

        if (GizmoLocal)
            foreach (var go in Core.SceneService.Root)
            DrawCollidersRecursive(ctx, vp, size, go);

        void DrawCollidersRecursive(DrawingContext ctx, in SN.Matrix4x4 viewProj, Size sz, GameObject go)
        {
            foreach (var col in go.Behaviors.OfType<Game_Engine.Core.Component.Collider>())
            {
                var mainColor = col.IsTrigger ? Colors.OrangeRed : Colors.DeepSkyBlue;

                // Capsule: true capsule wire
                if (col is Game_Engine.Core.Component.CapsuleCollider capCol)
                {
                    var W = TransformUtil.WorldFromTransform(go.Transform);

                    // replicate CapsuleCollider.GetLocalCapsule math
                    var c = new SN.Vector3((float)capCol.Center.X, (float)capCol.Center.Y, (float)capCol.Center.Z);
                    var rr = Math.Max(0.0001f, capCol.Radius);
                    var hh = Math.Max(2f * rr, capCol.Height);
                    var halfCyl = 0.5f * (hh - 2f * rr);

                    SN.Vector3 axis;
                    switch (capCol.Direction)
                    {
                        case Game_Engine.Core.Component.CapsuleCollider.Axis.X: axis = new SN.Vector3(1, 0, 0); break;
                        case Game_Engine.Core.Component.CapsuleCollider.Axis.Z: axis = new SN.Vector3(0, 0, 1); break;
                        default: axis = new SN.Vector3(0, 1, 0); break;
                    }

                    var a = c + axis * halfCyl; // local top center
                    var b = c - axis * halfCyl; // local bottom center

                    ColliderGizmos.DrawCapsule(ctx, W, viewProj, sz, a, b, axis, rr, mainColor, 1f, 32);
                    continue;
                }

                // exact mesh wire (each target), plus faint union AABB
                if (col is Game_Engine.Core.Component.MeshCollider mc)
                {
                    foreach (var (mesh, Wm) in mc.EnumerateTargetMeshesWorld())
                        ColliderGizmos.DrawMeshWire(ctx, viewProj, sz, mesh, Wm, mainColor, 1f);

                    // faint union AABB for quick visual bounds
                    var aabb = mc.GetWorldAABB();
                    var faint = mc.IsTrigger
                        ? Color.FromArgb(64, Colors.OrangeRed.R, Colors.OrangeRed.G, Colors.OrangeRed.B)
                        : Color.FromArgb(64, Colors.DeepSkyBlue.R, Colors.DeepSkyBlue.G, Colors.DeepSkyBlue.B);
                    ColliderGizmos.DrawAABB(ctx, viewProj, sz, aabb, faint, 1f);
                    continue;
                }

                // Fallback (BoxCollider, future shapes): draw world AABB
                {
                    var aabb = col.GetWorldAABB();
                    ColliderGizmos.DrawAABB(ctx, viewProj, sz, aabb, mainColor, 1f);
                }
            }

            // children
            foreach (var child in go.Children)
                DrawCollidersRecursive(ctx, viewProj, sz, child);
        }

        void DrawTerrainRecursive(DrawingContext ctx, in SN.Matrix4x4 viewProj, Size sz, GameObject go)
        {
            // draw a terrain gizmo for each Terrain on this node
            foreach (var t in go.Behaviors.OfType<Game_Engine.Core.Component.Terrain>())
            {
                var W = TransformUtil.WorldFromTransform(go.Transform);
                bool highlight = ReferenceEquals(_selected, go);
                Game_Engine.Core.TerrainGizmos.Draw(ctx, viewProj, sz, W, t, highlight);
            }

            // then recurse into children
            foreach (var child in go.Children)
                DrawTerrainRecursive(ctx, viewProj, sz, child);
        }

        if (ShowTerrainGizmos && _hasHover && _hoverTerrain != null)
        {
            float radius = TerrainBrushRadiusProvider != null ? TerrainBrushRadiusProvider(_hoverTerrain) : 5f;
            float strength = TerrainBrushStrengthProvider != null ? Clamp01(TerrainBrushStrengthProvider(_hoverTerrain)) : 0.5f;
            float falloff = TerrainBrushFalloffProvider != null ? Clamp01(TerrainBrushFalloffProvider(_hoverTerrain)) : 0.5f;

            // visualize: outer ring alpha follows strength; inner ring shows falloff width
            byte aOuter = (byte)(80 + 160 * strength);            // 80..240
            var outer = Color.FromArgb(aOuter, 255, 255, 255);
            var inner = Color.FromArgb((byte)Math.Max(40, aOuter / 2), 255, 255, 255);

            TerrainGizmos.DrawBrushWithFalloff(ctx, vp, size, _hoverPointW, radius, falloff, strength, outer, inner, 64);
        }

        if (ShowTerrainGizmos && _hasHover && _hoverTerrain != null &&
    (SceneView.TerrainToolIndexProvider?.Invoke(_hoverTerrain) ?? -1) >= 0)
        {
            foreach (var root in Core.SceneService.Root)
                DrawTerrainRecursive(ctx, vp, size, root);

        }

        if (GizmoLocal)
            DrawTranslateGizmo(ctx, view, proj, size);
            
    }



    void DumpSelectedMaterialDebug()
    {
        try
        {
            var go = _selected;
            if (go == null)
            {
                Debug.WriteLine("[SceneView] No selection.");
                return;
            }

            // All enabled filters/renderers on this GO
            var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int pairs = Math.Min(filters.Count, renderers.Count);

            Debug.WriteLine($"[SceneView] '{go.Name}' meshPairs={pairs} (filters={filters.Count}, renderers={renderers.Count})");
            if (pairs == 0)
            {
                Debug.WriteLine("[SceneView] No enabled MeshFilter+MeshRenderer pairs.");
                return;
            }

            // --- helpers ----------------------------------------------------------
            static System.Numerics.Vector2[]? TryGetUVs(Mesh m)
            {
                var t = m.GetType();
                string[] candidates = { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
                foreach (var n in candidates)
                {
                    var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(System.Numerics.Vector2[]))
                        return (System.Numerics.Vector2[]?)p.GetValue(m);

                    var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(System.Numerics.Vector2[]))
                        return (System.Numerics.Vector2[]?)f.GetValue(m);
                }
                return null;
            }

            static string GetTexUsage(object slot)
            {
                var prop = slot.GetType().GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = prop?.GetValue(slot);
                return v?.ToString() ?? "Albedo";
            }

            static int GetFaceMask(object slot)
            {
                var prop = slot.GetType().GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) return -1;
                var v = prop.GetValue(slot);
                if (v is int i) return i;
                if (v != null && v.GetType().IsEnum) return Convert.ToInt32(v);
                return -1;
            }

            // ----------------------------------------------------------------------

            for (int i = 0; i < pairs; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];

                var mesh = mf.Mesh;
                var verts = mesh?.Vertices?.Length ?? 0;
                var tris = (mesh?.TriIndices?.Length ?? 0) / 3;
                var uvs = mesh != null ? TryGetUVs(mesh) : null;

                Debug.WriteLine($"[SceneView]  Pair[{i}]  wire={mr.Wireframe}, castShadows={mr.CastShadows}, recvShadows={mr.ReceiveShadows}, color={mr.Color}");
                Debug.WriteLine($"[SceneView]    Mesh    : verts={verts}, tris={tris}, hasUVs={(uvs != null)}, uvLen={(uvs?.Length ?? 0)}");

                // Material (via reflection to work with non-public property)
                var matProp = mr.GetType().GetProperty("Material",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Material;

                if (mat == null)
                {
                    Debug.WriteLine($"[SceneView]    Material: <null>");
                    continue;
                }

                int texCount = mat.Textures?.Count ?? 0;
                Debug.WriteLine($"[SceneView]    Material: textures={texCount}");

                if (texCount > 0 && mat.Textures != null)
                {
                    for (int ti = 0; ti < mat.Textures.Count; ti++)
                    {
                        var slot = mat.Textures[ti];
                        var tex = slot.Texture;
                        string size = tex != null ? $"{tex.Width}x{tex.Height}" : "null";
                        string usage = GetTexUsage(slot);
                        int mask = GetFaceMask(slot);
                        string maskStr = mask == -1 ? "all" : $"0x{mask:X}";
                        string name = slot.Name ?? "(unnamed)";

                        Debug.WriteLine($"[SceneView]      [{ti}] name='{name}', usage={usage}, faceMask={maskStr}, tex={size}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SceneView] Debug dump error: " + ex);
        }
    }

   

    void DrawNodeWire(DrawingContext ctx, in SN.Matrix4x4 vp, Size sz,
                  GameObject go, in SN.Matrix4x4 parentWorld, bool globalWire)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mf.Mesh != null && (globalWire || mr.Wireframe))
                DrawMeshWire(ctx, mf.Mesh, world, vp, sz, mr.Color, (float)mr.LineWidth);
        }

        foreach (var child in go.Children)
            DrawNodeWire(ctx, vp, sz, child, world, globalWire);
    }


    void DrawMeshWire(DrawingContext ctx, Mesh mesh, in SN.Matrix4x4 world,
                      in SN.Matrix4x4 vp, Size sz, Color color, float lineWidth)
    {
        if (mesh?.Vertices == null || mesh.TriIndices == null) return;
        var pen = new Pen(new SolidColorBrush(color), lineWidth <= 0 ? 1 : lineWidth);
        var v = mesh.Vertices;
        var tri = mesh.TriIndices;
        for (int i = 0; i < tri.Length; i += 3)
        {
            var p0w = SN.Vector3.Transform(v[tri[i]], world);
            var p1w = SN.Vector3.Transform(v[tri[i + 1]], world);
            var p2w = SN.Vector3.Transform(v[tri[i + 2]], world);
            if (!Core.Projection.ProjectToScreenVP(p0w, vp, sz, out var s0)) continue;
            if (!Core.Projection.ProjectToScreenVP(p1w, vp, sz, out var s1)) continue;
            if (!Core.Projection.ProjectToScreenVP(p2w, vp, sz, out var s2)) continue;
            ctx.DrawLine(pen, s0, s1);
            ctx.DrawLine(pen, s1, s2);
            ctx.DrawLine(pen, s2, s0);
        }
    }
    #endregion


    
}