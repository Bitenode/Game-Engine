using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// User-facing planet terrain component. Attach to a GameObject to create a
/// transvoxel cube-sphere planet with biome-driven terrain, caves, and ocean water.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetTerrain : Behavior
{
    public static readonly List<PlanetTerrain> ActivePlanets = new();

    [Persist] public float Radius { get; set; } = 1000f;
    [Persist] public float SeaLevelFraction { get; set; } = 0.25f;
    [Persist] public int MaxLodDepth { get; set; } = 6;
    [Persist] public int ChunkSize { get; set; } = 32;
    [Persist] public float LodDistanceMultiplier { get; set; } = 5.0f;
    [Persist] public int Seed { get; set; } = 42;
    [Persist] public bool EnableCaves { get; set; } = true;
    [Persist] public bool EnableWater { get; set; } = true;
    [Persist] public int MaxActiveChunks { get; set; } = 120;
    [Persist] public float MacroFrequency { get; set; } = 0.0015f;
    [Persist] public float RidgeStrength { get; set; } = 0f;
    [Persist] public float BasinStrength { get; set; } = 0f;
    [Persist] public float TemperatureBias { get; set; } = 0f;
    [Persist] public float MoistureBias { get; set; } = 0f;
    [Persist] public bool EnableAdaptiveScheduling { get; set; } = true;
    [Persist] public int AdaptiveMinScheduleBudget { get; set; } = 12;
    [Persist] public int AdaptiveMaxScheduleBudget { get; set; } = 64;
    [Persist] public float AdaptiveMotionBoost { get; set; } = 1.0f;
    [Persist] public float AdaptiveAltitudeBoost { get; set; } = 0.6f;
    [Persist] public float AdaptiveActiveChunkBoost { get; set; } = 0.35f;
    [Persist] public float MergeDistanceScale { get; set; } = 1.35f;
    [Persist] public float VoxelIsoSearchRange { get; set; } = 96f;
    [Persist] public int VoxelIsoSearchSteps { get; set; } = 20;
    [Persist] public int MaxEditCommandsPerUpdate { get; set; } = 8;
    [Persist] public int MaxEditDirtyLeavesPerUpdate { get; set; } = 96;
    [Persist] public float DefaultManipulationStrength { get; set; } = 10f;
    [Persist] public float DefaultManipulationFalloff { get; set; } = 0.6f;

    [Persist] public string BiomeGraphPath { get; set; } = "";

    PlanetConfig? _config;
    BiomeMap? _biomeMap;
    PlanetChunkManager? _chunkManager;
    PlanetVoxelEditStore? _voxelEditStore;
    PlanetWater? _planetWater;

    // Cached noise instances for runtime height queries (same params as mesh generator)
    Noise.FractalNoise[]? _biomeNoises;
    Noise.FractalNoise? _erosionNoise;
    Noise.FractalNoise? _caveNoise;
    Noise.FractalNoise? _ridgeNoise;
    Noise.FractalNoise? _basinNoise;
    Noise.SimplexNoise? _riverNoisePrimary;
    Noise.SimplexNoise? _riverNoiseMeander;
    public GameObject? WaterGO { get; private set; }
    public float WaterAnimTime { get; private set; }

    public BiomeDefinition OceanBiome => _config?.Biomes?.FirstOrDefault(b => b.Name == "Ocean")
        ?? BiomeDefinition.Ocean;
    public PlanetAtmosphere? Atmosphere => gameObject?.Behaviors
        .OfType<PlanetAtmosphere>()
        .FirstOrDefault(a => a.IsActiveAndEnabled);

    public PlanetConfig? Config => _config;
    public BiomeMap? Map => _biomeMap;
    public int ActiveGenerationJobs => _chunkManager?.ActiveJobs ?? 0;
    public int PendingMeshJobs => _chunkManager?.PendingCompletedJobs ?? 0;
    public int PendingVoxelEditCommands => _chunkManager?.PendingEditCommands ?? 0;
    public int LastAppliedVoxelEditCommands => _chunkManager?.LastAppliedEditCommands ?? 0;
    public int LastVoxelDirtyLeaves => _chunkManager?.LastDirtyLeavesFromEdits ?? 0;
    public int ActiveChunkCount
    {
        get
        {
            if (gameObject == null) return 0;
            int count = 0;
            var children = gameObject.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var n = children[i].Name;
                if (n != null && n.StartsWith("PlanetChunk_"))
                    count++;
            }
            return count;
        }
    }

    public SN.Vector3 LastCameraPosition { get; set; }
    const float ChunkUpdateIntervalSec = 0.10f; // 10 Hz chunk manager update
    const float ChunkUpdateMoveThreshold = 2.5f;
    float _chunkUpdateAccumSec;
    SN.Vector3 _lastChunkUpdateCamPos = new(float.NaN);

    public override void Awake()
    {
        if (!ActivePlanets.Contains(this))
            ActivePlanets.Add(this);
        Initialize();
    }

    public override void PostDeserialize()
    {
        TryLoadBiomeGraph();
    }

    public override void OnEnable()
    {
        if (!ActivePlanets.Contains(this))
            ActivePlanets.Add(this);

        if (_chunkManager == null)
            Initialize();
    }

    public override void OnDestroy()
    {
        ActivePlanets.Remove(this);
        _chunkManager?.Dispose();
        _chunkManager = null;

        if (WaterGO != null)
        {
            WaterGO.RemoveFromParent();
            WaterGO = null;
        }
    }

    void Initialize()
    {
        _config = new PlanetConfig
        {
            Radius = Radius,
            MaxLodDepth = MaxLodDepth,
            ChunkSize = ChunkSize,
            LodDistanceMultiplier = LodDistanceMultiplier,
            Seed = Seed,
            EnableCaves = EnableCaves,
            MaxActiveChunks = Math.Max(64, MaxActiveChunks),
            MacroFrequency = MacroFrequency,
            RidgeStrength = RidgeStrength,
            BasinStrength = BasinStrength,
            TemperatureBias = TemperatureBias,
            MoistureBias = MoistureBias,
            EnableAdaptiveScheduling = EnableAdaptiveScheduling,
            AdaptiveMinScheduleBudget = AdaptiveMinScheduleBudget,
            AdaptiveMaxScheduleBudget = AdaptiveMaxScheduleBudget,
            AdaptiveMotionBoost = AdaptiveMotionBoost,
            AdaptiveAltitudeBoost = AdaptiveAltitudeBoost,
            AdaptiveActiveChunkBoost = AdaptiveActiveChunkBoost,
            MergeDistanceScale = MergeDistanceScale,
            VoxelIsoSearchRange = VoxelIsoSearchRange,
            VoxelIsoSearchSteps = VoxelIsoSearchSteps,
            MaxEditCommandsPerUpdate = MaxEditCommandsPerUpdate,
            MaxEditDirtyLeavesPerUpdate = MaxEditDirtyLeavesPerUpdate,
        };
        _voxelEditStore ??= new PlanetVoxelEditStore();

        // If a graph path is present, apply it immediately so runtime/game view
        // doesn't fall back to default biome colors unless manually compiled.
        TryLoadBiomeGraph();

        // If graph load didn't build runtime state, create defaults.
        if (_chunkManager == null || _biomeMap == null)
        {
            RecalcSeaLevel();

            _biomeMap = new BiomeMap(_config.Seed, _config.Biomes,
                noiseScale: 2f,
                tempLatWeight: _config.TemperatureLatWeight,
                tempNoiseWeight: _config.TemperatureNoiseWeight,
                moistureNoiseScale: _config.MoistureNoiseScale,
                altitudeWeight: _config.AltitudeWeight,
                edgeDistortionFreq: _config.EdgeDistortionFreq,
                edgeDistortionAmp: _config.EdgeDistortionAmp);
            _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
            RebuildPhysicsNoise();
        }

        if (EnableWater && WaterGO == null)
            SetupWater();
    }

    void RebuildPhysicsNoise()
    {
        if (_config == null) return;
        var biomes = _config.Biomes;
        int seed = _config.Seed;

        _biomeNoises = new Noise.FractalNoise[biomes.Length];
        for (int i = 0; i < biomes.Length; i++)
        {
            _biomeNoises[i] = new Noise.FractalNoise(seed)
            {
                Octaves = biomes[i].NoiseOctaves,
                Frequency = biomes[i].NoiseFrequency,
                Lacunarity = biomes[i].NoiseLacunarity,
                Persistence = 0.5f,
                Mode = biomes[i].NoiseMode switch
                {
                    "Ridged" => Noise.FractalMode.Ridged,
                    "Billow" => Noise.FractalMode.Billow,
                    _ => Noise.FractalMode.FBM,
                },
            };
        }

        _erosionNoise = new Noise.FractalNoise(seed + 8000)
        {
            Octaves = 4, Persistence = 0.45f, Mode = Noise.FractalMode.Ridged,
        };

        _caveNoise = _config.EnableCaves
            ? new Noise.FractalNoise(seed + 9000)
            {
                Octaves = 3, Frequency = _config.CaveFrequency,
                Persistence = 0.5f, Mode = Noise.FractalMode.Ridged,
            }
            : null;

        _ridgeNoise = _config.RidgeStrength > 0f
            ? new Noise.FractalNoise(seed + 7100)
            {
                Octaves = 4,
                Frequency = _config.MacroFrequency,
                Persistence = 0.5f,
                Mode = Noise.FractalMode.Ridged,
            }
            : null;

        _basinNoise = _config.BasinStrength > 0f
            ? new Noise.FractalNoise(seed + 7200)
            {
                Octaves = 3,
                Frequency = _config.MacroFrequency,
                Persistence = 0.55f,
                Mode = Noise.FractalMode.FBM,
            }
            : null;

        _riverNoisePrimary = _config.HasRiver ? new Noise.SimplexNoise(seed + 10000) : null;
        _riverNoiseMeander = _config.HasRiver ? new Noise.SimplexNoise(seed + 11000) : null;
    }

    /// <summary>
    /// Sample the actual terrain surface radius at a given direction from planet center.
    /// Uses the same noise pipeline as the mesh generator for pixel-accurate results.
    /// Returns <c>radius + terrainHeight</c> (distance from center to surface).
    /// </summary>
    public float SampleSurfaceRadius(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null || _biomeNoises == null)
            return Radius;

        float height = PlanetSurfaceUtility.SampleHeight(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _caveNoise,
            _ridgeNoise,
            _basinNoise,
            sphereDir);
        float baseSurfaceR = _config.Radius + height;

        var sampler = CreateDensitySampler();
        if (sampler == null)
            return baseSurfaceR;

        return FindSurfaceRadiusOnRay(sphereDir, baseSurfaceR, sampler);
    }

    PlanetDensitySampler? CreateDensitySampler()
    {
        if (_config == null || _biomeMap == null || _biomeNoises == null)
            return null;

        return new PlanetDensitySampler(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _caveNoise,
            _ridgeNoise,
            _basinNoise,
            _voxelEditStore);
    }

    float FindSurfaceRadiusOnRay(SN.Vector3 sphereDir, float baseSurfaceR, PlanetDensitySampler sampler)
    {
        float searchRange = Math.Max(1f, _config?.VoxelIsoSearchRange ?? 96f);
        int steps = Math.Max(8, _config?.VoxelIsoSearchSteps ?? 20);
        float innerR = Math.Max(1f, baseSurfaceR - searchRange);
        float outerR = baseSurfaceR + searchRange;

        float prevR = outerR;
        float prevD = sampler.SampleDensity(sphereDir * prevR);
        float step = (outerR - innerR) / steps;

        for (int i = 1; i <= steps; i++)
        {
            float currR = outerR - i * step;
            float currD = sampler.SampleDensity(sphereDir * currR);
            if (prevD >= 0f && currD <= 0f)
                return RefineSurfaceCrossing(sphereDir, sampler, prevR, currR);
            prevR = currR;
            prevD = currD;
        }

        return baseSurfaceR;
    }

    static float RefineSurfaceCrossing(SN.Vector3 sphereDir, PlanetDensitySampler sampler, float outerR, float innerR)
    {
        float hi = outerR;
        float lo = innerR;
        for (int i = 0; i < 6; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float d = sampler.SampleDensity(sphereDir * mid);
            if (d <= 0f) lo = mid;
            else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    public float SampleWaterMask(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null)
            return 0f;

        if (sphereDir.LengthSquared() < 1e-8f)
            return 0f;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        var blends = _biomeMap.GetBiomes(sphereDir);

        float biomeWater = 0f;
        for (int i = 0; i < blends.Length; i++)
        {
            if (blends[i].Biome.SpawnWater)
                biomeWater += blends[i].Weight;
        }

        float riverWater = 0f;
        if (_config.HasRiver && _riverNoisePrimary != null)
        {
            float freq = MathF.Max(0.0001f, _config.RiverFrequency);
            float width = MathF.Max(0.001f, _config.RiverWidth);

            float n1 = _riverNoisePrimary.Noise3D(
                sphereDir.X * freq,
                sphereDir.Y * freq,
                sphereDir.Z * freq);
            float n2 = _riverNoiseMeander != null
                ? _riverNoiseMeander.Noise3D(
                    sphereDir.X * freq * 1.9f + 33.7f,
                    sphereDir.Y * freq * 1.9f + 77.2f,
                    sphereDir.Z * freq * 1.9f + 19.4f)
                : 0f;

            float line = MathF.Abs(n1 + n2 * Math.Clamp(_config.RiverMeander, 0f, 2f));
            riverWater = 1f - Math.Clamp(line / width, 0f, 1f);

            var allowed = _config.RiverAllowedBiomes;
            if (riverWater > 0f && allowed.Length > 0)
            {
                float allowedWeight = 0f;
                for (int i = 0; i < blends.Length; i++)
                {
                    for (int j = 0; j < allowed.Length; j++)
                    {
                        if (string.Equals(blends[i].Biome.Name, allowed[j], StringComparison.OrdinalIgnoreCase))
                        {
                            allowedWeight += blends[i].Weight;
                            break;
                        }
                    }
                }
                riverWater *= Math.Clamp(allowedWeight * 1.5f, 0f, 1f);
            }
        }

        return Math.Clamp(Math.Max(biomeWater, riverWater), 0f, 1f);
    }

    void RecalcSeaLevel()
    {
        if (_config == null) return;
        float maxAmp = 0f;
        foreach (var b in _config.Biomes)
            maxAmp = MathF.Max(maxAmp, b.HeightAmplitude);

        float terrainMin = Radius - maxAmp;
        float terrainMax = Radius + maxAmp;
        _config.SeaLevel = terrainMin + SeaLevelFraction * (terrainMax - terrainMin);
        Log.Info($"[PlanetTerrain] SeaLevel={_config.SeaLevel:F1} (Radius={Radius}, maxAmp={maxAmp:F1}, frac={SeaLevelFraction}, range={terrainMin:F1}-{terrainMax:F1})");
    }

    void SetupWater()
    {
        if (WaterGO != null || gameObject == null || _config == null) return;

        _planetWater = new PlanetWater(_config.SeaLevel, 48, SampleWaterMask, 0.35f);
        if (_planetWater.WaterMesh == null) return;

        WaterGO = new GameObject("PlanetWater");

        var mf = new MeshFilter();
        WaterGO.AddBehavior(mf);
        mf.Mesh = _planetWater.WaterMesh;

        var mr = new MeshRenderer();
        WaterGO.AddBehavior(mr);

        gameObject.AddChild(WaterGO);
        Log.Info($"[PlanetTerrain] Water mesh created at radius {_config.SeaLevel:F1}");
    }

    void RebuildWater()
    {
        if (!EnableWater || gameObject == null || _config == null) return;

        if (WaterGO != null)
        {
            WaterGO.RemoveFromParent();
            WaterGO = null;
        }
        _planetWater = null;

        SetupWater();
    }

    public override void Update()
    {
        if (_chunkManager == null || gameObject == null) return;

        _chunkUpdateAccumSec += Math.Max(0f, (float)Time.deltaTime);
        bool shouldUpdateChunks = _chunkUpdateAccumSec >= ChunkUpdateIntervalSec;

        if (!float.IsNaN(_lastChunkUpdateCamPos.X))
        {
            var d = LastCameraPosition - _lastChunkUpdateCamPos;
            if (d.LengthSquared() >= ChunkUpdateMoveThreshold * ChunkUpdateMoveThreshold)
                shouldUpdateChunks = true;
        }
        else
        {
            shouldUpdateChunks = true;
        }

        if (shouldUpdateChunks)
        {
            _chunkUpdateAccumSec = 0f;
            _lastChunkUpdateCamPos = LastCameraPosition;
            _chunkManager.Update(LastCameraPosition, gameObject);
        }

        // Cloud animation also uses this timeline, so keep ticking even when water is disabled.
        WaterAnimTime += Math.Max(0f, (float)Time.deltaTime);
    }

    public void UpdateLOD(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
    }

    /// <summary>
    /// Load and apply a .biomegraph file if BiomeGraphPath is set.
    /// </summary>
    static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (System.IO.Path.IsPathRooted(path)) return path;
        var proj = ProjectService.Current;
        return proj != null ? System.IO.Path.Combine(proj.RootPath, path) : path;
    }

    public void TryLoadBiomeGraph()
    {
        if (string.IsNullOrWhiteSpace(BiomeGraphPath) || _config == null) return;

        string absPath = ResolvePath(BiomeGraphPath);
        Log.Info($"[PlanetTerrain] TryLoadBiomeGraph: stored='{BiomeGraphPath}' resolved='{absPath}' exists={System.IO.File.Exists(absPath)}");

        if (!System.IO.File.Exists(absPath)) return;

        try
        {
            var graph = Biome.Graph.BiomeGraph.LoadFromFile(absPath);
            var result = graph.Compile();
            ApplyGraphResultInternal(result);
            Log.Info($"[PlanetTerrain] Loaded biome graph OK. Layers={result.Layers.Length}, Amp={result.HeightAmplitude}, Freq={result.NoiseFrequency}");

            for (int i = 0; i < _config.Biomes.Length; i++)
            {
                var b = _config.Biomes[i];
                Log.Info($"[PlanetTerrain]   Biome[{i}] {b.Name}: amp={b.HeightAmplitude:F1} freq={b.NoiseFrequency:F4} tex='{b.TopTexturePath}'");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[PlanetTerrain] Failed to load biome graph: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply compiled biome graph results (from the BiomeGraphPanel node editor).
    /// Updates the planet's biome definitions and triggers a full rebuild.
    /// </summary>
    public void ApplyGraphResult(Biome.Graph.BiomeGraphResult result, string? graphPath)
    {
        if (!string.IsNullOrEmpty(graphPath))
            BiomeGraphPath = graphPath;

        ApplyGraphResultInternal(result);
    }

    void ApplyGraphResultInternal(Biome.Graph.BiomeGraphResult result)
    {
        if (_config == null) return;

        _config.EnableCaves = result.EnableCaves;
        _config.CaveFrequency = result.CaveFrequency;
        _config.CaveThreshold = result.CaveThreshold;

        _config.TemperatureLatWeight = result.TemperatureLatWeight;
        _config.TemperatureNoiseWeight = result.TemperatureNoiseWeight;
        _config.MoistureNoiseScale = result.MoistureNoiseScale;
        _config.HasRiver = result.HasRiver;
        _config.RiverWidth = result.RiverWidth;
        _config.RiverDepth = result.RiverDepth;
        _config.RiverFrequency = result.RiverFrequency;
        _config.RiverMeander = result.RiverMeander;
        _config.RiverAllowedBiomes = result.RiverAllowedBiomes ?? Array.Empty<string>();

        float ampScale = result.HeightAmplitude / 50f;
        float freqScale = result.NoiseFrequency / 0.005f;

        for (int i = 0; i < _config.Biomes.Length && i < result.Layers.Length; i++)
        {
            var layer = result.Layers[i];
            var biome = _config.Biomes[i];

            if (!string.IsNullOrEmpty(layer.BiomeName))
                biome.Name = layer.BiomeName;

            biome.BaseColorR = layer.BaseColorR;
            biome.BaseColorG = layer.BaseColorG;
            biome.BaseColorB = layer.BaseColorB;
            biome.TopTexturePath = layer.AlbedoPath;
            biome.TopNormalMapPath = layer.NormalPath;
            biome.TopTiling = layer.Tiling;

            if (!string.IsNullOrEmpty(layer.UnderTexturePath))
                biome.UnderTexturePath = layer.UnderTexturePath;
            if (!string.IsNullOrEmpty(layer.UnderNormalPath))
                biome.UnderNormalMapPath = layer.UnderNormalPath;
            if (layer.UnderTiling > 0f)
                biome.UnderTiling = layer.UnderTiling;

            if (!string.IsNullOrEmpty(layer.NoiseMode))
                biome.NoiseMode = layer.NoiseMode;
            if (layer.NoiseOctaves > 0)
                biome.NoiseOctaves = layer.NoiseOctaves;
            if (layer.ErosionStrength >= 0f)
                biome.ErosionStrength = layer.ErosionStrength;
            if (layer.ErosionFrequency > 0f)
                biome.ErosionFrequency = layer.ErosionFrequency;
            biome.SpawnWater = layer.SpawnWater;

            if (layer.SpawnWater)
            {
                biome.WaterShallowColorR = layer.WaterShallowR;
                biome.WaterShallowColorG = layer.WaterShallowG;
                biome.WaterShallowColorB = layer.WaterShallowB;
                biome.WaterDeepColorR = layer.WaterDeepR;
                biome.WaterDeepColorG = layer.WaterDeepG;
                biome.WaterDeepColorB = layer.WaterDeepB;
            }
        }

        foreach (var biome in _config.Biomes)
        {
            biome.HeightAmplitude *= ampScale;
            biome.NoiseFrequency *= freqScale;
        }

        RecalcSeaLevel();
        RebuildWater();

        _chunkManager?.Dispose();
        _chunkManager = null;

        if (gameObject != null)
        {
            for (int i = gameObject.Children.Count - 1; i >= 0; i--)
            {
                var child = gameObject.Children[i];
                if (child.Name != null && child.Name.StartsWith("PlanetChunk_"))
                    child.RemoveFromParent();
            }
        }

        _biomeMap = new BiomeMap(_config.Seed, _config.Biomes,
            noiseScale: 2f,
            tempLatWeight: _config.TemperatureLatWeight,
            tempNoiseWeight: _config.TemperatureNoiseWeight,
            moistureNoiseScale: _config.MoistureNoiseScale,
            altitudeWeight: _config.AltitudeWeight,
            edgeDistortionFreq: _config.EdgeDistortionFreq,
            edgeDistortionAmp: _config.EdgeDistortionAmp);
        _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
        RebuildPhysicsNoise();

        SceneRenderer.ResetBiomeTexDebug();
        SceneService.NotifyChanged();
    }

    public void ApplyRuntimeTuning()
    {
        if (_config == null) return;

        _config.MaxActiveChunks = Math.Max(64, MaxActiveChunks);
        _config.MacroFrequency = Math.Max(0.0001f, MacroFrequency);
        _config.RidgeStrength = Math.Max(0f, RidgeStrength);
        _config.BasinStrength = Math.Max(0f, BasinStrength);
        _config.TemperatureBias = Math.Clamp(TemperatureBias, -0.5f, 0.5f);
        _config.MoistureBias = Math.Clamp(MoistureBias, -0.5f, 0.5f);
        _config.EnableAdaptiveScheduling = EnableAdaptiveScheduling;
        _config.AdaptiveMinScheduleBudget = Math.Max(1, AdaptiveMinScheduleBudget);
        _config.AdaptiveMaxScheduleBudget = Math.Max(_config.AdaptiveMinScheduleBudget, AdaptiveMaxScheduleBudget);
        _config.AdaptiveMotionBoost = Math.Max(0f, AdaptiveMotionBoost);
        _config.AdaptiveAltitudeBoost = Math.Max(0f, AdaptiveAltitudeBoost);
        _config.AdaptiveActiveChunkBoost = Math.Max(0f, AdaptiveActiveChunkBoost);
        _config.MergeDistanceScale = Math.Max(1.0f, MergeDistanceScale);
        _config.VoxelIsoSearchRange = Math.Max(8f, VoxelIsoSearchRange);
        _config.VoxelIsoSearchSteps = Math.Max(8, VoxelIsoSearchSteps);
        _config.MaxEditCommandsPerUpdate = Math.Max(1, MaxEditCommandsPerUpdate);
        _config.MaxEditDirtyLeavesPerUpdate = Math.Max(1, MaxEditDirtyLeavesPerUpdate);

        _biomeMap = new BiomeMap(_config.Seed, _config.Biomes,
            noiseScale: 2f,
            tempLatWeight: _config.TemperatureLatWeight,
            tempNoiseWeight: _config.TemperatureNoiseWeight,
            moistureNoiseScale: _config.MoistureNoiseScale,
            altitudeWeight: _config.AltitudeWeight,
            edgeDistortionFreq: _config.EdgeDistortionFreq,
            edgeDistortionAmp: _config.EdgeDistortionAmp);

        _chunkManager?.Dispose();
        _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
        RebuildPhysicsNoise();

        if (gameObject != null)
        {
            for (int i = gameObject.Children.Count - 1; i >= 0; i--)
            {
                var child = gameObject.Children[i];
                if (child.Name != null && child.Name.StartsWith("PlanetChunk_"))
                    child.RemoveFromParent();
            }
        }

        SceneRenderer.ResetBiomeTexDebug();
        SceneService.NotifyChanged();
    }

    public void DigSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f)
    {
        QueueSphereEdit(worldCenter, radius, Math.Abs(strength <= 0f ? DefaultManipulationStrength : strength), ResolveFalloff(falloff));
    }

    public void BuildSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f)
    {
        QueueSphereEdit(worldCenter, radius, -Math.Abs(strength <= 0f ? DefaultManipulationStrength : strength), ResolveFalloff(falloff));
    }

    public void SetVoxelDensity(SN.Vector3 worldPos, float targetDensity)
    {
        var sampler = CreateDensitySampler();
        if (sampler == null) return;

        float current = sampler.SampleDensity(worldPos);
        float delta = targetDensity - current;
        if (MathF.Abs(delta) <= 1e-6f) return;

        float pointRadius = Math.Max(0.5f, Radius * 0.0025f);
        QueueSphereEdit(worldPos, pointRadius, delta, 0f);
    }

    public void ClearVoxelEdits(bool rebuildNow = true)
    {
        _voxelEditStore?.Clear();
        if (!rebuildNow || gameObject == null) return;

        for (int i = gameObject.Children.Count - 1; i >= 0; i--)
        {
            var child = gameObject.Children[i];
            if (child.Name != null && child.Name.StartsWith("PlanetChunk_"))
                child.RemoveFromParent();
        }

        if (_chunkManager != null)
        {
            for (int f = 0; f < _chunkManager.Faces.Length; f++)
            {
                var leaves = _chunkManager.Faces[f].GetLeafNodes();
                for (int l = 0; l < leaves.Count; l++)
                    leaves[l].NeedsMeshRebuild = true;
            }
        }
    }

    void QueueSphereEdit(SN.Vector3 worldCenter, float radius, float densityDelta, float falloff)
    {
        if (_chunkManager == null) return;
        _chunkManager.EnqueueSphereEdit(worldCenter, Math.Max(0.05f, radius), densityDelta, falloff);
    }

    float ResolveFalloff(float value) => value < 0f ? DefaultManipulationFalloff : Math.Clamp(value, 0f, 1f);
}
