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
using Game_Engine.Core.Extensibility;
using Avalonia.Controls.ApplicationLifetimes;

namespace Game_Engine.Views;

public partial class ScriptEditorWindow : Window
{
    private string _path;
    private bool _dirty;
    private EditorTab? _displayedTab;   // tracks which tab the editor is currently showing
    private static AssemblyLoadContext? s_scriptsAlc;
    private readonly ObservableCollection<TreeViewItem> _treeItems = new();

    private const string EditorVersion = "v2.5";

    // ── Constructor ─────────────────────────────────────────────

    public ScriptEditorWindow(string path)
    {
        _path = path;
        InitializeComponent();

        // Track edits via the custom code-editor control
        CodeEditor.TextModified += () =>
        {
            _dirty = true;
            if (TabBar.ActiveTab != null) TabBar.ActiveTab.IsDirty = true;
            TabBar.RefreshTabDirtyState();
            UpdateTitle();
        };

        CodeEditor.CaretMoved += () =>
        {
            var (ln, col) = CodeEditor.GetCaretLineColumn();
            Dispatcher.UIThread.Post(() =>
            {
                if (StatusPos != null)
                    StatusPos.Text = $"Ln {ln + 1}, Col {col + 1}";
                if (StatusLines != null)
                    StatusLines.Text = $"{CodeEditor.Buffer.LineCount} lines";
            });
        };

        // Tab bar wiring
        TabBar.TabSelected += OnTabSelected;
        TabBar.TabCloseRequested += OnTabCloseRequested;

        // File tree wiring
        FileTree.ItemsSource = _treeItems;
        FileTree.DoubleTapped += OnTreeDoubleTapped;
        FileTree.SelectionChanged += OnTreeSelectionChanged;

        RebuildScriptTree();

        // Global keyboard shortcuts (Save, Build, Tab cycling)
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Wire toolbar buttons
        BtnBuild.Click += OnBuildAll;
        BtnSave.Click += OnSave;
        BtnSaveAs.Click += OnSaveAs;
        BtnReload.Click += OnReload;
        BtnClose.Click += (_, __) => Close();

        // Open initial file as a tab
        OpenFileInTab(path);
    }

    // ── NodeTag (tree item metadata) ────────────────────────────

    private sealed class NodeTag
    {
        public string FullPath;
        public bool IsFolder;
        public NodeTag(string fullPath, bool isFolder) { FullPath = fullPath; IsFolder = isFolder; }
    }

    // ── Project changes ─────────────────────────────────────────

    private void OnProjectChanged()
    {
        Dispatcher.UIThread.Post(RebuildScriptTree);
    }

    // ── Window-level key shortcuts ──────────────────────────────

    private void OnWindowKeyDown(object? s, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            OnSave(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                 e.Key == Key.B)
        {
            OnBuildAll(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Tab)
        {
            // Ctrl+Tab / Ctrl+Shift+Tab: cycle through tabs
            var tabs = TabBar.Tabs;
            if (tabs.Count > 1)
            {
                int idx = -1;
                for (int i = 0; i < tabs.Count; i++) { if (tabs[i] == TabBar.ActiveTab) { idx = i; break; } }
                bool reverse = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                int next = reverse ? (idx - 1 + tabs.Count) % tabs.Count
                                   : (idx + 1) % tabs.Count;
                TabBar.SetActiveTab(tabs[next]);
            }
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.W)
        {
            // Ctrl+W: close current tab
            if (TabBar.ActiveTab != null)
                OnTabCloseRequested(TabBar.ActiveTab);
            e.Handled = true;
        }
    }

    // ── Tab management ──────────────────────────────────────────

    private void OpenFileInTab(string filePath)
    {
        // Don't activate yet — load content first so the tab has data
        // before OnTabSelected tries to display it.
        var tab = TabBar.AddTab(filePath, activate: false);
        if (tab.Buffer.Length == 0)
        {
            try
            {
                string content = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
                tab.Buffer.SetText(content);
            }
            catch { }
        }
        TabBar.SetActiveTab(tab);
    }

    private void OnTabSelected(EditorTab tab)
    {
        // Save the current editor state into the PREVIOUS tab
        // (TabBar.ActiveTab is already the new tab at this point)
        if (_displayedTab != null && _displayedTab != tab)
            SaveEditorStateToTab(_displayedTab);

        _displayedTab = tab;

        // Load the new tab's state into the editor
        _path = tab.FilePath;
        _dirty = tab.IsDirty;
        CodeEditor.SetText(tab.Buffer.GetText());
        CodeEditor.Caret.Position = tab.Caret.Position;
        CodeEditor.Caret.AnchorPosition = tab.Caret.AnchorPosition;
        CodeEditor.Buffer.GetText(); // ensure line starts are built

        UpdateTitle();
    }

    private void OnTabCloseRequested(EditorTab tab)
    {
        if (tab.IsDirty)
        {
            // Auto-save before closing
            try
            {
                SaveEditorStateToTab(tab);
                File.WriteAllText(tab.FilePath, tab.Buffer.GetText());
                ProjectService.TouchModified();
            }
            catch { }
        }
        TabBar.RemoveTab(tab);

        if (TabBar.Tabs.Count == 0) Close();
    }

    private void SaveEditorStateToTab(EditorTab? tab)
    {
        if (tab == null) return;
        tab.Buffer.SetText(CodeEditor.GetText());
        tab.Caret.Position = CodeEditor.Caret.Position;
        tab.Caret.AnchorPosition = CodeEditor.Caret.AnchorPosition;
        tab.IsDirty = _dirty;
    }

    // ── File I/O ────────────────────────────────────────────────

    private void TryLoad()
    {
        try
        {
            string content = File.Exists(_path) ? File.ReadAllText(_path) : "";
            CodeEditor.SetText(content);
        }
        catch
        {
            CodeEditor.SetText("");
        }
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_path, CodeEditor.GetText());
            ProjectService.TouchModified();
            _dirty = false;
            CodeEditor.ClearDirty();
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
            File.WriteAllText(dst, CodeEditor.GetText());
            _path = dst;
            ProjectService.TouchModified();
            _dirty = false;
            CodeEditor.ClearDirty();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            File.WriteAllText(_path, CodeEditor.GetText());
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            ShowError("Auto-save failed:\n" + ex.Message);
        }
        finally
        {
            _dirty = false;
            CodeEditor.ClearDirty();
            UpdateTitle();
        }
    }

    private void OnReload(object? s, RoutedEventArgs e)
    {
        TryLoad();
        _dirty = false;
        UpdateTitle();
    }

    // ── File tree ───────────────────────────────────────────────

    private void RebuildScriptTree()
    {
        _treeItems.Clear();

        var scriptRoots = CandidateScriptFolders()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var p = ProjectService.Current;
        if (scriptRoots.Count == 0 && p != null)
        {
            var fallback = Path.Combine(p.AssetsPath, "Scripts");
            try { Directory.CreateDirectory(fallback); } catch { }
            if (Directory.Exists(fallback)) scriptRoots.Add(fallback);
        }

        foreach (var dir in scriptRoots)
        {
            var rootItem = BuildTreeItemForFolder(dir, displayName: MakeNiceRootName(dir));
            if (rootItem != null) _treeItems.Add(rootItem);
        }

        if (_treeItems.Count > 0) _treeItems[0].IsExpanded = true;
    }

    private static string MakeNiceRootName(string scriptsDir)
    {
        var proj = ProjectService.Current;
        if (proj == null) return Path.GetFileName(scriptsDir);
        var rel = Path.GetRelativePath(proj.RootPath, scriptsDir);
        return string.IsNullOrWhiteSpace(rel) ? Path.GetFileName(scriptsDir) : rel.Replace('\\', '/');
    }

    private TreeViewItem? BuildTreeItemForFolder(string dir, string? displayName = null)
    {
        if (!Directory.Exists(dir)) return null;

        var header = string.IsNullOrEmpty(displayName) ? Path.GetFileName(dir) : displayName;
        var item = new TreeViewItem
        {
            Header = string.IsNullOrEmpty(header) ? dir : header,
            Tag = new NodeTag(dir, isFolder: true)
        };

        IEnumerable<string> subDirs = Enumerable.Empty<string>();
        try { subDirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase); } catch { }

        foreach (var sd in subDirs)
        {
            var name = Path.GetFileName(sd);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;

            var child = BuildTreeItemForFolder(sd);
            if (child != null) item.Items.Add(child);
        }

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

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        var tvi = FileTree.SelectedItem as TreeViewItem;
        var tag = tvi?.Tag as NodeTag;
        if (tag == null || tag.IsFolder) return;
        TryOpenScriptPath(tag.FullPath);
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var tvi = FileTree.SelectedItem as TreeViewItem;
        var tag = tvi?.Tag as NodeTag;
        if (tag != null) StatusText(tag.FullPath);
    }

    private void TryOpenScriptPath(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) { ShowError("File not found:\n" + fullPath); return; }
            OpenFileInTab(fullPath);
        }
        catch (Exception ex)
        {
            ShowError("Failed to open:\n" + ex.Message);
        }
    }

    // ── Title / Status ──────────────────────────────────────────

    private void UpdateTitle()
    {
        Title = $"Script Editor {EditorVersion} — {(_dirty ? "*" : "")}{Path.GetFileName(_path)}";
    }

    private void StatusText(string text)
    {
        if (Status == null) return;
        Dispatcher.UIThread.Post(() => Status.Text = text);
    }

    // ── Build / compile ─────────────────────────────────────────

    private async void OnBuildAll(object? s, RoutedEventArgs e)
    {
        OnSave(this, new RoutedEventArgs());

        try
        {
            var (files, typesLoaded) = await BuildAndLoadProjectScriptsAsync();
            ProjectService.TouchModified();

            var msg = $"Build OK — {typesLoaded} Behavior types loaded from {files} script file(s).";
            StatusText(msg);
            Game_Engine.Core.Log.Info(msg);

            ExtensionService.RefreshFromEditorScriptsFolder();

            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                    && life.MainWindow is MainWindow mw)
                {
                    mw.RefreshProjectUI();
                    mw.RebuildExtensionMenus();
                }
            });
        }
        catch (Exception ex)
        {
            StatusText("Build failed. See details.");
            ShowError("Build failed:\n\n" + ex.Message);
        }
    }

    private Task<(int files, int typesLoaded)> BuildAndLoadProjectScriptsAsync()
    {
        return Task.Run(() =>
        {
            var roots = CandidateScriptRoots().ToList();
            var allFiles = new List<string>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in roots)
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
                        if (seenFiles.Add(full)) allFiles.Add(full);
                    }
                }
                catch { }
            }

            if (allFiles.Count == 0)
                throw new InvalidOperationException("No .cs files found under your Assets/Packages folders.");

            var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var trees = allFiles
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOpts, f))
                .ToList();

            const string Prelude = @"
                global using Avalonia.Controls;
                global using Game_Engine.Views;
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

            void TryDeleteOrQuarantine(string file)
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                try { File.Delete(file); return; } catch { }
                try
                {
                    var old = file + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                catch { }
            }

            try { s_scriptsAlc?.Unload(); } catch { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var proj = ProjectService.Current;
            var outRoot = proj?.BuildsPath;
            if (string.IsNullOrWhiteSpace(outRoot))
                outRoot = proj?.RootPath ?? Path.GetTempPath();

            var outDir = Path.Combine(outRoot!, "EditorScripts");
            Directory.CreateDirectory(outDir);

            try
            {
                foreach (var old in Directory.GetFiles(outDir, "EditorScripts_*.dll"))
                    TryDeleteOrQuarantine(old);
                foreach (var old in Directory.GetFiles(outDir, "EditorScripts_*.pdb"))
                    TryDeleteOrQuarantine(old);
            }
            catch { }

            try
            {
                var dllPath = Path.Combine(outDir, asmName + ".dll");
                File.WriteAllBytes(dllPath, ms.ToArray());
                StatusText($"Build OK — {asmName}.dll saved to {dllPath}");
            }
            catch { }

            ms.Position = 0;
            var alc = new AssemblyLoadContext(asmName, isCollectible: true);
            var asm = alc.LoadFromStream(ms);
            s_scriptsAlc = alc;

            var behaviorType = typeof(Game_Engine.Core.Behavior);
            int loaded = 0;
            try
            {
                loaded = asm.GetTypes().Count(t => t != null && !t.IsAbstract && behaviorType.IsAssignableFrom(t));
            }
            catch { }

            return (allFiles.Count, loaded);
        });
    }

    private static IEnumerable<string> CandidateScriptRoots()
    {
        var p = ProjectService.Current;
        if (p == null) yield break;

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

    private static List<string> CandidateScriptFolders()
    {
        var list = new List<string>();
        var p = ProjectService.Current;
        if (p == null) return list;

        var a = Path.Combine(p.AssetsPath, "Scripts");
        if (Directory.Exists(a)) list.Add(Path.GetFullPath(a));

        IEnumerable<string> pkgs;
        try
        {
            pkgs = Directory.Exists(p.PackagesPath)
                ? Directory.EnumerateDirectories(p.PackagesPath)
                : Array.Empty<string>();
        }
        catch { pkgs = Array.Empty<string>(); }

        foreach (var pkg in pkgs)
        {
            var sc = Path.Combine(pkg, "Scripts");
            if (Directory.Exists(sc)) list.Add(Path.GetFullPath(sc));
        }

        return list;
    }

    private static IEnumerable<MetadataReference> CollectMetadataReferences()
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

    // ── Error dialog ────────────────────────────────────────────

    private async void ShowError(string message)
    {
        var win = new Window
        {
            Title = "Error",
            Width = 420,
            Height = 280,
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
