using Avalonia;
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Importers;

namespace Game_Engine
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            WireSceneSerialization();           // set the delegates once at startup
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        static void WireSceneSerialization()
        {
            // Preferred: give SceneSerialization a multi-mesh resolver (preserves model layers)
            SceneSerialization.ResolveMeshesFromModelPath = absPath =>
            {
                try
                {
                    var root = ModelImporter.ImportModel(absPath);   // returns a GO tree
                    return CollectMeshes(root);                      // all MeshFilter.Mesh in DFS order
                }
                catch
                {
                    return new List<Mesh>(); // empty -> loader will gracefully skip
                }
            };

            // Fallback for older loaders that only call single-mesh
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
        }

        // Depth-first: collect every MeshFilter.Mesh in stable order (per node)
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
}
