using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Game_Engine.Core.Editor;

/// <summary>Result of resolving Go to Definition for the script editor.</summary>
public readonly struct GoToDefinitionResult
{
    public bool Found { get; init; }
    /// <summary>1-based line for <see cref="TargetFilePath"/> or the current document.</summary>
    public int Line1Based { get; init; }
    /// <summary>Set when the definition is in another file on disk.</summary>
    public string? TargetFilePath { get; init; }
    public bool IsSameDocument { get; init; }
}

public readonly struct SymbolReferenceResult
{
    public string FilePath { get; init; }
    public int Line1Based { get; init; }
    public int Column1Based { get; init; }
    public string LineText { get; init; }
}

/// <summary>
/// Builds the same multi-tree script compilation as the editor build (prelude + Assets/Packages .cs),
/// with the active document overlaid from unsaved buffer text, then resolves symbols for F12.
/// </summary>
public static class EditorScriptsGoToDefinition
{
    const string Prelude = @"
                global using Avalonia.Controls;
                global using Game_Engine.Views;
            ";

    public static GoToDefinitionResult TryResolve(string currentSource, string? currentDocumentPath, int caretOffset)
    {
        if (string.IsNullOrEmpty(currentSource)) return default;

        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var refs = CollectMetadataReferences();
        var preludeTree = CSharpSyntaxTree.ParseText(Prelude, parseOpts, "ScriptPrelude.g.cs");
        var trees = new List<SyntaxTree> { preludeTree };
        var scriptPaths = EnumerateScriptFilePaths();
        var curNorm = string.IsNullOrEmpty(currentDocumentPath) ? null : NormalizePath(currentDocumentPath);

        SyntaxTree? docTree = null;

        if (scriptPaths.Count == 0)
        {
            var path = currentDocumentPath ?? "OpenDocument.cs";
            docTree = CSharpSyntaxTree.ParseText(currentSource, parseOpts, path);
            trees.Add(docTree);
        }
        else
        {
            bool matchedDiskPath = false;
            foreach (var f in scriptPaths)
            {
                var full = NormalizePath(f);
                string txt;
                if (curNorm != null && string.Equals(full, curNorm, StringComparison.OrdinalIgnoreCase))
                {
                    txt = currentSource;
                    matchedDiskPath = true;
                }
                else
                {
                    try { txt = File.ReadAllText(f); }
                    catch { continue; }
                }
                var t = CSharpSyntaxTree.ParseText(txt, parseOpts, f);
                trees.Add(t);
                if (curNorm != null && string.Equals(full, curNorm, StringComparison.OrdinalIgnoreCase))
                    docTree = t;
            }

            if (!matchedDiskPath && curNorm != null)
            {
                docTree = CSharpSyntaxTree.ParseText(currentSource, parseOpts, currentDocumentPath!);
                trees.Add(docTree);
            }
            else if (docTree == null)
            {
                docTree = CSharpSyntaxTree.ParseText(currentSource, parseOpts, currentDocumentPath ?? "Untitled.cs");
                trees.Add(docTree);
            }
        }

        var scriptSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in scriptPaths)
            scriptSet.Add(NormalizePath(p));

        var compilation = CSharpCompilation.Create(
            "GoToDefScratch",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true));

        var model = compilation.GetSemanticModel(docTree);
        return ResolveFromModel(model, docTree, caretOffset, currentDocumentPath, scriptSet);
    }

    /// <summary>Returns every .cs file currently indexed for script-editor definition lookup.</summary>
    public static IReadOnlyList<string> GetIndexedFilePaths()
        => EnumerateScriptFilePaths();

    public static IReadOnlyList<SymbolReferenceResult> FindReferences(string currentSource, string? currentDocumentPath, int caretOffset)
    {
        if (!TryBuildCompilation(currentSource, currentDocumentPath, out var comp, out var docTree))
            return Array.Empty<SymbolReferenceResult>();

        var docModel = comp.GetSemanticModel(docTree);
        var root = docTree.GetRoot();
        var tok = root.FindToken(caretOffset);
        ISymbol? symbol = null;
        for (var n = tok.Parent; n != null; n = n.Parent)
        {
            if (n is not SimpleNameSyntax sn) continue;
            var info = docModel.GetSymbolInfo(sn);
            symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol != null) break;
        }
        if (symbol == null) return Array.Empty<SymbolReferenceResult>();

        var refs = new List<SymbolReferenceResult>();
        foreach (var tree in comp.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith("ScriptPrelude.g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var model = comp.GetSemanticModel(tree);
            var tr = tree.GetRoot();
            foreach (var sn in tr.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(sn);
                var s = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (s == null) continue;
                if (!SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, symbol.OriginalDefinition) &&
                    !SymbolEqualityComparer.Default.Equals(s, symbol))
                    continue;
                var span = tree.GetLineSpan(sn.Span);
                var line = span.StartLinePosition.Line + 1;
                var col = span.StartLinePosition.Character + 1;
                var lineText = "";
                try { lineText = tr.GetText().Lines[line - 1].ToString().Trim(); } catch { }
                refs.Add(new SymbolReferenceResult
                {
                    FilePath = NormalizePath(tree.FilePath),
                    Line1Based = line,
                    Column1Based = col,
                    LineText = lineText
                });
            }
        }
        return refs
            .OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line1Based)
            .ThenBy(r => r.Column1Based)
            .ToList();
    }

    public static bool RenameSymbol(string currentSource, string? currentDocumentPath, int caretOffset, string newName, out int changedFiles)
    {
        changedFiles = 0;
        if (string.IsNullOrWhiteSpace(newName)) return false;
        if (!TryBuildCompilation(currentSource, currentDocumentPath, out var comp, out var docTree))
            return false;

        var docModel = comp.GetSemanticModel(docTree);
        var root = docTree.GetRoot();
        var tok = root.FindToken(caretOffset);
        ISymbol? symbol = null;
        for (var n = tok.Parent; n != null; n = n.Parent)
        {
            if (n is not SimpleNameSyntax sn) continue;
            var info = docModel.GetSymbolInfo(sn);
            symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol != null) break;
        }
        if (symbol == null) return false;

        var changesByPath = new Dictionary<string, List<TextChange>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in comp.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith("ScriptPrelude.g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var model = comp.GetSemanticModel(tree);
            foreach (var sn in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(sn);
                var s = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (s == null) continue;
                if (!SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, symbol.OriginalDefinition) &&
                    !SymbolEqualityComparer.Default.Equals(s, symbol))
                    continue;
                if (sn.Identifier.ValueText == newName) continue;
                var path = NormalizePath(tree.FilePath);
                if (!changesByPath.TryGetValue(path, out var list))
                {
                    list = new List<TextChange>();
                    changesByPath[path] = list;
                }
                list.Add(new TextChange(sn.Identifier.Span, newName));
            }
        }

        foreach (var kvp in changesByPath)
        {
            var path = kvp.Key;
            if (!File.Exists(path)) continue;
            var txt = SourceText.From(File.ReadAllText(path));
            var updated = txt.WithChanges(kvp.Value.OrderByDescending(c => c.Span.Start));
            if (updated.ToString() == txt.ToString()) continue;
            File.WriteAllText(path, updated.ToString());
            changedFiles++;
        }
        return changedFiles > 0;
    }

    static GoToDefinitionResult ResolveFromModel(
        SemanticModel model,
        SyntaxTree docTree,
        int caretOffset,
        string? currentDocumentPath,
        HashSet<string> scriptFileSet)
    {
        var root = docTree.GetRoot();
        var token = root.FindToken(caretOffset);
        if (token.IsKind(SyntaxKind.None)) return default;

        int depth = 0;
        for (var node = token.Parent; node != null && depth < 48; node = node.Parent, depth++)
        {
            if (node is not SimpleNameSyntax sn) continue;

            var info = model.GetSymbolInfo(sn);
            var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (sym == null) continue;

            if (!TryPickDefinitionLocation(sym, scriptFileSet, out var defPath, out var line1))
                continue;

            var curFull = string.IsNullOrEmpty(currentDocumentPath) ? null : NormalizePath(currentDocumentPath);
            var defFull = NormalizePath(defPath);
            bool same = curFull != null && string.Equals(curFull, defFull, StringComparison.OrdinalIgnoreCase);
            return new GoToDefinitionResult
            {
                Found = true,
                Line1Based = line1,
                TargetFilePath = same ? null : defPath,
                IsSameDocument = same
            };
        }

        return default;
    }

    static bool TryBuildCompilation(string currentSource, string? currentDocumentPath, out CSharpCompilation compilation, out SyntaxTree docTree)
    {
        compilation = null!;
        docTree = null!;
        if (string.IsNullOrEmpty(currentSource)) return false;
        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var refs = CollectMetadataReferences();
        var preludeTree = CSharpSyntaxTree.ParseText(Prelude, parseOpts, "ScriptPrelude.g.cs");
        var trees = new List<SyntaxTree> { preludeTree };
        var scriptPaths = EnumerateScriptFilePaths();
        var curNorm = string.IsNullOrEmpty(currentDocumentPath) ? null : NormalizePath(currentDocumentPath);

        SyntaxTree? doc = null;
        if (scriptPaths.Count == 0)
        {
            var path = currentDocumentPath ?? "OpenDocument.cs";
            doc = CSharpSyntaxTree.ParseText(currentSource, parseOpts, path);
            trees.Add(doc);
        }
        else
        {
            bool matched = false;
            foreach (var f in scriptPaths)
            {
                var full = NormalizePath(f);
                string txt;
                if (curNorm != null && string.Equals(full, curNorm, StringComparison.OrdinalIgnoreCase))
                {
                    txt = currentSource;
                    matched = true;
                }
                else
                {
                    try { txt = File.ReadAllText(f); } catch { continue; }
                }
                var t = CSharpSyntaxTree.ParseText(txt, parseOpts, f);
                trees.Add(t);
                if (curNorm != null && string.Equals(full, curNorm, StringComparison.OrdinalIgnoreCase))
                    doc = t;
            }
            if (!matched && curNorm != null)
            {
                doc = CSharpSyntaxTree.ParseText(currentSource, parseOpts, currentDocumentPath!);
                trees.Add(doc);
            }
            else if (doc == null)
            {
                doc = CSharpSyntaxTree.ParseText(currentSource, parseOpts, currentDocumentPath ?? "Untitled.cs");
                trees.Add(doc);
            }
        }

        compilation = CSharpCompilation.Create(
            "EditorScriptsSymbolOps",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true));
        docTree = doc!;
        return true;
    }

    static bool TryPickDefinitionLocation(ISymbol sym, HashSet<string> scriptFileSet, out string path, out int line1Based)
    {
        path = "";
        line1Based = 0;
        Location? bestScript = null;
        Location? any = null;

        foreach (var loc in sym.Locations)
        {
            if (!loc.IsInSource) continue;
            var fp = loc.SourceTree?.FilePath;
            if (string.IsNullOrEmpty(fp)) continue;
            try { fp = NormalizePath(fp); } catch { continue; }
            if (!File.Exists(fp)) continue;
            if (fp.EndsWith("ScriptPrelude.g.cs", StringComparison.OrdinalIgnoreCase)) continue;

            any ??= loc;
            if (scriptFileSet.Count == 0 || scriptFileSet.Contains(fp))
            {
                bestScript = loc;
                break;
            }
        }

        var pick = bestScript ?? any;
        if (pick == null) return false;
        path = NormalizePath(pick.SourceTree!.FilePath!);
        line1Based = pick.GetLineSpan().StartLinePosition.Line + 1;
        return true;
    }

    static string NormalizePath(string p) => Path.GetFullPath(p);

    static List<string> EnumerateScriptFilePaths()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in CandidateScriptRoots())
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
                {
                    var normalized = f.Replace('/', Path.DirectorySeparatorChar);
                    var d = Path.DirectorySeparatorChar;
                    if (normalized.IndexOf($"{d}obj{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (normalized.IndexOf($"{d}bin{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (normalized.IndexOf($"{d}.git{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var full = Path.GetFullPath(f);
                    if (seen.Add(full)) list.Add(full);
                }
            }
            catch { }
        }
        return list;
    }

    static IEnumerable<string> CandidateScriptRoots()
    {
        var p = ProjectService.Current;
        var seeds = new List<string>();
        if (p != null)
        {
            seeds.Add(p.AssetsPath);
            seeds.Add(p.PackagesPath);
        }
        seeds.AddRange(CandidateEditorSourceRoots());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in seeds)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var full = Path.GetFullPath(d);
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) yield return full;
        }
    }

    static IEnumerable<string> CandidateEditorSourceRoots()
    {
        // Development layout fallback: <repo>/Game-Engine/bin/<cfg>/<tfm> -> climb to project root.
        string? baseDir = null;
        try { baseDir = AppContext.BaseDirectory; } catch { }
        if (string.IsNullOrWhiteSpace(baseDir)) yield break;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var csproj = Path.Combine(dir.FullName, "Game_Engine.csproj");
            if (!File.Exists(csproj)) continue;
            var coreDir = Path.Combine(dir.FullName, "Core");
            var viewsDir = Path.Combine(dir.FullName, "Views");
            if (Directory.Exists(coreDir) && Directory.Exists(viewsDir))
                yield return dir.FullName;
            yield break;
        }
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
                if (string.IsNullOrWhiteSpace(loc) || !File.Exists(loc)) continue;
                list.Add(MetadataReference.CreateFromFile(loc));
            }
            catch { }
        }
        return list;
    }
}
