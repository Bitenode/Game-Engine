using System;
using System.IO;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Game_Engine.Core;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Game_Engine.Views;

public partial class ScriptEditorWindow : Window
{
    private string _path;
    private bool _dirty;
    private static AssemblyLoadContext? s_scriptsAlc; // hot-reloadable ALC


    public ScriptEditorWindow(string path)
    {
        _path = path;
        InitializeComponent();

        // Track edits -> title shows "*"
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                _dirty = true;
                UpdateTitle();
            }
        };


        // Keyboard shortcuts
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Wire Build button
        BtnBuild.Click += OnBuildAll;

        // First title
        UpdateTitle();

        Title = $"Script Editor v1.1 — {Path.GetFileName(_path)}";

        // Wire UI
        BtnSave.Click += OnSave;
        BtnSaveAs.Click += OnSaveAs;
        BtnReload.Click += OnReload;
        BtnClose.Click += (_, __) => Close();

        // Load file text
        TryLoad();
    }

    private void UpdateTitle()
    {
        Title = $"Script Editor v1.1 — {(_dirty ? "*" : "")}{Path.GetFileName(_path)}";
        if (Status != null && string.IsNullOrWhiteSpace(Status.Text))
            Status.Text = "";
    }

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            OnSave(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.B)
        {
            OnBuildAll(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }


    private void TryLoad()
    {
        try { Editor.Text = File.Exists(_path) ? File.ReadAllText(_path) : ""; }
        catch { Editor.Text = ""; }
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_path, Editor.Text ?? "");
            ProjectService.TouchModified();
            _dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private async void OnSaveAs(object? s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Script As…",
            InitialFileName = Path.GetFileName(_path),
            Filters = { new FileDialogFilter { Name = "C# File", Extensions = { "cs" } } }
        };
        var dst = await dlg.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(dst)) return;

        try
        {
            File.WriteAllText(dst, Editor.Text ?? "");
            _path = dst;
            Title = $"Script Editor — {Path.GetFileName(_path)}";
            ProjectService.TouchModified();
            _dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private void OnReload(object? s, RoutedEventArgs e) { 

        TryLoad();
        _dirty = false;
        UpdateTitle();
    }

    private async void OnBuildAll(object? s, RoutedEventArgs e)
    {
        // Save current file first so the build picks it up
        OnSave(this, new RoutedEventArgs());

        try
        {
            var (files, typesLoaded) = await BuildAndLoadProjectScriptsAsync();
            ProjectService.TouchModified(); // nudge UI to refresh caches

            var msg = $"Build OK — {typesLoaded} Behavior types loaded from {files} script file(s).";
            StatusText(msg);
            Game_Engine.Core.Log.Info(msg);
        }
        catch (Exception ex)
        {
            StatusText("Build failed. See details.");
            ShowError("Build failed:\n\n" + ex.Message);
        }
    }

    private void StatusText(string text)
    {
        if (Status == null) return;
        Dispatcher.UIThread.Post(() => Status.Text = text);
    }

    // Compile every .cs under project roots and hot-load the assembly
    private Task<(int files, int typesLoaded)> BuildAndLoadProjectScriptsAsync()
    {
        return Task.Run(() =>
        {
            // Collect project script files (de-duped; skip bin/obj/.git)
            var roots = CandidateScriptRoots().ToList();
            var allFiles = new List<string>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in roots)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
                    {
                        var s = f.Replace('/', Path.DirectorySeparatorChar);
                        var d = Path.DirectorySeparatorChar;
                        if (s.IndexOf($"{d}obj{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (s.IndexOf($"{d}bin{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (s.IndexOf($"{d}.git{d}", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        var full = Path.GetFullPath(f);
                        if (seenFiles.Add(full))
                            allFiles.Add(full);
                    }
                }
                catch { /* ignore bad dirs */ }
            }

            if (allFiles.Count == 0)
                throw new InvalidOperationException("No .cs files found under your Assets/Packages folders.");

            // Roslyn compile to an in-memory DLL
            var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var trees = allFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOpts, f)).ToList();

            var refs = CollectMetadataReferences();
            var compOpts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithAllowUnsafe(true);

            var asmName = "EditorScripts_" + Guid.NewGuid().ToString("N");
            var compilation = CSharpCompilation.Create(asmName, trees, refs, compOpts);

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            if (!result.Success)
            {
                var errors = string.Join("\n", result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                throw new Exception(errors);
            }

            // Persist the DLL to disk then hot-load from memory
            try
            {
                var proj = ProjectService.Current;
                var outRoot = proj?.BuildsPath;
                if (string.IsNullOrWhiteSpace(outRoot))
                    outRoot = proj?.RootPath ?? Path.GetTempPath();

                var outDir = Path.Combine(outRoot!, "EditorScripts");
                Directory.CreateDirectory(outDir);

                // keep the folder tidy
                foreach (var old in Directory.GetFiles(outDir, "EditorScripts_*.dll"))
                    try { File.Delete(old); } catch { /* ignore locks */ }

                var dllPath = Path.Combine(outDir, asmName + ".dll");
                File.WriteAllBytes(dllPath, ms.ToArray());

                StatusText($"Build OK — {asmName}.dll saved to {dllPath}");
            }
            catch
            {
                // Writing the file is optional don’t fail the build if it can’t be written.
            }

            // Unload any previous hot assembly, then load this one from memory
            try { s_scriptsAlc?.Unload(); } catch { }
            GC.Collect(); GC.WaitForPendingFinalizers();

            ms.Position = 0;
            var alc = new AssemblyLoadContext(asmName, isCollectible: true);
            var asm = alc.LoadFromStream(ms);
            s_scriptsAlc = alc;

            // Count Behaviour types for a friendly status
            var behaviorType = typeof(Game_Engine.Core.Behavior);
            int loaded = 0;
            try
            {
                loaded = asm.GetTypes().Count(t =>
                    t != null && !t.IsAbstract && behaviorType.IsAssignableFrom(t));
            }
            catch { /* ignore type load issues */ }

            return (allFiles.Count, loaded);
        });
    }


    // Where to search for scripts (same places the Inspector scans)
    private static IEnumerable<string> CandidateScriptRoots()
    {
        var p = ProjectService.Current;
        if (p == null) yield break;

        // Only places users put scripts; don't include Root/Scenes/Builds to avoid dupes
        var seeds = new[] { p.AssetsPath, p.PackagesPath };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in seeds)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var full = Path.GetFullPath(d);
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) yield return full;
        }
    }


    // Grab all currently-loaded assembly locations as Roslyn references
    private static IEnumerable<MetadataReference> CollectMetadataReferences()
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
            catch
            {
                // ignore assemblies that can't be resolved
            }
        }

        return list;
    }


    private async void ShowError(string message)
    {
        var win = new Window
        {
            Title = "Error",
            Width = 420,
            Height = 180,
            Content = new TextBlock
            {
                Text = message,
                Margin = new Thickness(16),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        await win.ShowDialog(this);
    }

    /// <summary>Convenience opener used by ProjectPanel.</summary>
    public static async void Open(Window? owner, string path)
    {
        var w = new ScriptEditorWindow(path) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (owner is null) w.Show();
        else await w.ShowDialog(owner);
    }
}
