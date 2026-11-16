using Avalonia;
using System;
using System.IO;
using Game_Engine.Core;

namespace Game_Engine;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Locate build.json: either passed as arg or next to the executable in Data/
        string? buildJson = null;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            buildJson = Path.GetFullPath(args[0]);
        }
        else
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
