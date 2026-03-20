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
    private enum UnsavedChoice { Save, DontSave, Cancel }

    private DockManager? _dock;
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _autosaveMissingPathLogged;

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
            [typeof(TimelineSequencerPanel)] = ("Timeline", DockRegion.Bottom, () => new TimelineSequencerPanel()),
            [typeof(ProfilerPanel)] = ("Profiler", DockRegion.Bottom, () => new ProfilerPanel()),
            [typeof(ShaderEditorPanel)] = ("Shader Editor", DockRegion.Center, () => new ShaderEditorPanel()),
            [typeof(BiomeGraphPanel)] = ("Biome Graph", DockRegion.Center, () => new BiomeGraphPanel()),
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
        BindNew("NewTimelineTab", typeof(TimelineSequencerPanel), DockRegion.Bottom);
        BindNew("NewGameTab", typeof(GamePanel), DockRegion.Center);
        BindNew("NewProfilerTab", typeof(ProfilerPanel), DockRegion.Bottom);
        BindNew("NewShaderEditorTab", typeof(ShaderEditorPanel), DockRegion.Center);
        BindNew("NewBiomeGraphTab", typeof(BiomeGraphPanel), DockRegion.Center);

        if (this.FindControl<MenuItem>("InputRemappingMenu") is { } settings)
            settings.Click += (_, __) => InputRemappingAsync();

        // ----- Project menu (items are named in XAML) -----
        MI_NewProject.Click += OnNewProject;
        MI_OpenProject.Click += OnOpenProject;
        MI_BuildSettings.Click += (_, __) => OpenBuildSettings();
        MI_ValidateProject.Click += (_, __) => RunProjectValidation();
        MI_AutosaveEnabled.Click += (_, __) => ToggleAutosave();
        MI_Autosave1.Click += (_, __) => SetAutosaveInterval(1);
        MI_Autosave5.Click += (_, __) => SetAutosaveInterval(5);
        MI_Autosave10.Click += (_, __) => SetAutosaveInterval(10);
        MI_CloseProject.Click += async (_, __) =>
        {
            if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
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
        RebuildRecentProjectsMenu();
        _autosaveTimer.Tick += async (_, __) => await TryAutosaveTickAsync();
        _autosaveTimer.Start();
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

    private async void OpenBuildSettings()
    {
        var dlg = new BuildSettingsWindow();
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
        MI_SaveScene.IsEnabled = has;
        MI_LoadScene.IsEnabled = has;
        MI_BuildSettings.IsEnabled = has;
        MI_ValidateProject.IsEnabled = has;
        MI_AutosaveRoot.IsEnabled = has;

        Title = ProjectService.Current is { } p
            ? $"{p.Name} — Game Engine"
            : "Game Engine";

        if (!has) return;

        var proj = ProjectService.Current!;
        MI_AutosaveEnabled.Header = proj.AutosaveEnabled ? "_Disable Autosave" : "_Enable Autosave";
        MI_AutosaveEnabled.IsChecked = proj.AutosaveEnabled;
        MI_Autosave1.IsChecked = proj.AutosaveIntervalMinutes == 1;
        MI_Autosave5.IsChecked = proj.AutosaveIntervalMinutes == 5;
        MI_Autosave10.IsChecked = proj.AutosaveIntervalMinutes == 10;
        RebuildRecentProjectsMenu();
    }

    private async void OnNewProject(object? s, RoutedEventArgs e)
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;

        var parentDlg = new OpenFolderDialog { Title = "Choose parent folder for new project" };
        var parent = await parentDlg.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(parent)) return;

        var nameDlg = new ProjectNameDialog { Title = "New Project" };
        var name = await nameDlg.ShowDialog<string?>(this);   // <-- ShowDialog<T>
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            ProjectService.CreateNew(parent, name, openAfterCreate: true);
            if (ProjectService.Current is { } opened)
                RecentProjectsStore.AddRecent(opened.ManifestPath);
            RefreshProjectUI();
            ExtensionService.RefreshFromAppDomain();
            RebuildExtensionMenus();
            SceneService.SetCurrentScenePath(null);
            SceneService.SetDirty(false);
            TryAutoLoadLastOpenedScene();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to create project:\n{ex.Message}");
        }
    }

    private async void OnOpenProject(object? s, RoutedEventArgs e)
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;

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
            if (ProjectService.Current is { } opened)
                RecentProjectsStore.AddRecent(opened.ManifestPath);
            RefreshProjectUI();
            ExtensionService.RefreshFromAppDomain();
            RebuildExtensionMenus();
            SceneService.SetCurrentScenePath(null);
            SceneService.SetDirty(false);
            TryAutoLoadLastOpenedScene();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open project:\n{ex.Message}");
        }
    }


    
    private async void OnMenuSaveScene_Click(object? sender, RoutedEventArgs e)
    {
        await SaveSceneAsync(forceSaveAsDialog: true);
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
            if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
            SceneService.LoadFromFile(path);
            ProjectService.RememberLastOpenedScene(path);
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

    private void TryAutoLoadLastOpenedScene()
    {
        var lastScenePath = ProjectService.GetLastOpenedSceneAbsolutePath();
        if (string.IsNullOrWhiteSpace(lastScenePath)) return;

        try
        {
            SceneService.LoadFromFile(lastScenePath);
            Log.Info($"Auto-loaded last scene: {lastScenePath}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to auto-load last scene '{lastScenePath}': {ex.Message}");
        }
    }

    private async Task<bool> SaveSceneAsync(bool forceSaveAsDialog)
    {
        string? path = forceSaveAsDialog ? null : SceneService.CurrentScenePath;

        if (string.IsNullOrWhiteSpace(path))
        {
            var dlg = new SaveFileDialog
            {
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Scene", Extensions = { "scene" } }
                },
                DefaultExtension = "scene"
            };

            path = await dlg.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(path)) return false;
        }

        SceneService.SaveToFile(path);
        ProjectService.RememberLastOpenedScene(path);
        Log.Info($"Scene saved: {path}");
        return true;
    }

    private void ToggleAutosave()
    {
        if (ProjectService.Current is not { } proj) return;
        ProjectService.UpdateAutosaveSettings(!proj.AutosaveEnabled, proj.AutosaveIntervalMinutes);
        ProjectService.ReloadCurrentFromManifest();
        RefreshProjectUI();
    }

    private void SetAutosaveInterval(int minutes)
    {
        if (ProjectService.Current is not { } proj) return;
        ProjectService.UpdateAutosaveSettings(proj.AutosaveEnabled, minutes);
        ProjectService.ReloadCurrentFromManifest();
        RefreshProjectUI();
    }

    private async Task TryAutosaveTickAsync()
    {
        var proj = ProjectService.Current;
        if (proj is null || !proj.AutosaveEnabled || !SceneService.IsDirty) return;

        var interval = TimeSpan.FromMinutes(Math.Clamp(proj.AutosaveIntervalMinutes, 1, 60));
        var sinceModified = DateTime.UtcNow - proj.ModifiedUtc;
        if (sinceModified < interval) return;

        if (string.IsNullOrWhiteSpace(SceneService.CurrentScenePath))
        {
            if (!_autosaveMissingPathLogged)
            {
                Log.Warning("Autosave skipped: scene has no save path yet. Save once manually to enable autosave.");
                _autosaveMissingPathLogged = true;
            }
            return;
        }

        try
        {
            await SaveSceneAsync(forceSaveAsDialog: false);
            _autosaveMissingPathLogged = false;
            Log.Info($"Autosaved scene ({proj.AutosaveIntervalMinutes} min interval).");
        }
        catch (Exception ex)
        {
            Log.Warning($"Autosave failed: {ex.Message}");
        }
    }

    private async Task<bool> EnsureSafeToLoseUnsavedSceneAsync()
    {
        if (!SceneService.IsDirty) return true;

        var choice = await ShowUnsavedChangesPromptAsync();
        if (choice == UnsavedChoice.Cancel) return false;
        if (choice == UnsavedChoice.DontSave) return true;

        return await SaveSceneAsync(forceSaveAsDialog: false);
    }

    private async Task<UnsavedChoice> ShowUnsavedChangesPromptAsync()
    {
        var tcs = new TaskCompletionSource<UnsavedChoice>();
        var msg = "You have unsaved scene changes.\nSave before continuing?";

        var btnSave = new Button { Content = "Save", MinWidth = 90 };
        var btnDontSave = new Button { Content = "Don't Save", MinWidth = 90 };
        var btnCancel = new Button { Content = "Cancel", MinWidth = 90 };

        var buttonBar = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { btnSave, btnDontSave, btnCancel }
        };

        var dlg = new Window
        {
            Width = 460,
            Height = 170,
            Title = "Unsaved Changes",
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap },
                    buttonBar
                }
            }
        };

        btnSave.Click += (_, __) => { tcs.TrySetResult(UnsavedChoice.Save); dlg.Close(); };
        btnDontSave.Click += (_, __) => { tcs.TrySetResult(UnsavedChoice.DontSave); dlg.Close(); };
        btnCancel.Click += (_, __) => { tcs.TrySetResult(UnsavedChoice.Cancel); dlg.Close(); };
        dlg.Closed += (_, __) => tcs.TrySetResult(UnsavedChoice.Cancel);

        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    private void RebuildRecentProjectsMenu()
    {
        if (MI_RecentRoot == null) return;
        MI_RecentRoot.Items.Clear();

        var currentManifest = ProjectService.Current?.ManifestPath;
        if (!string.IsNullOrWhiteSpace(currentManifest))
        {
            var pinCurrent = new MenuItem
            {
                Header = RecentProjectsStore.IsPinned(currentManifest)
                    ? "Unpin Current Project"
                    : "Pin Current Project"
            };
            pinCurrent.Click += (_, __) =>
            {
                RecentProjectsStore.TogglePinned(currentManifest);
                RebuildRecentProjectsMenu();
            };
            MI_RecentRoot.Items.Add(pinCurrent);
            MI_RecentRoot.Items.Add(new Separator());
        }

        var pinned = RecentProjectsStore.GetPinned();
        foreach (var manifestPath in pinned)
            MI_RecentRoot.Items.Add(MakeRecentMenuItem(manifestPath, pinnedItem: true));

        if (pinned.Count > 0) MI_RecentRoot.Items.Add(new Separator());

        var recents = RecentProjectsStore.GetRecents();
        foreach (var manifestPath in recents)
        {
            if (pinned.Any(p => string.Equals(p, manifestPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            MI_RecentRoot.Items.Add(MakeRecentMenuItem(manifestPath, pinnedItem: false));
        }

        if (MI_RecentRoot.Items.Count == 0)
            MI_RecentRoot.Items.Add(new MenuItem { Header = "(No recent projects)", IsEnabled = false });
    }

    private MenuItem MakeRecentMenuItem(string manifestPath, bool pinnedItem)
    {
        var headerPrefix = pinnedItem ? "★ " : "";
        var mi = new MenuItem { Header = $"{headerPrefix}{manifestPath}" };

        mi.Click += async (_, __) => await OpenProjectByManifestPathAsync(manifestPath);
        return mi;
    }

    private async Task OpenProjectByManifestPathAsync(string manifestPath)
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;

        try
        {
            ProjectService.Open(manifestPath);
            if (ProjectService.Current is { } opened)
                RecentProjectsStore.AddRecent(opened.ManifestPath);

            RefreshProjectUI();
            ExtensionService.RefreshFromAppDomain();
            RebuildExtensionMenus();
            SceneService.SetCurrentScenePath(null);
            SceneService.SetDirty(false);
            TryAutoLoadLastOpenedScene();
            RebuildRecentProjectsMenu();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open recent project:\n{ex.Message}");
        }
    }

    private void RunProjectValidation()
    {
        var issues = ProjectValidator.ValidateCurrentProject();
        if (issues.Count == 0)
        {
            Log.Success("Project validation passed. No missing references found.");
            return;
        }

        Log.Warning($"Project validation found {issues.Count} issue(s):");
        foreach (var issue in issues)
            Log.Warning("  - " + issue);
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
