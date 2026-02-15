using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            s_json.Converters.Add(new JsonStringEnumConverter());
        }

        // ------------ Frame lifecycle ------------
        public static void NewFrame(float deltaTime)
        {
            sFrameId++;
            sDt = deltaTime;

            // Clear per-frame edges.
            sDownKeys.Clear(); sUpKeys.Clear();
            sDownMouse.Clear(); sUpMouse.Clear();

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

            bool hit = false;
            for (int i = 0; i < b.Keys.Count; i++) if (sDownKeys.Contains(b.Keys[i])) hit = true;
            for (int i = 0; i < b.MouseButtons.Count; i++) if (sDownMouse.Contains(b.MouseButtons[i])) hit = true;

           // if (hit) Debug.WriteLine($"[Input] GetActionDown \"{name}\" TRUE (frame={sFrameId})");
            return hit;
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
        }

        public sealed class ActionBindingInfo
        {
            public string Name;
            public List<KeyCode> Keys = new List<KeyCode>();
            public List<MouseButton> MouseButtons = new List<MouseButton>();
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
            return info;
        }

        public static bool RemoveAction(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return sActions.Remove(name);
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
        }

        class ActionDTO
        {
            public string Name { get; set; }
            public List<KeyCode> Keys { get; set; }
            public List<MouseButton> MouseButtons { get; set; }
        }

        class BindingsFile
        {
            public float MouseSensitivity { get; set; }
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
                    IsMouseY = a.IsMouseY
                });
            }

            foreach (var kv in sActions)
            {
                var b = kv.Value;
                bf.Actions.Add(new ActionDTO
                {
                    Name = b.Name,
                    Keys = new List<KeyCode>(b.Keys),
                    MouseButtons = new List<MouseButton>(b.MouseButtons)
                });
            }

            var json = JsonSerializer.Serialize(bf, s_json);
            if (path != null)
                File.WriteAllText(path, json);
                Core.Log.Success("Saved input bindings.");
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

            if (bf.Axes != null)
                for (int i = 0; i < bf.Axes.Count; i++)
                {
                    var a = bf.Axes[i];
                    SetAxis(a.Name, a.Positive ?? new List<KeyCode>(), a.Negative ?? new List<KeyCode>(),
                            a.Sensitivity, a.Gravity, a.Snap);
                }

            if (bf.Actions != null)
                for (int j = 0; j < bf.Actions.Count; j++)
                {
                    var b = bf.Actions[j];
                    SetAction(b.Name, b.Keys ?? new List<KeyCode>(), b.MouseButtons ?? new List<MouseButton>());
                }

            return true;
        }


    }
}
