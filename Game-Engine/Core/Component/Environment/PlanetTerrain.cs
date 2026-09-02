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
    [Persist] public float SeaLevelFraction { get; set; } = 0.55f;
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
    bool _pendingVoxelMeshRefresh;
    PlanetWater? _planetWater;
    PlanetVegetationAssetData? _pendingVegetationAssetData;
    bool? _wiredStreamClient;

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

    // Cached noise / sampler for runtime height queries (same cache shape as mesh generator)
    PlanetNoiseCache? _noiseCache;
    PlanetDensitySampler? _densitySampler;
    PlanetClimateAtlas? _climateAtlas;
    Noise.FractalNoise[]? _biomeNoises;
    Noise.FractalNoise? _erosionNoise;
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
    public PlanetClimateAtlas? ClimateAtlas => _climateAtlas;
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
    const float ChunkUpdateIntervalSec = 0.05f; // 20 Hz chunk manager update
    const float ChunkUpdateMoveThreshold = 1.0f;
    const float FastApproachSpeed = 40f;
    float _chunkUpdateAccumSec;
    float _playEditLodCooldown;
    bool _playEditRefreshQueued;
    SN.Vector3 _lastChunkUpdateCamPos = new(float.NaN);
    bool _skipLodChunkUpdate;

    // Per-frame cache for expensive rendered-crust samples (grass batches call this thousands of times).
    int _vegCrustCacheFrame = -1;
    readonly Dictionary<long, SN.Vector3> _vegCrustCache = new(256);
    int _worldXformFrame = int.MinValue;
    SN.Vector3 _worldCenterCached;
    float _worldScaleCached = 1f;

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
        _voxelEditStore ??= new PlanetVoxelEditStore();
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
        _config.WaterBodies = CloneWaterBodies(_config.WaterBodies);
        _config.WaterPaths = CloneWaterPaths(_config.WaterPaths);
        _voxelEditStore ??= new PlanetVoxelEditStore();

        // If a graph path is present, apply it immediately so runtime/game view
        // doesn't fall back to default biome colors unless manually compiled.
        TryLoadBiomeGraph();

        // If graph load didn't build runtime state, create defaults.
        if (_chunkManager == null || _biomeMap == null)
        {
            RecalcSeaLevel();

            _biomeMap = CreateBiomeMap();
            _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
            _wiredStreamClient = null;
            RebuildPhysicsNoise();
        }
        else if (_riverNoisePrimary == null && _config.NeedsRiverNoise)
        {
            RebuildPhysicsNoise();
        }

        if (EnableWater)
            RebuildWater();

        ApplyPendingVegetationAssetData();
        WireSurfaceStreaming();
        FlushPendingVoxelMeshRefresh();
    }

    void FlushPendingVoxelMeshRefresh()
    {
        if (!_pendingVoxelMeshRefresh || _chunkManager == null || _voxelEditStore == null)
            return;
        if (_voxelEditStore.SphereEditCount == 0 && _voxelEditStore.BakedCellCount == 0)
        {
            _pendingVoxelMeshRefresh = false;
            return;
        }

        _chunkManager.ResetAfterVoxelEditsLoaded();
        _pendingVoxelMeshRefresh = false;
    }

    void WireSurfaceStreaming()
    {
        if (_chunkManager == null) return;
        bool streamClient = NetworkManager.IsActive && NetworkManager.IsClient && StreamSurfaceFromServerWhenClient;
        if (_wiredStreamClient == streamClient) return;
        _wiredStreamClient = streamClient;
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
            SaveVoxelEdits();
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
        int seed = _config.Seed;

        if (_biomeMap != null)
        {
            _noiseCache = PlanetNoiseCache.Create(_config);
            _biomeNoises = _noiseCache.BiomeNoises;
            _erosionNoise = _noiseCache.ErosionNoise;
            _ridgeNoise = _noiseCache.RidgeNoise;
            _basinNoise = _noiseCache.BasinNoise;
            _riverNoisePrimary = _noiseCache.RiverPrimary;
            _riverNoiseMeander = _noiseCache.RiverMeander;
            // Bind climate coupling before atlas bake so lapse / water / rain-shadow land in the LUT.
            _biomeMap.BindClimateCoupling(_config, _riverNoisePrimary, _riverNoiseMeander, _ridgeNoise);
            _config.GeologyNoise = new Noise.SimplexNoise(seed + 11000);
            _densitySampler = new PlanetDensitySampler(_config, _biomeMap, _noiseCache, _voxelEditStore);
            try
            {
                _climateAtlas = PlanetClimateAtlas.Bake(_config, _biomeMap, _noiseCache, 256);
                _densitySampler?.SetClimateAtlas(_climateAtlas);
                _chunkManager?.SetClimateAtlas(_climateAtlas);
            }
            catch (Exception ex)
            {
                Log.Warning($"[PlanetTerrain] Climate atlas bake failed: {ex.Message}");
                _climateAtlas = null;
            }
        }
        else
        {
            _riverNoisePrimary = _config.NeedsRiverNoise ? new Noise.SimplexNoise(seed + 10000) : null;
            _riverNoiseMeander = _config.NeedsRiverNoise ? new Noise.SimplexNoise(seed + 11000) : null;
        }
    }

    PlanetWaterCarveContext CreateWaterCarveContext() => new()
    {
        Config = _config!,
        RiverPrimary = _riverNoisePrimary,
        RiverMeander = _riverNoiseMeander,
        ClimateAtlas = _climateAtlas
    };

    float SampleCarvedHeight(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null || _biomeNoises == null)
            return 0f;

        // Macro height from climate LUT when available; detail noise still applied via full sample
        // for water carving / edits. LUT path is the hot query for vegetation radial estimates.
        if (_climateAtlas != null)
        {
            float macro = _climateAtlas.SampleMacroHeight(sphereDir);
            var carve = CreateWaterCarveContext();
            return PlanetWaterSampler.ApplyWaterCarving(
                macro, sphereDir, carve.Config, _biomeMap, carve.RiverPrimary, carve.RiverMeander, carve.ClimateAtlas);
        }

        return PlanetSurfaceUtility.SampleHeight(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _ridgeNoise,
            _basinNoise,
            sphereDir,
            CreateWaterCarveContext());
    }

    public PlanetWaterSurfaceSample SampleWaterSurface(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null)
            return PlanetWaterSurfaceSample.Empty;

        if (sphereDir.LengthSquared() < 1e-8f)
            return PlanetWaterSurfaceSample.Empty;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        // Must match chunk water meshing (SampleEditedSurfaceRadius), not the
        // climate-atlas macro shortcut used for vegetation estimates.
        float terrainR = SampleLocalCrustRadius(sphereDir);
        return PlanetWaterSampler.SampleWaterSurface(
            sphereDir,
            _config,
            _biomeMap,
            terrainR,
            _riverNoisePrimary,
            _riverNoiseMeander,
            FindBiomeIndex);
    }

    /// <summary>
    /// Shared water-column test for swimming and underwater FX.
    /// Occupant can be the capsule or the camera. Lava bowls are not swim water.
    /// </summary>
    public bool TryGetWaterColumn(
        SN.Vector3 sphereDir,
        float occupantRadius,
        out float waterWorldR,
        out float crustWorldR,
        out PlanetWaterSurfaceSample sample)
    {
        waterWorldR = 0f;
        crustWorldR = 0f;
        sample = PlanetWaterSurfaceSample.Empty;
        if (_config == null || sphereDir.LengthSquared() < 1e-8f)
            return false;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        float scale = GetWorldRadiusScale();
        crustWorldR = SampleCollisionRadius(sphereDir);
        sample = SampleWaterSurface(sphereDir);

        if (sample.Kind == PlanetWaterKind.Lava)
        {
            waterWorldR = sample.Radius * scale;
            return false;
        }

        float waterR = sample.Mask >= 0.04f ? sample.Radius * scale : 0f;
        float seaR = PlanetWaterSampler.GetOceanFillRadius(_config) * scale;
        if (waterR < 1f && crustWorldR < seaR - 0.2f
            && PlanetSurfaceUtility.SampleMagmaBowl(_config, sphereDir) < 0.18f)
        {
            waterR = seaR;
            sample = new PlanetWaterSurfaceSample(
                seaR / MathF.Max(1e-4f, scale), 1f, 0, PlanetWaterKind.Ocean, 0);
        }

        if (waterR < 1f)
            return false;

        waterWorldR = waterR;
        if (waterR < crustWorldR + 0.05f)
            return false;
        // Jump-in from a bank, or stand on a wet seabed — both are swim water.
        if (occupantRadius > waterR + 2.4f)
            return false;
        // Deep cave under the crust, not a seabed stand.
        if (occupantRadius < crustWorldR - 8f)
            return false;
        return true;
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

        float height = SampleCarvedHeight(sphereDir);
        float baseSurfaceR = _config.Radius + height;

        var sampler = CreateDensitySampler();
        if (sampler == null)
            return baseSurfaceR * worldScale;

        return FindSurfaceRadiusOnRay(sphereDir, baseSurfaceR, sampler) * worldScale;
    }

    /// <summary>
    /// Visible crust radius (heightfield only). Matches the shell mesh while volumetric
    /// transvoxel is off. Avoids the density ray that can pin a player to one radial.
    /// </summary>
    public float SampleHeightfieldRadius(SN.Vector3 sphereDir)
    {
        float worldScale = GetWorldRadiusScale();
        if (_config == null || _biomeMap == null || _biomeNoises == null)
            return Radius * worldScale;

        float height = SampleCarvedHeight(sphereDir);
        return (_config.Radius + height) * worldScale;
    }

    /// <summary>
    /// Player stand radius on the visible chunk mesh. Never Max with the
    /// climate-atlas heightfield — that LUT is a coarser LOD and lifts the
    /// capsule into the air over the rendered crust.
    /// </summary>
    public float SampleCollisionRadius(SN.Vector3 sphereDir)
    {
        float worldScale = GetWorldRadiusScale();
        float crustLocal = _config != null
            ? SampleLocalCrustRadius(sphereDir)
            : Radius;
        float meshLocal = _chunkManager?.SampleCollisionLocalRadius(sphereDir) ?? 0f;
        if (meshLocal <= 1e-4f)
            return crustLocal * worldScale;

        // Cave / empty-bin samples sit tens of meters inside the shell — treat as missing.
        // Small undershoot (bilinear) may pull up a couple of meters toward the mesher crust.
        // Do not ride the analytical peak when it is far above the visible LOD triangle.
        if (meshLocal < crustLocal - 50f)
            return crustLocal * worldScale;
        float pulled = MathF.Max(meshLocal, MathF.Min(crustLocal, meshLocal + 3f));
        return pulled * worldScale;
    }

    /// <summary>
    /// Local unscaled crust radius matching the visible shell mesh
    /// (<see cref="PlanetDensitySampler.SampleEditedSurfaceRadius"/>), not the
    /// volumetric isosurface which can sit far outside the heightfield.
    /// </summary>
    public float SampleLocalCrustRadius(SN.Vector3 sphereDir)
    {
        var sampler = CreateDensitySampler();
        if (sampler != null)
            return MathF.Max(1f, sampler.SampleEditedSurfaceRadius(sphereDir, 0f));
        return WorldToLocalLength(SampleHeightfieldRadius(sphereDir));
    }

    /// <summary>Planet-local crust point (same space as chunk mesh vertices).</summary>
    public SN.Vector3 SampleLocalCrustPoint(SN.Vector3 sphereDir)
    {
        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        sphereDir = SN.Vector3.Normalize(sphereDir);
        return sphereDir * SampleLocalCrustRadius(sphereDir);
    }

    /// <summary>
    /// Visible crust point from live chunk stand grids, including LOD transition neighbors.
    /// Grass-only — do not use for tree anchors (trees use <see cref="SampleVegetationAnchorLocal"/>).
    /// </summary>
    public SN.Vector3 SampleRenderedCrustLocal(SN.Vector3 sphereDir)
    {
        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        else
            sphereDir = SN.Vector3.Normalize(sphereDir);

        if (_chunkManager != null)
        {
            float r = _chunkManager.SampleCollisionLocalRadius(sphereDir);
            if (r > 1e-3f)
                return sphereDir * r;
        }

        return SampleLocalCrustPoint(sphereDir);
    }

    /// <summary>
    /// Vegetation anchor in planet-local space. Prefers the outermost generated chunk vertex
    /// along <paramref name="sphereDir"/> so plants sit on the visible transvoxel/shell mesh,
    /// not only the analytical heightfield sample (which can sit inside after cave meshing).
    /// </summary>
    public SN.Vector3 SampleVegetationAnchorLocal(SN.Vector3 sphereDir)
    {
        int frame = Time.frameCount;
        if (frame != _vegCrustCacheFrame)
        {
            _vegCrustCache.Clear();
            _vegCrustCacheFrame = frame;
        }

        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        sphereDir = SN.Vector3.Normalize(sphereDir);
        long key = QuantizeSphereDir(sphereDir);
        if (_vegCrustCache.TryGetValue(key, out var cached))
            return cached;

        SN.Vector3 local = SampleLocalCrustPoint(sphereDir);
        var leaf = _chunkManager?.FindLeafAtDirection(sphereDir);
        if (leaf != null)
        {
            float bestR = 0f;
            if (TrySampleRenderedCrustPointFromLeaf(sphereDir, leaf, ref local, ref bestR))
            {
                _vegCrustCache[key] = local;
                return local;
            }
        }

        _vegCrustCache[key] = local;
        return local;
    }

    static long QuantizeSphereDir(SN.Vector3 d)
    {
        const float scale = 384f;
        int x = (int)MathF.Round(d.X * scale);
        int y = (int)MathF.Round(d.Y * scale);
        int z = (int)MathF.Round(d.Z * scale);
        return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }

    /// <summary>
    /// Outermost visible crust point along a cube-sphere direction from live chunk meshes.
    /// </summary>
    public bool TrySampleRenderedCrustPoint(SN.Vector3 sphereDir, out SN.Vector3 localPoint, QuadNode? preferLeaf = null)
    {
        localPoint = SampleLocalCrustPoint(sphereDir);
        if (_chunkManager == null)
            return false;

        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        sphereDir = SN.Vector3.Normalize(sphereDir);

        float bestR = 0f;
        var leaf = preferLeaf ?? _chunkManager.FindLeafAtDirection(sphereDir);
        if (leaf == null)
            return false;
        return TrySampleRenderedCrustPointFromLeaf(sphereDir, leaf, ref localPoint, ref bestR);
    }

    static bool TrySampleRenderedCrustPointFromLeaf(
        SN.Vector3 sphereDir,
        QuadNode leaf,
        ref SN.Vector3 localPoint,
        ref float bestR)
    {
        var verts = leaf.GeneratedMesh?.Vertices;
        if (verts == null || verts.Length == 0)
            return false;

        bool found = false;
        for (int i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            float axial = SN.Vector3.Dot(v, sphereDir);
            if (axial <= 0f)
                continue;

            var onAxis = sphereDir * axial;
            float perp = SN.Vector3.Distance(v, onAxis);
            float tol = MathF.Max(1.25f, axial * 0.0075f);
            if (perp > tol)
                continue;

            if (axial <= bestR)
                continue;

            bestR = axial;
            localPoint = v;
            found = true;
        }

        return found;
    }

    static bool TrySampleRenderedCrustPointFromLeaf(SN.Vector3 sphereDir, QuadNode leaf, ref SN.Vector3 localPoint)
    {
        float bestR = 0f;
        return TrySampleRenderedCrustPointFromLeaf(sphereDir, leaf, ref localPoint, ref bestR);
    }

    PlanetDensitySampler? CreateDensitySampler()
    {
        if (_densitySampler != null)
            return _densitySampler;
        if (_config == null || _biomeMap == null || _noiseCache == null)
            return null;

        _densitySampler = new PlanetDensitySampler(_config, _biomeMap, _noiseCache, _voxelEditStore);
        return _densitySampler;
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
        return SampleWaterSurface(sphereDir).Mask;
    }

    public float SampleShoreBiomeIndex(SN.Vector3 sphereDir)
    {
        if (_config == null || _biomeMap == null)
            return 0f;

        if (sphereDir.LengthSquared() < 1e-8f)
            return 0f;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        float terrainR = _config.Radius + SampleCarvedHeight(sphereDir);
        var sand = PlanetWaterSampler.SampleSandWeight(
            sphereDir, _config, _biomeMap, terrainR, _riverNoisePrimary, _riverNoiseMeander, FindBiomeIndex);
        if (sand is { weight: > 0.25f } s)
            return s.biomeIndex;

        var sample = SampleWaterSurface(sphereDir);
        if (sample.Mask > 0.01f)
            return sample.ShoreBiomeIndex;

        float alt = _biomeMap.NormalizeAltitude(SampleCarvedHeight(sphereDir));
        var blends = _biomeMap.GetBiomes(sphereDir, alt);
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
        float prevSea = _config.SeaLevel;
        float maxAmp = 0f;
        foreach (var b in _config.Biomes)
            maxAmp = MathF.Max(maxAmp, b.HeightAmplitude);

        float terrainMin = Radius - maxAmp;
        float terrainMax = Radius + maxAmp;
        _config.SeaLevel = PlanetWaterSampler.ResolveSeaLevel(_config, SeaLevelFraction);
        Log.Info($"[PlanetTerrain] SeaLevel={_config.SeaLevel:F1} (Radius={Radius}, maxAmp={maxAmp:F1}, frac={SeaLevelFraction}, range={terrainMin:F1}-{terrainMax:F1})");

        if (_chunkManager != null && MathF.Abs(_config.SeaLevel - prevSea) > 0.05f)
        {
            RebuildWater();
            _chunkManager.RequestFullShellRebuild(16);
        }
    }

    void SetupWater()
    {
        if (WaterGO != null || gameObject == null || _config == null) return;

        float waterR = PlanetWaterSampler.GetOceanFillRadius(_config);
        _planetWater = new PlanetWater(waterR, 56);
        if (_planetWater.WaterMesh == null) return;

        WaterGO = new GameObject("PlanetWater");

        var mf = new MeshFilter();
        WaterGO.AddBehavior(mf);
        mf.Mesh = _planetWater.WaterMesh;

        var mr = new MeshRenderer();
        mr.Enabled = false;
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
        _chunkManager?.RequestFullShellRebuild(16);
    }

    public override void Update()
    {
        WireSurfaceStreaming();
        ApplyPendingVegetationAssetData();
        if (_chunkManager == null || gameObject == null) return;

        float dt = Math.Max(0f, (float)Time.deltaTime);
        WaterAnimTime += dt;
        if (SceneService.PlayMode)
        {
            _playEditLodCooldown -= dt;
            bool pendingEdits = _chunkManager.PendingEditCommands > 0;
            bool pendingApplies = _chunkManager.PendingCompletedJobs > 0;
            if ((_playEditRefreshQueued || pendingEdits)
                && LastCameraPosition.LengthSquared() > 1e-6f
                && (_playEditLodCooldown <= 0f || _playEditRefreshQueued))
            {
                RefreshLodAroundCamera(LastCameraPosition);
                _playEditRefreshQueued = false;
                _playEditLodCooldown = pendingEdits ? 0.12f : 0.20f;
            }
            else if (pendingApplies)
            {
                _chunkManager.ApplyCompletedMeshJobs();
            }
            return;
        }

        _chunkUpdateAccumSec += dt;
        bool shouldUpdateChunks = _chunkUpdateAccumSec >= ChunkUpdateIntervalSec;
        if (dt <= 1e-6f && !SceneService.PlayMode && _chunkManager.PendingEditCommands > 0)
            shouldUpdateChunks = true;

        if (!float.IsNaN(_lastChunkUpdateCamPos.X))
        {
            var d = LastCameraPosition - _lastChunkUpdateCamPos;
            float dist = d.Length();
            if (dist >= ChunkUpdateMoveThreshold)
                shouldUpdateChunks = true;
            float elapsed = Math.Max(1e-3f, _chunkUpdateAccumSec);
            if (dist / elapsed >= FastApproachSpeed)
                shouldUpdateChunks = true;
        }
        else
        {
            shouldUpdateChunks = true;
        }

        // Edit-mode Time.deltaTime can be 0, so pending strokes would never leave the queue.
        if (_chunkManager.PendingEditCommands > 0)
            shouldUpdateChunks = true;

        if (_skipLodChunkUpdate)
        {
            _skipLodChunkUpdate = false;
        }
        else if (shouldUpdateChunks)
        {
            _chunkUpdateAccumSec = 0f;
            _lastChunkUpdateCamPos = LastCameraPosition;
            SyncLodCameraState(LastCameraPosition);
            _chunkManager.Update(LastCameraPosition, GetWorldCenter());
        }
    }

    public void UpdateLOD(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
    }

    /// <summary>
    /// Scene View: run the real quadtree LOD every editor frame (do not collapse
    /// to one mesh per cube face — that is what made the planet look flat).
    /// </summary>
    public void UpdateSceneViewLod(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
        RefreshLodAroundCamera(cameraPos);
        _skipLodChunkUpdate = true;

        float dt = Math.Max(0f, (float)Time.deltaTime);
        WaterAnimTime += dt;
    }

    /// <summary>Queue mesh refresh after edits; throttled LOD runs from GameView.</summary>
    public void NotifyEdited(SN.Vector3 cameraPos)
    {
        LastCameraPosition = cameraPos;
        _playEditLodCooldown = 0f;
    }

    /// <summary>Refine chunks around a world-space camera (editor or play).</summary>
    public void RefreshLodAroundCamera(SN.Vector3 cameraPos, bool allowLodChanges = true)
    {
        LastCameraPosition = cameraPos;
        if (_chunkManager == null || gameObject == null) return;

        SyncLodCameraState(cameraPos);
        _chunkManager.Update(cameraPos, GetWorldCenter(), allowLodChanges);
        _skipLodChunkUpdate = true;
    }

    bool _cameraBelowCrustLatch;
    float _lodCaveCheckAccum;
    SN.Vector3 _lastLodDensityCam = new(float.NaN);

    void SyncLodCameraState(SN.Vector3 cameraPos)
    {
        if (_config == null) return;
        _config.WorldRadiusScale = GetWorldRadiusScale();
        bool belowCrust = _cameraBelowCrustLatch;
        if (_config.EnableCaves && ShouldRecheckCaveLod(cameraPos))
        {
            if (TrySampleWorldDensity(cameraPos, out float density))
            {
                if (density < -0.08f)
                    belowCrust = true;
                else if (density > -0.03f)
                    belowCrust = false;
            }
            else
            {
                float coreR = _config.EffectiveWorldRadius * 0.82f;
                belowCrust = (cameraPos - GetWorldCenter()).Length() < coreR;
            }
        }
        else if (!_config.EnableCaves)
        {
            belowCrust = false;
        }
        _cameraBelowCrustLatch = belowCrust;
        _config.CameraBelowCrust = belowCrust;
        ApplyChunkBudgets(SceneService.PlayMode, belowCrust);
    }

    bool ShouldRecheckCaveLod(SN.Vector3 cameraPos)
    {
        _lodCaveCheckAccum += Math.Max(0f, (float)Time.deltaTime);
        if (float.IsNaN(_lastLodDensityCam.X) || _lodCaveCheckAccum >= 0.25f)
        {
            _lodCaveCheckAccum = 0f;
            _lastLodDensityCam = cameraPos;
            return true;
        }
        if (SN.Vector3.DistanceSquared(cameraPos, _lastLodDensityCam) > 36f)
        {
            _lastLodDensityCam = cameraPos;
            return true;
        }
        return false;
    }

    void ApplyChunkBudgets(bool play, bool cameraInside = false)
    {
        if (_config == null) return;
        if (play)
        {
            _config.LodDistanceMultiplier = Math.Min(LodDistanceMultiplier, 1.85f);
            _config.SplitDistanceScale = 0.55f;
            _config.MergeDistanceScale = Math.Max(MergeDistanceScale, 1.8f);
            _config.MaxMeshAppliesPerUpdate = 12;
            _config.MaxVegetationSpawnsPerUpdate = Math.Min(MaxVegetationSpawnsPerUpdate, 8);

            if (cameraInside)
            {
                _config.MaxLodDepth = Math.Clamp(MaxLodDepth, 4, 6);
                int cap = Math.Clamp(MaxActiveChunks, 80, 96);
                _config.MaxActiveChunks = cap;
                _config.MaxLeafNodes = cap;
                _config.MaxGenerationSchedulesPerUpdate = 12;
            }
            else
            {
                _config.MaxLodDepth = Math.Clamp(MaxLodDepth, 4, 5);
                int cap = Math.Clamp(MaxActiveChunks, 32, 64);
                _config.MaxActiveChunks = cap;
                _config.MaxLeafNodes = cap;
                _config.MaxGenerationSchedulesPerUpdate = 6;
            }
        }
        else
        {
            _config.MaxLodDepth = Math.Clamp(MaxLodDepth, 4, 6);
            int cap = Math.Clamp(MaxActiveChunks, 48, 160);
            _config.MaxActiveChunks = cap;
            _config.MaxLeafNodes = cap;
            _config.LodDistanceMultiplier = Math.Min(LodDistanceMultiplier, 2.8f);
            _config.SplitDistanceScale = 0.70f;
            _config.MaxGenerationSchedulesPerUpdate = 16;
            _config.MaxMeshAppliesPerUpdate = 12;
        }

        _config.VolumetricMaxCellSize = 3.5f;
        if (cameraInside && play)
            _config.VolumetricMaxCellSize = 11f;
        if (cameraInside && !play)
        {
            _config.VolumetricMaxCellSize = 11f;
            _config.MaxLodDepth = Math.Max(_config.MaxLodDepth, 6);
            _config.MaxActiveChunks = Math.Max(_config.MaxActiveChunks, 200);
            _config.MaxLeafNodes = Math.Max(_config.MaxLeafNodes, 200);
            _config.MaxGenerationSchedulesPerUpdate = Math.Max(_config.MaxGenerationSchedulesPerUpdate, 18);
            _config.MaxMeshAppliesPerUpdate = Math.Max(_config.MaxMeshAppliesPerUpdate, 14);
        }
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

    void RefreshWorldXformCache()
    {
        int f = Time.frameCount;
        if (f == _worldXformFrame && f != 0)
            return;
        _worldXformFrame = f;
        if (gameObject == null)
        {
            _worldCenterCached = SN.Vector3.Zero;
            _worldScaleCached = 1f;
            return;
        }
        var world = SceneGraphUtil.AccumulateWorld(gameObject);
        _worldCenterCached = new SN.Vector3(world.M41, world.M42, world.M43);
        float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
        float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
        float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
        _worldScaleCached = MathF.Max(0.0001f, (sx + sy + sz) / 3f);
    }

    public SN.Vector3 GetWorldCenter()
    {
        RefreshWorldXformCache();
        return _worldCenterCached;
    }

    public float GetWorldRadiusScale()
    {
        RefreshWorldXformCache();
        return _worldScaleCached;
    }

    /// <summary>
    /// World point → planet-local unscaled space (subtract center, then unscale).
    /// Density, edits, and generated meshes all use this space.
    /// </summary>
    public SN.Vector3 WorldToLocal(SN.Vector3 worldPos)
        => PlanetSpace.WorldToLocal(worldPos, GetWorldCenter(), GetWorldRadiusScale());

    /// <summary>Planet-local unscaled point → world space.</summary>
    public SN.Vector3 LocalToWorld(SN.Vector3 localPos)
        => PlanetSpace.LocalToWorld(localPos, GetWorldCenter(), GetWorldRadiusScale());

    /// <summary>World length (brush radius) → planet-local unscaled length.</summary>
    public float WorldToLocalLength(float worldLength)
        => PlanetSpace.WorldToLocalLength(worldLength, GetWorldRadiusScale());

    /// <summary>Planet-local unscaled length → world length.</summary>
    public float LocalToWorldLength(float localLength)
        => PlanetSpace.LocalToWorldLength(localLength, GetWorldRadiusScale());

    /// <summary>
    /// Ray-march the volumetric density field (crust + caves + edits). Hits cave
    /// floors, walls, and ceilings on any hemisphere. For editor picking, prefer this
    /// over <see cref="SampleSurfaceRadius"/>.
    /// </summary>
    public bool RaycastDensity(
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float maxDistance,
        out PlanetDensityHit hit,
        PlanetDensityProbeQuality quality = default)
    {
        hit = default;
        var sampler = CreateDensitySampler();
        if (sampler == null || _config == null)
            return false;
        return PlanetDensityRaycast.Raycast(
            sampler, GetWorldCenter(), GetWorldRadiusScale(),
            worldOrigin, worldDirection, maxDistance, out hit, quality);
    }

    /// <summary>Player/gameplay ray: 32 steps / 4 refine (editor picking stays 96/10).</summary>
    public bool RaycastDensityGameplay(SN.Vector3 worldOrigin, SN.Vector3 worldDirection, float maxDistance, out PlanetDensityHit hit)
        => RaycastDensity(worldOrigin, worldDirection, maxDistance, out hit, PlanetDensityProbeQuality.Gameplay);

    /// <summary>Same as <see cref="RaycastDensity"/> — alias for Scene View brushes.</summary>
    public bool Raycast(SN.Vector3 worldOrigin, SN.Vector3 worldDirection, float maxDistance, out PlanetDensityHit hit)
        => RaycastDensity(worldOrigin, worldDirection, maxDistance, out hit);

    /// <summary>Play-mode tool picking: iso crossing, then geometric fallback.</summary>
    public bool RaycastPaintSurface(SN.Vector3 worldOrigin, SN.Vector3 worldDirection, float maxDistance, out PlanetDensityHit hit)
    {
        hit = default;
        var sampler = CreateDensitySampler();
        if (sampler != null && _config != null)
        {
            if (PlanetDensityRaycast.RaycastIsoCrossing(
                    sampler, GetWorldCenter(), GetWorldRadiusScale(),
                    worldOrigin, worldDirection, maxDistance, out hit))
                return true;
        }

        float lenSq = worldDirection.LengthSquared();
        if (lenSq < 1e-12f)
            return false;
        var dir = worldDirection / MathF.Sqrt(lenSq);
        var center = GetWorldCenter();
        float scale = GetWorldRadiusScale();
        float outerR = (Radius + 80f) * scale;
        float t = Picking.RayIntersectSphere(worldOrigin, dir, center, outerR);
        if (t >= float.MaxValue * 0.5f || t > maxDistance)
            return false;

        var approx = worldOrigin + dir * t;
        var local = WorldToLocal(approx);
        float localLen = local.Length();
        var sphereDir = localLen > 1e-5f ? local / localLen : SN.Vector3.UnitY;

        if (TrySampleLocalIsosurface(sphereDir, out var localPt, out var localN))
        {
            var worldPt = LocalToWorld(localPt);
            var nWorld = LocalToWorld(localPt + localN) - worldPt;
            hit = new PlanetDensityHit
            {
                Point = worldPt,
                Normal = nWorld.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(nWorld) : sphereDir,
                Distance = SN.Vector3.Distance(worldOrigin, worldPt),
                StartedInside = false
            };
            return true;
        }

        float surfaceR = SampleSurfaceRadius(sphereDir);
        var worldHit = center + sphereDir * surfaceR;
        hit = new PlanetDensityHit
        {
            Point = worldHit,
            Normal = sphereDir,
            Distance = SN.Vector3.Distance(worldOrigin, worldHit),
            StartedInside = false
        };
        return true;
    }

    /// <summary>
    /// Sphere-march the density field. <paramref name="worldRadius"/> is the query sphere
    /// in world units. Use for capsule/rigidbody contact.
    /// </summary>
    public bool Spherecast(
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float worldRadius,
        float maxDistance,
        out PlanetDensityHit hit,
        PlanetDensityProbeQuality quality = default)
    {
        hit = default;
        var sampler = CreateDensitySampler();
        if (sampler == null || _config == null)
            return false;
        return PlanetDensityRaycast.Spherecast(
            sampler, GetWorldCenter(), GetWorldRadiusScale(),
            worldOrigin, worldDirection, worldRadius, maxDistance, out hit, quality);
    }

    /// <summary>Player/gameplay spherecast: 32 steps / 4 refine (editor picking stays 96/10).</summary>
    public bool SpherecastGameplay(
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float worldRadius,
        float maxDistance,
        out PlanetDensityHit hit)
        => Spherecast(worldOrigin, worldDirection, worldRadius, maxDistance, out hit, PlanetDensityProbeQuality.Gameplay);

    /// <summary>
    /// Local isosurface along a cube-sphere direction (painted pits, cave mouths).
    /// Not the water/orbit outermost radius from <see cref="SampleSurfaceRadius"/>.
    /// </summary>
    public bool TrySampleLocalIsosurface(SN.Vector3 sphereDir, out SN.Vector3 localPoint, out SN.Vector3 localNormal)
    {
        localPoint = SN.Vector3.Zero;
        localNormal = SN.Vector3.UnitY;
        var sampler = CreateDensitySampler();
        if (sampler == null || _config == null)
            return false;
        return PlanetDensityRaycast.TrySampleLocalIsosurface(
            sampler, _config, sphereDir, out localPoint, out localNormal);
    }

    /// <summary>Push <paramref name="worldPos"/> out of solid density by at least <paramref name="worldClearance"/>.</summary>
    public bool ResolveDensityPenetration(ref SN.Vector3 worldPos, float worldClearance, int maxIters = 10)
    {
        var sampler = CreateDensitySampler();
        if (sampler == null)
            return false;
        return PlanetDensityRaycast.ResolvePenetration(
            sampler, GetWorldCenter(), GetWorldRadiusScale(), ref worldPos, worldClearance, maxIters);
    }

    /// <summary>
    /// Density at a world point (same field as meshing). Negative = solid, positive = air.
    /// </summary>
    public bool TrySampleWorldDensity(SN.Vector3 worldPos, out float density)
    {
        density = 1f;
        var sampler = CreateDensitySampler();
        if (sampler == null)
            return false;
        density = sampler.SampleDensity(WorldToLocal(worldPos));
        return true;
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
        LoadVoxelEdits(data.VoxelEditsPath);
        return true;
    }

    /// <summary>
    /// Writes the <c>.planetvox</c> sidecar. Safe for editor stroke-end auto-save.
    /// Clients skip persist; the server writes when it owns the asset.
    /// </summary>
    public bool SaveVoxelEdits()
    {
        if (!OwnsPlanetAssetForPersist())
            return false;

        EnsurePlanetAssetPath();
        _voxelEditStore ??= new PlanetVoxelEditStore();
        var sidecarRel = PlanetAssetIO.GetVoxelEditsSidecarProjectRelative(PlanetAssetPath);
        var asset = _voxelEditStore.ExportAsset(bakeIfOverThreshold: true);
        if (!PlanetAssetIO.TrySaveVoxelEdits(PlanetAssetPath, asset, out var error, sidecarRel))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Log.Info($"[PlanetTerrain] {error}");
            return false;
        }

        return true;
    }

    /// <summary>Replaces the live edit store from the sidecar next to the <c>.planet</c> (or <paramref name="voxelEditsPath"/>).</summary>
    public bool LoadVoxelEdits(string? voxelEditsPath = null)
    {
        EnsurePlanetAssetPath();
        _voxelEditStore ??= new PlanetVoxelEditStore();
        if (!PlanetAssetIO.TryLoadVoxelEdits(PlanetAssetPath, voxelEditsPath, out var asset, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Log.Info($"[PlanetTerrain] {error}");
            return false;
        }

        if (asset == null)
            return true;

        try
        {
            _voxelEditStore.LoadFromAsset(asset);
            if (_chunkManager != null)
                _chunkManager.ResetAfterVoxelEditsLoaded();
            else
                _pendingVoxelMeshRefresh = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"[PlanetTerrain] Voxel edit load failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when this process may write the .planet / .planetvox files (offline or server host).</summary>
    public static bool OwnsPlanetAssetForPersist()
    {
        if (!NetworkManager.IsActive)
            return true;
        return NetworkManager.IsServer;
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
            Version = PlanetAssetData.CurrentVersion,
            BiomeGraphPath = PlanetAssetIO.NormalizeProjectRelative(BiomeGraphPath),
            SeaLevelFraction = SeaLevelFraction,
            EnableWater = EnableWater,
            Config = CloneConfig(cfg),
            Vegetation = BuildVegetationAssetData(),
            VoxelEditsPath = PlanetAssetIO.GetVoxelEditsSidecarProjectRelative(PlanetAssetPath),
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
            UseSelectClassifier = src.UseSelectClassifier,
            AltitudeSeaLevel = src.AltitudeSeaLevel,
            AltitudeMaxHeight = src.AltitudeMaxHeight,
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
            WaterBodies = CloneWaterBodies(src.WaterBodies),
            WaterPaths = CloneWaterPaths(src.WaterPaths),
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

    static PlanetWaterBody[] CloneWaterBodies(PlanetWaterBody[]? src)
    {
        if (src == null || src.Length == 0)
            return Array.Empty<PlanetWaterBody>();
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<PlanetWaterBody[]>(json) ?? Array.Empty<PlanetWaterBody>();
    }

    static PlanetWaterPath[] CloneWaterPaths(PlanetWaterPath[]? src)
    {
        if (src == null || src.Length == 0)
            return Array.Empty<PlanetWaterPath>();
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<PlanetWaterPath[]>(json) ?? Array.Empty<PlanetWaterPath>();
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
        _config.WaterBodies = CloneWaterBodies(result.WaterBodies);
        _config.WaterPaths = CloneWaterPaths(result.WaterPaths);
        _config.RecipeHash = result.RecipeHash;
        _config.Continents = result.Continents ?? Array.Empty<Biome.Graph.ContinentRecipe>();
        _config.Craters = result.Craters ?? Array.Empty<Biome.Graph.CraterRecipe>();
        _config.Volcanoes = result.Volcanoes ?? Array.Empty<Biome.Graph.VolcanoRecipe>();
        _config.Cliffs = result.Cliffs ?? Array.Empty<Biome.Graph.CliffRecipe>();
        _config.DomainWarps = result.DomainWarps ?? Array.Empty<Biome.Graph.DomainWarpRecipe>();
        _config.LatitudeBands = result.LatitudeBands ?? Array.Empty<Biome.Graph.LatitudeBandRecipe>();

        // Climate coupling from compiled recipe / Climate+RainShadow nodes.
        var climate = result.Recipe?.Climate;
        if (climate != null)
        {
            if (climate.AltitudeLapseRate > 0f)
                _config.AltitudeLapseRate = climate.AltitudeLapseRate;
            if (climate.RainShadowStrength > 0f)
                _config.RainShadowStrength = climate.RainShadowStrength;
            if (climate.WaterMoistureBoost > 0f)
                _config.WaterMoistureBoost = climate.WaterMoistureBoost;
        }

        if (result.WaterBodies is { Length: > 0 })
        {
            var ocean = result.WaterBodies.FirstOrDefault(b => b.Kind == PlanetWaterBodyKind.Ocean);
            if (ocean != null)
                SeaLevelFraction = ocean.FillFraction;
        }

        _config.UseSelectClassifier = result.UseBiomeSelect;
        _config.AltitudeSeaLevel = result.AltitudeSeaLevel;
        _config.AltitudeMaxHeight = result.AltitudeMaxHeight > 0f ? result.AltitudeMaxHeight : 1f;
        if (result.UseBiomeSelect)
            _config.AltitudeWeight = MathF.Max(_config.AltitudeWeight, result.SelectAltitudeWeight);

        int layerCount = result.Layers.Length;
        if (layerCount > 0)
        {
            var next = new BiomeDefinition[layerCount];
            var presets = BiomeDefinition.AllPresets;
            for (int i = 0; i < layerCount; i++)
            {
                var layer = result.Layers[i];
                var preset = MatchPreset(layer.BiomeName, i);
                var biome = CloneBiomeDefinition(preset);
                biome.BiomeIndex = (byte)Math.Min(i, 255);

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
                if (layer.HasErosionInput || layer.ErosionStrength >= 0f)
                    biome.ErosionStrength = layer.ErosionStrength;
                if (layer.HasErosionInput || layer.ErosionFrequency > 0f)
                    biome.ErosionFrequency = layer.ErosionFrequency;
                biome.SpawnWater = layer.SpawnWater;
                if (layer.SpawnWater)
                    biome.MaxAltitude = MathF.Min(biome.MaxAltitude, 0.10f);

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
                biome.GrowthTemperatureMin = layer.GrowthTemperatureMin;
                biome.GrowthTemperatureMax = layer.GrowthTemperatureMax;
                biome.GrowthMoistureMin = layer.GrowthMoistureMin;
                biome.GrowthMoistureMax = layer.GrowthMoistureMax;
                biome.TreeMinSlope = layer.TreeMinSlope;
                biome.TreeMaxSlope = layer.TreeMaxSlope;
                biome.TreeMinAltitude = layer.TreeMinAltitude;
                biome.TreeMaxAltitude = layer.TreeMaxAltitude;

                // Graph height/noise win only when that layer has a height/noise input.
                // Presets stay as-is otherwise — no global amp/freq scale overwrite.
                if (layer.HasHeightInput && layer.HeightAmplitude > 0f)
                    biome.HeightAmplitude = layer.HeightAmplitude;
                if (layer.HasNoiseInput && layer.NoiseFrequency > 0f)
                    biome.NoiseFrequency = MathF.Max(0.0001f, layer.NoiseFrequency);

                next[i] = biome;
            }
            _config.Biomes = next;
            if (layerCount > Biome.Graph.BiomeOutputNode.MaxLayerSlots)
                Log.Info($"[PlanetTerrain] Graph has {layerCount} layers; shader binds 8 albedo slots (index >= 7 uses uBiomeTex7).");

            if (result.Recipe.Geology.MacroFrequency > 0f)
            {
                MacroFrequency = result.Recipe.Geology.MacroFrequency;
                _config.MacroFrequency = MacroFrequency;
            }
        }

        RecalcSeaLevel();

        _chunkManager?.Dispose();
        _chunkManager = null;

        _biomeMap = CreateBiomeMap();
        _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
        _wiredStreamClient = null;
        RebuildPhysicsNoise();
        RebuildWater();
        _chunkManager.RequestFullShellRebuild(16);

        // Bind compiled life/scatter/fauna tables onto companion components when present.
        var flora = GetComponent<PlanetFloraSpawner>();
        flora?.ApplyRecipes(result.FloraLayers);
        var scatter = GetComponent<PlanetScatterRenderer>();
        scatter?.ApplyRecipes(result.ScatterLayers);
        var fauna = GetComponent<PlanetFaunaTableBehavior>();
        fauna?.Bind(result.FaunaLayers);

        SceneRenderer.ResetBiomeTexDebug();
        SavePlanetAsset();
        SceneService.NotifyChanged();
    }

    static BiomeDefinition CloneBiomeDefinition(BiomeDefinition src) => new()
    {
        Name = src.Name,
        BiomeIndex = src.BiomeIndex,
        BaseColorR = src.BaseColorR,
        BaseColorG = src.BaseColorG,
        BaseColorB = src.BaseColorB,
        HeightAmplitude = src.HeightAmplitude,
        NoiseFrequency = src.NoiseFrequency,
        NoiseLacunarity = src.NoiseLacunarity,
        NoiseOctaves = src.NoiseOctaves,
        NoiseMode = src.NoiseMode,
        ErosionStrength = src.ErosionStrength,
        ErosionFrequency = src.ErosionFrequency,
        MinAltitude = src.MinAltitude,
        MaxAltitude = src.MaxAltitude,
        MinTemperature = src.MinTemperature,
        MaxTemperature = src.MaxTemperature,
        MinMoisture = src.MinMoisture,
        MaxMoisture = src.MaxMoisture,
        TopTexturePath = src.TopTexturePath,
        TopNormalMapPath = src.TopNormalMapPath,
        TopTiling = src.TopTiling,
        UnderTexturePath = src.UnderTexturePath,
        UnderNormalMapPath = src.UnderNormalMapPath,
        UnderTiling = src.UnderTiling,
        CavesEnabled = src.CavesEnabled,
        VegetationDensity = src.VegetationDensity,
        TreeDensity = src.TreeDensity,
        VegetationProfileId = src.VegetationProfileId,
        VegetationPatchiness = src.VegetationPatchiness,
        WeatherProfileId = src.WeatherProfileId,
        GrowthTemperatureMin = src.GrowthTemperatureMin,
        GrowthTemperatureMax = src.GrowthTemperatureMax,
        GrowthMoistureMin = src.GrowthMoistureMin,
        GrowthMoistureMax = src.GrowthMoistureMax,
        SeasonalGrowthMultiplier = src.SeasonalGrowthMultiplier,
        TreeMinSlope = src.TreeMinSlope,
        TreeMaxSlope = src.TreeMaxSlope,
        TreeMinAltitude = src.TreeMinAltitude,
        TreeMaxAltitude = src.TreeMaxAltitude,
    };

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

        _biomeMap = CreateBiomeMap();

        _chunkManager?.Dispose();
        _chunkManager = new PlanetChunkManager(_config, _biomeMap, _voxelEditStore);
        _wiredStreamClient = null;
        RebuildPhysicsNoise();

        SceneRenderer.ResetBiomeTexDebug();
        SavePlanetAsset();
        SceneService.NotifyChanged();
    }

    BiomeMap CreateBiomeMap()
    {
        float refAmp = 50f;
        if (_config?.Biomes != null)
        {
            foreach (var b in _config.Biomes)
                refAmp = MathF.Max(refAmp, b.HeightAmplitude);
        }
        var map = new BiomeMap(
            _config!.Seed,
            _config.Biomes,
            noiseScale: 2f,
            tempLatWeight: _config.TemperatureLatWeight,
            tempNoiseWeight: _config.TemperatureNoiseWeight,
            moistureNoiseScale: _config.MoistureNoiseScale,
            altitudeWeight: _config.AltitudeWeight,
            edgeDistortionFreq: _config.EdgeDistortionFreq,
            edgeDistortionAmp: _config.EdgeDistortionAmp,
            altitudeSeaLevel: _config.AltitudeSeaLevel,
            altitudeMaxHeight: _config.AltitudeMaxHeight,
            heightAmplitudeRef: refAmp);
        map.UseSelectClassifier = _config.UseSelectClassifier;
        map.BindClimateCoupling(_config, _riverNoisePrimary, _riverNoiseMeander, _ridgeNoise);
        return map;
    }

    static BiomeDefinition MatchPreset(string name, int index)
    {
        var presets = BiomeDefinition.AllPresets;
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var p in presets)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }
        return presets[Math.Min(index, presets.Length - 1)];
    }

    public void DigSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f)
    {
        if (SceneService.PlayMode)
            ApplyPlayModeSphereEdit(worldCenter, radius, HeightStep(strength), ResolveFalloff(falloff));
        else
            QueueSphereEdit(worldCenter, radius, HeightStep(strength), ResolveFalloff(falloff));
    }

    public void BuildSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f)
    {
        if (SceneService.PlayMode)
            ApplyPlayModeSphereEdit(worldCenter, radius, -HeightStep(strength), ResolveFalloff(falloff));
        else
            QueueSphereEdit(worldCenter, radius, -HeightStep(strength), ResolveFalloff(falloff));
    }

    /// <summary>
    /// Play-mode sculpting: store stroke and queue async remesh (never block the UI thread).
    /// </summary>
    void ApplyPlayModeSphereEdit(SN.Vector3 worldCenter, float radius, float densityDelta, float falloff)
    {
        if (_chunkManager == null || _voxelEditStore == null)
            return;

        float r = Math.Clamp(radius, 0.2f, 2.5f);
        if (NetworkManager.IsActive && NetworkManager.IsClient && StreamSurfaceFromServerWhenClient)
        {
            SendPlanetVoxelEditToServer(worldCenter, r, densityDelta, falloff);
            return;
        }

        var localCenter = WorldToLocal(worldCenter);
        float localRadius = WorldToLocalLength(r);
        float cap = MathF.Min(1.5f, MathF.Max(0.35f, localRadius * 0.65f));
        densityDelta = Math.Clamp(densityDelta, -cap, cap);
        falloff = ResolveFalloff(falloff);

        _voxelEditStore.AddSphere(localCenter, localRadius, densityDelta, falloff);
        float invalidateR = localRadius + MathF.Max(0.75f, MathF.Abs(densityDelta));
        _chunkManager.ApplyPlayModeEditVisual(localCenter, invalidateR);

        if (NetworkManager.IsActive && NetworkManager.IsServer)
            BroadcastPlanetInvalidateClients(GetPlanetNetworkId(), worldCenter, r + MathF.Max(0.5f, MathF.Abs(densityDelta)));
    }

    /// <summary>Pull the crust toward the average nearby surface radius.</summary>
    public void SmoothSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f)
    {
        var sampler = CreateDensitySampler();
        if (sampler == null) return;

        var local = WorldToLocal(worldCenter);
        float currentR = sampler.SampleEditedSurfaceRadius(local);
        float avg = SampleNeighborhoodRadius(sampler, local, WorldToLocalLength(Math.Max(0.05f, radius)));
        float error = currentR - avg;
        float step = HeightStep(strength) * 0.65f;
        float delta = Math.Sign(error) * Math.Min(MathF.Abs(error), step);
        if (MathF.Abs(delta) <= 1e-4f) return;
        QueueSphereEdit(worldCenter, radius, delta, ResolveFalloff(falloff));
    }

    /// <summary>
    /// Flatten toward a target crust radius (planet-local meters). Pass the radius
    /// sampled on mouse-down. <paramref name="targetRadius"/> &lt;= 0 uses the hit radius.
    /// </summary>
    public void FlattenSphere(SN.Vector3 worldCenter, float radius, float strength = 0f, float falloff = -1f, float targetRadius = 0f)
    {
        var sampler = CreateDensitySampler();
        if (sampler == null) return;

        var local = WorldToLocal(worldCenter);
        float currentR = sampler.SampleEditedSurfaceRadius(local);
        float target = targetRadius > 1f ? targetRadius : currentR;
        float error = currentR - target;
        float step = HeightStep(strength);
        float delta = Math.Sign(error) * Math.Min(MathF.Abs(error), step);
        if (MathF.Abs(delta) <= 1e-4f) return;
        QueueSphereEdit(worldCenter, radius, delta, ResolveFalloff(falloff));
    }

    float HeightStep(float strength)
    {
        float s = strength <= 0f ? DefaultManipulationStrength : MathF.Abs(strength);
        return Math.Clamp(s, 0.25f, 10f);
    }

    static float SampleNeighborhoodRadius(PlanetDensitySampler sampler, SN.Vector3 localCenter, float localRadius)
    {
        float len = localCenter.Length();
        if (len < 1e-4f)
            return sampler.SampleEditedSurfaceRadius(SN.Vector3.UnitY);
        var dir = localCenter / len;
        var tangent = MathF.Abs(dir.Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
        var u = SN.Vector3.Normalize(SN.Vector3.Cross(dir, tangent));
        var v = SN.Vector3.Cross(dir, u);
        float o = Math.Clamp(localRadius / MathF.Max(len, 1f), 0.002f, 0.12f);
        float sum = sampler.SampleEditedSurfaceRadius(dir);
        int n = 1;
        void Acc(SN.Vector3 d)
        {
            float l = d.Length();
            if (l < 1e-5f) return;
            sum += sampler.SampleEditedSurfaceRadius(d / l);
            n++;
        }
        Acc(dir + u * o);
        Acc(dir - u * o);
        Acc(dir + v * o);
        Acc(dir - v * o);
        Acc(dir + (u + v) * (o * 0.7f));
        Acc(dir + (u - v) * (o * 0.7f));
        Acc(dir + (-u + v) * (o * 0.7f));
        Acc(dir + (-u - v) * (o * 0.7f));
        return sum / n;
    }

    public void SetVoxelDensity(SN.Vector3 worldPos, float targetDensity)
    {
        var sampler = CreateDensitySampler();
        if (sampler == null) return;

        float current = sampler.SampleDensity(WorldToLocal(worldPos));
        float delta = targetDensity - current;
        if (MathF.Abs(delta) <= 1e-6f) return;

        float pointRadius = Math.Max(0.5f, Radius * 0.0025f);
        QueueSphereEdit(worldPos, pointRadius, delta, 0f);
    }

    public void ClearVoxelEdits(bool rebuildNow = true)
    {
        _voxelEditStore?.Clear();
        if (!rebuildNow || gameObject == null) return;
        _chunkManager?.ResetAfterVoxelEditsLoaded();
        _pendingVoxelMeshRefresh = false;
    }

    void MarkAllLeavesDirty()
    {
        if (_chunkManager == null) return;
        for (int f = 0; f < _chunkManager.Faces.Length; f++)
        {
            var leaves = _chunkManager.Faces[f].GetLeafNodes();
            for (int l = 0; l < leaves.Count; l++)
                leaves[l].NeedsMeshRebuild = true;
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

        var localCenter = WorldToLocal(worldCenter);
        float localRadius = WorldToLocalLength(r);
        float cap = MathF.Min(10f, MathF.Max(2.5f, localRadius * 0.45f));
        densityDelta = Math.Clamp(densityDelta, -cap, cap);
        _chunkManager.EnqueueSphereEdit(localCenter, localRadius, densityDelta, falloff);
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

                cm.EnqueueSphereEdit(p.WorldToLocal(center), p.WorldToLocalLength(radius), densityDelta, falloff);
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
