#if !PLAYER
#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game_Engine.Core;

/// <summary>
/// Reusable Roslyn-based script compiler used by both the Script Editor (hot-reload)
/// and the Build Settings (producing GameScripts.dll for standalone builds).
/// </summary>
public static class ScriptCompiler
{
    /// <summary>
    /// UI samples already compiled into Engine.Player — do not emit them again into GameScripts.dll.
    /// </summary>
    static readonly HashSet<string> s_playerBundledFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HotbarController.cs",
        "MainMenuController.cs",
        "ServerHostController.cs"
    };

    static readonly Regex s_windowsCopyName = new(@" \(\d+\)$", RegexOptions.Compiled);
    static readonly Regex s_topLevelType = new(
        @"^((?:public|internal|sealed|abstract|static|partial)\s+)*(?:class|struct|interface|enum|record)\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Result returned from <see cref="CompileToDll"/>.
    /// </summary>
    public sealed class CompileResult
    {
        public bool Success { get; init; }
        public string? DllPath { get; init; }
        public string? ErrorText { get; init; }
        public int FileCount { get; init; }
    }

    /// <summary>
    /// Collect all .cs files under the given root directories (skipping bin/obj/.git),
    /// compile them with Roslyn, and write the resulting DLL to <paramref name="outputDllPath"/>.
    /// </summary>
    /// <param name="scriptRoots">Directories to search for .cs files.</param>
    /// <param name="outputDllPath">Full path for the output DLL file.</param>
    /// <param name="assemblyName">Optional assembly name. Auto-generated if null.</param>
    /// <param name="optimized">True for Release optimizations; false for Debug.</param>
    /// <param name="playerBuild">When true, omit editor extensions and scripts already linked into the player.</param>
    /// <returns>A <see cref="CompileResult"/> indicating success or failure.</returns>
    public static CompileResult CompileToDll(
        IEnumerable<string> scriptRoots,
        string outputDllPath,
        string? assemblyName = null,
        bool optimized = false,
        bool playerBuild = false)
    {
        var allFiles = CollectProjectCsFiles(scriptRoots, playerBuild);

        if (allFiles.Count == 0)
        {
            return new CompileResult
            {
                Success = true,
                FileCount = 0,
                ErrorText = "No .cs script files found — skipping script compilation."
            };
        }

        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        if (playerBuild)
            parseOpts = parseOpts.WithPreprocessorSymbols("PLAYER");

        var trees = allFiles
            .Select(f => CSharpSyntaxTree.ParseText(ReadScriptSource(f), parseOpts, f))
            .ToList();

        // Avalonia 11 used SystemDecorations; 12 renamed it to WindowDecorations.
        var prelude = playerBuild
            ? """
            global using Avalonia.Controls;
            global using SystemDecorations = Avalonia.Controls.WindowDecorations;
            """
            : """
            global using Avalonia.Controls;
            global using Game_Engine.Views;
            global using SystemDecorations = Avalonia.Controls.WindowDecorations;
            """;
        trees.Insert(0, CSharpSyntaxTree.ParseText(prelude, parseOpts, "ScriptPrelude.g.cs"));

        var refs = CollectMetadataReferences();

        var compOpts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(optimized ? OptimizationLevel.Release : OptimizationLevel.Debug)
            .WithAllowUnsafe(true);

        var asmName = assemblyName ?? ("GameScripts_" + Guid.NewGuid().ToString("N"));
        var compilation = CSharpCompilation.Create(asmName, trees, refs, compOpts);

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return new CompileResult
            {
                Success = false,
                FileCount = allFiles.Count,
                ErrorText = errors
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputDllPath)!);
        File.WriteAllBytes(outputDllPath, ms.ToArray());

        return new CompileResult
        {
            Success = true,
            DllPath = outputDllPath,
            FileCount = allFiles.Count
        };
    }

    /// <summary>
    /// Get the script root directories for the current project (Assets/ and Packages/).
    /// </summary>
    public static IEnumerable<string> GetProjectScriptRoots()
    {
        var p = ProjectService.Current;
        if (p == null) yield break;

        var seeds = new[] { p.AssetsPath, p.PackagesPath };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in seeds)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) yield return full;
        }
    }

    /// <summary>
    /// Enumerate compile candidates under <paramref name="scriptRoots"/>, skipping junk folders,
    /// Windows duplicate copies, same-name files (prefers <c>Assets/Scripts</c> over Standard Assets),
    /// and — for player builds — editor-only samples.
    /// </summary>
    public static List<string> CollectProjectCsFiles(IEnumerable<string> scriptRoots, bool playerBuild = false)
    {
        var raw = new List<string>();
        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in scriptRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    var s = f.Replace('/', Path.DirectorySeparatorChar);
                    var d = Path.DirectorySeparatorChar;
                    if (s.IndexOf($"{d}obj{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.IndexOf($"{d}bin{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.IndexOf($"{d}.git{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    var fileName = Path.GetFileNameWithoutExtension(s);
                    if (s_windowsCopyName.IsMatch(fileName)) continue;

                    var full = Path.GetFullPath(f);
                    if (seenPath.Add(full))
                        raw.Add(full);
                }
            }
            catch { /* skip unreadable directories */ }
        }

        if (playerBuild)
            raw = raw.Where(p => !IsExcludedFromPlayerBuild(p)).ToList();

        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in raw.OrderBy(PreferUserScriptsThenPath))
        {
            var name = Path.GetFileName(path);
            if (byFileName.TryGetValue(name, out var existing))
            {
                Log.Info($"[Scripts] Skipping duplicate '{name}' ({path}) — using {existing}");
                continue;
            }
            byFileName[name] = path;
        }

        var claimedTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var path in byFileName.Values.OrderBy(PreferUserScriptsThenPath))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            var clash = false;
            foreach (Match m in s_topLevelType.Matches(text))
            {
                var typeName = m.Groups[2].Value;
                if (claimedTypes.TryGetValue(typeName, out var owner))
                {
                    Log.Info($"[Scripts] Skipping '{path}' — type {typeName} already defined in {owner}");
                    clash = true;
                    break;
                }
            }
            if (clash) continue;

            foreach (Match m in s_topLevelType.Matches(text))
                claimedTypes[m.Groups[2].Value] = path;
            result.Add(path);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    static string PreferUserScriptsThenPath(string path)
    {
        var n = path.Replace('/', Path.DirectorySeparatorChar);
        var d = Path.DirectorySeparatorChar;
        bool std = n.IndexOf($"{d}Standard Assets{d}", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf($"{d}Standard Assets", StringComparison.OrdinalIgnoreCase) >= 0;
        bool scripts = n.IndexOf($"{d}Scripts{d}", StringComparison.OrdinalIgnoreCase) >= 0;
        int rank = std ? 2 : scripts ? 0 : 1;
        return rank.ToString("D1") + n;
    }

    static bool IsExcludedFromPlayerBuild(string path)
    {
        var n = path.Replace('/', Path.DirectorySeparatorChar);
        var file = Path.GetFileName(n);
        if (s_playerBundledFileNames.Contains(file)) return true;
        if (file.Contains("EditorExtension", StringComparison.OrdinalIgnoreCase)) return true;
        if (ContainsFolder(n, "Custom Menu's") || ContainsFolder(n, "Custom Menus")) return true;
        if (ContainsFolder(n, "Scene Tools")) return true;
        if (ContainsFolder(n, "Project Tools")) return true;
        if (ContainsFolder(n, "Batch Tools")) return true;

        try
        {
            var text = File.ReadAllText(path);
            if (text.Contains("EditorExtension", StringComparison.Ordinal)) return true;
            if (text.Contains("ICustomInspector", StringComparison.Ordinal)) return true;
            if (text.Contains("Game_Engine.Core.Extensibility", StringComparison.Ordinal)) return true;
            if (text.Contains("Game_Engine.Core.UIX", StringComparison.Ordinal)) return true;
            if (text.Contains("Game_Engine.Core.Editor", StringComparison.Ordinal)) return true;
        }
        catch { return true; }

        return false;
    }

    /// <summary>Read a script, rewriting Avalonia 11 <c>SystemDecorations</c> to Avalonia 12 <c>WindowDecorations</c>.</summary>
    public static string ReadScriptSource(string path)
    {
        var src = File.ReadAllText(path);
        return src
            .Replace(".SystemDecorations", ".WindowDecorations")
            .Replace("SystemDecorations.", "WindowDecorations.");
    }

    static bool ContainsFolder(string path, string folder)
    {
        var d = Path.DirectorySeparatorChar;
        var needle = d + folder + d;
        return path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static List<MetadataReference> CollectMetadataReferences()
    {
        var list = new List<MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.IsDynamic) continue;
                var loc = asm.Location;
                if (string.IsNullOrWhiteSpace(loc)) continue;
                if (!File.Exists(loc)) continue;
                list.Add(MetadataReference.CreateFromFile(loc));
            }
            catch { /* ignore assemblies that can't be resolved */ }
        }
        return list;
    }
}
#endif
