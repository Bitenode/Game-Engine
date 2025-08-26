using System;
using System.Collections.Generic;
using System.Linq;
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
using System.Numerics;
using System.Diagnostics;

namespace Game_Engine.Views;

// Lightweight orbit/pan/zoom scene view rendered with Avalonia (software).
public class SceneView : Control
{
    #region Camera & selection
    float _yaw = -30f * MathF.PI / 180f;
    float _pitch = -20f * MathF.PI / 180f;
    float _distance = 8f;
    SN.Vector3 _target = SN.Vector3.Zero;

    Point _last;
    bool _orbiting, _panning;

    bool _logNextRender;            // log once on next render

    GameObject? _selected;

    // Reuse uv-sphere meshes by (lon,lat) so we don't allocate every frame.
    readonly Dictionary<(int lon, int lat), Mesh> _uvSphereCache = new();

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
    }
    #endregion

    #region Translate gizmo state
    const double GizmoScreenLen = 80.0; // pixels
    const double GizmoPickPixels = 10.0; // hit slop
    enum Axis { None, X, Y, Z }

    Axis _gizmoHot = Axis.None;

    // drag session
    Axis _dragAxis = Axis.None;
    bool _isDragging = false;

    SN.Vector3 _dragAxisW;           // chosen axis in WORLD space
    SN.Vector3 _dragAnchorW;         // world-space anchor at drag start
    SN.Vector3 _dragObjStartW;       // object's origin world position at start
    CoreVec3 _dragObjStartLocal;   // object's local position at start
    SN.Vector3 _dragPlaneN;          // plane normal for screen-plane intersection

    CoreVec3 _dragStartRotation;     // captured at BeginAxisDrag
    CoreVec3 _dragStartScale;        // captured at BeginAxisDrag


    #endregion

    #region Constants & helpers
    const float NearEps = 0.001f;

    static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);

    static uint PackBGRA(Color c) => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));

    static Color ShadeColor(Color c, float s)
    {
        s = Math.Clamp(s, 0f, 1f);
        byte r = (byte)Math.Clamp(c.R * s, 0, 255);
        byte g = (byte)Math.Clamp(c.G * s, 0, 255);
        byte b = (byte)Math.Clamp(c.B * s, 0, 255);
        return Color.FromArgb(c.A, r, g, b);
    }

    static SN.Matrix4x4 WorldFromTransform(Core.Transform t)
    {
        var s = SN.Matrix4x4.CreateScale((float)t.Scale.X, (float)t.Scale.Y, (float)t.Scale.Z);
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(Deg2Rad(t.Rotation.Y), Deg2Rad(t.Rotation.X), Deg2Rad(t.Rotation.Z));
        var tr = SN.Matrix4x4.CreateTranslation((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z);
        return s * r * tr;
    }

    // Computes a world-space AABB for a GameObject subtree.
    // If it contains no mesh, we fallback to the object's world origin.
    (SN.Vector3 min, SN.Vector3 max) ComputeWorldAABB(GameObject root)
    {
        bool hasPoint = false;
        SN.Vector3 min = default, max = default;

        void Expand(in SN.Vector3 p)
        {
            if (!hasPoint) { min = max = p; hasPoint = true; }
            else
            {
                min = new SN.Vector3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
                max = new SN.Vector3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            }
        }

        void Walk(GameObject go, SN.Matrix4x4 parentW)
        {
            var W = parentW * WorldFromTransform(go.Transform);

            // If there's a mesh, include all its transformed vertices
            var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault();
            if (mf?.Mesh?.Vertices is { Length: > 0 } vtx)
            {
                for (int i = 0; i < vtx.Length; i++)
                    Expand(SN.Vector3.Transform(vtx[i], W));
            }
            else
            {
                // No mesh? At least include the object's origin so framing works.
                Expand(SN.Vector3.Transform(SN.Vector3.Zero, W));
            }

            foreach (var ch in go.Children)
                Walk(ch, W);
        }

        Walk(root, SN.Matrix4x4.Identity);
        return (min, max);
    }


    void FrameSelected(GameObject go)
    {
        var (min, max) = ComputeWorldAABB(go);
        var center = (min + max) * 0.5f;
        float radius = (max - center).Length(); // sphere that encloses the AABB corners

        _target = center;

        // Fit to vertical FOV
        float fov = 60f * MathF.PI / 180f;           // keep in sync with GetViewProj
        float fit = radius / MathF.Tan(fov * 0.5f);  // distance to fit vertically
        _distance = MathF.Max(1.5f, fit * 1.15f);    // a little padding

        InvalidateVisual();
    }

    

    static uint PackFromRGBA(byte r, byte g, byte b, byte a)
    {
        var c = Color.FromArgb(a, r, g, b);
        return (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));
    }

    static Color MulColor(Color a, Color b)
    {
        byte r = (byte)((a.R * b.R) / 255);
        byte g = (byte)((a.G * b.G) / 255);
        byte b2 = (byte)((a.B * b.B) / 255);
        return Color.FromArgb(255, r, g, b2);
    }

    static Color SampleNearest(Game_Engine.Core.Texture2D t, float u, float v)
    {
        if (t.Width <= 0 || t.Height <= 0) return Color.FromArgb(255, 255, 255, 255);

        // Wrap UVs (repeat)
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);

        // Flip V to match top-left origin textures (common for PNG/JPG)
        v = 1f - v;

        int x = Math.Clamp((int)MathF.Round(u * (t.Width - 1)), 0, t.Width - 1);
        int y = Math.Clamp((int)MathF.Round(v * (t.Height - 1)), 0, t.Height - 1);

        int idx = (y * t.Width + x) * 4;
        var rgba = t.Rgba;
        byte r = rgba[idx + 0];
        byte g = rgba[idx + 1];
        byte b = rgba[idx + 2];
        byte a = rgba[idx + 3];

        return Color.FromArgb(a, r, g, b);
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
            _logNextRender = true;          // log when selection changes
            InvalidateVisual();
        };

        // scene graph/material changes
        SceneService.Changed += () =>
        {
            _logNextRender = true;          // log when material list changes, etc.
            InvalidateVisual();
        };
        // scene graph changes
        SceneService.Changed += () => InvalidateVisual();

        // input
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
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
    }


    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus();
        _last = e.GetPosition(this);
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
        var W = AccumulateWorld(_selected!);
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
        BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hit))
            _dragAnchorW = hit;

        InvalidateVisual();
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;

        BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;

        float delta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
        if (SnapEnabled && SnapStep > 1e-6f)
            delta = MathF.Round(delta / SnapStep) * SnapStep;

        switch (Tool)
        {
            case ToolMode.Move:
                {
                    var newWorld = _dragObjStartW + _dragAxisW * delta;
                    SetPositionWorld(_selected, newWorld);
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

                    // sensitivity (tweak to taste)
                    const float scaleK = 0.8f;

                    // clamp so we never hit zero/negative scale
                    float f = MathF.Max(0.001f, 1f + axisDelta * scaleK);
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

    static void SetPositionWorld(GameObject go, SN.Vector3 pWorld)
    {
        // accumulate parent world
        SN.Matrix4x4 parentW = SN.Matrix4x4.Identity;
        for (var p = go.Parent; p != null; p = p.Parent)
            parentW = WorldFromTransform(p.Transform) * parentW;

        SN.Matrix4x4.Invert(parentW, out var inv);
        var pLocal = SN.Vector3.Transform(pWorld, inv);

        // IMPORTANT: assign back to the Transform
        go.Transform.Position = new CoreVec3(pLocal.X, pLocal.Y, pLocal.Z);
    }
    #endregion

    #region Picking helpers
    void BuildPickRay(Point pt, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz,
                      out SN.Vector3 ro, out SN.Vector3 rd)
    {
        float x = (float)(pt.X / sz.Width * 2 - 1);
        float y = (float)(1 - pt.Y / sz.Height * 2);

        var np = new SN.Vector3(x, y, 0f);
        var fp = new SN.Vector3(x, y, 1f);

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var inv);

        var n4 = SN.Vector4.Transform(new SN.Vector4(np, 1), inv);
        var f4 = SN.Vector4.Transform(new SN.Vector4(fp, 1), inv);
        var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
        var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;

        ro = n3;
        rd = SN.Vector3.Normalize(f3 - n3);
    }

    static bool RayIntersectPlane(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 n, SN.Vector3 p0, out SN.Vector3 hit)
    {
        const float EPS = 1e-6f;
        float denom = SN.Vector3.Dot(rd, n);
        if (MathF.Abs(denom) < EPS) { hit = default; return false; }
        float t = SN.Vector3.Dot(p0 - ro, n) / denom;
        if (t < 0) { hit = default; return false; }
        hit = ro + rd * t;
        return true;
    }
    #endregion

    #region Projection helpers & grid/axes
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

    float EstimateProjectedRadiusPx(in SN.Matrix4x4 world, float radiusLocal,
                                in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        // center in world
        var centerW = SN.Vector3.Transform(SN.Vector3.Zero, world);

        // use X basis length as scale proxy (good for uniform or near-uniform scale)
        var basisX = new SN.Vector3(world.M11, world.M12, world.M13);
        float sx = basisX.Length();
        float rWorld = radiusLocal * (sx <= 1e-6f ? 1f : sx);

        var edgeW = centerW + SN.Vector3.Normalize(basisX) * rWorld;

        if (!ProjectToScreen(centerW, view, proj, sz, out var sc, out _) ||
            !ProjectToScreen(edgeW, view, proj, sz, out var se, out _))
            return 32f; // fallback

        double dx = se.X - sc.X, dy = se.Y - sc.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    bool TryProjectViewPoint(SN.Vector3 pView, in SN.Matrix4x4 proj, Size size,
                             out Point screen, out SN.Vector4 clip, out int outCode)
    {
        clip = SN.Vector4.Transform(new SN.Vector4(pView, 1), proj);
        if (clip.W < NearEps) { screen = default; outCode = 0; return false; }
        var ndc = clip / clip.W;
        screen = new Point(
            (ndc.X * 0.5f + 0.5f) * size.Width,
            (1 - (ndc.Y * 0.5f + 0.5f)) * size.Height);

        outCode = (ndc.X < -1 ? 1 : 0) | (ndc.X > 1 ? 2 : 0) |
                  (ndc.Y < -1 ? 4 : 0) | (ndc.Y > 1 ? 8 : 0) |
                  (ndc.Z < 0 ? 16 : 0) | (ndc.Z > 1 ? 32 : 0);
        return true;
    }

    bool ProjectToScreen(SN.Vector3 world, SN.Matrix4x4 view, SN.Matrix4x4 proj,
                         Size sz, out Point screen, out SN.Vector3 viewPos)
    {
        viewPos = SN.Vector3.Transform(world, view);
        return TryProjectViewPoint(viewPos, proj, sz, out screen, out _, out _);
    }

    bool ProjectToScreenVP(SN.Vector3 pW, in SN.Matrix4x4 vp, Size sz, out Point p)
    {
        var clip = SN.Vector4.Transform(new SN.Vector4(pW, 1f), vp);
        if (clip.W <= 0f) { p = default; return false; }
        float invW = 1f / clip.W;
        double x = ((clip.X * invW) * 0.5 + 0.5) * sz.Width;
        double y = (1.0 - ((clip.Y * invW) * 0.5 + 0.5)) * sz.Height;
        p = new Point(x, y);
        return true;
    }

    static bool ClipToNear(ref SN.Vector4 a, ref SN.Vector4 b)
    {
        bool ab = a.W < NearEps, bb = b.W < NearEps;
        if (ab && bb) return false;
        if (ab || bb)
        {
            var d = b - a; float t = (NearEps - a.W) / d.W; var p = a + t * d;
            if (ab) a = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
            else b = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
        }
        return true;
    }

    bool TryProjectSegment(SN.Vector3 A, SN.Vector3 B, SN.Matrix4x4 vp, Size size,
                           out Point p0, out Point p1)
    {
        var a = SN.Vector4.Transform(new SN.Vector4(A, 1), vp);
        var b = SN.Vector4.Transform(new SN.Vector4(B, 1), vp);
        if (!ClipToNear(ref a, ref b)) { p0 = default; p1 = default; return false; }

        var na = a / a.W; var nb = b / b.W;
        static int OutCode(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);

        if ((OutCode(na) & OutCode(nb)) != 0) { p0 = default; p1 = default; return false; }

        p0 = new Point((na.X * 0.5f + 0.5f) * size.Width,
                       (1 - (na.Y * 0.5f + 0.5f)) * size.Height);
        p1 = new Point((nb.X * 0.5f + 0.5f) * size.Width,
                       (1 - (nb.Y * 0.5f + 0.5f)) * size.Height);
        return true;
    }

    void DrawLine3D(DrawingContext ctx, SN.Matrix4x4 vp, Size size,
                    SN.Vector3 a, SN.Vector3 b, Color c, double th = 1)
    {
        if (!TryProjectSegment(a, b, vp, size, out var s0, out var s1)) return;
        ctx.DrawLine(new Pen(new SolidColorBrush(c), th), s0, s1);
    }

   
    #endregion

    #region Gizmo drawing & hit test
    void DrawTranslateGizmo(DrawingContext ctx, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return;

        var W = AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!ProjectToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return;

        // Determine world length -> ≈pixels
        if (!ProjectToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;

        var endX = anchor + SN.Vector3.UnitX * (float)worldLen;
        var endY = anchor + SN.Vector3.UnitY * (float)worldLen;
        var endZ = anchor + SN.Vector3.UnitZ * (float)worldLen;

        if (!ProjectToScreen(endX, view, proj, sz, out var pX, out _)) return;
        if (!ProjectToScreen(endY, view, proj, sz, out var pY, out _)) return;
        if (!ProjectToScreen(endZ, view, proj, sz, out var pZ, out _)) return;

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

        var W = AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);

        if (!ProjectToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return Axis.None;
        if (!ProjectToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return Axis.None;

        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;

        bool TryAxis(SN.Vector3 axis, out double d)
        {
            d = double.MaxValue;
            var end = anchor + axis * (float)worldLen;
            if (!ProjectToScreen(end, view, proj, sz, out var pEnd, out _)) return false;
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

    #region World accumulation
    SN.Matrix4x4 AccumulateWorld(GameObject go)
    {
        var stack = new Stack<GameObject>();
        for (var n = go; n != null; n = n.Parent) stack.Push(n);

        var w = SN.Matrix4x4.Identity;
        while (stack.Count > 0) w = w * WorldFromTransform(stack.Pop().Transform);
        return w;
    }
    #endregion

    #region Render pipeline
  

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var size = Bounds.Size;
        int W = Math.Max(1, (int)size.Width);
        int H = Math.Max(1, (int)size.Height);

        int SS = Supersample2x ? 2 : 1;
        int RW = W * SS;  // render width
        int RH = H * SS;  // render height

        var color = new uint[RW * RH];
        var zbuf = new float[RW * RH];

        uint bg = PackBGRA(Color.Parse("#1f1f1f"));
        for (int i = 0; i < zbuf.Length; i++)
        {
            zbuf[i] = 1.1f;
            color[i] = bg;
        }

        var (view, proj) = GetViewProj(new Size(RW, RH)); // aspect is the same

        // 🔎 Log once after selection/material/scene changes (see ctor handlers)
        if (_logNextRender)
        {
            _logNextRender = false;
            DumpSelectedMaterialDebug();

            if (ShowWire)
                System.Diagnostics.Debug.WriteLine("[SceneView] ShowWire is enabled — solid (textured) pass is skipped by design.");
        }

        // Depth-tested grid + solid pass at high res
        if (ShowGrid)
            DrawGridZ(view, proj, color, zbuf, RW, RH, halfLines: 20, step: 1f);

        if (!ShowWire)
        {
            foreach (var root in SceneService.Root)
                DrawNodeSolidZ(root, view, proj, SN.Matrix4x4.Identity, color, zbuf, RW, RH);
        }

        // Downsample (if needed) and blit
        var wb = new WriteableBitmap(new PixelSize(W, H), new Avalonia.Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var fb = wb.Lock())
            unsafe
            {
                byte* dst = (byte*)fb.Address;
                int stride = fb.RowBytes;

                if (SS == 2)
                {
                    var lo = new uint[W * H];
                    Downsample2x(color, RW, RH, lo, W, H);
                    fixed (uint* src = lo)
                    {
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * W, dst + y * stride, stride, W * 4);
                    }
                }
                else
                {
                    fixed (uint* src = color)
                    {
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * RW, dst + y * stride, stride, W * 4);
                    }
                }
            }

        ctx.DrawImage(wb, new Rect(0, 0, W, H));

        // Wireframe + gizmo (vector overlay at native res)
        var vp = view * proj;
        foreach (var root in SceneService.Root)
            DrawNodeWire(ctx, vp, size, root, SN.Matrix4x4.Identity, ShowWire);

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

            var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault(x => x.Enabled);
            var mr = go.Behaviors.OfType<MeshRenderer>().FirstOrDefault(x => x.Enabled);

            if (mr == null)
            {
                Debug.WriteLine($"[SceneView] '{go.Name}' has no enabled MeshRenderer.");
                return;
            }

            // Try to read a 'Material' property from the renderer (public or non-public)
            var matProp = mr.GetType().GetProperty("Material",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var mat = matProp?.GetValue(mr) as Game_Engine.Core.Material;

            if (mat == null)
            {
                Debug.WriteLine($"[SceneView] '{go.Name}' MeshRenderer has no Material.");
                return;
            }

            int texCount = mat.Textures?.Count ?? 0;
            var first = mat.Textures?.FirstOrDefault();
            var tex = first?.Texture;

            string texInfo = tex != null ? $"{tex.Width}x{tex.Height}" : "null";
            Debug.WriteLine($"[SceneView] Material: textures={texCount}, firstHasTexture={(tex != null)}, firstName='{first?.Name ?? "(none)"}', size={texInfo}");

            // UV presence on the current mesh
            int verts = mf?.Mesh?.Vertices?.Length ?? -1;
            System.Numerics.Vector2[]? uvs = null;

            if (mf?.Mesh != null)
            {
                // Look for Vector2[] UVs by common names (public or non-public)
                var cand = new[] { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
                var t = mf.Mesh.GetType();
                foreach (var n in cand)
                {
                    var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(System.Numerics.Vector2[]))
                    {
                        uvs = (System.Numerics.Vector2[]?)p.GetValue(mf.Mesh);
                        break;
                    }
                }
                if (uvs == null)
                {
                    foreach (var n in cand)
                    {
                        var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(System.Numerics.Vector2[]))
                        {
                            uvs = (System.Numerics.Vector2[]?)f.GetValue(mf.Mesh);
                            break;
                        }
                    }
                }
            }

            Debug.WriteLine($"[SceneView] Mesh: verts={verts}, hasUVs={(uvs != null)}, uvLen={(uvs?.Length ?? 0)}");

            // Reminder: your current RasterizeMeshSolidZ does NOT sample textures yet.
            Debug.WriteLine("[SceneView] Note: solid rasterizer is color-only; textures won’t show until we pass Material + sample UVs.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SceneView] Debug dump error: " + ex);
        }
    }


    void DrawNodeSolidZ(GameObject go, in Matrix4x4 view, in Matrix4x4 proj,
                    in Matrix4x4 parentWorld, uint[] color, float[] zbuf, int W, int H)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault(x => x.Enabled);
        var mr = go.Behaviors.OfType<MeshRenderer>().FirstOrDefault(x => x.Enabled);

        if (mf?.Mesh != null && mr != null && !mr.Wireframe)
        {
            var mesh = EnsureProceduralLod(go, mf.Mesh, world, view, proj, new Size(W, H));

            var matProp = mr.GetType().GetProperty("Material",
     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mat = matProp?.GetValue(mr) as Game_Engine.Core.Material;

            RasterizeMeshSolidZ(mesh, world, view, proj, color, zbuf, W, H, mr.Color, mat);

        }

        foreach (var child in go.Children)
            DrawNodeSolidZ(child, view, proj, world, color, zbuf, W, H);
    }


    Mesh EnsureProceduralLod(GameObject go, Mesh mesh,
                         in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        switch (mesh.Kind)
        {
            case MeshKind.Sphere:
                {
                    float rLocal = ApproxLocalRadius(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    var (needLon, needLat) = Mesh.SuggestSphereTesselation(rPx);

                    if (needLon > mesh.TessA || needLat > mesh.TessB)
                    {
                        // regenerate with the same local radius
                        var upgraded = Mesh.CreateUvSphere(needLon, needLat, rLocal);
                        // swap into the filter so wireframe etc. stays in sync
                        go.Behaviors.OfType<MeshFilter>().First().Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cylinder:
                {
                    var (rLocal, hLocal) = ApproxRadialAndHeight(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);

                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCylinder(needSides, rLocal, hLocal, caps: true);
                        go.Behaviors.OfType<MeshFilter>().First().Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cone:
                {
                    var (rLocal, hLocal) = ApproxRadialAndHeight(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);

                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCone(needSides, rLocal, hLocal, cap: true);
                        go.Behaviors.OfType<MeshFilter>().First().Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }
        }

        return mesh;
    }


    void DrawNodeWire(DrawingContext ctx, in SN.Matrix4x4 vp, Size sz,
                      GameObject go, in SN.Matrix4x4 parentWorld, bool globalWire)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault(x => x.Enabled);
        var mr = go.Behaviors.OfType<MeshRenderer>().FirstOrDefault(x => x.Enabled);

        if (mf?.Mesh != null && mr != null && (globalWire || mr.Wireframe))
            DrawMeshWire(ctx, mf.Mesh, world, vp, sz, mr.Color, (float)mr.LineWidth);

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

            if (!ProjectToScreenVP(p0w, vp, sz, out var s0)) continue;
            if (!ProjectToScreenVP(p1w, vp, sz, out var s1)) continue;
            if (!ProjectToScreenVP(p2w, vp, sz, out var s2)) continue;

            ctx.DrawLine(pen, s0, s1);
            ctx.DrawLine(pen, s1, s2);
            ctx.DrawLine(pen, s2, s0);
        }
    }
    #endregion

    #region Software rasterizer (solid pass)
    void RasterizeMeshSolidZ(
    Mesh mesh,
    in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    uint[] color, float[] zbuf, int W, int H,
    Color baseCol,
    Game_Engine.Core.Material? mat = null)
    {
        if (mesh?.Vertices == null || mesh.TriIndices == null) return;

        var V = mesh.Vertices;
        var I = mesh.TriIndices;
        var N0 = mesh.Normals; // may be null

        // Matrices
        var WV = world * view;
        var WVP = WV * proj;

        SN.Matrix4x4.Invert(world, out var invWorld);
        var normalM = SN.Matrix4x4.Transpose(invWorld);

        // Lighting
        SN.Vector3 L = SN.Vector3.Normalize(new SN.Vector3(0.35f, 0.9f, 0.45f));
        float DiffuseK = ShowLight ? 0.25f : 1.0f;
        float Ambient = ShowLight ? 0.90f : 0.0f;

        float winding = world.GetDeterminant() >= 0 ? 1f : -1f;

        static SN.Vector2 ToScreen(SN.Vector4 ndc, int W, int H)
            => new SN.Vector2((ndc.X * 0.5f + 0.5f) * W, (1 - (ndc.Y * 0.5f + 0.5f)) * H);

        const float INSIDE_EPS = 1e-6f;

        // Try to locate UVs
        static SN.Vector2[]? TryGetMeshUVs(Mesh m)
        {
            var cand = new[] { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
            var t = m.GetType();
            foreach (var name in cand)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(SN.Vector2[]))
                    return (SN.Vector2[]?)p.GetValue(m);
            }
            foreach (var name in cand)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(SN.Vector2[]))
                    return (SN.Vector2[]?)f.GetValue(m);
            }
            return null;
        }

        static uint PackBGRA_PM(Color c)
        {
            byte a = c.A;
            uint r = (uint)(c.R * a / 255);
            uint g = (uint)(c.G * a / 255);
            uint b = (uint)(c.B * a / 255);
            return (uint)(b | (g << 8) | (r << 16) | (a << 24));
        }

        static Color ShadeColor(Color c, float shade)
        {
            if (shade < 0f) shade = 0f; else if (shade > 1f) shade = 1f;
            byte r = (byte)Math.Clamp((int)(c.R * shade), 0, 255);
            byte g = (byte)Math.Clamp((int)(c.G * shade), 0, 255);
            byte b = (byte)Math.Clamp((int)(c.B * shade), 0, 255);
            return Color.FromArgb(c.A, r, g, b);
        }

        static uint SampleBGRA_PM(Game_Engine.Core.Texture2D t, float u, float v)
        {
            if (t.Width <= 0 || t.Height <= 0) return 0xFF000000;
            u -= MathF.Floor(u); v -= MathF.Floor(v);
            v = 1f - v; // flip V for top-left images
            int x = Math.Clamp((int)MathF.Round(u * (t.Width - 1)), 0, t.Width - 1);
            int y = Math.Clamp((int)MathF.Round(v * (t.Height - 1)), 0, t.Height - 1);
            int i = (y * t.Width + x) * 4; // RGBA source
            byte r = t.Rgba[i + 0], g = t.Rgba[i + 1], b = t.Rgba[i + 2], a = t.Rgba[i + 3];
            uint rp = (uint)(r * a / 255), gp = (uint)(g * a / 255), bp = (uint)(b * a / 255);
            return (uint)(bp | (gp << 8) | (rp << 16) | ((uint)a << 24));
        }

        static uint MulBGRA_PM(uint bgraTex, Color tint, float shade)
        {
            int tb = (int)(bgraTex & 0xFF);
            int tg = (int)((bgraTex >> 8) & 0xFF);
            int tr = (int)((bgraTex >> 16) & 0xFF);
            int ta = (int)((bgraTex >> 24) & 0xFF);

            int r = (int)(tr * tint.R / 255f * shade);
            int g = (int)(tg * tint.G / 255f * shade);
            int b = (int)(tb * tint.B / 255f * shade);
            int a = (int)(ta * tint.A / 255f);

            r = Math.Clamp(r, 0, 255);
            g = Math.Clamp(g, 0, 255);
            b = Math.Clamp(b, 0, 255);
            a = Math.Clamp(a, 0, 255);
            return (uint)(b | (g << 8) | (r << 16) | (a << 24));
        }

        // Material + source UVs (if any)
        var UV = TryGetMeshUVs(mesh);
        var tex = mat?.Textures?.FirstOrDefault(t => t?.Texture != null)?.Texture;

        // Precompute object-space AABB for fallback projection
        SN.Vector3 bbMin = new(float.MaxValue), bbMax = new(float.MinValue);
        for (int v = 0; v < V.Length; v++)
        {
            var p = V[v];
            bbMin = new SN.Vector3(MathF.Min(bbMin.X, p.X), MathF.Min(bbMin.Y, p.Y), MathF.Min(bbMin.Z, p.Z));
            bbMax = new SN.Vector3(MathF.Max(bbMax.X, p.X), MathF.Max(bbMax.Y, p.Y), MathF.Max(bbMax.Z, p.Z));
        }
        var bbSize = bbMax - bbMin;
        bbSize.X = bbSize.X == 0 ? 1 : bbSize.X;
        bbSize.Y = bbSize.Y == 0 ? 1 : bbSize.Y;
        bbSize.Z = bbSize.Z == 0 ? 1 : bbSize.Z;

        for (int iTri = 0; iTri < I.Length; iTri += 3)
        {
            int ia = I[iTri + 0], ib = I[iTri + 1], ic = I[iTri + 2];
            var a = V[ia]; var b = V[ib]; var c = V[ic];

            var Ac = SN.Vector4.Transform(new SN.Vector4(a, 1), WVP);
            var Bc = SN.Vector4.Transform(new SN.Vector4(b, 1), WVP);
            var Cc = SN.Vector4.Transform(new SN.Vector4(c, 1), WVP);
            if (Ac.W <= 0 || Bc.W <= 0 || Cc.W <= 0) continue;

            var An = Ac / Ac.W; var Bn = Bc / Bc.W; var Cn = Cc / Cc.W;

            static int OutMask(SN.Vector4 n) =>
                (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
                (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
                (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);
            if ((OutMask(An) & OutMask(Bn) & OutMask(Cn)) != 0) continue;

            var As = ToScreen(An, W, H);
            var Bs = ToScreen(Bn, W, H);
            var Cs = ToScreen(Cn, W, H);

            var av = SN.Vector3.Transform(a, WV);
            var bv = SN.Vector3.Transform(b, WV);
            var cv = SN.Vector3.Transform(c, WV);

            var nView = SN.Vector3.Cross(bv - av, cv - av);
            if (winding * nView.Z >= 0f) continue; // back-face

            // World-space vertex normals (Phong) or flat fallback
            SN.Vector3 nA, nB, nC;
            if (N0 != null && N0.Length == V.Length)
            {
                var na4 = SN.Vector4.Transform(new SN.Vector4(N0[ia], 0f), normalM);
                var nb4 = SN.Vector4.Transform(new SN.Vector4(N0[ib], 0f), normalM);
                var nc4 = SN.Vector4.Transform(new SN.Vector4(N0[ic], 0f), normalM);
                nA = new SN.Vector3(na4.X, na4.Y, na4.Z);
                nB = new SN.Vector3(nb4.X, nb4.Y, nb4.Z);
                nC = new SN.Vector3(nc4.X, nc4.Y, nc4.Z);
            }
            else
            {
                var aw = SN.Vector3.Transform(a, world);
                var bw = SN.Vector3.Transform(b, world);
                var cw = SN.Vector3.Transform(c, world);
                var nWorldFlat = SN.Vector3.Normalize(SN.Vector3.Cross(bw - aw, cw - aw));
                if (winding < 0) nWorldFlat = -nWorldFlat;
                nA = nB = nC = nWorldFlat;
            }

            // Per-vertex UVs
            SN.Vector2 ua = default, ub = default, uc = default;
            bool haveUV = (tex != null && UV != null && UV.Length == V.Length);
            if (haveUV)
            {
                ua = UV![ia]; ub = UV![ib]; uc = UV![ic];
            }
            else if (tex != null)
            {
                // Fallback: simple box-projection per triangle using object-space AABB
                // Project onto the major axis of the face normal (in world space)
                var aw = a; var bw = b; var cw = c; // object space
                var nFlat = SN.Vector3.Normalize(SN.Vector3.Cross(b - a, c - a));
                nFlat = new SN.Vector3(MathF.Abs(nFlat.X), MathF.Abs(nFlat.Y), MathF.Abs(nFlat.Z));
                if (nFlat.X >= nFlat.Y && nFlat.X >= nFlat.Z)
                {
                    // X-major -> use YZ plane
                    ua = new SN.Vector2((aw.Z - bbMin.Z) / bbSize.Z, (aw.Y - bbMin.Y) / bbSize.Y);
                    ub = new SN.Vector2((bw.Z - bbMin.Z) / bbSize.Z, (bw.Y - bbMin.Y) / bbSize.Y);
                    uc = new SN.Vector2((cw.Z - bbMin.Z) / bbSize.Z, (cw.Y - bbMin.Y) / bbSize.Y);
                }
                else if (nFlat.Y >= nFlat.X && nFlat.Y >= nFlat.Z)
                {
                    // Y-major -> use XZ plane
                    ua = new SN.Vector2((aw.X - bbMin.X) / bbSize.X, (aw.Z - bbMin.Z) / bbSize.Z);
                    ub = new SN.Vector2((bw.X - bbMin.X) / bbSize.X, (bw.Z - bbMin.Z) / bbSize.Z);
                    uc = new SN.Vector2((cw.X - bbMin.X) / bbSize.X, (cw.Z - bbMin.Z) / bbSize.Z);
                }
                else
                {
                    // Z-major -> use XY plane
                    ua = new SN.Vector2((aw.X - bbMin.X) / bbSize.X, (aw.Y - bbMin.Y) / bbSize.Y);
                    ub = new SN.Vector2((bw.X - bbMin.X) / bbSize.X, (bw.Y - bbMin.Y) / bbSize.Y);
                    uc = new SN.Vector2((cw.X - bbMin.X) / bbSize.X, (cw.Y - bbMin.Y) / bbSize.Y);
                }
                haveUV = true;
            }

            // Perspective-correct setup
            float aInvW = 1f / Ac.W, bInvW = 1f / Bc.W, cInvW = 1f / Cc.W;
            float aZw = An.Z * aInvW, bZw = Bn.Z * bInvW, cZw = Cn.Z * cInvW;

            int minX = (int)MathF.Floor(MathF.Min(As.X, MathF.Min(Bs.X, Cs.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(As.X, MathF.Max(Bs.X, Cs.X)));
            int minY = (int)MathF.Floor(MathF.Min(As.Y, MathF.Min(Bs.Y, Cs.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(As.Y, MathF.Max(Bs.Y, Cs.Y)));
            if (maxX < 0 || maxY < 0 || minX >= W || minY >= H) continue;
            minX = Math.Clamp(minX, 0, W - 1); maxX = Math.Clamp(maxX, 0, W - 1);
            minY = Math.Clamp(minY, 0, H - 1); maxY = Math.Clamp(maxY, 0, H - 1);

            static float Edge(SN.Vector2 p, SN.Vector2 a2, SN.Vector2 b2)
                => (p.X - a2.X) * (b2.Y - a2.Y) - (p.Y - a2.Y) * (b2.X - a2.X);

            float area = Edge(Cs, As, Bs); if (area == 0) continue;
            float invArea = 1f / area;

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new SN.Vector2(x + 0.5f, y + 0.5f);

                    float w0 = Edge(p, Bs, Cs);
                    float w1 = Edge(p, Cs, As);
                    float w2 = Edge(p, As, Bs);

                    if (area > 0f)
                    {
                        if (w0 < -INSIDE_EPS || w1 < -INSIDE_EPS || w2 < -INSIDE_EPS) continue;
                    }
                    else
                    {
                        if (w0 > INSIDE_EPS || w1 > INSIDE_EPS || w2 > INSIDE_EPS) continue;
                    }

                    w0 *= invArea; w1 *= invArea; w2 *= invArea;

                    float invW = w0 * aInvW + w1 * bInvW + w2 * cInvW;
                    if (invW <= 0) continue;

                    float z = (w0 * aZw + w1 * bZw + w2 * cZw) / invW;

                    int idx = y * W + x;
                    if (z >= zbuf[idx]) continue;

                    // Interpolate world normal and shade
                    SN.Vector3 nInterp =
                        (nA * (w0 * aInvW) +
                         nB * (w1 * bInvW) +
                         nC * (w2 * cInvW)) / invW;
                    nInterp = SN.Vector3.Normalize(nInterp);
                    float ndotl = MathF.Max(0f, SN.Vector3.Dot(nInterp, L));
                    float shade = MathF.Min(1f, Ambient + DiffuseK * ndotl);

                    uint outPixel;
                    if (tex != null && haveUV)
                    {
                        float u = (w0 * ua.X * aInvW + w1 * ub.X * bInvW + w2 * uc.X * cInvW) / invW;
                        float v = (w0 * ua.Y * aInvW + w1 * ub.Y * bInvW + w2 * uc.Y * cInvW) / invW;
                        var texel = SampleBGRA_PM(tex!, u, v);
                        outPixel = MulBGRA_PM(texel, baseCol, shade);
                    }
                    else
                    {
                        outPixel = PackBGRA_PM(ShadeColor(baseCol, shade));
                    }

                    zbuf[idx] = z;
                    color[idx] = outPixel;
                }
        }
    }



    


    #endregion

    #region Depth-tested grid
    void DrawGridZ(in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                   uint[] color, float[] zbuf, int W, int H,
                   int halfLines, float step)
    {
        uint light = PackBGRA(Color.FromRgb(0x30, 0x30, 0x30));
        uint dark = PackBGRA(Color.FromRgb(0x40, 0x40, 0x40));
        uint axis = PackBGRA(Color.FromRgb(0x50, 0x50, 0x50));

        for (int i = -halfLines; i <= halfLines; i++)
        {
            float z = i * step; float x = i * step;
            uint colZ = (i == 0) ? axis : (i % 5 == 0 ? dark : light);
            uint colX = (i == 0) ? axis : (i % 5 == 0 ? dark : light);

            ZLine(new SN.Vector3(-halfLines * step, 0, z), new SN.Vector3(halfLines * step, 0, z),
                  view, proj, color, zbuf, W, H, colZ);
            ZLine(new SN.Vector3(x, 0, -halfLines * step), new SN.Vector3(x, 0, halfLines * step),
                  view, proj, color, zbuf, W, H, colX);
        }
    }

    void ZLine(SN.Vector3 a, SN.Vector3 b,
               in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
               uint[] color, float[] zbuf, int W, int H, uint packed)
    {
        var wvp = view * proj;

        var ac = SN.Vector4.Transform(new SN.Vector4(a, 1), wvp);
        var bc = SN.Vector4.Transform(new SN.Vector4(b, 1), wvp);
        if (ac.W <= 0 || bc.W <= 0) return;

        var an = ac / ac.W;
        var bn = bc / bc.W;

        static int Out(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);
        if ((Out(an) & Out(bn)) != 0) return;

        var aS = new SN.Vector2((an.X * 0.5f + 0.5f) * W, (1 - (an.Y * 0.5f + 0.5f)) * H);
        var bS = new SN.Vector2((bn.X * 0.5f + 0.5f) * W, (1 - (bn.Y * 0.5f + 0.5f)) * H);

        float dx = bS.X - aS.X, dy = bS.Y - aS.Y;
        int steps = (int)MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        if (steps <= 0) return;

        float sx = dx / steps, sy = dy / steps;
        float sz = (bn.Z - an.Z) / steps;

        float x = aS.X, y = aS.Y, z = an.Z;
        for (int i = 0; i <= steps; i++, x += sx, y += sy, z += sz)
        {
            int ix = (int)x, iy = (int)y;
            if ((uint)ix < (uint)W && (uint)iy < (uint)H)
            {
                int idx = iy * W + ix;
                if (z < zbuf[idx]) { zbuf[idx] = z; color[idx] = packed; }
            }
        }
    }

    static void Downsample2x(uint[] src, int srcW, int srcH, uint[] dst, int dstW, int dstH)
    {
        for (int y = 0; y < dstH; y++)
        {
            int sy = y * 2;
            int row0 = sy * srcW;
            int row1 = (sy + 1) * srcW;
            int di = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                int sx = x * 2;

                uint p00 = src[row0 + sx];
                uint p01 = src[row0 + sx + 1];
                uint p10 = src[row1 + sx];
                uint p11 = src[row1 + sx + 1];

                // Average BGRA (premul-safe if A is 255, which we use)
                int b = ((int)(p00 & 0xFF) + (int)(p01 & 0xFF) + (int)(p10 & 0xFF) + (int)(p11 & 0xFF)) >> 2;
                int g = (((int)(p00 >> 8) & 0xFF) + ((int)(p01 >> 8) & 0xFF) + ((int)(p10 >> 8) & 0xFF) + ((int)(p11 >> 8) & 0xFF)) >> 2;
                int r = (((int)(p00 >> 16) & 0xFF) + ((int)(p01 >> 16) & 0xFF) + ((int)(p10 >> 16) & 0xFF) + ((int)(p11 >> 16) & 0xFF)) >> 2;
                int a = (((int)(p00 >> 24) & 0xFF) + ((int)(p01 >> 24) & 0xFF) + ((int)(p10 >> 24) & 0xFF) + ((int)(p11 >> 24) & 0xFF)) >> 2;

                dst[di + x] = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            }
        }
    }

    

    // Approx local bounding radius (for any mesh)
    static float ApproxLocalRadius(Mesh m)
    {
        float r2 = 0f;
        var v = m.Vertices;
        for (int i = 0; i < v.Length; i++)
        {
            float d2 = v[i].X * v[i].X + v[i].Y * v[i].Y + v[i].Z * v[i].Z;
            if (d2 > r2) r2 = d2;
        }
        return MathF.Sqrt(r2);
    }

    // Approx cylinder/cone parameters from geometry
    static (float radius, float height) ApproxRadialAndHeight(Mesh m)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, r = 0f;
        foreach (var p in m.Vertices)
        {
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            float rr = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            if (rr > r) r = rr;
        }
        return (r, maxY - minY);
    }



    #endregion



}