using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SN = System.Numerics;

namespace Game_Engine.Core.Input
{ 

    sealed class AxisBinding
    {
        public string Name;
        public List<KeyCode> Positive = new List<KeyCode>();
        public List<KeyCode> Negative = new List<KeyCode>();
        public float Sensitivity = 6f;  // accel towards target
        public float Gravity = 12f;     // return-to-zero rate
        public bool Snap = true;        // snap to 0 when direction flips
        public float Value;             // runtime
        public bool IsMouseX;           // special: reads from mouse delta X
        public bool IsMouseY;           // special: reads from mouse delta Y
        public GamepadAxis AnalogAxis = GamepadAxis.None;
        public bool InvertAnalog;
        public List<GamepadButton> PositiveButtons = new List<GamepadButton>();
        public List<GamepadButton> NegativeButtons = new List<GamepadButton>();

        public AxisBinding(string name) { Name = name; }
    }

    sealed class ActionBinding
    {
        public string Name;
        public List<KeyCode> Keys = new List<KeyCode>();
        public List<MouseButton> MouseButtons = new List<MouseButton>();
        public List<GamepadButton> GamepadButtons = new List<GamepadButton>();
        public ActionBinding(string name) { Name = name; }
    }

    /// <summary>
    /// Frame-based input manager:
    /// - Call Input.NewFrame(dt) once per Update frame (GameView).
    /// - Feed keys/mouse from UI callbacks (FeedKeyDown/Up, FeedMouse*).
    /// - Query from anywhere: GetAction, GetActionDown, GetAxis, MouseDelta, etc.
    /// </summary>
    public static class Input
    {
        // ------------ Public knobs ------------
        public static float MouseSensitivity = 0.12f;  // scales Mouse X/Y axes
        /// <summary>Stick/trigger ignore magnitude below this (0–1). Triggers use half of this.</summary>
        public static float GamepadDeadzone = 0.20f;
        /// <summary>Added to Mouse X/Y when those axes have a gamepad analog binding.</summary>
        public static float GamepadLookSensitivity = 2.5f;

        // ------------ Internal state ------------
        static readonly HashSet<KeyCode> sHeldKeys = new HashSet<KeyCode>();
        static readonly HashSet<KeyCode> sDownKeys = new HashSet<KeyCode>();
        static readonly HashSet<KeyCode> sUpKeys = new HashSet<KeyCode>();
        static readonly HashSet<KeyCode> sHardwareHeld = new HashSet<KeyCode>();

        static readonly HashSet<GamepadButton> sHeldGamepad = new HashSet<GamepadButton>();
        static readonly HashSet<GamepadButton> sDownGamepad = new HashSet<GamepadButton>();
        static readonly HashSet<GamepadButton> sUpGamepad = new HashSet<GamepadButton>();

        static readonly HashSet<MouseButton> sHeldMouse = new HashSet<MouseButton>();
        static readonly HashSet<MouseButton> sDownMouse = new HashSet<MouseButton>();
        static readonly HashSet<MouseButton> sUpMouse = new HashSet<MouseButton>();

        static float sMouseDX, sMouseDY;   // accumulated within current frame
        static float sMousePosX, sMousePosY; // absolute mouse position in viewport pixels
        static float sViewportW, sViewportH; // viewport size in the same space as mouse position
        static float sDt;                  // this frame dt (seconds)
        static int sFrameId;             // increments each NewFrame
        static int sAxesUpdatedInFrame;  // to update axes once per frame on demand
        /// <summary>True while the Game view holds pointer capture (LMB/RMB sculpting).</summary>
        public static bool PlayViewportCaptureActive;

        static readonly Dictionary<string, AxisBinding> sAxes = new Dictionary<string, AxisBinding>(StringComparer.Ordinal);
        static readonly Dictionary<string, ActionBinding> sActions = new Dictionary<string, ActionBinding>(StringComparer.Ordinal);

        // ------------ Init ------------
        static Input()
        {
            RegisterBuiltInBindings();
            s_json.Converters.Add(new JsonStringEnumConverter());
        }

        static void RegisterBuiltInBindings()
        {
            var horiz = new AxisBinding("Horizontal");
            horiz.Negative.Add(KeyCode.A); horiz.Negative.Add(KeyCode.LeftArrow);
            horiz.Positive.Add(KeyCode.D); horiz.Positive.Add(KeyCode.RightArrow);
            horiz.AnalogAxis = GamepadAxis.LeftStickX;
            horiz.PositiveButtons.Add(GamepadButton.DPadRight);
            horiz.NegativeButtons.Add(GamepadButton.DPadLeft);
            sAxes[horiz.Name] = horiz;

            var vert = new AxisBinding("Vertical");
            vert.Negative.Add(KeyCode.S); vert.Negative.Add(KeyCode.DownArrow);
            vert.Positive.Add(KeyCode.W); vert.Positive.Add(KeyCode.UpArrow);
            vert.AnalogAxis = GamepadAxis.LeftStickY;
            vert.PositiveButtons.Add(GamepadButton.DPadUp);
            vert.NegativeButtons.Add(GamepadButton.DPadDown);
            sAxes[vert.Name] = vert;

            var mx = new AxisBinding("Mouse X")
            {
                IsMouseX = true,
                AnalogAxis = GamepadAxis.RightStickX,
                Sensitivity = 1f, Gravity = 0f, Snap = false
            };
            var my = new AxisBinding("Mouse Y")
            {
                IsMouseY = true,
                AnalogAxis = GamepadAxis.RightStickY,
                InvertAnalog = true,
                Sensitivity = 1f, Gravity = 0f, Snap = false
            };
            sAxes[mx.Name] = mx; sAxes[my.Name] = my;

            var jump = new ActionBinding("Jump");
            jump.Keys.Add(KeyCode.Space);
            jump.GamepadButtons.Add(GamepadButton.A);
            var sprint = new ActionBinding("Sprint");
            sprint.Keys.Add(KeyCode.LeftShift);
            sprint.GamepadButtons.Add(GamepadButton.LeftStick);
            sprint.GamepadButtons.Add(GamepadButton.LeftShoulder);
            var fire = new ActionBinding("Fire1");
            fire.MouseButtons.Add(MouseButton.Left);
            fire.GamepadButtons.Add(GamepadButton.RightShoulder);
            fire.GamepadButtons.Add(GamepadButton.RightTrigger);
            var interact = new ActionBinding("Interact");
            interact.Keys.Add(KeyCode.E);
            interact.GamepadButtons.Add(GamepadButton.X);
            var crouch = new ActionBinding("Crouch");
            crouch.Keys.Add(KeyCode.LeftCtrl);
            crouch.GamepadButtons.Add(GamepadButton.B);
            sActions[jump.Name] = jump;
            sActions[sprint.Name] = sprint;
            sActions[fire.Name] = fire;
            sActions[interact.Name] = interact;
            sActions[crouch.Name] = crouch;
        }

        /// <summary>Restore built-in keyboard + gamepad map (clears custom actions).</summary>
        public static void ResetToEngineDefaults()
        {
            sAxes.Clear();
            sActions.Clear();
            MouseSensitivity = 0.12f;
            GamepadDeadzone = 0.20f;
            GamepadLookSensitivity = 2.5f;
            RegisterBuiltInBindings();
        }

        // ------------ Frame lifecycle ------------
        public static void NewFrame(float deltaTime)
        {
            sFrameId++;
            sDt = deltaTime;

            // Clear per-frame edges.
            sDownKeys.Clear(); sUpKeys.Clear();
            sDownMouse.Clear(); sUpMouse.Clear();
            sDownGamepad.Clear(); sUpGamepad.Clear();

            // IMPORTANT: do NOT clear mouse deltas here.
            // We want PlayerMovement to consume the deltas that were accumulated
            // since the last frame.

            // Axes will compute lazily on first GetAxis per frame.
            sAxesUpdatedInFrame = -1;
        }

        
        public static void EndFrame()
        {
            sMouseDX = 0f;
            sMouseDY = 0f;
        }


        public static void ClearAll()
        {
            sHeldKeys.Clear(); sDownKeys.Clear(); sUpKeys.Clear();
            sHardwareHeld.Clear();
            sHeldGamepad.Clear(); sDownGamepad.Clear(); sUpGamepad.Clear();
            sHeldMouse.Clear(); sDownMouse.Clear(); sUpMouse.Clear();
            sMouseDX = sMouseDY = 0f;
            PlayViewportCaptureActive = false;
            foreach (var kv in sAxes) kv.Value.Value = 0f;
        }

        // ------------ Feed from UI ------------
        public static void FeedKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return;
            if (!sHeldKeys.Contains(key)) sDownKeys.Add(key);
            sHeldKeys.Add(key);
          //  if (key == KeyCode.Space)
           //     Debug.WriteLine($"[Input] FeedKeyDown Space  held={(sHeldKeys.Contains(key))} downEdge={(sDownKeys.Contains(key))}");
        }

        public static void FeedKeyUp(KeyCode key)
        {
            if (key == KeyCode.None) return;
            if (sHeldKeys.Contains(key)) sUpKeys.Add(key);
            sHeldKeys.Remove(key);
          //  if (key == KeyCode.Space)
           //     Debug.WriteLine($"[Input] FeedKeyUp   Space  upEdge={(sUpKeys.Contains(key))}");
        }

        /// <summary>
        /// Read movement / digit keys from the OS even if Hierarchy has keyboard focus.
        /// Call after <see cref="NewFrame"/> so down-edges land in the same Update.
        /// Digit keys (1–9, 0, numpad) are included so HUD hotbars keep working in the editor.
        /// </summary>
        public static void PollHardwareHeldKeys()
        {
            if (!OperatingSystem.IsWindows()) return;
            SyncHardwareKey(0x57, KeyCode.W);
            SyncHardwareKey(0x41, KeyCode.A);
            SyncHardwareKey(0x53, KeyCode.S);
            SyncHardwareKey(0x44, KeyCode.D);
            SyncHardwareKey(0x20, KeyCode.Space);
            SyncHardwareKey(0x10, KeyCode.LeftShift);
            SyncHardwareKey(0x25, KeyCode.LeftArrow);
            SyncHardwareKey(0x26, KeyCode.UpArrow);
            SyncHardwareKey(0x27, KeyCode.RightArrow);
            SyncHardwareKey(0x28, KeyCode.DownArrow);
            SyncHardwareKey(0x46, KeyCode.F);
            SyncHardwareKey(0x47, KeyCode.G);
            // Top-row 0–9 (VK_0..VK_9)
            SyncHardwareKey(0x30, KeyCode.D0);
            SyncHardwareKey(0x31, KeyCode.D1);
            SyncHardwareKey(0x32, KeyCode.D2);
            SyncHardwareKey(0x33, KeyCode.D3);
            SyncHardwareKey(0x34, KeyCode.D4);
            SyncHardwareKey(0x35, KeyCode.D5);
            SyncHardwareKey(0x36, KeyCode.D6);
            SyncHardwareKey(0x37, KeyCode.D7);
            SyncHardwareKey(0x38, KeyCode.D8);
            SyncHardwareKey(0x39, KeyCode.D9);
            // Numpad 0–9 (VK_NUMPAD0..VK_NUMPAD9)
            SyncHardwareKey(0x60, KeyCode.NumPad0);
            SyncHardwareKey(0x61, KeyCode.NumPad1);
            SyncHardwareKey(0x62, KeyCode.NumPad2);
            SyncHardwareKey(0x63, KeyCode.NumPad3);
            SyncHardwareKey(0x64, KeyCode.NumPad4);
            SyncHardwareKey(0x65, KeyCode.NumPad5);
            SyncHardwareKey(0x66, KeyCode.NumPad6);
            SyncHardwareKey(0x67, KeyCode.NumPad7);
            SyncHardwareKey(0x68, KeyCode.NumPad8);
            SyncHardwareKey(0x69, KeyCode.NumPad9);
            PollGamepads();
        }

        /// <summary>Read Xbox-compatible pads (XInput on Windows). Safe to call from the remapper while the editor is idle.</summary>
        public static void PollGamepads()
        {
            Gamepad.PollHardware();
            ushort bits = Gamepad.MergedButtons();
            SyncGamepadButton(GamepadButton.A, Gamepad.ButtonBit(bits, GamepadButton.A));
            SyncGamepadButton(GamepadButton.B, Gamepad.ButtonBit(bits, GamepadButton.B));
            SyncGamepadButton(GamepadButton.X, Gamepad.ButtonBit(bits, GamepadButton.X));
            SyncGamepadButton(GamepadButton.Y, Gamepad.ButtonBit(bits, GamepadButton.Y));
            SyncGamepadButton(GamepadButton.LeftShoulder, Gamepad.ButtonBit(bits, GamepadButton.LeftShoulder));
            SyncGamepadButton(GamepadButton.RightShoulder, Gamepad.ButtonBit(bits, GamepadButton.RightShoulder));
            SyncGamepadButton(GamepadButton.LeftStick, Gamepad.ButtonBit(bits, GamepadButton.LeftStick));
            SyncGamepadButton(GamepadButton.RightStick, Gamepad.ButtonBit(bits, GamepadButton.RightStick));
            SyncGamepadButton(GamepadButton.Start, Gamepad.ButtonBit(bits, GamepadButton.Start));
            SyncGamepadButton(GamepadButton.Back, Gamepad.ButtonBit(bits, GamepadButton.Back));
            SyncGamepadButton(GamepadButton.DPadUp, Gamepad.ButtonBit(bits, GamepadButton.DPadUp));
            SyncGamepadButton(GamepadButton.DPadDown, Gamepad.ButtonBit(bits, GamepadButton.DPadDown));
            SyncGamepadButton(GamepadButton.DPadLeft, Gamepad.ButtonBit(bits, GamepadButton.DPadLeft));
            SyncGamepadButton(GamepadButton.DPadRight, Gamepad.ButtonBit(bits, GamepadButton.DPadRight));
            SyncGamepadButton(GamepadButton.LeftTrigger, Gamepad.DigitalTriggerHeld(left: true));
            SyncGamepadButton(GamepadButton.RightTrigger, Gamepad.DigitalTriggerHeld(left: false));
        }

        static void SyncGamepadButton(GamepadButton button, bool down)
        {
            if (button == GamepadButton.None) return;
            if (down)
            {
                if (!sHeldGamepad.Contains(button)) sDownGamepad.Add(button);
                sHeldGamepad.Add(button);
            }
            else if (sHeldGamepad.Contains(button))
            {
                sUpGamepad.Add(button);
                sHeldGamepad.Remove(button);
            }
        }

        static void SyncHardwareKey(int vk, KeyCode code)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool wasHw = sHardwareHeld.Contains(code);
            if (down)
            {
                // NewFrame clears sDownKeys. Avalonia may already have put the key in
                // sHeldKeys, which would make FeedKeyDown skip the down-edge — restore
                // it from the hardware rising edge so GetKeyDown still works.
                if (!wasHw) sDownKeys.Add(code);
                sHeldKeys.Add(code);
                sHardwareHeld.Add(code);
            }
            else
            {
                if (sHeldKeys.Contains(code))
                {
                    sUpKeys.Add(code);
                    sHeldKeys.Remove(code);
                }
                sHardwareHeld.Remove(code);
            }
        }

        /// <summary>
        /// Detect held mouse buttons from the OS when the cursor is over the Game view.
        /// Never releases buttons — PointerReleased remains authoritative.
        /// </summary>
        public static void PollPlayMouseButtons(bool cursorOverGameView)
        {
            if (!OperatingSystem.IsWindows() || !cursorOverGameView) return;
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0) FeedMouseButtonDown(MouseButton.Left);
            if ((GetAsyncKeyState(0x02) & 0x8000) != 0) FeedMouseButtonDown(MouseButton.Right);
            if ((GetAsyncKeyState(0x04) & 0x8000) != 0) FeedMouseButtonDown(MouseButton.Middle);
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        public static void FeedMouseButtonDown(MouseButton btn)
        {
            if (!sHeldMouse.Contains(btn)) sDownMouse.Add(btn);
            sHeldMouse.Add(btn);
        }

        public static void FeedMouseButtonUp(MouseButton btn)
        {
            if (sHeldMouse.Contains(btn)) sUpMouse.Add(btn);
            sHeldMouse.Remove(btn);
        }

        public static void FeedMousePosition(float x, float y)
        {
            sMousePosX = x;
            sMousePosY = y;
        }

        /// <summary>Set the viewport size (same coordinate space as MousePosition).</summary>
        public static void FeedViewportSize(float w, float h)
        {
            sViewportW = w;
            sViewportH = h;
        }

        public static void FeedMouseDelta(float dx, float dy)
        {
            sMouseDX += dx;
            sMouseDY += dy;
        }

        // ------------ Queries: keys/mouse ------------
        public static bool GetKey(KeyCode key) { return sHeldKeys.Contains(key); }
        public static bool GetKeyDown(KeyCode key) { return sDownKeys.Contains(key); }
        public static bool GetKeyUp(KeyCode key) { return sUpKeys.Contains(key); }

        public static bool GetMouse(MouseButton btn) { return sHeldMouse.Contains(btn); }
        public static bool GetMouseDown(MouseButton btn) { return sDownMouse.Contains(btn); }
        public static bool GetMouseUp(MouseButton btn) { return sUpMouse.Contains(btn); }

        public static bool GetGamepadButton(GamepadButton button) { return sHeldGamepad.Contains(button); }
        public static bool GetGamepadButtonDown(GamepadButton button) { return sDownGamepad.Contains(button); }
        public static bool GetGamepadButtonUp(GamepadButton button) { return sUpGamepad.Contains(button); }
        public static int ConnectedGamepadCount => Gamepad.ConnectedCount;

        /// <summary>Deadzoned analog from the first connected pad.</summary>
        public static float GetGamepadAxis(GamepadAxis axis) => ApplyDeadzone(Gamepad.GetAxisRaw(axis), axis);

        static float ApplyDeadzone(float v, GamepadAxis axis)
        {
            float dz = GamepadDeadzone;
            if (axis == GamepadAxis.LeftTrigger || axis == GamepadAxis.RightTrigger)
                dz = Math.Max(0.02f, dz * 0.5f);
            float a = Math.Abs(v);
            if (a <= dz) return 0f;
            float sign = v < 0f ? -1f : 1f;
            return sign * Math.Clamp((a - dz) / Math.Max(1e-5f, 1f - dz), 0f, 1f);
        }

        public static SN.Vector2 MouseDelta
        {
            get { return new SN.Vector2(sMouseDX, sMouseDY); }
        }

        /// <summary>Current mouse position in viewport/window coordinates.</summary>
        public static SN.Vector2 MousePosition
        {
            get { return new SN.Vector2(sMousePosX, sMousePosY); }
        }

        /// <summary>Current viewport size (same coordinate space as MousePosition).</summary>
        public static SN.Vector2 ViewportSize
        {
            get { return new SN.Vector2(sViewportW, sViewportH); }
        }

        // ------------ Queries: actions ------------
        public static bool GetAction(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            for (int i = 0; i < b.Keys.Count; i++) if (sHeldKeys.Contains(b.Keys[i])) return true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sHeldMouse.Contains(b.MouseButtons[i])) return true;
            for (int i = 0; i < b.GamepadButtons.Count; i++) if (sHeldGamepad.Contains(b.GamepadButtons[i])) return true;
            return false;
        }

        public static bool GetActionDown(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            bool hit = false;
            for (int i = 0; i < b.Keys.Count; i++) if (sDownKeys.Contains(b.Keys[i])) hit = true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sDownMouse.Contains(b.MouseButtons[i])) hit = true;
            for (int i = 0; i < b.GamepadButtons.Count; i++) if (sDownGamepad.Contains(b.GamepadButtons[i])) hit = true;

           // if (hit) Debug.WriteLine($"[Input] GetActionDown \"{name}\" TRUE (frame={sFrameId})");
            return hit;
        }

        public static bool GetActionUp(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            for (int i = 0; i < b.Keys.Count; i++) if (sUpKeys.Contains(b.Keys[i])) return true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sUpMouse.Contains(b.MouseButtons[i])) return true;
            for (int i = 0; i < b.GamepadButtons.Count; i++) if (sUpGamepad.Contains(b.GamepadButtons[i])) return true;
            return false;
        }

        // ------------ Queries: axes ------------
        public static float GetAxis(string name)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) return 0f;

            // Mouse axes: raw delta plus optional right-stick look
            if (a.IsMouseX) return sMouseDX * MouseSensitivity + ReadAnalog(a) * GamepadLookSensitivity;
            if (a.IsMouseY) return sMouseDY * MouseSensitivity + ReadAnalog(a) * GamepadLookSensitivity;

            EnsureAxesUpdatedOncePerFrame();
            return a.Value;
        }

        public static float GetAxisRaw(string name)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) return 0f;

            if (a.IsMouseX) return sMouseDX + ReadAnalog(a);
            if (a.IsMouseY) return sMouseDY + ReadAnalog(a);

            return ComputeAxisTarget(a);
        }

        static void EnsureAxesUpdatedOncePerFrame()
        {
            if (sAxesUpdatedInFrame == sFrameId) return;
            sAxesUpdatedInFrame = sFrameId;

            float dt = sDt <= 0f ? (1f / 60f) : sDt;

            foreach (var kv in sAxes)
            {
                var a = kv.Value;

                if (a.IsMouseX || a.IsMouseY)
                    continue; // mouse axes are raw per-frame

                float analog = ReadAnalog(a);
                float target = ComputeAxisTarget(a); // -1…1

                // Analog already is the target — don't ease it like a key (feels sluggish).
                if (Math.Abs(analog) > 0.01f)
                {
                    a.Value = target;
                    continue;
                }

                if (a.Snap && Math.Sign(a.Value) != Math.Sign(target) && target != 0)
                    a.Value = 0f;

                if (Math.Abs(target) < 0.001f)
                {
                    // decay toward 0
                    float gstep = a.Gravity * dt;
                    if (a.Value > 0f) a.Value = Math.Max(0f, a.Value - gstep);
                    else if (a.Value < 0f) a.Value = Math.Min(0f, a.Value + gstep);
                }
                else
                {
                    // accelerate toward target (analog sticks already are the target)
                    float s = a.Sensitivity * dt;
                    if (a.Value < target) a.Value = Math.Min(target, a.Value + s);
                    else if (a.Value > target) a.Value = Math.Max(target, a.Value - s);
                    if (a.Value > 1f) a.Value = 1f;
                    if (a.Value < -1f) a.Value = -1f;
                }
            }
        }

        static float ComputeAxisTarget(AxisBinding a)
        {
            int pos = 0, neg = 0;
            for (int i = 0; i < a.Positive.Count; i++) if (sHeldKeys.Contains(a.Positive[i])) pos = 1;
            for (int i = 0; i < a.Negative.Count; i++) if (sHeldKeys.Contains(a.Negative[i])) neg = 1;
            for (int i = 0; i < a.PositiveButtons.Count; i++) if (sHeldGamepad.Contains(a.PositiveButtons[i])) pos = 1;
            for (int i = 0; i < a.NegativeButtons.Count; i++) if (sHeldGamepad.Contains(a.NegativeButtons[i])) neg = 1;
            float digital = pos - neg;
            float analog = ReadAnalog(a);
            float v = digital + analog;
            if (v > 1f) return 1f;
            if (v < -1f) return -1f;
            return v;
        }

        static float ReadAnalog(AxisBinding a)
        {
            if (a.AnalogAxis == GamepadAxis.None) return 0f;
            float v = ApplyDeadzone(Gamepad.GetAxisRaw(a.AnalogAxis), a.AnalogAxis);
            return a.InvertAnalog ? -v : v;
        }

       
        // ------------ Remapping API  ------------
        public static void SetAxis(string name, IEnumerable<KeyCode> positive, IEnumerable<KeyCode> negative,
                                   float sensitivity = 6f, float gravity = 12f, bool snap = true,
                                   GamepadAxis? analogAxis = null, bool? invertAnalog = null,
                                   IEnumerable<GamepadButton>? positiveButtons = null,
                                   IEnumerable<GamepadButton>? negativeButtons = null)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) { a = new AxisBinding(name); sAxes[name] = a; }
            a.Positive.Clear(); a.Negative.Clear();
            if (positive != null) a.Positive.AddRange(positive);
            if (negative != null) a.Negative.AddRange(negative);
            a.Sensitivity = sensitivity; a.Gravity = gravity; a.Snap = snap;
            if (analogAxis.HasValue) a.AnalogAxis = analogAxis.Value;
            if (invertAnalog.HasValue) a.InvertAnalog = invertAnalog.Value;
            if (positiveButtons != null)
            {
                a.PositiveButtons.Clear();
                a.PositiveButtons.AddRange(positiveButtons);
            }
            if (negativeButtons != null)
            {
                a.NegativeButtons.Clear();
                a.NegativeButtons.AddRange(negativeButtons);
            }
        }

        public static void SetAction(string name, IEnumerable<KeyCode> keys, IEnumerable<MouseButton> mouse,
                                     IEnumerable<GamepadButton>? gamepad = null)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) { b = new ActionBinding(name); sActions[name] = b; }
            b.Keys.Clear(); b.MouseButtons.Clear();
            if (keys != null) b.Keys.AddRange(keys);
            if (mouse != null) b.MouseButtons.AddRange(mouse);
            if (gamepad != null)
            {
                b.GamepadButtons.Clear();
                b.GamepadButtons.AddRange(gamepad);
            }
        }

        // Public, read-only snapshots for UI
        public sealed class AxisBindingInfo
        {
            public string Name;
            public List<KeyCode> Positive = new List<KeyCode>();
            public List<KeyCode> Negative = new List<KeyCode>();
            public float Sensitivity;
            public float Gravity;
            public bool Snap;
            public bool IsMouseX;
            public bool IsMouseY;
            public GamepadAxis AnalogAxis;
            public bool InvertAnalog;
            public List<GamepadButton> PositiveButtons = new List<GamepadButton>();
            public List<GamepadButton> NegativeButtons = new List<GamepadButton>();
        }

        public sealed class ActionBindingInfo
        {
            public string Name;
            public List<KeyCode> Keys = new List<KeyCode>();
            public List<MouseButton> MouseButtons = new List<MouseButton>();
            public List<GamepadButton> GamepadButtons = new List<GamepadButton>();
        }

        // ----- Read current bindings (snapshots) -----
        public static List<string> GetAxisNames()
        {
            // copy keys to avoid exposing internal dictionary
            var list = new List<string>();
            foreach (var kv in sAxes) list.Add(kv.Key);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public static AxisBindingInfo GetAxisInfo(string name)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) return null;
            var info = new AxisBindingInfo();
            info.Name = a.Name;
            info.Positive.AddRange(a.Positive);
            info.Negative.AddRange(a.Negative);
            info.Sensitivity = a.Sensitivity;
            info.Gravity = a.Gravity;
            info.Snap = a.Snap;
            info.IsMouseX = a.IsMouseX;
            info.IsMouseY = a.IsMouseY;
            info.AnalogAxis = a.AnalogAxis;
            info.InvertAnalog = a.InvertAnalog;
            info.PositiveButtons.AddRange(a.PositiveButtons);
            info.NegativeButtons.AddRange(a.NegativeButtons);
            return info;
        }

        public static List<string> GetActionNames()
        {
            var list = new List<string>();
            foreach (var kv in sActions) list.Add(kv.Key);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public static ActionBindingInfo GetActionInfo(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return null;
            var info = new ActionBindingInfo();
            info.Name = b.Name;
            info.Keys.AddRange(b.Keys);
            info.MouseButtons.AddRange(b.MouseButtons);
            info.GamepadButtons.AddRange(b.GamepadButtons);
            return info;
        }

        public static bool RemoveAction(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return sActions.Remove(name);
        }

        public static void ApplyAxisInfo(AxisBindingInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Name)) return;
            SetAxis(info.Name, info.Positive, info.Negative, info.Sensitivity, info.Gravity, info.Snap,
                    info.AnalogAxis, info.InvertAnalog, info.PositiveButtons, info.NegativeButtons);
        }

        public static void ApplyActionInfo(ActionBindingInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Name)) return;
            SetAction(info.Name, info.Keys, info.MouseButtons, info.GamepadButtons);
        }

        // ---------- Persistence (save/load to project) ----------

        class AxisDTO
        {
            public string Name { get; set; }
            public List<KeyCode> Positive { get; set; }
            public List<KeyCode> Negative { get; set; }
            public float Sensitivity { get; set; }
            public float Gravity { get; set; }
            public bool Snap { get; set; }
            public bool IsMouseX { get; set; }
            public bool IsMouseY { get; set; }
            public GamepadAxis AnalogAxis { get; set; }
            public bool InvertAnalog { get; set; }
            public List<GamepadButton> PositiveButtons { get; set; }
            public List<GamepadButton> NegativeButtons { get; set; }
        }

        class ActionDTO
        {
            public string Name { get; set; }
            public List<KeyCode> Keys { get; set; }
            public List<MouseButton> MouseButtons { get; set; }
            public List<GamepadButton> GamepadButtons { get; set; }
        }

        class BindingsFile
        {
            public float MouseSensitivity { get; set; }
            public float GamepadDeadzone { get; set; } = 0.20f;
            public float GamepadLookSensitivity { get; set; } = 2.5f;
            public List<AxisDTO> Axes { get; set; }
            public List<ActionDTO> Actions { get; set; }
        }

        static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Return the default bindings path for the current project, or null if no project open.</summary>
        public static string GetBindingsPathForCurrentProject()
        {
            var cur = ProjectService.Current;
            if (cur == null) return null;
            var dir = Path.Combine(cur.RootPath, "ProjectSettings");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "input.bindings.json");
        }

        /// <summary>Save current axes/actions to project JSON. Returns the written path.</summary>
        public static string SaveBindingsToProject()
        {
            var path = GetBindingsPathForCurrentProject();
            if (path == null)
                Core.Log.Error("No project is open. Cannot save input bindings.");

            var bf = new BindingsFile
            {
                MouseSensitivity = MouseSensitivity,
                GamepadDeadzone = GamepadDeadzone,
                GamepadLookSensitivity = GamepadLookSensitivity,
                Axes = new List<AxisDTO>(),
                Actions = new List<ActionDTO>()
            };

            foreach (var kv in sAxes)
            {
                var a = kv.Value;
                bf.Axes.Add(new AxisDTO
                {
                    Name = a.Name,
                    Positive = new List<KeyCode>(a.Positive),
                    Negative = new List<KeyCode>(a.Negative),
                    Sensitivity = a.Sensitivity,
                    Gravity = a.Gravity,
                    Snap = a.Snap,
                    IsMouseX = a.IsMouseX,
                    IsMouseY = a.IsMouseY,
                    AnalogAxis = a.AnalogAxis,
                    InvertAnalog = a.InvertAnalog,
                    PositiveButtons = new List<GamepadButton>(a.PositiveButtons),
                    NegativeButtons = new List<GamepadButton>(a.NegativeButtons)
                });
            }

            foreach (var kv in sActions)
            {
                var b = kv.Value;
                bf.Actions.Add(new ActionDTO
                {
                    Name = b.Name,
                    Keys = new List<KeyCode>(b.Keys),
                    MouseButtons = new List<MouseButton>(b.MouseButtons),
                    GamepadButtons = new List<GamepadButton>(b.GamepadButtons)
                });
            }

            var json = JsonSerializer.Serialize(bf, s_json);
            if (path != null)
            {
                File.WriteAllText(path, json);
                Core.Log.Success("Saved input bindings.");
            }
            return path;
        }

        /// <summary>Load bindings from project JSON if it exists. Returns true if loaded.</summary>
        public static bool TryLoadBindingsFromProject()
        {
            var path = GetBindingsPathForCurrentProject();
            if (path == null || !File.Exists(path)) return false;

            var text = File.ReadAllText(path);
            var bf = JsonSerializer.Deserialize<BindingsFile>(text, s_json);
            if (bf == null) return false;

            MouseSensitivity = bf.MouseSensitivity;
            if (bf.GamepadDeadzone > 0f) GamepadDeadzone = bf.GamepadDeadzone;
            if (bf.GamepadLookSensitivity > 0f) GamepadLookSensitivity = bf.GamepadLookSensitivity;

            if (bf.Axes != null)
                for (int i = 0; i < bf.Axes.Count; i++)
                {
                    var a = bf.Axes[i];
                    SetAxis(a.Name, a.Positive ?? new List<KeyCode>(), a.Negative ?? new List<KeyCode>(),
                            a.Sensitivity, a.Gravity, a.Snap,
                            a.AnalogAxis, a.InvertAnalog,
                            a.PositiveButtons ?? new List<GamepadButton>(),
                            a.NegativeButtons ?? new List<GamepadButton>());
                    if (sAxes.TryGetValue(a.Name, out var live))
                    {
                        live.IsMouseX = a.IsMouseX;
                        live.IsMouseY = a.IsMouseY;
                    }
                }

            if (bf.Actions != null)
                for (int j = 0; j < bf.Actions.Count; j++)
                {
                    var b = bf.Actions[j];
                    SetAction(b.Name, b.Keys ?? new List<KeyCode>(), b.MouseButtons ?? new List<MouseButton>(),
                              b.GamepadButtons ?? new List<GamepadButton>());
                }

            if (!BindingsIncludeGamepad(bf))
                EnsureDefaultGamepadBindings();
            return true;
        }

        static bool BindingsIncludeGamepad(BindingsFile bf)
        {
            if (bf.Axes != null)
            {
                for (int i = 0; i < bf.Axes.Count; i++)
                {
                    var a = bf.Axes[i];
                    if (a.AnalogAxis != GamepadAxis.None) return true;
                    if (a.PositiveButtons != null && a.PositiveButtons.Count > 0) return true;
                    if (a.NegativeButtons != null && a.NegativeButtons.Count > 0) return true;
                }
            }
            if (bf.Actions != null)
            {
                for (int j = 0; j < bf.Actions.Count; j++)
                {
                    var g = bf.Actions[j].GamepadButtons;
                    if (g != null && g.Count > 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Fill analog / pad defaults on built-in names when an older JSON file omitted them.
        /// Does not overwrite keys the user already remapped.
        /// </summary>
        public static void EnsureDefaultGamepadBindings()
        {
            FillAxisGamepad("Horizontal", GamepadAxis.LeftStickX, false, GamepadButton.DPadRight, GamepadButton.DPadLeft);
            FillAxisGamepad("Vertical", GamepadAxis.LeftStickY, false, GamepadButton.DPadUp, GamepadButton.DPadDown);
            FillAxisGamepad("Mouse X", GamepadAxis.RightStickX, false, null, null);
            FillAxisGamepad("Mouse Y", GamepadAxis.RightStickY, true, null, null);

            FillActionGamepad("Jump", GamepadButton.A);
            FillActionGamepad("Sprint", GamepadButton.LeftStick, GamepadButton.LeftShoulder);
            FillActionGamepad("Fire1", GamepadButton.RightShoulder, GamepadButton.RightTrigger);
            if (!sActions.ContainsKey("Interact"))
                SetAction("Interact", new[] { KeyCode.E }, Array.Empty<MouseButton>(), new[] { GamepadButton.X });
            else
                FillActionGamepad("Interact", GamepadButton.X);
            if (!sActions.ContainsKey("Crouch"))
                SetAction("Crouch", new[] { KeyCode.LeftCtrl }, Array.Empty<MouseButton>(), new[] { GamepadButton.B });
            else
                FillActionGamepad("Crouch", GamepadButton.B);
        }

        static void FillAxisGamepad(string name, GamepadAxis analog, bool invert, GamepadButton? pos, GamepadButton? neg)
        {
            if (!sAxes.TryGetValue(name, out var a)) return;
            if (a.AnalogAxis == GamepadAxis.None)
            {
                a.AnalogAxis = analog;
                a.InvertAnalog = invert;
            }
            if (pos.HasValue && a.PositiveButtons.Count == 0) a.PositiveButtons.Add(pos.Value);
            if (neg.HasValue && a.NegativeButtons.Count == 0) a.NegativeButtons.Add(neg.Value);
        }

        static void FillActionGamepad(string name, params GamepadButton[] buttons)
        {
            if (!sActions.TryGetValue(name, out var b)) return;
            if (b.GamepadButtons.Count > 0) return;
            b.GamepadButtons.AddRange(buttons);
        }


    }
}
