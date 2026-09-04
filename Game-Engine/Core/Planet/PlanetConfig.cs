using System;
using System.Text.Json.Serialization;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Biome.Graph;

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

    /// <summary>
    /// Runtime: camera is under the outer crust (caves). Surface walking stays false
    /// so LOD does not remesh the neighborhood into coarse volumetric chunks.
    /// </summary>
    [JsonIgnore]
    public bool CameraBelowCrust { get; set; }

    public BiomeDefinition[] Biomes { get; set; } = BiomeDefinition.AllPresets;

    /// <summary>
    /// Global cap for total quadtree leaves across all 6 faces.
    /// Reduces worst-case chunk explosion near the camera.
    /// </summary>
    public int MaxLeafNodes { get; set; } = 128;

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
    public float CaveDepth { get; set; } = 280f;

    public float TemperatureLatWeight { get; set; } = 1f;
    public float TemperatureNoiseWeight { get; set; } = 0.15f;
    public float MoistureNoiseScale { get; set; } = 3f;
    public float MacroFrequency { get; set; } = 0.0015f;
    public float RidgeStrength { get; set; } = 0f;
    public float BasinStrength { get; set; } = 0f;
    public float TemperatureBias { get; set; } = 0f;
    public float MoistureBias { get; set; } = 0f;
    /// <summary>Temperature drop over normalized altitude [0,1]. Peaks cool toward tundra/snow.</summary>
    public float AltitudeLapseRate { get; set; } = 0.35f;
    /// <summary>Extra moisture near compiled ocean / river / lake (0–1 boost scale).</summary>
    public float WaterMoistureBoost { get; set; } = 0.35f;
    /// <summary>Moisture drop on the lee side of ridges (scales with <see cref="RidgeStrength"/>).</summary>
    public float RainShadowStrength { get; set; } = 0.45f;
    /// <summary>Scales shore sand blend weight by local climate moisture (0 = geometric only).</summary>
    public float ShoreClimateBias { get; set; } = 0.35f;
    public float AltitudeWeight { get; set; } = 0.3f;
    public bool UseSelectClassifier { get; set; }
    public float AltitudeSeaLevel { get; set; }
    public float AltitudeMaxHeight { get; set; } = 1f;
    public float EdgeDistortionFreq { get; set; } = 0.01f;
    public float EdgeDistortionAmp { get; set; } = 0.1f;

    /// <summary>
    /// When true, bake D8 flow-accumulation river channels once from the height LUT.
    /// Noise rivers remain the default (this flag stays false).
    /// </summary>
    public bool UseFlowAccumulationRivers { get; set; } = false;
    /// <summary>Carve depth for flow-accumulation channels (planet-local units).</summary>
    public float FlowRiverDepth { get; set; } = 4f;
    /// <summary>Normalized flow threshold (0–1) before a cell becomes a river channel.</summary>
    public float FlowRiverThreshold { get; set; } = 0.72f;

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

    /// <summary>Graph-authored water bodies (ocean / lake / pond). Empty = legacy sea-level shell.</summary>
    public PlanetWaterBody[] WaterBodies { get; set; } = Array.Empty<PlanetWaterBody>();
    /// <summary>Graph-authored rivers and streams.</summary>
    public PlanetWaterPath[] WaterPaths { get; set; } = Array.Empty<PlanetWaterPath>();

    public const int MaxWaterBodies = 8;
    public bool HasCompiledWaterBodies => WaterBodies != null && WaterBodies.Length > 0;
    public bool NeedsRiverNoise =>
        HasRiver
        || UseFlowAccumulationRivers
        || (WaterPaths != null && WaterPaths.Length > 0);

    /// <summary>
    /// Transvoxel crust is used when a leaf's tangential cell size is at or below this
    /// (planet-local units). Coarser leaves use a smooth spherical heightfield shell so
    /// orbit / default Scene View does not look like marching-cubes shards.
    /// </summary>
    public float VolumetricMaxCellSize { get; set; } = 3.5f;

    /// <summary>
    /// Generate Lengyel transition cells on volumetric LOD seams. Default on (full 512-entry table).
    /// Set false, or <c>TransvoxelMesher.EnableTransitionCells = false</c>, if a case still fans.
    /// </summary>
    public bool EnableTransvoxelTransitions { get; set; } = true;

    /// <summary>Signed-density iso-surface search range around the procedural surface radius.</summary>
    public float VoxelIsoSearchRange { get; set; } = 96f;

    /// <summary>Number of radial samples used to detect an iso-surface crossing.</summary>
    public int VoxelIsoSearchSteps { get; set; } = 20;

    /// <summary>Max voxel edit commands consumed per chunk update.</summary>
    public int MaxEditCommandsPerUpdate { get; set; } = 8;

    /// <summary>Max leaves marked dirty by manipulation edits per chunk update.</summary>
    public int MaxEditDirtyLeavesPerUpdate { get; set; } = 96;

    // Biome ecosystem/weather defaults
    public int WeatherSeed { get; set; } = 1337;
    public float SeasonLengthMinutes { get; set; } = 18f;
    public float GlobalWeatherIntensity { get; set; } = 1f;
    public float GlobalWindMultiplier { get; set; } = 1f;
    public int MaxVegetationInstances { get; set; } = 50000;
    public int MaxVegetationSpawnsPerUpdate { get; set; } = 256;

    /// <summary>Hash of last compiled biome graph recipe (chunk cache key).</summary>
    [JsonIgnore]
    public ulong RecipeHash { get; set; }

    /// <summary>Compiled continent / crater / volcano / cliff tables from the biome graph.</summary>
    [JsonIgnore] public ContinentRecipe[] Continents { get; set; } = Array.Empty<ContinentRecipe>();
    [JsonIgnore] public CraterRecipe[] Craters { get; set; } = Array.Empty<CraterRecipe>();
    [JsonIgnore] public VolcanoRecipe[] Volcanoes { get; set; } = Array.Empty<VolcanoRecipe>();
    [JsonIgnore] public CliffRecipe[] Cliffs { get; set; } = Array.Empty<CliffRecipe>();
    [JsonIgnore] public DomainWarpRecipe[] DomainWarps { get; set; } = Array.Empty<DomainWarpRecipe>();
    [JsonIgnore] public LatitudeBandRecipe[] LatitudeBands { get; set; } = Array.Empty<LatitudeBandRecipe>();
    [JsonIgnore] public Game_Engine.Core.Noise.SimplexNoise? GeologyNoise { get; set; }
}
