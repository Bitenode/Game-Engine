using System;
using System.IO;
using System.Runtime.Loader;
using System.Threading.Tasks;
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
using Game_Engine.Core.Editor;
using Avalonia.Controls.ApplicationLifetimes;

namespace Game_Engine.Views;

public partial class ScriptEditorWindow : Window
{
    private string _path;
    private bool _dirty;
    private EditorTab? _displayedTab;   // tracks which tab the editor is currently showing
    private static AssemblyLoadContext? s_scriptsAlc;
    private readonly ObservableCollection<TreeViewItem> _treeItems = new();
    private bool _isHandlingClosePrompt;

    private const string EditorVersion = "v2.5";

    // ── Constructor ─────────────────────────────────────────────

    public ScriptEditorWindow(string path, int? initialLine1Based = null)
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
        Closing += OnWindowClosing;

        // Wire toolbar buttons
        BtnBuild.Click += OnBuildAll;
        BtnSave.Click += OnSave;
        BtnSaveAs.Click += OnSaveAs;
        BtnReload.Click += OnReload;
        BtnFormat.Click += OnFormatDocument;
        BtnZoomIn.Click += (_, __) => CodeEditor.AdjustEditorFontSize(1);
        BtnZoomOut.Click += (_, __) => CodeEditor.AdjustEditorFontSize(-1);
        BtnDefinitionFiles.Click += (_, __) => ShowDefinitionFilesWindow();
        BtnFindReferences.Click += (_, __) => ShowReferencesAtCaret();
        BtnRenameSymbol.Click += (_, __) => RenameSymbolAtCaret();
        BtnToggleMinimap.IsChecked = EditorSettings.ScriptEditorShowMinimap;
        BtnToggleMinimap.IsCheckedChanged += (_, __) =>
        {
            CodeEditor.MinimapVisible = BtnToggleMinimap.IsChecked == true;
            EditorSettings.ScriptEditorShowMinimap = CodeEditor.MinimapVisible;
            EditorSettings.Save();
        };
        BtnToggleLineNumbers.IsChecked = EditorSettings.ScriptEditorShowLineNumbers;
        BtnToggleLineNumbers.IsCheckedChanged += (_, __) =>
        {
            CodeEditor.ShowLineNumbers = BtnToggleLineNumbers.IsChecked == true;
            EditorSettings.ScriptEditorShowLineNumbers = CodeEditor.ShowLineNumbers;
            EditorSettings.Save();
        };
        BtnToggleWordWrap.IsChecked = EditorSettings.ScriptEditorWordWrap;
        BtnToggleWordWrap.IsCheckedChanged += (_, __) =>
        {
            CodeEditor.WordWrap = BtnToggleWordWrap.IsChecked == true;
            EditorSettings.ScriptEditorWordWrap = CodeEditor.WordWrap;
            EditorSettings.Save();
        };
        BtnClose.Click += (_, __) => Close();

        CodeEditor.DiagnosticsUpdated += OnDiagnosticsUpdated;
        CodeEditor.GoToDefinitionRequestedAtOffset += OnGoToDefinitionRequestedAtOffset;
        if (StatusProblems != null)
            StatusProblems.Text = "";
        CodeEditor.MinimapVisible = EditorSettings.ScriptEditorShowMinimap;
        CodeEditor.ShowLineNumbers = EditorSettings.ScriptEditorShowLineNumbers;
        CodeEditor.WordWrap = EditorSettings.ScriptEditorWordWrap;

        // Open initial file as a tab
        OpenFileInTab(path);
        if (initialLine1Based is >= 1)
            CodeEditor.GoToLine1Based(initialLine1Based.Value);
    }

    void OnProblemsStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CodeEditor.GoToFirstDiagnostic();
    }

    void OnDiagnosticsUpdated(IReadOnlyList<EditorDiagnostic> diags)
    {
        if (StatusProblems == null) return;
        int err = 0, warn = 0;
        foreach (var d in diags)
        {
            if (d.Severity == DiagSeverity.Error) err++;
            else if (d.Severity == DiagSeverity.Warning) warn++;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (err == 0 && warn == 0)
            {
                StatusProblems.Text = diags.Count == 0 ? "" : "No errors or warnings.";
                StatusProblems.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6A9955"));
                return;
            }
            var parts = new List<string>();
            if (err > 0) parts.Add($"{err} error(s)");
            if (warn > 0) parts.Add($"{warn} warning(s)");
            StatusProblems.Text = string.Join(", ", parts);
            StatusProblems.Foreground = err > 0
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F48771"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E8C468"));
            string? first = null;
            foreach (var d in diags)
            {
                if (d.Severity == DiagSeverity.Error) { first = d.Message; break; }
            }
            if (first == null)
            {
                foreach (var d in diags)
                {
                    if (d.Severity == DiagSeverity.Warning) { first = d.Message; break; }
                }
            }
            if (!string.IsNullOrEmpty(first))
                StatusProblems.Text += " — " + first;
        });
    }

    async void OnFormatDocument(object? s, RoutedEventArgs e)
    {
        try
        {
            await CodeEditor.FormatDocumentAsync();
            _dirty = true;
            if (TabBar.ActiveTab != null) TabBar.ActiveTab.IsDirty = true;
            TabBar.RefreshTabDirtyState();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            ShowError("Format failed:\n" + ex.Message);
        }
    }

    static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>Open or focus a script editor and move the caret to <paramref name="line1Based"/> (1-based).</summary>
    public static void OpenAtLine(Window? owner, string path, int line1Based)
    {
        var full = NormalizePath(path);
        if (!File.Exists(full)) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            foreach (var win in life.Windows)
            {
                if (win is ScriptEditorWindow sew && sew.TryFocusFileAndLine(full, line1Based))
                    return;
            }
        }

        var editor = new ScriptEditorWindow(full, line1Based > 0 ? line1Based : null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (owner != null) editor.Show(owner);
        else editor.Show();
    }

    bool TryFocusFileAndLine(string fullPath, int line1Based)
    {
        var want = NormalizePath(fullPath);
        foreach (var t in TabBar.Tabs)
        {
            if (!string.Equals(NormalizePath(t.FilePath), want, StringComparison.OrdinalIgnoreCase))
                continue;
            TabBar.SetActiveTab(t);
            if (line1Based >= 1)
                CodeEditor.GoToLine1Based(line1Based);
            Activate();
            return true;
        }
        return false;
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
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                 e.Key == Key.O)
        {
            ShowDefinitionFilesWindow();
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
        else if (e.Key == Key.F12 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ShowReferencesAtCaret();
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            e.Handled = true;
            var text = CodeEditor.GetText();
            var docPath = CodeEditor.DocumentPath;
            var caretPos = CodeEditor.Caret.Position;
            _ = RunGoToDefinitionAsync(text, docPath, caretPos);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                 e.Key == Key.R)
        {
            RenameSymbolAtCaret();
            e.Handled = true;
        }
        else if (e.Key == Key.F8)
        {
            CodeEditor.GoToNextDiagnostic(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            CodeEditor.AdjustEditorFontSize(1);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            CodeEditor.AdjustEditorFontSize(-1);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.D0)
        {
            CodeEditor.ResetEditorFontSize();
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

    private async Task RunGoToDefinitionAsync(string text, string? docPath, int caretOffset)
    {
        var result = await Task.Run(() =>
            EditorScriptsGoToDefinition.TryResolve(text, docPath, caretOffset)).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!result.Found)
            {
                StatusText("No definition found for the symbol at the caret.");
                return;
            }
            if (!result.IsSameDocument && !string.IsNullOrEmpty(result.TargetFilePath))
                OpenFileInTab(result.TargetFilePath);
            CodeEditor.GoToLine1Based(result.Line1Based);
        });
    }

    private void OnGoToDefinitionRequestedAtOffset(int offset)
    {
        var text = CodeEditor.GetText();
        var docPath = CodeEditor.DocumentPath;
        _ = RunGoToDefinitionAsync(text, docPath, offset);
    }

    private void ShowReferencesAtCaret()
    {
        var text = CodeEditor.GetText();
        var path = CodeEditor.DocumentPath;
        var caret = CodeEditor.Caret.Position;
        var refs = EditorScriptsGoToDefinition.FindReferences(text, path, caret);
        if (refs.Count == 0)
        {
            StatusText("No references found.");
            return;
        }

        var list = new ListBox { SelectionMode = SelectionMode.Single };
        foreach (var r in refs)
        {
            var label = $"{r.FilePath}:{r.Line1Based}:{r.Column1Based}  {r.LineText}";
            list.Items.Add(new ListBoxItem { Content = label, Tag = r, Padding = new Thickness(6, 3) });
        }
        if (list.ItemCount > 0) list.SelectedIndex = 0;

        var win = new Window
        {
            Title = $"References ({refs.Count})",
            Width = 980,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = list
        };
        void OpenSelection()
        {
            if (list.SelectedItem is not ListBoxItem lbi || lbi.Tag is not SymbolReferenceResult r) return;
            OpenFileInTab(r.FilePath);
            CodeEditor.GoToLine1Based(r.Line1Based);
            win.Close();
        }
        list.DoubleTapped += (_, __) => OpenSelection();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelection(); e.Handled = true; } };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { win.Close(); e.Handled = true; } };
        win.Show(this);
    }

    private async void RenameSymbolAtCaret()
    {
        var newName = await AskTextAsync("Rename Symbol", "New symbol name:", "");
        if (string.IsNullOrWhiteSpace(newName)) return;
        var text = CodeEditor.GetText();
        var path = CodeEditor.DocumentPath;
        var caret = CodeEditor.Caret.Position;
        bool ok;
        int changedFiles;
        try
        {
            ok = EditorScriptsGoToDefinition.RenameSymbol(text, path, caret, newName.Trim(), out changedFiles);
        }
        catch (Exception ex)
        {
            ShowError("Rename failed:\n" + ex.Message);
            return;
        }
        if (!ok)
        {
            StatusText("Rename found no symbol usages.");
            return;
        }
        StatusText($"Renamed symbol in {changedFiles} file(s). Reloading tab...");
        if (!string.IsNullOrWhiteSpace(_path) && File.Exists(_path))
            TryLoad();
    }

    private void ShowDefinitionFilesWindow()
    {
        var all = EditorScriptsGoToDefinition.GetIndexedFilePaths()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (all.Count == 0)
        {
            StatusText("No indexed definition files found.");
            return;
        }

        var search = new TextBox
        {
            Watermark = "Filter files... (Enter=open, Esc=close)",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var list = new ListBox
        {
            MinHeight = 320,
            SelectionMode = SelectionMode.Single
        };

        void Rebuild()
        {
            var q = (search.Text ?? "").Trim();
            var rows = string.IsNullOrWhiteSpace(q)
                ? all
                : all.Where(p => p.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            list.Items.Clear();
            foreach (var p in rows)
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = p, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    Tag = p,
                    Padding = new Thickness(6, 3)
                });
            }
            if (list.ItemCount > 0) list.SelectedIndex = 0;
        }

        var layout = new Avalonia.Controls.Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        layout.Children.Add(search);
        var listHost = new Border { Child = list };
        Avalonia.Controls.Grid.SetRow(listHost, 1);
        layout.Children.Add(listHost);

        var win = new Window
        {
            Title = $"Definition Files ({all.Count})",
            Width = 900,
            Height = 560,
            MinWidth = 640,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        void OpenSelection()
        {
            if (list.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;
            OpenFileInTab(path);
            win.Close();
        }

        search.TextChanged += (_, __) => Rebuild();
        list.DoubleTapped += (_, __) => OpenSelection();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelection();
                e.Handled = true;
            }
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                win.Close();
                e.Handled = true;
            }
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.Focus();
                list.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                OpenSelection();
                e.Handled = true;
            }
        };
        win.Opened += (_, __) =>
        {
            Rebuild();
            search.Focus();
        };
        win.Show(this);
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
        try
        {
            CodeEditor.DocumentPath = string.IsNullOrWhiteSpace(tab.FilePath)
                ? null
                : Path.GetFullPath(tab.FilePath);
        }
        catch
        {
            CodeEditor.DocumentPath = tab.FilePath;
        }
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

    private async void OnReload(object? s, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var decision = await AskSaveDiscardCancelAsync(
                "Reload from disk?",
                "You have unsaved changes in this tab. Save before reloading?");
            if (decision == PendingAction.Cancel) return;
            if (decision == PendingAction.Save)
                OnSave(this, new RoutedEventArgs());
        }
        TryLoad();
        _dirty = false;
        UpdateTitle();
    }

    enum PendingAction { Save, Discard, Cancel }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isHandlingClosePrompt) return;
        if (!HasAnyDirtyTabs()) return;
        e.Cancel = true;

        var choice = await AskSaveDiscardCancelAsync(
            "Close script editor?",
            "There are unsaved script tabs. Save changes before closing?");
        if (choice == PendingAction.Cancel) return;
        if (choice == PendingAction.Save)
            SaveAllDirtyTabs();

        _isHandlingClosePrompt = true;
        Close();
    }

    private bool HasAnyDirtyTabs()
    {
        if (_dirty) return true;
        foreach (var t in TabBar.Tabs)
            if (t.IsDirty) return true;
        return false;
    }

    private void SaveAllDirtyTabs()
    {
        if (_displayedTab != null)
            SaveEditorStateToTab(_displayedTab);
        foreach (var t in TabBar.Tabs)
        {
            if (!t.IsDirty) continue;
            try
            {
                File.WriteAllText(t.FilePath, t.Buffer.GetText());
                t.IsDirty = false;
            }
            catch { }
        }
        _dirty = false;
        CodeEditor.ClearDirty();
        TabBar.RefreshTabDirtyState();
        UpdateTitle();
        ProjectService.TouchModified();
    }

    private async Task<PendingAction> AskSaveDiscardCancelAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<PendingAction>();
        var txt = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var btnSave = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
        var btnDiscard = new Button { Content = "Don't Save", MinWidth = 90 };
        var btnCancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { btnCancel, btnDiscard, btnSave }
        };
        var host = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children = { txt, row }
        };
        var win = new Window
        {
            Title = title,
            Width = 430,
            Height = 170,
            MinWidth = 400,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = host
        };

        btnSave.Click += (_, __) => { tcs.TrySetResult(PendingAction.Save); win.Close(); };
        btnDiscard.Click += (_, __) => { tcs.TrySetResult(PendingAction.Discard); win.Close(); };
        btnCancel.Click += (_, __) => { tcs.TrySetResult(PendingAction.Cancel); win.Close(); };
        win.Closing += (_, __) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(PendingAction.Cancel); };
        await win.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> AskTextAsync(string title, string prompt, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 10) };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { cancel, ok }
        };
        var host = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 6,
            Children = { new TextBlock { Text = prompt }, tb, row }
        };
        var win = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = host
        };
        ok.Click += (_, __) => { tcs.TrySetResult(tb.Text); win.Close(); };
        cancel.Click += (_, __) => { tcs.TrySetResult(null); win.Close(); };
        win.Closing += (_, __) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(null); };
        await win.ShowDialog(this);
        return await tcs.Task;
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
        => RunCompileProjectScriptsAsync(s => StatusText(s));

    /// <summary>Compile and hot-load all project scripts (same as Script Editor Build). Safe to call when no editor is open.</summary>
    public static Task<(int files, int typesLoaded)> CompileAllProjectScriptsAsync()
        => RunCompileProjectScriptsAsync(null);

    private static Task<(int files, int typesLoaded)> RunCompileProjectScriptsAsync(Action<string>? statusSink)
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
                var msg = $"Build OK — {asmName}.dll saved to {dllPath}";
                statusSink?.Invoke(msg);
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
