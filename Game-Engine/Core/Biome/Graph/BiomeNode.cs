#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Biome.Graph;

public enum BiomeDataType { Float, Vec2, Vec3, BiomeLayer, Water, Climate, Life, Scatter, Atmosphere }

public sealed class BiomePort
{
    public string Name { get; set; } = "";
    public BiomeDataType DataType { get; set; }
    public bool IsOutput { get; set; }
    public BiomeNode Owner { get; set; } = null!;
    public float[] DefaultValue { get; set; } = { 0f };
    public BiomePort? Connection { get; set; }
}

/// <summary>Base class for all biome graph nodes.</summary>
public abstract class BiomeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Node";
    public float EditorX { get; set; }
    public float EditorY { get; set; }

    public List<BiomePort> Inputs { get; } = new();
    public List<BiomePort> Outputs { get; } = new();

    protected BiomePort AddInput(string name, BiomeDataType type, params float[] defaultValue)
    {
        var port = new BiomePort
        {
            Name = name, DataType = type, IsOutput = false, Owner = this,
            DefaultValue = defaultValue.Length > 0 ? defaultValue : new[] { 0f }
        };
        Inputs.Add(port);
        return port;
    }

    protected BiomePort AddOutput(string name, BiomeDataType type)
    {
        var port = new BiomePort
        {
            Name = name, DataType = type, IsOutput = true, Owner = this
        };
        Outputs.Add(port);
        return port;
    }

    public float GetInputValue(int index) =>
        index >= 0 && index < Inputs.Count ? BiomeGraph.EvaluateFloat(Inputs[index]) : 0f;

    public BiomePort? FindInput(string name)
    {
        for (int i = 0; i < Inputs.Count; i++)
        {
            if (Inputs[i].Name == name)
                return Inputs[i];
        }
        return null;
    }
}

// ── Coordinate Node ──
public sealed class BiomeCoordinateNode : BiomeNode
{
    public BiomeCoordinateNode()
    {
        Name = "Coordinates";
        AddOutput("Latitude", BiomeDataType.Float);
        AddOutput("Longitude", BiomeDataType.Float);
        AddOutput("Altitude", BiomeDataType.Float);
        AddOutput("SphereDir", BiomeDataType.Vec3);
    }
}

// ── Noise Node ──
public sealed class BiomeNoiseNode : BiomeNode
{
    public float Frequency { get; set; } = 1f;
    public int Octaves { get; set; } = 4;
    public int Seed { get; set; } = 0;
    public string NoiseMode { get; set; } = "FBM"; // FBM, Ridged, Billow

    public BiomeNoiseNode()
    {
        Name = "Noise";
        AddInput("Frequency", BiomeDataType.Float, 1f);
        AddInput("Octaves", BiomeDataType.Float, 4f);
        AddOutput("Value", BiomeDataType.Float);
    }
}

// ── Temperature Node ──
public sealed class BiomeTemperatureNode : BiomeNode
{
    public float LatitudeWeight { get; set; } = 1f;
    public float NoiseWeight { get; set; } = 0.15f;

    public BiomeTemperatureNode()
    {
        Name = "Temperature";
        AddInput("LatitudeWeight", BiomeDataType.Float, 1f);
        AddInput("NoiseWeight", BiomeDataType.Float, 0.15f);
        AddOutput("Temperature", BiomeDataType.Float);
    }
}

// ── Moisture Node ──
public sealed class BiomeMoistureNode : BiomeNode
{
    public float NoiseScale { get; set; } = 3f;

    public BiomeMoistureNode()
    {
        Name = "Moisture";
        AddInput("NoiseScale", BiomeDataType.Float, 3f);
        AddOutput("Moisture", BiomeDataType.Float);
    }
}

// ── Biome Select Node ──
public sealed class BiomeSelectNode : BiomeNode
{
    public BiomeSelectNode()
    {
        Name = "Biome Select";
        AddInput("Temperature", BiomeDataType.Float, 0.5f);
        AddInput("Moisture", BiomeDataType.Float, 0.5f);
        AddInput("Altitude", BiomeDataType.Float, 0f);
        AddOutput("BiomeIndex", BiomeDataType.Climate);
        AddOutput("BlendWeights", BiomeDataType.Vec3);
    }
}

// ── Biome Layer Node ──
public sealed class BiomeLayerNode : BiomeNode
{
    public string AlbedoPath { get; set; } = "";
    public string NormalPath { get; set; } = "";
    public float Tiling { get; set; } = 10f;
    public float Roughness { get; set; } = 0.8f;
    public float Metallic { get; set; } = 0f;
    public float BaseColorR { get; set; } = 0.5f;
    public float BaseColorG { get; set; } = 0.5f;
    public float BaseColorB { get; set; } = 0.5f;
    public string BiomeName { get; set; } = "Unnamed";

    public string UnderTexturePath { get; set; } = "";
    public string UnderNormalPath { get; set; } = "";
    public float UnderTiling { get; set; } = 10f;
    public string NoiseMode { get; set; } = "FBM";
    public int NoiseOctaves { get; set; } = 6;
    public float ErosionStrength { get; set; } = 0f;
    public float ErosionFrequency { get; set; } = 0.01f;

    public bool SpawnWater { get; set; } = false;
    public float WaterShallowR { get; set; } = 0.08f;
    public float WaterShallowG { get; set; } = 0.30f;
    public float WaterShallowB { get; set; } = 0.38f;
    public float WaterDeepR { get; set; } = 0.02f;
    public float WaterDeepG { get; set; } = 0.08f;
    public float WaterDeepB { get; set; } = 0.22f;
    public float VegetationDensity { get; set; } = 0f;
    public float TreeDensity { get; set; } = 0f;
    public string VegetationProfileId { get; set; } = "Default";
    public float VegetationPatchiness { get; set; } = 0.45f;
    public string WeatherProfileId { get; set; } = "Temperate";
    public float RainChance { get; set; } = 0.15f;
    public float SnowChance { get; set; } = 0.04f;
    public float StormChance { get; set; } = 0.01f;
    public float WindBias { get; set; } = 1f;
    public float CloudCoverageBias { get; set; } = 1f;
    public float FogDensityBias { get; set; } = 1f;
    public float SeasonalGrowthMultiplier { get; set; } = 1f;

    /// <summary>Graph-authored height amplitude. &lt;= 0 means fall back to preset.</summary>
    public float HeightAmplitude { get; set; } = -1f;
    /// <summary>Graph-authored noise frequency. &lt;= 0 means fall back to preset.</summary>
    public float NoiseFrequency { get; set; } = -1f;

    public float GrowthTemperatureMin { get; set; } = 0.2f;
    public float GrowthTemperatureMax { get; set; } = 0.8f;
    public float GrowthMoistureMin { get; set; } = 0.2f;
    public float GrowthMoistureMax { get; set; } = 0.9f;

    public BiomeLayerNode()
    {
        Name = "Biome Layer";
        AddInput("HeightAmp", BiomeDataType.Float, -1f);
        AddInput("NoiseFreq", BiomeDataType.Float, -1f);
        AddInput("Erosion", BiomeDataType.Float, -1f);
        AddOutput("Layer", BiomeDataType.BiomeLayer);
    }
}

// ── Blend Node ──
public sealed class BiomeBlendNode : BiomeNode
{
    public BiomeBlendNode()
    {
        Name = "Blend";
        AddInput("A", BiomeDataType.Float, 0f);
        AddInput("B", BiomeDataType.Float, 1f);
        AddInput("Weight", BiomeDataType.Float, 0.5f);
        AddOutput("Result", BiomeDataType.Float);
    }
}

// ── Math Node ──
public enum BiomeMathOp { Add, Subtract, Multiply, Divide, Clamp, Smoothstep, Remap, Min, Max }

public sealed class BiomeMathNode : BiomeNode
{
    public BiomeMathOp Operation { get; set; } = BiomeMathOp.Add;

    public BiomeMathNode()
    {
        Name = "Math";
        AddInput("A", BiomeDataType.Float, 0f);
        AddInput("B", BiomeDataType.Float, 1f);
        AddOutput("Result", BiomeDataType.Float);
    }
}

// ── Height Node ──
public sealed class BiomeHeightNode : BiomeNode
{
    public float BaseHeight { get; set; } = 0f;
    public float Amplitude { get; set; } = 50f;

    public BiomeHeightNode()
    {
        Name = "Height";
        AddInput("NoiseValue", BiomeDataType.Float, 0f);
        AddInput("BaseHeight", BiomeDataType.Float, 0f);
        AddInput("Amplitude", BiomeDataType.Float, 50f);
        AddOutput("Height", BiomeDataType.Float);
    }
}

// ── Cave Node ──
public sealed class BiomeCaveNode : BiomeNode
{
    public float Frequency { get; set; } = 0.02f;
    public float Threshold { get; set; } = 0.18f;

    public BiomeCaveNode()
    {
        Name = "Cave";
        AddInput("Frequency", BiomeDataType.Float, 0.02f);
        AddInput("Threshold", BiomeDataType.Float, 0.18f);
        AddOutput("CaveMask", BiomeDataType.Float);
    }
}

// ── Output Node (terminal) ──
public sealed class BiomeOutputNode : BiomeNode
{
    public const int MaxLayerSlots = 16;

    public BiomeOutputNode()
    {
        Name = "Output";
        AddInput("Height", BiomeDataType.Float, 50f);
        AddInput("CaveMask", BiomeDataType.Float, 0f);
        for (int i = 0; i < MaxLayerSlots; i++)
            AddInput($"Layer{i}", BiomeDataType.BiomeLayer);
        AddInput("Water", BiomeDataType.Water);
        AddInput("Climate", BiomeDataType.Climate);
        AddInput("Life", BiomeDataType.Life);
        AddInput("Scatter", BiomeDataType.Scatter);
        AddInput("Atmosphere", BiomeDataType.Atmosphere);
    }
}

// ── Altitude Node ──
public sealed class BiomeAltitudeNode : BiomeNode
{
    public float SeaLevel { get; set; } = 0f;
    public float MaxHeight { get; set; } = 1f;

    public BiomeAltitudeNode()
    {
        Name = "Altitude";
        AddInput("RawHeight", BiomeDataType.Float, 0f);
        AddInput("SeaLevel", BiomeDataType.Float, 0f);
        AddInput("MaxHeight", BiomeDataType.Float, 1f);
        AddOutput("NormalizedAlt", BiomeDataType.Float);
    }
}

// ── Slope Node ──
public sealed class BiomeSlopeNode : BiomeNode
{
    public float SlopeScale { get; set; } = 1f;

    public BiomeSlopeNode()
    {
        Name = "Slope";
        AddInput("Normal", BiomeDataType.Vec3);
        AddOutput("SlopeAngle", BiomeDataType.Float);
    }
}

// ── Erosion Node ──
public sealed class BiomeErosionNode : BiomeNode
{
    public float Strength { get; set; } = 0.5f;
    public float Frequency { get; set; } = 0.02f;
    public int Octaves { get; set; } = 4;

    public BiomeErosionNode()
    {
        Name = "Erosion";
        AddInput("Strength", BiomeDataType.Float, 0.5f);
        AddInput("Frequency", BiomeDataType.Float, 0.02f);
        AddOutput("ErosionMask", BiomeDataType.Float);
    }
}

// ── Mask Node ──
public enum BiomeMaskBlendMode { Add, Multiply, Screen, Overlay, Subtract }

public sealed class BiomeMaskNode : BiomeNode
{
    public BiomeMaskBlendMode BlendMode { get; set; } = BiomeMaskBlendMode.Multiply;

    public BiomeMaskNode()
    {
        Name = "Mask";
        AddInput("A", BiomeDataType.Float, 1f);
        AddInput("B", BiomeDataType.Float, 1f);
        AddOutput("Result", BiomeDataType.Float);
    }
}

// ── River Node ──
public sealed class BiomeRiverNode : BiomeNode
{
    public float RiverWidth { get; set; } = 0.02f;
    public float RiverDepth { get; set; } = 5f;
    public float Frequency { get; set; } = 0.003f;
    public float Meander { get; set; } = 0.5f;
    public string AllowedBiomes { get; set; } = "";

    public float SandWidth { get; set; } = 0.04f;
    public string SandBiomeName { get; set; } = "Beach";
    public bool FlowToOcean { get; set; } = true;

    public BiomeRiverNode()
    {
        Name = "River";
        AddInput("Width", BiomeDataType.Float, 0.02f);
        AddInput("Depth", BiomeDataType.Float, 5f);
        AddOutput("RiverMask", BiomeDataType.Float);
        AddOutput("RiverDepth", BiomeDataType.Float);
        AddOutput("Water", BiomeDataType.Water);
    }
}

// ── Water Body Node ──
public sealed class BiomeWaterBodyNode : BiomeNode
{
    public string Kind { get; set; } = "Ocean";
    public float FillFraction { get; set; } = 0.55f;
    public string AllowedBiomes { get; set; } = "";
    public float MinBasinDepth { get; set; } = 8f;
    public float ShallowR { get; set; } = 0.08f;
    public float ShallowG { get; set; } = 0.30f;
    public float ShallowB { get; set; } = 0.38f;
    public float DeepR { get; set; } = 0.02f;
    public float DeepG { get; set; } = 0.08f;
    public float DeepB { get; set; } = 0.22f;
    public float DeepestR { get; set; } = 0.01f;
    public float DeepestG { get; set; } = 0.04f;
    public float DeepestB { get; set; } = 0.12f;
    public string ShoreBiomeName { get; set; } = "Beach";
    public float ShoreWidth { get; set; } = 0.08f;

    public BiomeWaterBodyNode()
    {
        Name = "Water Body";
        AddOutput("Water", BiomeDataType.Water);
    }
}

// ── Water Path Node ──
public sealed class BiomeWaterPathNode : BiomeNode
{
    public float Width { get; set; } = 0.02f;
    public float Depth { get; set; } = 5f;
    public float Frequency { get; set; } = 0.003f;
    public float Meander { get; set; } = 0.5f;
    public string AllowedBiomes { get; set; } = "";
    public float SandWidth { get; set; } = 0.04f;
    public string SandBiomeName { get; set; } = "Beach";
    public bool FlowToOcean { get; set; } = true;

    public BiomeWaterPathNode()
    {
        Name = "Water Path";
        AddInput("Width", BiomeDataType.Float, 0.02f);
        AddInput("Depth", BiomeDataType.Float, 5f);
        AddOutput("Water", BiomeDataType.Water);
    }
}

// ── Shore Node ──
public sealed class BiomeShoreNode : BiomeNode
{
    public string ShoreBiomeName { get; set; } = "Beach";
    public float ShoreWidth { get; set; } = 0.08f;
    public string TexturePath { get; set; } = "";
    public float Tiling { get; set; } = 28f;

    public BiomeShoreNode()
    {
        Name = "Shore";
        AddInput("Water", BiomeDataType.Water);
        AddOutput("Water", BiomeDataType.Water);
    }
}

// ── Water Merge Node ──
public sealed class BiomeWaterMergeNode : BiomeNode
{
    public BiomeWaterMergeNode()
    {
        Name = "Water Merge";
        AddInput("A", BiomeDataType.Water);
        AddInput("B", BiomeDataType.Water);
        AddOutput("Water", BiomeDataType.Water);
    }
}

// ── Geology: Continent ──
public sealed class BiomeContinentNode : BiomeNode
{
    public float Frequency { get; set; } = 0.0015f;
    public float Threshold { get; set; } = 0.45f;
    public float Strength { get; set; } = 1f;
    public int Seed { get; set; }

    public BiomeContinentNode()
    {
        Name = "Continent";
        AddInput("Frequency", BiomeDataType.Float, 0.0015f);
        AddInput("Threshold", BiomeDataType.Float, 0.45f);
        AddOutput("LandMask", BiomeDataType.Float);
    }
}

// ── Geology: Crater ──
public sealed class BiomeCraterNode : BiomeNode
{
    public float Radius { get; set; } = 0.08f;
    public float Depth { get; set; } = 25f;
    public float RimHeight { get; set; } = 8f;
    public float Density { get; set; } = 0.35f;
    public int Seed { get; set; }

    public BiomeCraterNode()
    {
        Name = "Crater";
        AddInput("Radius", BiomeDataType.Float, 0.08f);
        AddInput("Depth", BiomeDataType.Float, 25f);
        AddOutput("HeightDelta", BiomeDataType.Float);
    }
}

// ── Geology: Volcano ──
public sealed class BiomeVolcanoNode : BiomeNode
{
    public float Radius { get; set; } = 0.06f;
    public float Height { get; set; } = 80f;
    public float CalderaRadius { get; set; } = 0.015f;
    public string LavaBiomeName { get; set; } = "Volcanic";
    public float Density { get; set; } = 0.2f;
    public int Seed { get; set; }

    public BiomeVolcanoNode()
    {
        Name = "Volcano";
        AddInput("Radius", BiomeDataType.Float, 0.06f);
        AddInput("Height", BiomeDataType.Float, 80f);
        AddOutput("HeightDelta", BiomeDataType.Float);
    }
}

// ── Geology: Cliff ──
public sealed class BiomeCliffNode : BiomeNode
{
    public float Strength { get; set; } = 1.5f;
    public float Frequency { get; set; } = 0.01f;
    public float SlopeBias { get; set; } = 0.6f;

    public BiomeCliffNode()
    {
        Name = "Cliff";
        AddInput("Strength", BiomeDataType.Float, 1.5f);
        AddInput("Frequency", BiomeDataType.Float, 0.01f);
        AddOutput("Escarpment", BiomeDataType.Float);
    }
}

// ── Geology: Domain Warp ──
public sealed class BiomeDomainWarpNode : BiomeNode
{
    public float Strength { get; set; } = 0.15f;
    public float Frequency { get; set; } = 0.004f;
    public int Octaves { get; set; } = 3;
    public int Seed { get; set; }

    public BiomeDomainWarpNode()
    {
        Name = "Domain Warp";
        AddInput("Value", BiomeDataType.Float, 0f);
        AddInput("Strength", BiomeDataType.Float, 0.15f);
        AddOutput("Warped", BiomeDataType.Float);
    }
}

// ── Climate: Climate ──
public sealed class BiomeClimateNode : BiomeNode
{
    public float LatitudeWeight { get; set; } = 1f;
    public float AltitudeLapse { get; set; } = 0.45f;
    public float MoistureWeight { get; set; } = 1f;
    public float NoiseWeight { get; set; } = 0.12f;

    public BiomeClimateNode()
    {
        Name = "Climate";
        AddInput("Temperature", BiomeDataType.Float, 0.5f);
        AddInput("Moisture", BiomeDataType.Float, 0.5f);
        AddInput("Altitude", BiomeDataType.Float, 0.35f);
        AddOutput("Climate", BiomeDataType.Climate);
    }
}

// ── Climate: Rain Shadow ──
public sealed class BiomeRainShadowNode : BiomeNode
{
    public float Strength { get; set; } = 0.55f;
    public float Width { get; set; } = 0.12f;
    public float RidgeFrequency { get; set; } = 0.008f;

    public BiomeRainShadowNode()
    {
        Name = "Rain Shadow";
        AddInput("Moisture", BiomeDataType.Float, 0.5f);
        AddInput("Strength", BiomeDataType.Float, 0.55f);
        AddOutput("Moisture", BiomeDataType.Float);
    }
}

// ── Climate: Season ──
public sealed class BiomeSeasonNode : BiomeNode
{
    public float GrowthMultiplier { get; set; } = 1f;
    public float SnowLineAltitude { get; set; } = 0.72f;
    public float SeasonPhase { get; set; }

    public BiomeSeasonNode()
    {
        Name = "Season";
        AddInput("Phase", BiomeDataType.Float, 0f);
        AddOutput("GrowthMul", BiomeDataType.Float);
        AddOutput("SnowLine", BiomeDataType.Float);
    }
}

// ── Climate: Latitude Band ──
public sealed class BiomeLatitudeBandNode : BiomeNode
{
    public float MinLatitude { get; set; } = -0.35f;
    public float MaxLatitude { get; set; } = 0.35f;
    public float TemperatureBias { get; set; }
    public float MoistureBias { get; set; }
    public string BandName { get; set; } = "Temperate";

    public BiomeLatitudeBandNode()
    {
        Name = "Latitude Band";
        AddOutput("Mask", BiomeDataType.Float);
        AddOutput("Climate", BiomeDataType.Climate);
    }
}

// ── Life: Flora Layer ──
public sealed class BiomeFloraLayerNode : BiomeNode
{
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

    public BiomeFloraLayerNode()
    {
        Name = "Flora Layer";
        AddInput("Mask", BiomeDataType.Float, 1f);
        AddOutput("Life", BiomeDataType.Life);
    }
}

// ── Life: Scatter Layer ──
public sealed class BiomeScatterLayerNode : BiomeNode
{
    public string ProfileId { get; set; } = "Default";
    public string TargetBiome { get; set; } = "";
    public float RockDensity { get; set; } = 0.4f;
    public float DebrisDensity { get; set; } = 0.2f;
    public float MinSlope { get; set; }
    public float MaxSlope { get; set; } = 55f;
    public float MinAltitude { get; set; }
    public float MaxAltitude { get; set; } = 1f;
    public string ScatterType { get; set; } = "Rock";

    public BiomeScatterLayerNode()
    {
        Name = "Scatter Layer";
        AddInput("Mask", BiomeDataType.Float, 1f);
        AddOutput("Scatter", BiomeDataType.Scatter);
    }
}

// ── Life: Fauna Layer ──
public sealed class BiomeFaunaLayerNode : BiomeNode
{
    public string SpeciesId { get; set; } = "Deer";
    public string TargetBiome { get; set; } = "";
    public float HerdSpacing { get; set; } = 18f;
    public float Density { get; set; } = 0.15f;
    public bool Diurnal { get; set; } = true;
    public string BiomeMask { get; set; } = "";

    public BiomeFaunaLayerNode()
    {
        Name = "Fauna Layer";
        AddInput("Mask", BiomeDataType.Float, 1f);
        AddOutput("Life", BiomeDataType.Life);
    }
}

// ── Life: Underwater Life ──
public sealed class BiomeUnderwaterLifeNode : BiomeNode
{
    public string ProfileId { get; set; } = "Ocean";
    public float KelpDensity { get; set; } = 0.4f;
    public float CoralDensity { get; set; } = 0.2f;
    public float FishDensity { get; set; } = 0.25f;
    public float MinDepth { get; set; } = 2f;
    public float MaxDepth { get; set; } = 80f;
    public bool RequireWaterPlanet { get; set; }

    public BiomeUnderwaterLifeNode()
    {
        Name = "Underwater Life";
        AddInput("Mask", BiomeDataType.Float, 1f);
        AddOutput("Life", BiomeDataType.Life);
    }
}

// ── Life: Resource Vein ──
public sealed class BiomeResourceVeinNode : BiomeNode
{
    public string ResourceId { get; set; } = "Ore";
    public float Density { get; set; } = 0.35f;
    public float Frequency { get; set; } = 0.025f;
    public float CaveOnlyBias { get; set; } = 1f;
    public int Seed { get; set; }

    public BiomeResourceVeinNode()
    {
        Name = "Resource Vein";
        AddInput("CaveMask", BiomeDataType.Float, 1f);
        AddOutput("Density", BiomeDataType.Float);
    }
}

// ── Atmosphere ──
public sealed class BiomeAtmosphereNode : BiomeNode
{
    public string Preset { get; set; } = "EarthLike";
    public float RayleighStrength { get; set; } = 1f;
    public float MieStrength { get; set; } = 0.3f;
    public float DayLengthMinutes { get; set; } = 20f;
    public float AtmosphereHeight { get; set; } = 220f;

    public BiomeAtmosphereNode()
    {
        Name = "Atmosphere";
        AddOutput("Atmosphere", BiomeDataType.Atmosphere);
    }
}

public sealed class BiomeWeatherProfileNode : BiomeNode
{
    public string ProfileId { get; set; } = "Temperate";
    public float RainChance { get; set; } = 0.15f;
    public float SnowChance { get; set; } = 0.04f;
    public float StormChance { get; set; } = 0.01f;
    public float WindBias { get; set; } = 1f;
    public float CloudCoverageBias { get; set; } = 1f;
    public float FogDensityBias { get; set; } = 1f;

    public BiomeWeatherProfileNode()
    {
        Name = "Weather Profile";
        AddOutput("Climate", BiomeDataType.Climate);
    }
}

public sealed class BiomeCloudLayerNode : BiomeNode
{
    public float Coverage { get; set; } = 0.46f;
    public float Density { get; set; } = 1f;
    public float BaseHeight { get; set; } = 120f;
    public float TopHeight { get; set; } = 220f;
    public string CloudType { get; set; } = "Cumulus";

    public BiomeCloudLayerNode()
    {
        Name = "Cloud Layer";
        AddInput("Coverage", BiomeDataType.Float, 0.46f);
        AddOutput("Atmosphere", BiomeDataType.Atmosphere);
    }
}

// ── Water extras ──
public sealed class BiomeIceSheetNode : BiomeNode
{
    public float MaxTemperature { get; set; } = 0.28f;
    public float Thickness { get; set; } = 12f;
    public float Coverage { get; set; } = 0.7f;
    public string TargetWaterKind { get; set; } = "Ocean";

    public BiomeIceSheetNode()
    {
        Name = "Ice Sheet";
        AddInput("Temperature", BiomeDataType.Float, 0.2f);
        AddOutput("Water", BiomeDataType.Water);
    }
}

public sealed class BiomeWetlandNode : BiomeNode
{
    public float FloodDepth { get; set; } = 1.5f;
    public float ReedDensity { get; set; } = 0.55f;
    public float MoistureBoost { get; set; } = 0.35f;
    public string TargetBiome { get; set; } = "Grassland";

    public BiomeWetlandNode()
    {
        Name = "Wetland";
        AddInput("Moisture", BiomeDataType.Float, 0.7f);
        AddOutput("Water", BiomeDataType.Water);
        AddOutput("Life", BiomeDataType.Life);
    }
}
