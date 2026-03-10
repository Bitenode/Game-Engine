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
}
