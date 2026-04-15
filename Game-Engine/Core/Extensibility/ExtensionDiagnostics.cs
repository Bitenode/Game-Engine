using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core.Extensibility;

/// <summary>Last editor extension load snapshot for validation UI and logs.</summary>
public static class ExtensionDiagnostics
{
    public sealed class Snapshot
    {
        public string LoadSource { get; init; } = "";
        public string? EditorScriptsDir { get; init; }
        public string? ManifestPath { get; init; }
        public EditorExtensionsManifest? Manifest { get; init; }
        public IReadOnlyList<string> LoadedDllPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExtensionTypeNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CommandIdCollisions { get; init; } = Array.Empty<string>();
    }

    public static Snapshot? Last { get; private set; }

    /// <summary>Whether the last <see cref="RecordCompileReload"/> reported success.</summary>
    public static bool LastCompileReloadSucceeded { get; private set; }

    /// <summary>Human-readable summary from the last script compile / extension reload (palette or script editor).</summary>
    public static string? LastCompileReloadMessage { get; private set; }

    public static void Record(Snapshot snapshot)
    {
        Last = snapshot;
    }

    public static void RecordCompileReload(bool success, string message)
    {
        LastCompileReloadSucceeded = success;
        LastCompileReloadMessage = message;
    }

    /// <summary>Non-fatal issues for <see cref="ProjectValidator"/> and console.</summary>
    public static IReadOnlyList<string> GetValidationIssues()
    {
        var snap = Last;
        var list = new List<string>();
        if (snap == null)
        {
            list.Add("Extensions: no load snapshot yet (open a project or reload extensions).");
            return list;
        }

        list.Add($"Extensions [{snap.LoadSource}]: {snap.ExtensionTypeNames.Count} EditorExtension type(s) active.");
        if (!string.IsNullOrEmpty(snap.EditorScriptsDir))
            list.Add($"  EditorScripts: {snap.EditorScriptsDir}");
        if (snap.Manifest != null)
        {
            var m = snap.Manifest;
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.DisplayName)) meta.Add($"name={m.DisplayName}");
            if (!string.IsNullOrWhiteSpace(m.Version)) meta.Add($"ver={m.Version}");
            if (!string.IsNullOrWhiteSpace(m.Author)) meta.Add($"by={m.Author}");
            if (!string.IsNullOrWhiteSpace(m.Description)) meta.Add(m.Description!);
            if (meta.Count > 0)
                list.Add("  Manifest: " + string.Join(", ", meta));
            if (!string.IsNullOrWhiteSpace(m.HomepageUrl))
                list.Add($"  Homepage: {m.HomepageUrl}");
        }
        else if (!string.IsNullOrEmpty(snap.ManifestPath))
            list.Add($"  Manifest: (none or invalid JSON at {snap.ManifestPath})");

        foreach (var p in snap.LoadedDllPaths)
            list.Add($"  DLL: {p}");

        foreach (var e in snap.Errors)
            list.Add("  Error: " + e);
        foreach (var w in snap.Warnings)
            list.Add("  Warning: " + w);
        foreach (var c in snap.CommandIdCollisions)
            list.Add("  Command id collision: " + c);

        return list;
    }
}
