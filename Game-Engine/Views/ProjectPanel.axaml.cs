using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Game_Engine.Core;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        ProjectService.Current is { } p ? $"{p.Name}  —  {p.RootPath}" : "No project open";

    private ContextMenu? _addMenu;

    // Drag state
    private Point _pressPt;
    private ProjectNode? _pressNode;

    public ProjectPanel()
    {
        InitializeComponent();
        DataContext = this;

        // Toolbar
        BtnRefresh.Click += (_, __) => Refresh();
        BtnAdd.Click += OnAddClicked;

        // Tree interactions
        Tree.DoubleTapped += OnTreeDoubleTapped;
        Tree.SelectionChanged += OnTreeSelectionChanged;
        

        // Start internal drag
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);

        // Enable drops + handlers (internal moves & OS file drops)
        DragDrop.SetAllowDrop(Tree, true); // <- attached property
        Tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DropEvent, OnTreeDrop, RoutingStrategies.Tunnel);

        // Context menu for the tree
        Tree.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MakeMenu("_Reveal in Explorer", (_, __) => RevealSelected()),
                new Separator(),
                MakeMenu("_Refresh", (_, __) => Refresh()),
            }
        };

        // “+” dropdown
        _addMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MakeMenu("_New Folder",    async (_, __) => await NewFolder()),
                MakeMenu("New _C# Script", async (_, __) => await NewScript()),
                MakeMenu("New _Scene",     async (_, __) => await NewScene()),
                MakeMenu("New _Material",  async (_, __) => await NewMaterial()),
                new Separator(),
                MakeMenu("_Import Files…", async (_, __) => await ImportFiles()),
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

    private void OnAddClicked(object? s, RoutedEventArgs e)
    {
        if (_addMenu is null) return;
        _addMenu.PlacementTarget = BtnAdd;
        _addMenu.Open(BtnAdd);
    }

    private Window? OwnerWindow => this.GetVisualRoot() as Window;

    // ---------------- Refresh tree ----------------

    public void Refresh()
    {
        Roots.Clear();

        if (TitleText is not null) TitleText.Text = ProjectTitle;

        var p = ProjectService.Current;
        if (p is null) return;

        EnsureFolder(p.AssetsPath);
        EnsureFolder(p.ScenesPath);
        EnsureFolder(p.PackagesPath);
        EnsureFolder(p.BuildsPath);

        Roots.Add(LoadDirAsRoot(p.AssetsPath, "Assets"));
        Roots.Add(LoadDirAsRoot(p.ScenesPath, "Scenes"));
        Roots.Add(LoadDirAsRoot(p.PackagesPath, "Packages"));
        Roots.Add(LoadDirAsRoot(p.BuildsPath, "Builds"));
    }

    private static void EnsureFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
    }

    private ProjectNode LoadDirAsRoot(string dir, string display) => LoadDirRecursive(dir, display, parent: null);

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

        var name = await AskText("New Scene", "Enter scene file name:", "Main.scene.json");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name += ".json";

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
                var data = new DataObject();

                // keep your internal key (used for tree-to-tree moves)
                data.Set("project-node-path", path);

                // ALSO provide standard FileNames when it’s a file
                if (File.Exists(path))
                    data.Set(DataFormats.FileNames, new[] { path });

                // IMPORTANT: allow both Copy and Move so external targets (Inspector) can accept it
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move);
                _pressNode = null;
            }
        }
    }


    private void OnTreeDragOver(object? s, DragEventArgs e)
    {
        var target = NodeFromObject(e.Source);
        var targetFolder = GetDropFolder(target);
        if (targetFolder is null) { e.DragEffects = DragDropEffects.None; return; }

        // Internal move?
        if (e.Data.Contains("project-node-path"))
        {
            var src = e.Data.Get("project-node-path") as string;
            if (string.IsNullOrWhiteSpace(src)) { e.DragEffects = DragDropEffects.None; return; }

            // cannot drop into itself or descendant
            if (IsSameOrDescendant(src, targetFolder.FullPath))
                e.DragEffects = DragDropEffects.None;
            else
                e.DragEffects = DragDropEffects.Move;

            e.Handled = true;
            return;
        }

        // OS files drop
        if (e.Data.Contains(DataFormats.FileNames))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private async void OnTreeDrop(object? s, DragEventArgs e)
    {
        var target = NodeFromObject(e.Source);
        var targetFolder = GetDropFolder(target);
        if (targetFolder is null) return;

        // Internal move
        if (e.Data.Contains("project-node-path"))
        {
            var src = e.Data.Get("project-node-path") as string;
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
        if (e.Data.Contains(DataFormats.FileNames))
        {
            var files = e.Data.GetFileNames();
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
