using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Diagnostics;
using Avalonia.Input;
using System.IO;
using static Game_Engine.Core.Component.CapsuleCollider;
using Game_Engine.Core.Input;

namespace Game_Engine.Views
{
    public class GameView : Control
    {
        // Mirror the GamePanel state (we bind/forward to this)
        public static readonly StyledProperty<GamePanel.GameState> StateProperty =
            AvaloniaProperty.Register<GameView, GamePanel.GameState>(nameof(State), GamePanel.GameState.Stopped);

        public GamePanel.GameState State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        // --- Loop clocks ---
        readonly DispatcherTimer _updateTimer = new DispatcherTimer();     // ~60Hz
        readonly DispatcherTimer _fixedTimer = new DispatcherTimer();      // 50Hz (20ms)

        readonly Stopwatch _updateWatch = new();
        readonly Stopwatch _fixedWatch = new();

        bool _awakened, _started;
        // mark when we must refresh collider targets 
        bool _collidersWarm;

        //INPUT
        bool _mouseLook;
        SN.Vector2 _lastMouse;
        bool _hasLastMouse;
        private Avalonia.Input.IPointer _capturedPointer;




        // ---------- Play-mode snapshot -----------------------------------------
        string? _playSnapshotPath; // temp .scene file path 

        public GameView()
        {
            ClipToBounds = true;

            _updateTimer.Interval = TimeSpan.FromMilliseconds(16.666); // ≈60Hz
            _updateTimer.Tick += (_, __) => { TickUpdate(); InvalidateVisual(); };

            _fixedTimer.Interval = TimeSpan.FromMilliseconds(20);      // 50Hz
            _fixedTimer.Tick += (_, __) => TickFixedUpdate();

            // Repaint when the scene changes (materials, transforms, etc.)
            SceneService.Changed += () =>
            {
                _collidersWarm = false; //  edits may invalidate targets
                InvalidateVisual();
            };

            // React to State changes
            StateProperty.Changed.AddClassHandler<GameView>((s, e) => s.OnStateChanged());

            Focusable = true;
            this.AttachedToVisualTree += (_, __) => Focus();

            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;

            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerMoved += OnPointerMoved;

            this.LostFocus += (_, __) => ExitLookAndClear();
            this.DetachedFromVisualTree += (_, __) => ExitLookAndClear();


        }

        void ExitLookAndClear()
        {
            if (_capturedPointer != null)
            {
                try { _capturedPointer.Capture(null); } catch { }
                _capturedPointer = null;
            }
            _mouseLook = false;
            _hasLastMouse = false;

            Game_Engine.Core.Input.Input.ClearAll();
            Game_Engine.Core.Input.Input.FeedMouseDelta(0, 0);
        }




        static Game_Engine.Core.Input.KeyCode MapKey(Avalonia.Input.Key k)
        {
            switch (k)
            {
                case Avalonia.Input.Key.W: return Game_Engine.Core.Input.KeyCode.W;
                case Avalonia.Input.Key.A: return Game_Engine.Core.Input.KeyCode.A;
                case Avalonia.Input.Key.S: return Game_Engine.Core.Input.KeyCode.S;
                case Avalonia.Input.Key.D: return Game_Engine.Core.Input.KeyCode.D;
                case Avalonia.Input.Key.Up: return Game_Engine.Core.Input.KeyCode.UpArrow;
                case Avalonia.Input.Key.Down: return Game_Engine.Core.Input.KeyCode.DownArrow;
                case Avalonia.Input.Key.Left: return Game_Engine.Core.Input.KeyCode.LeftArrow;
                case Avalonia.Input.Key.Right: return Game_Engine.Core.Input.KeyCode.RightArrow;
                case Avalonia.Input.Key.Space: return Game_Engine.Core.Input.KeyCode.Space;
                case Avalonia.Input.Key.LeftShift: return Game_Engine.Core.Input.KeyCode.LeftShift;
                case Avalonia.Input.Key.Escape: return Game_Engine.Core.Input.KeyCode.Escape;
                default: return Game_Engine.Core.Input.KeyCode.None;
            }
        }


        void OnKeyDown(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;

            var code = MapKey(e.Key);
            Game_Engine.Core.Input.Input.FeedKeyDown(code);

            // Toggle/exit mouse look with Escape
            if (code == Game_Engine.Core.Input.KeyCode.Escape && _mouseLook)
            {
                _mouseLook = false;
                if (_capturedPointer != null)
                {
                    try { _capturedPointer.Capture(null); } catch { }
                    _capturedPointer = null;
                }
                _hasLastMouse = false;
                Game_Engine.Core.Input.Input.FeedMouseDelta(0, 0);
            }
        }





        void OnKeyUp(object? s, KeyEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;

            var code = MapKey(e.Key);
            Game_Engine.Core.Input.Input.FeedKeyUp(code);
        }

        void OnPointerPressed(object? s, PointerPressedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;

            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsLeftButtonPressed)
                Game_Engine.Core.Input.Input.FeedMouseButtonDown(Game_Engine.Core.Input.MouseButton.Left);

            if (pt.Properties.IsRightButtonPressed)
            {
                Game_Engine.Core.Input.Input.FeedMouseButtonDown(Game_Engine.Core.Input.MouseButton.Right);

                _mouseLook = true;

                _capturedPointer = e.Pointer;
                try { _capturedPointer.Capture(this); } catch { }

                // seed so first move has no spike
                _lastMouse = new SN.Vector2((float)pt.Position.X, (float)pt.Position.Y);
                _hasLastMouse = true;
            }

            if (pt.Properties.IsMiddleButtonPressed)
                Game_Engine.Core.Input.Input.FeedMouseButtonDown(Game_Engine.Core.Input.MouseButton.Middle);
        }




        void OnPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;

            var pt = e.GetCurrentPoint(this);

            if (!pt.Properties.IsLeftButtonPressed)
                Game_Engine.Core.Input.Input.FeedMouseButtonUp(Game_Engine.Core.Input.MouseButton.Left);

            if (!pt.Properties.IsRightButtonPressed)
            {
                Game_Engine.Core.Input.Input.FeedMouseButtonUp(Game_Engine.Core.Input.MouseButton.Right);
                _mouseLook = false;

                if (_capturedPointer != null)
                {
                    try { _capturedPointer.Capture(null); } catch { }
                    _capturedPointer = null;
                }
                _hasLastMouse = false;
                Game_Engine.Core.Input.Input.FeedMouseDelta(0, 0);
            }

            if (!pt.Properties.IsMiddleButtonPressed)
                Game_Engine.Core.Input.Input.FeedMouseButtonUp(Game_Engine.Core.Input.MouseButton.Middle);
        }





        void OnPointerMoved(object? s, PointerEventArgs e)
        {
            if (State != GamePanel.GameState.Playing) return;

            var p = e.GetPosition(this);
            var cur = new SN.Vector2((float)p.X, (float)p.Y);

            if (_mouseLook)
            {
                if (_hasLastMouse)
                {
                    var dx = cur.X - _lastMouse.X;
                    var dy = cur.Y - _lastMouse.Y;
                    Game_Engine.Core.Input.Input.FeedMouseDelta(dx, dy);
                }
                _lastMouse = cur;
                _hasLastMouse = true;
            }
            else
            {
                // Not in look mode: just refresh last so first click doesn't spike
                _lastMouse = cur;
                _hasLastMouse = true;
            }
        }



        void OnStateChanged()
        {
            switch (State)
            {
                case GamePanel.GameState.Playing:
                    EnsurePlaySnapshot();
                    EnsureAwakeStart();
                    WarmAllColliders();
                    Focus();
                    Game_Engine.Core.Time.Reset();
                    _updateWatch.Restart();
                    _fixedWatch.Restart();
                    Game_Engine.Core.Input.Input.ClearAll();
                    _fixedTimer.Start();
                    _updateTimer.Start();
                    break;

                case GamePanel.GameState.Paused:
                    _fixedTimer.Stop();
                    _updateTimer.Stop();
                    break;

                case GamePanel.GameState.Stopped:
                    _fixedTimer.Stop();
                    _updateTimer.Stop();
                    _updateWatch.Reset();
                    _fixedWatch.Reset();
                    CallOnDestroyAll();
                    RestorePlaySnapshot();
                    _awakened = _started = false;
                    _collidersWarm = false;

                    if (_capturedPointer != null)
                    {
                        try { _capturedPointer.Capture(null); } catch { }
                        _capturedPointer = null;
                    }
                    _mouseLook = false; _hasLastMouse = false;
                    Game_Engine.Core.Input.Input.ClearAll();
                    break;
            }
            InvalidateVisual();
        }



        // ---------- Snapshot helpers -------------------------------------------
        void EnsurePlaySnapshot()
        {
            if (_playSnapshotPath != null) return; // already captured
            var tmp = Path.Combine(Path.GetTempPath(),
                $"GE_PlaySnapshot_{Guid.NewGuid():N}.scene");
            SceneService.SaveToFile(tmp);
            _playSnapshotPath = tmp;
        }

        void RestorePlaySnapshot()
        {
            if (_playSnapshotPath == null) return;
            SceneService.LoadFromFile(_playSnapshotPath);
            try { File.Delete(_playSnapshotPath); } catch { }
            _playSnapshotPath = null;

            // freshly loaded scene: targets are cold; prewarm now
            _collidersWarm = false;
            WarmAllColliders();
        }


        // ----- Lifecycle drivers -----

        // Ensure *all* colliders are ready for queries.
        // - MeshCollider: resolve target meshes (triangle soup) once edits/loads happen
        // - Other Collider types (Box/Capsule/etc.): usually no-op, but we call a few
        //   optional "prep" methods via reflection 
        void WarmAllColliders()
        {
            if (_collidersWarm) return;

            // Methods we’ll try on generic Collider types if present (optional)
            static void EnsureColliderReady(Collider c)
            {
                var t = c.GetType();
                // Try a few common names; all optional and safe
                string[] names =
                {
                    "EnsureReady", "EnsureBaked", "Bake", "Precompute",
                    "Rebuild", "SyncFromTransform", "Warm"
                };

                foreach (var name in names)
                {
                    var m = t.GetMethod(name,
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic,
                            binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (m != null)
                    {
                        try { m.Invoke(c, null); } catch { /* ignore */ }
                        break; // first match is enough
                    }
                }
            }

            // Specific resolver for MeshCollider (preferred/explicit)
            var mcType = typeof(MeshCollider);
            var resolveMeshTargets =
                mcType.GetMethod("EnsureTargetsResolved",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)
                ?? mcType.GetMethod("ResolveTargets",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);

            foreach (var root in SceneService.Root)
            {
                Traverse(root, b =>
                {
                    if (b is MeshCollider mc)
                    {
                        // Make sure multi-target mesh list is up to date (paths -> meshes)
                        try { resolveMeshTargets?.Invoke(mc, null); } catch { /* ignore */ }
                    }
                    else if (b is Collider c)
                    {
                        // BoxCollider / CapsuleCollider / any custom collider
                        EnsureColliderReady(c);
                    }
                });
            }

            _collidersWarm = true;
        }



        void EnsureAwakeStart()
        {
            if (!_awakened) { ForEachBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { ForEachBehavior(b => b.__Start()); _started = true; }
        }

        void TickUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;

            // keep colliders fresh if scene changed
            WarmAllColliders();

            var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
            _updateWatch.Restart();
            Game_Engine.Core.Time.BeginUpdate(dt);

            // >>> INPUT: start-of-frame
            Game_Engine.Core.Input.Input.NewFrame((float)dt);

            ForEachBehavior(b => b.__Update());
            ForEachBehavior(b => b.__LateUpdate());

            // let the UI (Inspector/SceneView) know values changed this frame
            SceneService.NotifyChanged();
        }

        void TickFixedUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;

            // Physics runs here—make sure colliders are ready.
            WarmAllColliders();

            double dt = _fixedWatch.IsRunning ? _fixedWatch.Elapsed.TotalSeconds : _fixedTimer.Interval.TotalSeconds;
            _fixedWatch.Restart();
            Game_Engine.Core.Time.BeginFixedUpdate(dt);

            ForEachBehavior(b => b.__FixedUpdate());
        }

        void CallOnDestroyAll()
        {
            ForEachBehavior(b => b.__OnDestroy());
        }

        static void ForEachBehavior(Action<Behavior> action)
        {
            // Traverse the scene and call on every enabled Behavior
            foreach (var root in SceneService.Root)
                Traverse(root, action);
        }

        static void Traverse(GameObject go, Action<Behavior> action)
        {
            // Behaviors first (consistent with Unity‐like expectations)
            foreach (var b in go.Behaviors)
                action(b);
            // Then children
            foreach (var c in go.Children)
                Traverse(c, action);
        }

        // ----- Rendering (Game camera path) -----

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            var size = Bounds.Size;
            int W = Math.Max(1, (int)size.Width);
            int H = Math.Max(1, (int)size.Height);

            // --- Gate: only render during Play ---
            if (State != GamePanel.GameState.Playing)
            {
                ctx.FillRectangle(new SolidColorBrush(Color.Parse("#121417")), new Rect(0, 0, W, H));
                return;
            }
            var color = new uint[W * H];
            var zbuf = new float[W * H];

            // Skybox (same, but NEVER affected by scene Light)
            var sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
            var skyTop = sky?.Top ?? Color.Parse("#1f1f1f");
            var skyBot = sky?.Bottom ?? Color.Parse("#0a0a0a");

            Texture2D skyTex = null;
            float skyBlend = 0f;
            if (sky != null)
            {
                skyTex = sky.Texture;
                skyBlend = Math.Clamp(sky.TextureBlend, 0f, 1f);
            }

            // Lighting: single Light for now (matches SceneView’s current approach)
            var light = SceneQuery.FindBehaviors<Light>().FirstOrDefault();

            // Defaults
            SN.Vector3 L = SN.Vector3.Normalize(new SN.Vector3(0.35f, 0.9f, 0.45f));
            float Ambient = Math.Clamp(sky?.Ambient ?? 0f, 0f, 1f);
            float DiffuseK = 1f;

            bool lightIsPoint = false;
            SN.Vector3 lightPosW = SN.Vector3.Zero;
            float lightRange = 10f;

            if (light != null)
            {
                float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
                DiffuseK *= MathF.Max(0.01f, light.Intensity * lum);

                if (light.Type == LightType.Directional && light.gameObject != null)
                {
                    // Light direction = -forward in WORLD space
                    var go = light.gameObject;
                    var m = TransformUtil.WorldFromTransform(go.Transform);
                    var fwd = SN.Vector3.Normalize(new SN.Vector3(m.M13, m.M23, m.M33));
                    L = -fwd;
                }
                else if (light.Type == LightType.Point && light.gameObject != null)
                {
                    lightIsPoint = true;
                    var m = TransformUtil.WorldFromTransform(light.gameObject.Transform);
                    lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, m);
                    lightRange = Math.Max(0.001f, light.Range);
                }
            }

            // sun hotspot purely from sky yaw (unlit sky)
            SN.Vector3? sunDir = null;
            if (sky != null)
            {
                var baseSun = SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));
                var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, sky.Yaw);
                sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
            }

            // Cameras in “Game” — use in-scene cameras; if none, render nothing (or preview fallback)
            var cams = SceneQuery.FindBehaviors<Camera>().ToList();

            if (cams.Count == 0)
            {
                // Fallback: just fill with sky so the Game tab shows 
                var view = SN.Matrix4x4.Identity;
                var proj = SN.Matrix4x4.Identity;
                Core.Sky.FillWorldUp(color, zbuf, W, H, view, proj,
                    skyTop, skyBot, sunDir, skyTex, skyBlend,
                    sky?.Yaw ?? 0f, sky?.SeamFeather ?? 0.01f,
                    sky?.KeyOutNearBlack ?? false, sky?.KeyLuma ?? 0.08f,
                    zWriteNdc: 1f - 1e-6f);

                Blit(ctx, color, W, H);
                return;
            }

            // Render all cameras with their normalized viewports (like game)
            foreach (var cam in cams)
            {
                // compute pixel viewport
                var (vx, vy, vw, vh) = SceneGraphUtil.ViewportPx(cam, W, H);

                var vColor = new uint[vw * vh];
                var vZ = new float[vw * vh];

                var vView = cam.GetViewMatrix();
                var vProj = cam.GetProjectionMatrix(new Size(vw, vh));

                // Clear per camera (sky or solid)
                CameraClear.ClearForCamera(cam, vColor, vZ, vw, vh,
                    vView, vProj,
                    skyTop, skyBot, sunDir,
                    skyTex, skyBlend,
                    sky?.Yaw ?? 0f, sky?.SeamFeather ?? 0.01f,
                    sky?.KeyOutNearBlack ?? false, sky?.KeyLuma ?? 0.08f);

                // Solid pass
                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ(root, vView, vProj, SN.Matrix4x4.Identity,
                        vColor, vZ, vw, vh,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange,
                        shadow: null);

                // Transparent queue
                foreach (var root in SceneService.Root)
                    SceneRenderer.DrawNodeSolidZ_QueueTransparent(root, vView, vProj, SN.Matrix4x4.Identity,
                        vColor, vZ, vw, vh,
                        L, DiffuseK, Ambient,
                        lightIsPoint, lightPosW, lightRange,
                        shadow: null);

                // Blit into master backbuffer
                ImageUtil.Blit(vColor, vw, vh, color, W, H, vx, vy);
            }

            Blit(ctx, color, W, H);
        }

        static void Blit(DrawingContext ctx, uint[] color, int W, int H)
        {
            var wb = new WriteableBitmap(new PixelSize(W, H), new Avalonia.Vector(96, 96),
                                         PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
                unsafe
                {
                    byte* dst = (byte*)fb.Address;
                    int rowB = fb.RowBytes;
                    fixed (uint* src = color)
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * W, dst + y * rowB, rowB, W * 4);
                }
            ctx.DrawImage(wb, new Rect(0, 0, W, H));
        }
    }
}
