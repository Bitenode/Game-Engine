#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Biome.Graph;

public enum BiomeDataType { Float, Vec2, Vec3, BiomeLayer, Water }

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
        Inputs.Count > index && Inputs[index].Connection != null
            ? Inputs[index].DefaultValue[0]
            : (Inputs.Count > index ? Inputs[index].DefaultValue[0] : 0f);
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
        AddOutput("BiomeIndex", BiomeDataType.Float);
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

    public BiomeLayerNode()
    {
        Name = "Biome Layer";
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
    public BiomeOutputNode()
    {
        Name = "Output";
        AddInput("Height", BiomeDataType.Float, 50f);
        AddInput("CaveMask", BiomeDataType.Float, 0f);
        AddInput("Layer0", BiomeDataType.BiomeLayer);
        AddInput("Layer1", BiomeDataType.BiomeLayer);
        AddInput("Layer2", BiomeDataType.BiomeLayer);
        AddInput("Layer3", BiomeDataType.BiomeLayer);
        AddInput("Layer4", BiomeDataType.BiomeLayer);
        AddInput("Layer5", BiomeDataType.BiomeLayer);
        AddInput("Layer6", BiomeDataType.BiomeLayer);
        AddInput("Layer7", BiomeDataType.BiomeLayer);
        AddInput("Water", BiomeDataType.Water);
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
