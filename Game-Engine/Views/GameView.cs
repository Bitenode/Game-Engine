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
using Game_Engine.Core.Physics;
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

        #region GPU Resources
        private GLContext? _glCtx;
        private ShaderProgram? _standardShader;
        private ShaderProgram? _depthShader;
        private ShaderProgram? _skyShader;
        private ShaderProgram? _gridShader;
        private ShaderProgram? _terrainShader;
        private ShaderProgram? _particleShader;
        private ShaderProgram? _waterShader;
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
        private GPUFramebuffer? _gbufferFBO;
        private GPUFramebuffer? _ssaoFBO;
        private GPUFramebuffer? _ssaoBlurFBO;
        private GPUFramebuffer? _ssrFBO;
        private int _gbufferW, _gbufferH;
        private SN.Vector3[]? _ssaoKernel;
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

        const double FIXED_DT = 1.0 / 60.0;
        double _fixedAccum = 0.0;

        bool _mouseLook;
        SN.Vector2 _lastMouse;
        bool _hasLastMouse;
        IPointer? _capturedPointer;

        string? _playSnapshotPath;
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
        #endregion

        // Render gating: prevent InvalidateVisual() from piling up.
        // Avalonia's OpenGlControlBase compositing is very expensive (~500ms on some systems).
        // We only request a new render after OnOpenGlRender has completed the previous one.
        private volatile bool _renderInFlight;

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
                    InvalidateVisual();
                }
            };
            _fixedTimer.Interval = TimeSpan.FromMilliseconds(8);
            _fixedTimer.Tick += (_, __) => TickFixedUpdate();

            SceneService.Changed += () => { RebuildSceneCaches(); _needsWarm = true; _cache?.InvalidateAll(); InvalidateVisual(); };

            // Full scene replacement: request a full GPU cache flush on the next render pass
            SceneService.SceneReplaced += () =>
            {
                if (_cache != null) _cache.FlushRequested = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual,
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

            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
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

                // Generate SSAO hemisphere kernel (biased toward the surface)
                _ssaoKernel = GenerateSSAOKernel(32);

                _fsQuad = new FullscreenQuad(g);
                _cache = new ResourceCache(g);
                _shadow = new ShadowMapGPU(g, 1024, 1024);

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
            _canvasRenderer?.Dispose(); _canvasRenderer = null;
            _sceneFBO?.Dispose(); _sceneFBO = null; _sceneFBO_W = 0; _sceneFBO_H = 0;
            // Deferred pipeline cleanup
            _gbufferFBO?.Dispose(); _gbufferFBO = null; _gbufferW = 0; _gbufferH = 0;
            _ssaoFBO?.Dispose(); _ssaoFBO = null;
            _ssaoBlurFBO?.Dispose(); _ssaoBlurFBO = null;
            _ssrFBO?.Dispose(); _ssrFBO = null;
            _ssrShader?.Dispose(); _ssrShader = null;
            _ssaoBlurShader?.Dispose(); _ssaoBlurShader = null;
            _ssaoShader?.Dispose(); _ssaoShader = null;
            _deferredLightShader?.Dispose(); _deferredLightShader = null;
            _gbufferShader?.Dispose(); _gbufferShader = null;

            _postProcessShader?.Dispose(); _postProcessShader = null;
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
            base.OnOpenGlDeinit(gl);
        }

        static void WalkTreeLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTreeLOD(c, cam);
        }

        static void WalkTerrainLOD(GameObject go, SN.Vector3 cam)
        {
            if (!go.Enabled) return;
            foreach (var b in go.Behaviors)
                if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
            foreach (var c in go.Children) WalkTerrainLOD(c, cam);
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

            // Bind Avalonia's framebuffer and reset essential GL state.
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            g.Disable(EnableCap.Blend);
            g.ColorMask(true, true, true, true);
            g.DepthMask(true);

            // --- EDITOR MODE: dark screen ---
            if (State != GamePanel.GameState.Playing)
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

            // Camera
            var cams = _cams;
            Camera? cam = cams.Count > 0 ? cams[0] : null;

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

                // Restore main viewport after shadow pass
                g.Viewport(0, 0, (uint)W, (uint)H);
                shadowFBO = _shadow.FBO;
            }

            _tShadow = sec.Elapsed.TotalMilliseconds; sec.Restart();

            // --- UNDERWATER DETECTION ---
            var underwaterWater = Water.GetUnderwaterWater(camPos);
            float underwaterDepth = 0f;
            if (underwaterWater != null)
            {
                float surfaceY = underwaterWater.SampleHeight(camPos.X, camPos.Z);
                underwaterDepth = Math.Max(0f, surfaceY - camPos.Y);
            }

            // --- POST-PROCESSING setup ---
            var postVolume = PostProcessVolume.GetActive();
            bool usePostFX = (postVolume != null || underwaterWater != null) && _postProcessShader != null;

            // Update terrain LOD per frame
            foreach (var root in SceneService.Root) WalkTerrainLOD(root, camPos);
            // Update tree LOD per frame
            foreach (var root in SceneService.Root) WalkTreeLOD(root, camPos);

            var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            bool isES = _glCtx.IsES;

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
            if (_ssaoShader != null && _ssaoBlurShader != null && _ssaoKernel != null)
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

                SceneRenderer.RenderSSAO(g, _ssaoShader, _fsQuad!, _gbufferFBO,
                    view, proj, _ssaoKernel, W, H, 0.5f, 0.025f);

                // Blur SSAO
                _ssaoBlurFBO.Bind();
                g.ClearColor(1f, 1f, 1f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                SceneRenderer.RenderSSAOBlur(g, _ssaoBlurShader, _fsQuad!, _ssaoFBO.ColorTexture!, ssaoW, ssaoH);

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
            SceneRenderer.RenderDeferredLighting(g, _deferredLightShader!, _fsQuad!,
                _gbufferFBO, ssaoResult, shadowFBO,
                view, proj, camPos, shadowVP, sunSD,
                Ambient, 0.008f);
            g.BindVertexArray(0);

            // 6. BLIT G-BUFFER DEPTH to scene FBO for correct forward overlay depth testing
            g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _gbufferFBO.Handle);
            g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFBO.Handle);
            g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            _sceneFBO.Bind();

            // 7. FORWARD OVERLAYS — terrain, custom shaders, transparent objects
            SceneRenderer.RenderForwardOverlays(g, _standardShader!, _cache!,
                view, proj, camPos,
                SN.Vector3.Normalize(-L), DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunSD,
                terrainShader: _terrainShader, isES: isES,
                lightColor: lightColorNorm);

            // 8. WATER
            if (_waterShader != null)
            {
                var skyC = _sky != null
                    ? new SN.Vector3(_sky.Top.R / 255f, _sky.Top.G / 255f, _sky.Top.B / 255f)
                    : new SN.Vector3(0.5f, 0.6f, 0.8f);
                SceneRenderer.RenderWater(g, _waterShader, _cache, view, proj,
                    SN.Vector3.Normalize(-L), Ambient, DiffuseK, camPos, skyC);
            }

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
            GPUTexture? finalSceneTex = _sceneFBO.ColorTexture;
            if (_ssrShader != null && _sceneFBO.ColorTexture != null)
            {
                if (_ssrFBO == null) _ssrFBO = new GPUFramebuffer(g);
                if (_ssrFBO.Width != W || _ssrFBO.Height != H)
                    _ssrFBO.SetupColorDepth(W, H);

                _ssrFBO.Bind();
                g.ClearColor(0f, 0f, 0f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit);

                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.RenderSSR(g, _ssrShader, _fsQuad!, _sceneFBO.ColorTexture, _gbufferFBO,
                    view, proj, camPos, W, H);
                g.BindVertexArray(0);

                finalSceneTex = _ssrFBO.ColorTexture;
            }

            // 11. POST-PROCESSING → Avalonia framebuffer
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);

            if (usePostFX && finalSceneTex != null)
            {
                g.Disable(EnableCap.DepthTest);
                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, finalSceneTex, W, H,
                    postVolume, underwaterWater, underwaterDepth, (float)Core.Time.time);
                g.BindVertexArray(0);
                g.Enable(EnableCap.DepthTest);
            }
            else if (finalSceneTex != null)
            {
                // No post-processing: simple blit to screen
                g.Disable(EnableCap.DepthTest);
                g.BindVertexArray(_fsQuad!.VAO);
                SceneRenderer.ApplyPostProcessing(g, _postProcessShader!, finalSceneTex, W, H,
                    null, null, 0f, 0f);
                g.BindVertexArray(0);
                g.Enable(EnableCap.DepthTest);
            }

            _tScene = sec.Elapsed.TotalMilliseconds;

            // 12. CANVAS UI OVERLAY — draw screen-space UI canvases on top of everything
            if (_canvasRenderer != null && _cache != null)
            {
                _canvasRenderer.RenderOverlays(W, H, _cache);
            }

            Profiler.End(); // end "Render"

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
        #endregion

        #region 2D HUD overlay (after GL render)
        public override void Render(DrawingContext ctx)
        {
            // Material warm-up runs outside GL context to avoid blocking GPU work
            MaterialRebind.RepairScene();
            if (MaterialRebind.NeedsMoreFrames)
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

            base.Render(ctx);
            DrawFpsHud(ctx);
        }

        void DrawFpsHud(DrawingContext ctx)
        {
            string line1 = $"FPS:{_fpsDisplay:F0}  GL:{_msFrameEma:F1}ms  Sh:{_tShadow:F0} M:{_tScene:F0}";
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

        /// <summary>
        /// Generate SSAO hemisphere kernel samples, biased toward the surface center.
        /// Points are in tangent space with +Z as the surface normal.
        /// </summary>
        static SN.Vector3[] GenerateSSAOKernel(int size)
        {
            var rng = new Random(42); // deterministic for reproducibility
            var kernel = new SN.Vector3[size];
            for (int i = 0; i < size; i++)
            {
                // Random point in hemisphere
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                float y = (float)(rng.NextDouble() * 2.0 - 1.0);
                float z = (float)rng.NextDouble(); // hemisphere: z >= 0

                var sample = SN.Vector3.Normalize(new SN.Vector3(x, y, z));
                sample *= (float)rng.NextDouble();

                // Bias toward center: more samples near the origin
                float scale = (float)i / size;
                scale = 0.1f + scale * scale * 0.9f; // lerp(0.1, 1.0, scale^2)
                sample *= scale;

                kernel[i] = sample;
            }
            return kernel;
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
        }

        void OnKeyDown(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var code = KeyMap.FromAvalonia(e.Key); Input.FeedKeyDown(code);
        }

        void OnKeyUp(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
        }

        void OnPointerPressed(object? s, PointerPressedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            Focus();
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
            if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
            if (pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
        }

        void OnPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Left);
            if (!pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Middle);
            if (!pt.Properties.IsRightButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Right);
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
        }
        #endregion

        #region State management
        void OnStateChanged()
        {
            switch (State)
            {
                case GamePanel.GameState.Playing:
                    EnsurePlaySnapshot(); EnsureAwakeStart();
                    _needsWarm = true; Focus();
                    Core.Time.Reset();
                    _updateWatch.Restart(); _fixedWatch.Restart();
                    Input.ClearAll();
                    _updateTimer.Start(); _fixedTimer.Start();
                    _fpsTick.Restart(); _fpsWindow.Restart();
                    RebuildSceneCaches();
                    break;
                case GamePanel.GameState.Paused:
                    _fixedTimer.Stop(); _updateTimer.Stop(); break;
                case GamePanel.GameState.Stopped:
                    _fixedTimer.Stop(); _updateTimer.Stop();
                    _updateWatch.Reset(); _fixedWatch.Reset();
                    Game_Engine.Core.AudioBackend.StopAll();   // kill all audio immediately
                    CallOnDestroyAll();
                    // Purge static component registries that __OnDestroy may have missed
                    PostProcessVolume.ClearAll();
                    Core.Component.UI.Canvas.ClearAll();
                    Light.ClearAll();
                    SceneManager.Reset();
                    RestorePlaySnapshot();
                    // After scene restore, the old selected GO no longer exists in the new scene tree.
                    // Try to re-select a GO with the same name, or clear the selection.
                    ReSelectAfterRestore();
                    _awakened = _started = false; _collidersWarm = false; _needsWarm = true;
                    if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
                    _mouseLook = false; _hasLastMouse = false;
                    Input.ClearAll();
                    break;
            }
            _renderInFlight = false; // Reset gate so first frame renders immediately
            InvalidateVisual();
        }

        void EnsurePlaySnapshot()
        {
            if (_playSnapshotPath != null) return;
            // Cache material textures before snapshot — serialization doesn't preserve them
            _snapshotMaterialTextures = CacheMaterialTextures();
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GE_PlaySnapshot_{Guid.NewGuid():N}.scene");
            SceneService.SaveToFile(tmp); _playSnapshotPath = tmp;
        }

        void RestorePlaySnapshot()
        {
            if (_playSnapshotPath == null) return;
            SceneService.LoadFromFile(_playSnapshotPath);
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
        /// </summary>
        void ReSelectAfterRestore()
        {
            var prev = SelectionService.Current;
            if (prev == null) { SelectionService.Touch(); return; }
            string? name = prev.Name;
            // Walk the restored scene to find a match by name
            GameObject? match = null;
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var root in SceneService.Root)
                {
                    match = FindByName(root, name);
                    if (match != null) break;
                }
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
            if (!_awakened) { ForEachBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { ForEachBehavior(b => b.__Start()); _started = true; }
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

            // Feed viewport size in DIP space (matches MousePosition coordinate space)
            Input.FeedViewportSize((float)Bounds.Width, (float)Bounds.Height);

            // Process UI events before game scripts so scripts can query UI state.
            {
                int vpW = Math.Max(1, (int)Bounds.Width);
                int vpH = Math.Max(1, (int)Bounds.Height);
                Core.Rendering.UI.UIEventSystem.ProcessEvents(vpW, vpH);
            }

            Profiler.Begin("Scripts");
            ForEachBehavior(b => b.__Update());
            ForEachBehavior(b => b.__LateUpdate());
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
            while (_fixedAccum >= FIXED_DT)
            {
                Profiler.Begin("Physics");
                Core.Time.BeginFixedUpdate(FIXED_DT);
                Core.Physics.PhysicsCache.Tick();
                ForEachBehavior(b => b.__FixedUpdate());
                Profiler.End();
                _fixedAccum -= FIXED_DT;
            }
        }

        void CallOnDestroyAll() => ForEachBehavior(b => b.__OnDestroy());
        static void ForEachBehavior(Action<Behavior> a) { foreach (var r in SceneService.Root) Traverse(r, a); }
        static void Traverse(GameObject go, Action<Behavior> a)
        { if (!go.Enabled) return; foreach (var b in go.Behaviors) a(b); foreach (var c in go.Children) Traverse(c, a); }
        #endregion
    }
}
