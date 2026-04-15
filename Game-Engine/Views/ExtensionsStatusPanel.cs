using System;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Game_Engine.Core.Extensibility;

namespace Game_Engine.Views;

/// <summary>Built-in panel: last extension load snapshot, compile/reload summary, and manifest trust hints.</summary>
public sealed class ExtensionsStatusPanel : UserControl
{
    readonly TextBlock _compileBlock = new() { Margin = new Avalonia.Thickness(12, 12, 12, 8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly TextBlock _body = new() { Margin = new Avalonia.Thickness(12, 0, 12, 12), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace") };
    readonly ScrollViewer _scroll;

    public ExtensionsStatusPanel()
    {
        var refresh = new Button { Content = "Refresh", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(12, 8, 12, 0) };
        refresh.Click += (_, __) => Refresh();

        _scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var root = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Add-ons / extensions",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(12, 12, 12, 0)
                },
                _compileBlock,
                refresh,
                _scroll
            }
        };
        Content = root;

        ExtensionService.Changed += OnExtensionsChanged;
        Loaded += (_, __) => Refresh();
        DetachedFromVisualTree += (_, __) => ExtensionService.Changed -= OnExtensionsChanged;
    }

    void OnExtensionsChanged() => Dispatcher.UIThread.Post(Refresh);

    void Refresh()
    {
        var sb = new StringBuilder();

        var compileOk = ExtensionDiagnostics.LastCompileReloadSucceeded;
        var compileMsg = ExtensionDiagnostics.LastCompileReloadMessage;
        if (!string.IsNullOrEmpty(compileMsg))
        {
            _compileBlock.Text = (compileOk ? "Last compile/reload: OK — " : "Last compile/reload: FAILED — ") + compileMsg;
            _compileBlock.Foreground = compileOk
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6A9955"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F48771"));
        }
        else
        {
            _compileBlock.Text = "Last compile/reload: (none this session)";
            _compileBlock.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCCC"));
        }

        var snap = ExtensionDiagnostics.Last;
        if (snap is null)
        {
            _body.Text = "No extension load snapshot yet. Open a project or run Scripts: Compile and Reload Extensions.";
            return;
        }

        sb.AppendLine($"Load source: {snap.LoadSource}");
        if (!string.IsNullOrEmpty(snap.EditorScriptsDir))
            sb.AppendLine($"EditorScripts: {snap.EditorScriptsDir}");
        if (!string.IsNullOrEmpty(snap.ManifestPath))
            sb.AppendLine($"Manifest path: {snap.ManifestPath}");

        if (snap.Manifest != null)
        {
            var m = snap.Manifest;
            if (!string.IsNullOrWhiteSpace(m.DisplayName)) sb.AppendLine($"Manifest name: {m.DisplayName}");
            if (!string.IsNullOrWhiteSpace(m.Version)) sb.AppendLine($"Manifest version: {m.Version}");
            if (!string.IsNullOrWhiteSpace(m.Author)) sb.AppendLine($"Author: {m.Author}");
            if (m.TrustedAssemblies is { Count: > 0 } ta)
            {
                sb.AppendLine();
                sb.AppendLine("Trusted assemblies (SHA-256 enforced at load):");
                foreach (var t in ta)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.File)) continue;
                    var name = System.IO.Path.GetFileName(t.File.Trim());
                    var inSet = false;
                    foreach (var p in snap.LoadedDllPaths)
                    {
                        if (string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase))
                        {
                            inSet = true;
                            break;
                        }
                    }

                    var hash = string.IsNullOrWhiteSpace(t.Sha256) ? "(no hash)" : "sha256=" + t.Sha256.Trim();
                    sb.AppendLine($"  • {name}: {hash}" + (inSet ? " — loaded, hash matched" : " — (not in resolved load set or skipped)"));
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Active EditorExtension type(s): {snap.ExtensionTypeNames.Count}");
        foreach (var n in snap.ExtensionTypeNames)
            sb.AppendLine("  • " + n);

        sb.AppendLine();
        sb.AppendLine("Loaded DLLs:");
        if (snap.LoadedDllPaths.Count == 0)
            sb.AppendLine("  (none)");
        else
        {
            foreach (var p in snap.LoadedDllPaths)
                sb.AppendLine("  • " + p);
        }

        if (snap.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Errors:");
            foreach (var e in snap.Errors)
                sb.AppendLine("  • " + e);
        }

        if (snap.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var w in snap.Warnings)
                sb.AppendLine("  • " + w);
        }

        if (snap.CommandIdCollisions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Command id collisions (last refresh):");
            foreach (var c in snap.CommandIdCollisions)
                sb.AppendLine("  • " + c);
        }

        _body.Text = sb.ToString();
    }
}
