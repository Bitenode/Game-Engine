#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Collects only the asset files referenced by the scenes included in a player build,
/// so Builds do not package/extract the entire Assets tree (often many GB).
/// </summary>
public static class BuildAssetCollector
{
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fbx", ".obj", ".gltf", ".glb", ".dae", ".blend",
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".webp", ".hdr", ".exr", ".ktx", ".ktx2", ".basis",
        ".material", ".shader", ".shadergraph", ".prefab",
        ".wav", ".mp3", ".ogg", ".flac",
        ".ttf", ".otf",
        ".biomegraph", ".terrain", ".json", ".bin", ".mesh"
    };

    private static readonly Regex QuotedPath = new(
        "\"([^\"]+\\.(?:" + string.Join("|", AssetExtensions.Select(e => e.TrimStart('.'))) + "))\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns absolute file paths under the project that should be packed into Assets.dll,
    /// keyed for packaging as paths relative to <paramref name="assetsRoot"/>.
    /// </summary>
    public static IReadOnlyList<string> CollectForScenes(Project proj, IEnumerable<string> scenePaths)
    {
        var assetsRoot = Path.GetFullPath(proj.AssetsPath);
        var rootPath = Path.GetFullPath(proj.RootPath);
        var pending = new Queue<string>();
        var visitedDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scene in scenePaths)
        {
            if (!string.IsNullOrWhiteSpace(scene) && File.Exists(scene))
                EnqueueDoc(scene);
        }

        while (pending.Count > 0)
        {
            var docPath = pending.Dequeue();
            if (!visitedDocs.Add(Path.GetFullPath(docPath)))
                continue;

            string text;
            try { text = File.ReadAllText(docPath); }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not read '{docPath}' for asset refs: {ex.Message}");
                continue;
            }

            foreach (Match m in QuotedPath.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(raw) || raw.Equals("null", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var abs in ResolveCandidates(raw, rootPath, assetsRoot))
                {
                    if (!File.Exists(abs)) continue;
                    if (!IsUnderAssets(abs, assetsRoot) && !IsUnderRoot(abs, rootPath))
                        continue;

                    // Only pack files that live under Assets/ (player extracts into Assets/).
                    if (!IsUnderAssets(abs, assetsRoot))
                        continue;

                    if (!files.Add(abs))
                        continue;

                    var ext = Path.GetExtension(abs);
                    if (ext.Equals(".material", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".biomegraph", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".shadergraph", StringComparison.OrdinalIgnoreCase))
                    {
                        EnqueueDoc(abs);
                    }

                    if (IsModelExtension(ext))
                        IncludeModelSidecars(abs, files, assetsRoot);
                }
            }

            // Materials also use nested JSON without always matching the regex if extensionless — parse textures object.
            if (Path.GetExtension(docPath).Equals(".material", StringComparison.OrdinalIgnoreCase))
                CollectMaterialTextures(docPath, rootPath, assetsRoot, files, EnqueueDoc);
        }

        Log.Info($"[Build] Scene asset collector: {files.Count} file(s) for {scenePaths.Count()} scene(s) (skipped full Assets tree).");
        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        void EnqueueDoc(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full) && !visitedDocs.Contains(full))
                    pending.Enqueue(full);
            }
            catch { /* ignore bad paths */ }
        }
    }

    private static void CollectMaterialTextures(
        string materialPath,
        string rootPath,
        string assetsRoot,
        HashSet<string> files,
        Action<string> enqueueDoc)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(materialPath));
            if (!doc.RootElement.TryGetProperty("textures", out var textures))
                return;

            foreach (var prop in textures.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var raw = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                foreach (var abs in ResolveCandidates(raw, rootPath, assetsRoot))
                {
                    if (!File.Exists(abs) || !IsUnderAssets(abs, assetsRoot)) continue;
                    if (files.Add(abs) && Path.GetExtension(abs).Equals(".material", StringComparison.OrdinalIgnoreCase))
                        enqueueDoc(abs);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[Build] Could not parse material '{materialPath}': {ex.Message}");
        }
    }

    private static void IncludeModelSidecars(string modelAbs, HashSet<string> files, string assetsRoot)
    {
        try
        {
            var dir = Path.GetDirectoryName(modelAbs);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var baseName = Path.GetFileNameWithoutExtension(modelAbs);
            var materialsDir = Path.Combine(dir, baseName + "_Materials");
            if (Directory.Exists(materialsDir))
            {
                foreach (var f in Directory.EnumerateFiles(materialsDir, "*", SearchOption.AllDirectories))
                {
                    if (IsUnderAssets(f, assetsRoot))
                        files.Add(f);
                }
            }

            // Same-folder companions that share the model basename (e.g. foo.bin next to foo.gltf).
            foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*", SearchOption.TopDirectoryOnly))
            {
                if (IsUnderAssets(f, assetsRoot))
                    files.Add(f);
            }
        }
        catch { /* best-effort */ }
    }

    private static IEnumerable<string> ResolveCandidates(string raw, string rootPath, string assetsRoot)
    {
        var list = new List<string>();
        string Norm(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }

        var stored = raw.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(stored))
        {
            list.Add(Norm(stored));
            // Also try remapping foreign absolute paths by filename under Assets.
            var name = Path.GetFileName(stored);
            if (!string.IsNullOrEmpty(name) && Directory.Exists(assetsRoot))
            {
                try
                {
                    var match = Directory.EnumerateFiles(assetsRoot, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (match != null) list.Add(Norm(match));
                }
                catch { }
            }
            return list;
        }

        list.Add(Norm(Path.Combine(rootPath, stored)));
        if (stored.StartsWith("Assets" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || stored.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            // already covered by rootPath combine
        }
        else
        {
            list.Add(Norm(Path.Combine(assetsRoot, stored)));
        }

        return list;
    }

    private static bool IsUnderAssets(string path, string assetsRoot)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(assetsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsUnderRoot(string path, string rootPath)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsModelExtension(string ext) =>
        ext.Equals(".fbx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".obj", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".dae", StringComparison.OrdinalIgnoreCase);
}
#endif
