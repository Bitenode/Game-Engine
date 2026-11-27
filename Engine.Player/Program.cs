using Avalonia;
using System;
using System.IO;
using Game_Engine.Core;
using Game_Engine.Core.Rendering.GPU;

namespace Game_Engine;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Locate build.json: either passed as arg or next to the executable in Data/
        string? buildJson = null;
        bool showFps = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--fps", StringComparison.OrdinalIgnoreCase))
            {
                showFps = true;
            }
            else if (buildJson == null && File.Exists(arg))
            {
                buildJson = Path.GetFullPath(arg);
            }
        }
        if (buildJson == null)
        {
            var exeDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(exeDir, "Data", "build.json");
            if (File.Exists(candidate))
                buildJson = candidate;
        }

        if (buildJson == null)
        {
            Console.Error.WriteLine("Could not find Data/build.json. Place it next to the executable or pass its path as an argument.");
            Environment.Exit(1);
            return;
        }

        App.BuildJsonPath = buildJson;
        App.ShowFpsHud = showFps;

        if (string.IsNullOrEmpty(ShaderProgram.BinaryCacheDirectory))
        {
            ShaderProgram.BinaryCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameEnginePlayer", "ShaderCache");
        }

        // Model and material loaders for scene files. Shared with the Xbox host.
        PlayerBootstrap.WireSceneSerialization();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .LogToTrace();
}
