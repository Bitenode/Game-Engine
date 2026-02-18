#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game_Engine.Core;

namespace Game_Engine.Core.SaveSystem
{
    /// <summary>
    /// Manages runtime game state persistence with slot-based saving.
    /// Iterates all ISaveable components and serializes their state to JSON.
    /// </summary>
    public static class SaveManager
    {
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new ObjectJsonConverter() }
        };

        /// <summary>Save the current game state to a numbered slot.</summary>
        public static bool Save(int slotId, string? description = null)
        {
            try
            {
                var saveData = new SaveData
                {
                    SlotId = slotId,
                    Description = description ?? $"Save {slotId}",
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Version = 1,
                    SceneName = SceneManager.CurrentSceneName ?? ""
                };

                // Collect data from all ISaveable components
                foreach (var root in SceneService.Root)
                    CollectSaveables(root, saveData.Entries);

                var json = JsonSerializer.Serialize(saveData, _jsonOpts);
                var path = GetSlotPath(slotId);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, json);
                Log.Info($"[SaveManager] Game saved to slot {slotId}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SaveManager.Save");
                return false;
            }
        }

        /// <summary>Load game state from a numbered slot.</summary>
        public static bool Load(int slotId)
        {
            try
            {
                var path = GetSlotPath(slotId);
                if (!File.Exists(path))
                {
                    Log.Warning($"[SaveManager] Save slot {slotId} not found");
                    return false;
                }

                var json = File.ReadAllText(path);
                var saveData = JsonSerializer.Deserialize<SaveData>(json, _jsonOpts);
                if (saveData == null) return false;

                // Build lookup of save entries by ID
                var lookup = new Dictionary<string, Dictionary<string, object>>();
                foreach (var entry in saveData.Entries)
                    lookup[entry.SaveId] = entry.Data;

                // Apply data to all ISaveable components
                foreach (var root in SceneService.Root)
                    ApplySaveables(root, lookup);

                Log.Info($"[SaveManager] Game loaded from slot {slotId}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SaveManager.Load");
                return false;
            }
        }

        /// <summary>Delete a save slot.</summary>
        public static bool DeleteSlot(int slotId)
        {
            var path = GetSlotPath(slotId);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        /// <summary>Check if a save slot exists.</summary>
        public static bool SlotExists(int slotId) => File.Exists(GetSlotPath(slotId));

        /// <summary>Get information about all save slots.</summary>
        public static List<SaveSlotInfo> ListSlots()
        {
            var result = new List<SaveSlotInfo>();
            var savesDir = GetSavesDirectory();
            if (!Directory.Exists(savesDir)) return result;

            foreach (var file in Directory.GetFiles(savesDir, "slot_*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var saveData = JsonSerializer.Deserialize<SaveData>(json, _jsonOpts);
                    if (saveData != null)
                    {
                        result.Add(new SaveSlotInfo
                        {
                            SlotId = saveData.SlotId,
                            Description = saveData.Description,
                            Timestamp = saveData.Timestamp,
                            SceneName = saveData.SceneName
                        });
                    }
                }
                catch { }
            }

            result.Sort((a, b) => a.SlotId.CompareTo(b.SlotId));
            return result;
        }

        // ── Internal ──

        private static void CollectSaveables(GameObject go, List<SaveEntry> entries)
        {
            foreach (var behavior in go.Behaviors)
            {
                if (behavior is ISaveable saveable && behavior.Enabled)
                {
                    var data = new Dictionary<string, object>();
                    saveable.OnSave(data);
                    entries.Add(new SaveEntry
                    {
                        SaveId = saveable.SaveId,
                        ComponentType = behavior.GetType().Name,
                        Data = data
                    });
                }
            }

            foreach (var child in go.Children)
                CollectSaveables(child, entries);
        }

        private static void ApplySaveables(GameObject go, Dictionary<string, Dictionary<string, object>> lookup)
        {
            foreach (var behavior in go.Behaviors)
            {
                if (behavior is ISaveable saveable && lookup.TryGetValue(saveable.SaveId, out var data))
                    saveable.OnLoad(data);
            }

            foreach (var child in go.Children)
                ApplySaveables(child, lookup);
        }

        private static string GetSavesDirectory()
        {
            var proj = ProjectService.Current;
            if (proj != null)
                return Path.Combine(proj.RootPath, "Saves");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameEngine", "Saves");
        }

        private static string GetSlotPath(int slotId)
            => Path.Combine(GetSavesDirectory(), $"slot_{slotId}.json");

        // ── DTOs ──

        private class SaveData
        {
            public int SlotId { get; set; }
            public string Description { get; set; } = "";
            public string Timestamp { get; set; } = "";
            public int Version { get; set; } = 1;
            public string SceneName { get; set; } = "";
            public List<SaveEntry> Entries { get; set; } = new();
        }

        private class SaveEntry
        {
            public string SaveId { get; set; } = "";
            public string ComponentType { get; set; } = "";
            public Dictionary<string, object> Data { get; set; } = new();
        }

        /// <summary>Custom converter that handles object types in the save data dictionary.</summary>
        private class ObjectJsonConverter : JsonConverter<object>
        {
            public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return reader.TokenType switch
                {
                    JsonTokenType.True => true,
                    JsonTokenType.False => false,
                    JsonTokenType.Number when reader.TryGetInt64(out long l) => l,
                    JsonTokenType.Number => reader.GetDouble(),
                    JsonTokenType.String => reader.GetString() ?? "",
                    _ => JsonDocument.ParseValue(ref reader).RootElement.Clone()
                };
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }
        }
    }

    /// <summary>Information about a save slot for display in UI.</summary>
    public class SaveSlotInfo
    {
        public int SlotId { get; set; }
        public string Description { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string SceneName { get; set; } = "";
    }
}
