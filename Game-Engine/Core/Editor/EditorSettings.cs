#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Game_Engine.Core.Editor;

/// <summary>Persisted editor preferences (AppData).</summary>
public static class EditorSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameEngine", "editor_settings.json");

    public static bool ClearConsoleOnPlay { get; set; }
    public static bool ScriptEditorShowMinimap { get; set; } = true;
    public static bool ScriptEditorShowLineNumbers { get; set; } = true;
    public static bool ScriptEditorWordWrap { get; set; }

    /// <summary>When true, show the project hub modal when the main window opens.</summary>
    public static bool ShowWelcomeDialogOnStartup { get; set; } = true;

    /// <summary>Default for “include standard assets” when creating a new project.</summary>
    public static bool IncludeStandardAssetsWhenCreatingProject { get; set; } = true;

    public const double ScriptEditorDefaultWidth = 1280;
    public const double ScriptEditorDefaultHeight = 800;
    public const double ScriptEditorDefaultTreeWidth = 280;

    public static double? ScriptEditorWindowWidth { get; set; }
    public static double? ScriptEditorWindowHeight { get; set; }
    public static double? ScriptEditorWindowX { get; set; }
    public static double? ScriptEditorWindowY { get; set; }
    public static bool ScriptEditorWindowMaximized { get; set; }
    public static double ScriptEditorTreeColumnWidth { get; set; } = ScriptEditorDefaultTreeWidth;

    private static readonly List<string> s_scriptEditorRecents = new();
    private const int ScriptEditorRecentCap = 12;

    public static IReadOnlyList<string> GetScriptEditorRecents() =>
        s_scriptEditorRecents.Where(File.Exists).ToList();

    public static void AddScriptEditorRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
            s_scriptEditorRecents.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            s_scriptEditorRecents.Insert(0, full);
            while (s_scriptEditorRecents.Count > ScriptEditorRecentCap)
                s_scriptEditorRecents.RemoveAt(s_scriptEditorRecents.Count - 1);
        }
        catch { /* ignore */ }
    }

    static EditorSettings()
    {
        try
        {
            Load();
        }
        catch
        {
            ClearConsoleOnPlay = false;
            ScriptEditorShowMinimap = true;
            ScriptEditorShowLineNumbers = true;
            ScriptEditorWordWrap = false;
            ShowWelcomeDialogOnStartup = true;
            IncludeStandardAssetsWhenCreatingProject = true;
        }
    }

    public static void Load()
    {
        var path = StorePath;
        if (!File.Exists(path))
        {
            ClearConsoleOnPlay = false;
            ScriptEditorShowMinimap = true;
            ScriptEditorShowLineNumbers = true;
            ScriptEditorWordWrap = false;
            ShowWelcomeDialogOnStartup = true;
            IncludeStandardAssetsWhenCreatingProject = true;
            ScriptEditorTreeColumnWidth = ScriptEditorDefaultTreeWidth;
            s_scriptEditorRecents.Clear();
            return;
        }
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<Dto>(json);
        ClearConsoleOnPlay = dto?.ClearConsoleOnPlay ?? false;
        ScriptEditorShowMinimap = dto?.ScriptEditorShowMinimap ?? true;
        ScriptEditorShowLineNumbers = dto?.ScriptEditorShowLineNumbers ?? true;
        ScriptEditorWordWrap = dto?.ScriptEditorWordWrap ?? false;
        ShowWelcomeDialogOnStartup = dto?.ShowWelcomeDialogOnStartup ?? true;
        IncludeStandardAssetsWhenCreatingProject = dto?.IncludeStandardAssetsWhenCreatingProject ?? true;
        ScriptEditorWindowWidth = dto?.ScriptEditorWindowWidth;
        ScriptEditorWindowHeight = dto?.ScriptEditorWindowHeight;
        ScriptEditorWindowX = dto?.ScriptEditorWindowX;
        ScriptEditorWindowY = dto?.ScriptEditorWindowY;
        ScriptEditorWindowMaximized = dto?.ScriptEditorWindowMaximized ?? false;
        ScriptEditorTreeColumnWidth = dto?.ScriptEditorTreeColumnWidth is > 120 and < 2000
            ? dto.ScriptEditorTreeColumnWidth!.Value
            : ScriptEditorDefaultTreeWidth;
        s_scriptEditorRecents.Clear();
        if (dto?.ScriptEditorRecents != null)
        {
            foreach (var r in dto.ScriptEditorRecents)
            {
                if (!string.IsNullOrWhiteSpace(r) && s_scriptEditorRecents.Count < ScriptEditorRecentCap)
                    s_scriptEditorRecents.Add(r);
            }
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new Dto
            {
                ClearConsoleOnPlay = ClearConsoleOnPlay,
                ScriptEditorShowMinimap = ScriptEditorShowMinimap,
                ScriptEditorShowLineNumbers = ScriptEditorShowLineNumbers,
                ScriptEditorWordWrap = ScriptEditorWordWrap,
                ShowWelcomeDialogOnStartup = ShowWelcomeDialogOnStartup,
                IncludeStandardAssetsWhenCreatingProject = IncludeStandardAssetsWhenCreatingProject,
                ScriptEditorWindowWidth = ScriptEditorWindowWidth,
                ScriptEditorWindowHeight = ScriptEditorWindowHeight,
                ScriptEditorWindowX = ScriptEditorWindowX,
                ScriptEditorWindowY = ScriptEditorWindowY,
                ScriptEditorWindowMaximized = ScriptEditorWindowMaximized,
                ScriptEditorTreeColumnWidth = ScriptEditorTreeColumnWidth,
                ScriptEditorRecents = s_scriptEditorRecents.ToList()
            }, JsonOpts));
        }
        catch { /* ignore */ }
    }

    private sealed class Dto
    {
        public bool ClearConsoleOnPlay { get; set; }
        public bool ScriptEditorShowMinimap { get; set; } = true;
        public bool ScriptEditorShowLineNumbers { get; set; } = true;
        public bool ScriptEditorWordWrap { get; set; }
        public bool ShowWelcomeDialogOnStartup { get; set; } = true;
        public bool IncludeStandardAssetsWhenCreatingProject { get; set; } = true;
        public double? ScriptEditorWindowWidth { get; set; }
        public double? ScriptEditorWindowHeight { get; set; }
        public double? ScriptEditorWindowX { get; set; }
        public double? ScriptEditorWindowY { get; set; }
        public bool ScriptEditorWindowMaximized { get; set; }
        public double? ScriptEditorTreeColumnWidth { get; set; }
        public List<string>? ScriptEditorRecents { get; set; }
    }
}
#endif
