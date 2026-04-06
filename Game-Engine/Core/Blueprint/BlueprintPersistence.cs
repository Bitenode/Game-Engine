#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Blueprint document on disk (JSON, typically extension .blueprint).</summary>
    public sealed class BlueprintDocument
    {
        public int Version { get; set; } = 1;
        public BlueprintGraph Graph { get; set; } = new();
    }

    /// <summary>Load/save <c>.blueprint</c> JSON. Default project folder <c>Assets/Blueprints/</c>; see <c>Docs/14_Visual_Blueprints.md</c>.</summary>
    public static class BlueprintPersistence
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string BlueprintsFolderAbs(string projectRoot)
            => Path.Combine(projectRoot, "Assets", "Blueprints");

        /// <summary>Ensures Assets/Blueprints exists; returns absolute path.</summary>
        public static string EnsureBlueprintsFolder(string projectRoot)
        {
            var dir = BlueprintsFolderAbs(projectRoot);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Save(string filePathAbs, BlueprintGraph graph)
        {
            var doc = new BlueprintDocument { Version = 1, Graph = graph };
            var json = JsonSerializer.Serialize(doc, JsonOptions);
            var dir = Path.GetDirectoryName(filePathAbs);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePathAbs, json);
        }

        public static BlueprintDocument LoadDocument(string filePathAbs)
        {
            var text = File.ReadAllText(filePathAbs);
            var doc = JsonSerializer.Deserialize<BlueprintDocument>(text, JsonOptions);
            if (doc?.Graph == null)
                throw new InvalidDataException("Invalid blueprint file.");
            doc.Graph.Nodes ??= new();
            doc.Graph.Wires ??= new();
            BlueprintFlowRuntime.NormalizeLegacyPins(doc.Graph);
            return doc;
        }

        public static string? TryGetDisplayPath(string? absPath, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(absPath)) return null;
            try
            {
                var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(absPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(root, full).Replace('\\', '/');
            }
            catch { /* ignore */ }
            return absPath;
        }
    }
}
