#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Game_Engine.Docking;

public sealed class DockLayoutTabDto
{
    public string Region { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Header { get; set; } = "";
    public bool IsActive { get; set; }
}

public static class DockLayoutPresetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string PathForSlot(int slot) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameEngine", $"dock_layout_{slot}.json");

    private static string PathForProject(string projectRoot)
    {
        var key = projectRoot ?? "";
        key = key.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameEngine", $"dock_layout_project_{key}.json");
    }

    public static void Save(int slot, IReadOnlyList<DockLayoutTabDto> tabs)
    {
        try
        {
            var path = PathForSlot(slot);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(tabs, JsonOpts));
        }
        catch { /* ignore */ }
    }

    public static List<DockLayoutTabDto>? Load(int slot)
    {
        try
        {
            var path = PathForSlot(slot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<DockLayoutTabDto>>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void SaveForProject(string projectRoot, IReadOnlyList<DockLayoutTabDto> tabs)
    {
        try
        {
            var path = PathForProject(projectRoot);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(tabs, JsonOpts));
        }
        catch { }
    }

    public static List<DockLayoutTabDto>? LoadForProject(string projectRoot)
    {
        try
        {
            var path = PathForProject(projectRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<DockLayoutTabDto>>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
#endif
