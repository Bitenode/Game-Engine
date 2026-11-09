#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;

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

        // Asset cache under EnginePlayerCache is reused across launches — do not delete it.
        _tempRoot = null;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await LoadBuildConfigAsync();
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Fatal: {ex}");
        }
    }

    private async Task LoadBuildConfigAsync()
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

        // Multi‑GB Assets.dll used to extract on the UI thread and freeze as "Not Responding".
        if (host != null)
            host.Title = $"{productName} — preparing assets…";

        _tempRoot = await Task.Run(() => ExtractAssetDll(dataDir)).ConfigureAwait(true);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (host != null)
                host.Title = productName;

            SetupProject(_tempRoot!, productName);
            TryInstallInputBindings(dataDir, _tempRoot);

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
        });
    }

    /// <summary>
    /// Extract Assets.dll into a reusable cache folder keyed by size + mtime.
    /// Avoids re-unzipping multi‑GB packs on every Local Machine / Build &amp; Run launch.
    /// </summary>
    private static string ExtractAssetDll(string dataDir)
    {
        var dllPath = Path.Combine(dataDir, "Assets.dll");
        if (!File.Exists(dllPath))
        {
            var empty = Path.Combine(Path.GetTempPath(), "EnginePlayerCache", "empty");
            Directory.CreateDirectory(Path.Combine(empty, "Assets"));
            return empty;
        }

        var fi = new FileInfo(dllPath);
        var cacheKey = $"{fi.Length:X}_{fi.LastWriteTimeUtc.Ticks:X}";
        var cacheRoot = Path.Combine(Path.GetTempPath(), "EnginePlayerCache", cacheKey);
        var assetsDir = Path.Combine(cacheRoot, "Assets");
        var marker = Path.Combine(cacheRoot, ".extract-ok");

        if (File.Exists(marker) && Directory.Exists(assetsDir))
        {
            Log.Info($"[Player] Reusing cached assets: {cacheRoot}");
            return cacheRoot;
        }

        try
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
        catch { /* best-effort clean */ }

        Directory.CreateDirectory(assetsDir);
        Log.Info($"[Player] Extracting Assets.dll ({fi.Length / (1024.0 * 1024.0):F0} MB) — first launch can take several minutes…");
        try
        {
            ZipFile.ExtractToDirectory(dllPath, assetsDir, overwriteFiles: true);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            Log.Info("[Player] Assets.dll extracted to cache.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Failed to extract Assets.dll: {ex.Message}");
        }

        return cacheRoot;
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
            Input.ResetToEngineDefaults();
            Log.Info($"[Player] Project set: {productName} at {rootPath}");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Failed to set up project: {ex.Message}");
        }
    }

    static void TryInstallInputBindings(string dataDir, string? projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return;
        var src = Path.Combine(dataDir, "ProjectSettings", "input.bindings.json");
        if (!File.Exists(src)) return;
        try
        {
            var dstDir = Path.Combine(projectRoot, "ProjectSettings");
            Directory.CreateDirectory(dstDir);
            File.Copy(src, Path.Combine(dstDir, "input.bindings.json"), overwrite: true);
            Input.ResetToEngineDefaults();
            Input.TryLoadBindingsFromProject();
        }
        catch (Exception ex)
        {
            Log.Warning($"[Player] Could not load input bindings: {ex.Message}");
        }
    }
}
