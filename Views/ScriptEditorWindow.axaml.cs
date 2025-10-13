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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.ObjectModel;

namespace Game_Engine.Views;

public partial class ScriptEditorWindow : Window
{
    private string _path;
    private bool _dirty;
    private static AssemblyLoadContext? s_scriptsAlc; // hot-reloadable ALC
    private readonly ObservableCollection<TreeViewItem> _treeItems = new ObservableCollection<TreeViewItem>();



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

        //  file tree wiring 
        FileTree.ItemsSource = _treeItems;
        FileTree.DoubleTapped += OnTreeDoubleTapped; // open on double-click
        FileTree.SelectionChanged += OnTreeSelectionChanged; // just to show selection path in status (optional)

        // Build initial tree (uses project paths)
        RebuildScriptTree();

        // Keyboard shortcuts
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Wire Build button
        BtnBuild.Click += OnBuildAll;

        // First title
        UpdateTitle();

        Title = $"Script Editor v1.3 — {Path.GetFileName(_path)}";

        // Wire UI
        BtnSave.Click += OnSave;
        BtnSaveAs.Click += OnSaveAs;
        BtnReload.Click += OnReload;
        BtnClose.Click += (_, __) => Close();

        // Load file text
        TryLoad();
    }

    // Represents a folder or .cs file in the Scripts tree
    private sealed class NodeTag
    {
        public string FullPath;
        public bool IsFolder;
        public NodeTag(string fullPath, bool isFolder) { FullPath = fullPath; IsFolder = isFolder; }
    }

    private void OnProjectChanged()
    {
        // Avoid blocking UI thread while scanning disk; small projects are fine sync.
        Dispatcher.UIThread.Post(RebuildScriptTree);
    }

    private void RebuildScriptTree()
    {
        _treeItems.Clear();

        // Collect candidate "Scripts" folders
        var scriptRoots = CandidateScriptFolders().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // If none exist yet, ensure Assets/Scripts so there is at least one root
        var p = ProjectService.Current;
        if (scriptRoots.Count == 0 && p != null)
        {
            var fallback = Path.Combine(p.AssetsPath, "Scripts");
            try { Directory.CreateDirectory(fallback); } catch { }
            if (Directory.Exists(fallback)) scriptRoots.Add(fallback);
        }

        foreach (var scriptsDir in scriptRoots)
        {
            var rootItem = BuildTreeItemForFolder(scriptsDir, displayName: MakeNiceRootName(scriptsDir));
            if (rootItem != null) _treeItems.Add(rootItem);
        }

        // Expand first root by default
        if (_treeItems.Count > 0) _treeItems[0].IsExpanded = true;
    }

    private static string MakeNiceRootName(string scriptsDir)
    {
        var proj = ProjectService.Current;
        if (proj == null) return Path.GetFileName(scriptsDir);
        // Show relative path under project root for clarity
        var rel = Path.GetRelativePath(proj.RootPath, scriptsDir);
        return string.IsNullOrWhiteSpace(rel) ? Path.GetFileName(scriptsDir) : rel.Replace('\\', '/');
    }

    private TreeViewItem BuildTreeItemForFolder(string dir, string displayName = null)
    {
        if (!Directory.Exists(dir)) return null;

        var header = string.IsNullOrEmpty(displayName) ? Path.GetFileName(dir) : displayName;
        var item = new TreeViewItem
        {
            Header = string.IsNullOrEmpty(header) ? dir : header,
            Tag = new NodeTag(dir, isFolder: true)
        };

        // Folders first (alpha)
        IEnumerable<string> subDirs = Enumerable.Empty<string>();
        try { subDirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase); } catch { }

        foreach (var sd in subDirs)
        {
            // Skip typical build/system folders just in case
            var name = Path.GetFileName(sd);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;

            var child = BuildTreeItemForFolder(sd);
            if (child != null) item.Items.Add(child);
        }

        // Then .cs files (alpha)
        IEnumerable<string> files = Enumerable.Empty<string>();
        try { files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase); } catch { }

        foreach (var f in files)
        {
            item.Items.Add(new TreeViewItem
            {
                Header = Path.GetFileName(f),
                Tag = new NodeTag(f, isFolder: false)
            });
        }

        return item;
    }

    private void OnTreeDoubleTapped(object sender, RoutedEventArgs e)
    {
        var tvi = FileTree.SelectedItem as TreeViewItem;
        var tag = tvi?.Tag as NodeTag;
        if (tag == null || tag.IsFolder) return;

        TryOpenScriptPath(tag.FullPath);
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tvi = FileTree.SelectedItem as TreeViewItem;
        var tag = tvi?.Tag as NodeTag;
        if (tag != null) StatusText(tag.FullPath);
    }

    private void TryOpenScriptPath(string fullPath)
    {
        try
        {
            // Save current file first if edited
            SaveIfDirty();

            if (!File.Exists(fullPath)) { ShowError("File not found:\n" + fullPath); return; }
            _path = fullPath;
            TryLoad();
            _dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            ShowError("Failed to open:\n" + ex.Message);
        }
    }




    private void UpdateTitle()
    {
        Title = $"Script Editor v1.3 — {(_dirty ? "*" : "")}{Path.GetFileName(_path)}";
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

    // Auto-save current buffer if there are unsaved edits.
    private void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            File.WriteAllText(_path, Editor.Text ?? "");
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            // Don’t block the switch; just report the issue.
            ShowError("Auto-save failed:\n" + ex.Message);
        }
        finally
        {
            _dirty = false;
            UpdateTitle();
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

            // Make editor APIs visible to all scripts without per-file usings
            const string Prelude = @"
                global using Avalonia.Controls;        // Control
                global using Game_Engine.Views;        // ICustomInspector, InspectorContext, CustomInspectorAttribute
            ";
            trees.Insert(0, CSharpSyntaxTree.ParseText(Prelude, parseOpts, "ScriptPrelude.g.cs"));

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

            // Helper: try delete; if locked, quarantine by renaming to .old
            void TryDeleteOrQuarantine(string file)
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                try { File.Delete(file); return; } catch { /* locked */ }
                try
                {
                    var old = file + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                catch { /* give up quietly */ }
            }

            // Unload any previous hot assembly BEFORE touching files
            try { s_scriptsAlc?.Unload(); } catch { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            // Decide output dir
            var proj = ProjectService.Current;
            var outRoot = proj?.BuildsPath;
            if (string.IsNullOrWhiteSpace(outRoot))
                outRoot = proj?.RootPath ?? Path.GetTempPath();

            var outDir = Path.Combine(outRoot!, "EditorScripts");
            Directory.CreateDirectory(outDir);

            // Clean old builds (dll + pdb)
            try
            {
                foreach (var old in Directory.GetFiles(outDir, "EditorScripts_*.dll"))
                    TryDeleteOrQuarantine(old);
                foreach (var old in Directory.GetFiles(outDir, "EditorScripts_*.pdb"))
                    TryDeleteOrQuarantine(old);
            }
            catch { /* non-fatal */ }

            // Save the new DLL to disk (optional; we still load from memory)
            try
            {
                var dllPath = Path.Combine(outDir, asmName + ".dll");
                File.WriteAllBytes(dllPath, ms.ToArray());
                StatusText($"Build OK — {asmName}.dll saved to {dllPath}");
            }
            catch
            {
                // Writing the file is optional — don’t fail the build.
            }

            // Load the new assembly from memory into a fresh collectible ALC (no file lock)
            ms.Position = 0;
            var alc = new AssemblyLoadContext(asmName, isCollectible: true);
            var asm = alc.LoadFromStream(ms);
            s_scriptsAlc = alc;

            // Count Behaviour types for a friendly status
            var behaviorType = typeof(Game_Engine.Core.Behavior);
            int loaded = 0;
            try
            {
                loaded = asm.GetTypes().Count(t => t != null && !t.IsAbstract && behaviorType.IsAssignableFrom(t));
            }
            catch { /* ignore type load issues */ }

            return (allFiles.Count, loaded);
        });
    }


    // Where to search for scripts 
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

    // Find "Scripts" folders inside project Assets and Packages
    private static List<string> CandidateScriptFolders()
    {
        var list = new List<string>();
        var p = ProjectService.Current;
        if (p == null) return list;

        // Assets/Scripts
        var a = Path.Combine(p.AssetsPath, "Scripts");
        if (Directory.Exists(a)) list.Add(Path.GetFullPath(a));

        // Top-level Packages/*/Scripts
        IEnumerable<string> pkgs;
        try
        {
            pkgs = Directory.Exists(p.PackagesPath)
                ? Directory.EnumerateDirectories(p.PackagesPath)
                : Array.Empty<string>();
        }
        catch
        {
            pkgs = Array.Empty<string>();
        }

        foreach (var pkg in pkgs)
        {
            var s = Path.Combine(pkg, "Scripts");
            if (Directory.Exists(s)) list.Add(Path.GetFullPath(s));
        }

        return list;
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

    /// <summary>Convenience opener used by ProjectPanel and Inspector.</summary>
    public static async void Open(Window? owner, string path)
    {
        var w = new ScriptEditorWindow(path) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (owner is null) w.Show();
        else await w.ShowDialog(owner);
    }
}
