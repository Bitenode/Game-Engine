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

    [Persist] public string BiomeGraphPath { get; set; } = "";

    PlanetConfig? _config;
    BiomeMap? _biomeMap;
    PlanetChunkManager? _chunkManager;
    PlanetWater? _planetWater;

    // Cached noise instances for runtime height queries (same params as mesh generator)
    Noise.FractalNoise[]? _biomeNoises;
    Noise.FractalNoise? _erosionNoise;
    Noise.FractalNoise? _caveNoise;
    public GameObject? WaterGO { get; private set; }
    public float WaterAnimTime { get; private set; }

    public BiomeDefinition OceanBiome => _config?.Biomes?.FirstOrDefault(b => b.Name == "Ocean")
        ?? BiomeDefinition.Ocean;

    public PlanetConfig? Config => _config;
    public BiomeMap? Map => _biomeMap;
    public int ActiveGenerationJobs => _chunkManager?.ActiveJobs ?? 0;
    public int PendingMeshJobs => _chunkManager?.PendingCompletedJobs ?? 0;
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
        };

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
            _chunkManager = new PlanetChunkManager(_config, _biomeMap);
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

        float radius = _config.Radius;
        var blends = _biomeMap.GetBiomes(sphereDir);

        float nx = sphereDir.X * radius;
        float ny = sphereDir.Y * radius;
        float nz = sphereDir.Z * radius;

        float height = 0f;
        for (int b = 0; b < blends.Length && b < 4; b++)
        {
            var biome = blends[b].Biome;
            float w = blends[b].Weight;
            int idx = Math.Clamp(biome.BiomeIndex, 0, _biomeNoises.Length - 1);
            float sample = _biomeNoises[idx].Sample3D(nx, ny, nz);

            var mode = biome.NoiseMode;
            if (mode == "Ridged") sample = sample * 0.7f - 0.3f;
            else if (mode == "Billow") sample = sample * 0.8f;

            height += biome.HeightAmplitude * sample * w;
        }

        if (_erosionNoise != null && blends.Length > 0)
        {
            float totalErosion = 0f;
            for (int b = 0; b < blends.Length && b < 4; b++)
            {
                var biome = blends[b].Biome;
                if (biome.ErosionStrength > 0f)
                {
                    _erosionNoise.Frequency = biome.ErosionFrequency;
                    float e = _erosionNoise.Sample3D(nx, ny, nz);
                    e = Math.Clamp(e, 0f, 1f);
                    totalErosion += e * biome.ErosionStrength * 5f * blends[b].Weight;
                }
            }
            height -= totalErosion;
        }

        if (_caveNoise != null && blends.Length > 0)
        {
            var dominant = blends[0].Biome;
            if (dominant.CavesEnabled)
            {
                float caveSample = _caveNoise.Sample3D(nx, ny, nz);
                caveSample = Math.Clamp(caveSample, 0f, 1f);
                if (caveSample > _config.CaveThreshold)
                {
                    float caveIntensity = (caveSample - _config.CaveThreshold) / (1f - _config.CaveThreshold);
                    height -= caveIntensity * Math.Min(dominant.CaveDepth, 8f);
                }
            }
        }

        return radius + height;
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

        _planetWater = new PlanetWater(_config.SeaLevel, 48);
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

        if (EnableWater)
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
        _chunkManager = new PlanetChunkManager(_config, _biomeMap);
        RebuildPhysicsNoise();

        SceneRenderer.ResetBiomeTexDebug();
        SceneService.NotifyChanged();
    }
}
