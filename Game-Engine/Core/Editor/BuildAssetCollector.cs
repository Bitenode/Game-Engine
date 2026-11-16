#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Collects only the asset files a player build needs, so builds do not package the entire
/// Assets tree (often many GB). Three sources are followed:
/// <list type="bullet">
/// <item>Paths quoted in the build scenes and in every document those reach (materials, prefabs, JSON data).</item>
/// <item>Paths and folders named by the project's player scripts. A script that loads
/// <c>Assets/Game/Data/Items.json</c> or lists <c>Assets/Game/Audio</c> never appears in a scene.</item>
/// <item>Files that belong to a model: its <c>_Materials</c> and <c>_Animations</c> folders and the textures it names.</item>
/// </list>
/// </summary>
public static class BuildAssetCollector
{
    /// <summary>What a build must stage.</summary>
    public sealed class Result
    {
        /// <summary>Absolute paths of files under the project Assets folder.</summary>
        public List<string> Files { get; } = new();

        /// <summary>
        /// Files a scene names by absolute path outside the project. <c>RelativePath</c> is where the
        /// copy belongs under Assets in the build.
        /// </summary>
        public List<(string Source, string RelativePath)> External { get; } = new();

        /// <summary>
        /// Absolute paths as written in the scenes, mapped to the project-relative path the build should use.
        /// The authoring machine's drive does not exist on a console.
        /// </summary>
        public Dictionary<string, string> PathRemap { get; } = new(StringComparer.Ordinal);

        /// <summary>Absolute paths that were found neither on this machine nor under Assets.</summary>
        public List<string> Unresolved { get; } = new();

        /// <summary>
        /// Images named by a <c>.material</c> file. Only these go through the renderer's compressed-texture
        /// path; sky, terrain-layer and UI images are uploaded as plain RGBA.
        /// </summary>
        public HashSet<string> MaterialTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private const string ExternalFolder = "_external";

    // Any quoted string that ends in a file extension. Existence on disk decides whether it is an asset.
    private static readonly Regex QuotedFile = new(
        "\"([^\"\\r\\n]{1,400}\\.[A-Za-z0-9]{1,12})\"",
        RegexOptions.Compiled);

    // Folder references such as "Assets/Game/Data/Diseases".
    private static readonly Regex QuotedAssetsFolder = new(
        "\"(Assets[/\\\\][^\"\\r\\n]{1,300})\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // C# string literals, regular and verbatim.
    private static readonly Regex CsLiteral = new(
        "@\"((?:[^\"]|\"\")*)\"|\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"",
        RegexOptions.Compiled);

    // Image extensions inside a model file (binary FBX keeps texture names as plain bytes).
    // Only the extension is matched; the name is read backwards from it, which stays linear on large files.
    private static readonly Regex ModelTextureExtension = new(
        "\\.(?:png|jpg|jpeg|tga|tif|tiff|bmp|dds)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".material", ".prefab", ".biomegraph", ".json", ".shadergraph", ".terrain", ".planet", ".blueprint", ".mtl"
    };

    private static readonly HashSet<string> NeverPacked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".meta", ".scene", ".csproj", ".sln", ".dll", ".exe", ".pdb", ".bak"
    };

    /// <summary>Absolute paths under Assets that the build scenes need. See <see cref="Collect"/> for the full result.</summary>
    public static IReadOnlyList<string> CollectForScenes(Project proj, IEnumerable<string> scenePaths)
        => Collect(proj, scenePaths).Files;

    public static Result Collect(Project proj, IEnumerable<string> scenePaths)
    {
        var result = new Result();
        var assetsRoot = Path.GetFullPath(proj.AssetsPath);
        var rootPath = Path.GetFullPath(proj.RootPath);
        var scenes = scenePaths.Where(s => !string.IsNullOrWhiteSpace(s) && File.Exists(s))
            .Select(Path.GetFullPath).ToList();
        var sceneSet = new HashSet<string>(scenes, StringComparer.OrdinalIgnoreCase);

        var pending = new Queue<string>();
        var visitedDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var external = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var externalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triedStrings = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, List<string>>? byName = null; // file name → every file under Assets with that name
        bool inMaterial = false;                           // the document being read is a .material
        bool planetContentAdded = false;

        foreach (var scene in scenes)
            pending.Enqueue(scene);

        ScanScripts();

        while (pending.Count > 0)
        {
            var docPath = pending.Dequeue();
            if (!visitedDocs.Add(docPath))
                continue;

            string text;
            try { text = File.ReadAllText(docPath); }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not read '{docPath}' for asset refs: {ex.Message}");
                continue;
            }

            bool isScene = sceneSet.Contains(docPath);
            inMaterial = Path.GetExtension(docPath).Equals(".material", StringComparison.OrdinalIgnoreCase);
            foreach (Match m in QuotedFile.Matches(text))
                Reference(m.Groups[1].Value, isScene);
            foreach (Match m in QuotedAssetsFolder.Matches(text))
                ReferenceFolder(Unescape(m.Groups[1].Value));

            // Materials also list textures in a nested object. Extensionless values would miss the regex above.
            if (inMaterial)
                CollectMaterialTextures(docPath);
            inMaterial = false;
        }

        result.Files.AddRange(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        foreach (var kv in external.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase))
            result.External.Add((kv.Key, kv.Value));
        result.Unresolved.AddRange(unresolved.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));

        Log.Info($"[Build] Asset collector: {result.Files.Count} project file(s), {result.External.Count} external, "
                 + $"{result.PathRemap.Count} remapped path(s) for {scenes.Count} scene(s).");
        foreach (var missing in result.Unresolved)
            Log.Warning($"[Build] Referenced file is not on this PC or in Assets, so it is not in the build: {missing}");
        return result;

        // ---- reference handling ------------------------------------------------------------

        void Reference(string raw, bool fromScene)
        {
            if (!triedStrings.Add((fromScene ? "s|" : inMaterial ? "m|" : "d|") + raw)) return;
            var stored = Unescape(raw).Trim();
            if (stored.Length == 0) return;

            if (Path.IsPathRooted(stored))
            {
                ReferenceRooted(raw, stored, fromScene);
                return;
            }

            foreach (var candidate in new[] { Combine(rootPath, stored), Combine(assetsRoot, stored) })
            {
                if (candidate != null && File.Exists(candidate) && IsUnder(candidate, assetsRoot))
                {
                    AddFile(candidate);
                    return;
                }
            }
        }

        void ReferenceRooted(string raw, string stored, bool fromScene)
        {
            string? full = SafeFullPath(stored);
            if (full == null) return;

            // Inside this project: keep the file, and let the build use the relative form.
            if (IsUnder(full, assetsRoot))
            {
                if (!File.Exists(full)) return;
                AddFile(full);
                if (fromScene) result.PathRemap[raw] = ToProjectRelative(full);
                return;
            }

            // A real file somewhere else on this PC. That is the file the editor loads, so ship it,
            // unless Assets already holds the same file.
            if (File.Exists(full))
            {
                if (NeverPacked.Contains(Path.GetExtension(full))) return;
                var twin = AssetPathRemap.FindUnderAssets(stored, assetsRoot);
                if (twin != null && SameLength(twin, full))
                {
                    AddFile(twin);
                    if (fromScene) result.PathRemap[raw] = ToProjectRelative(twin);
                    return;
                }

                if (!external.TryGetValue(full, out var rel))
                {
                    var name = Path.GetFileName(full);
                    if (!externalNames.Add(name))
                    {
                        // Same file name from two folders: keep both apart.
                        name = Path.GetFileNameWithoutExtension(full) + "_"
                             + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetDirectoryName(full) ?? "")).ToString("x")
                             + Path.GetExtension(full);
                        externalNames.Add(name);
                    }
                    rel = ExternalFolder + "/" + name;
                    external[full] = rel;
                    if (DocExtensions.Contains(Path.GetExtension(full)))
                        pending.Enqueue(full);
                }
                if (fromScene) result.PathRemap[raw] = "Assets/" + rel;
                return;
            }

            // Saved on another machine or checkout. Use the copy in this project.
            var found = AssetPathRemap.FindUnderAssets(stored, assetsRoot);
            if (found != null)
            {
                AddFile(found);
                if (fromScene) result.PathRemap[raw] = ToProjectRelative(found);
            }
            else if (LooksLikeAsset(stored))
            {
                unresolved.Add(stored);
            }
        }

        void ReferenceFolder(string stored)
        {
            if (!triedStrings.Add("f|" + stored)) return;
            foreach (var candidate in new[] { Combine(rootPath, stored), Combine(assetsRoot, stored) })
            {
                if (candidate == null || !Directory.Exists(candidate)) continue;
                if (!IsUnder(candidate, assetsRoot)) continue; // also rejects the Assets folder itself
                try
                {
                    foreach (var f in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                        AddFile(f);
                }
                catch { /* best-effort */ }
                return;
            }
        }

        void AddFile(string abs)
        {
            abs = Path.GetFullPath(abs);
            var ext = Path.GetExtension(abs);
            if (NeverPacked.Contains(ext)) return;
            if (inMaterial) result.MaterialTextures.Add(abs);
            if (!files.Add(abs)) return;

            if (DocExtensions.Contains(ext) && !visitedDocs.Contains(abs))
                pending.Enqueue(abs);
            if (IsModelExtension(ext))
                IncludeModelSidecars(abs);
            // A planet's trees, grass and animals are not named in the scene. The graph names a
            // profile id and a species id; the files live in known folders.
            if (!planetContentAdded && (ext.Equals(".planet", StringComparison.OrdinalIgnoreCase)
                                         || ext.Equals(".biomegraph", StringComparison.OrdinalIgnoreCase)))
            {
                planetContentAdded = true;
                IncludePlanetContent();
            }
        }

        void IncludePlanetContent()
        {
            foreach (var folder in new[] { "Biomes", "Animals" })
            {
                var dir = Path.Combine(assetsRoot, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    AddFile(f);
            }
            // Custom shaders the planet materials and weather look up by path.
            foreach (var pattern in new[] { "*.shader", "*.shadergraph" })
            {
                foreach (var f in Directory.EnumerateFiles(assetsRoot, pattern, SearchOption.AllDirectories))
                    AddFile(f);
            }
        }

        // ---- scripts -----------------------------------------------------------------------

        void ScanScripts()
        {
            List<string> scripts;
            try
            {
                scripts = ScriptCompiler.CollectProjectCsFiles(
                    new[] { proj.AssetsPath, proj.PackagesPath }.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)),
                    playerBuild: true);
            }
            catch (Exception ex)
            {
                Log.Warning("[Build] Could not scan scripts for asset references: " + ex.Message);
                return;
            }

            foreach (var script in scripts)
            {
                string[] lines;
                try { lines = File.ReadAllLines(script); }
                catch { continue; }

                foreach (var line in lines)
                {
                    var literals = new List<(string Value, int Start, int End)>();
                    foreach (Match m in CsLiteral.Matches(line))
                    {
                        var value = m.Groups[1].Success
                            ? m.Groups[1].Value.Replace("\"\"", "\"")
                            : Unescape(m.Groups[2].Value);
                        literals.Add((value, m.Index, m.Index + m.Length));
                    }
                    if (literals.Count == 0) continue;

                    foreach (var lit in literals)
                        ReferenceFromScript(lit.Value);

                    // Path.Combine(root, "Assets", "Game", "Data", "Items.json"): join literals that only a comma separates.
                    var run = new List<string>();
                    for (int i = 0; i < literals.Count; i++)
                    {
                        if (run.Count > 0 && line.Substring(literals[i - 1].End, literals[i].Start - literals[i - 1].End).Trim() != ",")
                        {
                            if (run.Count > 1) ReferenceFromScript(string.Join("/", run));
                            run.Clear();
                        }
                        run.Add(literals[i].Value);
                    }
                    if (run.Count > 1) ReferenceFromScript(string.Join("/", run));
                }
            }
        }

        void ReferenceFromScript(string value)
        {
            value = value.Trim();
            if (value.Length < 3 || value.Length > 400) return;
            if (value.IndexOfAny(new[] { '*', '?', '<', '>', '|', '{', '}' }) >= 0) return;
            if (!triedStrings.Add("c|" + value)) return;

            bool hasSeparator = value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0;
            if (hasSeparator)
            {
                if (Path.IsPathRooted(value)) return;
                foreach (var candidate in new[] { Combine(rootPath, value), Combine(assetsRoot, value) })
                {
                    if (candidate == null || !IsUnder(candidate, assetsRoot)) continue;
                    if (File.Exists(candidate)) { AddFile(candidate); return; }
                    if (Directory.Exists(candidate)) { ReferenceFolder(value); return; }
                }
                return;
            }

            // A bare file name such as "Items.json" that the script appends to a folder.
            var ext = Path.GetExtension(value);
            if (ext.Length < 2 || NeverPacked.Contains(ext)) return;
            var hits = FilesNamed(value);
            if (hits == null || hits.Count == 0 || hits.Count > 8) return;
            foreach (var hit in hits)
                AddFile(hit);
        }

        List<string>? FilesNamed(string fileName)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                    {
                        var key = Path.GetFileName(f);
                        if (!byName.TryGetValue(key, out var list))
                            byName[key] = list = new List<string>(1);
                        list.Add(f);
                    }
                }
                catch { /* best-effort */ }
            }
            return byName.TryGetValue(fileName, out var found) ? found : null;
        }

        // ---- materials and models ----------------------------------------------------------

        void CollectMaterialTextures(string materialPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(materialPath));
                if (!doc.RootElement.TryGetProperty("textures", out var textures)
                    || textures.ValueKind != JsonValueKind.Object)
                    return;

                foreach (var prop in textures.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var value = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    // GetString() already unescaped it. Reference() expects the text as written in the file.
                    Reference(value.Replace("\\", "\\\\"), fromScene: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not parse material '{materialPath}': {ex.Message}");
            }
        }

        void IncludeModelSidecars(string modelAbs)
        {
            try
            {
                var dir = Path.GetDirectoryName(modelAbs);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var baseName = Path.GetFileNameWithoutExtension(modelAbs);

                // Materials and bone clips the importer wrote next to the model. A read-only console
                // package cannot regenerate them.
                foreach (var suffix in new[] { "_Materials", "_Animations" })
                {
                    var sidecarDir = Path.Combine(dir, baseName + suffix);
                    if (!Directory.Exists(sidecarDir)) continue;
                    foreach (var f in Directory.EnumerateFiles(sidecarDir, "*", SearchOption.AllDirectories))
                        if (IsUnder(f, assetsRoot)) AddFile(f);
                }

                // Same-folder companions that share the model basename (e.g. foo.bin next to foo.gltf).
                foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*", SearchOption.TopDirectoryOnly))
                    if (IsUnder(f, assetsRoot) && !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) AddFile(f);

                // SpeedTree-style trees (Pine_002) ship materials with no textures. The planet binds
                // these three by file name from a sibling Materials folder when the tree loads
                // (PlanetVegetationSystem.FixSpeedTreeImportedTreeMaterials). Neither the model nor its
                // .material names them, so without this the console draws that tree untextured.
                var siblingMaterials = Path.Combine(dir, "Materials");
                if (Directory.Exists(siblingMaterials))
                {
                    foreach (var texName in new[] { "Branches_Albedo", "trunk_Albedo", "Branches_MaskMap" })
                    {
                        foreach (var texExt in new[] { ".tif", ".tga", ".png", ".jpg", ".jpeg", ".webp" })
                        {
                            var hit = Directory.EnumerateFiles(siblingMaterials, texName + texExt, SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (hit == null || !IsUnder(hit, assetsRoot)) continue;
                            AddFile(hit);
                            break;
                        }
                    }
                }

                // Textures the model file itself names. The importer looks these up when it builds materials.
                string modelText;
                try { modelText = Encoding.Latin1.GetString(File.ReadAllBytes(modelAbs)); }
                catch { return; }
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in ModelTextureExtension.Matches(modelText))
                {
                    int start = m.Index;
                    while (start > 0 && m.Index - start < 120 && IsFileNameChar(modelText[start - 1]))
                        start--;
                    var name = modelText.Substring(start, m.Index + m.Length - start).Trim();
                    if (name.Length <= m.Length || !seenNames.Add(name)) continue;
                    var hits = FilesNamed(name);
                    if (hits == null || hits.Count == 0) continue;
                    AddFile(Nearest(hits, dir));
                }
            }
            catch { /* best-effort */ }
        }

        // ---- helpers -----------------------------------------------------------------------

        string ToProjectRelative(string abs)
            => Path.GetRelativePath(rootPath, abs).Replace('\\', '/');
    }

    /// <summary>The hit that shares the longest folder prefix with <paramref name="dir"/>.</summary>
    private static string Nearest(List<string> hits, string dir)
    {
        if (hits.Count == 1) return hits[0];
        string best = hits[0];
        int bestScore = -1;
        foreach (var hit in hits)
        {
            int n = Math.Min(hit.Length, dir.Length), score = 0;
            while (score < n && char.ToUpperInvariant(hit[score]) == char.ToUpperInvariant(dir[score])) score++;
            if (score > bestScore) { bestScore = score; best = hit; }
        }
        return best;
    }

    private static bool IsFileNameChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ' or '(' or ')' or '&' or '\'';

    private static bool LooksLikeAsset(string stored)
    {
        var ext = Path.GetExtension(stored).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".tif" or ".tiff" or ".dds" or ".hdr"
            or ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae"
            or ".wav" or ".mp3" or ".ogg" or ".flac"
            or ".material" or ".prefab" or ".json" or ".boneanim" or ".ttf" or ".otf";
    }

    /// <summary>JSON and C# string escapes that occur in paths: <c>\\</c>, <c>\/</c> and <c>\"</c>.</summary>
    private static string Unescape(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length && (raw[i + 1] == '\\' || raw[i + 1] == '/' || raw[i + 1] == '"'))
            {
                sb.Append(raw[i + 1]);
                i++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? Combine(string root, string relative)
    {
        try { return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return null; }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)); }
        catch { return null; }
    }

    private static bool SameLength(string a, string b)
    {
        try { return new FileInfo(a).Length == new FileInfo(b).Length; }
        catch { return false; }
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="root"/> (not the root itself).</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
