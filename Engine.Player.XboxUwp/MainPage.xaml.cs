using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Engine.Player.XboxUwp;

public sealed partial class MainPage : Page
{
    Thread _glThread;
    bool _chromeHidden;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnterFullscreen();
        LoadBuildManifest();
        var window = Window.Current?.CoreWindow;
        if (window != null)
            XboxGameLoop.HookCoreWindow(window);
        XboxGameLoop.HookPanel(GlPanel);
        try
        {
            SystemNavigationManager.GetForCurrentView().BackRequested += (_, args) => args.Handled = true;
            ApplicationView.GetForCurrentView()
                .SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
        }
        catch { }

        // Size the swap chain to the real panel before the GL thread creates EGL.
        PushPanelLayout();
        GlPanel.SizeChanged += (_, __) =>
        {
            PushPanelLayout();
            TryEnterFullscreen();
        };
        GlPanel.CompositionScaleChanged += (_, __) => PushPanelLayout();
        RootGrid.SizeChanged += (_, __) => PushPanelLayout();

        var panel = GlPanel;
        _glThread = new Thread(() => XboxGameLoop.Run(panel, Report, HideChrome))
        {
            IsBackground = true,
            Name = "XboxGL"
        };
        _glThread.Start();
    }

    static void TryEnterFullscreen()
    {
        try
        {
            var view = ApplicationView.GetForCurrentView();
            if (!view.IsFullScreenMode)
                view.TryEnterFullScreenMode();
        }
        catch { }
    }

    void PushPanelLayout()
    {
        float scale = 1f;
        try
        {
            scale = Math.Max(GlPanel.CompositionScaleX, GlPanel.CompositionScaleY);
            if (scale < 0.01f) scale = 1f;
        }
        catch { scale = 1f; }

        // Prefer the root grid size — SwapChainPanel ActualWidth can lag at 0 during boot.
        double w = Math.Max(GlPanel.ActualWidth, RootGrid.ActualWidth);
        double h = Math.Max(GlPanel.ActualHeight, RootGrid.ActualHeight);
        if (w < 1 || h < 1)
        {
            try
            {
                var bounds = Window.Current?.Bounds ?? default;
                w = Math.Max(w, bounds.Width);
                h = Math.Max(h, bounds.Height);
            }
            catch { }
        }
        if (w < 1 || h < 1) return;
        XboxGameLoop.SetPanelLayout(w, h, scale);
    }

    void Report(string text)
    {
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
        {
            if (StatusText != null)
                StatusText.Text = text ?? "";
        });
    }

    void HideChrome()
    {
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
        {
            if (_chromeHidden) return;
            _chromeHidden = true;
            if (HostChrome != null)
                HostChrome.Visibility = Visibility.Collapsed;
            TryEnterFullscreen();
            PushPanelLayout();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TryEnterFullscreen();
    }

    private void LoadBuildManifest()
    {
        try
        {
            var dataRoot = Path.Combine(Package.Current.InstalledLocation.Path, "Data");
            var buildJson = Path.Combine(dataRoot, "build.json");

            if (!File.Exists(buildJson))
            {
                TitleText.Text = Package.Current.DisplayName;
                SubtitleText.Text = "Xbox Dev Mode host";
                StatusText.Text =
                    "No Data/build.json in the package yet. Build platform Xbox from the editor " +
                    "so scenes and the manifest are staged into this project, then redeploy.";
                SceneList.ItemsSource = Array.Empty<string>();
                return;
            }

            var root = JsonObject.Parse(File.ReadAllText(buildJson));

            var product = root.GetNamedString("product", "");
            var version = root.GetNamedString("version", "");
            TitleText.Text = string.IsNullOrWhiteSpace(product) ? Package.Current.DisplayName : product;
            SubtitleText.Text = string.IsNullOrWhiteSpace(version)
                ? "Xbox Dev Mode host"
                : "Xbox Dev Mode host  ·  v" + version;

            var scenes = new List<string>();
            if (root.ContainsKey("scenes") && root["scenes"].ValueType == JsonValueType.Array)
            {
                foreach (var item in root.GetNamedArray("scenes"))
                {
                    if (item.ValueType != JsonValueType.String) continue;
                    var name = item.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        scenes.Add(name);
                }
            }

            SceneList.ItemsSource = scenes;
            if (scenes.Count > 0)
                SceneList.SelectedIndex = 0;

            StatusText.Text = "Starting the scene…";
        }
        catch (Exception ex)
        {
            TitleText.Text = Package.Current.DisplayName;
            StatusText.Text = "Failed to read Data/build.json: " + ex.Message;
            SceneList.ItemsSource = Array.Empty<string>();
        }
    }

    private void SceneList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var scene = e.ClickedItem as string;
        if (!string.IsNullOrEmpty(scene))
            StatusText.Text = "Selected \"" + scene + "\". The startup scene is already playing.";
    }
}
