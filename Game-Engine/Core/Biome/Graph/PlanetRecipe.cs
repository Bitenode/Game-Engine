#nullable enable
using System;

namespace Game_Engine.Core.Biome.Graph;

/// <summary>
/// Compile-time planet recipe produced by <see cref="BiomeGraph.Compile"/>.
/// Climate, geology, classifier, cave, and output-port payloads, plus
/// life/scatter/atmosphere/geology feature tables.
/// </summary>
public sealed class PlanetRecipe
{
    public ClimateRecipe Climate { get; set; } = new();
    public GeologyRecipe Geology { get; set; } = new();
    public BiomeClassifierRecipe Classifier { get; set; } = new();
    public CaveRecipe Cave { get; set; } = new();
    public GraphPortRecipe Life { get; set; } = new();
    public GraphPortRecipe Scatter { get; set; } = new();
    public GraphPortRecipe Atmosphere { get; set; } = new();

    // Feature tables
    public ContinentRecipe[] Continents { get; set; } = Array.Empty<ContinentRecipe>();
    public CraterRecipe[] Craters { get; set; } = Array.Empty<CraterRecipe>();
    public VolcanoRecipe[] Volcanoes { get; set; } = Array.Empty<VolcanoRecipe>();
    public CliffRecipe[] Cliffs { get; set; } = Array.Empty<CliffRecipe>();
    public DomainWarpRecipe[] DomainWarps { get; set; } = Array.Empty<DomainWarpRecipe>();
    public ClimateNodeRecipe[] ClimateNodes { get; set; } = Array.Empty<ClimateNodeRecipe>();
    public RainShadowRecipe[] RainShadows { get; set; } = Array.Empty<RainShadowRecipe>();
    public SeasonRecipe[] Seasons { get; set; } = Array.Empty<SeasonRecipe>();
    public LatitudeBandRecipe[] LatitudeBands { get; set; } = Array.Empty<LatitudeBandRecipe>();
    public FloraLayerRecipe[] FloraLayers { get; set; } = Array.Empty<FloraLayerRecipe>();
    public ScatterLayerRecipe[] ScatterLayers { get; set; } = Array.Empty<ScatterLayerRecipe>();
    public FaunaLayerRecipe[] FaunaLayers { get; set; } = Array.Empty<FaunaLayerRecipe>();
    public UnderwaterLifeRecipe[] UnderwaterLife { get; set; } = Array.Empty<UnderwaterLifeRecipe>();
    public ResourceVeinRecipe[] ResourceVeins { get; set; } = Array.Empty<ResourceVeinRecipe>();
    public AtmosphereNodeRecipe[] AtmosphereNodes { get; set; } = Array.Empty<AtmosphereNodeRecipe>();
    public WeatherProfileRecipe[] WeatherProfiles { get; set; } = Array.Empty<WeatherProfileRecipe>();
    public CloudLayerRecipe[] CloudLayers { get; set; } = Array.Empty<CloudLayerRecipe>();
    public IceSheetRecipe[] IceSheets { get; set; } = Array.Empty<IceSheetRecipe>();
    public WetlandRecipe[] Wetlands { get; set; } = Array.Empty<WetlandRecipe>();
}

public sealed class ClimateRecipe
{
    public float TemperatureLatWeight { get; set; } = 1f;
    public float TemperatureNoiseWeight { get; set; } = 0.15f;
    public float MoistureNoiseScale { get; set; } = 3f;
    public float AltitudeSeaLevel { get; set; }
    public float AltitudeMaxHeight { get; set; } = 1f;
    public float AltitudeWeight { get; set; } = 0.3f;
    /// <summary>Temperature drop over normalized altitude.</summary>
    public float AltitudeLapseRate { get; set; } = 0.35f;
    public float RainShadowStrength { get; set; } = 0.45f;
    public float WaterMoistureBoost { get; set; } = 0.35f;
    public bool HasAltitudeFromGraph { get; set; }
}

public sealed class GeologyRecipe
{
    public float HeightAmplitude { get; set; } = 50f;
    public float NoiseFrequency { get; set; } = 0.005f;
    public int NoiseOctaves { get; set; } = 6;
    public string NoiseMode { get; set; } = "FBM";
    public float NoiseLacunarity { get; set; } = 2f;
    public float NoisePersistence { get; set; } = 0.5f;
    public float ErosionStrength { get; set; }
    public float ErosionFrequency { get; set; } = 0.01f;
    public int ErosionOctaves { get; set; } = 4;
    public float MacroFrequency { get; set; } = 0.0015f;
}

/// <summary>One compiled BiomeSelect climate box, usually copied from a named preset.</summary>
public sealed class BiomeSelectRule
{
    public string BiomeName { get; set; } = "";
    public int LayerIndex { get; set; }
    public float MinTemperature { get; set; }
    public float MaxTemperature { get; set; } = 1f;
    public float MinMoisture { get; set; }
    public float MaxMoisture { get; set; } = 1f;
    public float MinAltitude { get; set; }
    public float MaxAltitude { get; set; } = 1f;
}

public sealed class BiomeClassifierRecipe
{
    public bool UseSelectClassifier { get; set; }
    public BiomeSelectRule[] Rules { get; set; } = Array.Empty<BiomeSelectRule>();
}

public sealed class CaveRecipe
{
    public bool Enable { get; set; } = true;
    public float Frequency { get; set; } = 0.02f;
    public float Threshold { get; set; } = 0.18f;
}

/// <summary>Compiled Output.Life / Scatter / Atmosphere payload for later phases.</summary>
public sealed class GraphPortRecipe
{
    public bool Connected { get; set; }
    public string NodeId { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string PortName { get; set; } = "";
    public float EvaluatedValue { get; set; }
}

// ── Geology ──

public sealed class ContinentRecipe
{
    public string NodeId { get; set; } = "";
    public float Frequency { get; set; } = 0.0015f;
    public float Threshold { get; set; } = 0.45f;
    public float Strength { get; set; } = 1f;
    public int Seed { get; set; }
}

public sealed class CraterRecipe
{
    public string NodeId { get; set; } = "";
    public float Radius { get; set; } = 0.08f;
    public float Depth { get; set; } = 25f;
    public float RimHeight { get; set; } = 8f;
    public float Density { get; set; } = 0.35f;
    public int Seed { get; set; }
}

public sealed class VolcanoRecipe
{
    public string NodeId { get; set; } = "";
    public float Radius { get; set; } = 0.06f;
    public float Height { get; set; } = 80f;
    public float CalderaRadius { get; set; } = 0.015f;
    public string LavaBiomeName { get; set; } = "Volcanic";
    public float Density { get; set; } = 0.2f;
    public int Seed { get; set; }
}

public sealed class CliffRecipe
{
    public string NodeId { get; set; } = "";
    public float Strength { get; set; } = 1.5f;
    public float Frequency { get; set; } = 0.01f;
    public float SlopeBias { get; set; } = 0.6f;
}

public sealed class DomainWarpRecipe
{
    public string NodeId { get; set; } = "";
    public float Strength { get; set; } = 0.15f;
    public float Frequency { get; set; } = 0.004f;
    public int Octaves { get; set; } = 3;
    public int Seed { get; set; }
}

// ── Climate ──

public sealed class ClimateNodeRecipe
{
    public string NodeId { get; set; } = "";
    public float LatitudeWeight { get; set; } = 1f;
    public float AltitudeLapse { get; set; } = 0.45f;
    public float MoistureWeight { get; set; } = 1f;
    public float NoiseWeight { get; set; } = 0.12f;
}

public sealed class RainShadowRecipe
{
    public string NodeId { get; set; } = "";
    public float Strength { get; set; } = 0.55f;
    public float Width { get; set; } = 0.12f;
    public float RidgeFrequency { get; set; } = 0.008f;
}

public sealed class SeasonRecipe
{
    public string NodeId { get; set; } = "";
    public float GrowthMultiplier { get; set; } = 1f;
    public float SnowLineAltitude { get; set; } = 0.72f;
    public float SeasonPhase { get; set; }
}

public sealed class LatitudeBandRecipe
{
    public string NodeId { get; set; } = "";
    public float MinLatitude { get; set; } = -0.35f;
    public float MaxLatitude { get; set; } = 0.35f;
    public float TemperatureBias { get; set; }
    public float MoistureBias { get; set; }
    public string BandName { get; set; } = "Temperate";
}

// ── Life / scatter ──

public sealed class FloraLayerRecipe
{
    public string NodeId { get; set; } = "";
    public string ProfileId { get; set; } = "Forest";
    public string TargetBiome { get; set; } = "";
    public float GrassDensity { get; set; } = 0.8f;
    public float BushDensity { get; set; } = 0.25f;
    public float TreeDensity { get; set; } = 0.6f;
    public float Patchiness { get; set; } = 0.45f;
    public float MinSlope { get; set; }
    public float MaxSlope { get; set; } = 35f;
    public float MinAltitude { get; set; }
    public float MaxAltitude { get; set; } = 0.85f;
    public float GrowthTemperatureMin { get; set; } = 0.2f;
    public float GrowthTemperatureMax { get; set; } = 0.8f;
    public float GrowthMoistureMin { get; set; } = 0.2f;
    public float GrowthMoistureMax { get; set; } = 0.9f;
}

public sealed class ScatterLayerRecipe
{
    public string NodeId { get; set; } = "";
    public string ProfileId { get; set; } = "Default";
    public string TargetBiome { get; set; } = "";
    public float RockDensity { get; set; } = 0.4f;
    public float DebrisDensity { get; set; } = 0.2f;
    public float MinSlope { get; set; }
    public float MaxSlope { get; set; } = 55f;
    public float MinAltitude { get; set; }
    public float MaxAltitude { get; set; } = 1f;
    public string ScatterType { get; set; } = "Rock"; // Rock / Debris / Prop
}

public sealed class FaunaLayerRecipe
{
    public string NodeId { get; set; } = "";
    public string SpeciesId { get; set; } = "Deer";
    public string TargetBiome { get; set; } = "";
    public float HerdSpacing { get; set; } = 18f;
    public float Density { get; set; } = 0.15f;
    public bool Diurnal { get; set; } = true;
    public string BiomeMask { get; set; } = "";
}

public sealed class UnderwaterLifeRecipe
{
    public string NodeId { get; set; } = "";
    public string ProfileId { get; set; } = "Ocean";
    public float KelpDensity { get; set; } = 0.4f;
    public float CoralDensity { get; set; } = 0.2f;
    public float FishDensity { get; set; } = 0.25f;
    public float MinDepth { get; set; } = 2f;
    public float MaxDepth { get; set; } = 80f;
    public bool RequireWaterPlanet { get; set; }
}

public sealed class ResourceVeinRecipe
{
    public string NodeId { get; set; } = "";
    public string ResourceId { get; set; } = "Ore";
    public float Density { get; set; } = 0.35f;
    public float Frequency { get; set; } = 0.025f;
    public float CaveOnlyBias { get; set; } = 1f;
    public int Seed { get; set; }
}

// ── Atmosphere / water extras ──

public sealed class AtmosphereNodeRecipe
{
    public string NodeId { get; set; } = "";
    public string Preset { get; set; } = "EarthLike";
    public float RayleighStrength { get; set; } = 1f;
    public float MieStrength { get; set; } = 0.3f;
    public float DayLengthMinutes { get; set; } = 20f;
    public float AtmosphereHeight { get; set; } = 220f;
}

public sealed class WeatherProfileRecipe
{
    public string NodeId { get; set; } = "";
    public string ProfileId { get; set; } = "Temperate";
    public float RainChance { get; set; } = 0.15f;
    public float SnowChance { get; set; } = 0.04f;
    public float StormChance { get; set; } = 0.01f;
    public float WindBias { get; set; } = 1f;
    public float CloudCoverageBias { get; set; } = 1f;
    public float FogDensityBias { get; set; } = 1f;
}

public sealed class CloudLayerRecipe
{
    public string NodeId { get; set; } = "";
    public float Coverage { get; set; } = 0.46f;
    public float Density { get; set; } = 1f;
    public float BaseHeight { get; set; } = 120f;
    public float TopHeight { get; set; } = 220f;
    public string CloudType { get; set; } = "Cumulus";
}

public sealed class IceSheetRecipe
{
    public string NodeId { get; set; } = "";
    public float MaxTemperature { get; set; } = 0.28f;
    public float Thickness { get; set; } = 12f;
    public float Coverage { get; set; } = 0.7f;
    public string TargetWaterKind { get; set; } = "Ocean";
}

public sealed class WetlandRecipe
{
    public string NodeId { get; set; } = "";
    public float FloodDepth { get; set; } = 1.5f;
    public float ReedDensity { get; set; } = 0.55f;
    public float MoistureBoost { get; set; } = 0.35f;
    public string TargetBiome { get; set; } = "Grassland";
}
