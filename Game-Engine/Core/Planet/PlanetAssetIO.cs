using System;
using System.IO;
using System.Text.Json;
using Game_Engine.Core.Biome;

namespace Game_Engine.Core.Planet;

public sealed class PlanetAssetData
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string BiomeGraphPath { get; set; } = "";
    public float SeaLevelFraction { get; set; } = 0.55f;
    public bool EnableWater { get; set; } = true;
    public PlanetConfig Config { get; set; } = new();
    public PlanetVegetationAssetData Vegetation { get; set; } = new();

    /// <summary>
    /// Project-relative path to the <c>.planetvox</c> sidecar. Empty means derive from the <c>.planet</c> path.
    /// Voxel payloads stay out of the main JSON.
    /// </summary>
    public string VoxelEditsPath { get; set; } = "";
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
            data.VoxelEditsPath = NormalizeProjectRelative(data.VoxelEditsPath ?? "");
            if (data.Config == null)
                data.Config = new PlanetConfig();
            data.Config.Biomes ??= BiomeDefinition.AllPresets;
            data.Config.RiverAllowedBiomes ??= Array.Empty<string>();
            data.Config.WaterBodies ??= Array.Empty<PlanetWaterBody>();
            data.Config.WaterPaths ??= Array.Empty<PlanetWaterPath>();
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
            data.VoxelEditsPath = NormalizeProjectRelative(data.VoxelEditsPath ?? "");
            data.Config ??= new PlanetConfig();
            data.Config.Biomes ??= BiomeDefinition.AllPresets;
            data.Config.RiverAllowedBiomes ??= Array.Empty<string>();
            data.Config.WaterBodies ??= Array.Empty<PlanetWaterBody>();
            data.Config.WaterPaths ??= Array.Empty<PlanetWaterPath>();
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

    /// <summary>Absolute path of the <c>.planetvox</c> sidecar next to <paramref name="planetAssetPath"/>.</summary>
    public static string GetVoxelEditsSidecarAbsolutePath(string planetAssetPath)
    {
        string abs = ToAbsolutePath(planetAssetPath);
        if (string.IsNullOrWhiteSpace(abs))
            return abs;
        string dir = Path.GetDirectoryName(abs) ?? "";
        string name = Path.GetFileNameWithoutExtension(abs);
        return Path.Combine(dir, name + PlanetVoxelEditAsset.SidecarExtension);
    }

    public static string GetVoxelEditsSidecarProjectRelative(string planetAssetPath)
        => NormalizeProjectRelative(GetVoxelEditsSidecarAbsolutePath(planetAssetPath));

    public static string ResolveVoxelEditsAbsolutePath(string planetAssetPath, string? voxelEditsPath)
    {
        if (!string.IsNullOrWhiteSpace(voxelEditsPath))
            return ToAbsolutePath(voxelEditsPath);
        return GetVoxelEditsSidecarAbsolutePath(planetAssetPath);
    }

    public static bool TryLoadVoxelEdits(string planetAssetPath, string? voxelEditsPath, out PlanetVoxelEditAsset? data, out string? error)
    {
        data = null;
        error = null;
        try
        {
            string abs = ResolveVoxelEditsAbsolutePath(planetAssetPath, voxelEditsPath);
            if (!File.Exists(abs))
            {
                data = null;
                return true;
            }

            var json = File.ReadAllText(abs);
            data = JsonSerializer.Deserialize<PlanetVoxelEditAsset>(json, _json);
            if (data == null)
            {
                error = $"Planet voxel sidecar is empty or invalid JSON: {abs}";
                return false;
            }

            data.Strokes ??= Array.Empty<PlanetVoxelSphereStroke>();
            data.BakedCells ??= Array.Empty<PlanetVoxelBakedCell>();
            if (string.IsNullOrWhiteSpace(data.Space))
                data.Space = PlanetVoxelEditAsset.PlanetLocalUnscaledSpace;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Planet voxel sidecar load failed: {ex.Message}";
            return false;
        }
    }

    public static bool TrySaveVoxelEdits(string planetAssetPath, PlanetVoxelEditAsset data, out string? error, string? voxelEditsPath = null)
    {
        error = null;
        try
        {
            data ??= new PlanetVoxelEditAsset();
            data.Version = PlanetVoxelEditAsset.CurrentVersion;
            data.Space = PlanetVoxelEditAsset.PlanetLocalUnscaledSpace;
            data.Strokes ??= Array.Empty<PlanetVoxelSphereStroke>();
            data.BakedCells ??= Array.Empty<PlanetVoxelBakedCell>();

            string abs = ResolveVoxelEditsAbsolutePath(planetAssetPath, voxelEditsPath);
            string? dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, _json);
            File.WriteAllText(abs, json);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Planet voxel sidecar save failed: {ex.Message}";
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
