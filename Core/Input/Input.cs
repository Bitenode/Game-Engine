using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Input
{
    //  enums small & focused for now
    public enum KeyCode
    {
        None = 0,
        W, A, S, D,
        UpArrow, DownArrow, LeftArrow, RightArrow,
        Space, LeftShift, Escape,
    }

    public enum MouseButton
    {
        Left = 0,
        Right = 1,
        Middle = 2,
    }

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

        public AxisBinding(string name) { Name = name; }
    }

    sealed class ActionBinding
    {
        public string Name;
        public List<KeyCode> Keys = new List<KeyCode>();
        public List<MouseButton> MouseButtons = new List<MouseButton>();
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

        // ------------ Internal state ------------
        static readonly HashSet<KeyCode> sHeldKeys = new HashSet<KeyCode>();
        static readonly HashSet<KeyCode> sDownKeys = new HashSet<KeyCode>();
        static readonly HashSet<KeyCode> sUpKeys = new HashSet<KeyCode>();

        static readonly HashSet<MouseButton> sHeldMouse = new HashSet<MouseButton>();
        static readonly HashSet<MouseButton> sDownMouse = new HashSet<MouseButton>();
        static readonly HashSet<MouseButton> sUpMouse = new HashSet<MouseButton>();

        static float sMouseDX, sMouseDY;   // accumulated within current frame
        static float sDt;                  // this frame dt (seconds)
        static int sFrameId;             // increments each NewFrame
        static int sAxesUpdatedInFrame;  // to update axes once per frame on demand

        static readonly Dictionary<string, AxisBinding> sAxes = new Dictionary<string, AxisBinding>(StringComparer.Ordinal);
        static readonly Dictionary<string, ActionBinding> sActions = new Dictionary<string, ActionBinding>(StringComparer.Ordinal);

        // ------------ Init ------------
        static Input()
        {
            // Axes
            var horiz = new AxisBinding("Horizontal");
            horiz.Negative.Add(KeyCode.A); horiz.Negative.Add(KeyCode.LeftArrow);
            horiz.Positive.Add(KeyCode.D); horiz.Positive.Add(KeyCode.RightArrow);
            sAxes[horiz.Name] = horiz;

            var vert = new AxisBinding("Vertical");
            vert.Negative.Add(KeyCode.S); vert.Negative.Add(KeyCode.DownArrow);
            vert.Positive.Add(KeyCode.W); vert.Positive.Add(KeyCode.UpArrow);
            sAxes[vert.Name] = vert;

            var mx = new AxisBinding("Mouse X") { IsMouseX = true };
            var my = new AxisBinding("Mouse Y") { IsMouseY = true };
            // No smoothing for mouse deltas; read raw per frame:
            mx.Sensitivity = my.Sensitivity = 1f; mx.Gravity = my.Gravity = 0f; mx.Snap = my.Snap = false;
            sAxes[mx.Name] = mx; sAxes[my.Name] = my;

            // Actions
            var jump = new ActionBinding("Jump"); jump.Keys.Add(KeyCode.Space);
            var sprint = new ActionBinding("Sprint"); sprint.Keys.Add(KeyCode.LeftShift);
            var fire = new ActionBinding("Fire1"); fire.MouseButtons.Add(MouseButton.Left);
            sActions[jump.Name] = jump; sActions[sprint.Name] = sprint; sActions[fire.Name] = fire;
        }

        // ------------ Frame lifecycle ------------
        public static void NewFrame(float deltaTime)
        {
            sFrameId++;
            sDt = deltaTime;

            // clear per-frame edges & mouse deltas
            sDownKeys.Clear(); sUpKeys.Clear();
            sDownMouse.Clear(); sUpMouse.Clear();
            sMouseDX = 0f; sMouseDY = 0f;

            // axes will compute lazily on first GetAxis per frame
            sAxesUpdatedInFrame = -1;
        }

        public static void ClearAll()
        {
            sHeldKeys.Clear(); sDownKeys.Clear(); sUpKeys.Clear();
            sHeldMouse.Clear(); sDownMouse.Clear(); sUpMouse.Clear();
            sMouseDX = sMouseDY = 0f;
            foreach (var kv in sAxes) kv.Value.Value = 0f;
        }

        // ------------ Feed from UI ------------
        public static void FeedKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return;
            if (!sHeldKeys.Contains(key)) sDownKeys.Add(key);
            sHeldKeys.Add(key);
        }

        public static void FeedKeyUp(KeyCode key)
        {
            if (key == KeyCode.None) return;
            if (sHeldKeys.Contains(key)) sUpKeys.Add(key);
            sHeldKeys.Remove(key);
        }

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

        public static SN.Vector2 MouseDelta
        {
            get { return new SN.Vector2(sMouseDX, sMouseDY); }
        }

        // ------------ Queries: actions ------------
        public static bool GetAction(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            for (int i = 0; i < b.Keys.Count; i++) if (sHeldKeys.Contains(b.Keys[i])) return true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sHeldMouse.Contains(b.MouseButtons[i])) return true;
            return false;
        }

        public static bool GetActionDown(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            for (int i = 0; i < b.Keys.Count; i++) if (sDownKeys.Contains(b.Keys[i])) return true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sDownMouse.Contains(b.MouseButtons[i])) return true;
            return false;
        }

        public static bool GetActionUp(string name)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) return false;

            for (int i = 0; i < b.Keys.Count; i++) if (sUpKeys.Contains(b.Keys[i])) return true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sUpMouse.Contains(b.MouseButtons[i])) return true;
            return false;
        }

        // ------------ Queries: axes ------------
        public static float GetAxis(string name)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) return 0f;

            // Mouse axes: read raw delta and scale per-frame
            if (a.IsMouseX) return sMouseDX * MouseSensitivity;
            if (a.IsMouseY) return sMouseDY * MouseSensitivity;

            EnsureAxesUpdatedOncePerFrame();
            return a.Value;
        }

        public static float GetAxisRaw(string name)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) return 0f;

            if (a.IsMouseX) return sMouseDX;
            if (a.IsMouseY) return sMouseDY;

            int pos = 0, neg = 0;
            for (int i = 0; i < a.Positive.Count; i++) if (sHeldKeys.Contains(a.Positive[i])) pos = 1;
            for (int i = 0; i < a.Negative.Count; i++) if (sHeldKeys.Contains(a.Negative[i])) neg = 1;
            return (float)(pos - neg);
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

                int pos = 0, neg = 0;
                for (int i = 0; i < a.Positive.Count; i++) if (sHeldKeys.Contains(a.Positive[i])) pos = 1;
                for (int i = 0; i < a.Negative.Count; i++) if (sHeldKeys.Contains(a.Negative[i])) neg = 1;

                int target = pos - neg; // -1, 0, +1

                if (a.Snap && Math.Sign(a.Value) != Math.Sign(target) && target != 0)
                    a.Value = 0f;

                if (target == 0)
                {
                    // decay toward 0
                    float gstep = a.Gravity * dt;
                    if (a.Value > 0f) a.Value = Math.Max(0f, a.Value - gstep);
                    else if (a.Value < 0f) a.Value = Math.Min(0f, a.Value + gstep);
                }
                else
                {
                    // accelerate toward target
                    float s = a.Sensitivity * dt * target;
                    a.Value += s;
                    if (a.Value > 1f) a.Value = 1f;
                    if (a.Value < -1f) a.Value = -1f;
                }
            }
        }

        //WIP LATER DOWN THE ROADMAP
        // ------------ Remapping API  ------------
        public static void SetAxis(string name, IEnumerable<KeyCode> positive, IEnumerable<KeyCode> negative,
                                   float sensitivity = 6f, float gravity = 12f, bool snap = true)
        {
            AxisBinding a;
            if (!sAxes.TryGetValue(name, out a)) { a = new AxisBinding(name); sAxes[name] = a; }
            a.Positive.Clear(); a.Negative.Clear();
            if (positive != null) a.Positive.AddRange(positive);
            if (negative != null) a.Negative.AddRange(negative);
            a.Sensitivity = sensitivity; a.Gravity = gravity; a.Snap = snap;
        }

        public static void SetAction(string name, IEnumerable<KeyCode> keys, IEnumerable<MouseButton> mouse)
        {
            ActionBinding b;
            if (!sActions.TryGetValue(name, out b)) { b = new ActionBinding(name); sActions[name] = b; }
            b.Keys.Clear(); b.MouseButtons.Clear();
            if (keys != null) b.Keys.AddRange(keys);
            if (mouse != null) b.MouseButtons.AddRange(mouse);
        }
    }
}
