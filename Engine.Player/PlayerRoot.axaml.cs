#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Game_Engine.Core;
using Game_Engine.Core.Networking;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;

namespace Game_Engine;

public partial class PlayerRoot : UserControl
{
    private string? _tempRoot;

    public PlayerRoot()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetached;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Cleanup();
    }

    internal void Cleanup()
    {
        if (NetworkManager.IsActive)
            NetworkManager.Stop();

        if (_tempRoot != null)
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best-effort */ }
            _tempRoot = null;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            LoadBuildConfig();
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Fatal: {ex}");
        }
    }

    private void LoadBuildConfig()
    {
        var buildJsonPath = App.BuildJsonPath;
        if (string.IsNullOrEmpty(buildJsonPath) || !File.Exists(buildJsonPath))
            throw new FileNotFoundException("build.json not found.");

        var dataDir = Path.GetFullPath(Path.GetDirectoryName(buildJsonPath)!);
        var json = File.ReadAllText(buildJsonPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string productName = "Game";
        if (root.TryGetProperty("product", out var prod))
            productName = prod.GetString() ?? "Game";

        int width = 1280, height = 720;
        bool fullscreen = false;
        if (root.TryGetProperty("resolution", out var res))
        {
            if (res.TryGetProperty("width", out var w)) width = w.GetInt32();
            if (res.TryGetProperty("height", out var h)) height = h.GetInt32();
            if (res.TryGetProperty("fullscreen", out var fs)) fullscreen = fs.GetBoolean();
        }

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host != null)
        {
            host.Title = productName;
            host.Width = width;
            host.Height = height;
            if (fullscreen)
            {
                host.WindowState = WindowState.FullScreen;
#if ANDROID
                host.SystemDecorations = SystemDecorations.None;
#else
                host.WindowDecorations = WindowDecorations.None;
#endif
            }
        }

        _tempRoot = ExtractAssetDll(dataDir);
        SetupProject(_tempRoot, productName);

        var scriptsDll = Path.Combine(dataDir, "GameScripts.dll");
        if (File.Exists(scriptsDll))
        {
            try
            {
                var alc = new AssemblyLoadContext("PlayerScripts", isCollectible: false);
                alc.LoadFromAssemblyPath(scriptsDll);
                Log.Info("[Player] Loaded GameScripts.dll");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Player] Failed to load GameScripts.dll: {ex.Message}");
            }
        }

        string? startupScene = null;
        if (root.TryGetProperty("startupScene", out var ss))
            startupScene = ss.GetString();

        if (!string.IsNullOrEmpty(startupScene))
        {
            var scenePath = Path.Combine(dataDir, "Scenes", startupScene);
            if (File.Exists(scenePath))
            {
                Log.Info($"[Player] Loading startup scene: {startupScene}");
                SceneService.LoadFromFile(scenePath);
            }
            else
            {
                Log.Warning($"[Player] Startup scene not found: {scenePath}");
            }
        }

        var view = this.FindControl<PlayerView>("View");
        view?.StartPlaying();
    }

    private static string ExtractAssetDll(string dataDir)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"EnginePlayer_{Guid.NewGuid():N}");
        var assetsDir = Path.Combine(tempRoot, "Assets");
        Directory.CreateDirectory(assetsDir);

        var dllPath = Path.Combine(dataDir, "Assets.dll");
        if (File.Exists(dllPath))
        {
            Log.Info("[Player] Extracting Assets.dll...");
            try
            {
                ZipFile.ExtractToDirectory(dllPath, assetsDir, overwriteFiles: true);
                Log.Info("[Player] Assets.dll extracted to temp.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Player] Failed to extract Assets.dll: {ex.Message}");
            }
        }

        return tempRoot;
    }

    private static void SetupProject(string rootPath, string productName)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "Scenes"));

            var proj = new Project
            {
                Id = Guid.NewGuid(),
                Name = productName,
                RootPath = rootPath,
                Version = 1,
                EngineVersion = ProjectService.EngineVersion,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow
            };

            ProjectService.SetRuntime(proj);
            Log.Info($"[Player] Project set: {productName} at {rootPath}");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Failed to set up project: {ex.Message}");
        }
    }
}
