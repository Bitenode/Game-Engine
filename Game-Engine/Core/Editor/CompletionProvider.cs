using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Game_Engine.Core.Editor;

/// <summary>
/// A single completion suggestion.
/// </summary>
public readonly struct CompletionEntry
{
    public string DisplayText { get; init; }
    public string InsertText { get; init; }
    public string Kind { get; init; }        // Method, Property, Class, etc.
    public string? Description { get; init; }
}

/// <summary>
/// Uses Roslyn's CompletionService to provide IntelliSense suggestions.
/// Shares the workspace with CSharpClassifier for consistency.
/// </summary>
public sealed class CompletionProvider : IDisposable
{
    private AdhocWorkspace? _workspace;
    private DocumentId? _documentId;
    private CancellationTokenSource? _cts;

    public CompletionProvider()
    {
        InitWorkspace();
    }

    private void InitWorkspace()
    {
        try
        {
            _workspace = new AdhocWorkspace(MefHostServices.DefaultHost);

            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default,
                "CompletionProject", "CompletionProject", LanguageNames.CSharp,
                parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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

            projectInfo = projectInfo.WithMetadataReferences(refs);
            _workspace.AddProject(projectInfo);

            _documentId = DocumentId.CreateNewId(projectInfo.Id);
            _workspace.AddDocument(DocumentInfo.Create(_documentId, "Script.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(""), VersionStamp.Default))));
        }
        catch
        {
            _workspace = null;
        }
    }

    /// <summary>
    /// Get completion items at the given position in the source text.
    /// </summary>
    public async Task<List<CompletionEntry>> GetCompletionsAsync(string sourceText, int position)
    {
        var result = new List<CompletionEntry>();
        if (_workspace == null || _documentId == null) return result;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var src = SourceText.From(sourceText);
            var solution = _workspace.CurrentSolution.WithDocumentText(_documentId, src);
            _workspace.TryApplyChanges(solution);

            var doc = _workspace.CurrentSolution.GetDocument(_documentId);
            if (doc == null) return result;

            var completionService = CompletionService.GetService(doc);
            if (completionService == null) return result;

            var completions = await completionService.GetCompletionsAsync(doc, position, cancellationToken: ct);
            if (completions == null) return result;

            foreach (var item in completions.ItemsList.Take(200))
            {
                ct.ThrowIfCancellationRequested();

                string kind = "";
                foreach (var tag in item.Tags)
                {
                    if (!string.IsNullOrEmpty(tag))
                    {
                        kind = tag;
                        break;
                    }
                }

                result.Add(new CompletionEntry
                {
                    DisplayText = item.DisplayText,
                    InsertText = item.DisplayText,
                    Kind = kind,
                    Description = item.InlineDescription,
                });
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        return result;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _workspace?.Dispose();
    }
}
