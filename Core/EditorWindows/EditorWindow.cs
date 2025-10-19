using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Game_Engine.Core.EditorWindows
{
    ///  EditorWindow base (singleton per type, remembers bounds).
    public abstract class EditorWindow : Window
    {
        private string _saveKey;

        protected EditorWindow()
        {
            CanResize = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        // -----------style API ----------

        public static T GetWindow<T>(string title = null, bool utility = false) where T : EditorWindow, new()
        {
            var w = EditorWindowManager.GetOrCreate<T>();
            if (!string.IsNullOrWhiteSpace(title)) w.Title = title;
            if (utility) w.MakeUtility();
            return w;
        }

        public static T ShowWindow<T>(string title = null, bool utility = false) where T : EditorWindow, new()
        {
            var w = GetWindow<T>(title, utility);
            if (!w.IsVisible) w.Show();
            else w.Activate();
            return w;
        }

        public static T ShowUtility<T>(string title = null) where T : EditorWindow, new()
            => ShowWindow<T>(title, utility: true);

        // ---------- Lifecycle hooks ----------

        /// Called once right after construction (similar to OnEnable in spirit).
        protected virtual void OnInit() { }
        /// Called before close (similar to OnDisable).
        protected virtual void OnShutdown() { }

        internal void __InitializeFromManager(string saveKey)
        {
            _saveKey = saveKey;
            OnInit();
            LoadBounds();
            Closed += (_, __) => { OnShutdown(); SaveBounds(); EditorWindowManager.OnClosed(this); };
        }

        private void MakeUtility()
        {
            // Utility style: small title bar feeling
            SystemDecorations = SystemDecorations.BorderOnly;

            // Give a *list* of allowed transparency levels (or omit entirely)
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            // e.g., for fancy effects you could allow others:
            // TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };

            ShowInTaskbar = false;
        }


        // ---------- Persist size/pos per project+window type ----------

        private void LoadBounds()
        {
            var p = EditorWindowPrefs.Load(_saveKey);
            if (p == null) return;
            Position = new PixelPoint(p.X, p.Y);
            Width = p.W; Height = p.H;
        }
        private void SaveBounds()
        {
            var p = new EditorWindowPrefs.Bounds { X = Position.X, Y = Position.Y, W = (int)Width, H = (int)Height };
            EditorWindowPrefs.Save(_saveKey, p);
        }
    }

    internal static class EditorWindowManager
    {
        private static readonly Dictionary<Type, EditorWindow> _live = new Dictionary<Type, EditorWindow>();

        public static T GetOrCreate<T>() where T : EditorWindow, new()
        {
            var t = typeof(T);
            EditorWindow w;
            if (_live.TryGetValue(t, out w))
                return (T)w;

            var inst = new T();
            _live[t] = inst;

            var projName = ProjectService.Current != null ? ProjectService.Current.Name ?? "no-project" : "no-project";
            var key = projName + "|" + t.FullName;
            inst.__InitializeFromManager(key);

            return inst;
        }

        public static void OnClosed(EditorWindow w)
        {
            var t = w.GetType();
            if (_live.ContainsKey(t)) _live.Remove(t);
        }
    }

    internal static class EditorWindowPrefs
    {
        internal sealed class Bounds { public int X, Y, W, H; }

        private static string FilePath
        {
            get
            {
                var root = ProjectService.Current != null ? ProjectService.Current.RootPath : Environment.CurrentDirectory;
                var dir = System.IO.Path.Combine(root, ".Editor");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "EditorWindowPrefs.json");
            }
        }

        public static Bounds Load(string key)
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return null;
                var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Bounds>>(System.IO.File.ReadAllText(FilePath));
                Bounds b; return (all != null && all.TryGetValue(key, out b)) ? b : null;
            }
            catch { return null; }
        }

        public static void Save(string key, Bounds b)
        {
            try
            {
                Dictionary<string, Bounds> all = null;
                if (System.IO.File.Exists(FilePath))
                    all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Bounds>>(System.IO.File.ReadAllText(FilePath));
                if (all == null) all = new Dictionary<string, Bounds>(StringComparer.OrdinalIgnoreCase);
                all[key] = b;
                System.IO.File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
