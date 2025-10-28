#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;

namespace Game_Engine.Views
{
    public class GameView : Control
    {
        public static readonly StyledProperty<GamePanel.GameState> StateProperty =
            AvaloniaProperty.Register<GameView, GamePanel.GameState>(
                nameof(State), GamePanel.GameState.Stopped);

        public GamePanel.GameState State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        // ---------- clocks ----------
        readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        readonly DispatcherTimer _fixedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; // 20 Hz driver 
        readonly Stopwatch _updateWatch = new Stopwatch();
        readonly Stopwatch _fixedWatch = new Stopwatch();

        readonly Stopwatch _frameWatch = new Stopwatch();
        double _msFrameLast, _msFrameEma;
        double _msUploadLast, _msUploadEma;
        int _fpsFrames;
        double _fpsDisplay; // rolling FPS (windowed counter)

        // --- dynamic resolution (software raster) ---
        float _resScale = 1.0f;                 // internal render scale vs screen
        const double TargetMs = 16.6;
        const float ResMin = 0.55f, ResMax = 1.0f;
        double _lastRenderScale = 1.0; // track HiDPI scaling used by the backbuffer

        // Sky clear cache (single-cam fullscreen fast path)
        uint[] _skyCacheColor = null;
        int _skyCacheW, _skyCacheH;
        SkyKey _skyCacheKey;
        bool _skyCacheValid;
        // also track the camera yaw used to build the cache
        int _skyCacheViewYawKey;


        void TuneResolution(double opaqueMs, double transpMs)
        {
            double ms = opaqueMs + transpMs;

            // Simple hysteresis so it doesn't flap
            if (ms > TargetMs * 1.15) _resScale *= 0.92f;   // slow → shrink
            else if (ms < TargetMs * 0.85) _resScale *= 1.03f;   // fast → grow

            if (_resScale < ResMin) _resScale = ResMin;
            if (_resScale > ResMax) _resScale = ResMax;
        }


        bool _awakened, _started;
        bool _collidersWarm;
        bool _needsWarm;

        // step physics (60 Hz) with accumulator
        const double FIXED_DT = 1.0 / 60.0;
        double _fixedAccum = 0.0;

        // ---------- input ----------
        bool _mouseLook;
        SN.Vector2 _lastMouse;
        bool _hasLastMouse;
        IPointer? _capturedPointer;

        // ---------- play snapshot ----------
        string? _playSnapshotPath;

        // ---------- FPS HUD ----------
        readonly Stopwatch _fpsTick = new Stopwatch();
        readonly Stopwatch _fpsWindow = new Stopwatch();
        double _fpsEma; bool _fpsPrimed;

        // ---------- pass profiling ----------
        readonly Stopwatch _passWatch = new Stopwatch();
        double _msOpaqueLast, _msTranspLast;
        double _msOpaqueEma, _msTranspEma;

        // ---------- reusable backbuffer ----------
        uint[]? _bbColor;
        float[]? _bbZ;
        WriteableBitmap? _bbWB;
        int _bbW, _bbH;

        // ---------- render-on-change cache keys ----------
        bool _cacheValid;
        SN.Matrix4x4 _lastView, _lastProj;
        int _lastWKey, _lastHKey;

        // ---------- scene caches ----------
        Skybox? _sky;
        Light? _light;
        readonly List<Camera> _cams = new(4);
        bool _sceneHasTransparent;

        // Common fallback sky colors (avoid Color.Parse per frame)
        static readonly Color FallbackSkyTop = Color.FromRgb(0x1f, 0x1f, 0x1f);
        static readonly Color FallbackSkyBot = Color.FromRgb(0x0a, 0x0a, 0x0a);

        // HUD resources (no per-frame allocations)
        static readonly Typeface HudTypeface = new("Segoe UI");
        static readonly IBrush HudText = Brushes.White;
        static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        // light/sky keys + hashers
        static class HashUtil { public static int Mix(int h, int v) => unchecked(h * 397 ^ v); }

        struct LightKey : IEquatable<LightKey>
        {
            public int Type;   // 0=none,1=dir,2=point
            public int Dx, Dy, Dz;
            public int Px, Py, Pz;
            public int Range;
            public int DiffuseK, Ambient;

            public bool Equals(LightKey o) =>
                Type == o.Type && Dx == o.Dx && Dy == o.Dy && Dz == o.Dz &&
                Px == o.Px && Py == o.Py && Pz == o.Pz &&
                Range == o.Range && DiffuseK == o.DiffuseK && Ambient == o.Ambient;

            public override bool Equals(object? obj) => obj is LightKey k && Equals(k);

            public override int GetHashCode()
            {
                int h = Type;
                h = HashUtil.Mix(h, Dx); h = HashUtil.Mix(h, Dy); h = HashUtil.Mix(h, Dz);
                h = HashUtil.Mix(h, Px); h = HashUtil.Mix(h, Py); h = HashUtil.Mix(h, Pz);
                h = HashUtil.Mix(h, Range);
                h = HashUtil.Mix(h, DiffuseK);
                h = HashUtil.Mix(h, Ambient);
                return h;
            }
        }
        struct SkyKey : IEquatable<SkyKey>
        {
            public int R0, G0, B0, R1, G1, B1;
            public int YawScaled;
            public int BlendScaled;
            public int TexId;

            public bool Equals(SkyKey o) =>
                R0 == o.R0 && G0 == o.G0 && B0 == o.B0 &&
                R1 == o.R1 && G1 == o.G1 && B1 == o.B1 &&
                YawScaled == o.YawScaled && BlendScaled == o.BlendScaled && TexId == o.TexId;

            public override bool Equals(object? obj) => obj is SkyKey k && Equals(k);

            public override int GetHashCode()
            {
                int h = R0;
                h = HashUtil.Mix(h, G0); h = HashUtil.Mix(h, B0);
                h = HashUtil.Mix(h, R1); h = HashUtil.Mix(h, G1); h = HashUtil.Mix(h, B1);
                h = HashUtil.Mix(h, YawScaled);
                h = HashUtil.Mix(h, BlendScaled);
                h = HashUtil.Mix(h, TexId);
                return h;
            }
        }

        LightKey _lastLightKey;
        SkyKey _lastSkyKey;

        public GameView()
        {
            ClipToBounds = true;

            // UI/update timer (keeps ~60 FPS cadence and repaints)
            _updateTimer.Interval = TimeSpan.FromMilliseconds(16.666);
            _updateTimer.Tick += (_, __) =>
            {
                TickUpdate();
                InvalidateVisual();
            };

            // Physics driver 
            _fixedTimer.Interval = TimeSpan.FromMilliseconds(8);  // fast driver; accumulator holds 60 Hz
            _fixedTimer.Tick += (_, __) =>
            {
                TickFixedUpdate();
            };


            // Scene changes: rebuild **scene caches** once, then invalidate render cache
            SceneService.Changed += () =>
            {
                RebuildSceneCaches();
                _needsWarm = true;
                _cacheValid = false;
                InvalidateVisual();
            };

            StateProperty.Changed.AddClassHandler<GameView>((s, e) => s.OnStateChanged());

            Focusable = true;
            AttachedToVisualTree += (_, __) => Focus();
            LostFocus += (_, __) => ExitLookAndClear();
            DetachedFromVisualTree += (_, __) => ExitLookAndClear();

            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerMoved += OnPointerMoved;

            // Build initial caches so first frame is cheap
            RebuildSceneCaches();

            _fpsTick.Restart();
            _fpsWindow.Restart();
        }

        void RebuildSceneCaches()
        {
            _sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
            _light = SceneQuery.FindBehaviors<Light>().FirstOrDefault(l => l.Enabled);
            _cams.Clear();
            // Avoid deferred LINQ enumeration per frame
            foreach (var c in SceneQuery.FindBehaviors<Camera>())
                _cams.Add(c);

            _sceneHasTransparent = EstimateSceneHasTransparent();
        }

        // ---------- backbuffer ----------
        void EnsureBackbuffers(int w, int h, double renderScale)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);

            bool needNew =
                _bbWB == null || _bbW != w || _bbH != h ||
                _bbColor == null || _bbZ == null ||
                Math.Abs(_lastRenderScale - renderScale) > 0.0001;

            if (!needNew) return;

            _bbW = w; _bbH = h;
            _bbColor = new uint[w * h];
            _bbZ = new float[w * h];

            // IMPORTANT: DPI matches the visual root scaling so DIP→pixel is correct
            _bbWB = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96 * renderScale, 96 * renderScale),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            _lastRenderScale = renderScale;
            _cacheValid = false; // res/DPI change invalidates cached frame
        }




        void ExitLookAndClear()
        {
            if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
            _mouseLook = false; _hasLastMouse = false;
            Input.ClearAll();
            Input.FeedMouseDelta(0, 0);
        }

        void OnKeyDown(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var code = KeyMap.FromAvalonia(e.Key);
            Input.FeedKeyDown(code);

            if (code == KeyCode.Escape && _mouseLook)
            {
                _mouseLook = false;
                if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
                _hasLastMouse = false;
                Input.FeedMouseDelta(0, 0);
            }
        }

        void OnKeyUp(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            Input.FeedKeyUp(KeyMap.FromAvalonia(e.Key));
        }

        void OnPointerPressed(object? s, PointerPressedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Left);
            if (pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonDown(Core.Input.MouseButton.Middle);
            if (pt.Properties.IsRightButtonPressed)
            {
                Input.FeedMouseButtonDown(Core.Input.MouseButton.Right);
                _mouseLook = true;
                _capturedPointer = e.Pointer;
                try { _capturedPointer.Capture(this); } catch { }
                _lastMouse = new SN.Vector2((float)pt.Position.X, (float)pt.Position.Y);
                _hasLastMouse = true;
            }
        }
        void OnPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Left);
            if (!pt.Properties.IsMiddleButtonPressed) Input.FeedMouseButtonUp(Core.Input.MouseButton.Middle);
            if (!pt.Properties.IsRightButtonPressed)
            {
                Input.FeedMouseButtonUp(Core.Input.MouseButton.Right);
                _mouseLook = false;
                if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
                _hasLastMouse = false;
                Input.FeedMouseDelta(0, 0);
            }
        }
        void OnPointerMoved(object? s, PointerEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;
            var p = e.GetPosition(this);
            var cur = new SN.Vector2((float)p.X, (float)p.Y);
            if (_mouseLook && _hasLastMouse)
            {
                var dx = cur.X - _lastMouse.X; var dy = cur.Y - _lastMouse.Y;
                if (dx != 0 || dy != 0) Input.FeedMouseDelta(dx, dy);
            }
            _lastMouse = cur; _hasLastMouse = true;
        }

        // ---------- state ----------
        void OnStateChanged()
        {
            switch (State)
            {
                case GamePanel.GameState.Playing:
                    EnsurePlaySnapshot();
                    EnsureAwakeStart();
                    _needsWarm = true;
                    Focus();
                    Core.Time.Reset();
                    _updateWatch.Restart();
                    _fixedWatch.Restart();
                    Input.ClearAll();

                    _updateTimer.Interval = TimeSpan.FromMilliseconds(16.666);
                    _updateTimer.Start();

                    _fixedTimer.Interval = TimeSpan.FromMilliseconds(8);   // keep it fast
                    _fixedTimer.Start();

                    _fpsTick.Restart(); _fpsWindow.Restart();
                    _fpsPrimed = false; _fpsEma = 0;
                    _msOpaqueEma = _msTranspEma = 0;
                    _msOpaqueLast = _msTranspLast = 0;

                    _cacheValid = false;
                    RebuildSceneCaches();
                    break;

                case GamePanel.GameState.Paused:
                    _fixedTimer.Stop();
                    _updateTimer.Stop();
                    break;

                case GamePanel.GameState.Stopped:
                    _fixedTimer.Stop();
                    _updateTimer.Stop();
                    _updateWatch.Reset(); _fixedWatch.Reset();
                    CallOnDestroyAll();
                    RestorePlaySnapshot();
                    _awakened = _started = false;
                    _collidersWarm = false;
                    _needsWarm = true;
                    _cacheValid = false;
                    if (_capturedPointer != null) { try { _capturedPointer.Capture(null); } catch { } _capturedPointer = null; }
                    _mouseLook = false; _hasLastMouse = false;
                    Input.ClearAll();
                    break;
            }
            InvalidateVisual();
        }

        // ---------- snapshot ----------
        void EnsurePlaySnapshot()
        {
            if (_playSnapshotPath != null) return;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"GE_PlaySnapshot_{Guid.NewGuid():N}.scene");
            SceneService.SaveToFile(tmp);
            _playSnapshotPath = tmp;
        }
        void RestorePlaySnapshot()
        {
            if (_playSnapshotPath == null) return;
            SceneService.LoadFromFile(_playSnapshotPath);
            try { System.IO.File.Delete(_playSnapshotPath); } catch { }
            _playSnapshotPath = null;
            _collidersWarm = false;
            _needsWarm = true;
            _cacheValid = false;
        }

        // ---------- lifecycle drivers ----------
        void EnsureAwakeStart()
        {
            if (!_awakened) { ForEachBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { ForEachBehavior(b => b.__Start()); _started = true; }
        }

        void WarmAllColliders()
        {
            if (_collidersWarm) return;

            static void EnsureColliderReady(Collider c)
            {
                var t = c.GetType();
                string[] names = { "EnsureReady", "EnsureBaked", "Bake", "Precompute", "Rebuild", "SyncFromTransform", "Warm" };
                for (int i = 0; i < names.Length; i++)
                {
                    var n = names[i];
                    var m = t.GetMethod(n,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (m != null) { try { m.Invoke(c, null); } catch { } break; }
                }
            }

            var mcType = typeof(MeshCollider);
            var resolveMeshTargets =
                mcType.GetMethod("EnsureTargetsResolved",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? mcType.GetMethod("ResolveTargets",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            foreach (var root in SceneService.Root)
            {
                Traverse(root, b =>
                {
                    var mc = b as MeshCollider;
                    if (mc != null)
                    {
                        try { if (resolveMeshTargets != null) resolveMeshTargets.Invoke(mc, null); } catch { }
                    }
                    else
                    {
                        var c = b as Collider;
                        if (c != null) EnsureColliderReady(c);
                    }
                });
            }

            _collidersWarm = true;
        }

        void TickUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;

            if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

            var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
            _updateWatch.Restart();

            // Clamp huge spikes so controllers don’t “jump”
            if (dt > 0.05) dt = 0.05;  // ~20 FPS floor for Update

            Core.Time.BeginUpdate(dt);

            Input.NewFrame((float)dt);
            ForEachBehavior(b => b.__Update());
            ForEachBehavior(b => b.__LateUpdate());
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
                Core.Time.BeginFixedUpdate(FIXED_DT);
                ForEachBehavior(b => b.__FixedUpdate());
                _fixedAccum -= FIXED_DT;
            }
        }

        void CallOnDestroyAll() => ForEachBehavior(b => b.__OnDestroy());
        static void ForEachBehavior(Action<Behavior> a) { foreach (var r in SceneService.Root) Traverse(r, a); }
        static void Traverse(GameObject go, Action<Behavior> a)
        {
            foreach (var b in go.Behaviors) a(b);
            foreach (var c in go.Children) Traverse(c, a);
        }

        // ---------- render ----------

        static int ViewYawKey(in SN.Matrix4x4 view)
        {
            // camera world matrix
            if (!SN.Matrix4x4.Invert(view, out var inv)) return 0;
            // forward in world space
            var f = SN.Vector3.Normalize(new SN.Vector3(inv.M13, inv.M23, inv.M33));
            float yaw = MathF.Atan2(f.X, f.Z); // [-π, π]
            return (int)MathF.Round(yaw * 2048f); // quantize to ~0.003 rad (~0.17°)
        }


        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            _frameWatch.Restart();

            double dt = _fpsTick.IsRunning ? _fpsTick.Elapsed.TotalSeconds : 0.0;
            _fpsTick.Restart();
            UpdateFps(dt);

            void EndFrame()
            {
                _frameWatch.Stop();
                _msFrameLast = _frameWatch.Elapsed.TotalMilliseconds;
                Ema(ref _msFrameEma, _msFrameLast, 0.18);
            }

            double Wdip = Math.Max(1.0, Bounds.Width);
            double Hdip = Math.Max(1.0, Bounds.Height);

            var top = TopLevel.GetTopLevel(this);
            double rs = top?.RenderScaling ?? 1.0;

            int Wpx = Math.Max(1, (int)Math.Round(Wdip * rs));
            int Hpx = Math.Max(1, (int)Math.Round(Hdip * rs));

            int RW = Math.Max(1, (int)Math.Round(Wpx * _resScale));
            int RH = Math.Max(1, (int)Math.Round(Hpx * _resScale));

            EnsureBackbuffers(RW, RH, rs);
            var color = _bbColor!; var zbuf = _bbZ!;

            if (State != GamePanel.GameState.Playing)
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x17)), new Rect(0, 0, Wdip, Hdip));
                DrawFpsHud(ctx);
                EndFrame();
                return;
            }

            // ---------- Sky (from cache) ----------
            var sky = _sky;
            var skyTop = sky != null ? sky.Top : FallbackSkyTop;
            var skyBot = sky != null ? sky.Bottom : FallbackSkyBot;
            Texture2D? skyTex = sky != null ? sky.Texture : null;
            float skyBlend = Math.Clamp(sky != null ? sky.TextureBlend : 0f, 0f, 1f);

            SN.Vector3? sunDir = null;
            if (sky != null)
            {
                var baseSun = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));
                var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, sky.Yaw);
                sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
            }

            // ---------- Lighting ----------
            float Ambient = Math.Clamp(sky != null ? sky.Ambient : 0f, 0f, 1f);

            var light = _light;
            SN.Vector3 L = SN.Vector3.UnitY;
            float DiffuseK = 0f;
            bool lightIsPoint = false;
            SN.Vector3 lightPosW = SN.Vector3.Zero;
            float lightRange = 0f;

            if (light != null && light.gameObject != null)
            {
                float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
                float baseK = light.Intensity * (lum > 0f ? lum : 0.001f);
                DiffuseK = baseK > 0.001f ? baseK : 0.001f;

                if (light.Type == LightType.Directional)
                {
                    var m = TransformUtil.WorldFromTransform(light.gameObject.Transform);
                    var fwd = SN.Vector3.Normalize(new SN.Vector3(m.M13, m.M23, m.M33));
                    L = -fwd;
                    lightIsPoint = false;
                }
                else if (light.Type == LightType.Point)
                {
                    lightIsPoint = true;
                    var m = TransformUtil.WorldFromTransform(light.gameObject.Transform);
                    lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, m);
                    lightRange = Math.Max(0.001f, light.Range);
                }
            }

            var cams = _cams;

            // ---------- FULLSCREEN SINGLE-CAMERA FAST PATH + SKY CACHE ----------
            if (cams.Count == 1)
            {
                var cam = cams[0];
                var vp = SceneGraphUtil.ViewportPx(cam, RW, RH);
                int vx = vp.Item1, vy = vp.Item2, vw = vp.Item3, vh = vp.Item4;

                if (vx == 0 && vy == 0 && vw == RW && vh == RH)
                {
                    var vView = cam.GetViewMatrix();
                    var vProj = cam.GetProjectionMatrix(new Size(Wdip, Hdip));

                    var lightKey = BuildLightKey(lightIsPoint, L, lightPosW, lightRange, DiffuseK, Ambient);
                    var curSkyKey = BuildSkyKey(skyTop, skyBot, sky != null ? sky.Yaw : 0f, skyBlend, skyTex);

                    //  include camera yaw in sky-cache key
                    int curViewYawKey = ViewYawKey(vView);

                    bool changed =
                        !_cacheValid ||
                        _lastWKey != RW || _lastHKey != RH ||
                        !MatNearEqual(vView, _lastView) ||
                        !MatNearEqual(vProj, _lastProj) ||
                        !_lastLightKey.Equals(lightKey) ||
                        !_lastSkyKey.Equals(curSkyKey) ||
                        _needsWarm;

                    // ---- update sky cache if needed ----
                    bool needSky =
                        !_skyCacheValid ||
                        _skyCacheW != RW || _skyCacheH != RH ||
                        !_skyCacheKey.Equals(curSkyKey) ||
                        _skyCacheViewYawKey != curViewYawKey;   // rebuild when camera yaw changes

                    if (needSky)
                    {
                        _skyCacheW = RW; _skyCacheH = RH;
                        _skyCacheKey = curSkyKey;
                        _skyCacheViewYawKey = curViewYawKey;
                        _skyCacheColor = new uint[RW * RH];
                        _skyCacheValid = true;

                        CameraClear.ClearForCamera(cam, _skyCacheColor, zbuf, RW, RH,
                            vView, vProj,
                            skyTop, skyBot, sunDir,
                            skyTex, skyBlend,
                            sky != null ? sky.Yaw : 0f, sky != null ? sky.SeamFeather : 0.01f,
                            sky != null ? sky.KeyOutNearBlack : false, sky != null ? sky.KeyLuma : 0.08f);
                    }

                    if (!changed)
                    {
                        // copy cached sky to frame, reset Z to far
                        Array.Copy(_skyCacheColor, color, color.Length);
                        Array.Fill<float>(zbuf, 1f);

                        _passWatch.Restart();
                        BlitToScreen(ctx, _bbWB!, color, RW, RH, Wdip, Hdip);
                        _passWatch.Stop();
                        _msUploadLast = _passWatch.Elapsed.TotalMilliseconds;
                        Ema(ref _msUploadEma, _msUploadLast, 0.18);

                        DrawFpsHud(ctx);
                        EndFrame();
                        return;
                    }

                    // start from cached sky
                    Array.Copy(_skyCacheColor, color, color.Length);
                    Array.Fill<float>(zbuf, 1f);

                    // Opaque pass
                    _passWatch.Restart();
                    foreach (var root in SceneService.Root)
                        SceneRenderer.DrawNodeSolidZ(root, vView, vProj, SN.Matrix4x4.Identity,
                            color, zbuf, RW, RH,
                            L, DiffuseK, Ambient,
                            lightIsPoint, lightPosW, lightRange,
                            shadow: null);
                    _passWatch.Stop();
                    _msOpaqueLast = _passWatch.Elapsed.TotalMilliseconds;
                    Ema(ref _msOpaqueEma, _msOpaqueLast, 0.18);

                    // Transparent pass (optional)
                    _msTranspLast = 0;
                    if (_sceneHasTransparent)
                    {
                        _passWatch.Restart();
                        foreach (var root in SceneService.Root)
                            SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                                color, zbuf, RW, RH,
                                L, DiffuseK, Ambient,
                                lightIsPoint, lightPosW, lightRange,
                                shadow: null);
                        _passWatch.Stop();
                        _msTranspLast = _passWatch.Elapsed.TotalMilliseconds;
                        Ema(ref _msTranspEma, _msTranspLast, 0.18);
                    }

                    // update cache keys
                    _lastView = vView; _lastProj = vProj;
                    _lastLightKey = lightKey; _lastSkyKey = curSkyKey;
                    _lastWKey = RW; _lastHKey = RH;
                    _cacheValid = true;

                    _passWatch.Restart();
                    BlitToScreen(ctx, _bbWB!, color, RW, RH, Wdip, Hdip);
                    _passWatch.Stop();
                    _msUploadLast = _passWatch.Elapsed.TotalMilliseconds;
                    Ema(ref _msUploadEma, _msUploadLast, 0.18);

                    DrawFpsHud(ctx);
                    TuneResolution(_msOpaqueEma, _msTranspEma);
                    EndFrame();
                    return;
                }
            }

            // ---------- Multi-camera path ----------
            FillVerticalGradient(color, RW, RH, skyTop, skyBot);

            double totalOpaqueMs = 0, totalTranspMs = 0;

            for (int i = 0; i < cams.Count; i++)
            {
                var cam = cams[i];

                var vp = SceneGraphUtil.ViewportPx(cam, RW, RH);
                int vx = vp.Item1, vy = vp.Item2, vw = vp.Item3, vh = vp.Item4;

                int vwD = Math.Max(1, (int)Math.Round(vw / rs));
                int vhD = Math.Max(1, (int)Math.Round(vh / rs));

                var vColor = ArrayPool<uint>.Shared.Rent(vw * vh);
                var vZ = ArrayPool<float>.Shared.Rent(vw * vh);

                try
                {
                    var vView = cam.GetViewMatrix();
                    var vProj = cam.GetProjectionMatrix(new Size(vwD, vhD));

                    CameraClear.ClearForCamera(cam, vColor, vZ, vw, vh,
                        vView, vProj,
                        skyTop, skyBot, sunDir,
                        skyTex, skyBlend,
                        sky != null ? sky.Yaw : 0f, sky != null ? sky.SeamFeather : 0.01f,
                        sky != null ? sky.KeyOutNearBlack : false, sky != null ? sky.KeyLuma : 0.08f);

                    _passWatch.Restart();
                    foreach (var root in SceneService.Root)
                        SceneRenderer.DrawNodeSolidZ(root, vView, vProj, SN.Matrix4x4.Identity,
                            vColor, vZ, vw, vh,
                            L, DiffuseK, Ambient,
                            lightIsPoint, lightPosW, lightRange,
                            shadow: null);
                    _passWatch.Stop();
                    totalOpaqueMs += _passWatch.Elapsed.TotalMilliseconds;

                    if (_sceneHasTransparent)
                    {
                        _passWatch.Restart();
                        foreach (var root in SceneService.Root)
                            SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                                vColor, vZ, vw, vh,
                                L, DiffuseK, Ambient,
                                lightIsPoint, lightPosW, lightRange,
                                shadow: null);
                        _passWatch.Stop();
                        totalTranspMs += _passWatch.Elapsed.TotalMilliseconds;
                    }

                    ImageUtil.Blit(vColor, vw, vh, color, RW, RH, vx, vy);
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(vColor, false);
                    ArrayPool<float>.Shared.Return(vZ, false);
                }
            }

            _msOpaqueLast = totalOpaqueMs;
            _msTranspLast = totalTranspMs;
            Ema(ref _msOpaqueEma, _msOpaqueLast, 0.18);
            Ema(ref _msTranspEma, _msTranspLast, 0.18);

            _passWatch.Restart();
            BlitToScreen(ctx, _bbWB!, color, RW, RH, Wdip, Hdip);
            _passWatch.Stop();
            _msUploadLast = _passWatch.Elapsed.TotalMilliseconds;
            Ema(ref _msUploadEma, _msUploadLast, 0.18);

            DrawFpsHud(ctx);
            TuneResolution(_msOpaqueEma, _msTranspEma);
            EndFrame();
        }


        // ---------- helpers ----------
        static void Ema(ref double acc, double sample, double a)
        {
            acc = acc <= 0 ? sample : (1 - a) * acc + a * sample;
        }

        void UpdateFps(double dt)
        {
            // count frames and recompute FPS every 0.5s for stability
            _fpsFrames++;
            if (!_fpsWindow.IsRunning) _fpsWindow.Restart();
            if (_fpsWindow.ElapsedMilliseconds >= 500)
            {
                _fpsDisplay = _fpsFrames / _fpsWindow.Elapsed.TotalSeconds;
                _fpsFrames = 0;
                _fpsWindow.Restart();
            }
        }


        void DrawFpsHud(DrawingContext ctx)
        {
            string line1 = $"FPS: {_fpsDisplay:F1}   Frame: {_msFrameEma:F2} ms   Upload: {_msUploadEma:F2} ms";
            string line2 = $"Opaque: {_msOpaqueLast:F2} ms (EMA {_msOpaqueEma:F2})   Transparent: {_msTranspLast:F2} ms (EMA {_msTranspEma:F2})";

            const double font = 12;
            const double padX = 8, padY = 6;
            const double gapY = 2;

            double est1 = line1.Length * font * 0.62;
            double est2 = line2.Length * font * 0.62;
            double lineH = font * 1.4;

            double w = Math.Max(est1, est2) + padX * 2;
            double h = (lineH + gapY + lineH) + padY * 2;

            var bg = new Rect(6, 6, Math.Ceiling(w), Math.Ceiling(h));
            ctx.FillRectangle(HudBg, bg);

            double x = bg.X + padX;
            double y = bg.Y + padY;

            new TextLayout(line1, HudTypeface, font, HudText).Draw(ctx, new Point(x, y));
            y += lineH + gapY;
            new TextLayout(line2, HudTypeface, font, HudText).Draw(ctx, new Point(x, y));
        }


        static unsafe void BlitToScreen(
    DrawingContext ctx, WriteableBitmap wb, uint[] color,
    int srcW, int srcH, double dstWdip, double dstHdip)
        {
            using (var fb = wb.Lock())
            {
                byte* dst = (byte*)fb.Address;
                int rowB = fb.RowBytes;
                fixed (uint* src = color)
                {
                    int bytesW = srcW * 4;
                    if (rowB == bytesW)
                    {
                        Buffer.MemoryCopy(src, dst, (long)rowB * srcH, (long)bytesW * srcH);
                    }
                    else
                    {
                        for (int y = 0; y < srcH; y++)
                            Buffer.MemoryCopy(src + y * srcW, dst + y * rowB, rowB, bytesW);
                    }
                }
            }

            // Source rect is in device pixels of the WriteableBitmap;
            // Dest rect is in DIPs (control size).
            var srcRect = new Rect(0, 0, wb.PixelSize.Width, wb.PixelSize.Height);
            var dstRect = new Rect(0, 0, dstWdip, dstHdip);
            ctx.DrawImage(wb, srcRect, dstRect);
        }




        private static void FillVerticalGradient(uint[] dst, int W, int H, Color top, Color bot)
        {
            static uint Pack(byte r, byte g, byte b) => (uint)(0xFF << 24 | (uint)b << 16 | (uint)g << 8 | r);

            int r0 = top.R, g0 = top.G, b0 = top.B;
            int r1 = bot.R, g1 = bot.G, b1 = bot.B;

            if (H <= 1)
            {
                uint c = Pack((byte)r0, (byte)g0, (byte)b0);
                for (int i = 0; i < dst.Length; i++) dst[i] = c;
                return;
            }

            for (int y = 0; y < H; y++)
            {
                int r = r0 + (r1 - r0) * y / (H - 1);
                int g = g0 + (g1 - g0) * y / (H - 1);
                int b = b0 + (b1 - b0) * y / (H - 1);

                uint row = Pack((byte)r, (byte)g, (byte)b);
                int off = y * W;
                for (int x = 0; x < W; x++) dst[off + x] = row;
            }
        }

        // ---------- render-on-change helpers ----------
        static int Q(float f, float scale = 2048f) => (int)MathF.Round(f * scale);
        static bool MatNearEqual(in SN.Matrix4x4 a, in SN.Matrix4x4 b, float eps = 1e-4f)
        {
            return MathF.Abs(a.M11 - b.M11) < eps && MathF.Abs(a.M12 - b.M12) < eps && MathF.Abs(a.M13 - b.M13) < eps && MathF.Abs(a.M14 - b.M14) < eps &&
                   MathF.Abs(a.M21 - b.M21) < eps && MathF.Abs(a.M22 - b.M22) < eps && MathF.Abs(a.M23 - b.M23) < eps && MathF.Abs(a.M24 - b.M24) < eps &&
                   MathF.Abs(a.M31 - b.M31) < eps && MathF.Abs(a.M32 - b.M32) < eps && MathF.Abs(a.M33 - b.M33) < eps && MathF.Abs(a.M34 - b.M34) < eps &&
                   MathF.Abs(a.M41 - b.M41) < eps && MathF.Abs(a.M42 - b.M42) < eps && MathF.Abs(a.M43 - b.M43) < eps && MathF.Abs(a.M44 - b.M44) < eps;
        }
        static LightKey BuildLightKey(bool lightIsPoint, SN.Vector3 L, SN.Vector3 lightPosW, float lightRange, float diffuseK, float ambient)
        {
            return new LightKey
            {
                Type = lightIsPoint ? 2 : (diffuseK > 0f ? 1 : 0),
                Dx = Q(L.X),
                Dy = Q(L.Y),
                Dz = Q(L.Z),
                Px = Q(lightPosW.X),
                Py = Q(lightPosW.Y),
                Pz = Q(lightPosW.Z),
                Range = Q(lightRange, 64f),
                DiffuseK = Q(diffuseK, 1024f),
                Ambient = Q(ambient, 1024f)
            };
        }
        static SkyKey BuildSkyKey(Color top, Color bot, float yaw, float blend, Texture2D? tex)
        {
            return new SkyKey
            {
                R0 = top.R,
                G0 = top.G,
                B0 = top.B,
                R1 = bot.R,
                G1 = bot.G,
                B1 = bot.B,
                YawScaled = Q(yaw, 1000f),
                BlendScaled = Q(blend, 1000f),
                TexId = tex?.GetHashCode() ?? 0
            };
        }

        // ---------- transparency estimator ----------
        static bool MaterialHasTransparency(Material? m)
        {
            if (m == null) return false;

            try
            {
                var p = m.GetType().GetProperty("Opacity",
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null && p.GetValue(m) is float f && f < 0.999f) return true;
            }
            catch { }

            try
            {
                var list = m.Textures;
                if (list != null)
                {
                    foreach (var slot in list)
                    {
                        if (slot == null) continue;
                        var pu = slot.GetType().GetProperty("Usage",
                                 System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        var usage = pu?.GetValue(slot)?.ToString()?.ToLowerInvariant() ?? "";
                        if (usage.Contains("opacity") || usage.Contains("alpha") || usage.Contains("transp"))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }
        static bool EstimateSceneHasTransparent()
        {
            foreach (var mr in SceneQuery.FindBehaviors<MeshRenderer>())
                if (MaterialHasTransparency(mr.Material)) return true;
            return false;
        }
    }
}