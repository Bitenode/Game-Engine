#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Game_Engine.Core;
using Game_Engine.Core.Networking;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;

namespace Game_Engine;

public partial class PlayerWindow : Window
{
    /// <summary>Temp directory where Assets.dll is extracted. Cleaned up on close.</summary>
    private string? _tempRoot;

    public PlayerWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            LoadBuildConfig();
        }
        catch (Exception ex)
        {
            Title = "Error: " + ex.Message;
            Log.Warning($"[Player] Fatal: {ex}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (NetworkManager.IsActive)
            NetworkManager.Stop();

        // Clean up extracted assets from temp
        if (_tempRoot != null)
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
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

        // ── Window title ──
        string productName = "Game";
        if (root.TryGetProperty("product", out var prod))
            productName = prod.GetString() ?? "Game";
        Title = productName;

        // ── Resolution ──
        int width = 1280, height = 720;
        bool fullscreen = false;
        if (root.TryGetProperty("resolution", out var res))
        {
            if (res.TryGetProperty("width", out var w)) width = w.GetInt32();
            if (res.TryGetProperty("height", out var h)) height = h.GetInt32();
            if (res.TryGetProperty("fullscreen", out var fs)) fullscreen = fs.GetBoolean();
        }

        Width = width;
        Height = height;
        if (fullscreen)
        {
            WindowState = WindowState.FullScreen;
            SystemDecorations = SystemDecorations.None;
        }

        // ── Extract Assets.dll to a temp directory ──
        _tempRoot = ExtractAssetDll(dataDir);

        // ── Set up ProjectService pointing at the temp dir ──
        SetupProject(_tempRoot, productName);

        // ── Load user scripts DLL ──
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

        // ── Load startup scene ──
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

        // ── Start playing ──
        var view = this.FindControl<PlayerView>("View");
        view?.StartPlaying();
    }

    /// <summary>
    /// Extract Data/Assets.dll (ZIP archive) to a temp directory so all
    /// file-based asset loading works. Returns the temp root path.
    /// The structure is: {temp}/Assets/fbx/textures/...
    /// </summary>
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

    /// <summary>
    /// Set up ProjectService.Current pointing at the temp directory so
    /// Core path resolution (textures, materials, models) works correctly.
    /// No project.json file needed.
    /// </summary>
    private static void SetupProject(string rootPath, string productName)
    {
        try
        {
            // Create Scenes dir in temp so ProjectService.ScenesPath exists
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
