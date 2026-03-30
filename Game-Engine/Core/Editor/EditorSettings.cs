#if !PLAYER
using System;
using System.IO;
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
        }
    }

    public static void Load()
    {
        var path = StorePath;
        if (!File.Exists(path))
        {
            ClearConsoleOnPlay = false;
            ScriptEditorShowMinimap = true;
            return;
        }
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<Dto>(json);
        ClearConsoleOnPlay = dto?.ClearConsoleOnPlay ?? false;
        ScriptEditorShowMinimap = dto?.ScriptEditorShowMinimap ?? true;
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
                ScriptEditorShowMinimap = ScriptEditorShowMinimap
            }, JsonOpts));
        }
        catch { /* ignore */ }
    }

    private sealed class Dto
    {
        public bool ClearConsoleOnPlay { get; set; }
        public bool ScriptEditorShowMinimap { get; set; } = true;
    }
}
#endif
