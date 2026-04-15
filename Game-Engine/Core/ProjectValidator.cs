using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
#if !PLAYER
using Game_Engine.Core.Extensibility;
#endif

namespace Game_Engine.Core;

public static class ProjectValidator
{
    public static IReadOnlyList<string> ValidateCurrentProject()
    {
        var project = ProjectService.Current;
        var issues = new List<string>();
        if (project == null)
        {
            issues.Add("No project is currently open.");
            return issues;
        }

        foreach (var scenePath in Directory.EnumerateFiles(project.ScenesPath, "*.scene", SearchOption.AllDirectories))
            ValidateScene(scenePath, project.RootPath, issues);

#if !PLAYER
        foreach (var line in ExtensionDiagnostics.GetValidationIssues())
            issues.Add(line);
#endif

        return issues;
    }

    private static void ValidateScene(string scenePath, string projectRoot, List<string> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(scenePath));
            WalkElement(doc.RootElement, scenePath, projectRoot, issues);
        }
        catch (Exception ex)
        {
            issues.Add($"{scenePath}: failed to parse scene ({ex.Message})");
        }
    }

    private static void WalkElement(JsonElement element, string scenePath, string projectRoot, List<string> issues)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(element, "type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
            {
                var behaviorType = typeProp.GetString();
                if (!string.IsNullOrWhiteSpace(behaviorType) && SceneSerialization.ResolveType(behaviorType) == null)
                    issues.Add($"{scenePath}: unresolved behavior type '{behaviorType}'.");
            }

            foreach (var prop in element.EnumerateObject())
            {
                var key = prop.Name.ToLowerInvariant();
                if (prop.Value.ValueKind == JsonValueKind.String && key.Contains("path"))
                {
                    var value = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        var abs = ResolvePath(projectRoot, value);
                        if (!File.Exists(abs))
                            issues.Add($"{scenePath}: missing file for '{prop.Name}' -> {value}");
                    }
                }

                WalkElement(prop.Value, scenePath, projectRoot, issues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                WalkElement(item, scenePath, projectRoot, issues);
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string ResolvePath(string projectRoot, string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(Path.Combine(projectRoot, path));
    }
}
