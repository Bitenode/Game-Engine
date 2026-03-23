using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Game_Engine.Core.Editor;

/// <summary>
/// A single diagnostic entry (error or warning) with location info.
/// </summary>
public readonly struct EditorDiagnostic
{
    public int StartOffset { get; init; }
    public int Length { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string Message { get; init; }
    public DiagSeverity Severity { get; init; }
    /// <summary>Roslyn id e.g. CS0246 (empty when unknown).</summary>
    public string Id { get; init; }
}

public enum DiagSeverity { Error, Warning, Info }

/// <summary>
/// Runs background Roslyn compilation on a debounced timer and emits diagnostics.
/// </summary>
public sealed class DiagnosticService : IDisposable
{
    private CancellationTokenSource? _cts;
    private Timer? _debounce;
    private string _pendingSource = "";
    private const int DebounceMs = 500;

    /// <summary>Raised (on background thread) when diagnostics are ready.</summary>
    public event Action<IReadOnlyList<EditorDiagnostic>>? DiagnosticsReady;

    /// <summary>Queue a source update. Compilation will run after a debounce period.</summary>
    public void UpdateSource(string sourceText)
    {
        _pendingSource = sourceText;
        _debounce?.Dispose();
        _debounce = new Timer(_ => RunDiagnostics(), null, DebounceMs, Timeout.Infinite);
    }

    private void RunDiagnostics()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var source = _pendingSource;

        Task.Run(() =>
        {
            try
            {
                var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
                var tree = CSharpSyntaxTree.ParseText(source, parseOpts);

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
                    "DiagCheck",
                    new[] { tree },
                    refs,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                ct.ThrowIfCancellationRequested();

                var diags = compilation.GetDiagnostics(ct);
                var results = new List<EditorDiagnostic>();

                var text = tree.GetText();

                foreach (var d in diags)
                {
                    if (d.Severity == DiagnosticSeverity.Hidden) continue;
                    if (d.Location == Location.None || !d.Location.IsInSource) continue;

                    var span = d.Location.GetLineSpan();

                    results.Add(new EditorDiagnostic
                    {
                        StartOffset = d.Location.SourceSpan.Start,
                        Length = d.Location.SourceSpan.Length,
                        Line = span.StartLinePosition.Line,
                        Column = span.StartLinePosition.Character,
                        Message = d.GetMessage(),
                        Id = d.Id ?? "",
                        Severity = d.Severity switch
                        {
                            DiagnosticSeverity.Error => DiagSeverity.Error,
                            DiagnosticSeverity.Warning => DiagSeverity.Warning,
                            _ => DiagSeverity.Info,
                        }
                    });
                }

                if (!ct.IsCancellationRequested)
                    DiagnosticsReady?.Invoke(results);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _debounce?.Dispose();
    }
}
