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

        SceneService.Changed += () => { RebuildSceneCaches(); _needsWarm = true; _res?.Cache?.InvalidateAll(); RequestNextFrameRendering(); };

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RebuildSceneCaches();
        _fpsTick.Restart();
        _fpsWindow.Restart();
    }

    /// <summary>Called by PlayerWindow after loading the startup scene.</summary>
    public void StartPlaying()
    {
        if (_playing) return;
        _playing = true;

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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerView] GL init failed: {ex}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
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

        var g = _glCtx.GL;

        // Flush stale GL errors
        while (g.GetError() != GLEnum.NoError) { }

        _frameWatch.Restart();

        double dt = _fpsTick.IsRunning ? _fpsTick.Elapsed.TotalSeconds : 0.0;
        _fpsTick.Restart();
        UpdateFps(dt);

        // Wind system update
        WindSystem.Update((float)Math.Min(dt, 0.1));

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double Wdip = Math.Max(1.0, Bounds.Width);
        double Hdip = Math.Max(1.0, Bounds.Height);
        int W = Math.Max(1, (int)(Wdip * scaling));
        int H = Math.Max(1, (int)(Hdip * scaling));

        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)W, (uint)H);
        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Less);
        g.Disable(EnableCap.Blend);
        g.ColorMask(true, true, true, true);
        g.DepthMask(true);

        // If not yet playing, show black screen
        if (!_playing)
        {
            g.ClearColor(0f, 0f, 0f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            CleanupGLState(g, fb);
            return;
        }

        // --- SCENE SETUP ---
        var sky = _sky;
        var skyTop = sky?.Top ?? FallbackSkyTop;
        var skyBot = sky?.Bottom ?? FallbackSkyBot;
        Texture2D? skyTex = sky?.Texture;
        float skyMix = sky != null ? Math.Clamp(sky.TextureBlend, 0f, 1f) : 0f;
        float skyYaw = sky?.Yaw ?? 0f;

        // Sun direction
        SN.Vector3? sunDir = null;
        if (sky != null)
        {
            float elevRad = Math.Clamp(sky.SunElevation, 1f, 89f) * MathF.PI / 180f;
            float yawRad = sky.Yaw * MathF.PI / 180f;
            var baseSun = new SN.Vector3(0f, MathF.Sin(elevRad), MathF.Cos(elevRad));
            var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, yawRad);
            sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
        }

        // Lighting
        float Ambient = Math.Clamp(sky?.Ambient ?? 0f, 0f, 1f);
        var light = _light;
        SN.Vector3 L = SN.Vector3.UnitY;
        float DiffuseK = 0f;
        bool lightIsPoint = false;
        SN.Vector3 lightPosW = SN.Vector3.Zero;
        float lightRange = 0f;

        SN.Vector3 lightColorNorm = new SN.Vector3(1f, 1f, 1f);
        if (light?.gameObject != null)
        {
            float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
            DiffuseK = Math.Max(light.Intensity * Math.Max(lum, 0.001f), 0.001f);
            lightColorNorm = new SN.Vector3(light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f);
            var m = TransformUtil.WorldFromTransform(light.gameObject.Transform);
            if (light.Type == LightType.Directional)
            {
                var fwd = SN.Vector3.Normalize(new SN.Vector3(m.M13, m.M23, m.M33));
                L = -fwd;
            }
            else
            {
                lightIsPoint = true;
                lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, m);
                lightRange = Math.Max(0.001f, light.Range);
            }
        }

        // Camera
        Camera? cam = _cams.Count > 0 ? _cams[0] : null;
        SN.Matrix4x4 view, proj;
        if (cam != null)
        {
            view = cam.GetViewMatrix();
            proj = cam.GetProjectionMatrix(new Size(Wdip, Hdip));
        }
        else
        {
            view = SN.Matrix4x4.CreateLookAt(new SN.Vector3(0, 5, 10), SN.Vector3.Zero, SN.Vector3.UnitY);
            proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, (float)(Wdip / Math.Max(1, Hdip)), 0.1f, 1000f);
        }

        SN.Matrix4x4.Invert(view, out var invView);
        var camPos = new SN.Vector3(invView.M41, invView.M42, invView.M43);

        // --- CLEAR ---
        g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var res = _res!;

        // --- SKY ---
        Sky.RenderGPU(g, res.Sky!, res.FsQuad!, res.Cache!, view, proj,
            skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);

        // --- SHADOW MAP PASS ---
        SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
        GPUFramebuffer? shadowFBO = null;
        if (res.Shadow != null && res.Depth != null)
        {
            var sunShineDir = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            SN.Matrix4x4.Invert(view, out var invV);
            var camFwd = new SN.Vector3(-invV.M31, -invV.M32, -invV.M33);
            var sceneCenter = camPos + camFwd * 12f;
            float sceneRadius = 50f;
            shadowVP = ShadowMapGPU.BuildDirectionalLightVP(sunShineDir, sceneCenter, sceneRadius);
            res.Shadow.LightVP = shadowVP;

            res.Shadow.Begin(g);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            SceneRenderer.RenderShadowPass(g, res.Depth, res.Cache!, shadowVP);
            res.Shadow.End(g, (uint)fb);

            g.Viewport(0, 0, (uint)W, (uint)H);
            shadowFBO = res.Shadow.FBO;
        }

        var underwater = UnderwaterQuery.GetState(camPos);
        var postVolume = PostProcessVolume.GetActive();
        bool usePostFX = (postVolume != null || underwater != null) && res.PostProcess != null;
        bool useDeferred = GameRenderPipeline.UseDeferred(usePostFX);

        var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
        var fallbackPlanetSunDir = sunSD.LengthSquared() > 1e-8f
            ? SN.Vector3.Normalize(sunSD)
            : SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));

        GameRenderPipeline.UpdateLod(camPos);

        GPUTexture? finalSceneTex = null;
        if (useDeferred)
        {
            finalSceneTex = GameRenderPipeline.RenderDeferred(g, res, W, H,
                view, proj, camPos, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunSD, lightColorNorm, light, sky,
                skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw,
                postVolume, fallbackPlanetSunDir);
            _sceneFBO = res.SceneFbo;
            _sceneFBO_W = res.SceneW;
            _sceneFBO_H = res.SceneH;
        }
        else
        {
            if (usePostFX)
            {
                if (_sceneFBO == null) _sceneFBO = new GPUFramebuffer(g);
                if (_sceneFBO_W != W || _sceneFBO_H != H)
                {
                    _sceneFBO.SetupColorDepth(W, H);
                    _sceneFBO_W = W; _sceneFBO_H = H;
                }
                _sceneFBO.Bind();
                g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                Sky.RenderGPU(g, res.Sky!, res.FsQuad!, res.Cache!, view, proj,
                    skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);
            }

            GameRenderPipeline.RenderForwardWorld(g, res,
                view, proj, camPos, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunSD, lightColorNorm, light, sky, fallbackPlanetSunDir);
            finalSceneTex = usePostFX ? _sceneFBO?.ColorTexture : null;
        }

        if (res.Canvas != null && res.Cache != null)
        {
            var viewProj = view * proj;
            foreach (var wc in Core.Component.UI.Canvas.All)
            {
                if (wc.IsActiveAndEnabled && wc.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
                    res.Canvas.RenderWorldCanvas(wc, in viewProj, res.Cache);
            }
        }

        if (finalSceneTex != null && res.PostProcess != null)
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Disable(EnableCap.DepthTest);
            g.BindVertexArray(res.FsQuad!.VAO);
            SceneRenderer.ApplyPostProcessing(g, res.PostProcess, finalSceneTex, W, H,
                usePostFX ? postVolume : null, usePostFX ? underwater : null,
                usePostFX ? (float)Core.Time.time : 0f);
            g.BindVertexArray(0);
            g.Enable(EnableCap.DepthTest);
        }

        if (res.Particle != null && res.Cache != null)
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            SceneRenderer.RenderParticles(g, res.Particle, res.Cache, view, proj, W, H, overlayPass: true);
        }

        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)W, (uint)H);
        if (res.Canvas != null && res.Cache != null)
            res.Canvas.RenderOverlays(W, H, res.Cache);

        g.Flush();
        CleanupGLState(g, fb);
    }

    void CleanupGLState(GL g, int fb)
    {
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.UseProgram(0);
        g.BindVertexArray(0);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Disable(EnableCap.Blend);
        g.ActiveTexture(TextureUnit.Texture0);
        g.BindTexture(TextureTarget.Texture2D, 0);

        _frameWatch.Stop();
        _msFrameLast = _frameWatch.Elapsed.TotalMilliseconds;
        Ema(ref _msFrameEma, _msFrameLast, 0.18);
        _renderInFlight = false;
    }
    #endregion

    #region 2D HUD overlay
    public override void Render(DrawingContext ctx)
    {
        MaterialRebind.RepairScene();
        if (MaterialRebind.NeedsMoreFrames)
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

        base.Render(ctx);
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
        if (!_playing) return;
        Input.FeedKeyDown(KeyMap.FromAvalonia(e.Key));
    }

    void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (!_playing) return;
        Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
    }

    void RecenterPointer()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var mid = new Point(Bounds.Width * 0.5, Bounds.Height * 0.5);
        var screen = this.PointToScreen(mid);
        Input.WarpCursorScreen((int)screen.X, (int)screen.Y);
        _lastMouse = new SN.Vector2((float)mid.X, (float)mid.Y);
        _hasLastMouse = true;
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!_playing) return;
        Focus();
        var pt = e.GetCurrentPoint(this);
        var pos = e.GetPosition(this);
        Input.FeedMousePosition((float)pos.X, (float)pos.Y);
        Input.FeedViewportSize((float)Bounds.Width, (float)Bounds.Height);
        _lastMouse = new SN.Vector2((float)pos.X, (float)pos.Y);
        _hasLastMouse = true;
        if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
        if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
        if (pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;
        Input.PlayViewportCaptureActive = true;
        if (Input.PointerLock)
        {
            Cursor = new Cursor(StandardCursorType.None);
            RecenterPointer();
        }
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_playing) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Left);
        if (!pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Middle);
        if (!pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Right);
        if (ReferenceEquals(_capturedPointer, e.Pointer) && !Input.PointerLock)
        {
            try { e.Pointer.Capture(null); } catch { }
            _capturedPointer = null;
            Input.PlayViewportCaptureActive = false;
            Cursor = Cursor.Default;
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
        Input.FeedMousePosition(cur.X, cur.Y);
        if (Input.PointerLock)
            RecenterPointer();
    }
    #endregion

    #region Update / FixedUpdate
    void TickUpdate()
    {
        if (!_playing) return;

        // Process any deferred scene load queued by SceneManager.LoadScene()
        if (SceneManager.HasPendingLoad)
        {
            SceneManager.ProcessPendingLoad(
                callOnDestroyAll: () => SceneService.ForEachActiveBehavior(b => b.__OnDestroy()),
                clearRegistries: () =>
                {
                    PostProcessVolume.ClearAll();
                    Core.Component.UI.Canvas.ClearAll();
                    Core.Rendering.UI.UIEventSystem.Reset();
                    Input.ClearAll();
                },
                rebuildCaches: () =>
                {
                    _needsWarm = true;
                    _collidersWarm = false;
                },
                callAwakeStart: () =>
                {
                    _awakened = false; _started = false;
                });
        }

        if (!_awakened) { SceneService.ForEachActiveBehavior(b => b.__Awake()); _awakened = true; }
        if (!_started) { SceneService.ForEachActiveBehavior(b => b.__Start()); _started = true; }
        if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

        var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
        _updateWatch.Restart();
        if (dt > 0.05) dt = 0.05;
        Core.Time.BeginUpdate(dt);
        Input.NewFrame((float)dt);
        Input.PollHardwareHeldKeys();
        Input.PollPlayMouseButtons(_capturedPointer != null || IsPointerOver);
        Input.FeedViewportSize((float)Bounds.Width, (float)Bounds.Height);
        {
            int vpW = Math.Max(1, (int)Bounds.Width);
            int vpH = Math.Max(1, (int)Bounds.Height);
            UIEventSystem.ProcessEvents(vpW, vpH);
        }
        if (NetworkManager.IsActive)
            NetworkManager.Update();
        AudioManager.UpdateListenerTransform();
        SceneService.TickActiveBehaviors(Profiler.ScriptPhase.Update);
        SceneService.TickActiveBehaviors(Profiler.ScriptPhase.LateUpdate);
        Profiler.PublishScriptCosts();
        Input.EndFrame();
    }

    void TickFixedUpdate()
    {
        if (!_playing) return;
        if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

        double dt = _fixedWatch.IsRunning ? _fixedWatch.Elapsed.TotalSeconds : FIXED_DT;
        _fixedWatch.Restart();
        if (dt > 0.1) dt = 0.1;
        _fixedAccum += dt;
        if (_fixedAccum > 0.25) _fixedAccum = 0.25;
        while (_fixedAccum >= FIXED_DT)
        {
            Core.Time.BeginFixedUpdate(FIXED_DT);
            PhysicsCache.Tick();
            SceneService.TickActiveBehaviors(Profiler.ScriptPhase.FixedUpdate);
            _fixedAccum -= FIXED_DT;
        }
    }

    void WarmAllColliders()
    {
        if (_collidersWarm) return;
        foreach (var root in SceneService.Root)
            Traverse(root, b =>
            {
                if (b is Collider c)
                {
                    var t = c.GetType();
                    string[] names = { "EnsureReady", "EnsureBaked", "Bake", "Precompute", "Rebuild", "SyncFromTransform", "Warm" };
                    foreach (var n in names)
                    {
                        var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (m != null) { try { m.Invoke(c, null); } catch { } break; }
                    }
                }
            });
        _collidersWarm = true;
    }

    static void Traverse(GameObject go, Action<Behavior> a)
    {
        var behaviors = go.Behaviors;
        for (int i = 0; i < behaviors.Count; i++)
            a(behaviors[i]);
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
            Traverse(children[i], a);
    }
    #endregion
}
