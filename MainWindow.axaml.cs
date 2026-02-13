using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.Input;
using Game_Engine.Docking;
using Game_Engine.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Game_Engine;

public partial class MainWindow : Window
{
    private DockManager? _dock;

    // Tracks how many of each tab we’ve created (for numbering)
    private readonly Dictionary<string, int> _counts = new();

    // Map panel type -> (Base title, default region, factory)
    private Dictionary<Type, (string Base, DockRegion Region, Func<Control> Factory)> _registry = null!;

    public MainWindow()
    {
        InitializeComponent();

        _dock = new DockManager(LeftTabs, CenterTabs, RightTabs, BottomLeftTabs, BottomTabs);

        // Register panels
        _registry = new()
        {
            [typeof(HierarchyPanel)] = ("Hierarchy", DockRegion.Left, () => new HierarchyPanel()),
            [typeof(ScenePanel)] = ("Scene", DockRegion.Center, () => new ScenePanel()),
            [typeof(InspectorPanel)] = ("Inspector", DockRegion.Right, () => new InspectorPanel()),
            [typeof(ProjectPanel)] = ("Project", DockRegion.BottomLeft, () => new ProjectPanel()),
            [typeof(ConsolePanel)] = ("Console", DockRegion.Bottom, () => new ConsolePanel()),
            [typeof(GamePanel)] = ("Game", DockRegion.Center, () => new GamePanel()),
            [typeof(AnimationPanel)] = ("Animation", DockRegion.Bottom, () => new AnimationPanel()),
        };

        // Defaults
        _counts.Clear();
        AddInitialPanels();

        // Context menus for initial tabs
        AddTabMenus(LeftTabs);
        AddTabMenus(CenterTabs);
        AddTabMenus(RightTabs);
        AddTabMenus(BottomLeftTabs);
        AddTabMenus(BottomTabs);


        // Window ▸ Reset Layout
        if (this.FindControl<MenuItem>("ResetLayoutMenu") is { } reset)
            reset.Click += (_, __) => ResetLayout();

        // Window ▸ New …
        void BindNew(string name, Type t, DockRegion r)
        {
            if (this.FindControl<MenuItem>(name) is { } mi)
                mi.Click += (_, __) => AddPanel(t, r);
        }
        BindNew("NewSceneTab", typeof(ScenePanel), DockRegion.Center);
        BindNew("NewInspectorTab", typeof(InspectorPanel), DockRegion.Right);
        BindNew("NewHierarchyTab", typeof(HierarchyPanel), DockRegion.Left);
        BindNew("NewProjectTab", typeof(ProjectPanel), DockRegion.BottomLeft);
        BindNew("NewConsoleTab", typeof(ConsolePanel), DockRegion.Bottom);
        BindNew("NewAnimationTab", typeof(AnimationPanel), DockRegion.Bottom);
        BindNew("NewGameTab", typeof(GamePanel), DockRegion.Center);

        if (this.FindControl<MenuItem>("InputRemappingMenu") is { } settings)
            settings.Click += (_, __) => InputRemappingAsync();

        // ----- Project menu (items are named in XAML) -----
        MI_NewProject.Click += OnNewProject;
        MI_OpenProject.Click += OnOpenProject;
        MI_OpenFolder.Click += OnOpenFolder;
        MI_CloseProject.Click += (_, __) =>
        {
            ProjectService.Close();
            RefreshProjectUI();

            // Remove all extension menus + instances, then rebuild top bar to only Project/Window.
            ExtensionService.Clear();
            RebuildExtensionMenus();
        };
        MI_RevealInExplorer.Click += (_, __) => RevealInExplorer();

        // Project lifecycle → refresh title/enablement
        ProjectService.ProjectOpened += RefreshProjectUI;
        ProjectService.ProjectClosed += RefreshProjectUI;
        ProjectService.Changed += RefreshProjectUI;

        // --- Extensions + Menus wiring ---

        // Seal built-in commands once (so extension commands can be cleared safely)
        CommandRegistry.SealBuiltins();

        // Subscribe exactly once to extension changes → rebuild menu bar
        ExtensionService.Changed -= OnExtensionsChanged;
        ExtensionService.Changed += OnExtensionsChanged;

        // Initial populate: scan current AppDomain for extensions; event will rebuild menus
        ExtensionService.RefreshFromAppDomain();

        // Also build once now so the bar is correct even if no extensions are found
        RebuildExtensionMenus();

        // Keep menus in sync when a project opens/closes (event will rebuild)
        ProjectService.ProjectOpened += () => ExtensionService.RefreshFromAppDomain();
        ProjectService.ProjectClosed += () => ExtensionService.RefreshFromAppDomain();

        // Final: window title etc.
        RefreshProjectUI();
    }



    // ---------- layout / tabs ----------
    public void RebuildExtensionMenus()
    {
        var host = this.FindControl<Menu>("MainMenu");
        if (host == null) { Log.Warning("[UI] RebuildExtensionMenus: MainMenu not found"); return; }

        var projectRoot = this.FindControl<MenuItem>("MI_ProjectRoot");
        var windowRoot = this.FindControl<MenuItem>("MI_WindowRoot");
        var SettingsRoot = this.FindControl<MenuItem>("MI_SettingsRoot");

        //  Log.Info($"[UI] RebuildExtensionMenus: clearing old menu (was count={host.Items?.Count ?? 0})");
        host.Items.Clear();

        if (projectRoot != null) host.Items.Add(projectRoot);
        if (windowRoot != null) host.Items.Add(windowRoot);
        if (SettingsRoot != null) host.Items.Add(SettingsRoot);

        var customs = ExtensionService.BuildAvaloniaMenus();
        for (int i = 0; i < customs.Count; i++) host.Items.Add(customs[i]);

      //  Log.Info($"[UI] RebuildExtensionMenus: rebuilt total count={host.Items.Count} (project+window+customs={2 + customs.Count})");

        // dump what’s there
        //DumpMenu(host);
    }

    private static void DumpMenu(Menu host)
    {
        int i = 0;
        foreach (var it in host.Items)
        {
            var head = (it as MenuItem)?.Header?.ToString() ?? it?.ToString() ?? "<null>";
            Log.Info($"[UI] Menu[{i++}] = {head}");
        }
    }

    private void OnExtensionsChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(RebuildExtensionMenus);
        else
            RebuildExtensionMenus();
    }





    private void AddInitialPanels()
    {
        AddPanel(typeof(HierarchyPanel));
        AddPanel(typeof(ScenePanel));
        AddPanel(typeof(InspectorPanel));
        AddPanel(typeof(ProjectPanel));
        AddPanel(typeof(ConsolePanel));
        AddPanel(typeof(GamePanel));
        AddPanel(typeof(AnimationPanel));
    }

    private string NextTitle(string baseTitle)
    {
        if (!_counts.TryGetValue(baseTitle, out var n)) n = 0;
        n++;
        _counts[baseTitle] = n;
        return n == 1 ? baseTitle : $"{baseTitle} {n}";
    }

    private Control AddPanel(Type panelType, DockRegion? regionOverride = null)
    {
        var (baseTitle, defaultRegion, factory) = _registry[panelType];
        var ctrl = factory();
        var title = NextTitle(baseTitle);
        var region = regionOverride ?? defaultRegion;
        _dock!.Add(ctrl, title, region);

        AddTabMenus(HostOf(region));
        return ctrl;
    }

    private TabControl HostOf(DockRegion r) => r switch
    {
        DockRegion.Left => LeftTabs,
        DockRegion.Center => CenterTabs,
        DockRegion.Right => RightTabs,
        DockRegion.BottomLeft => BottomLeftTabs,
        _ => BottomTabs
    };

    private DockRegion RegionOfHost(TabControl host)
    {
        if (host == LeftTabs) return DockRegion.Left;
        if (host == CenterTabs) return DockRegion.Center;
        if (host == RightTabs) return DockRegion.Right;
        if (host == BottomLeftTabs) return DockRegion.BottomLeft;
        return DockRegion.Bottom;
    }

    private async Task InputRemappingAsync()
    {
        var dlg = new InputRemappingWindow();
        await dlg.ShowDialog(this);
    }

    private void ResetLayout()
    {
        if (_dock is null) return;

        LeftTabs.Items.Clear();
        CenterTabs.Items.Clear();
        RightTabs.Items.Clear();
        BottomLeftTabs.Items.Clear();
        BottomTabs.Items.Clear();

        _dock = new DockManager(LeftTabs, CenterTabs, RightTabs, BottomLeftTabs, BottomTabs);
        _counts.Clear();
        AddInitialPanels();

        AddTabMenus(LeftTabs);
        AddTabMenus(CenterTabs);
        AddTabMenus(RightTabs);
        AddTabMenus(BottomLeftTabs);
        AddTabMenus(BottomTabs);
    }

    private void AddTabMenus(TabControl tc)
    {
        foreach (var obj in tc.Items)
        {
            if (obj is not TabItem tab || tab.ContextMenu != null) continue;
            if (tab.Content is not Control content || _dock is null) continue;

            var t = content.GetType();
            _registry.TryGetValue(t, out var info);
            var hostRegion = RegionOfHost(tc);

            var items = new List<object>();
            if (info.Factory is not null)
            {
                items.Add(Make($"New {info.Base} Tab", () => AddPanel(t, hostRegion)));
                items.Add(new Separator());
            }

            items.Add(Make("_Close", () => _dock!.Close(content)));
            items.Add(new Separator());
            items.Add(Make("_Float", () => _dock!.Float(content)));
            items.Add(new Separator());
            items.Add(Make("Dock _Left", () => _dock!.DockTo(content, DockRegion.Left)));
            items.Add(Make("Dock _Center", () => _dock!.DockTo(content, DockRegion.Center)));
            items.Add(Make("Dock _Right", () => _dock!.DockTo(content, DockRegion.Right)));
            items.Add(Make("Dock _Bottom Left", () => _dock!.DockTo(content, DockRegion.BottomLeft)));
            items.Add(Make("Dock _Bottom", () => _dock!.DockTo(content, DockRegion.Bottom)));

            tab.ContextMenu = new ContextMenu { ItemsSource = items };
        }

        static MenuItem Make(string header, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, __) => onClick();
            return mi;
        }
    }

    // ---------- Project menu helpers ----------

    public void RefreshProjectUI()
    {
        var has = ProjectService.Current is not null;
        ProjectService.ProjectOpened += () => Input.TryLoadBindingsFromProject();

        MI_CloseProject.IsEnabled = has;
        MI_RevealInExplorer.IsEnabled = has;

        Title = ProjectService.Current is { } p
            ? $"{p.Name} — Game Engine"
            : "Game Engine";
    }

    private async void OnNewProject(object? s, RoutedEventArgs e)
    {
        var parentDlg = new OpenFolderDialog { Title = "Choose parent folder for new project" };
        var parent = await parentDlg.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(parent)) return;

        var nameDlg = new ProjectNameDialog { Title = "New Project" };
        var name = await nameDlg.ShowDialog<string?>(this);   // <-- ShowDialog<T>
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            ProjectService.CreateNew(parent, name, openAfterCreate: true);
            RefreshProjectUI();
            ExtensionService.RefreshFromAppDomain();
            RebuildExtensionMenus();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to create project:\n{ex.Message}");
        }
    }

    private async void OnOpenProject(object? s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            AllowMultiple = false,
            Title = "Open project.json",
            Filters = { new FileDialogFilter { Name = "Project", Extensions = { "json" } } }
        };
        var files = await dlg.ShowAsync(this);
        if (files is not { Length: > 0 }) return;

        try
        {
            ProjectService.Open(files[0]);
            RefreshProjectUI();
            ExtensionService.RefreshFromAppDomain();
            RebuildExtensionMenus();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open project:\n{ex.Message}");
        }
    }

    private async void OnOpenFolder(object? s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Open project folder (contains project.json)" };
        var folder = await dlg.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            ProjectService.Open(folder);
            RefreshProjectUI();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open project:\n{ex.Message}");
        }
    }

    
    private async void OnMenuSaveScene_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filters = new List<FileDialogFilter>
        {
            new FileDialogFilter { Name = "Scene", Extensions = { "scene" } }
        },
            DefaultExtension = "scene"
        };

        var path = await dlg.ShowAsync(this);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SceneService.SaveToFile(path);
            Log.Info($"Scene saved: {path}");
        }
    }

    
    private async void OnMenuLoadScene_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters = new List<FileDialogFilter>
        {
            new FileDialogFilter { Name = "Scene", Extensions = { "scene" } }
        }
        };

        var result = await dlg.ShowAsync(this);
        var path = result?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SceneService.LoadFromFile(path);
            Log.Info($"Scene loaded: {path}");
        }
    }


    private void RevealInExplorer()
    {
        var proj = ProjectService.Current;
        if (proj is null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = proj.RootPath,
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    // super-lightweight error popup
    private async System.Threading.Tasks.Task ShowError(string message)
    {
        var dlg = new Window
        {
            Width = 420,
            Height = 180,
            Title = "Error",
            Content = new TextBlock
            {
                Text = message,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap
            }
        };
        await dlg.ShowDialog(this);
    }
}
