using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Biome;

/// <summary>Single material layer within a biome, applied based on slope and altitude.</summary>
public sealed class BiomeMaterialLayer
{
    public string TexturePath { get; set; } = "";
    public float Tiling { get; set; } = 10f;
    public string NormalMapPath { get; set; } = "";
    public float Roughness { get; set; } = 0.8f;
    public float Metallic { get; set; } = 0f;
    public float MinSlope { get; set; } = 0f;
    public float MaxSlope { get; set; } = 90f;
    public float MinAltitude { get; set; } = float.MinValue;
    public float MaxAltitude { get; set; } = float.MaxValue;
}

/// <summary>
/// Defines the properties of a single biome type: terrain shape, materials, vegetation.
/// </summary>
public sealed class BiomeDefinition
{
    public string Name { get; set; } = "Default";
    public byte BiomeIndex { get; set; }

    public float BaseColorR { get; set; } = 0.4f;
    public float BaseColorG { get; set; } = 0.6f;
    public float BaseColorB { get; set; } = 0.3f;

    public float HeightAmplitude { get; set; } = 50f;
    public float NoiseFrequency { get; set; } = 0.005f;
    public float NoiseLacunarity { get; set; } = 2.0f;
    public int NoiseOctaves { get; set; } = 6;
    public string NoiseMode { get; set; } = "FBM";

    public float ErosionStrength { get; set; } = 0f;
    public float ErosionFrequency { get; set; } = 0.01f;

    public float MinAltitude { get; set; } = 0f;
    public float MaxAltitude { get; set; } = 1f;

    public float CaveFrequency { get; set; } = 0.02f;
    public float CaveDensity { get; set; } = 0.7f;
    public float CaveDepth { get; set; } = 10f;

    public float MinTemperature { get; set; } = 0f;
    public float MaxTemperature { get; set; } = 1f;
    public float MinMoisture { get; set; } = 0f;
    public float MaxMoisture { get; set; } = 1f;

    public List<BiomeMaterialLayer> MaterialLayers { get; set; } = new();

    // --- Planet multi-texture (triplanar) ---

    public string TopTexturePath { get; set; } = "";
    public string TopNormalMapPath { get; set; } = "";
    public float TopTiling { get; set; } = 10f;

    public string UnderTexturePath { get; set; } = "";
    public string UnderNormalMapPath { get; set; } = "";
    public float UnderTiling { get; set; } = 10f;

    public bool CavesEnabled { get; set; } = false;
    public string CaveTexturePath { get; set; } = "";
    public string CaveNormalMapPath { get; set; } = "";

    // --- Ocean water (only relevant for Ocean biome) ---

    public string WaterTexturePath { get; set; } = "";
    public string WaterNormalMapPath { get; set; } = "";
    public float WaterShallowColorR { get; set; } = 0.1f;
    public float WaterShallowColorG { get; set; } = 0.4f;
    public float WaterShallowColorB { get; set; } = 0.5f;
    public float WaterDeepColorR { get; set; } = 0.02f;
    public float WaterDeepColorG { get; set; } = 0.1f;
    public float WaterDeepColorB { get; set; } = 0.3f;
    public float WaterDeepestColorR { get; set; } = 0.01f;
    public float WaterDeepestColorG { get; set; } = 0.03f;
    public float WaterDeepestColorB { get; set; } = 0.1f;
    public float WaterDepthColorRange { get; set; } = 50f;

    public float VegetationDensity { get; set; } = 0f;
    public float TreeDensity { get; set; } = 0f;

    // --- Built-in presets ---

    public static BiomeDefinition Ocean => new()
    {
        Name = "Ocean", BiomeIndex = 0,
        BaseColorR = 0.08f, BaseColorG = 0.15f, BaseColorB = 0.45f,
        HeightAmplitude = 5f, NoiseFrequency = 0.002f, NoiseMode = "FBM",
        MinAltitude = 0f, MaxAltitude = 0.2f,
        MinTemperature = 0f, MaxTemperature = 1f,
        MinMoisture = 0.8f, MaxMoisture = 1f,
        TopTiling = 20f, UnderTiling = 15f,
        CavesEnabled = false,
        WaterShallowColorR = 0.08f, WaterShallowColorG = 0.30f, WaterShallowColorB = 0.38f,
        WaterDeepColorR = 0.02f, WaterDeepColorG = 0.08f, WaterDeepColorB = 0.22f,
        WaterDeepestColorR = 0.005f, WaterDeepestColorG = 0.015f, WaterDeepestColorB = 0.06f,
        WaterDepthColorRange = 80f,
    };

    public static BiomeDefinition Beach => new()
    {
        Name = "Beach", BiomeIndex = 1,
        BaseColorR = 0.92f, BaseColorG = 0.85f, BaseColorB = 0.55f,
        HeightAmplitude = 8f, NoiseFrequency = 0.005f, NoiseMode = "FBM",
        MinAltitude = 0.1f, MaxAltitude = 0.3f,
        MinTemperature = 0.3f, MaxTemperature = 1f,
        MinMoisture = 0.4f, MaxMoisture = 0.8f,
        TopTiling = 25f, UnderTiling = 15f,
        CavesEnabled = false,
    };

    public static BiomeDefinition Grassland => new()
    {
        Name = "Grassland", BiomeIndex = 2,
        BaseColorR = 0.35f, BaseColorG = 0.65f, BaseColorB = 0.18f,
        HeightAmplitude = 30f, NoiseFrequency = 0.003f, NoiseMode = "FBM",
        MinAltitude = 0.2f, MaxAltitude = 0.5f,
        MinTemperature = 0.3f, MaxTemperature = 0.7f,
        MinMoisture = 0.3f, MaxMoisture = 0.6f,
        VegetationDensity = 0.5f,
        TopTiling = 15f, UnderTiling = 10f,
        CavesEnabled = true,
    };

    public static BiomeDefinition Forest => new()
    {
        Name = "Forest", BiomeIndex = 3,
        BaseColorR = 0.12f, BaseColorG = 0.42f, BaseColorB = 0.1f,
        HeightAmplitude = 40f, NoiseFrequency = 0.004f, NoiseMode = "FBM",
        MinAltitude = 0.2f, MaxAltitude = 0.6f, ErosionStrength = 0.3f, ErosionFrequency = 0.015f,
        MinTemperature = 0.3f, MaxTemperature = 0.7f,
        MinMoisture = 0.6f, MaxMoisture = 1f,
        VegetationDensity = 0.8f, TreeDensity = 0.6f,
        TopTiling = 12f, UnderTiling = 8f,
        CavesEnabled = true,
    };

    public static BiomeDefinition Desert => new()
    {
        Name = "Desert", BiomeIndex = 4,
        BaseColorR = 0.88f, BaseColorG = 0.72f, BaseColorB = 0.38f,
        HeightAmplitude = 20f, NoiseFrequency = 0.003f, NoiseMode = "Billow",
        MinAltitude = 0.15f, MaxAltitude = 0.45f,
        MinTemperature = 0.6f, MaxTemperature = 1f,
        MinMoisture = 0f, MaxMoisture = 0.3f,
        TopTiling = 30f, UnderTiling = 20f,
        CavesEnabled = false,
    };

    public static BiomeDefinition Tundra => new()
    {
        Name = "Tundra", BiomeIndex = 5,
        BaseColorR = 0.82f, BaseColorG = 0.88f, BaseColorB = 0.92f,
        HeightAmplitude = 15f, NoiseFrequency = 0.004f, NoiseMode = "FBM",
        MinAltitude = 0.6f, MaxAltitude = 1f, ErosionStrength = 0.2f, ErosionFrequency = 0.012f,
        MinTemperature = 0f, MaxTemperature = 0.3f,
        MinMoisture = 0f, MaxMoisture = 0.5f,
        TopTiling = 20f, UnderTiling = 12f,
        CavesEnabled = true,
    };

    public static BiomeDefinition Mountains => new()
    {
        Name = "Mountains", BiomeIndex = 6,
        BaseColorR = 0.5f, BaseColorG = 0.48f, BaseColorB = 0.45f,
        HeightAmplitude = 80f, NoiseFrequency = 0.004f, NoiseLacunarity = 2.2f, NoiseMode = "Ridged",
        MinAltitude = 0.5f, MaxAltitude = 1f, ErosionStrength = 0.5f, ErosionFrequency = 0.02f,
        MinTemperature = 0.1f, MaxTemperature = 0.5f,
        MinMoisture = 0.2f, MaxMoisture = 0.6f,
        TopTiling = 18f, UnderTiling = 10f,
        CavesEnabled = true,
    };

    public static BiomeDefinition Volcanic => new()
    {
        Name = "Volcanic", BiomeIndex = 7,
        BaseColorR = 0.28f, BaseColorG = 0.12f, BaseColorB = 0.08f,
        HeightAmplitude = 60f, NoiseFrequency = 0.005f, NoiseMode = "Ridged",
        MinAltitude = 0.3f, MaxAltitude = 0.8f, ErosionStrength = 0.6f, ErosionFrequency = 0.025f,
        CaveFrequency = 0.03f, CaveDensity = 0.5f,
        MinTemperature = 0.7f, MaxTemperature = 1f,
        MinMoisture = 0f, MaxMoisture = 0.3f,
        TopTiling = 15f, UnderTiling = 10f,
        CavesEnabled = true,
    };

    public static BiomeDefinition[] AllPresets => new[]
    {
        Ocean, Beach, Grassland, Forest, Desert, Tundra, Mountains, Volcanic
    };
}
