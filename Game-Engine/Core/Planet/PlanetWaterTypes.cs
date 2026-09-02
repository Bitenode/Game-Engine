using System;

namespace Game_Engine.Core.Planet;

public enum PlanetWaterBodyKind
{
    Ocean = 0,
    Lake = 1,
    Pond = 2
}

public enum PlanetWaterKind
{
    None = 0,
    Ocean = 1,
    Lake = 2,
    Pond = 3,
    River = 4,
    Lava = 5
}

public sealed class PlanetWaterBody
{
    public PlanetWaterBodyKind Kind { get; set; } = PlanetWaterBodyKind.Ocean;
    /// <summary>0–1 of terrain min–max radius (same meaning as planet sea-level fraction).</summary>
    public float FillFraction { get; set; } = 0.55f;
    public string[] MaskBiomes { get; set; } = Array.Empty<string>();
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

    public PlanetWaterBody Clone() => new()
    {
        Kind = Kind,
        FillFraction = FillFraction,
        MaskBiomes = MaskBiomes != null && MaskBiomes.Length > 0
            ? (string[])MaskBiomes.Clone()
            : Array.Empty<string>(),
        MinBasinDepth = MinBasinDepth,
        ShallowR = ShallowR, ShallowG = ShallowG, ShallowB = ShallowB,
        DeepR = DeepR, DeepG = DeepG, DeepB = DeepB,
        DeepestR = DeepestR, DeepestG = DeepestG, DeepestB = DeepestB,
        ShoreBiomeName = ShoreBiomeName ?? "Beach",
        ShoreWidth = ShoreWidth,
    };
}

public sealed class PlanetWaterPath
{
    public float Width { get; set; } = 0.02f;
    public float Depth { get; set; } = 5f;
    public float Frequency { get; set; } = 0.003f;
    public float Meander { get; set; } = 0.5f;
    public string[] AllowedBiomes { get; set; } = Array.Empty<string>();
    public float SandWidth { get; set; } = 0.04f;
    public string SandBiomeName { get; set; } = "Beach";
    public bool FlowToOcean { get; set; } = true;

    public PlanetWaterPath Clone() => new()
    {
        Width = Width,
        Depth = Depth,
        Frequency = Frequency,
        Meander = Meander,
        AllowedBiomes = AllowedBiomes != null && AllowedBiomes.Length > 0
            ? (string[])AllowedBiomes.Clone()
            : Array.Empty<string>(),
        SandWidth = SandWidth,
        SandBiomeName = SandBiomeName ?? "Beach",
        FlowToOcean = FlowToOcean,
    };
}

public readonly struct PlanetWaterSurfaceSample
{
    public PlanetWaterSurfaceSample(
        float radius,
        float mask,
        int shoreBiomeIndex,
        PlanetWaterKind kind,
        int bodyIndex)
    {
        Radius = radius;
        Mask = mask;
        ShoreBiomeIndex = shoreBiomeIndex;
        Kind = kind;
        BodyIndex = bodyIndex;
    }

    public float Radius { get; }
    public float Mask { get; }
    public int ShoreBiomeIndex { get; }
    public PlanetWaterKind Kind { get; }
    public int BodyIndex { get; }

    public static PlanetWaterSurfaceSample Empty => new(0f, 0f, 0, PlanetWaterKind.None, -1);
}

public sealed class PlanetWaterCarveContext
{
    public PlanetConfig Config { get; init; } = null!;
    public Noise.SimplexNoise? RiverPrimary { get; init; }
    public Noise.SimplexNoise? RiverMeander { get; init; }
    public PlanetClimateAtlas? ClimateAtlas { get; init; }
}
