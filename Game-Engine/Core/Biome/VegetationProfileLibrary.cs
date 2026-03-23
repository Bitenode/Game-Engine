#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Game_Engine.Core.Biome;

public sealed class VegetationProfileItem
{
    public string ModelPath { get; set; } = "";
    public string PrefabPath { get; set; } = "";
    public float Weight { get; set; } = 1f;
    public float DensityMultiplier { get; set; } = 1f;
    public float MinScale { get; set; } = 0.9f;
    public float MaxScale { get; set; } = 1.1f;
}

public sealed class VegetationProfile
{
    public string Id { get; set; } = "Default";
    public float VegetationDensity { get; set; } = 0f;
    public float TreeDensity { get; set; } = 0f;
    public float VegetationPatchiness { get; set; } = 0.45f;
    public float SeasonalGrowthMultiplier { get; set; } = 1f;
    public string GrassModelPath { get; set; } = "";
    public string TreeModelPath { get; set; } = "";
    public List<VegetationProfileItem> GrassItems { get; set; } = new();
    public List<VegetationProfileItem> TreeItems { get; set; } = new();
}

public static class VegetationProfileLibrary
{
    static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetProfileFilePath()
    {
        var proj = ProjectService.Current;
        string root = proj?.RootPath ?? Environment.CurrentDirectory;
        string dir = Path.Combine(root, "Assets", "Biomes");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vegetation-profiles.json");
    }

    public static Dictionary<string, VegetationProfile> LoadAll()
    {
        var map = new Dictionary<string, VegetationProfile>(StringComparer.OrdinalIgnoreCase);
        EnsureDefault(map);

        string path = GetProfileFilePath();
        if (!File.Exists(path))
            return map;

        try
        {
            var arr = JsonSerializer.Deserialize<List<VegetationProfile>>(File.ReadAllText(path), _json);
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var p = arr[i];
                    if (p == null || string.IsNullOrWhiteSpace(p.Id)) continue;
                    map[p.Id] = Sanitize(p);
                }
            }
        }
        catch { }

        EnsureDefault(map);
        return map;
    }

    public static void SaveAll(Dictionary<string, VegetationProfile> profiles)
    {
        var safe = new Dictionary<string, VegetationProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in profiles)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            safe[kv.Key] = Sanitize(kv.Value ?? new VegetationProfile { Id = kv.Key });
        }
        EnsureDefault(safe);
        var list = safe.Values.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(GetProfileFilePath(), JsonSerializer.Serialize(list, _json));
    }

    static void EnsureDefault(Dictionary<string, VegetationProfile> map)
    {
        if (!map.ContainsKey("Default"))
            map["Default"] = new VegetationProfile();
        EnsureLegacyCompatibility(map["Default"]);
    }

    static VegetationProfile Sanitize(VegetationProfile p)
    {
        var safe = new VegetationProfile
        {
            Id = string.IsNullOrWhiteSpace(p.Id) ? "Default" : p.Id.Trim(),
            VegetationDensity = Math.Clamp(p.VegetationDensity, 0f, 2f),
            TreeDensity = Math.Clamp(p.TreeDensity, 0f, 2f),
            VegetationPatchiness = Math.Clamp(p.VegetationPatchiness, 0f, 1f),
            SeasonalGrowthMultiplier = Math.Clamp(p.SeasonalGrowthMultiplier, 0f, 3f),
            GrassModelPath = p.GrassModelPath?.Trim() ?? "",
            TreeModelPath = p.TreeModelPath?.Trim() ?? "",
            GrassItems = SanitizeItems(p.GrassItems),
            TreeItems = SanitizeItems(p.TreeItems),
        };
        EnsureLegacyCompatibility(safe);
        return safe;
    }

    static List<VegetationProfileItem> SanitizeItems(List<VegetationProfileItem>? items)
    {
        var list = new List<VegetationProfileItem>();
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                list.Add(new VegetationProfileItem
                {
                    ModelPath = it.ModelPath?.Trim() ?? "",
                    PrefabPath = it.PrefabPath?.Trim() ?? "",
                    Weight = Math.Clamp(it.Weight, 0f, 100f),
                    DensityMultiplier = Math.Clamp(it.DensityMultiplier, 0f, 3f),
                    MinScale = Math.Clamp(it.MinScale, 0.05f, 8f),
                    MaxScale = Math.Clamp(it.MaxScale, 0.05f, 8f),
                });
                var added = list[^1];
                if (string.IsNullOrWhiteSpace(added.PrefabPath) &&
                    added.ModelPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    added.PrefabPath = added.ModelPath;
                    list[^1] = added;
                }
            }
        }
        return list;
    }

    static void EnsureLegacyCompatibility(VegetationProfile p)
    {
        p.GrassItems ??= new List<VegetationProfileItem>();
        p.TreeItems ??= new List<VegetationProfileItem>();

        // Migrate old single-path fields into first list item.
        if (!string.IsNullOrWhiteSpace(p.GrassModelPath) && !p.GrassItems.Any(i => string.Equals(i.ModelPath, p.GrassModelPath, StringComparison.OrdinalIgnoreCase)))
        {
            p.GrassItems.Add(new VegetationProfileItem
            {
                ModelPath = p.GrassModelPath,
                Weight = 1f,
                DensityMultiplier = 1f,
                MinScale = 0.9f,
                MaxScale = 1.1f
            });
        }
        if (!string.IsNullOrWhiteSpace(p.TreeModelPath) && !p.TreeItems.Any(i => string.Equals(i.ModelPath, p.TreeModelPath, StringComparison.OrdinalIgnoreCase)))
        {
            p.TreeItems.Add(new VegetationProfileItem
            {
                ModelPath = p.TreeModelPath,
                Weight = 1f,
                DensityMultiplier = 1f,
                MinScale = 0.9f,
                MaxScale = 1.1f
            });
        }

        // Keep legacy fields in sync for old consumers.
        p.GrassModelPath = p.GrassItems.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ModelPath))?.ModelPath ?? p.GrassModelPath;
        p.TreeModelPath = p.TreeItems.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ModelPath))?.ModelPath ?? p.TreeModelPath;
    }
}
