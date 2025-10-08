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

        bool _awakened, _started;

        public GameView()
        {
            ClipToBounds = true;

            _updateTimer.Interval = TimeSpan.FromMilliseconds(16.666); // ≈60Hz
            _updateTimer.Tick += (_, __) => { TickUpdate(); InvalidateVisual(); };

            _fixedTimer.Interval = TimeSpan.FromMilliseconds(20);      // 50Hz
            _fixedTimer.Tick += (_, __) => TickFixedUpdate();

            // Repaint when the scene changes (materials, transforms, etc.)
            SceneService.Changed += () => InvalidateVisual();

            // React to State changes
            StateProperty.Changed.AddClassHandler<GameView>((s, e) => s.OnStateChanged());
        }

        void OnStateChanged()
        {
            switch (State)
            {
                case GamePanel.GameState.Playing:
                    EnsureAwakeStart();
                    _fixedTimer.Start();
                    _updateTimer.Start();
                    break;

                case GamePanel.GameState.Paused:
                case GamePanel.GameState.Stopped:
                    _fixedTimer.Stop();
                    _updateTimer.Stop();
                    break;
            }

            // Force a repaint so the view blanks out when Paused/Stopped
            InvalidateVisual();
        }


        // ----- Lifecycle drivers -----

        void EnsureAwakeStart()
        {
            if (!_awakened) { ForEachBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { ForEachBehavior(b => b.__Start()); _started = true; }
        }

        void TickUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;
            ForEachBehavior(b => b.__Update());
            // late update comes after Update in the same “frame”
            ForEachBehavior(b => b.__LateUpdate());
        }

        void TickFixedUpdate()
        {
            if (State != GamePanel.GameState.Playing) return;
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
