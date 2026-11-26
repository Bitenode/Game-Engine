#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;

namespace Game_Engine.Core
{
    /// <summary>
    /// Loads build.json, scripts, and the startup scene. Desktop applies window size itself;
    /// Xbox calls <see cref="Load"/> on the GL thread.
    /// </summary>
    public static class PlayerBootstrap
    {
        public sealed class BuildConfig
        {
            public string ProductName = "Game";
            public int Width = 1280, Height = 720;
            public bool Fullscreen;
            public string? StartupScene;
            public string DataDir = "";
        }

        public static string? TempRoot { get; private set; }
        public static string? LastScriptError { get; private set; }

        public static BuildConfig ReadConfig(string buildJsonPath)
        {
            if (string.IsNullOrEmpty(buildJsonPath) || !File.Exists(buildJsonPath))
                throw new FileNotFoundException("build.json not found.", buildJsonPath);

            var cfg = new BuildConfig { DataDir = Path.GetFullPath(Path.GetDirectoryName(buildJsonPath)!) };
            using var doc = JsonDocument.Parse(File.ReadAllText(buildJsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("product", out var prod))
                cfg.ProductName = prod.GetString() ?? "Game";
            if (root.TryGetProperty("resolution", out var res))
            {
                if (res.TryGetProperty("width", out var w)) cfg.Width = w.GetInt32();
                if (res.TryGetProperty("height", out var h)) cfg.Height = h.GetInt32();
                if (res.TryGetProperty("fullscreen", out var fs)) cfg.Fullscreen = fs.GetBoolean();
            }
            if (root.TryGetProperty("startupScene", out var ss))
                cfg.StartupScene = ss.GetString();
            return cfg;
        }

        static bool s_sceneSerializationWired;

        /// <summary>
        /// Gives the scene loader its model and material loaders. Without these a MeshFilter that names a
        /// model keeps its placeholder cube, and a <c>.material</c> file loads without its texture slots.
        /// Every player host must call this before the first scene is loaded.
        /// </summary>
        public static void WireSceneSerialization()
        {
            if (s_sceneSerializationWired) return;
            s_sceneSerializationWired = true;

            SceneSerialization.ResolveMeshesFromModelPath = absPath =>
            {
                try { return Importers.ModelImporter.LoadModelMeshParts(absPath); }
                catch (Exception ex)
                {
                    Log.Warning($"[Player] Model import failed for '{Path.GetFileName(absPath)}': {ex.Message}");
                    return new System.Collections.Generic.List<Mesh>();
                }
            };

            SceneSerialization.ResolveMeshFromModelPath = absPath =>
            {
                try
                {
                    var list = Importers.ModelImporter.LoadModelMeshParts(absPath);
                    return list.Count > 0 ? list[0] : null;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[Player] Model import failed for '{Path.GetFileName(absPath)}': {ex.Message}");
                    return null;
                }
            };

            SceneSerialization.ResolveMaterialFromPath = absPath =>
            {
                try { return ProjectService.MaterialsLoad(absPath); }
                catch { return null; }
            };
        }

        static System.Collections.Generic.List<Mesh> CollectMeshes(GameObject go)
        {
            var result = new System.Collections.Generic.List<Mesh>();
            void Walk(GameObject n)
            {
                foreach (var b in n.Behaviors)
                {
                    if (b is Component.MeshFilter mf && mf.Mesh != null)
                        result.Add(mf.Mesh);
                }
                foreach (var c in n.Children)
                    Walk(c);
            }
            Walk(go);
            return result;
        }

        /// <summary>Extract or reuse Assets.dll, set the runtime project, load scripts and the startup scene.</summary>
        public static BuildConfig Load(string buildJsonPath)
        {
            WireSceneSerialization();
            var cfg = ReadConfig(buildJsonPath);
            TempRoot = ExtractAssetDll(cfg.DataDir);
            SetupProject(TempRoot, cfg.ProductName);
            TryInstallInputBindings(cfg.DataDir, TempRoot);

            LastScriptError = null;
            var scriptsDll = Path.Combine(cfg.DataDir, "GameScripts.dll");
            if (File.Exists(scriptsDll))
            {
                try
                {
                    RedirectEditorAssemblyToHost();
                    var alc = new AssemblyLoadContext("PlayerScripts", isCollectible: false);
                    alc.Resolving += (_, name) =>
                    {
                        // Resolving may only return an assembly with the requested simple name.
                        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                                return loaded;
                        }
                        return null;
                    };
                    var scripts = alc.LoadFromAssemblyPath(scriptsDll);
                    ScriptTypeReload.NotePlayerScripts(scripts);

                    // Bind every script type now. A failed bind otherwise only shows up as components silently missing.
                    int typeCount;
                    try { typeCount = scripts.GetTypes().Length; }
                    catch (System.Reflection.ReflectionTypeLoadException rtle)
                    {
                        typeCount = 0;
                        foreach (var t in rtle.Types)
                            if (t != null) typeCount++;
                        var first = rtle.LoaderExceptions is { Length: > 0 } ? rtle.LoaderExceptions[0]?.Message : rtle.Message;
                        LastScriptError = "Some script types did not load: " + first;
                        Log.Warning("[Player] " + LastScriptError);
                    }
                    Log.Info($"[Player] Loaded GameScripts.dll ({typeCount} types)");
                }
                catch (Exception ex)
                {
                    LastScriptError = ex.Message;
                    Log.Warning($"[Player] Failed to load GameScripts.dll: {ex.Message}");
                }
            }
            else
                LastScriptError = "GameScripts.dll was not in the package.";

            if (!string.IsNullOrEmpty(cfg.StartupScene))
            {
                var scenePath = Path.Combine(cfg.DataDir, "Scenes", cfg.StartupScene);
                if (File.Exists(scenePath))
                {
                    Log.Info($"[Player] Loading startup scene: {cfg.StartupScene}");
                    SceneService.LoadFromFile(scenePath);
                }
                else
                    Log.Warning($"[Player] Startup scene not found: {scenePath}");
            }

            return cfg;
        }

        static bool s_redirectInstalled;

        /// <summary>
        /// GameScripts.dll is compiled against the editor assembly <c>Game_Engine</c>. A host that compiles
        /// Core into its own assembly (Xbox) has no assembly by that name, so every script type fails to bind.
        /// <see cref="AssemblyLoadContext.Resolving"/> rejects an assembly whose name differs from the request;
        /// <see cref="AppDomain.AssemblyResolve"/> does not, so the redirect lives there.
        /// </summary>
        public static void RedirectEditorAssemblyToHost()
        {
            if (s_redirectInstalled) return;
            s_redirectInstalled = true;
            var host = typeof(PlayerBootstrap).Assembly;
            if (string.Equals(host.GetName().Name, "Game_Engine", StringComparison.OrdinalIgnoreCase))
                return;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string requested;
                try { requested = new System.Reflection.AssemblyName(args.Name).Name ?? ""; }
                catch { return null; }
                return string.Equals(requested, "Game_Engine", StringComparison.OrdinalIgnoreCase) ? host : null;
            };
        }

        public static string ExtractAssetDll(string dataDir)
        {
            var dllPath = Path.Combine(dataDir, "Assets.dll");
            if (!File.Exists(dllPath))
            {
                // Loose layout: Data/Assets is already the project Assets folder.
                var loose = Path.Combine(dataDir, "Assets");
                if (Directory.Exists(loose))
                    return dataDir;
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
                return cacheRoot;

            try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); } catch { }
            Directory.CreateDirectory(assetsDir);
            try
            {
                ZipFile.ExtractToDirectory(dllPath, assetsDir, overwriteFiles: true);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Log.Warning($"[Player] Failed to extract Assets.dll: {ex.Message}");
            }
            return cacheRoot;
        }

        public static void SetupProject(string rootPath, string productName)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "Scenes"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Assets"));
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
            Input.Input.ResetToEngineDefaults();
        }

        public static void TryInstallInputBindings(string dataDir, string? projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;
            var src = Path.Combine(dataDir, "ProjectSettings", "input.bindings.json");
            if (!File.Exists(src)) return;
            try
            {
                var dstDir = Path.Combine(projectRoot, "ProjectSettings");
                var dst = Path.Combine(dstDir, "input.bindings.json");
                // Loose layout: Data is the project root, so the file is already in place.
                // Copying it onto itself fails in a read-only package and the bindings were never loaded.
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(dstDir);
                    File.Copy(src, dst, overwrite: true);
                }
                Input.Input.ResetToEngineDefaults();
                Input.Input.TryLoadBindingsFromProject();
            }
            catch (Exception ex)
            {
                Log.Warning($"[Player] Could not load input bindings: {ex.Message}");
            }
        }
    }
}
