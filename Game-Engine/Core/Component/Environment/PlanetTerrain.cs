using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game_Engine.Core;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Networking;
using Game_Engine.Core.Planet;
using Game_Engine.Core.Voxel;
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

    static bool _registeredPlanetNetRpcs;

    /// <summary>Called when <see cref="NetworkManager.Stop"/> tears down networking so RPC registration can run again on the next host.</summary>
    public static void ResetPlanetNetworkStatics() => _registeredPlanetNetRpcs = false;

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
    [Persist] public int WeatherSeed { get; set; } = 1337;
    [Persist] public float SeasonLengthMinutes { get; set; } = 18f;
    [Persist] public float GlobalWeatherIntensity { get; set; } = 1f;
    [Persist] public float GlobalWindMultiplier { get; set; } = 1f;
    [Persist] public int MaxVegetationInstances { get; set; } = 20000;
    [Persist] public int MaxVegetationSpawnsPerUpdate { get; set; } = 256;

    /// <summary>
    /// When networking as a <see cref="NetworkManager.IsClient"/>, request planet meshes from the server instead of running local generation.
    /// </summary>
    [Persist] public bool StreamSurfaceFromServerWhenClient { get; set; } = true;

    [Persist] public string PlanetAssetPath { get; set; } = "";
    [Persist] public string BiomeGraphPath { get; set; } = "";

    PlanetConfig? _config;
    BiomeMap? _biomeMap;
    PlanetChunkManager? _chunkManager;
    PlanetVoxelEditStore? _voxelEditStore;
    PlanetWater? _planetWater;
    PlanetVegetationAssetData? _pendingVegetationAssetData;

    /// <summary>
    /// When vegetation is read from disk but not yet applied to <see cref="PlanetVegetationSystem"/>
    /// (deferred scene load), <see cref="SavePlanetAsset"/> must not serialize an empty placement list
    /// or the .planet file would be overwritten and placements lost.
    /// </summary>
    PlanetVegetationAssetData? _deferredDiskVegetation;

    /// <summary>
    /// While true, <see cref="BuildVegetationAssetData"/> may substitute <see cref="_deferredDiskVegetation"/>
    /// when live export is empty. Cleared by <see cref="ReleaseVegetationDiskSnapshotAfterImport"/> after
    /// <see cref="PlanetVegetationSystem.ImportAssetData"/> — not tied to <see cref="AsyncVegetationHydrationPending"/>,
    /// which is cleared even when async hydrate aborts early.
    /// </summary>
    bool _useDiskVegetationSnapshotOnSave;

    /// <summary>
    /// When true, synchronous <see cref="TryLoadPlanetAsset"/> skips embedding vegetation so
    /// <c>PlanetVegetationSceneLoader</c> can apply it on the UI thread after a background read.
    /// </summary>
    internal bool AsyncVegetationHydrationPending;

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
    public PlanetChunkManager? ChunkManager => _chunkManager;
    public int ActiveGenerationJobs => _chunkManager?.ActiveJobs ?? 0;
    public int PendingMeshJobs => _chunkManager?.PendingCompletedJobs ?? 0;
    public int PendingVoxelEditCommands => _chunkManager?.PendingEditCommands ?? 0;
    public int LastAppliedVoxelEditCommands => _chunkManager?.LastAppliedEditCommands ?? 0;
    public int LastVoxelDirtyLeaves => _chunkManager?.LastDirtyLeavesFromEdits ?? 0;
    public int ActiveChunkCount
    {
        get
        {
            if (_chunkManager == null) return 0;
            return _chunkManager.GetRenderableLeaves().Count;
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
        EnsurePlanetNetworkRpcsRegistered();
        EnsurePlanetAssetPath();
        Initialize();
    }

    public override void PostDeserialize()
    {
        EnsurePlanetAssetPath();
        TryLoadPlanetAsset(importVegetation: !SceneService.DeferPlanetVegetationImport);
        Initialize();
    }

    public override void OnEnable()
    {
        if (!ActivePlanets.Contains(this))
            ActivePlanets.Add(this);

        EnsurePlanetAssetPath();
        var abs = PlanetAssetIO.ToAbsolutePath(PlanetAssetPath);
        if (!File.Exists(abs))
            SavePlanetAsset();

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
        EnsurePlanetAssetPath();
        bool importVeg = !SceneService.DeferPlanetVegetationImport && !AsyncVegetationHydrationPending;
        TryLoadPlanetAsset(importVegetation: importVeg);

        var baseConfig = _config != null ? CloneConfig(_config) : new PlanetConfig();
        _config = baseConfig;
        _config.WorldRadiusScale = GetWorldRadiusScale();
        _config.Radius = Radius;
        _config.MaxLodDepth = MaxLodDepth;
        _config.ChunkSize = ChunkSize;
        _config.LodDistanceMultiplier = LodDistanceMultiplier;
        _config.Seed = Seed;
        _config.EnableCaves = EnableCaves;
        _config.MaxActiveChunks = Math.Max(64, MaxActiveChunks);
        _config.MacroFrequency = MacroFrequency;
        _config.RidgeStrength = RidgeStrength;
        _config.BasinStrength = BasinStrength;
        _config.TemperatureBias = TemperatureBias;
        _config.MoistureBias = MoistureBias;
        _config.EnableAdaptiveScheduling = EnableAdaptiveScheduling;
        _config.AdaptiveMinScheduleBudget = AdaptiveMinScheduleBudget;
        _config.AdaptiveMaxScheduleBudget = AdaptiveMaxScheduleBudget;
        _config.AdaptiveMotionBoost = AdaptiveMotionBoost;
        _config.AdaptiveAltitudeBoost = AdaptiveAltitudeBoost;
        _config.AdaptiveActiveChunkBoost = AdaptiveActiveChunkBoost;
        _config.MergeDistanceScale = MergeDistanceScale;
        _config.VoxelIsoSearchRange = VoxelIsoSearchRange;
        _config.VoxelIsoSearchSteps = VoxelIsoSearchSteps;
        _config.MaxEditCommandsPerUpdate = MaxEditCommandsPerUpdate;
        _config.MaxEditDirtyLeavesPerUpdate = MaxEditDirtyLeavesPerUpdate;
        _config.WeatherSeed = WeatherSeed;
        _config.SeasonLengthMinutes = SeasonLengthMinutes;
        _config.GlobalWeatherIntensity = GlobalWeatherIntensity;
        _config.GlobalWindMultiplier = GlobalWindMultiplier;
        _config.MaxVegetationInstances = MaxVegetationInstances;
        _config.MaxVegetationSpawnsPerUpdate = MaxVegetationSpawnsPerUpdate;
        _config.Biomes = CloneBiomes(_config.Biomes);
        _config.RiverAllowedBiomes = _config.RiverAllowedBiomes?.ToArray() ?? Array.Empty<string>();
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

        ApplyPendingVegetationAssetData();
        WireSurfaceStreaming();
    }

    void WireSurfaceStreaming()
    {
        if (_chunkManager == null) return;
        bool streamClient = NetworkManager.IsActive && NetworkManager.IsClient && StreamSurfaceFromServerWhenClient;
        _chunkManager.SetClientStreamingMode(streamClient, streamClient ? OnClientChunkMeshRequested : null);
    }

    void OnClientChunkMeshRequested(QuadNode node)
    {
        if (_chunkManager == null)
        {
            node.IsGenerating = false;
            return;
        }

        uint netId = GetPlanetNetworkId();
        var key = PlanetSurfaceChunkKey.Encode(netId, node.Face, node.LodLevel, node.U0, node.V0, node.U1, node.V1);
        NetworkManager.RequestSurfaceChunk(NetworkManager.SurfaceKindPlanetChunk, key, (_, _, payload) =>
        {
            if (_chunkManager == null)
            {
                node.IsGenerating = false;
                return;
            }

            var data = TransvoxelMeshData.DeserializeFromBytes(payload);
            if (data == null)
            {
                node.IsGenerating = false;
                node.NeedsMeshRebuild = true;
                return;
            }

            _chunkManager.ApplyNetworkMesh(node, data);
        });
    }

    uint GetPlanetNetworkId()
    {
        if (gameObject == null) return 0;
        foreach (var b in gameObject.Behaviors)
        {
            if (b is NetworkIdentity ni)
                return ni.NetworkId;
        }
        return 0;
    }

    /// <summary>Server: build payload for a planet chunk request (used by <see cref="NetworkSurfaceDispatch"/>).</summary>
    public static byte[]? HandlePlanetChunkRequestForServer(byte[] key)
    {
        if (!PlanetSurfaceChunkKey.TryDecode(key, out uint netId, out int face, out _, out float u0, out float v0, out float u1, out float v1))
            return null;

        foreach (var p in ActivePlanets)
        {
            if (!p.IsActiveAndEnabled) continue;
            var cm = p.ChunkManager;
            if (cm == null) continue;

            uint pid = p.GetPlanetNetworkId();
            if (netId != 0)
            {
                if (pid != netId) continue;
            }
            else
            {
                if (pid != 0) continue;
            }

            var data = cm.ServerGenerateMeshForBounds(face, u0, v0, u1, v1);
            if (data == null || data.IsEmpty) return null;
            return data.SerializeToBytes();
        }

        return null;
    }

    public void SavePlanetAsset()
    {
        EnsurePlanetAssetPath();
        var data = BuildPlanetAssetData();
        if (PlanetAssetIO.TrySave(PlanetAssetPath, data, out var error))
        {
            Log.Info($"[PlanetTerrain] Planet asset saved: {PlanetAssetPath}");
            ProjectService.TouchModified();
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            Log.Info($"[PlanetTerrain] {error}");
        }
    }

    public void LoadPlanetAsset()
    {
        EnsurePlanetAssetPath();
        if (!TryLoadPlanetAsset())
            return;

        _chunkManager?.Dispose();
        _chunkManager = null;
        _biomeMap = null;
        RebuildWater();
        Initialize();
        SceneRenderer.ResetBiomeTexDebug();
        SceneService.NotifyChanged();
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
        float worldScale = GetWorldRadiusScale();
        if (_config == null || _biomeMap == null || _biomeNoises == null)
            return Radius * worldScale;

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
            return baseSurfaceR * worldScale;

        return FindSurfaceRadiusOnRay(sphereDir, baseSurfaceR, sampler) * worldScale;
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

    public float SampleShoreBiomeIndex(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null)
            return 0f;

        if (sphereDir.LengthSquared() < 1e-8f)
            return 0f;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        var blends = _biomeMap.GetBiomes(sphereDir);
        if (blends == null || blends.Length == 0)
            return 0f;

        int bestIndex = 0;
        float bestScore = float.MinValue;
        for (int i = 0; i < blends.Length; i++)
        {
            var biome = blends[i].Biome;
            int idx = FindBiomeIndex(biome.Name);
            if (idx < 0) continue;
            float score = blends[i].Weight + (biome.SpawnWater ? 0f : 0.25f);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = idx;
            }
        }

        return bestIndex;
    }

    int FindBiomeIndex(string name)
    {
        if (_config?.Biomes == null || string.IsNullOrWhiteSpace(name))
            return -1;
        for (int i = 0; i < _config.Biomes.Length; i++)
        {
            if (string.Equals(_config.Biomes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
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

        _planetWater = new PlanetWater(_config.SeaLevel, 48, SampleWaterMask, SampleShoreBiomeIndex);
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
        WireSurfaceStreaming();
        ApplyPendingVegetationAssetData();
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
            if (_config != null)
                _config.WorldRadiusScale = GetWorldRadiusScale();
            var planetCenter = GetWorldCenter();
            _chunkManager.Update(LastCameraPosition, planetCenter);
        }

        // Cloud animation also uses this timeline, so keep ticking even when water is disabled.
        WaterAnimTime += Math.Max(0f, (float)Time.deltaTime);
    }

    public void UpdateLOD(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
    }

    public void UpdateSceneViewNoLod(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
        if (_chunkManager == null || gameObject == null) return;

        if (_config != null)
            _config.WorldRadiusScale = GetWorldRadiusScale();
        var planetCenter = GetWorldCenter();
        _chunkManager.UpdateNoLod(planetCenter);

        // Keep water/cloud timeline advancing in Scene View too.
        WaterAnimTime += Math.Max(0f, (float)Time.deltaTime);
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

    SN.Vector3 GetWorldCenter()
    {
        if (gameObject == null) return SN.Vector3.Zero;
        var world = SceneGraphUtil.AccumulateWorld(gameObject);
        return new SN.Vector3(world.M41, world.M42, world.M43);
    }

    float GetWorldRadiusScale()
    {
        if (gameObject == null) return 1f;
        var world = SceneGraphUtil.AccumulateWorld(gameObject);
        float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
        float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
        float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
        float uniform = (sx + sy + sz) / 3f;
        return MathF.Max(0.0001f, uniform);
    }

    public bool TryGetSphereDirectionAtWorldPos(SN.Vector3 worldPos, out SN.Vector3 sphereDir)
    {
        sphereDir = SN.Vector3.UnitY;
        var center = GetWorldCenter();
        var toPos = worldPos - center;
        float lenSq = toPos.LengthSquared();
        if (lenSq <= 1e-10f)
            return false;
        sphereDir = toPos / MathF.Sqrt(lenSq);
        return true;
    }

    public bool TryGetBiomeBlendsAtWorldPos(SN.Vector3 worldPos, out BiomeBlend[] blends)
    {
        blends = Array.Empty<BiomeBlend>();
        if (_biomeMap == null || _config == null)
            return false;
        if (!TryGetSphereDirectionAtWorldPos(worldPos, out var sphereDir))
            return false;

        float worldScale = GetWorldRadiusScale();
        float worldRadius = Math.Max(0.001f, _config.Radius * worldScale);
        float worldSea = _config.SeaLevel * worldScale;
        float dist = (worldPos - GetWorldCenter()).Length();
        float altitudeNorm = Math.Clamp((dist - worldSea) / Math.Max(worldRadius * 0.25f, 1f), 0f, 1f);
        blends = _biomeMap.GetBiomes(sphereDir, altitudeNorm);
        return blends.Length > 0;
    }

    public bool TryGetDominantBiomeAtWorldPos(SN.Vector3 worldPos, out BiomeDefinition biome)
    {
        biome = OceanBiome;
        if (!TryGetBiomeBlendsAtWorldPos(worldPos, out var blends) || blends.Length == 0)
            return false;
        biome = blends[0].Biome;
        return true;
    }

    void EnsurePlanetAssetPath()
    {
        if (!string.IsNullOrWhiteSpace(PlanetAssetPath))
        {
            PlanetAssetPath = PlanetAssetIO.NormalizeProjectRelative(PlanetAssetPath);
            return;
        }

        string goName = gameObject?.Name ?? "Planet";
        string relPath = Path.Combine("Assets", "Planets", MakeSafeFileName(goName) + ".planet");
        PlanetAssetPath = PlanetAssetIO.NormalizeProjectRelative(relPath);
    }

    static string MakeSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Planet";
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' '))
                chars[i] = '_';
        }
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "Planet" : s;
    }

    bool TryLoadPlanetAsset(bool importVegetation = true)
    {
        if (string.IsNullOrWhiteSpace(PlanetAssetPath))
            return false;

        if (!PlanetAssetIO.TryLoad(PlanetAssetPath, out var data, out var error) || data == null)
        {
            if (!string.IsNullOrWhiteSpace(error) && !error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                Log.Info($"[PlanetTerrain] {error}");
            return false;
        }

        ApplyPlanetAssetData(data, importVegetation);
        return true;
    }

    PlanetAssetData BuildPlanetAssetData()
    {
        var cfg = _config ?? new PlanetConfig
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
            WeatherSeed = WeatherSeed,
            SeasonLengthMinutes = SeasonLengthMinutes,
            GlobalWeatherIntensity = GlobalWeatherIntensity,
            GlobalWindMultiplier = GlobalWindMultiplier,
            MaxVegetationInstances = MaxVegetationInstances,
            MaxVegetationSpawnsPerUpdate = MaxVegetationSpawnsPerUpdate,
            Biomes = CloneBiomes(BiomeDefinition.AllPresets),
            RiverAllowedBiomes = Array.Empty<string>(),
        };

        return new PlanetAssetData
        {
            Version = 1,
            BiomeGraphPath = PlanetAssetIO.NormalizeProjectRelative(BiomeGraphPath),
            SeaLevelFraction = SeaLevelFraction,
            EnableWater = EnableWater,
            Config = CloneConfig(cfg),
            Vegetation = BuildVegetationAssetData(),
        };
    }

    void ApplyPlanetAssetData(PlanetAssetData data, bool importVegetation = true)
    {
        if (data.Config == null)
            return;

        var c = data.Config;
        Radius = c.Radius;
        MaxLodDepth = c.MaxLodDepth;
        ChunkSize = c.ChunkSize;
        LodDistanceMultiplier = c.LodDistanceMultiplier;
        Seed = c.Seed;
        EnableCaves = c.EnableCaves;
        MaxActiveChunks = c.MaxActiveChunks;
        MacroFrequency = c.MacroFrequency;
        RidgeStrength = c.RidgeStrength;
        BasinStrength = c.BasinStrength;
        TemperatureBias = c.TemperatureBias;
        MoistureBias = c.MoistureBias;
        EnableAdaptiveScheduling = c.EnableAdaptiveScheduling;
        AdaptiveMinScheduleBudget = c.AdaptiveMinScheduleBudget;
        AdaptiveMaxScheduleBudget = c.AdaptiveMaxScheduleBudget;
        AdaptiveMotionBoost = c.AdaptiveMotionBoost;
        AdaptiveAltitudeBoost = c.AdaptiveAltitudeBoost;
        AdaptiveActiveChunkBoost = c.AdaptiveActiveChunkBoost;
        MergeDistanceScale = c.MergeDistanceScale;
        VoxelIsoSearchRange = c.VoxelIsoSearchRange;
        VoxelIsoSearchSteps = c.VoxelIsoSearchSteps;
        MaxEditCommandsPerUpdate = c.MaxEditCommandsPerUpdate;
        MaxEditDirtyLeavesPerUpdate = c.MaxEditDirtyLeavesPerUpdate;
        WeatherSeed = c.WeatherSeed;
        SeasonLengthMinutes = c.SeasonLengthMinutes;
        GlobalWeatherIntensity = c.GlobalWeatherIntensity;
        GlobalWindMultiplier = c.GlobalWindMultiplier;
        MaxVegetationInstances = c.MaxVegetationInstances;
        MaxVegetationSpawnsPerUpdate = c.MaxVegetationSpawnsPerUpdate;

        SeaLevelFraction = data.SeaLevelFraction;
        EnableWater = data.EnableWater;
        if (!string.IsNullOrWhiteSpace(data.BiomeGraphPath))
            BiomeGraphPath = PlanetAssetIO.NormalizeProjectRelative(data.BiomeGraphPath);

        _config = CloneConfig(c);
        if (importVegetation)
        {
            _pendingVegetationAssetData = data.Vegetation ?? new PlanetVegetationAssetData();
            _deferredDiskVegetation = null;
            _useDiskVegetationSnapshotOnSave = false;
        }
        else
        {
            _pendingVegetationAssetData = null;
            var diskVeg = data.Vegetation;
            if (diskVeg?.Placements != null && diskVeg.Placements.Length > 0)
            {
                _deferredDiskVegetation = diskVeg.Clone();
                _useDiskVegetationSnapshotOnSave = true;
            }
            else
            {
                _deferredDiskVegetation = null;
                _useDiskVegetationSnapshotOnSave = false;
            }
        }
    }

    PlanetVegetationAssetData BuildVegetationAssetData()
    {
        var veg = gameObject?.Behaviors?.OfType<PlanetVegetationSystem>().FirstOrDefault(v => v != null);
        PlanetVegetationAssetData live = veg == null ? new PlanetVegetationAssetData() : veg.ExportAssetData();
        int liveN = live.Placements?.Length ?? 0;
        int diskN = _deferredDiskVegetation?.Placements?.Length ?? 0;

        // Memory has no exportable rows yet but we stashed disk vegetation (deferred import or hydrate not run).
        // Do NOT require AsyncVegetationHydrationPending — it is cleared when background load aborts, which would
        // leave saves unprotected and wipe the .planet file.
        if (_useDiskVegetationSnapshotOnSave && _deferredDiskVegetation != null && diskN > 0 && liveN == 0)
            return _deferredDiskVegetation.Clone();

        return live;
    }

    /// <summary>
    /// Call after <see cref="PlanetVegetationSystem.ImportAssetData"/> has applied authoritative vegetation
    /// from disk (or an explicit empty block).
    /// </summary>
    internal void ReleaseVegetationDiskSnapshotAfterImport()
    {
        _useDiskVegetationSnapshotOnSave = false;
        _deferredDiskVegetation = null;
    }

    void ApplyPendingVegetationAssetData()
    {
        if (_pendingVegetationAssetData == null)
            return;
        var veg = gameObject?.Behaviors?.OfType<PlanetVegetationSystem>().FirstOrDefault(v => v != null);
        if (veg == null)
            return;
        veg.ImportAssetData(_pendingVegetationAssetData);
        _pendingVegetationAssetData = null;
        // Same warmup as deferred hydrate: small MaxAssetSpawnsPerUpdate can starve grass vs trees.
        veg.WarmSpawnAfterDeferredImport();
    }

    static PlanetConfig CloneConfig(PlanetConfig src)
    {
        return new PlanetConfig
        {
            Radius = src.Radius,
            WorldRadiusScale = src.WorldRadiusScale,
            SeaLevel = src.SeaLevel,
            MaxLodDepth = src.MaxLodDepth,
            ChunkSize = src.ChunkSize,
            LodDistanceMultiplier = src.LodDistanceMultiplier,
            SplitDistanceScale = src.SplitDistanceScale,
            Seed = src.Seed,
            EnableCaves = src.EnableCaves,
            Biomes = CloneBiomes(src.Biomes),
            MaxLeafNodes = src.MaxLeafNodes,
            MaxActiveChunks = src.MaxActiveChunks,
            MaxMeshAppliesPerUpdate = src.MaxMeshAppliesPerUpdate,
            MaxGenerationSchedulesPerUpdate = src.MaxGenerationSchedulesPerUpdate,
            CaveFrequency = src.CaveFrequency,
            CaveThreshold = src.CaveThreshold,
            CaveDepth = src.CaveDepth,
            TemperatureLatWeight = src.TemperatureLatWeight,
            TemperatureNoiseWeight = src.TemperatureNoiseWeight,
            MoistureNoiseScale = src.MoistureNoiseScale,
            MacroFrequency = src.MacroFrequency,
            RidgeStrength = src.RidgeStrength,
            BasinStrength = src.BasinStrength,
            TemperatureBias = src.TemperatureBias,
            MoistureBias = src.MoistureBias,
            AltitudeWeight = src.AltitudeWeight,
            EdgeDistortionFreq = src.EdgeDistortionFreq,
            EdgeDistortionAmp = src.EdgeDistortionAmp,
            EnableAdaptiveScheduling = src.EnableAdaptiveScheduling,
            AdaptiveMinScheduleBudget = src.AdaptiveMinScheduleBudget,
            AdaptiveMaxScheduleBudget = src.AdaptiveMaxScheduleBudget,
            AdaptiveMotionBoost = src.AdaptiveMotionBoost,
            AdaptiveAltitudeBoost = src.AdaptiveAltitudeBoost,
            AdaptiveActiveChunkBoost = src.AdaptiveActiveChunkBoost,
            MergeDistanceScale = src.MergeDistanceScale,
            HasRiver = src.HasRiver,
            RiverWidth = src.RiverWidth,
            RiverDepth = src.RiverDepth,
            RiverFrequency = src.RiverFrequency,
            RiverMeander = src.RiverMeander,
            RiverAllowedBiomes = src.RiverAllowedBiomes?.ToArray() ?? Array.Empty<string>(),
            VoxelIsoSearchRange = src.VoxelIsoSearchRange,
            VoxelIsoSearchSteps = src.VoxelIsoSearchSteps,
            MaxEditCommandsPerUpdate = src.MaxEditCommandsPerUpdate,
            MaxEditDirtyLeavesPerUpdate = src.MaxEditDirtyLeavesPerUpdate,
            WeatherSeed = src.WeatherSeed,
            SeasonLengthMinutes = src.SeasonLengthMinutes,
            GlobalWeatherIntensity = src.GlobalWeatherIntensity,
            GlobalWindMultiplier = src.GlobalWindMultiplier,
            MaxVegetationInstances = src.MaxVegetationInstances,
            MaxVegetationSpawnsPerUpdate = src.MaxVegetationSpawnsPerUpdate,
        };
    }

    static BiomeDefinition[] CloneBiomes(BiomeDefinition[]? src)
    {
        src ??= BiomeDefinition.AllPresets;
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<BiomeDefinition[]>(json) ?? BiomeDefinition.AllPresets;
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

            biome.VegetationDensity = Math.Max(0f, layer.VegetationDensity);
            biome.TreeDensity = Math.Max(0f, layer.TreeDensity);
            biome.VegetationProfileId = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? "Default" : layer.VegetationProfileId;
            biome.VegetationPatchiness = Math.Clamp(layer.VegetationPatchiness, 0f, 1f);
            biome.WeatherProfileId = string.IsNullOrWhiteSpace(layer.WeatherProfileId) ? "Temperate" : layer.WeatherProfileId;
            biome.RainChance = Math.Clamp(layer.RainChance, 0f, 1f);
            biome.SnowChance = Math.Clamp(layer.SnowChance, 0f, 1f);
            biome.StormChance = Math.Clamp(layer.StormChance, 0f, 1f);
            biome.WindBias = Math.Max(0f, layer.WindBias);
            biome.CloudCoverageBias = Math.Max(0f, layer.CloudCoverageBias);
            biome.FogDensityBias = Math.Max(0f, layer.FogDensityBias);
            biome.SeasonalGrowthMultiplier = Math.Max(0f, layer.SeasonalGrowthMultiplier);
        }

        var presets = BiomeDefinition.AllPresets;
        for (int i = 0; i < _config.Biomes.Length; i++)
        {
            var biome = _config.Biomes[i];
            var preset = presets[Math.Min(i, presets.Length - 1)];
            biome.HeightAmplitude = preset.HeightAmplitude * ampScale;
            biome.NoiseFrequency = MathF.Max(0.0001f, preset.NoiseFrequency * freqScale);
        }

        RecalcSeaLevel();
        RebuildWater();

        _chunkManager?.Dispose();
        _chunkManager = null;

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
        SavePlanetAsset();
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
        _config.WeatherSeed = WeatherSeed;
        _config.SeasonLengthMinutes = Math.Max(1f, SeasonLengthMinutes);
        _config.GlobalWeatherIntensity = Math.Max(0f, GlobalWeatherIntensity);
        _config.GlobalWindMultiplier = Math.Max(0f, GlobalWindMultiplier);
        _config.MaxVegetationInstances = Math.Max(1024, MaxVegetationInstances);
        _config.MaxVegetationSpawnsPerUpdate = Math.Max(8, MaxVegetationSpawnsPerUpdate);

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

        SceneRenderer.ResetBiomeTexDebug();
        SavePlanetAsset();
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
        float r = Math.Max(0.05f, radius);
        if (NetworkManager.IsActive && NetworkManager.IsClient && StreamSurfaceFromServerWhenClient)
        {
            SendPlanetVoxelEditToServer(worldCenter, r, densityDelta, falloff);
            return;
        }

        _chunkManager.EnqueueSphereEdit(worldCenter, r, densityDelta, falloff);
        if (NetworkManager.IsActive && NetworkManager.IsServer)
        {
            float invalidateR = r + Math.Max(2f, MathF.Abs(densityDelta));
            BroadcastPlanetInvalidateClients(GetPlanetNetworkId(), worldCenter, invalidateR);
        }
    }

    void SendPlanetVoxelEditToServer(SN.Vector3 worldCenter, float radius, float densityDelta, float falloff)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(GetPlanetNetworkId());
        bw.Write(worldCenter.X);
        bw.Write(worldCenter.Y);
        bw.Write(worldCenter.Z);
        bw.Write(radius);
        bw.Write(densityDelta);
        bw.Write(falloff);
        NetworkManager.SendRPC(0, "PlanetVoxelEdit", ms.ToArray());
    }

    /// <summary>Registers planet RPC handlers. Called from <see cref="PlanetTerrain.Awake"/> and <see cref="NetworkManager.StartServer"/>.</summary>
    public static void EnsurePlanetNetworkRpcsRegistered()
    {
        if (_registeredPlanetNetRpcs) return;
        NetworkManager.RegisterRPC("PlanetVoxelEdit", OnPlanetVoxelEditRpc);
        NetworkManager.RegisterRPC("PlanetVoxelInvalidate", OnPlanetVoxelInvalidateRpc);
        _registeredPlanetNetRpcs = true;
    }

    static void BroadcastPlanetInvalidateClients(uint netId, SN.Vector3 worldCenter, float invalidateRadius)
    {
        if (!NetworkManager.IsActive || !NetworkManager.IsServer) return;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(netId);
        bw.Write(worldCenter.X);
        bw.Write(worldCenter.Y);
        bw.Write(worldCenter.Z);
        bw.Write(invalidateRadius);
        NetworkManager.SendRPCAll("PlanetVoxelInvalidate", ms.ToArray());
    }

    static void OnPlanetVoxelInvalidateRpc(int peerId, byte[] data)
    {
        _ = peerId;
        if (NetworkManager.IsServer) return;
        try
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            uint netId = br.ReadUInt32();
            float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
            float invalidateR = br.ReadSingle();
            var center = new SN.Vector3(x, y, z);

            foreach (var p in ActivePlanets)
            {
                var cm = p.ChunkManager;
                if (!p.IsActiveAndEnabled || cm == null) continue;
                uint pid = p.GetPlanetNetworkId();
                if (netId != 0)
                {
                    if (pid != netId) continue;
                }
                else if (pid != 0)
                    continue;

                cm.MarkLeavesDirtyNearWorldEdit(center, p.GetWorldCenter(), invalidateR);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[PlanetTerrain] PlanetVoxelInvalidate error: {ex.Message}");
        }
    }

    static void OnPlanetVoxelEditRpc(int peerId, byte[] data)
    {
        _ = peerId;
        try
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            uint netId = br.ReadUInt32();
            float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
            float radius = br.ReadSingle();
            float densityDelta = br.ReadSingle();
            float falloff = br.ReadSingle();
            var center = new SN.Vector3(x, y, z);

            foreach (var p in ActivePlanets)
            {
                var cm = p.ChunkManager;
                if (!p.IsActiveAndEnabled || cm == null) continue;
                uint pid = p.GetPlanetNetworkId();
                if (netId != 0)
                {
                    if (pid != netId) continue;
                }
                else if (pid != 0)
                    continue;

                cm.EnqueueSphereEdit(center, radius, densityDelta, falloff);
                float invalidateR = radius + Math.Max(2f, MathF.Abs(densityDelta));
                BroadcastPlanetInvalidateClients(netId, center, invalidateR);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[PlanetTerrain] PlanetVoxelEdit RPC error: {ex.Message}");
        }
    }

    float ResolveFalloff(float value) => value < 0f ? DefaultManipulationFalloff : Math.Clamp(value, 0f, 1f);
}
