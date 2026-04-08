#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Game_Engine.Core;

namespace Game_Engine.Core.Rendering;

/// <summary>Persisted per-project rendering options (separate from build.json).</summary>
public static class ProjectRenderingSettings
{
    /// <summary>When true, <see cref="Game_Engine.Views.GameView"/> uses the deferred path in play mode.</summary>
    public static bool UseDeferredRendering { get; private set; } = true;

    static string? PathFor(Project? p) =>
        p == null ? null : System.IO.Path.Combine(p.RootPath, "ProjectSettings", "rendering.json");

    public static void Load(Project? project)
    {
        UseDeferredRendering = true;
        var path = PathFor(project);
        if (path == null || !File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("useDeferredRendering", out var d))
                UseDeferredRendering = d.GetBoolean();
        }
        catch
        {
            /* keep defaults */
        }
    }

    public static void Save(Project? project, bool useDeferred)
    {
        var path = PathFor(project);
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new { useDeferredRendering = useDeferred },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            UseDeferredRendering = useDeferred;
        }
        catch
        {
            /* ignore */
        }
    }
}
