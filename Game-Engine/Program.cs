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
            // Same as b09ed5c: fresh ImportModel per resolve + DFS mesh list (matches saved ModelPartIndex).
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

            // Wire up the material-from-path resolver so scene deserialization
            // can load .material files via ProjectService (same path the inspector uses).
            // Without this, FromDto falls through to a scalar-only fallback that has no textures.
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

        /// <summary>Depth-first: every <see cref="MeshFilter.Mesh"/> in stable order (matches b09ed5c).</summary>
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
