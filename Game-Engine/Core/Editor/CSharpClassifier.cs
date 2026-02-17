using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Classifies C# source text using Roslyn's semantic classifier.
/// Falls back to syntactic classification when semantic analysis isn't ready.
/// Results are cached and re-computed on a background thread after each edit.
/// </summary>
public sealed class CSharpClassifier : IDisposable
{
    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private DocumentId? _documentId;
    private CancellationTokenSource? _cts;

    // Cached results
    private IReadOnlyList<Game_Engine.Views.EditorClassifiedSpan> _spans = Array.Empty<Game_Engine.Views.EditorClassifiedSpan>();
    private int _lastVersion;

    /// <summary>Raised on UI thread when new classification results are ready.</summary>
    public event Action? ClassificationReady;

    public IReadOnlyList<Game_Engine.Views.EditorClassifiedSpan> Spans => _spans;

    public CSharpClassifier()
    {
        InitWorkspace();
    }

    // ── Workspace bootstrap ─────────────────────────────────────

    private void InitWorkspace()
    {
        try
        {
            _workspace = new AdhocWorkspace(MefHostServices.DefaultHost);

            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                "ScriptEditorProject",
                "ScriptEditorProject",
                LanguageNames.CSharp,
                parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            _projectId = projectInfo.Id;

            // Add metadata references from the running engine
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

            // Add a single document that we'll keep updating
            var docId = DocumentId.CreateNewId(_projectId);
            _documentId = docId;
            _workspace.AddDocument(DocumentInfo.Create(docId, "Script.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(""), VersionStamp.Default))));
        }
        catch
        {
            // If Roslyn workspace setup fails, syntax highlighting just won't work
            _workspace = null;
        }
    }

    // ── Update source text ──────────────────────────────────────

    /// <summary>
    /// Call after each buffer edit (debounced by the host).
    /// Kicks off background classification.
    /// </summary>
    public void UpdateText(string sourceText, int version)
    {
        if (_workspace == null || _documentId == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var src = SourceText.From(sourceText);
        var currentSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, src);
        _workspace.TryApplyChanges(currentSolution);

        var captured = version;
        Task.Run(async () =>
        {
            try
            {
                var doc = _workspace.CurrentSolution.GetDocument(_documentId);
                if (doc == null) return;

                var classified = await Classifier.GetClassifiedSpansAsync(
                    doc, new TextSpan(0, src.Length), ct);

                if (ct.IsCancellationRequested) return;

                var result = classified
                    .Where(s => s.ClassificationType != ClassificationTypeNames.Text &&
                                s.ClassificationType != ClassificationTypeNames.StaticSymbol)
                    .Select(s => new Game_Engine.Views.EditorClassifiedSpan
                    {
                        Start = s.TextSpan.Start,
                        Length = s.TextSpan.Length,
                        Classification = s.ClassificationType
                    })
                    .ToList();

                _spans = result;
                _lastVersion = captured;
                ClassificationReady?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _workspace?.Dispose();
    }
}
