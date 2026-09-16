#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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

            // Mark the source GO and all its children as part of this prefab
            StampPrefabRecursive(go, prefab.PrefabId, savePath);

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
                StampPrefabRecursive(go, PrefabId, FilePath);
                ApplyOverrides(go);

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
        /// Apply prefab changes to all instances in the current scene (deep apply including children).
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
                var overrides = new Dictionary<string, string>(inst.PrefabOverrides);

                // Rebuild from prefab data
                var fresh = DeserializeGameObject(SerializedData);
                if (fresh == null) continue;

                // Copy behaviors from fresh to instance
                inst.Behaviors.Clear();
                foreach (var b in fresh.Behaviors)
                    inst.AddBehavior(b);

                // Deep apply: rebuild children hierarchy
                // Remove old children that are not prefab-overridden
                var oldChildren = new List<GameObject>(inst.Children);
                foreach (var child in oldChildren)
                    child.RemoveFromParent();

                // Add children from fresh prefab data
                foreach (var freshChild in fresh.Children)
                {
                    freshChild.PrefabId = PrefabId;
                    freshChild.PrefabPath = FilePath;
                    inst.AddChild(freshChild);
                }

                // Restore instance transform
                inst.Transform.Position = pos;
                inst.Transform.Rotation = rot;
                inst.Transform.Scale = scale;
                inst.PrefabOverrides.Clear();
                foreach (var kv in overrides)
                    inst.PrefabOverrides[kv.Key] = kv.Value;
                ApplyOverrides(inst);
            }

            SceneService.NotifyChanged();
            Log.Info($"[Prefab] Applied changes to {instances.Count} instances of {Name}");
        }

        /// <summary>
        /// Revert a prefab instance to its prefab state (reload from file).
        /// </summary>
        public static bool RevertInstance(GameObject go)
        {
            if (string.IsNullOrEmpty(go.PrefabPath)) return false;

            var prefab = Load(go.PrefabPath);
            if (prefab == null) return false;

            var fresh = DeserializeGameObject(prefab.SerializedData);
            if (fresh == null) return false;

            // Preserve transform
            var pos = go.Transform.Position;
            var rot = go.Transform.Rotation;
            var scale = go.Transform.Scale;
            go.PrefabOverrides.Clear();

            // Replace behaviors
            go.Behaviors.Clear();
            foreach (var b in fresh.Behaviors)
                go.AddBehavior(b);

            // Replace children
            var oldChildren = new List<GameObject>(go.Children);
            foreach (var child in oldChildren)
                child.RemoveFromParent();
            foreach (var freshChild in fresh.Children)
                go.AddChild(freshChild);

            // Restore transform
            go.Transform.Position = pos;
            go.Transform.Rotation = rot;
            go.Transform.Scale = scale;

            SceneService.NotifyChanged();
            Log.Info($"[Prefab] Reverted instance: {go.Name}");
            return true;
        }

        /// <summary>
        /// Update this prefab's data from a live instance (save overrides back to prefab).
        /// </summary>
        public void UpdateFromInstance(GameObject instance)
        {
            SerializedData = SerializeGameObject(instance);
            Name = instance.Name;
            Save();
            Log.Info($"[Prefab] Updated prefab from instance: {Name}");
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

        public static void RecordOverride(GameObject go, string key, object? value)
        {
            if (go == null || string.IsNullOrEmpty(go.PrefabId) || string.IsNullOrEmpty(key)) return;
            go.PrefabOverrides[key] = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        public static void ApplyOverrides(GameObject go)
        {
            if (go == null || go.PrefabOverrides.Count == 0) return;
            foreach (var kv in go.PrefabOverrides)
                TryApplyOverride(go, kv.Key, kv.Value);
        }

        static void TryApplyOverride(GameObject go, string key, string value)
        {
            var dot = key.IndexOf('.');
            if (dot <= 0) return;
            var typeName = key[..dot];
            var propName = key[(dot + 1)..];

            object? target = null;
            if (string.Equals(typeName, "Transform", StringComparison.OrdinalIgnoreCase))
                target = go.Transform;
            else
            {
                foreach (var b in go.Behaviors)
                {
                    if (b.GetType().Name == typeName) { target = b; break; }
                }
            }
            if (target == null) return;
            var prop = target.GetType().GetProperty(propName);
            if (prop == null || !prop.CanWrite) return;
            try
            {
                var converted = ConvertOverride(prop.PropertyType, value);
                prop.SetValue(target, converted);
            }
            catch { /* ignore bad override */ }
        }

        static object? ConvertOverride(Type t, string value)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(string)) return value;
            if (t == typeof(bool)) return bool.Parse(value);
            if (t.IsEnum) return Enum.Parse(t, value, true);
            if (t == typeof(Vector3))
            {
                var parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    return new Vector3(x, y, z);
            }
            return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        }

        /// <summary>Recursively set PrefabId and PrefabPath on a GO and all its descendants.</summary>
        private static void StampPrefabRecursive(GameObject go, string prefabId, string prefabPath)
        {
            go.PrefabId = prefabId;
            go.PrefabPath = prefabPath;
            foreach (var child in go.Children)
                StampPrefabRecursive(child, prefabId, prefabPath);
        }

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
            // Use the engine's DTO pipeline which properly handles children, behaviors, transforms.
            // includeAll: true so ALL children are captured (no "Grass"/"chunk_" filtering).
            try
            {
                return SceneSerialization.SerializeGameObjectToJson(go, includeAll: true);
            }
            catch (Exception ex)
            {
                Log.Error($"[Prefab] Serialize failed: {ex.Message}");
                return "{}";
            }
        }

        private static GameObject? DeserializeGameObject(string json)
        {
            try
            {
                return SceneSerialization.DeserializeGameObjectFromJson(json);
            }
            catch (Exception ex)
            {
                Log.Error($"[Prefab] Deserialize failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Clear the prefab cache.</summary>
        public static void ClearCache() => _cache.Clear();
    }
}
