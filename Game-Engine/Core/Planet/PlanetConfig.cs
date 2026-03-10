using System;
using Game_Engine.Core.Biome;

namespace Game_Engine.Core.Planet;

public sealed class PlanetConfig
{
    public float Radius { get; set; } = 1000f;
    /// <summary>World-space uniform scale multiplier from the planet transform.</summary>
    public float WorldRadiusScale { get; set; } = 1f;
    public float EffectiveWorldRadius => Radius * MathF.Max(0.0001f, WorldRadiusScale);
    public float SeaLevel { get; set; } = 0.35f;
    public int MaxLodDepth { get; set; } = 8;
    public int ChunkSize { get; set; } = 32;
    public float LodDistanceMultiplier { get; set; } = 5.0f;
    /// <summary>
    /// Multiplier applied to split distance checks. Lower values split less aggressively.
    /// </summary>
    public float SplitDistanceScale { get; set; } = 0.75f;
    public int Seed { get; set; } = 42;
    public bool EnableCaves { get; set; } = true;
    public BiomeDefinition[] Biomes { get; set; } = BiomeDefinition.AllPresets;

    /// <summary>
    /// Global cap for total quadtree leaves across all 6 faces.
    /// Reduces worst-case chunk explosion near the camera.
    /// </summary>
    public int MaxLeafNodes { get; set; } = 640;

    /// <summary>
    /// Hard cap for active chunk GameObjects kept around the player.
    /// Farther chunks are unloaded and regenerated when needed.
    /// </summary>
    public int MaxActiveChunks { get; set; } = 120;

    /// <summary>Cap completed mesh apply operations per update tick.</summary>
    public int MaxMeshAppliesPerUpdate { get; set; } = 32;

    /// <summary>Cap new mesh generation jobs scheduled per update tick.</summary>
    public int MaxGenerationSchedulesPerUpdate { get; set; } = 32;

    public float CaveFrequency { get; set; } = 0.02f;
    public float CaveThreshold { get; set; } = 0.18f;
    public float CaveDepth { get; set; } = 10f;

    public float TemperatureLatWeight { get; set; } = 1f;
    public float TemperatureNoiseWeight { get; set; } = 0.15f;
    public float MoistureNoiseScale { get; set; } = 3f;
    public float MacroFrequency { get; set; } = 0.0015f;
    public float RidgeStrength { get; set; } = 0f;
    public float BasinStrength { get; set; } = 0f;
    public float TemperatureBias { get; set; } = 0f;
    public float MoistureBias { get; set; } = 0f;
    public float AltitudeWeight { get; set; } = 0.3f;
    public float EdgeDistortionFreq { get; set; } = 0.01f;
    public float EdgeDistortionAmp { get; set; } = 0.1f;

    public bool EnableAdaptiveScheduling { get; set; } = true;
    public int AdaptiveMinScheduleBudget { get; set; } = 12;
    public int AdaptiveMaxScheduleBudget { get; set; } = 64;
    public float AdaptiveMotionBoost { get; set; } = 1.0f;
    public float AdaptiveAltitudeBoost { get; set; } = 0.6f;
    public float AdaptiveActiveChunkBoost { get; set; } = 0.35f;
    public float MergeDistanceScale { get; set; } = 1.35f;

    public bool HasRiver { get; set; } = false;
    public float RiverWidth { get; set; } = 0.02f;
    public float RiverDepth { get; set; } = 5f;
    public float RiverFrequency { get; set; } = 0.003f;
    public float RiverMeander { get; set; } = 0.5f;
    public string[] RiverAllowedBiomes { get; set; } = System.Array.Empty<string>();

    /// <summary>Signed-density iso-surface search range around the procedural surface radius.</summary>
    public float VoxelIsoSearchRange { get; set; } = 96f;

    /// <summary>Number of radial samples used to detect an iso-surface crossing.</summary>
    public int VoxelIsoSearchSteps { get; set; } = 20;

    /// <summary>Max voxel edit commands consumed per chunk update.</summary>
    public int MaxEditCommandsPerUpdate { get; set; } = 8;

    /// <summary>Max leaves marked dirty by manipulation edits per chunk update.</summary>
    public int MaxEditDirtyLeavesPerUpdate { get; set; } = 96;
}
