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
        // Keep update timer defined but unused during Play (single 20 Hz loop).
        readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        readonly DispatcherTimer _fixedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; // 20 Hz
        readonly Stopwatch _updateWatch = new Stopwatch();
        readonly Stopwatch _fixedWatch = new Stopwatch();

        bool _awakened, _started;
        bool _collidersWarm;
        bool _needsWarm; // warm colliders only when scene actually changes

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

        // ---------- pass profiling (always on) ----------
        readonly Stopwatch _passWatch = new Stopwatch();
        double _msOpaqueLast, _msTranspLast;
        double _msOpaqueEma, _msTranspEma;

        // ---------- reusable backbuffer ----------
        uint[]? _bbColor;
        float[]? _bbZ;
        WriteableBitmap? _bbWB;
        int _bbW, _bbH;

        public GameView()
        {
            ClipToBounds = true;

            // We do not run _updateTimer while playing (single 20 Hz loop), but keep this for non-Play.
            _updateTimer.Tick += (_, __) => { TickUpdate(); InvalidateVisual(); };

            // Single driver at 20 Hz: FixedUpdate -> Update/LateUpdate -> render
            _fixedTimer.Tick += (_, __) =>
            {
                TickFixedUpdate();
                TickUpdate();
                InvalidateVisual();
            };

            // When the scene changes, request a one-shot collider warm and a repaint.
            SceneService.Changed += () => { _needsWarm = true; InvalidateVisual(); };

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

            _fpsTick.Restart();
            _fpsWindow.Restart();
        }

        // ---------- backbuffer ----------
        void EnsureBackbuffers(int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (_bbWB != null && _bbW == w && _bbH == h && _bbColor != null && _bbZ != null) return;

            _bbW = w; _bbH = h;
            _bbColor = new uint[w * h];
            _bbZ = new float[w * h];
            _bbWB = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                        PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        // ---------- input ----------
        static KeyCode MapKey(Key k) => k switch
        {
            Key.W => KeyCode.W,
            Key.A => KeyCode.A,
            Key.S => KeyCode.S,
            Key.D => KeyCode.D,
            Key.Up => KeyCode.UpArrow,
            Key.Down => KeyCode.DownArrow,
            Key.Left => KeyCode.LeftArrow,
            Key.Right => KeyCode.RightArrow,
            Key.Space => KeyCode.Space,
            Key.LeftShift => KeyCode.LeftShift,
            Key.Escape => KeyCode.Escape,
            _ => KeyCode.None
        };

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
            var code = MapKey(e.Key);
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
            Input.FeedKeyUp(MapKey(e.Key));
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

                    _updateTimer.Stop();
                    _fixedTimer.Interval = TimeSpan.FromMilliseconds(50);
                    _fixedTimer.Start();

                    _fpsTick.Restart(); _fpsWindow.Restart();
                    _fpsPrimed = false; _fpsEma = 0;
                    _msOpaqueEma = _msTranspEma = 0;
                    _msOpaqueLast = _msTranspLast = 0;
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

            double dt = _fixedWatch.IsRunning ? _fixedWatch.Elapsed.TotalSeconds : _fixedTimer.Interval.TotalSeconds;
            _fixedWatch.Restart();
            Core.Time.BeginFixedUpdate(dt);
            ForEachBehavior(b => b.__FixedUpdate());
        }

        void CallOnDestroyAll() => ForEachBehavior(b => b.__OnDestroy());
        static void ForEachBehavior(Action<Behavior> a) { foreach (var r in SceneService.Root) Traverse(r, a); }
        static void Traverse(GameObject go, Action<Behavior> a)
        {
            foreach (var b in go.Behaviors) a(b);
            foreach (var c in go.Children) Traverse(c, a);
        }

        // ---------- render ----------
        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            // FPS integrate
            double dt = _fpsTick.IsRunning ? _fpsTick.Elapsed.TotalSeconds : 0.0;
            _fpsTick.Restart(); UpdateFps(dt);

            int W = Math.Max(1, (int)Bounds.Width);
            int H = Math.Max(1, (int)Bounds.Height);
            EnsureBackbuffers(W, H);
            var color = _bbColor!; var zbuf = _bbZ!;

            if (State != GamePanel.GameState.Playing)
            {
                ctx.FillRectangle(new SolidColorBrush(Color.Parse("#121417")), new Rect(0, 0, W, H));
                DrawFpsHud(ctx);
                return;
            }

            // ---------- Sky ----------
            var sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
            var skyTop = sky != null ? sky.Top : Color.Parse("#1f1f1f");
            var skyBot = sky != null ? sky.Bottom : Color.Parse("#0a0a0a");
            Texture2D? skyTex = sky != null ? sky.Texture : null;
            float skyBlend = Math.Clamp(sky != null ? (sky.TextureBlend) : 0f, 0f, 1f);

            SN.Vector3? sunDir = null;
            if (sky != null)
            {
                var baseSun = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));
                var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, sky.Yaw);
                sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
            }

            // ---------- Lighting ----------
            float Ambient = Math.Clamp(sky != null ? sky.Ambient : 0f, 0f, 1f);

            var light = SceneQuery.FindBehaviors<Light>().FirstOrDefault(l => l.Enabled);
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

            // ---------- Cameras ----------
            var cams = SceneQuery.FindBehaviors<Camera>().ToList();

            // Fast path: one full-screen camera
            if (cams.Count == 1)
            {
                var cam = cams[0];
                var (vx, vy, vw, vh) = SceneGraphUtil.ViewportPx(cam, W, H);
                if (vx == 0 && vy == 0 && vw == W && vh == H)
                {
                    var vView = cam.GetViewMatrix();
                    var vProj = cam.GetProjectionMatrix(new Size(W, H));

                    CameraClear.ClearForCamera(cam, color, zbuf, W, H,
                        vView, vProj,
                        skyTop, skyBot, sunDir,
                        skyTex, skyBlend,
                        sky != null ? sky.Yaw : 0f, sky != null ? sky.SeamFeather : 0.01f,
                        sky != null ? sky.KeyOutNearBlack : false, sky != null ? sky.KeyLuma : 0.08f);

                    // ---- Opaque pass (timed) ----
                    _passWatch.Restart();
                    foreach (var root in SceneService.Root)
                        SceneRenderer.DrawNodeSolidZ(root, vView, vProj, SN.Matrix4x4.Identity,
                            color, zbuf, W, H,
                            L, DiffuseK, Ambient,
                            lightIsPoint, lightPosW, lightRange,
                            shadow: null);
                    _passWatch.Stop();
                    _msOpaqueLast = _passWatch.Elapsed.TotalMilliseconds;
                    Ema(ref _msOpaqueEma, _msOpaqueLast, 0.18);

                    // ---- Transparent pass (timed) ----
                    _passWatch.Restart();
                    foreach (var root in SceneService.Root)
                        SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                            color, zbuf, W, H,
                            L, DiffuseK, Ambient,
                            lightIsPoint, lightPosW, lightRange,
                            shadow: null);
                    _passWatch.Stop();
                    _msTranspLast = _passWatch.Elapsed.TotalMilliseconds;
                    Ema(ref _msTranspEma, _msTranspLast, 0.18);

                    BlitToScreen(ctx, _bbWB!, color, W, H);
                    DrawFpsHud(ctx);
                    return;
                }
            }

            // Multi-camera: cheap master background, then per-camera clears
            FillVerticalGradient(color, W, H, skyTop, skyBot);

            double totalOpaqueMs = 0, totalTranspMs = 0;

            foreach (var cam in cams)
            {
                var (vx, vy, vw, vh) = SceneGraphUtil.ViewportPx(cam, W, H);

                var vColor = ArrayPool<uint>.Shared.Rent(vw * vh);
                var vZ = ArrayPool<float>.Shared.Rent(vw * vh);

                try
                {
                    var vView = cam.GetViewMatrix();
                    var vProj = cam.GetProjectionMatrix(new Size(vw, vh));

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

                    _passWatch.Restart();
                    foreach (var root in SceneService.Root)
                        SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                            vColor, vZ, vw, vh,
                            L, DiffuseK, Ambient,
                            lightIsPoint, lightPosW, lightRange,
                            shadow: null);
                    _passWatch.Stop();
                    totalTranspMs += _passWatch.Elapsed.TotalMilliseconds;

                    ImageUtil.Blit(vColor, vw, vh, color, W, H, vx, vy);
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

            BlitToScreen(ctx, _bbWB!, color, W, H);
            DrawFpsHud(ctx);
        }

        // ---------- helpers ----------
        static void Ema(ref double acc, double sample, double a)
        {
            acc = acc <= 0 ? sample : (1 - a) * acc + a * sample;
        }

        void UpdateFps(double dt)
        {
            const double a = 0.12;
            double inst = dt > 1e-6 ? 1.0 / dt : 0.0;
            if (!_fpsPrimed) { _fpsEma = inst; _fpsPrimed = true; }
            else _fpsEma = (1 - a) * _fpsEma + a * inst;

            if (_fpsWindow.ElapsedMilliseconds >= 1000)
            {
                _fpsWindow.Restart();
            }
        }

        void DrawFpsHud(DrawingContext ctx)
        {
            string line1 = $"FPS: {_fpsEma:F1}";
            string line2 = $"Opaque: {_msOpaqueLast:F2} ms (EMA {_msOpaqueEma:F2})   Transparent: {_msTranspLast:F2} ms (EMA {_msTranspEma:F2})";

            const double font = 12;
            const double padX = 8, padY = 6;
            const double gapY = 2;

            // Heuristic metrics – avoids TextLayout.Size/Bounds so it compiles everywhere
            Func<string, double> estWidth = s => s.Length * font * 0.62;   // avg char width ≈0.62em
            double lineH = font * 1.4;                                     // line height ≈1.4em

            double w = Math.Max(estWidth(line1), estWidth(line2)) + padX * 2;
            double h = (lineH + gapY + lineH) + padY * 2;

            var bg = new Rect(6, 6, Math.Ceiling(w), Math.Ceiling(h));
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#80000000")), bg);

            var tf = new Typeface("Segoe UI");

            // Draw text 
            double x = bg.X + padX;
            double y = bg.Y + padY;

            var l1 = new TextLayout(line1, tf, font, Brushes.White);
            l1.Draw(ctx, new Point(x, y));

            y += lineH + gapY;
            var l2 = new TextLayout(line2, tf, font, Brushes.White);
            l2.Draw(ctx, new Point(x, y));
        }



        static unsafe void BlitToScreen(DrawingContext ctx, WriteableBitmap wb, uint[] color, int W, int H)
        {
            using (var fb = wb.Lock())
            {
                byte* dst = (byte*)fb.Address;
                int rowB = fb.RowBytes;
                fixed (uint* src = color)
                {
                    for (int y = 0; y < H; y++)
                        Buffer.MemoryCopy(src + y * W, dst + y * rowB, rowB, W * 4);
                }
            }
            ctx.DrawImage(wb, new Rect(0, 0, W, H));
        }

        private static void FillVerticalGradient(uint[] dst, int W, int H, Color top, Color bot)
        {
            Func<byte, byte, byte, uint> Pack = (r, g, b) => (uint)(0xFF << 24 | (uint)b << 16 | (uint)g << 8 | r);

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
    }
}
