#if !PLAYER
#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Game_Engine.Core;

/// <summary>
/// Reusable Roslyn-based script compiler used by both the Script Editor (hot-reload)
/// and the Build Settings (producing GameScripts.dll for standalone builds).
/// </summary>
public static class ScriptCompiler
{
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
    /// <returns>A <see cref="CompileResult"/> indicating success or failure.</returns>
    public static CompileResult CompileToDll(
        IEnumerable<string> scriptRoots,
        string outputDllPath,
        string? assemblyName = null,
        bool optimized = false)
    {
        // 1. Collect .cs files
        var allFiles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in scriptRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    var s = f.Replace('/', Path.DirectorySeparatorChar);
                    var d = Path.DirectorySeparatorChar;
                    if (s.IndexOf($"{d}obj{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.IndexOf($"{d}bin{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.IndexOf($"{d}.git{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    var full = Path.GetFullPath(f);
                    if (seen.Add(full))
                        allFiles.Add(full);
                }
            }
            catch { /* skip bad directories */ }
        }

        if (allFiles.Count == 0)
        {
            return new CompileResult
            {
                Success = true,
                FileCount = 0,
                ErrorText = "No .cs script files found — skipping script compilation."
            };
        }

        // 2. Parse syntax trees
        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var trees = allFiles
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOpts, f))
            .ToList();

        // Global usings prelude so user scripts can use engine + Avalonia types
        const string Prelude = @"
            global using Avalonia.Controls;
            global using Game_Engine.Views;
        ";
        trees.Insert(0, CSharpSyntaxTree.ParseText(Prelude, parseOpts, "ScriptPrelude.g.cs"));

        // 3. Collect metadata references from currently loaded assemblies
        var refs = CollectMetadataReferences();

        // 4. Compile
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

        // 5. Write DLL to disk
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
    /// Collect metadata references from all currently-loaded assemblies.
    /// </summary>
    private static List<MetadataReference> CollectMetadataReferences()
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
