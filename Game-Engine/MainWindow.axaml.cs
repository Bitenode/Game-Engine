using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Editor;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.Input;
using Game_Engine.Docking;
using Game_Engine.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Game_Engine;

public partial class MainWindow : Window
{
    private enum UnsavedChoice { Save, DontSave, Cancel }

    private bool _closingAfterUnsavedOk;
    private IBrush? _windowBgBeforePlay;
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

        Opened += (_, __) => EditorJobScheduler.AttachDispatcher(Dispatcher.UIThread);

        _dock = new DockManager(LeftTabs, CenterTabs, CenterGameTabs, RightTabs, BottomLeftTabs, BottomTabs);

        // Register panels
        _registry = new()
        {
            [typeof(HierarchyPanel)] = ("Hierarchy", DockRegion.Left, () => new HierarchyPanel()),
            [typeof(ScenePanel)] = ("Scene", DockRegion.Center, () => new ScenePanel()),
            [typeof(InspectorPanel)] = ("Inspector", DockRegion.Right, () => new InspectorPanel()),
            [typeof(ProjectPanel)] = ("Project", DockRegion.BottomLeft, () => new ProjectPanel()),
            [typeof(ConsolePanel)] = ("Console", DockRegion.Bottom, () => new ConsolePanel()),
            [typeof(GamePanel)] = ("Game", DockRegion.CenterSecondary, () => new GamePanel()),
            [typeof(AnimationPanel)] = ("Animation", DockRegion.Bottom, () => new AnimationPanel()),
            [typeof(TimelineSequencerPanel)] = ("Timeline", DockRegion.Bottom, () => new TimelineSequencerPanel()),
            [typeof(ProfilerPanel)] = ("Profiler", DockRegion.Bottom, () => new ProfilerPanel()),
            [typeof(ShaderEditorPanel)] = ("Shader Editor", DockRegion.Center, () => new ShaderEditorPanel()),
            [typeof(BiomeGraphPanel)] = ("Biome Graph", DockRegion.Center, () => new BiomeGraphPanel()),
            [typeof(BlueprintGraphPanel)] = ("Blueprint", DockRegion.Center, () => new BlueprintGraphPanel()),
        };

        // Defaults
        _counts.Clear();
        AddInitialPanels();

        // Context menus for initial tabs
        AddTabMenus(LeftTabs);
        AddTabMenus(CenterTabs);
        AddTabMenus(CenterGameTabs);
        AddTabMenus(RightTabs);
        AddTabMenus(BottomLeftTabs);
        AddTabMenus(BottomTabs);


        // Window ▸ Reset Layout
        if (this.FindControl<MenuItem>("ResetLayoutMenu") is { } reset)
            reset.Click += (_, __) => ResetLayout();

        if (this.FindControl<MenuItem>("LayoutSave1") is { } ls1) ls1.Click += (_, __) => SaveLayoutPreset(1);
        if (this.FindControl<MenuItem>("LayoutSave2") is { } ls2) ls2.Click += (_, __) => SaveLayoutPreset(2);
        if (this.FindControl<MenuItem>("LayoutSave3") is { } ls3) ls3.Click += (_, __) => SaveLayoutPreset(3);
        if (this.FindControl<MenuItem>("LayoutLoad1") is { } ll1) ll1.Click += (_, __) => LoadLayoutPreset(1);
        if (this.FindControl<MenuItem>("LayoutLoad2") is { } ll2) ll2.Click += (_, __) => LoadLayoutPreset(2);
        if (this.FindControl<MenuItem>("LayoutLoad3") is { } ll3) ll3.Click += (_, __) => LoadLayoutPreset(3);

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
        BindNew("NewGameTab", typeof(GamePanel), DockRegion.CenterSecondary);
        BindNew("NewProfilerTab", typeof(ProfilerPanel), DockRegion.Bottom);
        BindNew("NewShaderEditorTab", typeof(ShaderEditorPanel), DockRegion.Center);
        BindNew("NewBiomeGraphTab", typeof(BiomeGraphPanel), DockRegion.Center);
        BindNew("NewBlueprintTab", typeof(BlueprintGraphPanel), DockRegion.Center);

        if (this.FindControl<MenuItem>("InputRemappingMenu") is { } settings)
            settings.Click += (_, __) => InputRemappingAsync();

        if (this.FindControl<MenuItem>("MI_ClearConsoleOnPlay") is { } ccPlay)
        {
            ccPlay.IsChecked = EditorSettings.ClearConsoleOnPlay;
            ccPlay.Click += (_, __) =>
            {
                EditorSettings.ClearConsoleOnPlay = ccPlay.IsChecked == true;
                EditorSettings.Save();
            };
        }

        if (this.FindControl<MenuItem>("MI_ScriptLineNumbers") is { } ln)
        {
            ln.IsChecked = EditorSettings.ScriptEditorShowLineNumbers;
            ln.Click += (_, __) =>
            {
                EditorSettings.ScriptEditorShowLineNumbers = ln.IsChecked == true;
                EditorSettings.Save();
            };
        }

        if (this.FindControl<MenuItem>("MI_ScriptWordWrap") is { } ww)
        {
            ww.IsChecked = EditorSettings.ScriptEditorWordWrap;
            ww.Click += (_, __) =>
            {
                EditorSettings.ScriptEditorWordWrap = ww.IsChecked == true;
                EditorSettings.Save();
            };
        }

        Game_Engine.Views.GameView.AnyPlayingStateChanged += OnGlobalPlayStateChanged;

        // ----- Project menu (items are named in XAML) -----
        MI_NewProject.Click += OnNewProject;
        MI_OpenProject.Click += OnOpenProject;
        MI_WelcomeHub.Click += (_, __) => _ = ShowWelcomeHubAsync();
        MI_BuildSettings.Click += (_, __) => _ = OpenBuildSettingsAsync();
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
        ProjectService.ProjectOpened += TryRestoreProjectDockLayout;
        Closing += OnMainWindowClosing;
        Closing += (_, __) => SaveProjectDockLayout();
        Closed += (_, __) => _closingAfterUnsavedOk = false;
        SceneService.DirtyStateChanged += _ => UpdateEditorWindowTitle();

        // --- Extensions + Menus wiring ---

        RegisterEditorCommands();
        AddHandler(KeyDownEvent, OnEditorShortcutKeyDown, RoutingStrategies.Tunnel);

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

        Opened += OnFirstOpenedForWelcomeHub;
    }

    private async void OnFirstOpenedForWelcomeHub(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpenedForWelcomeHub;
        if (!EditorSettings.ShowWelcomeDialogOnStartup) return;
        var w = new WelcomeWindow(this);
        await w.ShowDialog(this);
    }

    /// <summary>Opens the project hub modal (Project menu). Does not change the auto-show setting.</summary>
    public async Task ShowWelcomeHubAsync()
    {
        var w = new WelcomeWindow(this);
        await w.ShowDialog(this);
    }

    private void RegisterEditorCommands()
    {
        bool HasProject() => ProjectService.Current is not null;

        CommandRegistry.Register("editor.commandPalette", "Editor: Command Palette", ShowCommandPalette, () => true);
        CommandRegistry.Register("editor.quickOpen", "Editor: Quick Open", ShowQuickOpen, HasProject);

        CommandRegistry.Register("editor.tab.scene", "Window: New Scene View Tab", () => AddPanel(typeof(ScenePanel)));
        CommandRegistry.Register("editor.tab.game", "Window: New Game View Tab", () => AddPanel(typeof(GamePanel), DockRegion.CenterSecondary));
        CommandRegistry.Register("editor.tab.inspector", "Window: New Inspector Tab", () => AddPanel(typeof(InspectorPanel)));
        CommandRegistry.Register("editor.tab.hierarchy", "Window: New Hierarchy Tab", () => AddPanel(typeof(HierarchyPanel)));
        CommandRegistry.Register("editor.tab.project", "Window: New Project Tab", () => AddPanel(typeof(ProjectPanel)));
        CommandRegistry.Register("editor.tab.console", "Window: New Console Tab", () => AddPanel(typeof(ConsolePanel)));
        CommandRegistry.Register("editor.tab.animation", "Window: New Animation Tab", () => AddPanel(typeof(AnimationPanel)));
        CommandRegistry.Register("editor.tab.timeline", "Window: New Timeline Tab", () => AddPanel(typeof(TimelineSequencerPanel)));
        CommandRegistry.Register("editor.tab.profiler", "Window: New Profiler Tab", () => AddPanel(typeof(ProfilerPanel)));
        CommandRegistry.Register("editor.tab.shader", "Window: New Shader Editor Tab", () => AddPanel(typeof(ShaderEditorPanel)));
        CommandRegistry.Register("editor.tab.biome", "Window: New Biome Graph Tab", () => AddPanel(typeof(BiomeGraphPanel)));
        CommandRegistry.Register("editor.tab.blueprint", "Window: New Blueprint Tab", () => AddPanel(typeof(BlueprintGraphPanel)));

        CommandRegistry.Register("editor.layout.reset", "Window: Reset Layout", () => ResetLayout());

        CommandRegistry.Register("editor.project.saveScene", "Project: Save Scene", () => _ = SaveSceneAsync(false), HasProject);
        CommandRegistry.Register("editor.project.loadScene", "Project: Load Scene…", () => _ = PromptLoadSceneAsync(), HasProject);
        CommandRegistry.Register("editor.project.revealExplorer", "Project: Reveal in Explorer", RevealInExplorer, HasProject);
        CommandRegistry.Register("editor.project.buildSettings", "Project: Build Settings…", () => _ = OpenBuildSettingsAsync(), HasProject);
        CommandRegistry.Register("editor.project.validate", "Project: Validate Project", RunProjectValidation, HasProject);
        CommandRegistry.Register("editor.project.new", "Project: New Project…", () => OnNewProject(this, new RoutedEventArgs()));
        CommandRegistry.Register("editor.project.open", "Project: Open Project…", () => OnOpenProject(this, new RoutedEventArgs()));
        CommandRegistry.Register("editor.project.welcomeHub", "Project: Project hub…", () => _ = ShowWelcomeHubAsync(), () => true);
        CommandRegistry.Register("editor.project.close", "Project: Close Project", () => _ = CloseProjectFromPaletteAsync(), HasProject);

        CommandRegistry.Register("editor.settings.input", "Settings: Input Remapping…", () => _ = InputRemappingAsync());

        CommandRegistry.Register("editor.scripts.compile", "Scripts: Compile and Reload Extensions", () => _ = CompileScriptsFromPaletteAsync(), HasProject);
        CommandRegistry.Register("editor.revealInProject", "Project: Reveal Selection in Project Panel", RevealInProjectForSelection, HasProject);
        CommandRegistry.Register("editor.game.togglePlay", "Game: Toggle Play / Stop", TogglePlayStopShortcut, HasProject);
    }

    private void OnGlobalPlayStateChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnGlobalPlayStateChanged);
            return;
        }

        var playing = Game_Engine.Views.GameView.IsAnyViewPlaying;
        if (playing)
        {
            if (_windowBgBeforePlay == null)
                _windowBgBeforePlay = Background;
            Background = new SolidColorBrush(Color.Parse("#3A2828"));
            if (EditorSettings.ClearConsoleOnPlay)
                ConsolePanel.ClearAllPanels();
        }
        else
        {
            Background = _windowBgBeforePlay;
            _windowBgBeforePlay = null;
        }

        RefreshProjectUI();
    }

    private void UpdateEditorWindowTitle()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateEditorWindowTitle);
            return;
        }

        var playing = Game_Engine.Views.GameView.IsAnyViewPlaying;
        var dirty = SceneService.IsDirty && ProjectService.Current is not null;
        var core = ProjectService.Current is { } p
            ? $"{p.Name} — Game Engine"
            : "Game Engine";
        var prefix = playing ? "▶ " : "";
        var dirtyMark = dirty ? "* " : "";
        Title = $"{prefix}{dirtyMark}{core}";
    }

    private void TogglePlayStopShortcut() => TryToggleFirstGamePanelPlay();

    /// <summary>Stops all playing Game panels, or starts play on the first Game panel in dock order.</summary>
    private void TryToggleFirstGamePanelPlay()
    {
        if (ProjectService.Current is null) return;
        var panels = EnumerateGamePanels().ToList();
        if (panels.Count == 0) return;

        if (Game_Engine.Views.GameView.IsAnyViewPlaying)
        {
            foreach (var p in panels)
            {
                if (p.State != GamePanel.GameState.Stopped)
                    p.State = GamePanel.GameState.Stopped;
            }
            return;
        }

        panels[0].State = GamePanel.GameState.Playing;
    }

    private IEnumerable<GamePanel> EnumerateGamePanels()
    {
        foreach (var gp in GamePanelsIn(LeftTabs)) yield return gp;
        foreach (var gp in GamePanelsIn(CenterTabs)) yield return gp;
        foreach (var gp in GamePanelsIn(CenterGameTabs)) yield return gp;
        foreach (var gp in GamePanelsIn(RightTabs)) yield return gp;
        foreach (var gp in GamePanelsIn(BottomLeftTabs)) yield return gp;
        foreach (var gp in GamePanelsIn(BottomTabs)) yield return gp;
    }

    private static IEnumerable<GamePanel> GamePanelsIn(TabControl tc)
    {
        foreach (var obj in tc.Items)
        {
            if (obj is TabItem { Content: GamePanel gp })
                yield return gp;
        }
    }

    private bool FocusInMainWindowTextEntry()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.FocusManager?.GetFocusedElement() is not Control el)
            return false;
        for (var c = el; c != null; c = c.Parent as Control)
        {
            if (c is TextBox or NumericUpDown)
                return true;
        }
        return false;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterUnsavedOk) return;
        if (!SceneService.IsDirty || ProjectService.Current is null) return;
        e.Cancel = true;
        _ = PromptCloseWithOptionalSaveAsync();
    }

    private async Task PromptCloseWithOptionalSaveAsync()
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
        _closingAfterUnsavedOk = true;
        try
        {
            Close();
        }
        catch
        {
            _closingAfterUnsavedOk = false;
        }
    }

    public void RevealInProjectForSelection()
    {
        var go = SelectionService.Current;
        if (go == null)
        {
            Log.Warning("Reveal in Project: nothing selected.");
            return;
        }

        string? rel = null;
        if (!string.IsNullOrWhiteSpace(go.PrefabPath))
            rel = go.PrefabPath;
        else
        {
            foreach (var b in go.Behaviors)
            {
                if (b is MeshFilter mf && !string.IsNullOrWhiteSpace(mf.ModelPath))
                {
                    rel = mf.ModelPath;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(rel))
        {
            Log.Warning("Reveal in Project: selection has no prefab path or mesh model path.");
            return;
        }

        if (FindProjectPanel()?.TryRevealPath(rel) != true)
            Log.Warning($"Reveal in Project: could not find '{rel}' in the project tree.");
        else
            Log.Info($"Project: revealed '{rel}'");
    }

    private ProjectPanel? FindProjectPanel()
    {
        foreach (var obj in BottomLeftTabs.Items)
        {
            if (obj is TabItem { Content: ProjectPanel pp })
                return pp;
        }
        return null;
    }

    private void SaveLayoutPreset(int slot)
    {
        var tabs = CaptureDockLayout();
        DockLayoutPresetStore.Save(slot, tabs);
        Log.Success($"Layout preset {slot} saved ({tabs.Count} tab(s)).");
    }

    private void LoadLayoutPreset(int slot)
    {
        var data = DockLayoutPresetStore.Load(slot);
        if (data == null || data.Count == 0)
        {
            Log.Warning($"Layout preset {slot} is empty or missing.");
            return;
        }
        RestoreDockLayout(data);
        Log.Info($"Layout preset {slot} loaded.");
    }

    private List<DockLayoutTabDto> CaptureDockLayout()
    {
        var list = new List<DockLayoutTabDto>();
        void Scan(DockRegion region, TabControl tc)
        {
            foreach (var obj in tc.Items)
            {
                if (obj is not TabItem tab || tab.Content is not Control c) continue;
                var tn = c.GetType().AssemblyQualifiedName ?? c.GetType().FullName ?? "";
                list.Add(new DockLayoutTabDto
                {
                    Region = region.ToString(),
                    TypeName = tn,
                    Header = tab.Header?.ToString() ?? "",
                    IsActive = ReferenceEquals(tc.SelectedItem, obj)
                });
            }
        }
        Scan(DockRegion.Left, LeftTabs);
        Scan(DockRegion.Center, CenterTabs);
        Scan(DockRegion.CenterSecondary, CenterGameTabs);
        Scan(DockRegion.Right, RightTabs);
        Scan(DockRegion.BottomLeft, BottomLeftTabs);
        Scan(DockRegion.Bottom, BottomTabs);
        return list;
    }

    private void RestoreDockLayout(List<DockLayoutTabDto> tabs)
    {
        ResetLayout(addDefaultPanels: false);
        foreach (var t in tabs)
        {
            if (!Enum.TryParse<DockRegion>(t.Region, out var region)) continue;
            var type = Type.GetType(t.TypeName);
            if (type == null && !string.IsNullOrWhiteSpace(t.TypeName))
            {
                var comma = t.TypeName.IndexOf(',');
                var simple = comma > 0 ? t.TypeName[..comma].Trim() : t.TypeName;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = asm.GetType(simple, false);
                        if (type != null) break;
                    }
                    catch { }
                }
            }
            if (type == null || !_registry.ContainsKey(type)) continue;
            AddPanel(type, region);
            TrySelectRestoredTab(t);
        }
    }

    private void TrySelectRestoredTab(DockLayoutTabDto dto)
    {
        if (!dto.IsActive) return;
        if (!Enum.TryParse<DockRegion>(dto.Region, out var region)) return;
        var tc = TabControlForRegion(region);
        if (tc == null) return;
        foreach (var obj in tc.Items)
        {
            if (obj is not TabItem tab || tab.Content is not Control c) continue;
            var tn = c.GetType().AssemblyQualifiedName ?? c.GetType().FullName ?? "";
            if (string.Equals(tn, dto.TypeName, StringComparison.Ordinal) &&
                string.Equals(tab.Header?.ToString() ?? "", dto.Header ?? "", StringComparison.Ordinal))
            {
                tc.SelectedItem = obj;
                return;
            }
        }
    }

    private TabControl? TabControlForRegion(DockRegion region) => region switch
    {
        DockRegion.Left => LeftTabs,
        DockRegion.Center => CenterTabs,
        DockRegion.CenterSecondary => CenterGameTabs,
        DockRegion.Right => RightTabs,
        DockRegion.BottomLeft => BottomLeftTabs,
        DockRegion.Bottom => BottomTabs,
        _ => null
    };

    private void SaveProjectDockLayout()
    {
        var p = ProjectService.Current;
        if (p == null) return;
        DockLayoutPresetStore.SaveForProject(p.RootPath, CaptureDockLayout());
    }

    private void TryRestoreProjectDockLayout()
    {
        var p = ProjectService.Current;
        if (p == null) return;
        var data = DockLayoutPresetStore.LoadForProject(p.RootPath);
        if (data == null || data.Count == 0) return;
        RestoreDockLayout(data);
    }

    private void OnEditorShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.F5 && !ctrl && !shift && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (FocusInMainWindowTextEntry()) return;
            TogglePlayStopShortcut();
            e.Handled = true;
            return;
        }

        if (!ctrl) return;

        if (!shift && e.Key == Key.S)
        {
            if (ProjectService.Current is null || FocusInMainWindowTextEntry()) return;
            _ = SaveSceneAsync(forceSaveAsDialog: false);
            e.Handled = true;
            return;
        }

        if (shift && e.Key == Key.R)
        {
            if (ProjectService.Current is null || FocusInMainWindowTextEntry()) return;
            RevealInProjectForSelection();
            e.Handled = true;
            return;
        }

        if (!shift && e.Key == Key.P)
        {
            if (ProjectService.Current is null) return;
            ShowQuickOpen();
            e.Handled = true;
            return;
        }

        if (shift && e.Key == Key.P)
        {
            ShowCommandPalette();
            e.Handled = true;
        }
    }

    public void ShowCommandPalette()
    {
        var w = new EditorCommandPaletteWindow(EditorCommandPaletteWindow.SourcesFromRegistry());
        w.Show(this);
    }

    public void ShowQuickOpen()
    {
        if (ProjectService.Current is null) return;
        new EditorQuickOpenWindow(this).Show(this);
    }

    public async Task OpenQuickOpenFileAsync(string absPath)
    {
        if (string.IsNullOrWhiteSpace(absPath) || !File.Exists(absPath)) return;

        var ext = Path.GetExtension(absPath);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            ScriptEditorWindow.Open(this, absPath);
            return;
        }

        if (ext.Equals(".scene", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
            SceneService.LoadFromFile(absPath);
            ProjectService.RememberLastOpenedScene(absPath);
            Log.Info($"Scene loaded: {absPath}");
            return;
        }

        if (ext.Equals(".material", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            ProjectService.SelectAssetForInspector(absPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = absPath, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private async Task PromptLoadSceneAsync()
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
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
        SceneService.LoadFromFile(path);
        ProjectService.RememberLastOpenedScene(path);
        Log.Info($"Scene loaded: {path}");
    }

    private async Task OpenBuildSettingsAsync()
    {
        var dlg = new BuildSettingsWindow();
        await dlg.ShowDialog(this);
    }

    private async Task CloseProjectFromPaletteAsync()
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
        ProjectService.Close();
        RefreshProjectUI();
        ExtensionService.Clear();
        RebuildExtensionMenus();
    }

    private async Task CompileScriptsFromPaletteAsync()
    {
        try
        {
            var (files, types) = await ScriptEditorWindow.CompileAllProjectScriptsAsync();
            ExtensionService.RefreshFromEditorScriptsFolder();
            RefreshProjectUI();
            RebuildExtensionMenus();
            Log.Success($"Scripts compiled ({files} files, {types} behavior types). Extensions reloaded.");
        }
        catch (Exception ex)
        {
            Log.Error($"Script compile failed: {ex.Message}");
        }
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
        DockRegion.CenterSecondary => CenterGameTabs,
        DockRegion.Right => RightTabs,
        DockRegion.BottomLeft => BottomLeftTabs,
        _ => BottomTabs
    };

    private DockRegion RegionOfHost(TabControl host)
    {
        if (host == LeftTabs) return DockRegion.Left;
        if (host == CenterTabs) return DockRegion.Center;
        if (host == CenterGameTabs) return DockRegion.CenterSecondary;
        if (host == RightTabs) return DockRegion.Right;
        if (host == BottomLeftTabs) return DockRegion.BottomLeft;
        return DockRegion.Bottom;
    }

    private async Task InputRemappingAsync()
    {
        var dlg = new InputRemappingWindow();
        await dlg.ShowDialog(this);
    }

    private void ResetLayout(bool addDefaultPanels = true)
    {
        if (_dock is null) return;

        LeftTabs.Items.Clear();
        CenterTabs.Items.Clear();
        CenterGameTabs.Items.Clear();
        RightTabs.Items.Clear();
        BottomLeftTabs.Items.Clear();
        BottomTabs.Items.Clear();

        _dock = new DockManager(LeftTabs, CenterTabs, CenterGameTabs, RightTabs, BottomLeftTabs, BottomTabs);
        _counts.Clear();
        if (addDefaultPanels)
            AddInitialPanels();

        AddTabMenus(LeftTabs);
        AddTabMenus(CenterTabs);
        AddTabMenus(CenterGameTabs);
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
            items.Add(Make("Dock Center _Secondary", () => _dock!.DockTo(content, DockRegion.CenterSecondary)));
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

        if (MI_RecentScenesRoot != null)
            MI_RecentScenesRoot.IsEnabled = has;

        UpdateEditorWindowTitle();

        if (!has)
        {
            RebuildRecentProjectsMenu();
            RebuildRecentScenesMenu();
            return;
        }

        var proj = ProjectService.Current!;
        MI_AutosaveEnabled.Header = proj.AutosaveEnabled ? "_Disable Autosave" : "_Enable Autosave";
        MI_AutosaveEnabled.IsChecked = proj.AutosaveEnabled;
        MI_Autosave1.IsChecked = proj.AutosaveIntervalMinutes == 1;
        MI_Autosave5.IsChecked = proj.AutosaveIntervalMinutes == 5;
        MI_Autosave10.IsChecked = proj.AutosaveIntervalMinutes == 10;
        RebuildRecentProjectsMenu();
        RebuildRecentScenesMenu();

        if (this.FindControl<MenuItem>("MI_ClearConsoleOnPlay") is { } ccPlay)
            ccPlay.IsChecked = EditorSettings.ClearConsoleOnPlay;
        if (this.FindControl<MenuItem>("MI_ScriptLineNumbers") is { } ln)
            ln.IsChecked = EditorSettings.ScriptEditorShowLineNumbers;
        if (this.FindControl<MenuItem>("MI_ScriptWordWrap") is { } ww)
            ww.IsChecked = EditorSettings.ScriptEditorWordWrap;
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
            if (EditorSettings.IncludeStandardAssetsWhenCreatingProject && ProjectService.Current is { } proj)
            {
                if (!StandardAssetsInstaller.TryCopyToProject(proj.RootPath, out var stdErr) && stdErr is not null)
                    await ShowError($"Project was created, but standard assets were not copied:\n{stdErr}");
            }

            ApplyProjectOpenedAfterCreateOrOpen();
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
            ApplyProjectOpenedAfterCreateOrOpen();
        }
        catch (Exception ex)
        {
            await ShowError($"Failed to open project:\n{ex.Message}");
        }
    }

    internal void ApplyProjectOpenedAfterCreateOrOpen()
    {
        if (ProjectService.Current is { } opened)
            RecentProjectsStore.AddRecent(opened.ManifestPath);
        RefreshProjectUI();
        ExtensionService.RefreshFromAppDomain();
        RebuildExtensionMenus();
        SceneService.SetCurrentScenePath(null);
        SceneService.SetDirty(false);
        TryAutoLoadLastOpenedScene();
    }

    
    private async void OnMenuSaveScene_Click(object? sender, RoutedEventArgs e)
    {
        await SaveSceneAsync(forceSaveAsDialog: true);
    }

    
    private async void OnMenuLoadScene_Click(object? sender, RoutedEventArgs e)
        => await PromptLoadSceneAsync();


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

    internal async Task<bool> EnsureSafeToLoseUnsavedSceneAsync()
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

    private void RebuildRecentScenesMenu()
    {
        if (MI_RecentScenesRoot == null) return;
        MI_RecentScenesRoot.Items.Clear();

        if (ProjectService.Current is null)
        {
            MI_RecentScenesRoot.Items.Add(new MenuItem { Header = "(Open a project)", IsEnabled = false });
            return;
        }

        var paths = ProjectService.GetRecentSceneAbsolutePaths();
        if (paths.Count == 0)
        {
            MI_RecentScenesRoot.Items.Add(new MenuItem { Header = "(No recent scenes)", IsEnabled = false });
            return;
        }

        foreach (var abs in paths)
        {
            var mi = new MenuItem { Header = Path.GetFileName(abs) };
            ToolTip.SetTip(mi, abs);
            var pathCopy = abs;
            mi.Click += async (_, __) => await LoadRecentSceneFromPathAsync(pathCopy);
            MI_RecentScenesRoot.Items.Add(mi);
        }
    }

    private async Task LoadRecentSceneFromPathAsync(string absPath)
    {
        if (!await EnsureSafeToLoseUnsavedSceneAsync()) return;
        if (string.IsNullOrWhiteSpace(absPath) || !File.Exists(absPath))
        {
            Log.Warning("Recent scene file is missing or path is invalid.");
            RebuildRecentScenesMenu();
            return;
        }

        SceneService.LoadFromFile(absPath);
        ProjectService.RememberLastOpenedScene(absPath);
        Log.Info($"Scene loaded: {absPath}");
        RebuildRecentScenesMenu();
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
            ApplyProjectOpenedAfterCreateOrOpen();
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
