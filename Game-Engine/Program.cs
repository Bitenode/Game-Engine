using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
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
            // Cached in SceneSerialization: one ImportModel per file, shared Mesh instances for all duplicates.
            SceneSerialization.ResolveMeshesFromModelPath = SceneSerialization.GetOrImportMeshPartsCached;
            SceneSerialization.ResolveMeshFromModelPath = absPath =>
            {
                var list = SceneSerialization.GetOrImportMeshPartsCached(absPath);
                return list.Count > 0 ? list[0] : null;
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

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .WithInterFont()
                         .LogToTrace();
    }
}
