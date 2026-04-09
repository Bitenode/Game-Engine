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
    private bool _isES = true;
    private ShaderProgram? _standardShader;
    private ShaderProgram? _depthShader;
    private ShaderProgram? _skyShader;
    private ShaderProgram? _terrainShader;
    private ShaderProgram? _particleShader;
    private ShaderProgram? _waterShader;
    private ShaderProgram? _postProcessShader;
    private FullscreenQuad? _fsQuad;
    private ResourceCache? _cache;
    private ShadowMapGPU? _shadow;
    private GPUFramebuffer? _sceneFBO;
    private int _sceneFBO_W, _sceneFBO_H;
    private CanvasRenderer? _canvasRenderer;
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
                InvalidateVisual();
            }
        };
        _fixedTimer.Tick += (_, __) => TickFixedUpdate();

        SceneService.Changed += () => { RebuildSceneCaches(); _needsWarm = true; _cache?.InvalidateAll(); InvalidateVisual(); };

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
            bool es = _glCtx.IsES;
            _isES = es;

            _standardShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.StandardVert, es),
                ShaderSources.Adapt(ShaderSources.StandardFrag, es));
            _depthShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.DepthOnlyVert, es),
                ShaderSources.Adapt(ShaderSources.DepthOnlyFrag, es));
            _skyShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.SkyVert, es),
                ShaderSources.Adapt(ShaderSources.SkyFrag, es));
            _terrainShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.TerrainVert, es),
                ShaderSources.Adapt(ShaderSources.TerrainFrag, es));
            _particleShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.ParticleVert, es),
                ShaderSources.Adapt(ShaderSources.ParticleFrag, es));
            _waterShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.WaterVert, es),
                ShaderSources.Adapt(ShaderSources.WaterFrag, es));
            _postProcessShader = new ShaderProgram(g,
                ShaderSources.Adapt(ShaderSources.PostProcessVert, es),
                ShaderSources.Adapt(ShaderSources.PostProcessFrag, es));
            _fsQuad = new FullscreenQuad(g);
            _cache = new ResourceCache(g);
            GpuCompressionCaps.Initialize(g);
            _shadow = new ShadowMapGPU(g, 1024, 1024);
            _canvasRenderer = new CanvasRenderer(g, es);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerView] GL init failed: {ex}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _sceneFBO?.Dispose(); _sceneFBO = null; _sceneFBO_W = 0; _sceneFBO_H = 0;
        _canvasRenderer?.Dispose();
        _canvasRenderer = null;
        _postProcessShader?.Dispose(); _postProcessShader = null;
        _waterShader?.Dispose(); _waterShader = null;
        _particleShader?.Dispose(); _particleShader = null;
        _terrainShader?.Dispose();
        _shadow?.Dispose();
        _cache?.Dispose();
        _fsQuad?.Dispose();
        _skyShader?.Dispose();
        _depthShader?.Dispose();
        _standardShader?.Dispose();
        _glCtx?.Dispose();
        _glCtx = null;
        base.OnOpenGlDeinit(gl);
    }

    static void WalkTreeLOD(GameObject go, SN.Vector3 cam)
    {
        foreach (var b in go.Behaviors)
            if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTreeLOD(c, cam);
    }

    static void WalkTerrainLOD(GameObject go, SN.Vector3 cam)
    {
        foreach (var b in go.Behaviors)
            if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTerrainLOD(c, cam);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_glCtx == null || _standardShader == null || _skyShader == null || _fsQuad == null || _cache == null)
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

        double scaling = VisualRoot?.RenderScaling ?? 1.0;
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

        // --- SKY ---
        Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
            skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);

        // --- SHADOW MAP PASS ---
        SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
        GPUFramebuffer? shadowFBO = null;
        if (_shadow != null && _depthShader != null)
        {
            var sunShineDir = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            SN.Matrix4x4.Invert(view, out var invV);
            var camFwd = new SN.Vector3(-invV.M31, -invV.M32, -invV.M33);
            var sceneCenter = camPos + camFwd * 12f;
            float sceneRadius = 50f;
            shadowVP = ShadowMapGPU.BuildDirectionalLightVP(sunShineDir, sceneCenter, sceneRadius);
            _shadow.LightVP = shadowVP;

            _shadow.Begin(g);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            SceneRenderer.RenderShadowPass(g, _depthShader, _cache!, shadowVP);
            _shadow.End(g, (uint)fb);

            g.Viewport(0, 0, (uint)W, (uint)H);
            shadowFBO = _shadow.FBO;
        }

        // --- UNDERWATER DETECTION ---
        var underwater = UnderwaterQuery.GetState(camPos);

        // --- POST-PROCESSING FBO setup ---
        var postVolume = PostProcessVolume.GetActive();
        bool usePostFX = (postVolume != null || underwater != null) && _postProcessShader != null;

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

            Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
                skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);
        }

        // Terrain & Tree LOD
        TerrainStreamer.SyncAll(camPos);
        foreach (var root in SceneService.Root) WalkTerrainLOD(root, camPos);
        foreach (var root in SceneService.Root) WalkTreeLOD(root, camPos);

        // --- SCENE ---
        var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
        SceneRenderer.RenderGPU(g, _standardShader!, _depthShader!, _cache,
            view, proj,
            SN.Vector3.Normalize(-L), DiffuseK, Ambient,
            lightIsPoint, lightPosW, lightRange,
            shadowFBO, shadowVP, camPos, sunSD,
            terrainShader: _terrainShader,
            isES: _isES,
            lightColor: lightColorNorm);

        // --- WATER ---
        if (_waterShader != null)
        {
            var skyC = _sky != null
                ? new SN.Vector3(_sky.Top.R / 255f, _sky.Top.G / 255f, _sky.Top.B / 255f)
                : new SN.Vector3(0.5f, 0.6f, 0.8f);
            SceneRenderer.RenderWater(g, _waterShader, _cache, view, proj,
                SN.Vector3.Normalize(-L), Ambient, DiffuseK, camPos, skyC);
        }

        // --- PARTICLES ---
        if (_particleShader != null)
            SceneRenderer.RenderParticles(g, _particleShader, _cache, view, proj);

        // World-space UI (same stage as GameView — before post blit when using scene FBO)
        if (_canvasRenderer != null && _cache != null)
        {
            var viewProj = view * proj;
            foreach (var wc in Core.Component.UI.Canvas.All)
            {
                if (wc.IsActiveAndEnabled && wc.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
                    _canvasRenderer.RenderWorldCanvas(wc, in viewProj, _cache);
            }
        }

        // --- POST-PROCESSING BLIT ---
        if (usePostFX && _sceneFBO?.ColorTexture != null)
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Disable(EnableCap.DepthTest);

            g.BindVertexArray(_fsQuad!.VAO);
            SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, _sceneFBO.ColorTexture, W, H,
                postVolume, underwater, (float)Core.Time.time);
            g.BindVertexArray(0);

            g.Enable(EnableCap.DepthTest);

            g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFBO.Handle);
            g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
            g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        }

        // Screen-space overlay UI (main menu, etc.) — must draw after final color is on the Avalonia FB
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)W, (uint)H);
        if (_canvasRenderer != null && _cache != null)
            _canvasRenderer.RenderOverlays(W, H, _cache);

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
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

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

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!_playing) return;
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
        if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
        if (pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_playing) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Left);
        if (!pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Middle);
        if (!pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Right);
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
                callOnDestroyAll: () => ForEachBehavior(b => b.__OnDestroy()),
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

        if (!_awakened) { ForEachBehavior(b => b.__Awake()); _awakened = true; }
        if (!_started) { ForEachBehavior(b => b.__Start()); _started = true; }
        if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

        var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
        _updateWatch.Restart();
        if (dt > 0.05) dt = 0.05;
        Core.Time.BeginUpdate(dt);
        Input.NewFrame((float)dt);
        Input.FeedViewportSize((float)Bounds.Width, (float)Bounds.Height);
        {
            int vpW = Math.Max(1, (int)Bounds.Width);
            int vpH = Math.Max(1, (int)Bounds.Height);
            UIEventSystem.ProcessEvents(vpW, vpH);
        }
        if (NetworkManager.IsActive)
            NetworkManager.Update();
        ForEachBehavior(b => b.__Update());
        ForEachBehavior(b => b.__LateUpdate());
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
            ForEachBehavior(b => b.__FixedUpdate());
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

    static void ForEachBehavior(Action<Behavior> a) { foreach (var r in SceneService.Root) Traverse(r, a); }
    static void Traverse(GameObject go, Action<Behavior> a)
    { foreach (var b in go.Behaviors) a(b); foreach (var c in go.Children) Traverse(c, a); }
    #endregion
}
