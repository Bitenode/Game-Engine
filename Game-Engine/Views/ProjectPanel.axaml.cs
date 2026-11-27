using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Game_Engine.Core;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Game_Engine.Views;

public sealed partial class ProjectPanel : UserControl
{
    // ---------------- Model ----------------
    public sealed class ProjectNode
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public ProjectNode? Parent { get; set; }
        public ObservableCollection<ProjectNode> Children { get; } = new();
        public override string ToString() => Name;
    }

    public ObservableCollection<ProjectNode> Roots { get; } = new();

    public string ProjectTitle =>
        ProjectService.Current is { } p ? $"{p.Name}  �  {p.RootPath}" : "No project open";

    private ContextMenu? _addMenu;

    // Drag state
    private Point _pressPt;
    private ProjectNode? _pressNode;
    private PointerPressedEventArgs? _pressArgs;
    private TreeViewItem? _dropTargetItem;

    public ProjectPanel()
    {
        InitializeComponent();
        DataContext = this;

        // Toolbar
        BtnRefresh.Click += (_, __) => Refresh();
        FilterText.TextChanged += (_, __) =>
        {
            if (BtnClearFilter is not null)
                BtnClearFilter.IsVisible = !string.IsNullOrEmpty(FilterText.Text);
            Refresh();
        };

        AddHandler(KeyDownEvent, OnProjectPanelKeyDown, RoutingStrategies.Tunnel);

        // Tree interactions
        Tree.DoubleTapped += OnTreeDoubleTapped;
        Tree.SelectionChanged += OnTreeSelectionChanged;

        // Start internal drag
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);

        // Enable drops + handlers (internal moves & OS file drops)
        DragDrop.SetAllowDrop(Tree, true); // <- attached property
        Tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DropEvent, OnTreeDrop, RoutingStrategies.Tunnel);

        // Context menu for the tree
        Tree.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MakeMenu("_Reveal in Explorer", (_, __) => RevealSelected()),
                MakeMenu("_Rename", async (_, __) => await RenameSelected()),
                MakeMenu("_Duplicate", async (_, __) => await DuplicateSelected()),
                MakeMenu("_Delete", async (_, __) => await DeleteSelected()),
                new Separator(),
                MakeMenu("_Refresh", (_, __) => Refresh()),
            }
        };

        // �+� dropdown
        _addMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MakeMenu("_New Folder",    async (_, __) => await NewFolder()),
                MakeMenu("New _C# Script", async (_, __) => await NewScript()),
                MakeMenu("New _Scene",     async (_, __) => await NewScene()),
                MakeMenu("New _Material",  async (_, __) => await NewMaterial()),
                new Separator(),
                MakeMenu("_Import Files�", async (_, __) => await ImportFiles()),
            }
        };

        // Project lifecycle
        ProjectService.ProjectOpened += Refresh;
        ProjectService.ProjectClosed += Refresh;
        ProjectService.Changed += Refresh;

        Refresh();
    }

    // ---------------- UI helpers ----------------

    private static MenuItem MakeMenu(string header, EventHandler<RoutedEventArgs> onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += onClick;
        return mi;
    }

    private void OnClearFilterClick(object? s, RoutedEventArgs e)
    {
        FilterText.Text = "";
        FilterText.Focus();
    }

    private async void OnNewFolderClick(object? s, RoutedEventArgs e) => await NewFolder();
    private async void OnNewScriptClick(object? s, RoutedEventArgs e) => await NewScript();
    private async void OnNewSceneClick(object? s, RoutedEventArgs e) => await NewScene();
    private async void OnNewMaterialClick(object? s, RoutedEventArgs e) => await NewMaterial();
    private async void OnImportFilesClick(object? s, RoutedEventArgs e) => await ImportFiles();

    private Window? OwnerWindow => this.GetVisualRoot() as Window;

    // ---------------- Refresh tree ----------------

    public void Refresh()
    {
        Roots.Clear();

        var p = ProjectService.Current;
        if (TitleText is not null)
        {
            TitleText.Text = p is not null ? p.Name : "No project open";
            ToolTip.SetTip(TitleText, p is not null ? $"{p.Name}\n{p.RootPath}" : "No project open");
        }
        if (ProjectPathText is not null)
        {
            ProjectPathText.Text = p?.RootPath ?? "";
            ProjectPathText.IsVisible = p is not null && !string.IsNullOrWhiteSpace(p.RootPath);
            ToolTip.SetTip(ProjectPathText, p?.RootPath);
        }

        if (p is null)
        {
            UpdateEmptyState();
            return;
        }

        EnsureFolder(p.AssetsPath);
        EnsureFolder(p.ScenesPath);
        EnsureFolder(p.PackagesPath);
        EnsureFolder(p.BuildsPath);

        var q = (FilterText?.Text ?? "").Trim();
        if (q.Length == 0)
        {
            Roots.Add(LoadDirAsRoot(p.AssetsPath, "Assets"));
            Roots.Add(LoadDirAsRoot(p.ScenesPath, "Scenes"));
            Roots.Add(LoadDirAsRoot(p.PackagesPath, "Packages"));
            Roots.Add(LoadDirAsRoot(p.BuildsPath, "Builds"));
        }
        else
        {
            var ql = q.ToLowerInvariant();
            void AddIfAny(string dir, string label)
            {
                var n = LoadDirRecursiveFiltered(dir, label, parent: null, ql);
                if (n != null) Roots.Add(n);
            }

            AddIfAny(p.AssetsPath, "Assets");
            AddIfAny(p.ScenesPath, "Scenes");
            AddIfAny(p.PackagesPath, "Packages");
            AddIfAny(p.BuildsPath, "Builds");
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (EmptyStateOverlay is null || EmptyStateText is null) return;

        var p = ProjectService.Current;
        if (p is null)
        {
            EmptyStateText.Text = "No project open.";
            EmptyStateOverlay.IsVisible = true;
            return;
        }

        if (Roots.Count == 0)
        {
            EmptyStateText.Text = "No matching files or folders.";
            EmptyStateOverlay.IsVisible = true;
            return;
        }

        bool hasAnyAsset = Roots.Any(r => r.Children.Count > 0);
        if (!hasAnyAsset && string.IsNullOrWhiteSpace(FilterText?.Text))
        {
            EmptyStateText.Text = "No assets yet.\nUse + to create or import.";
            EmptyStateOverlay.IsVisible = true;
            return;
        }

        EmptyStateOverlay.IsVisible = false;
    }

    /// <summary>Focus the project filter field and select all text (e.g. Ctrl+F).</summary>
    public void FocusFilterBox()
    {
        FilterText.Focus();
        FilterText.SelectAll();
    }

    private void OnProjectPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key != Key.F) return;
        FocusFilterBox();
        e.Handled = true;
    }

    private static void EnsureFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
    }

    private ProjectNode LoadDirAsRoot(string dir, string display) => LoadDirRecursive(dir, display, parent: null);

    private static bool NameMatches(string name, string filterLower)
        => name.Contains(filterLower, StringComparison.OrdinalIgnoreCase);

    private static ProjectNode? LoadDirRecursiveFiltered(string dir, string? overrideName, ProjectNode? parent, string filterLower)
    {
        var node = new ProjectNode
        {
            Name = overrideName ?? Path.GetFileName(dir),
            FullPath = dir,
            IsFolder = true,
            Parent = parent
        };

        if (!Directory.Exists(dir)) return node;

        foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
        {
            var child = LoadDirRecursiveFiltered(d, null, node, filterLower);
            if (child != null)
                node.Children.Add(child);
        }

        foreach (var f in Directory.GetFiles(dir).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(f);
            if (!NameMatches(name, filterLower)) continue;
            node.Children.Add(new ProjectNode
            {
                Name = name,
                FullPath = f,
                IsFolder = false,
                Parent = node
            });
        }

        if (node.Children.Count > 0)
            return node;

        var displayName = overrideName ?? Path.GetFileName(dir);
        return NameMatches(displayName, filterLower) ? node : null;
    }

    private ProjectNode LoadDirRecursive(string dir, string? overrideName = null, ProjectNode? parent = null)
    {
        var node = new ProjectNode
        {
            Name = overrideName ?? Path.GetFileName(dir),
            FullPath = dir,
            IsFolder = true,
            Parent = parent
        };

        if (!Directory.Exists(dir)) return node;

        foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
            node.Children.Add(LoadDirRecursive(d, null, node));

        foreach (var f in Directory.GetFiles(dir).OrderBy(Path.GetFileName))
        {
            node.Children.Add(new ProjectNode
            {
                Name = Path.GetFileName(f),
                FullPath = f,
                IsFolder = false,
                Parent = node
            });
        }

        return node;
    }

    // ---------------- Create / Import actions ----------------

    private async Task NewFolder()
    {
        var p = ProjectService.Current;
        if (p is null) return;

        var baseDir = CurrentDirOrFallback(p.AssetsPath);
        var name = await AskText("New Folder", "Enter folder name:", "New Folder");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = MakeSafeName(name);
        var path = UniquePath(Path.Combine(baseDir, name), isFolder: true);

        try
        {
            Directory.CreateDirectory(path);
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to create folder:\n{ex.Message}");
        }

        Refresh();
    }

    private async Task NewScript()
    {
        var p = ProjectService.Current;
        if (p is null) return;

        var baseDir = CurrentDirOrFallback(p.AssetsPath);

        // Let the user type either "MyScript" or "MyScript.cs"
        var name = await AskText("New C# Script", "Enter script name:", "NewBehaviour.cs");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            name += ".cs";

        name = MakeSafeName(name);

        // Ensure uniqueness on disk (may append " (1)" etc.)
        var path = UniquePath(Path.Combine(baseDir, name), isFolder: false);

        // Derive the class name from the *final* file name
        var finalFileNameNoExt = Path.GetFileNameWithoutExtension(path);
        var className = MakeValidClassName(finalFileNameNoExt);

        string template =
    @"using Game_Engine.Core;

    public class " + className + @" : Behavior
    {
        public override void Awake() { }

        public override void Start() { }
        
        public override void Update() { }

        public override void FixedUpdate() { }

        public override void LateUpdate() { }
        
        public override void OnEnable() { }

        public override void OnDisable() { }
        
        public override void OnDestroy() { }

    }
    ";

        try
        {
            File.WriteAllText(path, template);
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            await ShowError("Failed to create script:\n" + ex.Message);
        }

        Refresh();
    }

    private static string MakeValidClassName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "NewBehaviour";

        // Replace separators with underscores and strip invalid chars
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char ch = raw[i];

            // Treat common separators as underscore
            if (ch == ' ' || ch == '-' || ch == '.' || ch == '/' || ch == '\\')
                ch = '_';

            if (i == 0)
            {
                // First char must be letter or underscore
                if (char.IsLetter(ch) || ch == '_') sb.Append(ch);
                else if (char.IsDigit(ch)) { sb.Append('_'); sb.Append(ch); }
                else sb.Append('_');
            }
            else
            {
                // Subsequent chars: letters, digits, or underscore
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
                else sb.Append('_');
            }
        }

        // Collapse multiple underscores
        var s = sb.ToString();
        while (s.Contains("__")) s = s.Replace("__", "_");
        if (s == "_" || string.IsNullOrWhiteSpace(s)) s = "NewBehaviour";
        return s;
    }


    private async Task NewScene()
    {
        var p = ProjectService.Current;
        if (p is null) return;

        var defaultDir = Directory.Exists(p.ScenesPath) ? p.ScenesPath : p.RootPath;
        var baseDir = CurrentDirOrFallback(defaultDir);

        var name = await AskText("New Scene", "Enter scene file name:", "New Scene");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (!name.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            name += ".scene";

        name = MakeSafeName(name);
        var path = UniquePath(Path.Combine(baseDir, name), isFolder: false);

        var emptyScene = "{ \"name\": \"New Scene\", \"objects\": [] }";

        try
        {
            File.WriteAllText(path, emptyScene);
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to create scene:\n{ex.Message}");
        }

        Refresh();
    }

    private async Task NewMaterial()
    {
        var p = ProjectService.Current;
        if (p is null) return;

        var baseDir = CurrentDirOrFallback(p.AssetsPath);

        var name = await AskText("New Material", "Enter material file name:", "NewMaterial.material");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (!name.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
            name += ".material";

        name = MakeSafeName(name);
        var path = UniquePath(Path.Combine(baseDir, name), isFolder: false);

        // Use the filename as the material "name" field
        var matName = Path.GetFileNameWithoutExtension(path);

        
        string json =
        $@"{{
          ""name"": ""{matName}"",
          ""type"": ""Material"",
          ""version"": 1,
          ""shader"": """", 
          ""parameters"": {{
            ""Tint"": ""#FFFFFFFF"",
            ""Metallic"": 0,
            ""Roughness"": 0.5,
            ""Transparent"": false,
            ""AlphaCutoff"": 0.5
          }},
          ""textures"": {{
            ""Albedo"": null,
            ""Normal"": null,
            ""Metallic"": null,
            ""Roughness"": null,
            ""AmbientOcclusion"": null,
            ""Emissive"": null,
            ""Opacity"": null
          }}
        }}";

        try
        {
            File.WriteAllText(path, json);
            ProjectService.TouchModified(); // make the project know something changed
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to create material:\n{ex.Message}");
        }

        Refresh();
    }

    private async Task ImportFiles()
    {
        var p = ProjectService.Current;
        if (p is null) return;

        var dlg = new OpenFileDialog { Title = "Import files", AllowMultiple = true };
        var files = await dlg.ShowAsync(OwnerWindow);
        if (files is not { Length: > 0 }) return;

        var targetDir = CurrentDirOrFallback(p.AssetsPath);

        foreach (var src in files)
        {
            try
            {
                if (!File.Exists(src)) continue;
                var dst = UniquePath(Path.Combine(targetDir, Path.GetFileName(src)), isFolder: false);
                File.Copy(src, dst, overwrite: false);
            }
            catch (Exception ex)
            {
                await ShowError($"Failed to import '{Path.GetFileName(src)}':\n{ex.Message}");
            }
        }

        ProjectService.TouchModified();
        Refresh();
    }

    

    // ---------------- Selection / reveal / open ----------------

    private ProjectNode? SelectedNode => Tree.SelectedItem as ProjectNode;

    private string CurrentDirOrFallback(string fallback)
    {
        var sel = SelectedNode;
        if (sel is null) return fallback;
        if (sel.IsFolder) return sel.FullPath;
        return Path.GetDirectoryName(sel.FullPath) ?? fallback;
    }

    private void RevealSelected()
    {
        var sel = SelectedNode;
        if (sel is null) return;
        Reveal(sel);
    }

    private async Task RenameSelected()
    {
        var sel = SelectedNode;
        if (sel is null || string.IsNullOrWhiteSpace(sel.FullPath)) return;
        var oldPath = sel.FullPath;
        var baseName = sel.IsFolder ? Path.GetFileName(oldPath) : Path.GetFileNameWithoutExtension(oldPath);
        var prompt = await AskText("Rename", "New name:", baseName);
        if (string.IsNullOrWhiteSpace(prompt)) return;

        string targetName = MakeSafeName(prompt.Trim());
        if (!sel.IsFolder)
        {
            var ext = Path.GetExtension(oldPath);
            if (!targetName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                targetName += ext;
        }

        var dir = Path.GetDirectoryName(oldPath) ?? "";
        var desired = Path.Combine(dir, targetName);
        var dst = sel.IsFolder ? UniquePath(desired, true) : UniquePath(desired, false);
        try
        {
            if (sel.IsFolder) Directory.Move(oldPath, dst);
            else File.Move(oldPath, dst);
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            await ShowError($"Rename failed:\n{ex.Message}");
        }
        Refresh();
    }

    private async Task DuplicateSelected()
    {
        var sel = SelectedNode;
        if (sel is null || string.IsNullOrWhiteSpace(sel.FullPath)) return;
        try
        {
            if (sel.IsFolder)
            {
                var dst = UniquePath(sel.FullPath, true);
                CopyDirectory(sel.FullPath, dst);
            }
            else
            {
                var dst = UniquePath(sel.FullPath, false);
                File.Copy(sel.FullPath, dst, overwrite: false);
            }
            ProjectService.TouchModified();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowError($"Duplicate failed:\n{ex.Message}");
        }
    }

    private async Task DeleteSelected()
    {
        var sel = SelectedNode;
        if (sel is null || string.IsNullOrWhiteSpace(sel.FullPath)) return;
        var confirm = await AskText("Delete", $"Type DELETE to remove '{sel.Name}'.", "");
        if (!string.Equals(confirm?.Trim(), "DELETE", StringComparison.Ordinal)) return;
        try
        {
            if (sel.IsFolder)
                Directory.Delete(sel.FullPath, recursive: true);
            else
                File.Delete(sel.FullPath);
            ProjectService.TouchModified();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowError($"Delete failed:\n{ex.Message}");
        }
    }

    /// <summary>Project-relative or absolute path — select and expand the Project tree to it.</summary>
    public bool TryRevealPath(string relativeOrAbs)
    {
        var p = ProjectService.Current;
        if (p == null) return false;
        var normalized = relativeOrAbs.Replace('/', Path.DirectorySeparatorChar);
        string abs = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(p.RootPath, normalized));

        foreach (var root in Roots)
        {
            if (!TryFindNodeByPath(root, abs, out var node)) continue;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var tvi in Tree.GetVisualDescendants().OfType<TreeViewItem>())
                {
                    if (tvi.DataContext is ProjectNode pn && IsAncestorOf(pn, node))
                        tvi.IsExpanded = true;
                }
                Tree.SelectedItem = node;
                foreach (var tvi in Tree.GetVisualDescendants().OfType<TreeViewItem>())
                {
                    if (tvi.DataContext == node)
                    {
                        tvi.BringIntoView();
                        break;
                    }
                }
            });
            return true;
        }
        return false;
    }

    private static bool IsAncestorOf(ProjectNode anc, ProjectNode desc)
    {
        for (var x = desc.Parent; x != null; x = x.Parent)
        {
            if (x == anc) return true;
        }
        return false;
    }

    private static bool TryFindNodeByPath(ProjectNode n, string absFull, out ProjectNode? found)
    {
        if (string.Equals(Path.GetFullPath(n.FullPath), absFull, StringComparison.OrdinalIgnoreCase))
        {
            found = n;
            return true;
        }
        foreach (var c in n.Children)
        {
            if (TryFindNodeByPath(c, absFull, out found))
                return true;
        }
        found = null;
        return false;
    }

    private static void Reveal(ProjectNode node)
    {
        try
        {
            var path = node.FullPath;
            if (node.IsFolder && Directory.Exists(path) || File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }

    private void OnTreeSelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        var node = SelectedNode;
        if (node is null || node.IsFolder) return;

        var ext = Path.GetExtension(node.FullPath);
        if (ext.Equals(".material", StringComparison.OrdinalIgnoreCase))
        {
            // send absolute path to inspector
            ProjectService.SelectAssetForInspector(node.FullPath);
        }
    }

    private void OnTreeDoubleTapped(object? s, RoutedEventArgs e)
    {
        var node = NodeFromObject(e.Source) ?? SelectedNode;
        if (node is null) return;

        if (!node.IsFolder)
        {
            var ext = Path.GetExtension(node.FullPath);
            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                ScriptEditorWindow.Open(OwnerWindow, node.FullPath);
                return;
            }
            if (ext.Equals(".material", StringComparison.OrdinalIgnoreCase))
            {
                ProjectService.SelectAssetForInspector(node.FullPath);
                return;
            }
        }

        Reveal(node);
    }

    

    // ---------------- Internal drag & drop ----------------

    private void OnTreePointerPressed(object? s, PointerPressedEventArgs e)
    {
        _pressPt = e.GetPosition(Tree);
        _pressNode = NodeFromObject(e.Source);
        _pressArgs = e;
    }

    private async void OnTreePointerMoved(object? s, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed && _pressNode != null)
        {
            var p = e.GetPosition(Tree);
            var dx = p.X - _pressPt.X;
            var dy = p.Y - _pressPt.Y;
            if ((dx * dx + dy * dy) > 9) // ~3px
            {
                var path = _pressNode.FullPath;
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(EditorDragFormats.ProjectNodePath, path));

                if (File.Exists(path) && TopLevel.GetTopLevel(this) is { } top)
                {
                    var file = await top.StorageProvider.TryGetFileFromPathAsync(path);
                    if (file != null)
                        transfer.Add(DataTransferItem.CreateFile(file));
                }

                if (_pressArgs != null)
                    await DragDrop.DoDragDropAsync(_pressArgs, transfer, DragDropEffects.Copy | DragDropEffects.Move);
                _pressNode = null;
                _pressArgs = null;
            }
        }
    }


    private void SetDropTarget(TreeViewItem? item)
    {
        if (_dropTargetItem == item) return;
        _dropTargetItem?.Classes.Remove("drop-target");
        _dropTargetItem = item;
        _dropTargetItem?.Classes.Add("drop-target");
    }

    private TreeViewItem? FindTreeViewItemForNode(ProjectNode node)
    {
        return Tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(tvi => ReferenceEquals(tvi.DataContext, node));
    }

    private void OnTreeDragLeave(object? s, DragEventArgs e)
    {
        SetDropTarget(null);
    }

    private void OnTreeDragOver(object? s, DragEventArgs e)
    {
        var target = NodeFromObject(e.Source);
        var targetFolder = GetDropFolder(target);
        if (targetFolder is null)
        {
            e.DragEffects = DragDropEffects.None;
            SetDropTarget(null);
            return;
        }

        // Internal move?
        if (e.Payload().GetProjectNodePath() != null)
        {
            var src = e.Payload().GetProjectNodePath();
            if (string.IsNullOrWhiteSpace(src))
            {
                e.DragEffects = DragDropEffects.None;
                SetDropTarget(null);
                return;
            }

            // cannot drop into itself or descendant
            if (IsSameOrDescendant(src, targetFolder.FullPath))
            {
                e.DragEffects = DragDropEffects.None;
                SetDropTarget(null);
            }
            else
            {
                e.DragEffects = DragDropEffects.Move;
                SetDropTarget(FindTreeViewItemForNode(targetFolder));
            }

            e.Handled = true;
            return;
        }

        // OS files drop
        if (e.Payload().HasFiles)
        {
            e.DragEffects = DragDropEffects.Copy;
            SetDropTarget(FindTreeViewItemForNode(targetFolder));
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
        SetDropTarget(null);
    }

    private async void OnTreeDrop(object? s, DragEventArgs e)
    {
        SetDropTarget(null);
        var target = NodeFromObject(e.Source);
        var targetFolder = GetDropFolder(target);
        if (targetFolder is null) return;

        // Internal move
        if (e.Payload().GetProjectNodePath() != null)
        {
            var src = e.Payload().GetProjectNodePath();
            if (string.IsNullOrWhiteSpace(src)) return;

            try
            {
                await MovePathAsync(src, targetFolder.FullPath);
                ProjectService.TouchModified();
                Refresh();
            }
            catch (Exception ex)
            {
                await ShowError($"Move failed:\n{ex.Message}");
            }
            e.Handled = true;
            return;
        }

        // OS files drop
        if (e.Payload().HasFiles)
        {
            var files = e.Payload().GetFilePaths();
            if (files != null)
            {
                foreach (var src in files)
                {
                    try
                    {
                        if (!File.Exists(src)) continue;
                        var dst = UniquePath(Path.Combine(targetFolder.FullPath, Path.GetFileName(src)), isFolder: false);
                        File.Copy(src, dst, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        await ShowError($"Failed to import '{Path.GetFileName(src)}':\n{ex.Message}");
                    }
                }
                ProjectService.TouchModified();
                Refresh();
                e.Handled = true;
            }
        }
    }

    private static ProjectNode? NodeFromObject(object? src)
    {
        if (src is not Control c) return null;
        return c.DataContext as ProjectNode
               ?? c.FindAncestorOfType<TreeViewItem>()?.DataContext as ProjectNode;
    }

    private static ProjectNode? GetDropFolder(ProjectNode? node)
    {
        if (node is null) return null;
        return node.IsFolder ? node : node.Parent;
    }

    private static bool IsSameOrDescendant(string srcPath, string dstFolder)
    {
        var s = Path.GetFullPath(srcPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var d = Path.GetFullPath(dstFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(s, d, StringComparison.OrdinalIgnoreCase)) return true;
        if (Directory.Exists(s))
            return d.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static async Task MovePathAsync(string src, string dstFolder)
    {
        Directory.CreateDirectory(dstFolder);
        var name = Path.GetFileName(src);
        var dst = Path.Combine(dstFolder, name);

        if (Directory.Exists(src))
        {
            dst = UniquePath(dst, isFolder: true);
            try
            {
                Directory.Move(src, dst);
            }
            catch
            {
                CopyDirectory(src, dst);
                await Task.Yield();
                Directory.Delete(src, recursive: true);
            }
        }
        else if (File.Exists(src))
        {
            dst = UniquePath(dst, isFolder: false);
            try
            {
                File.Move(src, dst);
            }
            catch
            {
                File.Copy(src, dst);
                await Task.Yield();
                File.Delete(src);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    // ---------------- Small helpers ----------------

    private static string MakeSafeName(string name)
    {
        var cleaned = string.Concat(name.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is ' ' or '_' or '-' or '.' ? ch : '_')).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "NewItem" : cleaned;
    }

    private static string UniquePath(string path, bool isFolder)
    {
        if (isFolder)
        {
            if (!Directory.Exists(path)) return path;
            var baseName = path;
            int i = 1;
            while (Directory.Exists($"{baseName} ({i})")) i++;
            return $"{baseName} ({i})";
        }
        else
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int i = 1;
            string candidate;
            do { candidate = Path.Combine(dir, $"{name} ({i++}){ext}"); }
            while (File.Exists(candidate));
            return candidate;
        }
    }

    private async Task<string?> AskText(string title, string prompt, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        var tb = new TextBox { Text = initial, Width = 260, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(tb);
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);

        var win = new Window
        {
            Title = title,
            Width = 360,
            Height = 160,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        ok.Click += (_, __) => { tcs.TrySetResult(tb.Text); win.Close(); };
        cancel.Click += (_, __) => { tcs.TrySetResult(null); win.Close(); };

        await win.ShowDialog(OwnerWindow);
        return await tcs.Task;
    }

    private async Task ShowError(string message)
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
        await win.ShowDialog(OwnerWindow);
    }
}

/// <summary>Maps a ProjectNode to a vector icon Geometry based on folder/file extension.</summary>
public sealed class ProjectIconConverter : IValueConverter
{
    private static readonly Geometry FolderGeom   = Geometry.Parse("M3,6 C3,4.9 3.9,4 5,4 L9.5,4 L11.5,6.5 L19,6.5 C20.1,6.5 21,7.4 21,8.5 L21,18 C21,19.1 20.1,20 19,20 L5,20 C3.9,20 3,19.1 3,18 Z");
    private static readonly Geometry SceneGeom    = Geometry.Parse("M3,6 L21,6 L21,19 L3,19 Z M3,10 L21,10 M7,6 L9,10 M13,6 L15,10");
    private static readonly Geometry PrefabGeom   = Geometry.Parse("M12,3 L20,7.5 L20,16.5 L12,21 L4,16.5 L4,7.5 Z M12,12 L20,7.5 M12,12 L4,7.5 M12,12 L12,21");
    private static readonly Geometry ScriptGeom   = Geometry.Parse("M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,5 L10,19");
    private static readonly Geometry MaterialGeom = Geometry.Parse("M12,4 A8,8 0 1,0 12,20 A8,8 0 1,0 12,4 M12,4 C9,7 9,17 12,20 M4,12 L20,12");
    private static readonly Geometry ImageGeom    = Geometry.Parse("M3,5 L21,5 L21,19 L3,19 Z M4,17 L9.5,11.5 L14,16 L16.5,13.5 L20,17 M8.5,9 A1.2,1.2 0 1,0 8.5,11.4 A1.2,1.2 0 1,0 8.5,9");
    private static readonly Geometry ModelGeom    = Geometry.Parse("M12,3 L20,8 L12,13 L4,8 Z M4,12.5 L12,17.5 L20,12.5 M4,16.5 L12,21.5 L20,16.5");
    private static readonly Geometry AudioGeom    = Geometry.Parse("M4,9 L8,9 L13,5 L13,19 L8,15 L4,15 Z M16,9 C17.5,10.5 17.5,13.5 16,15 M18.5,6.5 C21,9.5 21,14.5 18.5,17.5");
    private static readonly Geometry FileGeom     = Geometry.Parse("M6,3 L14,3 L19,8 L19,21 L6,21 Z M14,3 L14,8 L19,8");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProjectPanel.ProjectNode node)
            return FileGeom;

        if (node.IsFolder)
            return FolderGeom;

        var ext = Path.GetExtension(node.Name).ToLowerInvariant();
        return ext switch
        {
            ".scene" => SceneGeom,
            ".prefab" => PrefabGeom,
            ".cs" or ".shader" or ".hlsl" or ".glsl" => ScriptGeom,
            ".material" or ".mat" => MaterialGeom,
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".webp" or ".gif" or ".hdr" or ".exr" or ".dds" or ".tif" or ".tiff" => ImageGeom,
            ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".stl" or ".3ds" or ".blend" or ".mesh" => ModelGeom,
            ".wav" or ".mp3" or ".ogg" or ".flac" or ".aiff" or ".aif" => AudioGeom,
            _ => FileGeom
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns the file name stem (without extension) for files, or the full name for folders.</summary>
public sealed class ProjectFileStemConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProjectPanel.ProjectNode node)
            return value?.ToString() ?? "";

        if (node.IsFolder)
            return node.Name;

        var ext = Path.GetExtension(node.Name);
        if (string.IsNullOrEmpty(ext))
            return node.Name;

        var stem = Path.GetFileNameWithoutExtension(node.Name);
        return string.IsNullOrEmpty(stem) ? node.Name : stem;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns the file extension (e.g. ".cs") for files, or empty string for folders.</summary>
public sealed class ProjectFileExtConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProjectPanel.ProjectNode node || node.IsFolder)
            return "";

        var ext = Path.GetExtension(node.Name);
        if (string.IsNullOrEmpty(ext))
            return "";

        var stem = Path.GetFileNameWithoutExtension(node.Name);
        return string.IsNullOrEmpty(stem) ? "" : ext;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
