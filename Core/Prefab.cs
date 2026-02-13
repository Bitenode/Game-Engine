#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>
    /// Prefab asset — a serialized GameObject hierarchy that can be instantiated
    /// multiple times. Saved as .prefab files (JSON format).
    /// </summary>
    public sealed class Prefab
    {
        /// <summary>Display name of the prefab.</summary>
        public string Name { get; set; } = "New Prefab";

        /// <summary>File path where this prefab is stored (relative to project).</summary>
        public string FilePath { get; set; } = "";

        /// <summary>Serialized JSON data of the root GameObject hierarchy.</summary>
        public string SerializedData { get; set; } = "";

        /// <summary>Unique ID to track prefab instances.</summary>
        public string PrefabId { get; set; } = Guid.NewGuid().ToString("N");

        // ── Static cache ──
        private static readonly Dictionary<string, Prefab> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Create a prefab from an existing GameObject hierarchy.
        /// </summary>
        public static Prefab CreateFrom(GameObject go, string savePath)
        {
            var prefab = new Prefab
            {
                Name = go.Name,
                FilePath = savePath,
                SerializedData = SerializeGameObject(go)
            };

            // Mark the source GO as a prefab instance
            go.PrefabId = prefab.PrefabId;
            go.PrefabPath = savePath;

            return prefab;
        }

        /// <summary>Save this prefab to disk.</summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;

            string abs = FilePath;
            var proj = ProjectService.Current;
            if (proj != null && !Path.IsPathRooted(FilePath))
                abs = Path.GetFullPath(Path.Combine(proj.RootPath, FilePath));

            var dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(abs, json);

            _cache[abs] = this;
            Log.Info($"[Prefab] Saved: {Name} → {abs}");
        }

        /// <summary>Load a prefab from disk.</summary>
        public static Prefab? Load(string path)
        {
            string abs = path;
            var proj = ProjectService.Current;
            if (proj != null && !Path.IsPathRooted(path))
                abs = Path.GetFullPath(Path.Combine(proj.RootPath, path));

            if (_cache.TryGetValue(abs, out var cached))
                return cached;

            if (!File.Exists(abs)) return null;

            try
            {
                var json = File.ReadAllText(abs);
                var prefab = JsonSerializer.Deserialize<Prefab>(json);
                if (prefab != null)
                {
                    prefab.FilePath = path;
                    _cache[abs] = prefab;
                }
                return prefab;
            }
            catch (Exception ex)
            {
                Log.Error($"[Prefab] Failed to load {abs}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Instantiate this prefab as a new GameObject hierarchy in the scene.
        /// </summary>
        public GameObject? Instantiate(GameObject? parent = null)
        {
            if (string.IsNullOrEmpty(SerializedData)) return null;

            try
            {
                var go = DeserializeGameObject(SerializedData);
                if (go == null) return null;

                go.Name = Name;
                go.PrefabId = PrefabId;
                go.PrefabPath = FilePath;

                if (parent != null)
                    parent.AddChild(go);
                else
                    SceneService.Add(go);

                Log.Info($"[Prefab] Instantiated: {Name}");
                return go;
            }
            catch (Exception ex)
            {
                Log.Error($"[Prefab] Instantiate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Apply prefab changes to all instances in the current scene.
        /// </summary>
        public void ApplyToInstances()
        {
            var instances = FindInstances(PrefabId);
            foreach (var inst in instances)
            {
                // Preserve transform of the instance
                var pos = inst.Transform.Position;
                var rot = inst.Transform.Rotation;
                var scale = inst.Transform.Scale;

                // Rebuild from prefab data
                var fresh = DeserializeGameObject(SerializedData);
                if (fresh == null) continue;

                // Copy behaviors from fresh to instance
                inst.Behaviors.Clear();
                foreach (var b in fresh.Behaviors)
                    inst.AddBehavior(b);

                // Restore instance transform
                inst.Transform.Position = pos;
                inst.Transform.Rotation = rot;
                inst.Transform.Scale = scale;
            }

            SceneService.NotifyChanged();
            Log.Info($"[Prefab] Applied changes to {instances.Count} instances of {Name}");
        }

        /// <summary>Unpack a prefab instance — break the prefab link.</summary>
        public static void Unpack(GameObject go)
        {
            go.PrefabId = null;
            go.PrefabPath = null;

            // Recursively unpack children
            foreach (var child in go.Children)
                Unpack(child);

            SceneService.NotifyChanged();
            Log.Info($"[Prefab] Unpacked: {go.Name}");
        }

        /// <summary>Check if a GameObject is a prefab instance.</summary>
        public static bool IsPrefabInstance(GameObject go) => !string.IsNullOrEmpty(go.PrefabId);

        // ── Helpers ──

        private static List<GameObject> FindInstances(string prefabId)
        {
            var result = new List<GameObject>();
            foreach (var root in SceneService.Root)
                FindInstancesRecursive(root, prefabId, result);
            return result;
        }

        private static void FindInstancesRecursive(GameObject go, string prefabId, List<GameObject> result)
        {
            if (go.PrefabId == prefabId)
                result.Add(go);
            foreach (var child in go.Children)
                FindInstancesRecursive(child, prefabId, result);
        }

        private static string SerializeGameObject(GameObject go)
        {
            // Use the engine's existing serialization infrastructure
            try
            {
                return JsonSerializer.Serialize(go, SceneSerialization.JsonOptions);
            }
            catch
            {
                return "{}";
            }
        }

        private static GameObject? DeserializeGameObject(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<GameObject>(json, SceneSerialization.JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Clear the prefab cache.</summary>
        public static void ClearCache() => _cache.Clear();
    }
}
