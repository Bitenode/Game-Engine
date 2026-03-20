using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Game_Engine.Core;

public static class RecentProjectsStore
{
    private sealed class StoreModel
    {
        public List<string> Recents { get; set; } = new();
        public List<string> Pinned { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameEngine",
        "editor-recents.json");

    private const int MaxRecents = 12;

    public static IReadOnlyList<string> GetRecents()
    {
        var store = Load();
        return store.Recents.Where(File.Exists).ToList();
    }

    public static IReadOnlyList<string> GetPinned()
    {
        var store = Load();
        return store.Pinned.Where(File.Exists).ToList();
    }

    public static void AddRecent(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return;
        var full = Path.GetFullPath(manifestPath);
        var store = Load();

        store.Recents.RemoveAll(p => PathsEqual(p, full));
        store.Recents.Insert(0, full);
        if (store.Recents.Count > MaxRecents)
            store.Recents = store.Recents.Take(MaxRecents).ToList();

        Save(store);
    }

    public static void TogglePinned(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return;
        var full = Path.GetFullPath(manifestPath);
        var store = Load();

        var idx = store.Pinned.FindIndex(p => PathsEqual(p, full));
        if (idx >= 0) store.Pinned.RemoveAt(idx);
        else store.Pinned.Add(full);

        Save(store);
    }

    public static bool IsPinned(string manifestPath)
    {
        var full = Path.GetFullPath(manifestPath);
        var store = Load();
        return store.Pinned.Any(p => PathsEqual(p, full));
    }

    private static StoreModel Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new StoreModel();
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<StoreModel>(json, JsonOptions) ?? new StoreModel();
        }
        catch
        {
            return new StoreModel();
        }
    }

    private static void Save(StoreModel model)
    {
        var dir = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(model, JsonOptions));
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
