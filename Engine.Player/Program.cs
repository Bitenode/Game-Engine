using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Importers;

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

        WireSceneSerialization();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static void WireSceneSerialization()
    {
        // Multi-mesh resolver (preserves model layers)
        SceneSerialization.ResolveMeshesFromModelPath = absPath =>
        {
            try
            {
                var root = ModelImporter.ImportModel(absPath);
                return CollectMeshes(root);
            }
            catch
            {
                return new List<Mesh>();
            }
        };

        SceneSerialization.ResolveMeshFromModelPath = absPath =>
        {
            try
            {
                var root = ModelImporter.ImportModel(absPath);
                var list = CollectMeshes(root);
                return list.Count > 0 ? list[0] : null;
            }
            catch
            {
                return null;
            }
        };

        // Material resolver
        SceneSerialization.ResolveMaterialFromPath = absPath =>
        {
            try
            {
                return ProjectService.MaterialsLoad(absPath);
            }
            catch
            {
                return null;
            }
        };
    }

    static List<Mesh> CollectMeshes(GameObject go)
    {
        var result = new List<Mesh>();
        void Walk(GameObject n)
        {
            foreach (var b in n.Behaviors)
            {
                if (b is MeshFilter mf && mf.Mesh != null)
                    result.Add(mf.Mesh);
            }
            foreach (var c in n.Children)
                Walk(c);
        }
        Walk(go);
        return result;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .LogToTrace();
}
