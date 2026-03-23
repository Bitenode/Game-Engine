using System;
using System.IO;
using System.Text.Json;
using Game_Engine.Core.Biome;

namespace Game_Engine.Core.Planet;

public sealed class PlanetAssetData
{
    public int Version { get; set; } = 1;
    public string BiomeGraphPath { get; set; } = "";
    public float SeaLevelFraction { get; set; } = 0.25f;
    public bool EnableWater { get; set; } = true;
    public PlanetConfig Config { get; set; } = new();
    public PlanetVegetationAssetData Vegetation { get; set; } = new();
}

public sealed class PlanetVegetationAssetData
{
    public bool UseStoredPlacements { get; set; } = false;
    public PlanetVegetationPlacement[] Placements { get; set; } = Array.Empty<PlanetVegetationPlacement>();

    /// <summary>Thread-safe copy for passing from a background load task to the UI thread.</summary>
    public PlanetVegetationAssetData Clone()
    {
        var pl = Placements;
        if (pl == null || pl.Length == 0)
            return new PlanetVegetationAssetData { UseStoredPlacements = UseStoredPlacements, Placements = Array.Empty<PlanetVegetationPlacement>() };
        var list = new List<PlanetVegetationPlacement>(pl.Length);
        for (int i = 0; i < pl.Length; i++)
        {
            var p = pl[i];
            if (p == null) continue;
            list.Add(new PlanetVegetationPlacement
            {
                IsGrass = p.IsGrass,
                BiomeName = p.BiomeName ?? "",
                PrefabPath = p.PrefabPath ?? "",
                ModelPath = p.ModelPath ?? "",
                TexturePath = p.TexturePath ?? "",
                DirX = p.DirX,
                DirY = p.DirY,
                DirZ = p.DirZ,
                Scale = p.Scale,
                YawDeg = p.YawDeg,
            });
        }
        return new PlanetVegetationAssetData { UseStoredPlacements = UseStoredPlacements, Placements = list.ToArray() };
    }
}

public sealed class PlanetVegetationPlacement
{
    public bool IsGrass { get; set; }
    public string BiomeName { get; set; } = "";
    public string PrefabPath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string TexturePath { get; set; } = "";
    public float DirX { get; set; }
    public float DirY { get; set; }
    public float DirZ { get; set; }
    public float Scale { get; set; } = 1f;
    public float YawDeg { get; set; }
}

public static class PlanetAssetIO
{
    static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static bool TryLoad(string planetAssetPath, out PlanetAssetData? data, out string? error)
    {
        data = null;
        error = null;
        try
        {
            string abs = ToAbsolutePath(planetAssetPath);
            if (!File.Exists(abs))
            {
                error = $"Planet asset not found: {planetAssetPath}";
                return false;
            }

            var json = File.ReadAllText(abs);
            data = JsonSerializer.Deserialize<PlanetAssetData>(json, _json);
            if (data == null)
            {
                error = $"Planet asset is empty or invalid JSON: {planetAssetPath}";
                return false;
            }

            data.BiomeGraphPath = NormalizeProjectRelative(data.BiomeGraphPath);
            if (data.Config == null)
                data.Config = new PlanetConfig();
            data.Config.Biomes ??= BiomeDefinition.AllPresets;
            data.Config.RiverAllowedBiomes ??= Array.Empty<string>();
            if (data.Vegetation == null)
                data.Vegetation = new PlanetVegetationAssetData();
            if (data.Vegetation.Placements == null)
                data.Vegetation.Placements = Array.Empty<PlanetVegetationPlacement>();
            for (int i = 0; i < data.Vegetation.Placements.Length; i++)
            {
                var p = data.Vegetation.Placements[i];
                if (p == null) continue;
                p.PrefabPath = NormalizeAssetReference(p.PrefabPath);
                p.ModelPath = NormalizeAssetReference(p.ModelPath);
                p.TexturePath = NormalizeAssetReference(p.TexturePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Planet asset load failed: {ex.Message}";
            return false;
        }
    }

    public static bool TrySave(string planetAssetPath, PlanetAssetData data, out string? error)
    {
        error = null;
        try
        {
            string abs = ToAbsolutePath(planetAssetPath);
            string? dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            data.BiomeGraphPath = NormalizeProjectRelative(data.BiomeGraphPath);
            data.Config ??= new PlanetConfig();
            data.Config.Biomes ??= BiomeDefinition.AllPresets;
            data.Config.RiverAllowedBiomes ??= Array.Empty<string>();
            data.Vegetation ??= new PlanetVegetationAssetData();
            data.Vegetation.Placements ??= Array.Empty<PlanetVegetationPlacement>();
            for (int i = 0; i < data.Vegetation.Placements.Length; i++)
            {
                var p = data.Vegetation.Placements[i];
                if (p == null) continue;
                p.PrefabPath = NormalizeAssetReference(p.PrefabPath);
                p.ModelPath = NormalizeAssetReference(p.ModelPath);
                p.TexturePath = NormalizeAssetReference(p.TexturePath);
            }

            var json = JsonSerializer.Serialize(data, _json);
            File.WriteAllText(abs, json);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Planet asset save failed: {ex.Message}";
            return false;
        }
    }

    public static string NormalizeProjectRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            var proj = ProjectService.Current;
            if (proj == null) return path.Replace('\\', '/');
            string abs = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(proj.RootPath, path));
            string rel = Path.GetRelativePath(proj.RootPath, abs);
            return rel.Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    public static string ToAbsolutePath(string projectRelativeOrAbsolute)
    {
        var path = projectRelativeOrAbsolute?.Trim() ?? "";
        var proj = ProjectService.Current;
        if (string.IsNullOrWhiteSpace(path))
            return proj != null
                ? Path.Combine(proj.AssetsPath, "Planets")
                : Path.GetFullPath("Assets/Planets");
        if (Path.IsPathRooted(path))
            return path;
        if (proj == null)
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(proj.RootPath, path));
    }

    public static string NormalizeAssetReference(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        string p = NormalizeProjectRelative(path).Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal))
            p = p.Substring(2);

        if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            return p;

        int idx = p.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + 1 < p.Length)
            return p.Substring(idx + 1);

        idx = p.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return p.Substring(idx);

        return p;
    }
}
