#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;
using Game_Engine.Core.Physics;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Rendering.UI;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using SN = System.Numerics;

namespace Game_Engine;

/// <summary>
/// Standalone player viewport -- simplified version of the editor's GameView.
/// Starts playing immediately on load (no Play/Pause/Stop state machine).
/// No editor overlays (gizmos, grid, selection highlights).
/// </summary>
public class PlayerView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    public bool HitTest(Point point) => true;

    #region GPU Resources
    private GLContext? _glCtx;
    private ViewRenderResources? _res;
    private GPUFramebuffer? _sceneFBO;
    private int _sceneFBO_W, _sceneFBO_H;
    #endregion

    #region Clocks & State
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(16.666) };
    readonly DispatcherTimer _fixedTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    readonly Stopwatch _updateWatch = new();
    readonly Stopwatch _fixedWatch = new();
    readonly Stopwatch _frameWatch = new();
    readonly Stopwatch _fpsTick = new();
    readonly Stopwatch _fpsWindow = new();

    double _msFrameLast, _msFrameEma;
    int _fpsFrames;
    double _fpsDisplay;

    bool _awakened, _started, _collidersWarm, _needsWarm;
    bool _playing;
    readonly Game_Engine.Core.Rendering.PlayerFrameState _frame = new();
    readonly Game_Engine.Core.PlayerSimulation _sim = new();

    const double FIXED_DT = 1.0 / 60.0;
    double _fixedAccum = 0.0;

    SN.Vector2 _lastMouse;
    bool _hasLastMouse;
    IPointer? _capturedPointer;

    Skybox? _sky;
    Light? _light;
    readonly List<Camera> _cams = new(4);

    static readonly Color FallbackSkyTop = Color.FromRgb(0x1f, 0x1f, 0x1f);
    static readonly Color FallbackSkyBot = Color.FromRgb(0x0a, 0x0a, 0x0a);
    static readonly Typeface HudTypeface = new("Segoe UI");
    static readonly IBrush HudText = Brushes.White;
    static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
    #endregion

    private volatile bool _renderInFlight;

    /// <summary>When true, draws the FPS / GL frame-time overlay. Defaults to false (enable via --fps or toggle with F3).</summary>
    public bool ShowFpsHud { get; set; } = App.ShowFpsHud;

    public PlayerView()
    {
        ClipToBounds = true;

        _updateTimer.Tick += (_, __) =>
        {
            TickUpdate();
            if (!_renderInFlight)
            {
                _renderInFlight = true;
                RequestNextFrameRendering();
            }
        };
        _fixedTimer.Tick += (_, __) => TickFixedUpdate();

        // Play-mode NotifyChanged is extremely chatty (transforms). Do not InvalidateAll here —
        // that wiped BCn skip state and re-probed every texture path every frame.
        SceneService.Changed += () => { RebuildSceneCaches(); _needsWarm = true; RequestNextFrameRendering(); };

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RebuildSceneCaches();
        _fpsTick.Restart();
        _fpsWindow.Restart();
    }

    /// <summary>Called by PlayerWindow after loading the startup scene.</summary>
    public void StartPlaying()
    {
        if (_playing) return;
        _playing = true;
        _sim.Start();

        _needsWarm = true;
        Focus();
        Core.Time.Reset();
        _updateWatch.Restart();
        _fixedWatch.Restart();
        Input.ClearAll();
        _updateTimer.Start();
        _fixedTimer.Start();
        _fpsTick.Restart();
        _fpsWindow.Restart();
        RebuildSceneCaches();
    }

    #region OpenGL Lifecycle
    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try
        {
            _glCtx = new GLContext(name => gl.GetProcAddress(name));
            var g = _glCtx.GL;
            _res = new ViewRenderResources();
            _res.Initialize(g, _glCtx.IsES, shadowResolution: 768);
            _frame.Res = _res;
            _frame.FpsTick.Restart();
            _frame.FpsWindow.Restart();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerView] GL init failed: {ex}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        var g = _glCtx?.GL;
        if (g != null)
        {
            SceneRenderer.DisposeStaticGlResources(g);
            CustomShaderCache.ClearContext(g);
        }
        _sceneFBO?.Dispose(); _sceneFBO = null; _sceneFBO_W = 0; _sceneFBO_H = 0;
        _res?.Dispose(); _res = null;
        _glCtx?.Dispose();
        _glCtx = null;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_glCtx == null || _res?.Standard == null || _res.Sky == null || _res.FsQuad == null || _res.Cache == null)
            return;

        _frame.Res = _res;
        _frame.Playing = _playing;
        _frame.Sky = _sky;
        _frame.Light = _light;
        _frame.Cameras = _cams;
        _frame.RenderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _frame.WidthDip = Math.Max(1.0, Bounds.Width);
        _frame.HeightDip = Math.Max(1.0, Bounds.Height);
        Game_Engine.Core.Rendering.PlayerFrameRenderer.Render(_glCtx.GL, _frame, fb);
        _renderInFlight = _frame.RenderInFlight;
        _msFrameEma = _frame.MsFrameEma;
        _fpsDisplay = _frame.FpsDisplay;
    }
    #endregion

    #region 2D HUD overlay
    public override void Render(DrawingContext ctx)
    {
        MaterialRebind.RepairScene();
        if (MaterialRebind.NeedsMoreFrames)
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

        base.Render(ctx);
        if (ShowFpsHud)
            DrawFpsHud(ctx);
    }

    void DrawFpsHud(DrawingContext ctx)
    {
        string line1 = $"FPS:{_fpsDisplay:F0}  GL:{_msFrameEma:F1}ms";
        const double font = 12, padX = 8, padY = 6;
        double est = line1.Length * font * 0.62;
        double lineH = font * 1.4;
        double w = est + padX * 2, h = lineH + padY * 2;
        var bg = new Rect(6, 6, Math.Ceiling(w), Math.Ceiling(h));
        ctx.FillRectangle(HudBg, bg);
        new TextLayout(line1, HudTypeface, font, HudText).Draw(ctx, new Point(bg.X + padX, bg.Y + padY));
    }
    #endregion

    #region Scene caches & helpers
    void RebuildSceneCaches()
    {
        _sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
        _light = SceneQuery.FindBehaviors<Light>().FirstOrDefault(l => l.Enabled);
        _cams.Clear();
        foreach (var c in SceneQuery.FindBehaviors<Camera>()) _cams.Add(c);
    }

    static void Ema(ref double acc, double sample, double a)
    { acc = acc <= 0 ? sample : (1 - a) * acc + a * sample; }

    void UpdateFps(double dt)
    {
        _fpsFrames++;
        if (!_fpsWindow.IsRunning) _fpsWindow.Restart();
        if (_fpsWindow.ElapsedMilliseconds >= 500)
        {
            _fpsDisplay = _fpsFrames / _fpsWindow.Elapsed.TotalSeconds;
            _fpsFrames = 0; _fpsWindow.Restart();
        }
    }
    #endregion

    #region Input
    void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.F3)
        {
            ShowFpsHud = !ShowFpsHud;
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        if (!_playing) return;
        Input.FeedKeyDown(KeyMap.FromAvalonia(e.Key));
    }

    void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.F3)
        {
            e.Handled = true;
            return;
        }
        if (!_playing) return;
        Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
    }

    float PlayUiScale() => (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

    void FeedPlayUiPointer(float dipX, float dipY)
    {
        float s = PlayUiScale();
        Input.FeedMousePosition(dipX * s, dipY * s);
        Input.FeedViewportSize(
            Math.Max(1f, (float)Bounds.Width * s),
            Math.Max(1f, (float)Bounds.Height * s));
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!_playing) return;
        Focus();
        var pt = e.GetCurrentPoint(this);
        var pos = e.GetPosition(this);
        FeedPlayUiPointer((float)pos.X, (float)pos.Y);
        _lastMouse = new SN.Vector2((float)pos.X, (float)pos.Y);
        _hasLastMouse = true;
        if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
        if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
        if (pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;
        Input.PlayViewportCaptureActive = true;
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_playing) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Left);
        if (!pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Middle);
        if (!pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Right);
        if (ReferenceEquals(_capturedPointer, e.Pointer))
        {
            try { e.Pointer.Capture(null); } catch { }
            _capturedPointer = null;
            Input.PlayViewportCaptureActive = false;
        }
    }

    void OnPointerMoved(object? s, PointerEventArgs e)
    {
        if (!_playing) return;
        var p = e.GetPosition(this);
        var cur = new SN.Vector2((float)p.X, (float)p.Y);
        if (_hasLastMouse)
            Input.FeedMouseDelta(cur.X - _lastMouse.X, cur.Y - _lastMouse.Y);
        _lastMouse = cur;
        _hasLastMouse = true;
        FeedPlayUiPointer(cur.X, cur.Y);
    }

    void OnPointerWheel(object? s, PointerWheelEventArgs e)
    {
        if (!_playing) return;
        Input.FeedMouseScroll((float)e.Delta.Y);
    }
    #endregion

    #region Update / FixedUpdate
    void TickUpdate()
    {
        if (!_playing) return;
        float s = PlayUiScale();
        _sim.TickUpdate(
            Math.Max(1f, (float)Bounds.Width * s),
            Math.Max(1f, (float)Bounds.Height * s),
            _capturedPointer != null || IsPointerOver);
    }

    void TickFixedUpdate()
    {
        if (!_playing) return;
        _sim.TickFixed();
    }
    #endregion
}