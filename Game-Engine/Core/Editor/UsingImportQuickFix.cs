using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Suggest <c>using</c> namespaces for unresolved type names (CS0246, etc.) using the same references as live diagnostics.
/// </summary>
public static class UsingImportQuickFix
{
    private static readonly Regex RxCs0246Name = new(
        @"type or namespace name '(?<n>[^']+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RxIdentifierHead = new(
        @"^(@)?(?<id>[_\p{L}][_\p{L}\p{N}]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsImportRelatedDiagnostic(in EditorDiagnostic d)
    {
        if (d.Severity != DiagSeverity.Error) return false;
        return d.Id switch
        {
            "CS0246" => true,
            "CS0305" => true,
            "CS0311" => true,
            _ => false,
        };
    }

    public static IReadOnlyList<string> SuggestNamespaces(string sourceText, in EditorDiagnostic diagnostic)
    {
        var typeName = ExtractTypeName(sourceText, diagnostic);
        if (string.IsNullOrEmpty(typeName)) return Array.Empty<string>();

        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(sourceText, parseOpts);
        if (tree.GetRoot() is not CompilationUnitSyntax cu) return Array.Empty<string>();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in cu.Usings)
        {
            if (u.StaticKeyword != default) continue;
            if (u.Alias != null) continue;
            var n = u.Name?.ToString().Trim();
            if (!string.IsNullOrEmpty(n)) existing.Add(n);
        }

        var refs = new List<MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.IsDynamic) continue;
                var loc = asm.Location;
                if (string.IsNullOrWhiteSpace(loc) || !System.IO.File.Exists(loc)) continue;
                refs.Add(MetadataReference.CreateFromFile(loc));
            }
            catch { }
        }

        var compilation = CSharpCompilation.Create(
            "ImportSuggest",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNamespacesForTypeName(compilation.GlobalNamespace, typeName, hits, maxTotal: 56);

        return hits
            .Where(ns => !existing.Contains(ns))
            .OrderBy(ns => ns.StartsWith("Game_Engine", StringComparison.OrdinalIgnoreCase) ? 0
                : ns.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ? 1
                : ns.StartsWith("System", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(ns => ns.Length)
            .ThenBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
    }

    static void CollectNamespacesForTypeName(INamespaceSymbol ns, string simpleName, HashSet<string> hits, int maxTotal, int depth = 0)
    {
        if (hits.Count >= maxTotal || depth > 280) return;
        foreach (var m in ns.GetMembers())
        {
            if (hits.Count >= maxTotal) return;
            if (m is INamespaceSymbol sub)
                CollectNamespacesForTypeName(sub, simpleName, hits, maxTotal, depth + 1);
            else if (m is INamedTypeSymbol t)
                ConsiderTypeAndNested(t, simpleName, hits, maxTotal);
        }
    }

    static void ConsiderTypeAndNested(INamedTypeSymbol t, string simpleName, HashSet<string> hits, int maxTotal)
    {
        if (hits.Count >= maxTotal) return;
        TryAddMatch(t, simpleName, hits, maxTotal);
        foreach (var nt in t.GetTypeMembers())
        {
            if (hits.Count >= maxTotal) return;
            ConsiderTypeAndNested(nt, simpleName, hits, maxTotal);
        }
    }

    static void TryAddMatch(INamedTypeSymbol t, string simpleName, HashSet<string> hits, int maxTotal)
    {
        if (hits.Count >= maxTotal) return;
        if (t.Name != simpleName) return;
        if (t.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) return;
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate))
            return;
        // "using N;" does not import nested types as unqualified names.
        if (t.ContainingType != null) return;

        var cns = t.ContainingNamespace;
        if (cns == null || cns.IsGlobalNamespace) return;
        var fq = FormatNamespace(cns);
        if (!string.IsNullOrEmpty(fq)) hits.Add(fq);
    }

    static string FormatNamespace(INamespaceSymbol ns)
    {
        var s = ns.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        if (s.StartsWith("global::", StringComparison.Ordinal))
            s = s["global::".Length..];
        return s;
    }

    public static string? ExtractTypeName(string sourceText, in EditorDiagnostic diagnostic)
    {
        int len = diagnostic.Length;
        if (len > 0 && diagnostic.StartOffset >= 0)
        {
            int start = diagnostic.StartOffset;
            if (start + len > sourceText.Length)
                len = Math.Max(0, sourceText.Length - start);
            if (len > 0)
            {
                var span = sourceText.Substring(start, len).Trim();
                var m = RxIdentifierHead.Match(span);
                if (m.Success) return m.Groups["id"].Value;
            }
        }
        return MessageTypeName(diagnostic.Message);
    }

    static string? MessageTypeName(string message)
    {
        var m = RxCs0246Name.Match(message);
        return m.Success ? m.Groups["n"].Value : null;
    }

    /// <summary>Insert <c>using N;</c> after existing usings or at the top of the file.</summary>
    public static bool TryBuildInsertion(string sourceText, string namespaceToAdd, out int offset, out string insertion)
    {
        offset = 0;
        insertion = "";
        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(sourceText, parseOpts);
        if (tree.GetRoot() is not CompilationUnitSyntax cu) return false;

        foreach (var u in cu.Usings)
        {
            if (u.StaticKeyword != default) continue;
            if (u.Alias != null) continue;
            if (string.Equals(u.Name?.ToString(), namespaceToAdd, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var line = "using " + namespaceToAdd + ";";
        if (cu.Usings.Count > 0)
        {
            var last = cu.Usings[^1];
            int end = last.Span.End;
            while (end < sourceText.Length && (sourceText[end] == '\r' || sourceText[end] == '\n'))
                end++;
            var sb = new StringBuilder();
            sb.Append(line);
            sb.Append('\n');
            insertion = sb.ToString();
            offset = end;
            return true;
        }

        insertion = line + "\n\n";
        offset = 0;
        return true;
    }
}
