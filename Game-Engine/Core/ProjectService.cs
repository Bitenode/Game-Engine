using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using Game_Engine.Core.Rendering;

namespace Game_Engine.Core
{

    /// <summary>In-memory project info derived from project.json.</summary>
    public sealed record class Project
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string RootPath { get; init; } = "";
        public string? LastOpenedScenePath { get; init; }
        /// <summary>Stored scene paths (relative under project root or absolute), most recent first. Capped when updated.</summary>
        public List<string> RecentScenes { get; init; } = new();
        public bool AutosaveEnabled { get; init; } = false;
        public int AutosaveIntervalMinutes { get; init; } = 5;

        [JsonIgnore] public string AssetsPath => Path.Combine(RootPath, "Assets");
        [JsonIgnore] public string ScenesPath => Path.Combine(RootPath, "Scenes");
        [JsonIgnore] public string BuildsPath => Path.Combine(RootPath, "Builds");
        [JsonIgnore] public string PackagesPath => Path.Combine(RootPath, "Packages");
        [JsonIgnore] public string TempPath => Path.Combine(RootPath, "Temp");

        public int Version { get; init; } = 1;
        public string EngineVersion { get; init; } = ProjectService.EngineVersion;
        public DateTime CreatedUtc { get; init; }
        public DateTime ModifiedUtc { get; init; }

        [JsonIgnore]
        public string ManifestPath => Path.Combine(RootPath, "project.json");
    }

    public delegate void AssetSelectedHandler(object sender, string absolutePath);
    /// <summary>Handles creating/opening/closing projects and writing the manifest.</summary>
    public static partial class ProjectService
    {
        public const string EngineVersion = "0.0.1"; 


        public static Project? Current { get; private set; }

        public static event Action? ProjectOpened;
        public static event Action? ProjectClosed;
        public static event Action? Changed; // generic "something about the project changed"

        /// <summary>Raises <see cref="Changed"/> when project files on disk change without a manifest update (e.g. Standard Assets copy).</summary>
        public static void NotifyProjectFilesystemChanged() => Changed?.Invoke();

        public static event AssetSelectedHandler AssetSelected; // fired with absolute path

        public static string SelectedAssetPath { get; private set; }

        public static void SelectAssetForInspector(string absolutePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(absolutePath)) return;
                SelectedAssetPath = absolutePath;

                var handler = AssetSelected;
                if (handler != null)
                    handler(null, absolutePath); // sender not used

                var ch = Changed; // optional: keep your generic “project changed” ping
                if (ch != null) ch();
            }
            catch
            {
                // swallow; inspector is best-effort
            }
        }

        static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // ---------- Create / Open / Close -----------------------------------

        /// <summary>Create a new project under parentDirectory / Safe(Name). Returns the project and opens it.</summary>
        public static Project CreateNew(string parentDirectory, string name, bool openAfterCreate = true)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory)) throw new ArgumentException("parentDirectory is required");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

            var parent = Path.GetFullPath(parentDirectory);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

            var folderName = MakeSafeName(name);
            var root = Path.Combine(parent, folderName);

            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException($"Directory already exists and is not empty: {root}");

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "Scenes"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            Directory.CreateDirectory(Path.Combine(root, "Builds"));
            Directory.CreateDirectory(Path.Combine(root, "Temp"));

            var now = DateTime.UtcNow;
            var proj = new Project
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                RootPath = root,
                Version = 1,
                EngineVersion = EngineVersion,
                CreatedUtc = now,
                ModifiedUtc = now,
                RecentScenes = new List<string>()
            };

            // Write manifest
            WriteManifest(proj);

            // seed an empty scene file we’ll use later
            // var defaultScene = Path.Combine(proj.ScenesPath, "Main.scene.json");
            // if (!File.Exists(defaultScene)) File.WriteAllText(defaultScene, "{ \"name\": \"Main\", \"objects\": [] }");

            if (openAfterCreate) Open(proj.ManifestPath);
            return proj;
        }

        /// <summary>Open an existing project by project.json path or by a folder that contains it.</summary>
        public static void Open(string pathOrProjectJson)
        {
            string projectJsonPath = ResolveManifestPath(pathOrProjectJson);
            if (!File.Exists(projectJsonPath))
                throw new FileNotFoundException("project.json not found", projectJsonPath);

            var text = File.ReadAllText(projectJsonPath);
            var proj = JsonSerializer.Deserialize<Project>(text, _json)
                ?? throw new InvalidDataException("Invalid project.json");

            // Ensure absolute root (allow moving projects)
            var root = Path.GetDirectoryName(projectJsonPath)!;
            proj = proj with
            {
                RootPath = root,
                RecentScenes = proj.RecentScenes ?? new List<string>()
            };

            Current = proj;
            EnsureFolders(proj);
            ProjectRenderingSettings.Load(proj);
            ProjectOpened?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>
        /// Set Current directly from a Project record without reading a project.json file.
        /// Used by the standalone player to point Core path resolution at the runtime Data/ folder.
        /// </summary>
        public static void SetRuntime(Project proj)
        {
            Current = proj;
            EnsureFolders(proj);
            ProjectRenderingSettings.Load(proj);
        }

        public static void Close()
        {
            if (Current is null) return;
            Current = null;
            ProjectClosed?.Invoke();
            Changed?.Invoke();
        }

        // ---------- Utilities ------------------------------------------------

        public static void TouchModified()
        {
            if (Current is null) return;
            var updated = Current with { ModifiedUtc = DateTime.UtcNow };
            Current = updated;
            WriteManifest(updated);
            Changed?.Invoke();
        }

        /// <summary>Persist the most recently opened scene path in project.json.</summary>
        public static void RememberLastOpenedScene(string scenePath)
        {
            if (Current is null || string.IsNullOrWhiteSpace(scenePath)) return;

            var storedPath = ToStoredScenePath(Current, scenePath);
            var recent = new List<string>(Current.RecentScenes ?? new List<string>());
            recent.RemoveAll(x => string.Equals(x, storedPath, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, storedPath);
            const int recentCap = 15;
            while (recent.Count > recentCap) recent.RemoveAt(recent.Count - 1);

            var updated = Current with
            {
                LastOpenedScenePath = storedPath,
                RecentScenes = recent,
                ModifiedUtc = DateTime.UtcNow
            };

            Current = updated;
            WriteManifest(updated);
            Changed?.Invoke();
        }

        /// <summary>Recent scene file paths that still exist, in MRU order (from <see cref="Project.RecentScenes"/>).</summary>
        public static IReadOnlyList<string> GetRecentSceneAbsolutePaths()
        {
            if (Current?.RecentScenes == null || Current.RecentScenes.Count == 0)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var stored in Current.RecentScenes)
            {
                var abs = ResolveStoredScenePath(Current, stored);
                if (abs == null || !File.Exists(abs)) continue;
                if (list.Any(x => string.Equals(x, abs, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(abs);
            }

            return list;
        }

        /// <summary>Resolve the persisted last scene path to an existing absolute file path, if any.</summary>
        public static string? GetLastOpenedSceneAbsolutePath()
        {
            if (Current is null || string.IsNullOrWhiteSpace(Current.LastOpenedScenePath)) return null;
            return ResolveStoredScenePath(Current, Current.LastOpenedScenePath);
        }

        public static void UpdateAutosaveSettings(bool enabled, int intervalMinutes)
        {
            if (Current is null) return;

            var safeInterval = Math.Clamp(intervalMinutes, 1, 60);
            var updated = Current with
            {
                AutosaveEnabled = enabled,
                AutosaveIntervalMinutes = safeInterval,
                ModifiedUtc = DateTime.UtcNow
            };

            Current = updated;
            WriteManifest(updated);
            Changed?.Invoke();
        }

        /// <summary>Reload only Current from disk manifest without reopening project services.</summary>
        public static void ReloadCurrentFromManifest()
        {
            if (Current is null) return;
            var manifestPath = Current.ManifestPath;
            if (!File.Exists(manifestPath)) return;

            var text = File.ReadAllText(manifestPath);
            var proj = JsonSerializer.Deserialize<Project>(text, _json);
            if (proj is null) return;

            var root = Path.GetDirectoryName(manifestPath)!;
            Current = proj with
            {
                RootPath = root,
                RecentScenes = proj.RecentScenes ?? new List<string>()
            };
            Changed?.Invoke();
        }

        static void WriteManifest(Project proj)
        {
            var json = JsonSerializer.Serialize(proj, _json);
            File.WriteAllText(proj.ManifestPath, json);
        }

        static void EnsureFolders(Project proj)
        {
            Directory.CreateDirectory(proj.AssetsPath);
            Directory.CreateDirectory(proj.ScenesPath);
#if !PLAYER
            // Builds, Packages, Temp are editor-only — the standalone player doesn't need them
            Directory.CreateDirectory(proj.BuildsPath);
            Directory.CreateDirectory(proj.PackagesPath);
            Directory.CreateDirectory(proj.TempPath);
#endif
        }

        static string ToStoredScenePath(Project proj, string scenePath)
        {
            var fullScenePath = Path.GetFullPath(scenePath);
            var fullRootPath = Path.GetFullPath(proj.RootPath);

            if (IsPathInsideRoot(fullRootPath, fullScenePath))
                return Path.GetRelativePath(fullRootPath, fullScenePath);

            // Keep absolute path for scenes outside the project root.
            return fullScenePath;
        }

        static string? ResolveStoredScenePath(Project proj, string storedPath)
        {
            if (Path.IsPathRooted(storedPath))
                return File.Exists(storedPath) ? storedPath : null;

            var candidate = Path.GetFullPath(Path.Combine(proj.RootPath, storedPath));
            return File.Exists(candidate) ? candidate : null;
        }

        static bool IsPathInsideRoot(string rootPath, string candidatePath)
        {
            static string Normalize(string path)
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full + Path.DirectorySeparatorChar;
            }

            var normalizedRoot = Normalize(rootPath);
            var normalizedCandidate = Normalize(candidatePath);
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        static string ResolveManifestPath(string pathOrProjectJson)
        {
            var p = Path.GetFullPath(pathOrProjectJson);
            if (Directory.Exists(p))
                return Path.Combine(p, "project.json");

            if (Path.GetFileName(p).Equals("project.json", StringComparison.OrdinalIgnoreCase))
                return p;

            // If user passed a file inside the project root, search upwards
            var dir = Path.GetDirectoryName(p)!;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "project.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
            }

            return p; // likely wrong; Open() will throw
        }

        static string MakeSafeName(string name)
        {
            var cleaned = string.Concat(name.Select(ch =>
                (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ') ? ch : '_')).Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Project";
            return cleaned;
        }
    }
}
