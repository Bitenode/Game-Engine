using System;
using System.IO;
using System.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Input;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace Engine.Player.XboxUwp
{
    /// <summary>
    /// Owns the ANGLE context. Probes ES 3, then runs the shared player frame if the context is ES 3.
    /// </summary>
    public static class XboxGameLoop
    {
        static volatile bool _stop;
        static int _ownerThreadId;
        static int _width = 1280, _height = 720;
        static int _dipWidth = 1280;
        static int _dipHeight = 720;
        static float _compositionScale = 1f;
        static Gl.AngleEgl _egl;

        /// <summary>Panel size in DIPs, from the UI thread. The pixel size is read from the EGL surface each frame.</summary>
        public static void SetSize(int w, int h)
        {
            if (w > 0) { _width = w; _dipWidth = w; }
            if (h > 0) _height = h;
        }

        /// <summary>
        /// UI thread: push SwapChainPanel DIP size + composition scale into ANGLE so the
        /// backbuffer grows with the window (fixes menu letterboxed into the top-left).
        /// </summary>
        public static void SetPanelLayout(double dipWidth, double dipHeight, float compositionScale)
        {
            float scale = compositionScale > 0.01f ? compositionScale : 1f;
            if (dipWidth > 1) _dipWidth = Math.Max(1, (int)Math.Round(dipWidth));
            if (dipHeight > 1) _dipHeight = Math.Max(1, (int)Math.Round(dipHeight));
            _compositionScale = scale;
            int pw = Math.Max(1, (int)Math.Round(_dipWidth * scale));
            int ph = Math.Max(1, (int)Math.Round(_dipHeight * scale));
            SetSize(pw, ph);
            // Do not touch the ANGLE PropertySet here. It was created on the GL thread;
            // writing it from the UI thread is a cross-apartment COM fault and UWP closes the app.
        }

        public static void Stop() => _stop = true;

        public static void Run(SwapChainPanel panel, Action<string> report, Action onPlaying)
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            XboxPlayerLog.Start();
            UwpXboxLiveService.Install();
            EngineDispatcher.UseQueue(() => Thread.CurrentThread.ManagedThreadId == _ownerThreadId);
            GameRenderPipeline.PreferForward = true;
            XboxImageDecoder.Register();

            AngleSession? angle = null;
            try
            {
                // Panel size/scale must come from the UI thread (SetPanelLayout) — never read XAML here.
                var egl = Gl.AngleEgl.Create(panel, _dipWidth, _dipHeight, _compositionScale);
                _egl = egl;
                var glCtx = new GLContext(Gl.AngleEgl.GetProc);
                angle = new AngleSession(egl, glCtx);
                string version = glCtx.VersionString ?? "";
                string renderer = glCtx.GL.GetStringS(StringName.Renderer) ?? "";
                bool es3 = version.IndexOf("OpenGL ES 3", StringComparison.OrdinalIgnoreCase) >= 0
                           && renderer.IndexOf("Software", StringComparison.OrdinalIgnoreCase) < 0
                           && renderer.IndexOf("WARP", StringComparison.OrdinalIgnoreCase) < 0;

                report("GL " + version + "\n" + renderer
                    + (es3 ? "\nES 3 hardware context OK." : "\nNeed OpenGL ES 3.0 on a hardware Direct3D11 renderer. 3D play stopped."));

                if (!es3)
                    return;

                // Drop the XAML boot strip as soon as GL owns the panel — don't leave the
                // "Scenes in build" host UI covering the game for the whole shader compile.
                onPlaying?.Invoke();

                var dataRoot = Path.Combine(Package.Current.InstalledLocation.Path, "Data");
                var buildJson = Path.Combine(dataRoot, "build.json");
                string sceneNote = "no build.json";
                if (File.Exists(buildJson))
                {
                    try
                    {
                        var cfg = PlayerBootstrap.Load(buildJson);
                        EngineDispatcher.Drain();
                        sceneNote = (string.IsNullOrEmpty(cfg.StartupScene) ? "scene" : cfg.StartupScene)
                            + " (" + SceneService.Root.Count + " objects)";
                        if (!string.IsNullOrEmpty(PlayerBootstrap.LastScriptError))
                            sceneNote += "\nScripts: " + PlayerBootstrap.LastScriptError;
                    }
                    catch (Exception ex) { report("Scene load failed: " + ex.Message); }
                }

                var phase = System.Diagnostics.Stopwatch.StartNew();
                XboxPlayerLog.Write("Info", "[Host] Scene ready: " + sceneNote.Replace("\n", " | "));
                SceneService.PlayMode = true;
                // ANGLE recompiles HLSL on every link. Keep the linked binaries so only the first launch pays for it.
                try
                {
                    ShaderProgram.BinaryCacheDirectory = Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Shaders");
                }
                catch { }
                XboxPlayerLog.Write("Info", "[Host] GL " + version + " detectedES=" + glCtx.IsES);
                report("Preparing shaders…");
                var res = new ViewRenderResources();
                // This host is an ES 3 context. A desktop #version 330 shader makes ANGLE's
                // HLSL compile sit inside glLinkProgram and the player never presents a frame.
                res.Initialize(glCtx.GL, true, shadowResolution: 256);
                XboxPlayerLog.Write("Info", "[Host] Render resources (shaders) ready in " + phase.ElapsedMilliseconds + " ms");
                var frame = new PlayerFrameState { Res = res, Playing = true };
                frame.FpsTick.Restart();
                frame.FpsWindow.Restart();
                PlayerFrameRenderer.RebuildSceneCaches(frame);
                // Same reaction as the desktop player: a changed scene needs fresh camera/sky/light lookups.
                bool sceneDirty = false;
                SceneService.Changed += () => sceneDirty = true;
                var sim = new PlayerSimulation { PumpInput = PumpInput };
                sim.Start();
                report("Playing " + sceneNote);
                onPlaying?.Invoke(); // idempotent — chrome already hidden after first swap

                string lastScene = SceneService.CurrentScenePath;
                long frameIndex = 0;
                var perf = System.Diagnostics.Stopwatch.StartNew();
                var part = new System.Diagnostics.Stopwatch();
                long perfFrames = 0, updateTicks = 0, fixedTicks = 0, renderTicks = 0, swapTicks = 0;
                int perfReports = 0;
                while (!_stop)
                {
                    // The first frames after a scene load do the Awake/Start, collider and GPU upload work.
                    bool timed = frameIndex < 3 || SceneManager.HasPendingLoad;
                    if (timed) phase.Restart();
                    EngineDispatcher.Drain();
                    try { egl.UpdatePanelSize(_dipWidth, _dipHeight, _compositionScale); }
                    catch (Exception ex) { XboxPlayerLog.Write("Error", "[Host] Panel size: " + ex.GetType().Name); }
                    egl.QuerySize(out int sw, out int sh);
                    if (sw > 0) _width = sw;
                    if (sh > 0) _height = sh;
                    if (SceneManager.HasPendingLoad)
                        report("Loading scene…");
                    part.Restart();
                    sim.TickUpdate(_width, _height, false);
                    updateTicks += part.ElapsedTicks;
                    part.Restart();
                    sim.TickFixed();
                    fixedTicks += part.ElapsedTicks;
                    long simMs = timed ? phase.ElapsedMilliseconds : 0;
                    var sceneNow = SceneService.CurrentScenePath;
                    if (!string.Equals(sceneNow, lastScene, StringComparison.OrdinalIgnoreCase))
                    {
                        PlayerFrameRenderer.RebuildSceneCaches(frame);
                        lastScene = sceneNow;
                        frameIndex = 0;
                        perfReports = 0;
                        perfFrames = 0;
                        perf.Restart();
                        sceneDirty = false;
                        report("Playing " + System.IO.Path.GetFileName(sceneNow ?? ""));
                        XboxPlayerLog.Write("Info", "[Host] Scene changed to " + System.IO.Path.GetFileName(sceneNow ?? ""));
                    }
                    else if (sceneDirty)
                    {
                        // Play-mode NotifyChanged fires constantly (transforms, fauna, grass).
                        // Only refresh camera/sky/light lookups — never InvalidateAll here.
                        sceneDirty = false;
                        PlayerFrameRenderer.RebuildSceneCaches(frame);
                    }
                    // Textures that were not readable when the scene was deserialized are retried here.
                    MaterialRebind.RepairScene();
                    frame.Playing = true;
                    // Must be 1: RenderScale only shrinks the GL viewport on the default
                    // framebuffer (no stretch), which letterboxed the main menu on the TV.
                    frame.RenderScale = 1;
                    frame.WidthDip = _width;
                    frame.HeightDip = _height;
                    part.Restart();
                    PlayerFrameRenderer.Render(glCtx.GL, frame, 0);
                    renderTicks += part.ElapsedTicks;
                    part.Restart();
                    egl.Swap();
                    swapTicks += part.ElapsedTicks;
                    if (timed)
                        XboxPlayerLog.Write("Info", "[Host] Frame " + frameIndex + ": update " + simMs
                            + " ms, render " + (phase.ElapsedMilliseconds - simMs) + " ms");
                    frameIndex++;
                    perfFrames++;
                    // A few frame-rate samples after each scene load, enough to tell a hitch from a slow scene.
                    if (perfReports < 6 && perf.ElapsedMilliseconds >= 5000)
                    {
                        double fps = perfFrames * 1000.0 / perf.ElapsedMilliseconds;
                        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, perfFrames);
                        XboxPlayerLog.Write("Info", "[Host] " + fps.ToString("F1") + " fps at " + _width + "x" + _height
                            + " — per frame: update " + (updateTicks * toMs).ToString("F1")
                            + " ms, physics " + (fixedTicks * toMs).ToString("F1")
                            + " ms, render " + (renderTicks * toMs).ToString("F1")
                            + " ms, present " + (swapTicks * toMs).ToString("F1") + " ms");
                        perfReports++;
                        perfFrames = 0;
                        updateTicks = fixedTicks = renderTicks = swapTicks = 0;
                        perf.Restart();
                    }
                }
            }
            catch (Exception ex)
            {
                // Message is often empty for UWP cross-thread COM failures — log the full type + stack.
                string detail = ex.GetType().Name;
                if (!string.IsNullOrWhiteSpace(ex.Message)) detail += ": " + ex.Message;
                if (ex.InnerException != null)
                    detail += " | inner " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
                XboxPlayerLog.Write("Error", "[Host] " + detail + "\n" + ex);
                report("ANGLE / game loop failed: " + detail);
            }
            finally
            {
                _egl = null;
                angle?.Dispose();
            }
        }

        // Window events arrive on the UI thread. They wait here until the game thread has started its
        // input frame; fed any earlier, Input.NewFrame wipes the pressed/released edges before a script runs.
        static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_input = new();

        /// <summary>Applies the queued window input. Runs on the game thread, after Input.NewFrame.</summary>
        static void PumpInput()
        {
            while (s_input.TryDequeue(out var feed))
                feed();
        }

        /// <summary>Window coordinates are in DIPs. The engine's UI works in the swap chain's pixels.</summary>
        static float PointerScale => _dipWidth > 0 ? (float)_width / _dipWidth : 1f;

        static void FeedWindowPointer(CoreWindow window, PointerEventArgs e, ref bool left, ref bool right, ref bool middle)
        {
            var point = e.CurrentPoint;
            double x = point.Position.X;
            double y = point.Position.Y;
            var bounds = window.Bounds;
            double dipW = bounds.Width;
            double dipH = bounds.Height;
            var props = point.Properties;
            bool leftNow = props.IsLeftButtonPressed;
            bool rightNow = props.IsRightButtonPressed;
            bool middleNow = props.IsMiddleButtonPressed;
            s_input.Enqueue(() =>
            {
                float bw = Math.Max(1f, _width);
                float bh = Math.Max(1f, _height);
                float px = dipW > 1.0 ? (float)(x / dipW * bw) : (float)x;
                float py = dipH > 1.0 ? (float)(y / dipH * bh) : (float)y;
                Input.FeedMousePosition(px, py);
            });
            void Sync(ref bool was, bool now, MouseButton button)
            {
                if (was == now) return;
                was = now;
                if (now) s_input.Enqueue(() => Input.FeedMouseButtonDown(button));
                else s_input.Enqueue(() => Input.FeedMouseButtonUp(button));
            }
            Sync(ref left, leftNow, MouseButton.Left);
            Sync(ref right, rightNow, MouseButton.Right);
            Sync(ref middle, middleNow, MouseButton.Middle);
        }

        public static void HookCoreWindow(CoreWindow window)
        {
            window.KeyDown += (_, e) =>
            {
                var code = MapKey(e.VirtualKey);
                if (code == KeyCode.None) return;
                // Auto-repeat is harmless: FeedKeyDown only raises the pressed edge for a key that was up.
                s_input.Enqueue(() => Input.FeedKeyDown(code));
            };
            window.KeyUp += (_, e) =>
            {
                var code = MapKey(e.VirtualKey);
                if (code == KeyCode.None) return;
                s_input.Enqueue(() => Input.FeedKeyUp(code));
            };

            // The Xbox pointer and the PC mouse are delivered here even when the swap chain
            // itself is not the hit-test target. Menu buttons read this position.
            bool left = false, right = false, middle = false;
            window.PointerPressed += (_, e) => FeedWindowPointer(window, e, ref left, ref right, ref middle);
            window.PointerMoved += (_, e) => FeedWindowPointer(window, e, ref left, ref right, ref middle);
            window.PointerReleased += (_, e) => FeedWindowPointer(window, e, ref left, ref right, ref middle);

            // Key-up events are not delivered to an inactive window. Drop held keys so nothing sticks.
            window.Activated += (_, e) =>
            {
                if (e.WindowActivationState != CoreWindowActivationState.Deactivated) return;
                s_input.Enqueue(() =>
                {
                    bool worldInput = Input.WorldInputEnabled;
                    Input.ClearAll();
                    Input.WorldInputEnabled = worldInput;
                });
            };
        }

        /// <summary>
        /// Pointer relative to the swap chain. CoreWindow DIPs do not line up with the
        /// backbuffer, so menu clicks missed every button.
        /// </summary>
        public static void HookPanel(SwapChainPanel panel)
        {
            bool left = false, right = false, middle = false;
            bool hasLast = false;
            double lastX = 0, lastY = 0;

            void Handle(Windows.UI.Xaml.Input.PointerRoutedEventArgs e, bool withDelta)
            {
                var point = e.GetCurrentPoint(panel);
                double x = point.Position.X;
                double y = point.Position.Y;
                double dipW = panel.ActualWidth;
                double dipH = panel.ActualHeight;
                float dx = withDelta && hasLast ? (float)(x - lastX) : 0f;
                float dy = withDelta && hasLast ? (float)(y - lastY) : 0f;
                lastX = x; lastY = y; hasLast = true;
                var props = point.Properties;
                bool turn = withDelta && !IsXboxConsole();
                s_input.Enqueue(() =>
                {
                    float bw = Math.Max(1f, _width);
                    float bh = Math.Max(1f, _height);
                    float px = dipW > 1.0 ? (float)(x / dipW * bw) : (float)x;
                    float py = dipH > 1.0 ? (float)(y / dipH * bh) : (float)y;
                    Input.FeedMousePosition(px, py);
                    // Look uses the raw DIP delta, same as the desktop player.
                    if (turn && (dx != 0f || dy != 0f))
                        Input.FeedMouseDelta(dx, dy);
                });
                void Sync(ref bool was, bool now, MouseButton button)
                {
                    if (was == now) return;
                    was = now;
                    if (now) s_input.Enqueue(() => Input.FeedMouseButtonDown(button));
                    else s_input.Enqueue(() => Input.FeedMouseButtonUp(button));
                }
                Sync(ref left, props.IsLeftButtonPressed, MouseButton.Left);
                Sync(ref right, props.IsRightButtonPressed, MouseButton.Right);
                Sync(ref middle, props.IsMiddleButtonPressed, MouseButton.Middle);
                if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
                    panel.CapturePointer(e.Pointer);
                else
                    panel.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }

            panel.PointerPressed += (_, e) => Handle(e, false);
            panel.PointerReleased += (_, e) => { Handle(e, false); hasLast = false; };
            panel.PointerMoved += (_, e) => Handle(e, true);
            panel.PointerCaptureLost += (_, __) => hasLast = false;
            panel.PointerWheelChanged += (_, e) =>
            {
                float notches = e.GetCurrentPoint(panel).Properties.MouseWheelDelta / 120f;
                s_input.Enqueue(() => Input.FeedMouseScroll(notches));
                e.Handled = true;
            };
        }

        static bool IsXboxConsole()
        {
            try
            {
                return string.Equals(
                    Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Xbox", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static KeyCode MapKey(VirtualKey key)
        {
            if (key >= VirtualKey.A && key <= VirtualKey.Z)
                return (KeyCode)((int)KeyCode.A + (key - VirtualKey.A));
            if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
                return (KeyCode)((int)KeyCode.D0 + (key - VirtualKey.Number0));
            if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
                return (KeyCode)((int)KeyCode.NumPad0 + (key - VirtualKey.NumberPad0));
            if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
                return (KeyCode)((int)KeyCode.F1 + (key - VirtualKey.F1));
            return key switch
            {
                VirtualKey.Space => KeyCode.Space,
                VirtualKey.Escape => KeyCode.Escape,
                VirtualKey.Enter => KeyCode.Enter,
                VirtualKey.Tab => KeyCode.Tab,
                VirtualKey.Back => KeyCode.Backspace,
                VirtualKey.Delete => KeyCode.Delete,
                VirtualKey.Insert => KeyCode.Insert,
                VirtualKey.Home => KeyCode.Home,
                VirtualKey.End => KeyCode.End,
                VirtualKey.PageUp => KeyCode.PageUp,
                VirtualKey.PageDown => KeyCode.PageDown,
                VirtualKey.Left => KeyCode.LeftArrow,
                VirtualKey.Right => KeyCode.RightArrow,
                VirtualKey.Up => KeyCode.UpArrow,
                VirtualKey.Down => KeyCode.DownArrow,
                // CoreWindow reports the generic modifier. The engine's default bindings use the left one.
                VirtualKey.Shift or VirtualKey.LeftShift => KeyCode.LeftShift,
                VirtualKey.RightShift => KeyCode.RightShift,
                VirtualKey.Control or VirtualKey.LeftControl => KeyCode.LeftCtrl,
                VirtualKey.RightControl => KeyCode.RightCtrl,
                VirtualKey.Menu or VirtualKey.LeftMenu => KeyCode.LeftAlt,
                VirtualKey.RightMenu => KeyCode.RightAlt,
                VirtualKey.CapitalLock => KeyCode.CapsLock,
                VirtualKey.Multiply => KeyCode.Multiply,
                VirtualKey.Add => KeyCode.Add,
                VirtualKey.Subtract => KeyCode.Subtract,
                VirtualKey.Decimal => KeyCode.Decimal,
                VirtualKey.Divide => KeyCode.Divide,
                (VirtualKey)0xBA => KeyCode.OemSemicolon,
                (VirtualKey)0xBB => KeyCode.OemPlus,
                (VirtualKey)0xBC => KeyCode.OemComma,
                (VirtualKey)0xBD => KeyCode.OemMinus,
                (VirtualKey)0xBE => KeyCode.OemPeriod,
                (VirtualKey)0xBF => KeyCode.OemQuestion,
                (VirtualKey)0xC0 => KeyCode.OemTilde,
                (VirtualKey)0xDB => KeyCode.OemOpenBrackets,
                (VirtualKey)0xDC => KeyCode.OemPipe,
                (VirtualKey)0xDD => KeyCode.OemCloseBrackets,
                (VirtualKey)0xDE => KeyCode.OemQuotes,
                _ => KeyCode.None
            };
        }

        sealed class AngleSession : IDisposable
        {
            public Gl.AngleEgl Egl;
            public GLContext Gl;
            public AngleSession(Gl.AngleEgl egl, GLContext gl) { Egl = egl; Gl = gl; }
            public void Dispose() { Gl.Dispose(); Egl.Dispose(); }
        }
    }
}
