using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Game_Engine.Core.Editor;

/// <summary>
/// A foldable region in the source text (start line through end line inclusive).
/// </summary>
public readonly struct FoldRegion
{
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Placeholder { get; init; }

    public int LineSpan => EndLine - StartLine;
}

/// <summary>
/// Uses Roslyn to detect foldable regions: method bodies, class/struct/interface
/// bodies, #region blocks, and multi-line comments.
/// </summary>
public static class FoldingProvider
{
    public static List<FoldRegion> GetFoldRegions(string sourceText)
    {
        var regions = new List<FoldRegion>();

        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var lineSpans = tree.GetText().Lines;

            void AddBlock(SyntaxNode? openBrace, SyntaxNode? closeBrace, string placeholder)
            {
                if (openBrace == null || closeBrace == null) return;
                int startLine = lineSpans.GetLinePosition(openBrace.SpanStart).Line;
                int endLine = lineSpans.GetLinePosition(closeBrace.Span.End).Line;
                if (endLine > startLine)
                    regions.Add(new FoldRegion
                    {
                        StartLine = startLine,
                        EndLine = endLine,
                        Placeholder = placeholder
                    });
            }

            void AddBraceBlock(SyntaxToken open, SyntaxToken close, string placeholder)
            {
                if (open.IsMissing || close.IsMissing) return;
                int startLine = lineSpans.GetLinePosition(open.SpanStart).Line;
                int endLine = lineSpans.GetLinePosition(close.Span.End).Line;
                if (endLine > startLine)
                    regions.Add(new FoldRegion
                    {
                        StartLine = startLine,
                        EndLine = endLine,
                        Placeholder = placeholder
                    });
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case NamespaceDeclarationSyntax ns:
                        AddBraceBlock(ns.OpenBraceToken, ns.CloseBraceToken, "{ ... }");
                        break;
                    case ClassDeclarationSyntax cls:
                        AddBraceBlock(cls.OpenBraceToken, cls.CloseBraceToken, "{ ... }");
                        break;
                    case StructDeclarationSyntax st:
                        AddBraceBlock(st.OpenBraceToken, st.CloseBraceToken, "{ ... }");
                        break;
                    case InterfaceDeclarationSyntax iface:
                        AddBraceBlock(iface.OpenBraceToken, iface.CloseBraceToken, "{ ... }");
                        break;
                    case EnumDeclarationSyntax en:
                        AddBraceBlock(en.OpenBraceToken, en.CloseBraceToken, "{ ... }");
                        break;
                    case RecordDeclarationSyntax rec:
                        AddBraceBlock(rec.OpenBraceToken, rec.CloseBraceToken, "{ ... }");
                        break;
                    case MethodDeclarationSyntax m when m.Body != null:
                        AddBlock(m.Body, m.Body, "{ ... }");
                        break;
                    case ConstructorDeclarationSyntax ctor when ctor.Body != null:
                        AddBlock(ctor.Body, ctor.Body, "{ ... }");
                        break;
                    case PropertyDeclarationSyntax prop when prop.AccessorList != null:
                        var acc = prop.AccessorList;
                        AddBraceBlock(acc.OpenBraceToken, acc.CloseBraceToken, "{ ... }");
                        break;
                    case SwitchStatementSyntax sw:
                        AddBraceBlock(sw.OpenBraceToken, sw.CloseBraceToken, "{ ... }");
                        break;
                }
            }

            // #region / #endregion
            var regionStack = new Stack<int>();
            foreach (var trivia in root.DescendantTrivia())
            {
                if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
                {
                    regionStack.Push(lineSpans.GetLinePosition(trivia.SpanStart).Line);
                }
                else if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia) && regionStack.Count > 0)
                {
                    int startLine = regionStack.Pop();
                    int endLine = lineSpans.GetLinePosition(trivia.SpanStart).Line;
                    if (endLine > startLine)
                        regions.Add(new FoldRegion
                        {
                            StartLine = startLine,
                            EndLine = endLine,
                            Placeholder = "#region ..."
                        });
                }
            }

            // Multi-line comments (/* ... */) and XML doc comments (/// ...)
            foreach (var trivia in root.DescendantTrivia())
            {
                if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    int startLine = lineSpans.GetLinePosition(trivia.SpanStart).Line;
                    int endLine = lineSpans.GetLinePosition(trivia.Span.End).Line;
                    if (endLine > startLine)
                        regions.Add(new FoldRegion
                        {
                            StartLine = startLine,
                            EndLine = endLine,
                            Placeholder = "/* ... */"
                        });
                }
            }
        }
        catch
        {
            // Parsing failed; return empty
        }

        regions.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return regions;
    }
}
