#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;
using Game_Engine.Core.Physics;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using SN = System.Numerics;

namespace Game_Engine.Views
{
    public class GameView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
    {
        // OpenGlControlBase renders via composition, not Avalonia visuals, so the control
        // isn't hit-testable by default.  Implement ICustomHitTest so pointer events work
        // over the entire surface.
        public bool HitTest(Point point) => true;


        public static readonly StyledProperty<GamePanel.GameState> StateProperty =
            AvaloniaProperty.Register<GameView, GamePanel.GameState>(
                nameof(State), GamePanel.GameState.Stopped);

        public GamePanel.GameState State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        // Tracks whether any GameView instance is actively running Play mode.
        private static int s_playingViewCount;
        public static bool IsAnyViewPlaying => System.Threading.Volatile.Read(ref s_playingViewCount) > 0;
        public static event Action? AnyPlayingStateChanged;

        #region GPU Resources
        private GLContext? _glCtx;
        private ShaderProgram? _standardShader;
        private ShaderProgram? _depthShader;
        private ShaderProgram? _skyShader;
        private ShaderProgram? _gridShader;
        private ShaderProgram? _terrainShader;
        private ShaderProgram? _particleShader;
        private ShaderProgram? _waterShader;
        private ShaderProgram? _planetTerrainShader;
        private ShaderProgram? _planetWaterShader;
        private ShaderProgram? _planetAtmosphereShader;
        private ShaderProgram? _planetCloudShader;
        private ShaderProgram? _postProcessShader;
        private FullscreenQuad? _fsQuad;
        private ResourceCache? _cache;
        private ShadowMapGPU? _shadow;
        private GPUFramebuffer? _sceneFBO;
        private int _sceneFBO_W, _sceneFBO_H;

        // Canvas UI renderer
        private Game_Engine.Core.Rendering.UI.CanvasRenderer? _canvasRenderer;

        // Deferred rendering pipeline
        private ShaderProgram? _gbufferShader;
        private ShaderProgram? _deferredLightShader;
        private ShaderProgram? _ssaoShader;
        private ShaderProgram? _ssaoBlurShader;
        private ShaderProgram? _ssrShader;
        private ShaderProgram? _volFogShader;
        private ShaderProgram? _taaResolveShader;
        private ShaderProgram? _depthCopyShader;
        private GPUFramebuffer? _gbufferFBO;
        private GPUFramebuffer? _ssaoFBO;
        private GPUFramebuffer? _ssaoBlurFBO;
        private GPUFramebuffer? _ssrFBO;
        private GPUFramebuffer? _volFogFBO;
        private GPUFramebuffer? _taaHistoryFbo;
        private GPUFramebuffer? _taaTempFbo;
        private int _gbufferW, _gbufferH;
        private SN.Matrix4x4 _prevViewProj = SN.Matrix4x4.Identity;
        private bool _taaResetHistory = true;
        private SN.Vector3 _taaLook;
        private bool _hasTaaLook;
        private bool _taaCamInSolid;
        private int _taaFrameCounter;
        private TiledLightTextureSystem? _tiledLights;
        #endregion

        #region Clocks & State
        readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(16.666) };
        readonly DispatcherTimer _fixedTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        readonly Stopwatch _updateWatch = new();
        readonly Stopwatch _fixedWatch = new();
        readonly Stopwatch _frameWatch = new();
        readonly Stopwatch _fpsTick = new();
        readonly Stopwatch _fpsWindow = new();

        double _msFrameLast, _msFrameEma;
        int _fpsFrames;
        double _fpsDisplay;

        public static readonly DirectProperty<GameView, string> FpsTextProperty =
            AvaloniaProperty.RegisterDirect<GameView, string>(nameof(FpsText), o => o.FpsText);

        private string _fpsText = "0 FPS";
        public string FpsText
        {
            get => _fpsText;
            private set => SetAndRaise(FpsTextProperty, ref _fpsText, value);
        }

        bool _awakened, _started, _collidersWarm, _needsWarm;
        bool _registeredAsPlaying;

        const double FIXED_DT = 1.0 / 60.0;
        double _fixedAccum = 0.0;

        bool _mouseLook;
        SN.Vector2 _lastMouse;
        bool _hasLastMouse;
        IPointer? _capturedPointer;

        string? _playSnapshotPath;
        /// <summary>Project .scene path before play snapshot overwrote <see cref="SceneService.CurrentScenePath"/>; restored after load.</summary>
        string? _scenePathBeforePlay;
        // Cache material textures across play snapshot save/restore so they survive serialization
        private Dictionary<string, List<object>>? _snapshotMaterialTextures;

        Skybox? _sky;
        Light? _light;
        readonly List<Camera> _cams = new(4);

        static readonly Color FallbackSkyTop = Color.FromRgb(0x1f, 0x1f, 0x1f);
        static readonly Color FallbackSkyBot = Color.FromRgb(0x0a, 0x0a, 0x0a);
        static readonly Typeface HudTypeface = new("Segoe UI");
        static readonly IBrush HudText = Brushes.White;
        static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        // LOD throttling: updating all terrain/tree/planet LOD every rendered frame is expensive.
        // We update at a fixed cadence or when the camera moves enough.
        const double LOD_UPDATE_INTERVAL_SEC = 0.10; // 10 Hz (flat terrain / trees)
        const double PLANET_LOD_UPDATE_INTERVAL_SEC = 0.10; // 10 Hz planet streaming
        const double PLAY_PLANET_LOD_UPDATE_INTERVAL_SEC = 0.40;
        const float LOD_UPDATE_MOVE_THRESHOLD = 2.0f;
        const float PLANET_LOD_MOVE_THRESHOLD = 2.0f;
        const float PLAY_PLANET_LOD_MOVE_THRESHOLD = 18f;
        const float PLANET_FAST_APPROACH_SPEED = 40f;
        const double MAX_RENDER_DT_SEC = 0.10;
        const double RENDER_GAP_LOD_PAUSE_SEC = 0.25;
        const int PLAYMODE_MAX_PLANET_LOD_DEPTH = 5;
        double _lodAccumSec;
        double _planetLodAccumSec;
        SN.Vector3 _lastLodCamPos = new(float.NaN);
        SN.Vector3 _lastPlanetLodCamPos = new(float.NaN);

        // Shadow throttling: render shadow map less frequently and reuse cached map.
        const double SHADOW_UPDATE_INTERVAL_SEC = 0.10; // 10 Hz
        const float SHADOW_UPDATE_MOVE_THRESHOLD = 1.5f;
        double _shadowAccumSec;
        SN.Vector3 _lastShadowCamPos = new(float.NaN);
        bool _hasShadowMap;
        int _gpuCacheMaintainCounter;
        bool _underwaterFxLatch;
        UnderwaterState? _underwaterFxCached;
        #endregion

        // Render gating: prevent RequestNextFrameRendering() from piling up.
        // Avalonia's OpenGlControlBase compositing is expensive if frames queue.
        // We only request a new render after OnOpenGlRender has completed the previous one.
        private volatile bool _renderInFlight;
        TopLevel? _playKeyHost;
        TopLevel? _playPointerHost;

        public GameView()
        {
            ClipToBounds = true;

            _updateTimer.Tick += (_, __) =>
            {
                TickUpdate();
                // Game logic runs every 16ms regardless, but we only request
                // a render if the previous one has completed. This prevents
                // Avalonia's expensive compositing from queueing up.
                if (!_renderInFlight)
                {
                    _renderInFlight = true;
                    RequestNextFrameRendering();
                }
            };
            _fixedTimer.Interval = TimeSpan.FromMilliseconds(8);
            _fixedTimer.Tick += (_, __) => TickFixedUpdate();

            SceneService.Changed += () =>
            {
                if (State == GamePanel.GameState.Playing)
                    return;
                RebuildSceneCaches();
                _needsWarm = true;
                _cache?.InvalidateAll();
                RequestNextFrameRendering();
            };

            // Full scene replacement: request a full GPU cache flush on the next render pass
            SceneService.SceneReplaced += () =>
            {
                if (_cache != null) _cache.FlushRequested = true;
                SceneRenderer.ResetBiomeTexDebug();
                Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering,
                    Avalonia.Threading.DispatcherPriority.Render);
            };

            StateProperty.Changed.AddClassHandler<GameView>((s, e) => s.OnStateChanged());

            Focusable = true;
            AttachedToVisualTree += (_, __) => Focus();
            // Don't kill mouse look on LostFocus — layout cascades from FPS text updates
            // or property changes can cause transient focus shifts.  Mouse look is released
            // by Escape or when the game stops (OnStateChanged).
            DetachedFromVisualTree += (_, __) => ExitLookAndClear();

            // FPS toolbar text update via timer (avoids layout cascades during render)
            var fpsDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            fpsDisplayTimer.Tick += (_, __) =>
            {
                if (!_fpsWindow.IsRunning || _fpsWindow.ElapsedMilliseconds < 400) return;
                FpsText = $"{_fpsDisplay:F0} FPS";
            };
            fpsDisplayTimer.Start();

            AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            // Use Tunnel routing to guarantee events arrive even if the OpenGL base
            // class marks them as handled during the bubble phase.
            AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            RebuildSceneCaches();
            _fpsTick.Restart();
            _fpsWindow.Restart();
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

                Debug.WriteLine($"[GameView] GL context: {_glCtx.VersionString} (ES={es})");

                _standardShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.StandardVert, es),
                    ShaderSources.Adapt(ShaderSources.StandardFrag, es));
                _depthShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.DepthOnlyVert, es),
                    ShaderSources.Adapt(ShaderSources.DepthOnlyFrag, es));
                _skyShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.SkyVert, es),
                    ShaderSources.Adapt(ShaderSources.SkyFrag, es));
                _gridShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.GridVert, es),
                    ShaderSources.Adapt(ShaderSources.GridFrag, es));
                _terrainShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.TerrainVert, es),
                    ShaderSources.Adapt(ShaderSources.TerrainFrag, es));
                _particleShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.ParticleVert, es),
                    ShaderSources.Adapt(ShaderSources.ParticleFrag, es));
                _waterShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.WaterVert, es),
                    ShaderSources.Adapt(ShaderSources.WaterFrag, es));
                _planetTerrainShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PlanetTerrainVert, es),
                    ShaderSources.Adapt(ShaderSources.PlanetTerrainFrag, es));
                _planetWaterShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PlanetWaterVert, es),
                    ShaderSources.Adapt(ShaderSources.PlanetWaterFrag, es));
                _planetAtmosphereShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PlanetAtmosphereVert, es),
                    ShaderSources.Adapt(ShaderSources.PlanetAtmosphereFrag, es));
                _planetCloudShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PlanetCloudsVert, es),
                    ShaderSources.Adapt(ShaderSources.PlanetCloudsFrag, es));
                _postProcessShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PostProcessVert, es),
                    ShaderSources.Adapt(ShaderSources.PostProcessFrag, es));

                // Deferred rendering shaders
                _gbufferShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.GBufferVert, es),
                    ShaderSources.Adapt(ShaderSources.GBufferFrag, es));
                _deferredLightShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.DeferredLightingVert, es),
                    ShaderSources.Adapt(ShaderSources.DeferredLightingFrag, es));
                _ssaoShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.SSAOVert, es),
                    ShaderSources.Adapt(ShaderSources.SSAOFrag, es));
                _ssaoBlurShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.SSAOVert, es),
                    ShaderSources.Adapt(ShaderSources.SSAOBlurFrag, es));
                _ssrShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.SSRVert, es),
                    ShaderSources.Adapt(ShaderSources.SSRFrag, es));
                _volFogShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.VolumetricFogVert, es),
                    ShaderSources.Adapt(ShaderSources.VolumetricFogFrag, es));
                _taaResolveShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.PostProcessVert, es),
                    ShaderSources.Adapt(ShaderSources.TaaResolveFrag, es));
                _depthCopyShader = new ShaderProgram(g,
                    ShaderSources.Adapt(ShaderSources.BlitVert, es),
                    ShaderSources.Adapt(ShaderSources.DepthCopyFrag, es));

                _fsQuad = new FullscreenQuad(g);
                _cache = new ResourceCache(g);
                GpuCompressionCaps.Initialize(g);
                _tiledLights = new TiledLightTextureSystem(g);
                _shadow = new ShadowMapGPU(g, 768, 768);

                _canvasRenderer = new Core.Rendering.UI.CanvasRenderer(g, es);

                Debug.WriteLine($"[GameView] OpenGL initialized OK — all shaders compiled (deferred pipeline).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameView] GL init failed: {ex}");
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            UpdatePlayingRegistration(isPlaying: false);
            _canvasRenderer?.Dispose(); _canvasRenderer = null;
            _sceneFBO?.Dispose(); _sceneFBO = null; _sceneFBO_W = 0; _sceneFBO_H = 0;
            // Deferred pipeline cleanup
            _gbufferFBO?.Dispose(); _gbufferFBO = null; _gbufferW = 0; _gbufferH = 0;
            _ssaoFBO?.Dispose(); _ssaoFBO = null;
            _ssaoBlurFBO?.Dispose(); _ssaoBlurFBO = null;
            _ssrFBO?.Dispose(); _ssrFBO = null;
            _volFogFBO?.Dispose(); _volFogFBO = null;
            _taaHistoryFbo?.Dispose(); _taaHistoryFbo = null;
            _taaTempFbo?.Dispose(); _taaTempFbo = null;
            _tiledLights?.Dispose(); _tiledLights = null;
            _taaResolveShader?.Dispose(); _taaResolveShader = null;
            _depthCopyShader?.Dispose(); _depthCopyShader = null;
            _ssrShader?.Dispose(); _ssrShader = null;
            _volFogShader?.Dispose(); _volFogShader = null;
            _ssaoBlurShader?.Dispose(); _ssaoBlurShader = null;
            _ssaoShader?.Dispose(); _ssaoShader = null;
            _deferredLightShader?.Dispose(); _deferredLightShader = null;
            _gbufferShader?.Dispose(); _gbufferShader = null;

            _postProcessShader?.Dispose(); _postProcessShader = null;
            _planetCloudShader?.Dispose(); _planetCloudShader = null;
            _planetAtmosphereShader?.Dispose(); _planetAtmosphereShader = null;
            _waterShader?.Dispose(); _waterShader = null;
            _particleShader?.Dispose(); _particleShader = null;
            _terrainShader?.Dispose();
            _shadow?.Dispose();
            _cache?.Dispose();
            _fsQuad?.Dispose();
            _gridShader?.Dispose();
            _skyShader?.Dispose();
            _depthShader?.Dispose();
            _standardShader?.Dispose();
            _glCtx?.Dispose();
            _glCtx = null;
            _hasShadowMap = false;
            _shadowAccumSec = 0.0;
            _lastShadowCamPos = new SN.Vector3(float.NaN);
            base.OnOpenGlDeinit(gl);
        }

        static void WalkTreeLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            if (go.HideInHierarchy) return;
            foreach (var b in go.Behaviors)
                if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTreeLOD(c, cam);
        }

        static void WalkMeshLodGroup(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is MeshLodGroup mg && mg.Enabled) mg.UpdateLOD(cam);
            foreach (var c in go.Children) WalkMeshLodGroup(c, cam);
        }

        static void WalkTerrainLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTerrainLOD(c, cam);
        }

        static void CollectPlanetProfilerStats(out int planetCount, out int chunkCount, out int activeJobs, out int pendingJobs)
        {
            planetCount = 0;
            chunkCount = 0;
            activeJobs = 0;
            pendingJobs = 0;

            var planets = PlanetTerrain.ActivePlanets;
            for (int i = 0; i < planets.Count; i++)
            {
                var p = planets[i];
                if (p == null || !p.IsActiveAndEnabled) continue;
                planetCount++;
                chunkCount += p.ActiveChunkCount;
                activeJobs += p.ActiveGenerationJobs;
                pendingJobs += p.PendingMeshJobs;
            }
        }

        // Shadows can be disabled at runtime to boost framerate on weak GPUs
        public static readonly StyledProperty<bool> ShowShadowsProperty =
            AvaloniaProperty.Register<GameView, bool>(nameof(ShowShadows), defaultValue: true);
        public bool ShowShadows { get => GetValue(ShowShadowsProperty); set => SetValue(ShowShadowsProperty, value); }

        // Section timing for HUD diagnostics
        private double _tSetup, _tShadow, _tScene;

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_glCtx == null || _standardShader == null || _skyShader == null || _fsQuad == null || _cache == null)
                return;

            if (!SceneRenderer.TryBeginViewRender())
            {
                _renderInFlight = false;
                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
                return;
            }
            try
            {
                SceneRenderer.SkipPlanetVegetationDraws = false;
                var g = _glCtx.GL;

            // Flush any GL errors accumulated by the other view's rendering.
            while (g.GetError() != GLEnum.NoError) { }

            // Full GPU cache flush requested (e.g. after loading a new scene).
            if (_cache.FlushRequested)
            {
                _cache.FlushAll();
            }

            _frameWatch.Restart();
            var sec = Stopwatch.StartNew();

            double rawDt = _fpsTick.IsRunning ? _fpsTick.Elapsed.TotalSeconds : 0.0;
            _fpsTick.Restart();
            bool renderGap = rawDt > RENDER_GAP_LOD_PAUSE_SEC;
            double dt = Math.Min(Math.Max(0.0, rawDt), MAX_RENDER_DT_SEC);
            UpdateFps(dt);

            // Wind system update
            WindSystem.Update((float)Math.Min(dt, 0.1));

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            double Wdip = Math.Max(1.0, Bounds.Width);
            double Hdip = Math.Max(1.0, Bounds.Height);
            int W = Math.Max(1, (int)(Wdip * scaling));
            int H = Math.Max(1, (int)(Hdip * scaling));

            // Bind Avalonia's framebuffer and reset essential GL state.
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            g.Disable(EnableCap.Blend);
            g.ColorMask(true, true, true, true);
            g.DepthMask(true);

            // --- EDITOR MODE: dark screen (paused still shows the frozen play frame) ---
            if (State == GamePanel.GameState.Stopped)
            {
                g.ClearColor(0.07f, 0.08f, 0.09f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                // IMPORTANT: Clean up GL state even on early return.
                // Both views share the same GL context; dirty state here
                // can permanently corrupt the SceneView's rendering.
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
                return;
            }

            Profiler.Begin("Render");

            // --- SCENE SETUP ---
            var sky = _sky;
            var skyTop = sky?.Top ?? FallbackSkyTop;
            var skyBot = sky?.Bottom ?? FallbackSkyBot;
            Texture2D? skyTex = sky?.Texture;
            float skyMix = sky != null ? Math.Clamp(sky.TextureBlend, 0f, 1f) : 0f;
            float skyYaw = sky?.Yaw ?? 0f;

            // Sun direction (toward the sun): from Yaw + SunElevation
            SN.Vector3? sunDir = null;
            if (sky != null)
            {
                float elevRad = Math.Clamp(sky.SunElevation, 1f, 89f) * MathF.PI / 180f;
                float yawRad  = sky.Yaw * MathF.PI / 180f;
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
            var fallbackPlanetSunDir = SN.Vector3.Normalize(-L);
            if (fallbackPlanetSunDir.LengthSquared() < 1e-5f)
                fallbackPlanetSunDir = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));

            // Camera
            var cams = _cams;
            Camera? cam = CameraService.MainOrFirst();
            if (cam == null || !cam.Enabled)
            {
                for (int i = 0; i < cams.Count; i++)
                {
                    if (cams[i] != null && cams[i].Enabled) { cam = cams[i]; break; }
                }
            }

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

            // Camera position
            SN.Matrix4x4.Invert(view, out var invView);
            var camPos = new SN.Vector3(invView.M41, invView.M42, invView.M43);

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
                    skyMix = 0f;
                    var top = atmo.ZenithTint;
                    var bot = atmo.HorizonTint;
                    skyTop = Color.FromRgb((byte)Math.Clamp((int)(top.X * 255f), 0, 255), (byte)Math.Clamp((int)(top.Y * 255f), 0, 255), (byte)Math.Clamp((int)(top.Z * 255f), 0, 255));
                    skyBot = Color.FromRgb((byte)Math.Clamp((int)(bot.X * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Y * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Z * 255f), 0, 255));
                    break;
                }
            }

            // Match planet terrain: inside an atmosphere shell, use PlanetAtmosphere ambient for deferred + forward overlays.
            SceneRenderer.TryApplyPlanetAtmosphereAmbient(camPos, light, ref Ambient);

            // --- CLEAR ---
            g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // --- SKY ---
            Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
                skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);

            _tSetup = sec.Elapsed.TotalMilliseconds; sec.Restart();

            // --- SHADOW MAP PASS (skippable) ---
            SN.Matrix4x4 shadowVP = SN.Matrix4x4.Identity;
            GPUFramebuffer? shadowFBO = null;
            if (ShowShadows && _shadow != null && _depthShader != null)
            {
                var sunShineDir = fallbackPlanetSunDir;

                SN.Matrix4x4.Invert(view, out var invV);
                var camFwd = new SN.Vector3(-invV.M31, -invV.M32, -invV.M33);
                var sceneCenter = camPos + camFwd * 12f;
                float sceneRadius = 50f;

                _shadowAccumSec += Math.Max(0.0, dt);
                bool updateShadow = _shadowAccumSec >= SHADOW_UPDATE_INTERVAL_SEC || !_hasShadowMap;
                if (!float.IsNaN(_lastShadowCamPos.X))
                {
                    var d = camPos - _lastShadowCamPos;
                    if (d.LengthSquared() >= SHADOW_UPDATE_MOVE_THRESHOLD * SHADOW_UPDATE_MOVE_THRESHOLD)
                        updateShadow = true;
                }
                else
                {
                    updateShadow = true;
                }

                if (updateShadow)
                {
                    _shadowAccumSec = 0.0;
                    _lastShadowCamPos = camPos;
                    shadowVP = ShadowMapGPU.BuildDirectionalLightVP(sunShineDir, sceneCenter, sceneRadius);
                    _shadow.LightVP = shadowVP;

                    _shadow.Begin(g);
                    g.Enable(EnableCap.DepthTest);
                    g.DepthFunc(DepthFunction.Less);
                    SceneRenderer.RenderShadowPass(g, _depthShader, _cache!, shadowVP);
                    _shadow.End(g, (uint)fb);

                    // Restore main viewport after shadow pass
                    g.Viewport(0, 0, (uint)W, (uint)H);
                    _hasShadowMap = true;
                }
                else
                {
                    shadowVP = _shadow.LightVP;
                }
                shadowFBO = _shadow.FBO;
            }

            _tShadow = sec.Elapsed.TotalMilliseconds; sec.Restart();

            // --- UNDERWATER DETECTION ---
            // Surface swim stays dry. Planet post only once the head is under
            // the water table — not while floating on the crust waterline.
            var rawUnderwater = UnderwaterQuery.GetState(camPos);
            if (!UnderwaterQuery.PlanetSwimFxActive())
                rawUnderwater = null;
            if (rawUnderwater.HasValue)
            {
                if (rawUnderwater.Value.Depth >= 0.28f)
                    _underwaterFxLatch = true;
                else if (rawUnderwater.Value.Depth <= 0.10f)
                    _underwaterFxLatch = false;
                _underwaterFxCached = rawUnderwater;
            }
            else
            {
                _underwaterFxLatch = false;
                _underwaterFxCached = null;
            }
            var underwater = _underwaterFxLatch ? _underwaterFxCached : null;

            // --- POST-PROCESSING setup ---
            var postVolume = PostProcessVolume.GetActive();
            bool usePostFX = (postVolume != null || underwater != null) && _postProcessShader != null;
            bool useSSAO = postVolume?.SSAOEnabled == true;
            bool useSSR = postVolume?.SSREnabled == true;
            double planetLodMs = 0.0;

            bool shouldUpdateLod = false;
            _lodAccumSec += Math.Max(0.0, dt);
            if (_lodAccumSec >= LOD_UPDATE_INTERVAL_SEC)
                shouldUpdateLod = true;

            if (!float.IsNaN(_lastLodCamPos.X))
            {
                var d = camPos - _lastLodCamPos;
                if (d.LengthSquared() >= LOD_UPDATE_MOVE_THRESHOLD * LOD_UPDATE_MOVE_THRESHOLD)
                    shouldUpdateLod = true;
            }
            else
            {
                shouldUpdateLod = true;
            }

            bool shouldUpdatePlanetLod = false;
            _planetLodAccumSec += Math.Max(0.0, dt);
            double planetLodInterval = State == GamePanel.GameState.Playing
                ? PLAY_PLANET_LOD_UPDATE_INTERVAL_SEC
                : PLANET_LOD_UPDATE_INTERVAL_SEC;
            float planetLodMove = State == GamePanel.GameState.Playing
                ? PLAY_PLANET_LOD_MOVE_THRESHOLD
                : PLANET_LOD_MOVE_THRESHOLD;
            if (_planetLodAccumSec >= planetLodInterval)
                shouldUpdatePlanetLod = true;
            if (!float.IsNaN(_lastPlanetLodCamPos.X))
            {
                var pd = camPos - _lastPlanetLodCamPos;
                float pDist = pd.Length();
                if (pDist >= planetLodMove)
                    shouldUpdatePlanetLod = true;
                float elapsed = Math.Max(1e-3f, (float)_planetLodAccumSec);
                if (pDist / elapsed >= PLANET_FAST_APPROACH_SPEED)
                    shouldUpdatePlanetLod = true;
            }
            else
            {
                shouldUpdatePlanetLod = true;
            }

            TerrainStreamer.SyncAll(camPos);

            if (shouldUpdateLod)
            {
                var lodSw = Stopwatch.StartNew();
                _lodAccumSec = 0.0;
                _lastLodCamPos = camPos;

                foreach (var root in SceneService.Root) WalkTerrainLOD(root, camPos);
                foreach (var root in SceneService.Root) WalkTreeLOD(root, camPos);
                foreach (var root in SceneService.Root) WalkMeshLodGroup(root, camPos);
                lodSw.Stop();
                planetLodMs = lodSw.Elapsed.TotalMilliseconds;
            }

            if (shouldUpdatePlanetLod)
            {
                _planetLodAccumSec = 0.0;
                _lastPlanetLodCamPos = camPos;
                bool allowPlanetLodChanges = !renderGap;
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config != null)
                        planet.Config.MaxLodDepth = Math.Clamp(planet.Config.MaxLodDepth, 4, PLAYMODE_MAX_PLANET_LOD_DEPTH);
                    planet?.RefreshLodAroundCamera(camPos, allowPlanetLodChanges);
                }
            }

            var sunSD = fallbackPlanetSunDir;
            bool isES = _glCtx.IsES;

            bool useDeferred = ProjectRenderingSettings.UseDeferredRendering;
            bool useTaa = postVolume?.TAAEnabled == true && useDeferred;
            if (cam != null && cam.InvalidateTemporalHistory)
            {
                _taaResetHistory = true;
                cam.InvalidateTemporalHistory = false;
            }
            bool camInSolid = CameraInsidePlanetSolid(camPos);
            if (camInSolid || _taaCamInSolid)
                _taaResetHistory = true;
            _taaCamInSolid = camInSolid;
            var camLook = new SN.Vector3(-invView.M31, -invView.M32, -invView.M33);
            if (camLook.LengthSquared() > 1e-8f)
            {
                camLook = SN.Vector3.Normalize(camLook);
                if (useTaa && _hasTaaLook && SN.Vector3.Dot(_taaLook, camLook) < 0.93f)
                    _taaResetHistory = true;
                _taaLook = camLook;
                _hasTaaLook = true;
            }
            if (useTaa)
            {
                _taaFrameCounter++;
                int fi = (_taaFrameCounter % 16) + 1;
                float jx = (TaaHalton(fi, 2) - 0.5f) * 2f / Math.Max(1, W);
                float jy = (TaaHalton(fi, 3) - 0.5f) * 2f / Math.Max(1, H);
                proj.M13 += jx;
                proj.M23 += jy;
            }
            SN.Matrix4x4.Invert(view * proj, out var invVpCurr);
            SN.Matrix4x4 prevVp = _taaResetHistory ? (view * proj) : _prevViewProj;

            GPUTexture? finalSceneTex = null;
            double planetRenderMs = 0;

            if (!useDeferred)
            {
                _taaResetHistory = true;
                if (usePostFX && _postProcessShader != null)
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

                var planetRenderSwF = Stopwatch.StartNew();
                SceneRenderer.RenderGPU(g, _standardShader!, _depthShader!, _cache!,
                    view, proj,
                    SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadowFBO, shadowVP, camPos, sunSD,
                    terrainShader: _terrainShader, isES: isES,
                    lightColor: lightColorNorm);

                if (_planetTerrainShader != null)
                {
                    foreach (var planet in PlanetTerrain.ActivePlanets)
                    {
                        if (planet?.Config == null) continue;
                        var tp = planet.gameObject?.Transform?.Position;
                        var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                        var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                        SceneRenderer.RenderPlanetTerrain(g, _planetTerrainShader, _cache!,
                            view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                            pc, shadowFBO, shadowVP);
                    }
                }
                if (_waterShader != null)
                {
                    var skyC = _sky != null
                        ? new SN.Vector3(_sky.Top.R / 255f, _sky.Top.G / 255f, _sky.Top.B / 255f)
                        : new SN.Vector3(0.5f, 0.6f, 0.8f);
                    SceneRenderer.RenderWater(g, _waterShader, _cache!, view, proj,
                        SN.Vector3.Normalize(-L), Ambient, DiffuseK, camPos, skyC);
                }
                if (_planetAtmosphereShader != null)
                {
                    foreach (var planet in PlanetTerrain.ActivePlanets)
                    {
                        if (planet?.Config == null) continue;
                        var tp = planet.gameObject?.Transform?.Position;
                        var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                        var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                        SceneRenderer.RenderPlanetAtmosphere(g, _planetAtmosphereShader, _cache!,
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
                        var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                        SceneRenderer.RenderPlanetClouds(g, _planetCloudShader, _cache!,
                            view, proj, planet, atmo, camPos, pc, (float)Core.Time.time);
                    }
                }
                if (_planetWaterShader != null)
                {
                    foreach (var planet in PlanetTerrain.ActivePlanets)
                    {
                        if (planet?.Config == null) continue;
                        var tp = planet.gameObject?.Transform?.Position;
                        var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                        var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                        SceneRenderer.RenderPlanetWater(g, _planetWaterShader, _cache!,
                            view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                            pc, planet.Config.SeaLevel);
                    }
                }
                planetRenderSwF.Stop();
                planetRenderMs = planetRenderSwF.Elapsed.TotalMilliseconds;

                if (_particleShader != null)
                    SceneRenderer.RenderParticles(g, _particleShader, _cache, view, proj);
                if (_canvasRenderer != null && _cache != null)
                {
                    var viewProj = view * proj;
                    foreach (var wc in Core.Component.UI.Canvas.All)
                    {
                        if (wc.IsActiveAndEnabled && wc.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
                            _canvasRenderer.RenderWorldCanvas(wc, in viewProj, _cache);
                    }
                }

                finalSceneTex = usePostFX ? _sceneFBO?.ColorTexture : null;
                _prevViewProj = view * proj;
            }
            else
            {

            // ═══════════ DEFERRED RENDERING PIPELINE ═══════════

            // 1. G-BUFFER PASS — draw opaque standard geometry to MRT
            if (_gbufferFBO == null) _gbufferFBO = new GPUFramebuffer(g);
            if (_gbufferW != W || _gbufferH != H)
            {
                _gbufferFBO.SetupGBuffer(W, H);
                _gbufferW = W; _gbufferH = H;
            }

            _gbufferFBO.Bind();
            g.ClearColor(0f, 0f, 0f, 0f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            SceneRenderer.RenderGBufferPass(g, _gbufferShader!, _cache!,
                view, proj, camPos, shadowFBO, shadowVP, sunSD, isES);

            // 2. SSAO PASS — screen-space ambient occlusion (half resolution)
            GPUTexture? ssaoResult = null;
            if (useSSAO && _ssaoShader != null && _ssaoBlurShader != null)
            {
                int ssaoW = Math.Max(1, W / 2);
                int ssaoH = Math.Max(1, H / 2);

                if (_ssaoFBO == null) _ssaoFBO = new GPUFramebuffer(g);
                if (_ssaoFBO.Width != ssaoW || _ssaoFBO.Height != ssaoH)
                    _ssaoFBO.SetupColorDepth(ssaoW, ssaoH);

                if (_ssaoBlurFBO == null) _ssaoBlurFBO = new GPUFramebuffer(g);
                if (_ssaoBlurFBO.Width != ssaoW || _ssaoBlurFBO.Height != ssaoH)
                    _ssaoBlurFBO.SetupColorDepth(ssaoW, ssaoH);

                // Raw SSAO
                _ssaoFBO.Bind();
                g.ClearColor(1f, 1f, 1f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                float ssaoRadius = postVolume != null ? Math.Clamp(postVolume.SSAORadius, 0.05f, 3f) : 0.5f;
                float ssaoBias = postVolume != null ? Math.Clamp(postVolume.SSAOBias, 0.0001f, 0.2f) : 0.025f;
                int ssaoSamples = postVolume != null ? Math.Clamp(postVolume.SSAOSamples, 4, 32) : 24;
                float depthSig = postVolume != null ? Math.Clamp(postVolume.SSAODepthSigma, 1f, 500f) : 80f;
                SceneRenderer.RenderSSAO(g, _ssaoShader, _fsQuad!, _gbufferFBO,
                    view, proj, W, H, ssaoRadius, ssaoBias, ssaoSamples);

                // Blur SSAO
                _ssaoBlurFBO.Bind();
                g.ClearColor(1f, 1f, 1f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                SceneRenderer.RenderSSAOBlur(g, _ssaoBlurShader, _fsQuad!, _ssaoFBO.ColorTexture!, _gbufferFBO,
                    ssaoW, ssaoH, depthSig);

                ssaoResult = _ssaoBlurFBO.ColorTexture;
            }

            // 3. SCENE FBO — setup for deferred lighting output + forward overlays
            if (_sceneFBO == null) _sceneFBO = new GPUFramebuffer(g);
            if (_sceneFBO_W != W || _sceneFBO_H != H)
            {
                _sceneFBO.SetupColorDepth(W, H);
                _sceneFBO_W = W; _sceneFBO_H = H;
            }

            _sceneFBO.Bind();
            g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // 4. SKY — render before deferred lighting (preserved because deferred discards sky pixels)
            Sky.RenderGPU(g, _skyShader, _fsQuad, _cache, view, proj,
                skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);

            // 5. DEFERRED LIGHTING — fullscreen PBR lighting from G-buffer
            g.BindVertexArray(_fsQuad!.VAO);
            float ssaoIntensity = postVolume != null ? postVolume.SSAOIntensity : 1f;
            foreach (var rp in ReflectionProbe.ActiveProbes)
                rp.EnsureGpuResources(g);
            var probePick = ReflectionProbe.GetBestForPosition(camPos);
            SceneRenderer.RenderDeferredLighting(g, _deferredLightShader!, _fsQuad!,
                _gbufferFBO, ssaoResult, shadowFBO,
                view, proj, camPos, shadowVP, sunSD,
                Ambient, 0.008f, ssaoIntensity,
                _tiledLights, W, H,
                probePick?.GpuCubemap, probePick?.Intensity ?? 0f);
            g.BindVertexArray(0);

            // 6. COPY G-BUFFER DEPTH → scene FBO (required before terrain forward pass).
            // Prefer shader copy: reads the same depth texture the deferred pass uses (texelFetch), so depth matches
            // what lighting sampled. glBlitFramebuffer can report success but mis-copy when one FBO fell back to
            // depth-only attachment and the other uses D24S8 (silent terrain depth-test failure on some drivers).
            _sceneFBO.Bind();
            while (g.GetError() != GLEnum.NoError) { }
            if (_depthCopyShader != null && _gbufferFBO.DepthTexture != null)
            {
                SceneRenderer.RenderDepthTextureToFramebufferDepth(g, _depthCopyShader, _fsQuad!, _gbufferFBO.DepthTexture);
                g.BindVertexArray(0);
            }
            else
            {
                g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _gbufferFBO.Handle);
                g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFBO.Handle);
                const ClearBufferMask StencilBufferBit = (ClearBufferMask)0x400;
                g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                    ClearBufferMask.DepthBufferBit | StencilBufferBit, BlitFramebufferFilter.Nearest);
                if (g.GetError() != GLEnum.NoError)
                {
                    while (g.GetError() != GLEnum.NoError) { }
                    g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                        ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
                }
#if DEBUG
                if (g.GetError() != GLEnum.NoError)
                    Debug.WriteLine($"[GameView] depth blit failed (no depth-copy shader): {g.GetError()}");
#endif
            }
            _sceneFBO.Bind();

            // 7. FORWARD OVERLAYS — terrain, custom shaders, transparent objects
            SceneRenderer.RenderForwardOverlays(g, _standardShader!, _cache!,
                view, proj, camPos,
                SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunSD,
                terrainShader: _terrainShader, isES: isES,
                lightColor: lightColorNorm);

            // 8. PLANET TERRAIN
            var planetRenderSw = Stopwatch.StartNew();
            if (_planetTerrainShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetTerrain(g, _planetTerrainShader, _cache,
                        view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                        pc, shadowFBO, shadowVP);
                }
            }

            // 8b. WATER
            if (_waterShader != null)
            {
                var skyC = _sky != null
                    ? new SN.Vector3(_sky.Top.R / 255f, _sky.Top.G / 255f, _sky.Top.B / 255f)
                    : new SN.Vector3(0.5f, 0.6f, 0.8f);
                SceneRenderer.RenderWater(g, _waterShader, _cache, view, proj,
                    SN.Vector3.Normalize(-L), Ambient, DiffuseK, camPos, skyC);
            }

            // 8c. PLANET ATMOSPHERE SHELL (visible from outside and inside)
            if (_planetAtmosphereShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetAtmosphere(g, _planetAtmosphereShader, _cache,
                        view, proj, planet, atmo, camPos, pc);
                }
            }

            // 8d. PLANET CLOUDS (separate from Skybox path)
            if (_planetCloudShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetClouds(g, _planetCloudShader, _cache,
                        view, proj, planet, atmo, camPos, pc, (float)Core.Time.time);
                }
            }

            // 8e. PLANET WATER — after atmosphere/cloud shells so haze does not cover the surface
            if (_planetWaterShader != null)
            {
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    var tp = planet.gameObject?.Transform?.Position;
                    var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                    var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, _light, fallbackPlanetSunDir, Ambient);
                    SceneRenderer.RenderPlanetWater(g, _planetWaterShader, _cache,
                        view, proj, planet, atmo, SN.Vector3.Normalize(-L), DiffuseK, camPos,
                        pc, planet.Config.SeaLevel);
                }
            }
            planetRenderSw.Stop();
            planetRenderMs = planetRenderSw.Elapsed.TotalMilliseconds;

            // 9. PARTICLES
            if (_particleShader != null)
                SceneRenderer.RenderParticles(g, _particleShader, _cache, view, proj);

            // 9b. WORLD-SPACE UI CANVASES (rendered in 3D space before post-processing)
            if (_canvasRenderer != null && _cache != null)
            {
                var viewProj = view * proj;
                foreach (var wc in Core.Component.UI.Canvas.All)
                {
                    if (wc.IsActiveAndEnabled && wc.RenderMode == Core.Component.UI.CanvasRenderMode.WorldSpace)
                        _canvasRenderer.RenderWorldCanvas(wc, in viewProj, _cache);
                }
            }

            // 10. SSR — screen-space reflections (reads lit scene + G-buffer)
            finalSceneTex = _sceneFBO.ColorTexture;
            if (useSSR && _ssrShader != null && _sceneFBO.ColorTexture != null)
            {
                if (_ssrFBO == null) _ssrFBO = new GPUFramebuffer(g);
                if (_ssrFBO.Width != W || _ssrFBO.Height != H)
                    _ssrFBO.SetupColorDepth(W, H);

                _ssrFBO.Bind();
                g.ClearColor(0f, 0f, 0f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.RenderSSR(g, _ssrShader, _fsQuad!, _sceneFBO.ColorTexture, _gbufferFBO,
                    view, proj, camPos, W, H,
                    postVolume?.SSRMaxRaySteps ?? 64,
                    postVolume?.SSRRoughnessCutoff ?? 0.6f,
                    postVolume?.SSRMaxRayDistance ?? 50f);
                g.BindVertexArray(0);

                finalSceneTex = _ssrFBO.ColorTexture;
            }

            // 10b. VOLUMETRIC FOG — ray-marched fullscreen pass (reads scene color + depth)
            if (_volFogShader != null && postVolume?.VolumetricFogEnabled == true
                && finalSceneTex != null && _gbufferFBO?.DepthTexture != null)
            {
                if (_volFogFBO == null) _volFogFBO = new GPUFramebuffer(g);
                if (_volFogFBO.Width != W || _volFogFBO.Height != H)
                    _volFogFBO.SetupColorDepth(W, H);

                _volFogFBO.Bind();
                g.ClearColor(0f, 0f, 0f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.RenderVolumetricFog(g, _volFogShader, _fsQuad!,
                    finalSceneTex, _gbufferFBO.DepthTexture,
                    view, proj, camPos,
                    sunSD, lightColorNorm,
                    shadowFBO, shadowVP, postVolume,
                    (float)Core.Time.time);
                g.BindVertexArray(0);

                finalSceneTex = _volFogFBO.ColorTexture;
            }

            // 10c. TAA — temporal resolve (camera motion via depth reprojection)
            if (useTaa && _taaResolveShader != null && finalSceneTex != null && _gbufferFBO?.DepthTexture != null)
            {
                if (_taaHistoryFbo == null) _taaHistoryFbo = new GPUFramebuffer(g);
                if (_taaHistoryFbo.Width != W || _taaHistoryFbo.Height != H)
                    _taaHistoryFbo.SetupColorDepth(W, H);
                if (_taaTempFbo == null) _taaTempFbo = new GPUFramebuffer(g);
                if (_taaTempFbo.Width != W || _taaTempFbo.Height != H)
                    _taaTempFbo.SetupColorDepth(W, H);

                _taaTempFbo.Bind();
                g.Viewport(0, 0, (uint)W, (uint)H);
                g.Clear(ClearBufferMask.ColorBufferBit);
                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.RenderTemporalAA(g, _taaResolveShader, _fsQuad!, finalSceneTex,
                    _taaHistoryFbo.ColorTexture, _gbufferFBO, invVpCurr, prevVp, W, H,
                    postVolume?.TAAFrameBlend ?? 0.12f,
                    postVolume?.TAASharpen ?? 0.35f,
                    _taaResetHistory);
                g.BindVertexArray(0);

                g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _taaTempFbo.Handle);
                g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _taaHistoryFbo.Handle);
                g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);

                finalSceneTex = _taaTempFbo.ColorTexture;
                _taaResetHistory = false;
            }
            else if (!useTaa)
                _taaResetHistory = true;

            _prevViewProj = view * proj;

            } // end deferred branch

            // 11. POST-PROCESSING → Avalonia framebuffer
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);

            if (usePostFX && finalSceneTex != null)
            {
                g.Disable(EnableCap.DepthTest);
                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, finalSceneTex, W, H,
                    postVolume, underwater, (float)Core.Time.time);
                g.BindVertexArray(0);
                g.Enable(EnableCap.DepthTest);
            }
            else if (finalSceneTex != null)
            {
                // No post-processing: simple blit to screen
                g.Disable(EnableCap.DepthTest);
                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, finalSceneTex, W, H,
                    null, null, 0f);
                g.BindVertexArray(0);
                g.Enable(EnableCap.DepthTest);
            }

            _tScene = sec.Elapsed.TotalMilliseconds;

            // 12. CANVAS UI OVERLAY — draw screen-space UI canvases on top of everything
            if (_canvasRenderer != null && _cache != null)
            {
                _canvasRenderer.RenderOverlays(W, H, _cache);
            }

            if (Profiler.Enabled)
            {
                CollectPlanetProfilerStats(out int planetCount, out int chunkCount, out int activeJobs, out int pendingJobs);
                Profiler.SetPlanetStats(planetCount, chunkCount, activeJobs, pendingJobs, planetLodMs, planetRenderMs);
            }

            Profiler.End(); // end "Render"

            if (_cache != null && ++_gpuCacheMaintainCounter >= 120)
            {
                _gpuCacheMaintainCounter = 0;
                _cache.Maintain(maxEntries: 384, maxReleasesPerFrame: 96);
            }

            g.Flush();

            // Restore Avalonia's FB and clean up GL state for compositing.
            // Unbind ALL texture units to prevent stale G-buffer/FBO textures from
            // bleeding into the Scene View (both views share the same GL context).
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.UseProgram(0);
            g.BindVertexArray(0);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
            g.Disable(EnableCap.DepthTest);
            g.Disable(EnableCap.CullFace);
            g.Disable(EnableCap.Blend);
            for (int unit = 0; unit < 8; unit++)
            {
                g.ActiveTexture(TextureUnit.Texture0 + unit);
                g.BindTexture(TextureTarget.Texture2D, 0);
            }
            g.ActiveTexture(TextureUnit.Texture0);

            _frameWatch.Stop();
            _msFrameLast = _frameWatch.Elapsed.TotalMilliseconds;
            Ema(ref _msFrameEma, _msFrameLast, 0.18);

            Profiler.EndFrame();

                // Signal the update timer that this render is done — it can request the next one.
                _renderInFlight = false;
            }
            finally
            {
                SceneRenderer.EndViewRender();
                _renderInFlight = false;
            }
        }
        #endregion

        #region 2D HUD overlay (after GL render)
        public override void Render(DrawingContext ctx)
        {
            // Material warm-up runs outside GL context to avoid blocking GPU work
            MaterialRebind.RepairScene();
            if (MaterialRebind.NeedsMoreFrames)
                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

            base.Render(ctx);
            DrawFpsHud(ctx);
        }

        void DrawFpsHud(DrawingContext ctx)
        {
            string line1 = $"FPS:{_fpsDisplay:F0}  GL:{_msFrameEma:F1}ms  Sh:{_tShadow:F0} M:{_tScene:F0}";
            string? line2 = null;
            if (Profiler.SampleScripts && Profiler.LatestTopScriptCount > 0)
            {
                var top = Profiler.GetLatestTopScript(0);
                line2 = $"Scripts:{Profiler.LatestScriptsMs:F1}ms  {top.TypeName} {top.Ms:F1}";
            }
            const double font = 12, padX = 8, padY = 6;
            int lines = line2 == null ? 1 : 2;
            double est = Math.Max(line1.Length, line2?.Length ?? 0) * font * 0.62;
            double lineH = font * 1.4;
            double w = est + padX * 2, h = lineH * lines + padY * 2;
            var bg = new Rect(6, 6, Math.Ceiling(w), Math.Ceiling(h));
            ctx.FillRectangle(HudBg, bg);
            new TextLayout(line1, HudTypeface, font, HudText).Draw(ctx, new Point(bg.X + padX, bg.Y + padY));
            if (line2 != null)
                new TextLayout(line2, HudTypeface, font, HudText).Draw(ctx, new Point(bg.X + padX, bg.Y + padY + lineH));
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

        static float TaaHalton(int index, int b)
        {
            float f = 1f, r = 0f;
            int i = index;
            while (i > 0)
            {
                f /= b;
                r += f * (i % b);
                i /= b;
            }
            return r;
        }

        static bool CameraInsidePlanetSolid(SN.Vector3 camPos)
        {
            var planets = PlanetTerrain.ActivePlanets;
            for (int i = 0; i < planets.Count; i++)
            {
                var p = planets[i];
                if (p?.gameObject == null || !p.IsActiveAndEnabled)
                    continue;
                if (p.TrySampleWorldDensity(camPos, out float d) && d <= 0f)
                    return true;
            }
            return false;
        }

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

        void ExitLookAndClear()
        {
            if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
            _mouseLook = false; _hasLastMouse = false;
            Input.ClearAll(); Input.FeedMouseDelta(0, 0);
            Input.PlayViewportCaptureActive = false;
        }

        void OnKeyDown(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var code = KeyMap.FromAvalonia(e.Key);
            Input.FeedKeyDown(code);
            if (IsGameplayKey(e.Key))
                e.Handled = true;
        }

        void OnKeyUp(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
            if (IsGameplayKey(e.Key))
                e.Handled = true;
        }

        static bool IsGameplayKey(Key key) =>
            key is Key.W or Key.A or Key.S or Key.D
                or Key.Up or Key.Down or Key.Left or Key.Right
                or Key.Space or Key.LeftShift or Key.RightShift;

        void BindPlayKeyboard()
        {
            UnbindPlayKeyboard();
            _playKeyHost = TopLevel.GetTopLevel(this);
            if (_playKeyHost == null) return;
            _playKeyHost.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _playKeyHost.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        void BindPlayPointer()
        {
            UnbindPlayPointer();
            _playPointerHost = TopLevel.GetTopLevel(this);
            if (_playPointerHost == null) return;
            _playPointerHost.AddHandler(InputElement.PointerPressedEvent, OnHostPointerPressed, RoutingStrategies.Tunnel);
            _playPointerHost.AddHandler(InputElement.PointerReleasedEvent, OnHostPointerReleased, RoutingStrategies.Tunnel);
            _playPointerHost.AddHandler(InputElement.PointerMovedEvent, OnHostPointerMoved, RoutingStrategies.Tunnel);
        }

        void UnbindPlayPointer()
        {
            if (_playPointerHost == null) return;
            _playPointerHost.RemoveHandler(InputElement.PointerPressedEvent, OnHostPointerPressed);
            _playPointerHost.RemoveHandler(InputElement.PointerReleasedEvent, OnHostPointerReleased);
            _playPointerHost.RemoveHandler(InputElement.PointerMovedEvent, OnHostPointerMoved);
            _playPointerHost = null;
        }

        bool IsPointerOverGameView(PointerEventArgs e)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return false;
            var pos = e.GetPosition(this);
            return pos.X >= 0 && pos.Y >= 0 && pos.X <= Bounds.Width && pos.Y <= Bounds.Height;
        }

        bool IsCursorOverGameView() => IsPointerOver && Bounds.Width > 0 && Bounds.Height > 0;

        void FeedPlayPointerButtons(PointerPoint pt)
        {
            if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
            if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
            if (pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
        }

        void TryPlayPlanetSculpt(PointerPoint pt)
        {
            if (State != GamePanel.GameState.Playing) return;
            var props = pt.Properties;
            bool shift = Input.GetKey(KeyCode.LeftShift);
            bool dig = props.IsLeftButtonPressed && !shift;
            bool build = props.IsRightButtonPressed || (props.IsLeftButtonPressed && shift);
            if (!dig && !build) return;
            PlanetTool.ApplyLookStroke(dig, build);
        }

        void OnHostPointerPressed(object? s, PointerPressedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing || !IsPointerOverGameView(e)) return;
            OnPointerPressed(this, e);
        }

        void OnHostPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            if (!ReferenceEquals(_capturedPointer, e.Pointer) && !IsPointerOverGameView(e)) return;
            OnPointerReleased(this, e);
        }

        void OnHostPointerMoved(object? s, PointerEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            if (!ReferenceEquals(_capturedPointer, e.Pointer) && !IsPointerOverGameView(e)) return;
            OnPointerMoved(this, e);
        }

        void UnbindPlayKeyboard()
        {
            if (_playKeyHost == null) return;
            _playKeyHost.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _playKeyHost.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            _playKeyHost = null;
        }

        void OnPointerPressed(object? s, PointerPressedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            Focus();
            var pt = e.GetCurrentPoint(this);
            FeedPlayPointerButtons(pt);
            TryPlayPlanetSculpt(pt);
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            Input.PlayViewportCaptureActive = true;
        }

        void OnPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
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
            if (State != GamePanel.GameState.Playing) return;
            var p = e.GetPosition(this);
            var cur = new SN.Vector2((float)p.X, (float)p.Y);
            if (_hasLastMouse)
                Input.FeedMouseDelta(cur.X - _lastMouse.X, cur.Y - _lastMouse.Y);
            _lastMouse = cur;
            _hasLastMouse = true;
            Input.FeedMousePosition(cur.X, cur.Y);

            var pt = e.GetCurrentPoint(this);
            FeedPlayPointerButtons(pt);
            TryPlayPlanetSculpt(pt);
        }
        #endregion

        #region State management
        void UpdatePlayingRegistration(bool isPlaying)
        {
            if (isPlaying)
            {
                if (_registeredAsPlaying) return;
                _registeredAsPlaying = true;
                System.Threading.Interlocked.Increment(ref s_playingViewCount);
                AnyPlayingStateChanged?.Invoke();
                return;
            }

            if (!_registeredAsPlaying) return;
            _registeredAsPlaying = false;
            System.Threading.Interlocked.Decrement(ref s_playingViewCount);
            AnyPlayingStateChanged?.Invoke();
        }

        void OnStateChanged()
        {
            UpdatePlayingRegistration(State == GamePanel.GameState.Playing);
            switch (State)
            {
                case GamePanel.GameState.Playing:
                    SceneService.PlayMode = true;
                    EnsurePlaySnapshot();
                    PlanetPlayerSpawner.EnsurePlayModeControllers();
                    EnsureAwakeStart();
                    PlanetPlayerSpawner.EnsurePlanetToolsOnPlayers();
                    _needsWarm = true; Focus();
                    BindPlayKeyboard();
                    BindPlayPointer();
                    SceneRenderer.ResetBiomeTexDebug();
                    Core.Time.Reset();
                    _updateWatch.Restart(); _fixedWatch.Restart();
                    Input.ClearAll();
                    _updateTimer.Start(); _fixedTimer.Start();
                    _fpsTick.Restart(); _fpsWindow.Restart();
                    RebuildSceneCaches();
                    break;
                case GamePanel.GameState.Paused:
                    SceneService.PlayMode = false;
                    UnbindPlayKeyboard();
                    UnbindPlayPointer();
                    _fixedTimer.Stop(); _updateTimer.Stop(); break;
                case GamePanel.GameState.Stopped:
                {
                    SceneService.PlayMode = false;
                    UnbindPlayKeyboard();
                    UnbindPlayPointer();
                    // Capture selection before LoadFromFile: SceneService.SceneReplaced (e.g. SceneView)
                    // clears SelectionService, so ReSelectAfterRestore cannot read the old Current afterward.
                    string? restoreSelName = SelectionService.Current?.Name;

                    _fixedTimer.Stop(); _updateTimer.Stop();
                    _updateWatch.Reset(); _fixedWatch.Reset();
                    Game_Engine.Core.AudioBackend.StopAll();   // kill all audio immediately

                    // Only tear down + reload when we have a play snapshot (Enter Play created one).
                    // Otherwise __OnDestroy runs without ReplaceAll → every Behavior.Enabled stays false.
                    if (_playSnapshotPath != null)
                    {
                        CallOnDestroyAll();
                        PostProcessVolume.ClearAll();
                        Core.Component.UI.Canvas.ClearAll();
                        Light.ClearAll();
                        SceneManager.Reset();
                        RestorePlaySnapshot();
                        // After scene restore, re-bind selection (SceneReplaced may have cleared Current).
                        ReSelectAfterRestore(restoreSelName);
                    }

                    SceneRenderer.ResetBiomeTexDebug();
                    _awakened = _started = false; _collidersWarm = false; _needsWarm = true;
                    if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
                    _mouseLook = false; _hasLastMouse = false;
                    Input.ClearAll();
                    break;
                }
            }
            _renderInFlight = false; // Reset gate so first frame renders immediately
            RequestNextFrameRendering();
        }

        void EnsurePlaySnapshot()
        {
            if (_playSnapshotPath != null) return;
            // Cache material textures before snapshot — serialization doesn't preserve them
            _snapshotMaterialTextures = CacheMaterialTextures();
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GE_PlaySnapshot_{Guid.NewGuid():N}.scene");
            // Save without SceneService.SaveToFile so CurrentScenePath stays the user's project scene.
            _scenePathBeforePlay = SceneService.CurrentScenePath;
            SceneSerialization.SaveScene(tmp, SceneService.Root);
            _playSnapshotPath = tmp;
        }

        void RestorePlaySnapshot()
        {
            if (_playSnapshotPath == null) return;
            SceneService.LoadFromFile(_playSnapshotPath);
            SceneService.SetCurrentScenePath(_scenePathBeforePlay);
            _scenePathBeforePlay = null;
            // Re-apply cached material textures and transparent flags
            if (_snapshotMaterialTextures != null)
            {
                RestoreMaterialTextures(_snapshotMaterialTextures);
                _snapshotMaterialTextures = null;
            }
            try { System.IO.File.Delete(_playSnapshotPath); } catch { }
            _playSnapshotPath = null; _collidersWarm = false; _needsWarm = true;

        }

        /// <summary>
        /// After scene restore, find a GO with the same name as the previously selected one
        /// and re-select it so the inspector refreshes with the new (restored) instance.
        /// Pass <paramref name="nameFromBeforeRestore"/> — selection is often cleared during
        /// <see cref="SceneService.SceneReplaced"/> before this runs.
        /// </summary>
        void ReSelectAfterRestore(string? nameFromBeforeRestore)
        {
            string? name = nameFromBeforeRestore;
            if (string.IsNullOrEmpty(name))
            {
                SelectionService.Touch();
                return;
            }
            GameObject? match = null;
            foreach (var root in SceneService.Root)
            {
                match = FindByName(root, name);
                if (match != null) break;
            }
            SelectionService.Set(match); // re-select (or clear if not found)
        }

        static GameObject? FindByName(GameObject go, string name)
        {
            if (go.Name == name) return go;
            foreach (var c in go.Children)
            {
                var found = FindByName(c, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Walk the scene graph and cache each Material's Textures list and Transparent flag,
        /// keyed by a unique identity string (GameObject path + material index).
        /// </summary>
        static Dictionary<string, List<object>> CacheMaterialTextures()
        {
            var cache = new Dictionary<string, List<object>>();
            foreach (var root in SceneService.Root)
                CacheMaterialTexturesRecursive(root, "", cache);
            return cache;
        }

        static void CacheMaterialTexturesRecursive(GameObject go, string parentPath, Dictionary<string, List<object>> cache)
        {
            string path = string.IsNullOrEmpty(parentPath) ? go.Name : (parentPath + "/" + go.Name);
            int mrIndex = 0;
            foreach (var b in go.Behaviors)
            {
                if (b is MeshRenderer mr)
                {
                    var mat = mr.Material;
                    if (mat != null && mat.Textures.Count > 0)
                    {
                        string key = $"{path}##MR{mrIndex}";
                        cache[key] = new List<object>(mat.Textures);
                        // Also store the transparent flag
                        cache[$"{key}##T"] = new List<object> { mat.Transparent };
                    }
                    mrIndex++;
                }
            }
            foreach (var child in go.Children)
                CacheMaterialTexturesRecursive(child, path, cache);
        }

        static void RestoreMaterialTextures(Dictionary<string, List<object>> cache)
        {
            foreach (var root in SceneService.Root)
                RestoreMaterialTexturesRecursive(root, "", cache);
        }

        static void RestoreMaterialTexturesRecursive(GameObject go, string parentPath, Dictionary<string, List<object>> cache)
        {
            string path = string.IsNullOrEmpty(parentPath) ? go.Name : (parentPath + "/" + go.Name);
            int mrIndex = 0;
            foreach (var b in go.Behaviors)
            {
                if (b is MeshRenderer mr)
                {
                    var mat = mr.Material;
                    if (mat != null)
                    {
                        string key = $"{path}##MR{mrIndex}";
                        if (cache.TryGetValue(key, out var texList) && texList.Count > 0)
                        {
                            mat.Textures.Clear();
                            foreach (var t in texList) mat.Textures.Add(t);
                        }
                        if (cache.TryGetValue($"{key}##T", out var flagList) && flagList.Count > 0 && flagList[0] is bool trans)
                        {
                            mat.Transparent = trans;
                        }
                    }
                    mrIndex++;
                }
            }
            foreach (var child in go.Children)
                RestoreMaterialTexturesRecursive(child, path, cache);
        }

        void EnsureAwakeStart()
        {
            if (!_awakened) { SceneService.ForEachActiveBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { SceneService.ForEachActiveBehavior(b => b.__Start()); _started = true; }
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

        #region Update / FixedUpdate
        void TickUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;

            // Process any deferred scene load queued by SceneManager.LoadScene()
            if (SceneManager.HasPendingLoad)
            {
                SceneManager.ProcessPendingLoad(
                    callOnDestroyAll: () => CallOnDestroyAll(),
                    clearRegistries: () =>
                    {
                        PostProcessVolume.ClearAll();
                        Core.Component.UI.Canvas.ClearAll();
                        Light.ClearAll();
                        Core.Rendering.UI.UIEventSystem.Reset();
                        Input.ClearAll();
                    },
                    rebuildCaches: () =>
                    {
                        _needsWarm = true;
                        _collidersWarm = false;
                        RebuildSceneCaches();
                    },
                    callAwakeStart: () =>
                    {
                        _awakened = false; _started = false;
                        EnsureAwakeStart();
                    });
            }

            if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

            Profiler.BeginFrame();

            var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
            _updateWatch.Restart();
            if (dt > 0.05) dt = 0.05;
            Core.Time.BeginUpdate(dt);
            Input.NewFrame((float)dt);
            Input.PollHardwareHeldKeys();
            if (State == GamePanel.GameState.Playing)
            {
                bool pollMouse = _capturedPointer != null || IsCursorOverGameView();
                Input.PollPlayMouseButtons(pollMouse);
            }

            // Feed viewport size in DIP space (matches MousePosition coordinate space)
            Input.FeedViewportSize((float)Bounds.Width, (float)Bounds.Height);

            // Process UI events before game scripts so scripts can query UI state.
            {
                int vpW = Math.Max(1, (int)Bounds.Width);
                int vpH = Math.Max(1, (int)Bounds.Height);
                Core.Rendering.UI.UIEventSystem.ProcessEvents(vpW, vpH);
            }

            if (NetworkManager.IsActive)
                NetworkManager.Update();

            Profiler.Begin("Scripts");
            SceneService.TickActiveBehaviors(Profiler.ScriptPhase.Update);
            SceneService.TickActiveBehaviors(Profiler.ScriptPhase.LateUpdate);
            Profiler.PublishScriptCosts();
            Profiler.End();

            Profiler.Begin("Audio");
            AudioManager.UpdateListenerTransform();
            Profiler.End();

            Input.EndFrame();
        }

        void TickFixedUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;
            if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }
            double dt = _fixedWatch.IsRunning ? _fixedWatch.Elapsed.TotalSeconds : FIXED_DT;
            _fixedWatch.Restart();
            if (dt > 0.1) dt = 0.1;
            _fixedAccum += dt;
            if (_fixedAccum > 0.25) _fixedAccum = 0.25;
            int physicsSteps = 0;
            const int maxPhysicsSteps = 3;
            while (_fixedAccum >= FIXED_DT && physicsSteps < maxPhysicsSteps)
            {
                Profiler.Begin("Physics");
                Core.Time.BeginFixedUpdate(FIXED_DT);
                Core.Physics.PhysicsCache.Tick();
                SceneService.TickActiveBehaviors(Profiler.ScriptPhase.FixedUpdate);
                Profiler.End();
                _fixedAccum -= FIXED_DT;
                physicsSteps++;
            }
        }

        void CallOnDestroyAll() => SceneService.ForEachActiveBehavior(b => b.__OnDestroy());
        #endregion
    }
}
