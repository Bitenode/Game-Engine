using Game_Engine.Core.Biome;

namespace Game_Engine.Core.Planet;

public sealed class PlanetConfig
{
    public float Radius { get; set; } = 1000f;
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
    public float AltitudeWeight { get; set; } = 0.3f;
    public float EdgeDistortionFreq { get; set; } = 0.01f;
    public float EdgeDistortionAmp { get; set; } = 0.1f;
}
