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
        Core.Projection.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (Core.Projection.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hit))
            _dragAnchorW = hit;
        InvalidateVisual();
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;
        Core.Projection.BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!Core.Projection.RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;
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

        // --- Active view/proj (camera component or editor orbit) -------------------
        var active = GetActiveViewProj(new Size(RW, RH));
        var view = active.View;
        var proj = active.Proj;
        var usingCam = active.UsingComponent && active.Cam is not null;

        // Sun highlight from directional light (for sky)
        var dirLight = SceneQuery.FindBehaviors<Light>().FirstOrDefault(l => l.Type == LightType.Directional);
        SN.Vector3? sunDir = null;
        if (dirLight?.gameObject is { } dgo)
        {
            var Wl = SceneGraphUtil.AccumulateWorld(dgo);
            var z = new SN.Vector3(Wl.M13, Wl.M23, Wl.M33);
            if (z.LengthSquared() < 1e-8f) z = SN.Vector3.UnitZ;
            sunDir = -SN.Vector3.Normalize(z);
        }

        // Skybox knobs
        float skyYaw = sky?.Yaw ?? 0f;
        float seamFeather = sky?.SeamFeather ?? 0.01f;
        bool keyOut = sky?.KeyOutNearBlack ?? true;
        float keyLuma = sky?.KeyLuma ?? 0.08f;

        // If not looking through a Camera component, clear full screen with sky now.
        // If using a Camera, we still fill the full background with sky first so area
        // outside the camera viewport looks nice.
        Sky.FillWorldUp(color, zbuf, RW, RH, view, proj,
               skyTop, skyBot, sunDir, skyTex, skyBlend,
               skyYaw, seamFeather, keyOut, keyLuma,
               zWriteNdc: 1f - 1e-6f);   // write at far plane

        // --- Lighting --------------------------------------------------------------
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

            var lw = light.gameObject is null ? SN.Matrix4x4.Identity : SceneGraphUtil.AccumulateWorld(light.gameObject);
            lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, lw);

            if (light.Type == LightType.Directional && light.gameObject is { } lt)
                L = -ForwardFrom(lt.Transform);
            else if (light.Type == LightType.Point)
            {
                lightIsPoint = true;
                lightRange = Math.Max(0.001f, light.Range);
            }
        }

        // --- Tiny shadow map for directional light --------------------------------
        ShadowMap? shadow = null;
        if (ShowLight && light is { Type: LightType.Directional } && !lightIsPoint)
        {
            var (smin, smax) = SceneGraphUtil.ComputeSceneAABB();
            var center = (smin + smax) * 0.5f;
            var diag = (smax - smin).Length();
            float ortho = Math.Max(8f, diag * 0.6f);
            float dist = Math.Max(8f, diag * 0.75f);

            var eye = center - L * dist;
            var lightView = SN.Matrix4x4.CreateLookAt(eye, center, SN.Vector3.UnitY);
            var lightProj = SN.Matrix4x4.CreateOrthographic(ortho, ortho, 0.1f, dist + diag + 8f);

            const int SW = 256, SH = 256;
            var sdepth = new float[SW * SH];
            for (int i = 0; i < sdepth.Length; i++) sdepth[i] = 1.1f;

            foreach (var root in SceneService.Root)
                SceneRenderer.DrawNodeDepth(root, lightView, lightProj, SN.Matrix4x4.Identity, sdepth, SW, SH);

            shadow = new ShadowMap { VP = lightView * lightProj, Depth = sdepth, W = SW, H = SH, Bias = 0.0025f };
        }

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
            // Editor orbit camera renders the whole surface
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
            // Render the selected camera into its normalized viewport
            var cam = active.Cam!;

            // Compute viewport rect in **render** resolution
            var (vx, vy, vw, vh) = ViewportUtil.ViewportPx(cam, RW, RH);

            // Sub-buffers sized to the viewport
            var vColor = new uint[vw * vh];
            var vZ = new float[vw * vh];

            // View/Proj must use the viewport's aspect to be correct
            var vView = cam.GetViewMatrix();
            var vProj = cam.GetProjectionMatrix(new Avalonia.Size(vw, vh));

            // Apply camera clear/background inside the viewport
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

            // Composite the camera's viewport into the full software framebuffer
            ImageUtil.Blit(vColor, vw, vh, color, RW, RH, vx, vy);

            // For overlays (wire/gizmo) use the same camera matrices
            view = vView;
            proj = vProj;
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

        // --- Optional wire overlay & gizmo (use 'view/proj' chosen above) ----------
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