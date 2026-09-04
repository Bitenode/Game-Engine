#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

[ComponentCategory("Environment")]
[Require(typeof(PlanetTerrain))]
public sealed class PlanetVegetationSystem : Behavior
{
    public const string RuntimeRootName = "PlanetVegetation_Runtime";

    static readonly List<PlanetVegetationSystem> s_activeSystems = new(4);
    public static IReadOnlyList<PlanetVegetationSystem> ActiveSystems => s_activeSystems;
    public static bool AnyUseDedicatedRenderPass
    {
        get
        {
            for (int i = 0; i < s_activeSystems.Count; i++)
            {
                var sys = s_activeSystems[i];
                if (sys != null && sys.IsActiveAndEnabled && sys.UseDedicatedRenderPass)
                    return true;
            }
            return false;
        }
    }

    [Persist] public bool AutoSpawn { get; set; } = false;
    [Persist] public int MaxTrackedLeaves { get; set; } = 64;
    [Persist] public float UpdateIntervalSeconds { get; set; } = 0.2f;
    [Persist] public float ActiveDistanceMultiplier { get; set; } = 2.6f;
    [Persist] public int MaxTreesPerLeaf { get; set; } = 4;
    [Persist] public int MaxGrassClumpsPerLeaf { get; set; } = 48;
    [Persist] public float TreeBaseHeight { get; set; } = 7f;
    [Persist] public float GrassBaseHeight { get; set; } = 1.05f;
    [Persist] public bool FullBiomePopulate { get; set; } = true;
    [Persist] public bool UsePlanetAssetPlacements { get; set; } = false;

    /// <summary>
    /// When true (default), loading a .planet file that contains placement entries enables
    /// <see cref="UsePlanetAssetPlacements"/> even if older assets omitted <c>useStoredPlacements</c>.
    /// </summary>
    [Persist] public bool AutoUseSavedPlacementsWhenPresent { get; set; } = true;

    /// <summary>
    /// When true (default), turns on <see cref="AutoSpawn"/> after saved placements are imported so trees/grass stream in over subsequent frames.
    /// </summary>
    [Persist] public bool AutoSpawnWhenUsingSavedPlacements { get; set; } = true;
    [Persist] public int MaxRuntimeGrassPatches { get; set; } = 50000;
    [Persist] public int MaxAssetSpawnsPerUpdate { get; set; } = 128;
    [Persist] public int GrassBladesPerPatch { get; set; } = 24;
    [Persist] public int MaxActiveAssetGrassPatches { get; set; } = 4096;
    [Persist] public int MaxActiveAssetTrees { get; set; } = 512;
    [Persist] public float AssetPlacementActivationDistanceMultiplier { get; set; } = 2.5f;
    [Persist] public int MaxStoredPlacements { get; set; } = 50000;

    /// <summary>0 = trunks use planet radial only (most stable on spheres). 1 = full sampled slope normal (can tilt badly on noisy height).</summary>
    [Persist] public float TreeSurfaceNormalBlend { get; set; } = 0.26f;

    /// <summary>
    /// After blending radial with the slope normal, limits how far trunk “up” can tilt away from planet radial.
    /// Keeps trees upright on cliffs where the sampled normal is nearly tangent to the sphere (avoids sideways / −90° roll artifacts).
    /// </summary>
    [Persist] public float TreeMaxTiltFromRadialDegrees { get; set; } = 28f;

    /// <summary>
    /// Extra distance along the planet radial beyond <see cref="PlanetTerrain.SampleSurfaceRadius"/> when placing trees.
    /// Keep this small (centimeters). Large values plus radius-scaled auto padding make trees hover above the crust.
    /// </summary>
    [Persist] public float TreeRadialSurfaceBias { get; set; } = 0.06f;

    /// <summary>
    /// Added to <see cref="Transform.Rotation"/> after planet alignment when spawning <strong>imported</strong> tree meshes
    /// (biome/asset .fbx/.obj paths). Example: <c>(180,0,0)</c> if the asset’s trunk grows along <c>-Y</c> in file space.
    /// Prefab instances are unchanged—bake corrections into the prefab if needed.
    /// </summary>
    [Persist] public Vector3 ImportedTreeMeshEulerCorrection { get; set; } = new Vector3(0, 0, 0);

    /// <summary>
    /// When true, vegetation groups are removed if their quad-leaf key is not in the current
    /// streaming set. Needed so LOD splits do not leak high-poly trees forever.
    /// </summary>
    [Persist] public bool CullVegetationWhenLeafNotActive { get; set; } = true;

    /// <summary>
    /// When true, instances are removed after ecosystem vitality drops to zero (weather/decay).
    /// When false (default), vitality still affects scale/wind but plants are not deleted.
    /// </summary>
    [Persist] public bool RemoveVegetationWhenVitalityExhausted { get; set; } = false;

    /// <summary>
    /// Keeps spawned trees/grass out of the Hierarchy panel so thousands of instances
    /// do not stall the editor UI.
    /// </summary>
    [Persist] public bool HideInstancesInHierarchy { get; set; } = true;

    /// <summary>
    /// Draw vegetation from a compact runtime root instead of walking the full scene graph.
    /// </summary>
    [Persist] public bool UseDedicatedRenderPass { get; set; } = true;

    /// <summary>
    /// Merge all grass in a terrain leaf into one mesh (one draw call per leaf).
    /// </summary>
    [Persist] public bool BatchGrassPerLeaf { get; set; } = true;

    /// <summary>
    /// Stress-test mode: uses editor-like vegetation density/caps while playing (for perf testing).
    /// </summary>
    [Persist] public bool VegetationStressTest { get; set; }

    /// <summary>
    /// When true, all land biomes share <see cref="UniversalVegetationProfileId"/> plant images.
    /// </summary>
    [Persist] public bool UseUniversalLandVegetation { get; set; } = false;

    /// <summary>Shared grass/tree catalog for every land biome when <see cref="UseUniversalLandVegetation"/> is on.</summary>
    [Persist] public string UniversalVegetationProfileId { get; set; } = "Universal";

    /// <summary>Planets with sea level above this fraction are treated as water worlds (underwater plants allowed).</summary>
    [Persist] public float WaterPlanetSeaLevelFraction { get; set; } = 0.88f;

    public GameObject? RuntimeDrawRoot => _runtimeRoot;
    public GameObject? TerrainGameObject => _terrain?.gameObject;

    /// <summary>
    /// GPU grass is first-person scale. Orbit / universe cameras must not draw it
    /// or unit cards without a valid instance buffer fill the skybox.
    /// </summary>
    public bool ShouldDrawGpuGrass(SN.Vector3 worldCam)
    {
        if (_terrain?.Config == null) return false;
        float r = Math.Max(1f, _terrain.Config.EffectiveWorldRadius) * Math.Max(0.0001f, _terrain.GetWorldRadiusScale());
        float dist = (worldCam - _terrain.GetWorldCenter()).Length();
        return dist <= r + 320f;
    }

    public int ActiveLeafGroups => _leafEntries.Count;
    public int ActiveVegetationInstances
    {
        get
        {
            int n = Math.Max(_assetActive.Count, PlanetGpuGrass.PatchCount(this));
            if (n > 0)
                return n;
            foreach (var group in _leafEntries.Values)
                n += group.Count;
            return n;
        }
    }
    /// <summary>Count of entries deserialized from the .planet asset (before proximity spawn).</summary>
    public int StoredPlacementCount => _assetPlacements.Count;
    public int LastSpawnedThisUpdate { get; private set; }
    public int LastDespawnedThisUpdate { get; private set; }

    const int MaxDespawnsPerRefresh = 32;
    const int PlayMaxDespawnsPerRefresh = 4;
    const float VegRefreshMoveThresholdSq = 100f; // 10 m
    /// <summary>Fixed grid per cube face — vegetation keys ignore LOD splits so plants do not pop when chunks refine.</summary>
    [Persist] public int VegetationCellsPerFaceEdge { get; set; } = 72;
    static int s_vegetationCellsPerFaceEdge = 72;
    /// <summary>Despawn farther out than spawn so nearby grass/trees do not flicker at the stream boundary.</summary>
    const float VegetationCullDistanceHysteresis = 1.45f;

    readonly Dictionary<string, List<Entry>> _leafEntries = new();
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);
    PlanetTerrain? _terrain;
    float _updateAccum;
    float _wetness;
    float _snow;
    float _rain;
    float _cloudiness;
    float _windMultiplier = 1f;
    bool _manualSpawnPass;
    bool _prunedWetPlacements;
    SN.Vector3 _lastVegCamPos = new(float.NaN);
    readonly List<PlanetVegetationPlacement> _assetPlacements = new();
    int _assetSpawnCursor;
    readonly Dictionary<int, Entry> _assetActive = new();
    readonly HashSet<int> _wantedAssetSet = new();
    readonly List<int> _staleAssetIndices = new();
    readonly List<int> _assetSpawnOrder = new();
    readonly List<SN.Vector3> _assetDirs = new();
    readonly List<int> _grassPlacementIdx = new();
    readonly List<int> _treePlacementIdx = new();
    readonly List<int> _nearestGrass = new();
    readonly List<int> _nearestTrees = new();
    readonly List<string> _staleLeafKeys = new();
    readonly Dictionary<int, List<int>> _grassBuckets = new();
    readonly Dictionary<int, List<int>> _treeBuckets = new();
    int[] _nearestIdxScratch = Array.Empty<int>();
    float[] _nearestDistScratch = Array.Empty<float>();
    GameObject? _runtimeRoot;
    GameObject? _gpuGrassMarker;
    bool _queuedProfileTemplates;
    bool _localTreeScanDone;
    bool _carpetUsedFallbackTrees;
    float _treeImportWaitSec;
    int _nextGpuGrassToken = -1;
    int _lastStreamFrame = int.MinValue;
    SN.Vector3 _cachedNearestCamDir = new(float.NaN);
    int _cachedNearestGrassCap;
    int _cachedNearestTreeCap;
    readonly Dictionary<int, SN.Vector3> _carpetDirs = new();
    readonly Dictionary<int, GameObject> _carpetTrees = new();
    readonly Dictionary<int, SN.Vector3> _carpetTreeDirs = new();
    readonly List<string> _localTreeModels = new();
    readonly List<string> _readyTreeScratch = new();
    readonly List<int> _carpetStale = new();
    int _carpetSeq;
    SN.Vector3 _carpetCamDir = new(float.NaN);
    SN.Vector3 _treeCamDir = new(float.NaN);

    sealed class Entry
    {
        public GameObject GameObject = null!;
        public BiomeDefinition Biome = null!;
        public bool IsGrass;
        public float BaseScale = 1f;
        public float Vitality = 1f;
        public SN.Vector3 SurfaceDir;
        public float YawDeg;
        public string BiomeName = "";
        public string PrefabPath = "";
        public string ModelPath = "";
        public string TexturePath = "";
        public int GpuGrassToken;
    }

    sealed class ImportedTreeTemplate
    {
        /// <summary>Live baked import (keeps UVs). JSON clones drop UV channels and kill alpha foliage.</summary>
        public GameObject? Source;
        /// <summary>Full import root serialized for cloning (multi-part trunk + foliage).</summary>
        public string? VisualHierarchyJson;
        public Mesh? Mesh;
        public Material? Material;
        public float MeshExtent;
    }

    static readonly Dictionary<string, ImportedTreeTemplate> s_treeTemplateCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object s_treeTemplateLock = new();
    /// <summary>Suffix so template cache invalidates when import/spawn pipeline changes.</summary>
    const string TreeTemplateCacheKeySuffix = "|hier_v30_pine002";
    const string DefaultPlanetGrassTexturePath = "Assets/Standard Assets/Planet Vegetation/Simple Grass_01.psd";
    const string DefaultPlanetGrassModelPath = "Assets/Standard Assets/Planet Vegetation/Grass/Meadow_Grass_01_Var4.FBX";
    const int MaxImportedTreeTriangles = 48000;
    const long MaxImportedTreeSourceBytes = 1_800_000;
    static readonly ConcurrentQueue<string> s_pendingTemplateLoads = new();
    static readonly HashSet<string> s_queuedTemplateLoads = new(StringComparer.OrdinalIgnoreCase);
    static int s_templateLoaderRunning;
    bool _deferSpawnForTemplate;

    bool PlayPerfLimited => SceneService.PlayMode && !VegetationStressTest;

    public override void Awake() => EnsureInitialized();

    public override void PostDeserialize() => EnsureInitialized();

    public override void OnEnable()
    {
        EnsureInitialized();
        if (AutoUseSavedPlacementsWhenPresent && _assetPlacements.Count > 0)
            UsePlanetAssetPlacements = true;
        if (AutoSpawn && UsePlanetAssetPlacements && _assetPlacements.Count > 0)
            WarmSpawnAfterDeferredImport();
    }

    public override void Start()
    {
        EnsureInitialized();
        if (AutoUseSavedPlacementsWhenPresent && _assetPlacements.Count > 0)
            UsePlanetAssetPlacements = true;
        PruneSubmergedAssetPlacements();
        if (UsePlanetAssetPlacements && _assetPlacements.Count > 0 && AutoSpawn)
            WarmSpawnAfterDeferredImport();
    }

    void EnsureInitialized()
    {
        _terrain ??= GetComponent<PlanetTerrain>();
        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();
        s_vegetationCellsPerFaceEdge = Math.Clamp(VegetationCellsPerFaceEdge, 8, 128);
        if (!s_activeSystems.Contains(this))
            s_activeSystems.Add(this);
        EnsureRuntimeRoot();
        if (!_queuedProfileTemplates)
        {
            QueueProfileTemplateLoads();
            _queuedProfileTemplates = true;
        }
        PlanetGrassTextureCache.EnsureCatalog();
    }

    /// <summary>
    /// Scene View has no play-mode behavior tick — call each render frame with the active camera position.
    /// </summary>
    public static void TickAllStreaming(SN.Vector3 cameraPos, float deltaSeconds)
    {
        for (int i = 0; i < s_activeSystems.Count; i++)
        {
            var sys = s_activeSystems[i];
            if (sys == null || !sys.IsActiveAndEnabled || !sys.AutoSpawn)
                continue;
            sys.TickStreaming(cameraPos, deltaSeconds);
        }
    }

    public void TickStreaming(SN.Vector3 cameraPos, float deltaSeconds)
    {
        if (!AutoSpawn) return;
        EnsureInitialized();
        if (_terrain == null || _terrain.gameObject == null || _terrain.Config == null) return;

        if (_assetPlacements.Count == 0)
            TryPullDeferredPlanetVegetation();

        bool waitingForPlanetFile = _terrain.AsyncVegetationHydrationPending;
        bool usingAssets = UsePlanetAssetPlacements && _assetPlacements.Count > 0;

        int frame = Time.frameCount;
        if (frame == _lastStreamFrame)
            return;
        _lastStreamFrame = frame;

        float dt = Math.Max(0f, deltaSeconds);
        if (dt <= 1e-6f) dt = 1f / 60f;

        // Carpet must run every frame. The asset-stream interval (0.10s+)
        // is what made walking grass trickle in.
        TickLocalGpuGrassCarpet(cameraPos);
        TickLocalTrees(cameraPos);

        _updateAccum += dt;
        float minInterval = Math.Max(0.10f, UpdateIntervalSeconds);
        if (_updateAccum < minInterval)
            return;
        _updateAccum = 0f;

        if (!usingAssets && (waitingForPlanetFile || UsePlanetAssetPlacements))
            return;
        if (!usingAssets && _terrain.ChunkManager == null)
            return;

        bool streamUsingAssets = usingAssets;
        int liveCount = streamUsingAssets ? _assetActive.Count : ActiveVegetationInstances;
        int maxStreamActive = ResolveActiveGrassCap() + ResolveActiveTreeCap();
        bool streamSaturated = streamUsingAssets && liveCount >= maxStreamActive;
        float moveThreshSq = PlayPerfLimited ? 35f * 35f : VegRefreshMoveThresholdSq;
        if (!float.IsNaN(_lastVegCamPos.X) && liveCount > 0 && streamSaturated
            && SN.Vector3.DistanceSquared(cameraPos, _lastVegCamPos) < moveThreshSq)
        {
            return;
        }
        _lastVegCamPos = cameraPos;

        if (streamUsingAssets)
        {
            SpawnFromAssetPlacements(clearExisting: false, cameraOverride: cameraPos);
            DropProceduralLeafGroups();
        }
        else
        {
            RefreshVegetation(playTimeBudgetMs: 2.0);
        }

        if (!SceneService.PlayMode && (LastSpawnedThisUpdate > 0 || LastDespawnedThisUpdate > 0))
            SceneService.NotifyChanged();
    }

    public override void Update()
    {
        TickStreaming(ResolveCameraPosition(), Math.Max(0f, (float)Time.deltaTime));
    }

    public override void OnDestroy()
    {
        s_activeSystems.Remove(this);
        ClearLocalCarpetTrees();
        _carpetDirs.Clear();
        PlanetGpuGrass.ClearOwner(this);
        foreach (var group in _leafEntries.Values)
            for (int i = 0; i < group.Count; i++)
                group[i].GameObject.RemoveFromParent();
        _leafEntries.Clear();
        _runtimeRoot?.RemoveFromParent();
        _runtimeRoot = null;
        _gpuGrassMarker = null;
    }

    public void ApplyWeather(float wetness, float snowCoverage, float windMultiplier, float rainIntensity = 0f, float cloudiness = 0f)
    {
        _wetness = Math.Clamp(wetness, 0f, 1f);
        _snow = Math.Clamp(snowCoverage, 0f, 1f);
        _rain = Math.Clamp(rainIntensity, 0f, 1f);
        _cloudiness = Math.Clamp(cloudiness, 0f, 1f);
        _windMultiplier = Math.Max(0f, windMultiplier);
    }

    public void GetGpuGrassEnvironment(
        out float wetness,
        out float snow,
        out float rain,
        out float cloudiness,
        out float windMul,
        out float sunIntensity,
        out float atmoAmbient)
    {
        wetness = _wetness;
        snow = _snow;
        rain = _rain;
        cloudiness = _cloudiness;
        windMul = _windMultiplier;
        var atmo = _terrain?.Atmosphere;
        sunIntensity = Math.Max(0.12f, atmo?.SunIntensity ?? 1f);
        atmoAmbient = Math.Max(0.10f, atmo?.Ambient ?? 0.18f);
    }

    public static bool TryGetActiveFoliageEnvironment(
        out float wetness,
        out float snow,
        out float rain,
        out float cloudiness,
        out float windMul,
        out float sunIntensity,
        out float atmoAmbient)
    {
        for (int i = 0; i < s_activeSystems.Count; i++)
        {
            var sys = s_activeSystems[i];
            if (sys == null || !sys.IsActiveAndEnabled)
                continue;
            sys.GetGpuGrassEnvironment(
                out wetness, out snow, out rain, out cloudiness,
                out windMul, out sunIntensity, out atmoAmbient);
            return true;
        }

        wetness = 0f;
        snow = 0f;
        rain = 0f;
        cloudiness = 0f;
        windMul = 1f;
        sunIntensity = 1f;
        atmoAmbient = 0.18f;
        return false;
    }

    public PlanetVegetationAssetData ExportAssetData()
    {
        var center = GetWorldCenter();
        var placements = new List<PlanetVegetationPlacement>();
        foreach (var group in _leafEntries.Values)
        {
            for (int i = 0; i < group.Count; i++)
            {
                var e = group[i];
                var go = e.GameObject;

                // Placement "position" on the sphere is encoded as a unit direction from the planet pivot
                // (same convention as SampleSurfaceRadius / SpawnVegetationFromPlacement). Refresh from the
                // live transform so editor moves persist.
                var surfaceDir = e.SurfaceDir;
                float yawDeg = e.YawDeg;
                float scaleOut = Math.Max(0.01f, e.BaseScale);
                if (go != null)
                {
                    float dx = (float)go.Transform.Position.X - center.X;
                    float dy = (float)go.Transform.Position.Y - center.Y;
                    float dz = (float)go.Transform.Position.Z - center.Z;
                    surfaceDir = SafeNormalize(new SN.Vector3(dx, dy, dz), surfaceDir);
                    yawDeg = (float)go.Transform.Rotation.Y;
                    var s = go.Transform.Scale;
                    scaleOut = Math.Max(0.01f, (float)Math.Max(Math.Max(s.X, s.Y), s.Z));
                }

                // Imported FBX/OBJ trees usually store ModelPath on child MeshFilters only; resolve the hierarchy.
                string modelPath = ResolveVegetationModelPathForAsset(go, e.IsGrass);
                if (string.IsNullOrWhiteSpace(modelPath))
                    modelPath = e.ModelPath ?? "";

                string texturePath = e.IsGrass ? ResolveVegetationTexturePathForAsset(go) : (e.TexturePath ?? "");
                if (string.IsNullOrWhiteSpace(texturePath))
                    texturePath = e.TexturePath ?? "";

                string prefabPath = go != null && !string.IsNullOrWhiteSpace(go.PrefabPath)
                    ? go.PrefabPath!
                    : e.PrefabPath ?? "";

                placements.Add(new PlanetVegetationPlacement
                {
                    IsGrass = e.IsGrass,
                    BiomeName = e.BiomeName ?? "",
                    PrefabPath = PlanetAssetIO.NormalizeAssetReference(prefabPath),
                    ModelPath = PlanetAssetIO.NormalizeAssetReference(modelPath),
                    TexturePath = PlanetAssetIO.NormalizeAssetReference(texturePath),
                    DirX = surfaceDir.X,
                    DirY = surfaceDir.Y,
                    DirZ = surfaceDir.Z,
                    Scale = scaleOut,
                    YawDeg = yawDeg
                });
            }
        }
        if (placements.Count == 0 && _assetPlacements.Count > 0)
            placements.AddRange(_assetPlacements);
        if (UsePlanetAssetPlacements && _assetPlacements.Count > placements.Count)
        {
            placements.Clear();
            placements.AddRange(_assetPlacements);
        }
        return new PlanetVegetationAssetData
        {
            UseStoredPlacements = UsePlanetAssetPlacements,
            Placements = placements.ToArray()
        };
    }

    public void ImportAssetData(PlanetVegetationAssetData? data)
    {
        _assetPlacements.Clear();
        if (data?.Placements != null)
            _assetPlacements.AddRange(data.Placements.Where(p => p != null));
        for (int i = 0; i < _assetPlacements.Count; i++)
        {
            var p = _assetPlacements[i];
            p.PrefabPath = PlanetAssetIO.NormalizeAssetReference(p.PrefabPath);
            p.ModelPath = PlanetAssetIO.NormalizeAssetReference(p.ModelPath);
            p.TexturePath = PlanetAssetIO.NormalizeAssetReference(p.TexturePath);
        }
        _assetSpawnCursor = 0;
        _prunedWetPlacements = false;
        RebuildAssetPlacementAccel();
        QueuePlacementTemplateLoads();
        bool hasPlacements = _assetPlacements.Count > 0;
        UsePlanetAssetPlacements = AutoUseSavedPlacementsWhenPresent && hasPlacements;
        if (AutoSpawnWhenUsingSavedPlacements && UsePlanetAssetPlacements && hasPlacements)
            AutoSpawn = true;

        EnsureInitialized();
        // Drop disk snapshot only once memory matches an intentional import result.
        if (_assetPlacements.Count > 0)
            _terrain?.ReleaseVegetationDiskSnapshotAfterImport();
        else if (data != null && data.Placements != null && data.Placements.Length == 0)
            _terrain?.ReleaseVegetationDiskSnapshotAfterImport();

        if (AutoSpawn && UsePlanetAssetPlacements && _assetPlacements.Count > 0)
            WarmSpawnAfterDeferredImport();
    }

    void TryPullDeferredPlanetVegetation()
    {
        _terrain ??= GetComponent<PlanetTerrain>();
        _terrain?.TryApplyPendingOrDeferredVegetation(this);
    }

    /// <summary>
    /// Manual editor/runtime trigger to populate vegetation immediately.
    /// Useful for Scene View workflows when AutoSpawn is off.
    /// </summary>
    public void SpawnNow(bool clearExisting = false)
    {
        if (_terrain == null)
            _terrain = GetComponent<PlanetTerrain>();
        if (_terrain == null || _terrain.ChunkManager == null || _terrain.gameObject == null || _terrain.Config == null)
            return;

        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();

        if (clearExisting)
        {
            foreach (var group in _leafEntries.Values)
                for (int i = 0; i < group.Count; i++)
                    group[i].GameObject.RemoveFromParent();
            _leafEntries.Clear();
            _assetSpawnCursor = 0;
            ClearLocalCarpetTrees();
            _carpetDirs.Clear();
            PlanetGpuGrass.ClearOwner(this);
            _assetActive.Clear();
            _assetPlacements.Clear();
            _prunedWetPlacements = false;
            RebuildAssetPlacementAccel();
            LastDespawnedThisUpdate = 0;
        }

        bool spawned = false;
        _manualSpawnPass = true;
        try
        {
            if (UsePlanetAssetPlacements && _assetPlacements.Count > 0 && !clearExisting)
            {
                PruneSubmergedAssetPlacements();
                int? warmBudget = _manualSpawnPass
                    ? Math.Clamp(Math.Max(MaxActiveAssetGrassPatches, 64), 64, 128)
                    : null;
                SpawnFromAssetPlacements(clearExisting: false, warmBudget);
            }
            else
            {
                if (UsePlanetAssetPlacements)
                {
                    _assetPlacements.Clear();
                    _assetSpawnCursor = 0;
                    RebuildAssetPlacementAccel();
                }
                PopulateStoredPlacementsFromGrid();
                RefreshVegetation();
            }
            spawned = true;
        }
        finally
        {
            _manualSpawnPass = false;
        }
        if (spawned && (ActiveVegetationInstances > 0 || (UsePlanetAssetPlacements && _assetPlacements.Count > 0)))
        {
            _terrain?.SavePlanetAsset();
        }
        else if (spawned)
        {
            Log.Info("[PlanetVegetationSystem] Spawn completed with 0 instances; skipping .planet vegetation overwrite.");
        }
    }

    void RefreshVegetation(double playTimeBudgetMs = 0.0)
    {
        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;
        long budgetTicks = playTimeBudgetMs > 0.0
            ? (long)(playTimeBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0)
            : 0;
        long budgetStart = budgetTicks > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        bool OverBudget()
            => budgetTicks > 0
               && (System.Diagnostics.Stopwatch.GetTimestamp() - budgetStart) >= budgetTicks;

        var cfg = _terrain!.Config!;
        var leaves = _terrain.ChunkManager!.GetRenderableLeaves();
        if (leaves.Count == 0 && _manualSpawnPass)
        {
            // Manual spawn should still work before chunk meshes finish generating.
            for (int f = 0; f < _terrain.ChunkManager.Faces.Length; f++)
                leaves.AddRange(_terrain.ChunkManager.Faces[f].GetLeafNodes());
        }
        if (leaves.Count == 0) return;

        var planetW = GetPlanetWorldMatrix();
        var center = GetWorldCenter();
        var camPos = ResolveCameraPosition();
        float worldRadius = Math.Max(1f, cfg.EffectiveWorldRadius);
        float spawnDist = PlayPerfLimited
            ? Math.Max(320f, worldRadius * 0.35f)
            : worldRadius * Math.Max(0.5f, ActiveDistanceMultiplier);
        float spawnDistSq = spawnDist * spawnDist;
        float cullDistSq = spawnDistSq * VegetationCullDistanceHysteresis * VegetationCullDistanceHysteresis;

        leaves.Sort((a, b) =>
        {
            var aw = LocalSpherePointToWorld(planetW, a.WorldCentre(worldRadius));
            var bw = LocalSpherePointToWorld(planetW, b.WorldCentre(worldRadius));
            return SN.Vector3.DistanceSquared(camPos, aw).CompareTo(SN.Vector3.DistanceSquared(camPos, bw));
        });

        bool fullPopulate = _manualSpawnPass && FullBiomePopulate;
        int spawnTickCap = ResolveSpawnTickCap(fullPopulate);

        // Map each terrain leaf to a stable face/UV cell (not LOD-specific) so split/merge does not despawn plants.
        var cellBestLeaf = new Dictionary<string, (QuadNode leaf, int lod)>(StringComparer.Ordinal);
        var cellDistSq = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            var leafCenter = LocalSpherePointToWorld(planetW, leaf.WorldCentre(worldRadius));
            float d2 = SN.Vector3.DistanceSquared(camPos, leafCenter);
            if (!fullPopulate && d2 > spawnDistSq)
                continue;

            string cellKey = BuildStableVegetationCellKey(leaf);
            if (!cellBestLeaf.TryGetValue(cellKey, out var prev) || leaf.LodLevel > prev.lod)
                cellBestLeaf[cellKey] = (leaf, leaf.LodLevel);
            if (!cellDistSq.TryGetValue(cellKey, out var bestD2) || d2 < bestD2)
                cellDistSq[cellKey] = d2;
        }

        int maxCells = fullPopulate
            ? cellBestLeaf.Count
            : Math.Max(64, MaxTrackedLeaves);
        var activeCells = cellBestLeaf.Keys
            .OrderBy(k => cellDistSq.TryGetValue(k, out var d) ? d : float.MaxValue)
            .Take(maxCells)
            .ToList();

        for (int i = 0; i < activeCells.Count; i++)
        {
            if (OverBudget()) break;
            if (!fullPopulate && LastSpawnedThisUpdate >= spawnTickCap)
                break;

            string cellKey = activeCells[i];
            if (!cellBestLeaf.TryGetValue(cellKey, out var picked))
                continue;
            EnsureLeafEntries(picked.leaf, cellKey);
            // Always drive vitality/regrowth from authored VegetationRegrowthRate/DecayRate
            // (weather wetness/snow modulate harshness via ApplyWeather).
            UpdateLeafVitality(cellKey);
            if (!fullPopulate && LastSpawnedThisUpdate >= spawnTickCap)
                break;
        }

        if (CullVegetationWhenLeafNotActive)
        {
            _staleLeafKeys.Clear();
            foreach (var k in _leafEntries.Keys)
            {
                if (string.Equals(k, "asset", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryParseStableVegetationCellKey(k, out int face, out int iu, out int iv))
                {
                    var dir = StableCellCenterDirection(face, iu, iv);
                    var wp = LocalSpherePointToWorld(planetW, dir * worldRadius);
                    if (SN.Vector3.DistanceSquared(camPos, wp) <= cullDistSq)
                        continue;
                }
                _staleLeafKeys.Add(k);
            }
            for (int i = 0; i < _staleLeafKeys.Count && LastDespawnedThisUpdate < ResolveDespawnCap(); i++)
                DespawnLeaf(_staleLeafKeys[i]);
        }
    }

    int ResolveSpawnTickCap(bool fullPopulate)
    {
        if (fullPopulate || _manualSpawnPass)
            return Math.Max(16, _terrain?.Config?.MaxVegetationSpawnsPerUpdate ?? 16);
        int configCap = Math.Max(1, _terrain?.Config?.MaxVegetationSpawnsPerUpdate ?? 4);
        if (PlayPerfLimited)
        {
            if (VegetationStressTest)
                return configCap;
            return Math.Clamp(configCap, 8, 24);
        }
        if (VegetationStressTest)
            return configCap;
        return Math.Min(6, configCap);
    }

    int ResolveDespawnCap()
        => PlayPerfLimited ? PlayMaxDespawnsPerRefresh : MaxDespawnsPerRefresh;

    int ResolveActiveTreeCap()
    {
        int configured = Math.Max(8, MaxActiveAssetTrees);
        if (!PlayPerfLimited)
            return configured;
        return Math.Clamp(configured, 24, 96);
    }

    int ResolveActiveGrassCap()
    {
        int configured = Math.Max(4, MaxActiveAssetGrassPatches);
        if (!PlayPerfLimited)
            return Math.Min(configured, 1024);
        return Math.Clamp(configured, 96, 280);
    }

    float ResolveAssetActivationDistance(float worldRadius)
    {
        // Chord along the crust from the camera's surface projection.
        // Play used to clamp this to 180–340 m, which missed every stored
        // Earth placement (nearest is ~540 m from the default spawn).
        float r = Math.Max(1f, worldRadius);
        float configured = r * Math.Max(0.25f, AssetPlacementActivationDistanceMultiplier);
        return Math.Min(configured, r * 0.65f);
    }

    void SpawnFromAssetPlacements(bool clearExisting, int? budgetOverride = null, SN.Vector3? cameraOverride = null)
    {
        if (_terrain?.Config == null) return;
        if (clearExisting)
        {
            foreach (var group in _leafEntries.Values)
                for (int i = 0; i < group.Count; i++)
                    group[i].GameObject.RemoveFromParent();
            _leafEntries.Clear();
            _assetSpawnCursor = 0;
            ClearLocalCarpetTrees();
            _carpetDirs.Clear();
            PlanetGpuGrass.ClearOwner(this);
            _assetActive.Clear();
        }

        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;
        int budget = _manualSpawnPass
            ? Math.Clamp(budgetOverride ?? 16, 8, 32)
            : Math.Clamp(budgetOverride ?? 16, 8, 20);
        const string key = "asset";
        if (!_leafEntries.TryGetValue(key, out var entries))
        {
            entries = new List<Entry>();
            _leafEntries[key] = entries;
        }

        float worldRadius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        float maxDist = ResolveAssetActivationDistance(worldRadius);
        float maxDistSq = maxDist * maxDist;
        var camPos = cameraOverride ?? ResolveCameraPosition();
        int grassCapWanted = ResolveActiveGrassCap();
        int treeCapWanted = ResolveActiveTreeCap();

        if (!TryUseCachedNearest(camPos, grassCapWanted, treeCapWanted))
        {
            CollectNearestPlacementIndices(
                isGrass: true,
                maxCount: grassCapWanted,
                maxDistSq: maxDistSq,
                cameraPos: camPos,
                _nearestGrass);
            CollectNearestPlacementIndices(
                isGrass: false,
                maxCount: treeCapWanted,
                maxDistSq: maxDistSq,
                cameraPos: camPos,
                _nearestTrees);
            CacheNearest(camPos, grassCapWanted, treeCapWanted);
        }

        int texN = Math.Min(_nearestGrass.Count, 12);
        for (int i = 0; i < texN; i++)
        {
            int gi = _nearestGrass[i];
            if ((uint)gi >= (uint)_assetPlacements.Count) continue;
            PlanetGrassTextureCache.Request(ResolveGrassTexturePath(_assetPlacements[gi]));
        }

        _wantedAssetSet.Clear();
        for (int i = 0; i < _nearestGrass.Count; i++)
            _wantedAssetSet.Add(_nearestGrass[i]);
        for (int i = 0; i < _nearestTrees.Count; i++)
            _wantedAssetSet.Add(_nearestTrees[i]);

        _staleAssetIndices.Clear();
        foreach (var kv in _assetActive)
            if (!_wantedAssetSet.Contains(kv.Key))
                _staleAssetIndices.Add(kv.Key);
        int despawnCap = ResolveDespawnCap();
        for (int i = 0; i < _staleAssetIndices.Count && LastDespawnedThisUpdate < despawnCap; i++)
        {
            int idx = _staleAssetIndices[i];
            if (_assetActive.TryGetValue(idx, out var old))
            {
                if (old.IsGrass)
                    PlanetGpuGrass.RemovePatch(this, idx);
                else
                    old.GameObject?.RemoveFromParent();
                _assetActive.Remove(idx);
                LastDespawnedThisUpdate++;
            }
        }

        _assetSpawnOrder.Clear();
        _assetSpawnOrder.AddRange(_nearestTrees);
        _assetSpawnOrder.AddRange(_nearestGrass);

        int grassCap = grassCapWanted;
        int treeCap = treeCapWanted;
        int activeGrass = 0;
        int activeTrees = 0;
        foreach (var kv in _assetActive)
        {
            if (kv.Value.IsGrass) activeGrass++;
            else activeTrees++;
        }

        int attempts = 0;
        int attemptCap = budget * 3 + 8;
        int treeSpawnsThisTick = 0;
        long spawnStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long spawnBudgetTicks = (long)(4.0 * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        foreach (var idx in _assetSpawnOrder)
        {
            if (budget <= 0 || attempts >= attemptCap) break;
            if (System.Diagnostics.Stopwatch.GetTimestamp() - spawnStart >= spawnBudgetTicks)
                break;
            if (_assetActive.ContainsKey(idx)) continue;
            if (idx < 0 || idx >= _assetPlacements.Count) continue;
            attempts++;
            var p = _assetPlacements[idx];
            _deferSpawnForTemplate = false;
            if (p.IsGrass)
            {
                if (activeGrass >= grassCap) continue;
                if (!TryRegisterCheapGpuGrass(p, idx))
                    continue;
                activeGrass++;
                LastSpawnedThisUpdate++;
                budget--;
                continue;
            }
            if (activeTrees >= treeCap)
                continue;
            if (PlayPerfLimited && treeSpawnsThisTick >= 8)
                continue;
            var go = SpawnVegetationFromPlacement(p, idx);
            if (_deferSpawnForTemplate)
                continue;
            if (go == null || go.Parent == null) continue;
            var entry = new Entry
            {
                GameObject = go,
                Biome = ResolveBiomeByName(p.BiomeName) ?? _terrain.OceanBiome,
                BiomeName = p.BiomeName ?? "",
                IsGrass = false,
                BaseScale = Math.Max(0.01f, p.Scale),
                Vitality = 1f,
                SurfaceDir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY),
                YawDeg = p.YawDeg,
                PrefabPath = p.PrefabPath ?? "",
                ModelPath = p.ModelPath ?? "",
                TexturePath = p.TexturePath ?? "",
            };
            _assetActive[idx] = entry;
            activeTrees++;
            treeSpawnsThisTick++;
            LastSpawnedThisUpdate++;
            budget--;
        }

        entries.Clear();
        entries.AddRange(_assetActive.Values);
    }

    /// <summary>
    /// After async <see cref="PlanetVegetationSceneLoader"/> import, applies a one-shot spawn budget so
    /// grass (which was easy to starve with <see cref="MaxAssetSpawnsPerUpdate"/>) appears with trees.
    /// </summary>
    public void WarmSpawnAfterDeferredImport()
    {
        if (_terrain == null)
            _terrain = GetComponent<PlanetTerrain>();
        if (!UsePlanetAssetPlacements || _assetPlacements.Count == 0 || _terrain?.Config == null)
            return;
        EnsureInitialized();
        SpawnFromAssetPlacements(clearExisting: false, budgetOverride: 8);
    }

    void RebuildAssetPlacementAccel()
    {
        _assetDirs.Clear();
        _grassPlacementIdx.Clear();
        _treePlacementIdx.Clear();
        _grassBuckets.Clear();
        _treeBuckets.Clear();
        if (_assetPlacements.Count > _assetDirs.Capacity)
            _assetDirs.Capacity = _assetPlacements.Count;
        for (int i = 0; i < _assetPlacements.Count; i++)
            RegisterAssetPlacementAt(i);
        _cachedNearestCamDir = new SN.Vector3(float.NaN, float.NaN, float.NaN);
    }

    void RegisterAssetPlacementAt(int i)
    {
        var p = _assetPlacements[i];
        var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
        if (i < _assetDirs.Count)
            _assetDirs[i] = dir;
        else
            _assetDirs.Add(dir);
        int bucket = PackDirBucket(dir);
        if (p.IsGrass)
        {
            _grassPlacementIdx.Add(i);
            AddToBucket(_grassBuckets, bucket, i);
        }
        else if (PlacementHasUsableTreeAsset(p, i))
        {
            _treePlacementIdx.Add(i);
            AddToBucket(_treeBuckets, bucket, i);
        }
    }

    static void AddToBucket(Dictionary<int, List<int>> buckets, int key, int index)
    {
        if (!buckets.TryGetValue(key, out var list))
        {
            list = new List<int>(8);
            buckets[key] = list;
        }
        list.Add(index);
    }

    const int DirBucketRes = 36;

    static int PackDirBucket(SN.Vector3 dir)
    {
        int x = Math.Clamp((int)((dir.X + 1f) * 0.5f * DirBucketRes), 0, DirBucketRes - 1);
        int y = Math.Clamp((int)((dir.Y + 1f) * 0.5f * DirBucketRes), 0, DirBucketRes - 1);
        int z = Math.Clamp((int)((dir.Z + 1f) * 0.5f * DirBucketRes), 0, DirBucketRes - 1);
        return (x * DirBucketRes + y) * DirBucketRes + z;
    }

    static void UnpackDirBucket(int key, out int x, out int y, out int z)
    {
        z = key % DirBucketRes;
        key /= DirBucketRes;
        y = key % DirBucketRes;
        x = key / DirBucketRes;
    }

    void DropProceduralLeafGroups()
    {
        if (_leafEntries.Count <= 1)
            return;
        _staleLeafKeys.Clear();
        foreach (var k in _leafEntries.Keys)
        {
            if (!string.Equals(k, "asset", StringComparison.Ordinal))
                _staleLeafKeys.Add(k);
        }
        for (int i = 0; i < _staleLeafKeys.Count; i++)
            DespawnLeaf(_staleLeafKeys[i]);
    }

    bool TryUseCachedNearest(SN.Vector3 cameraPos, int grassCap, int treeCap)
    {
        if (grassCap != _cachedNearestGrassCap || treeCap != _cachedNearestTreeCap)
            return false;
        if (_terrain == null || float.IsNaN(_cachedNearestCamDir.X))
            return false;
        var camLocal = _terrain.WorldToLocal(cameraPos);
        var camDir = camLocal.LengthSquared() < 1e-8f ? SN.Vector3.UnitY : SN.Vector3.Normalize(camLocal);
        return SN.Vector3.Dot(camDir, _cachedNearestCamDir) >= 0.997f;
    }

    void CacheNearest(SN.Vector3 cameraPos, int grassCap, int treeCap)
    {
        if (_terrain == null) return;
        var camLocal = _terrain.WorldToLocal(cameraPos);
        _cachedNearestCamDir = camLocal.LengthSquared() < 1e-8f ? SN.Vector3.UnitY : SN.Vector3.Normalize(camLocal);
        _cachedNearestGrassCap = grassCap;
        _cachedNearestTreeCap = treeCap;
    }

    bool TryRegisterCheapGpuGrass(PlanetVegetationPlacement p, int token)
    {
        if (_terrain?.Config == null) return false;
        var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
        var center = ResolveGrassSeatLocal(dir);
        float localH = ResolveGpuGrassHeight(p.Scale);
        float localPatch = Math.Max(0.55f, ResolveGpuGrassHeight(1f) * 0.45f);
        int blades = Math.Clamp(GrassBladesPerPatch, 8, 12);
        string tex = ResolveGrassTexturePath(p);
        int placed = PlanetGpuGrass.RegisterPatch(this, token, center, dir, localH, p.YawDeg, localPatch, blades, tex);
        if (placed <= 0) return false;

        _assetActive[token] = new Entry
        {
            GameObject = EnsureGpuGrassMarker(),
            Biome = ResolveBiomeByName(p.BiomeName) ?? _terrain.OceanBiome,
            BiomeName = p.BiomeName ?? "",
            IsGrass = true,
            BaseScale = Math.Max(0.01f, p.Scale),
            Vitality = 1f,
            SurfaceDir = dir,
            YawDeg = p.YawDeg,
            PrefabPath = "",
            ModelPath = p.ModelPath ?? "",
            TexturePath = p.TexturePath ?? "",
            GpuGrassToken = token,
        };
        return true;
    }

    GameObject EnsureGpuGrassMarker()
    {
        if (_gpuGrassMarker != null && _gpuGrassMarker.Parent != null)
            return _gpuGrassMarker;
        _gpuGrassMarker = new GameObject("GpuGrassBatch");
        AttachSpawnedInstance(_gpuGrassMarker);
        return _gpuGrassMarker;
    }

    SN.Vector3 ResolveGrassSeatLocal(SN.Vector3 dir)
    {
        if (TryPrepareCarpetClump(dir, out var center, out _))
            return center;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);
        var local = _terrain!.SampleRenderedCrustLocal(dir);
        if (local.LengthSquared() < 1f)
            local = _terrain.SampleLocalCrustPoint(dir);
        if (local.LengthSquared() < 1f)
            local = dir * Math.Max(1f, _terrain.Config?.EffectiveWorldRadius ?? 1000f);
        float embed = _terrain.WorldToLocalLength(0.04f);
        return local - dir * embed;
    }

    /// <summary>
    /// Seats a GPU clump on the visible carved crust and rejects coastal cliff walls.
    /// Heightfield-only seats float in an arc over shore cuts; radial blades then
    /// stick out into the valley.
    /// </summary>
    bool TryPrepareCarpetClump(SN.Vector3 dir, out SN.Vector3 center, out SN.Vector3 upLocal)
    {
        center = default;
        upLocal = SN.Vector3.UnitY;
        if (_terrain?.Config == null)
            return false;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);

        float crust = _terrain.SampleLocalCrustRadius(dir);
        if (crust < 1f)
            return false;

        if (_terrain.EnableWater)
        {
            var water = _terrain.SampleWaterSurface(dir);
            if (water.Mask >= 0.22f && water.Kind != PlanetWaterKind.Lava && crust < water.Radius - 0.35f)
                return false;
            if (crust < _terrain.Config.SeaLevel - 0.25f)
                return false;
        }

        var analytical = dir * crust;
        var rendered = _terrain.SampleRenderedCrustLocal(dir);
        float rendR = rendered.Length();
        var seat = analytical;
        if (rendR > 1f)
        {
            if (rendR > crust + 8f)
                seat = analytical;
            else
                seat = rendered;
        }

        float seatR = seat.Length();
        if (_terrain.EnableWater)
        {
            var water = _terrain.SampleWaterSurface(dir);
            if (water.Mask >= 0.18f && water.Kind != PlanetWaterKind.Lava && seatR < water.Radius - 0.25f)
                return false;
        }

        CarpetTangentBasis(dir, out var t, out var b);
        float eps = Math.Max(2.4f, crust * 0.0024f);
        float step = eps / Math.Max(crust, 1f);
        float rT = _terrain.SampleLocalCrustRadius(SafeNormalize(dir + t * step, dir));
        float rB = _terrain.SampleLocalCrustRadius(SafeNormalize(dir + b * step, dir));
        var p0 = dir * seatR;
        var pT = SafeNormalize(dir + t * step, dir) * rT;
        var pB = SafeNormalize(dir + b * step, dir) * rB;
        var n = SN.Vector3.Cross(pT - p0, pB - p0);
        if (n.LengthSquared() < 1e-10f)
            n = dir;
        else
            n = SN.Vector3.Normalize(n);
        if (SN.Vector3.Dot(n, dir) < 0f)
            n = -n;

        float align = SN.Vector3.Dot(n, dir);
        if (align < 0.56f)
            return false;

        float embed = _terrain.WorldToLocalLength(0.05f);
        center = seat - dir * embed;
        float follow = Math.Clamp(0.58f + (1f - align) * 0.75f, 0.58f, 0.96f);
        upLocal = SafeNormalize(SN.Vector3.Lerp(dir, n, follow), dir);
        return true;
    }

    float ResolveGpuGrassHeight(float scale)
    {
        // First-person blades. Do not use the old radius*0.0045 path (4–14 m) or the
        // 0.5 m clamp that read as specks after PSD padding.
        float worldH = Math.Clamp(Math.Max(GrassBaseHeight, 4.5f), 4.5f, 7.2f) * Math.Max(0.9f, scale);
        float local = _terrain!.WorldToLocalLength(worldH);
        float minLocal = Math.Max(4.2f, (_terrain.Config?.EffectiveWorldRadius ?? 1000f) * 0.0048f);
        return Math.Max(local, minLocal);
    }

    string ResolveGrassTexturePath(PlanetVegetationPlacement? p)
    {
        if (p != null)
        {
            if (IsImageAssetPath(p.TexturePath))
                return PlanetAssetIO.NormalizeAssetReference(p.TexturePath);
            if (IsImageAssetPath(p.ModelPath))
                return PlanetAssetIO.NormalizeAssetReference(p.ModelPath);
        }
        return PlanetGrassTextureCache.Pick(p == null ? _carpetSeq : HashPlacement(p));
    }

    static int HashPlacement(PlanetVegetationPlacement p)
        => HashCode.Combine(p.DirX, p.DirY, p.DirZ, p.TexturePath ?? "", p.ModelPath ?? "");

    static int PackCarpetDirToken(SN.Vector3 dir)
    {
        var (face, u, v) = CubeSphereMath.SphereToCube(dir);
        int iu = Math.Clamp((int)(u * 2048f), 0, 2047);
        int iv = Math.Clamp((int)(v * 2048f), 0, 2047);
        return unchecked((int)0xA0000000) | ((face & 7) << 22) | (iu << 11) | iv;
    }

    void TickLocalGpuGrassCarpet(SN.Vector3 cameraPos)
    {
        if (_terrain?.Config == null) return;
        PlanetGrassTextureCache.EnsureCarpetMix();
        if (_carpetDirs.Count == 0 && PlanetGrassTextureCache.ReadyMixCount() < 10)
            return;

        var camLocal = _terrain.WorldToLocal(cameraPos);
        if (camLocal.LengthSquared() < 1e-6f) return;
        var camDir = SN.Vector3.Normalize(camLocal);
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        float outer = Math.Clamp(radius * 0.30f, 260f, 340f);
        var prevDir = float.IsNaN(_carpetCamDir.X) ? camDir : _carpetCamDir;
        var step = camDir - prevDir;
        float stepM = step.Length() * radius;
        bool moving = stepM > 1.6f;
        var fillDir = camDir;
        if (moving)
            fillDir = SafeNormalize(camDir + step * (160f / Math.Max(stepM, 3f)), camDir);
        _carpetCamDir = camDir;

        float keep = moving ? outer * 1.12f : outer * 1.40f;
        float maxDirDeltaSq = (keep / radius) * (keep / radius);

        _carpetStale.Clear();
        foreach (var kv in _carpetDirs)
        {
            if ((camDir - kv.Value).LengthSquared() > maxDirDeltaSq)
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
        {
            int token = _carpetStale[i];
            PlanetGpuGrass.RemovePatch(this, token);
            _carpetDirs.Remove(token);
        }

        int cap = PlayPerfLimited ? 7200 : 8800;
        int perTick = moving
            ? (PlayPerfLimited ? 1800 : 2000)
            : (PlayPerfLimited ? 720 : 900);
        if (moving)
            RecycleRearCarpet(_carpetDirs, camDir, step, radius, cap, perTick, minKeepM: 28f, removeGrass: true);

        int slack = cap - _carpetDirs.Count;
        if (slack <= 0) return;
        int leftover = Math.Min(slack, perTick);

        CarpetTangentBasis(camDir, out var tCam, out var bCam);
        CarpetTangentBasis(fillDir, out var tFill, out var bFill);

        float h = ResolveGpuGrassHeight(1f);
        float patchR = Math.Max(1.7f, h * 0.38f);

        if (moving)
        {
            // Plant the chunk you are walking into first; keep feet as a second pass.
            int ahead = 0;
            FillCarpetAheadScatter(fillDir, tFill, bFill, radius, 24f, outer, h, 12, patchR * 1.1f, leftover * 3 / 4, ref ahead);
            leftover = Math.Max(0, leftover - ahead);
            int feet = 0;
            FillCarpetDisk(camDir, tCam, bCam, radius, spacing: 3.2f, rMin: 0.6f, rMax: 70f, h, 12, patchR, leftover, ref feet);
            return;
        }

        int nearAdded = 0;
        FillCarpetDisk(camDir, tCam, bCam, radius, spacing: 3.2f, rMin: 0.6f, rMax: 95f, h, 12, patchR, Math.Max(160, leftover / 3), ref nearAdded);
        leftover = Math.Max(0, leftover - nearAdded);

        int midAdded = 0;
        FillCarpetDisk(camDir, tCam, bCam, radius, spacing: 3.7f, rMin: 88f, rMax: 185f, h, 12, patchR * 1.08f, Math.Max(160, leftover / 2), ref midAdded);
        leftover = Math.Max(0, leftover - midAdded);

        int farAdded = 0;
        FillCarpetDisk(camDir, tCam, bCam, radius, spacing: 4.3f, rMin: 178f, rMax: outer, h, 12, patchR * 1.16f, leftover, ref farAdded);
    }

    static void CarpetTangentBasis(SN.Vector3 dir, out SN.Vector3 t, out SN.Vector3 b)
    {
        t = SN.Vector3.Cross(MathF.Abs(dir.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, dir);
        if (t.LengthSquared() < 1e-8f) t = SN.Vector3.UnitX;
        t = SN.Vector3.Normalize(t);
        b = SN.Vector3.Normalize(SN.Vector3.Cross(dir, t));
    }

    void FillCarpetDisk(
        SN.Vector3 camDir,
        SN.Vector3 t,
        SN.Vector3 b,
        float radius,
        float spacing,
        float rMin,
        float rMax,
        float height,
        int blades,
        float patchR,
        int want,
        ref int added)
    {
        if (want <= 0)
            return;
        // Hex short axis is spacing*0.866; overshoot rings so the disk reaches rMax.
        float step = Math.Max(1.2f, spacing) * 0.75f;
        int n = Math.Clamp((int)MathF.Ceiling(rMax / step), 2, 160);
        for (int ring = 0; ring <= n && added < want; ring++)
        {
            for (int iy = -ring; iy <= ring && added < want; iy++)
            {
                for (int ix = -ring; ix <= ring && added < want; ix++)
                {
                    if (Math.Max(Math.Abs(ix), Math.Abs(iy)) != ring)
                        continue;
                    float jx = (Fract(ix * 0.1731f + iy * 0.4197f) - 0.5f) * 0.55f;
                    float jy = (Fract(ix * 0.3911f + iy * 0.2333f) - 0.5f) * 0.55f;
                    float px = (ix + ((iy & 1) * 0.5f) + jx) * spacing;
                    float py = (iy * 0.8660254f + jy) * spacing;
                    float d2 = px * px + py * py;
                    if (d2 < rMin * rMin || d2 > rMax * rMax)
                        continue;

                    var dir = SafeNormalize(camDir * radius + t * px + b * py, camDir);
                    if (!TryPrepareCarpetClump(dir, out var center, out var upLocal))
                        continue;

                    int token = PackCarpetDirToken(dir);
                    if (_carpetDirs.ContainsKey(token) || PlanetGpuGrass.HasPatch(this, token))
                        continue;

                    float yaw = Fract(ix * 0.618034f + iy * 0.754877f) * 360f;
                    string? tex = PlanetGrassTextureCache.TryPickReady(token);
                    if (string.IsNullOrWhiteSpace(tex))
                        continue;
                    if (PlanetGpuGrass.RegisterPatch(this, token, center, upLocal, height, yaw, patchR, blades, tex) <= 0)
                        continue;
                    _carpetDirs[token] = dir;
                    added++;
                }
            }
        }
    }

    void FillCarpetAheadScatter(
        SN.Vector3 fillDir,
        SN.Vector3 t,
        SN.Vector3 b,
        float radius,
        float rMin,
        float rMax,
        float height,
        int blades,
        float patchR,
        int want,
        ref int added)
    {
        if (want <= 0) return;
        int tries = want * 12;
        for (int i = 0; i < tries && added < want; i++)
        {
            _carpetSeq++;
            float u = Fract(_carpetSeq * 0.754877666f);
            float v = Fract(_carpetSeq * 0.56984029f);
            float r = MathF.Sqrt(rMin * rMin + u * (rMax * rMax - rMin * rMin));
            float ang = v * MathF.Tau;
            float px = MathF.Cos(ang) * r;
            float py = MathF.Sin(ang) * r;
            var dir = SafeNormalize(fillDir * radius + t * px + b * py, fillDir);
            if (!TryPrepareCarpetClump(dir, out var center, out var upLocal))
                continue;
            int token = PackCarpetDirToken(dir);
            if (_carpetDirs.ContainsKey(token) || PlanetGpuGrass.HasPatch(this, token))
                continue;
            float yaw = Fract(_carpetSeq * 0.618034f) * 360f;
            string? tex = PlanetGrassTextureCache.TryPickReady(token);
            if (string.IsNullOrWhiteSpace(tex))
                continue;
            if (PlanetGpuGrass.RegisterPatch(this, token, center, upLocal, height, yaw, patchR, blades, tex) <= 0)
                continue;
            _carpetDirs[token] = dir;
            added++;
        }
    }

    void RecycleRearCarpet(
        Dictionary<int, SN.Vector3> dirs,
        SN.Vector3 camDir,
        SN.Vector3 step,
        float radius,
        int cap,
        int perTick,
        float minKeepM,
        bool removeGrass)
    {
        if (dirs.Count == 0 || step.LengthSquared() < 1e-12f)
            return;
        int need = perTick - (cap - dirs.Count);
        if (need <= 8) return;
        var move = SN.Vector3.Normalize(step);
        _carpetStale.Clear();
        foreach (var kv in dirs)
        {
            var delta = kv.Value - camDir;
            if (delta.Length() * radius < minKeepM)
                continue;
            if (SN.Vector3.Dot(delta, move) > -0.012f)
                continue;
            _carpetStale.Add(kv.Key);
        }
        int removed = 0;
        for (int i = 0; i < _carpetStale.Count && removed < need; i++)
        {
            int token = _carpetStale[i];
            if (removeGrass)
                PlanetGpuGrass.RemovePatch(this, token);
            dirs.Remove(token);
            removed++;
        }
    }

    void TickLocalTrees(SN.Vector3 cameraPos)
    {
        if (_terrain?.Config == null) return;
        EnsureLocalTreeModels();
        if (!WaitForProfileTreeFbxs())
            return;

        var camLocal = _terrain.WorldToLocal(cameraPos);
        if (camLocal.LengthSquared() < 1e-6f) return;
        var camDir = SN.Vector3.Normalize(camLocal);
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        float outer = Math.Clamp(radius * 0.22f, 160f, 240f);
        var prevDir = float.IsNaN(_treeCamDir.X) ? camDir : _treeCamDir;
        var step = camDir - prevDir;
        float stepM = step.Length() * radius;
        bool moving = stepM > 1.6f;
        var fillDir = moving
            ? SafeNormalize(camDir + step * (140f / Math.Max(stepM, 3f)), camDir)
            : camDir;
        _treeCamDir = camDir;

        float keep = moving ? outer * 1.15f : outer * 1.45f;
        float maxDirDeltaSq = (keep / radius) * (keep / radius);
        _carpetStale.Clear();
        foreach (var kv in _carpetTreeDirs)
        {
            if ((camDir - kv.Value).LengthSquared() > maxDirDeltaSq)
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveLocalCarpetTree(_carpetStale[i]);

        int cap = PlayPerfLimited ? 56 : 80;
        int perTick = moving ? 8 : 5;
        if (moving)
            RecycleRearTrees(camDir, step, radius, cap, perTick, minKeepM: 40f);

        int slack = cap - _carpetTrees.Count;
        if (slack <= 0) return;
        int want = Math.Min(slack, perTick);

        CarpetTangentBasis(fillDir, out var t, out var b);
        float spacing = 22f;
        int n = Math.Clamp((int)MathF.Ceiling(outer / (spacing * 0.75f)), 4, 24);
        int added = 0;
        for (int ring = 1; ring <= n && added < want; ring++)
        {
            for (int iy = -ring; iy <= ring && added < want; iy++)
            {
                for (int ix = -ring; ix <= ring && added < want; ix++)
                {
                    if (Math.Max(Math.Abs(ix), Math.Abs(iy)) != ring)
                        continue;
                    float jx = (Fract(ix * 0.211f + iy * 0.387f) - 0.5f) * 0.7f;
                    float jy = (Fract(ix * 0.173f + iy * 0.491f) - 0.5f) * 0.7f;
                    float px = (ix + ((iy & 1) * 0.5f) + jx) * spacing;
                    float py = (iy * 0.8660254f + jy) * spacing;
                    float d2 = px * px + py * py;
                    if (d2 < 16f * 16f || d2 > outer * outer)
                        continue;
                    var dir = SafeNormalize(fillDir * radius + t * px + b * py, fillDir);
                    if (!IsCheapDryLandForCarpet(dir))
                        continue;
                    int token = PackCarpetDirToken(dir) ^ unchecked((int)0x11000000);
                    if (_carpetTrees.ContainsKey(token))
                        continue;
                    if (!TrySpawnLocalCarpetTree(token, dir))
                        continue;
                    added++;
                }
            }
        }
    }

    void RecycleRearTrees(SN.Vector3 camDir, SN.Vector3 step, float radius, int cap, int perTick, float minKeepM)
    {
        int need = perTick - (cap - _carpetTrees.Count);
        if (need <= 1 || step.LengthSquared() < 1e-12f)
            return;
        var move = SN.Vector3.Normalize(step);
        _carpetStale.Clear();
        foreach (var kv in _carpetTreeDirs)
        {
            var delta = kv.Value - camDir;
            if (delta.Length() * radius < minKeepM)
                continue;
            if (SN.Vector3.Dot(delta, move) > -0.012f)
                continue;
            _carpetStale.Add(kv.Key);
        }
        int removed = 0;
        for (int i = 0; i < _carpetStale.Count && removed < need; i++)
        {
            RemoveLocalCarpetTree(_carpetStale[i]);
            removed++;
        }
    }

    void ClearLocalCarpetTrees()
    {
        foreach (var go in _carpetTrees.Values)
            go?.RemoveFromParent();
        _carpetTrees.Clear();
        _carpetTreeDirs.Clear();
        _carpetUsedFallbackTrees = false;
    }

    void RemoveLocalCarpetTree(int token)
    {
        if (_carpetTrees.TryGetValue(token, out var go))
            go?.RemoveFromParent();
        _carpetTrees.Remove(token);
        _carpetTreeDirs.Remove(token);
    }

    void EnsureLocalTreeModels()
    {
        if (_localTreeScanDone && _localTreeModels.Count > 0)
            return;
        if (ProjectService.Current == null)
            return;
        _localTreeScanDone = true;
        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddProfile(VegetationProfile? profile)
        {
            if (profile == null) return;
            ConsiderLocalTreeModel(profile.TreeModelPath, seen);
            var items = profile.TreeItems;
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null)
                    ConsiderLocalTreeModel(it.ModelPath, seen);
            }
        }
        if (_vegProfiles.TryGetValue("Universal", out var universal))
            AddProfile(universal);
        foreach (var profile in _vegProfiles.Values)
        {
            if (profile == null || string.Equals(profile.Id, "Universal", StringComparison.OrdinalIgnoreCase))
                continue;
            AddProfile(profile);
        }
    }

    bool WaitForProfileTreeFbxs()
    {
        if (_localTreeModels.Count == 0)
            return true;

        int ready = CountReadyLocalTreeModels();
        if (ready > 0)
        {
            if (_carpetUsedFallbackTrees)
            {
                ClearLocalCarpetTrees();
                _carpetUsedFallbackTrees = false;
            }
            _treeImportWaitSec = 0f;
            return true;
        }

        for (int i = 0; i < _localTreeModels.Count; i++)
        {
            string? abs = ResolveTreeModelAbsPath(_localTreeModels[i]);
            if (!string.IsNullOrWhiteSpace(abs))
                RequestTreeTemplateLoad(abs);
        }

        _treeImportWaitSec += Math.Max(1f / 60f, (float)Time.deltaTime);
        if (_treeImportWaitSec >= 0.85f)
            TryForceLoadProfileTreesOnMainThread();

        // Profile has real FBXs — do not plant white procedural stand-ins.
        return CountReadyLocalTreeModels() > 0;
    }

    int CountReadyLocalTreeModels()
    {
        int n = 0;
        for (int i = 0; i < _localTreeModels.Count; i++)
        {
            string? abs = ResolveTreeModelAbsPath(_localTreeModels[i]);
            if (!string.IsNullOrWhiteSpace(abs) && TryGetReadyTreeTemplate(abs, out _))
                n++;
        }
        return n;
    }

    void TryForceLoadProfileTreesOnMainThread()
    {
        int max = Math.Min(3, _localTreeModels.Count);
        for (int i = 0; i < max; i++)
        {
            string? abs = ResolveTreeModelAbsPath(_localTreeModels[i]);
            if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
                continue;
            try { GetOrLoadTreeTemplate(abs); }
            catch { }
            if (TryGetReadyTreeTemplate(abs, out _))
                return;
        }
    }

    void ConsiderLocalTreeModel(string? stored, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(stored) || !IsSupportedModelPath(stored))
            return;
        string? abs = ResolveTreeModelAbsPath(stored);
        if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs) || IsExcessivelyHeavySourceModel(abs))
            return;
        if (!seen.Add(abs))
            return;
        _localTreeModels.Add(PlanetAssetIO.NormalizeAssetReference(stored));
        RequestTreeTemplateLoad(abs);
    }

    bool TrySpawnLocalCarpetTree(int token, SN.Vector3 dir)
    {
        if (_terrain?.gameObject == null || _terrain.Config == null)
            return false;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);
        var planetW = GetPlanetWorldMatrix();
        float scale = 1.15f + Fract(token * 0.127f) * 0.45f;
        float treePad = GetTreeRadialOutwardPadding(scale);
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var surface = VegetationWorldPoint(_terrain, planetW, dir, treePad);
        var placeUp = ResolvePlanetTreeWorldUp(radialW, surfN);
        float yaw = Fract(token * 0.618034f) * 360f;

        var go = new GameObject($"LocalTree_{token:X8}");
        AttachSpawnedInstance(go);
        SetLocalCrustPosition(go, dir, treePad);

        string? model = PickReadyLocalTreeModel(token);
        bool spawned = false;
        if (!string.IsNullOrWhiteSpace(model))
            spawned = TrySpawnImportedVegetationMesh(go, model, isGrass: false, scale, surface, radialW, placeUp, yaw);
        if (!spawned && _localTreeModels.Count == 0 && !_deferSpawnForTemplate)
        {
            spawned = TrySpawnProceduralPlanetTree(go, scale, surface, radialW, placeUp, yaw);
            if (spawned)
                _carpetUsedFallbackTrees = true;
        }
        if (!spawned)
        {
            go.RemoveFromParent();
            return false;
        }

        _carpetTrees[token] = go;
        _carpetTreeDirs[token] = dir;
        return true;
    }

    string? PickReadyLocalTreeModel(int token)
    {
        _readyTreeScratch.Clear();
        for (int i = 0; i < _localTreeModels.Count; i++)
        {
            string? abs = ResolveTreeModelAbsPath(_localTreeModels[i]);
            if (!string.IsNullOrWhiteSpace(abs) && TryGetReadyTreeTemplate(abs, out _))
                _readyTreeScratch.Add(_localTreeModels[i]);
        }
        if (_readyTreeScratch.Count == 0)
            return null;
        int idx = token < 0 ? -token : token;
        return _readyTreeScratch[idx % _readyTreeScratch.Count];
    }

    bool TrySpawnProceduralPlanetTree(
        GameObject go,
        float scale,
        SN.Vector3 surface,
        SN.Vector3 radialW,
        SN.Vector3 placeUp,
        float yawDeg)
    {
        if (go == null)
            return false;
        if (!go.Behaviors.OfType<MeshFilter>().Any())
            go.AddBehavior(new MeshFilter());
        if (!go.Behaviors.OfType<MeshRenderer>().Any())
            go.AddBehavior(new MeshRenderer());
        if (!go.Behaviors.OfType<TreeLOD>().Any())
            go.AddBehavior(new TreeLOD());
        var tree = go.Behaviors.OfType<Tree>().FirstOrDefault();
        if (tree == null)
        {
            tree = new Tree();
            go.AddBehavior(tree);
        }

        int seed = HashCode.Combine(go.Name, (int)(yawDeg * 10f));
        tree.ModelPath = "";
        tree.Shape = (Math.Abs(seed) % 3) switch
        {
            0 => CanopyShape.Cone,
            1 => CanopyShape.LayeredCone,
            _ => CanopyShape.Sphere
        };
        tree.TrunkHeight = 4.4f + Fract(seed * 0.17f) * 3.8f;
        tree.TrunkRadiusBottom = 0.22f + Fract(seed * 0.31f) * 0.16f;
        tree.TrunkRadiusTop = 0.08f + Fract(seed * 0.41f) * 0.08f;
        tree.CanopyRadius = 1.7f + Fract(seed * 0.23f) * 1.9f;
        tree.CanopyHeight = 2.4f + Fract(seed * 0.29f) * 2.6f;
        tree.TrunkSegments = 6;
        tree.CanopySegments = 7;
        tree.CanopyLayers = 3;
        tree.IsVegetation = true;
        tree.RebuildTree();

        float s = Math.Clamp(scale, 0.85f, 1.85f);
        go.Transform.Scale = new Vector3(s, s, s);
        SetSurfaceAlignedRotation(go, placeUp, yawDeg);
        SinkTreeRootsToSurface(go, surface, radialW, placeUp, s);
        return go.Behaviors.OfType<MeshFilter>().Any(mf => mf.Mesh != null);
    }

    bool IsCheapDryLandForCarpet(SN.Vector3 dir)
        => TryPrepareCarpetClump(dir, out _, out _);

    static float Fract(float x) => x - MathF.Floor(x);

    void CollectNearestPlacementIndices(bool isGrass, int maxCount, float maxDistSq, SN.Vector3 cameraPos, List<int> result)
    {
        result.Clear();
        if (_assetPlacements.Count == 0 || maxCount <= 0 || _terrain?.Config == null)
            return;
        if (_assetDirs.Count != _assetPlacements.Count || _grassBuckets.Count + _treeBuckets.Count == 0)
            RebuildAssetPlacementAccel();

        var buckets = isGrass ? _grassBuckets : _treeBuckets;
        if (buckets.Count == 0)
            return;

        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        var camLocal = _terrain.WorldToLocal(cameraPos);
        var camDir = camLocal.LengthSquared() < 1e-8f
            ? SN.Vector3.UnitY
            : SN.Vector3.Normalize(camLocal);

        // |u-v|^2 on the unit sphere → crust chord² = that * r². Ignores camera altitude.
        float maxDirDeltaSq = maxDistSq / (radius * radius);
        UnpackDirBucket(PackDirBucket(camDir), out int cx, out int cy, out int cz);

        float bin = 2f / DirBucketRes;
        float ang = MathF.Sqrt(Math.Max(1e-8f, maxDistSq)) / radius;
        // Occupied-bucket expand only. A cubic 16-ring is ~36k empty probes; a 50k
        // fallback with a k-heap is tens of milliseconds. Earth spawn sits ~540 m
        // from the nearest stored grass, so keep enough ring to reach ~0.65 R.
        int ring = Math.Clamp((int)MathF.Ceiling(ang / bin) + 1, 2, 16);

        if (_nearestIdxScratch.Length < maxCount)
        {
            _nearestIdxScratch = new int[maxCount];
            _nearestDistScratch = new float[maxCount];
        }
        int count = 0;

        for (int r = 0; r <= ring && count < maxCount; r++)
        {
            foreach (var kv in buckets)
            {
                if (count >= maxCount) break;
                UnpackDirBucket(kv.Key, out int x, out int y, out int z);
                int md = Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz)));
                if (md != r) continue;
                var list = kv.Value;
                for (int n = 0; n < list.Count && count < maxCount; n++)
                {
                    int i = list[n];
                    if ((uint)i >= (uint)_assetDirs.Count) continue;
                    float dirD2 = (camDir - _assetDirs[i]).LengthSquared();
                    if (dirD2 > maxDirDeltaSq) continue;
                    _nearestIdxScratch[count++] = i;
                }
            }
        }

        for (int i = 0; i < count; i++)
            result.Add(_nearestIdxScratch[i]);
    }

    void EnsureLeafEntries(QuadNode leaf, string key)
    {
        if (!_leafEntries.TryGetValue(key, out var entries))
        {
            entries = new List<Entry>();
            _leafEntries[key] = entries;
        }

        if (_terrain?.Config == null) return;

        if (!_manualSpawnPass && entries.Count > 0)
        {
            int existingTrees = 0;
            int existingGrass = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsGrass) existingGrass++;
                else existingTrees++;
            }
            int treeDone = PlayPerfLimited ? Math.Min(4, MaxTreesPerLeaf) : MaxTreesPerLeaf;
            int grassDone = BatchGrassPerLeaf
                ? Math.Max(8, MaxGrassClumpsPerLeaf)
                : MaxGrassClumpsPerLeaf;
            if (existingTrees >= treeDone && existingGrass >= grassDone)
                return;
        }

        int hardCap = Math.Max(1024, _terrain.Config.MaxVegetationInstances);
        int currentTotal = ActiveVegetationInstances;
        if (currentTotal >= hardCap) return;

        bool fullPopulate = _manualSpawnPass && FullBiomePopulate;
        int tickCap = ResolveSpawnTickCap(fullPopulate);
        int spawnBudget = fullPopulate
            ? Math.Max(64, hardCap - currentTotal)
            : tickCap - LastSpawnedThisUpdate;
        if (spawnBudget <= 0) return;

        var sample = SampleLeafBiome(leaf, seedOffset: 0);
        var biome = sample ?? _terrain.OceanBiome;
        var leafDir = CubeSphereMath.FaceUVToDirection(leaf.Face, (leaf.U0 + leaf.U1) * 0.5f, (leaf.V0 + leaf.V1) * 0.5f);
        if (!ShouldSpawnVegetationAt(leafDir) && !LeafHasAnyDryLand(leaf))
            return;

        biome = ResolveLandVegetationBiome(biome, leafDir);
        var profile = ResolveVegetationProfile(biome);

        if (!PassesGrowthAndPatchiness(biome, leafDir, isTree: false) &&
            !PassesGrowthAndPatchiness(biome, leafDir, isTree: true))
            return;

        float treeDensityMul = GetAverageDensityMultiplier(profile?.TreeItems);
        float grassDensityMul = GetAverageDensityMultiplier(profile?.GrassItems);
        float leafArea01 = MathF.Max(1e-5f, MathF.Abs((leaf.U1 - leaf.U0) * (leaf.V1 - leaf.V0)));
        // Normalize UV leaf area into a practical multiplier so larger/coarser leaves
        // get proportionally more vegetation during full manual generation.
        float areaMul = Math.Clamp(leafArea01 * 1024f, 0.35f, 8f);
        int treeCapPerLeaf = fullPopulate
            ? Math.Max(1, (int)MathF.Round(MaxTreesPerLeaf * areaMul))
            : MaxTreesPerLeaf;
        int grassCapPerLeaf = fullPopulate
            ? Math.Max(1, (int)MathF.Round(MaxGrassClumpsPerLeaf * areaMul))
            : MaxGrassClumpsPerLeaf;
        if (PlayPerfLimited && !_manualSpawnPass)
            treeCapPerLeaf = Math.Min(treeCapPerLeaf, 4);
        int targetTrees = Math.Clamp((int)MathF.Round(biome.TreeDensity * treeDensityMul * treeCapPerLeaf), 0, treeCapPerLeaf);
        int targetGrass = Math.Clamp((int)MathF.Round(biome.VegetationDensity * grassDensityMul * grassCapPerLeaf), 0, grassCapPerLeaf);
        bool hasTreeItems = GetUsableTreeItems(profile).Count > 0;
        bool hasGrassItems = profile?.GrassItems?.Any(it => it != null && it.Weight > 0f) == true;
        if (ShouldSpawnVegetationAt(leafDir))
        {
            // Land with a vegetation profile should fill the leaf, not leave a couple of hidden plants.
            if (hasTreeItems && treeCapPerLeaf > 0)
                targetTrees = treeCapPerLeaf;
            if (hasGrassItems && grassCapPerLeaf > 0)
                targetGrass = grassCapPerLeaf;
        }
        if (_manualSpawnPass)
        {
            if (targetTrees <= 0 && hasTreeItems && treeCapPerLeaf > 0)
                targetTrees = treeCapPerLeaf;
            if (targetGrass <= 0 && hasGrassItems && grassCapPerLeaf > 0)
                targetGrass = grassCapPerLeaf;
        }

        int treeCount = 0;
        int grassCount = 0;
        for (int ei = 0; ei < entries.Count; ei++)
        {
            if (entries[ei].IsGrass) grassCount++;
            else treeCount++;
        }
        int globalGrassCount = CountActiveGrassInstances();
        var center = GetWorldCenter();

        // Grass first — tree spawns were consuming the per-frame budget and grass never ran.
        if (BatchGrassPerLeaf)
        {
            if (UsePlanetAssetPlacements && _manualSpawnPass)
            {
                int grassPlacements = Math.Clamp(targetGrass * 3, 8, Math.Max(24, MaxGrassClumpsPerLeaf * 3));
                for (int gi = 0; gi < grassPlacements; gi++)
                {
                    if (_assetPlacements.Count >= Math.Max(256, MaxStoredPlacements))
                        break;
                    var p = BuildPlacementFromLeaf(leaf, biome, isGrass: true, grassCount + 97 + gi * 19, profile);
                    if (p == null) continue;
                    _assetPlacements.Add(p);
                    RegisterAssetPlacementAt(_assetPlacements.Count - 1);
                    grassCount++;
                }
            }
            else
            {
                var usableGrass = GetUsableGrassItems(profile);
                int typeBatches = Math.Max(8, Math.Min(24, Math.Max(usableGrass.Count, targetGrass)));
                int clumpsPerBatch = Math.Max(3, targetGrass / Math.Max(1, typeBatches / 2));
                int maxBladesPerBatch = PlayPerfLimited ? 28 : 96;

                while (grassCount < targetGrass && spawnBudget > 0 && currentTotal < hardCap
                       && globalGrassCount < Math.Max(8, MaxRuntimeGrassPatches))
                {
                    int ti = grassCount;
                    int salt = HashLeaf(leaf, 97 + ti * 131);
                    var item = ChooseGrassItem(profile, salt, ti);
                    if (item == null && ti > 0)
                    {
                        grassCount++;
                        continue;
                    }
                    var patchDir = OffsetPatchDirection(leaf, ti, Math.Max(8, typeBatches));
                    if (!ShouldSpawnVegetationAt(patchDir))
                    {
                        grassCount++;
                        continue;
                    }
                    var go = SpawnLeafGrassBatch(leaf, biome, clumpsPerBatch, profile, salt, item, patchDir, maxBladesPerBatch);
                    if (go == null || !TryReadGpuGrassToken(go, out int gpuToken) || !PlanetGpuGrass.HasPatch(this, gpuToken))
                    {
                        grassCount++;
                        spawnBudget--;
                        continue;
                    }

                    entries.Add(new Entry
                    {
                        GameObject = go,
                        Biome = biome,
                        BiomeName = biome.Name,
                        IsGrass = true,
                        BaseScale = 1f,
                        SurfaceDir = patchDir,
                        YawDeg = 0f,
                        ModelPath = item?.ModelPath ?? "",
                        TexturePath = IsImageAssetPath(item?.ModelPath) ? item!.ModelPath : DefaultPlanetGrassTexturePath,
                        PrefabPath = "",
                        GpuGrassToken = gpuToken
                    });
                    grassCount++;
                    globalGrassCount++;
                    currentTotal++;
                    spawnBudget--;
                    LastSpawnedThisUpdate++;
                }
            }
        }
        else while (grassCount < targetGrass && spawnBudget > 0 && currentTotal < hardCap)
        {
            if (UsePlanetAssetPlacements && _manualSpawnPass)
            {
                if (_assetPlacements.Count >= Math.Max(256, MaxStoredPlacements))
                    break;
                var p = BuildPlacementFromLeaf(leaf, biome, isGrass: true, grassCount + 97, profile);
                if (p == null) break;
                _assetPlacements.Add(p);
                RegisterAssetPlacementAt(_assetPlacements.Count - 1);
                grassCount++;
                continue;
            }
            if (globalGrassCount >= Math.Max(8, MaxRuntimeGrassPatches))
                break;
            var go = SpawnVegetationObject(leaf, biome, isGrass: true, grassCount + 97, profile);
            if (_deferSpawnForTemplate || go == null)
                break;
            if (go.Parent == null || (!HasMeshFilterDeep(go) && !TryReadGpuGrassToken(go, out _)))
            {
                grassCount++;
                spawnBudget--;
                continue;
            }
            var dir = RadialDirFromLocalPosition(new SN.Vector3(
                (float)go.Transform.Position.X,
                (float)go.Transform.Position.Y,
                (float)go.Transform.Position.Z));
            string grassModelPath = ResolveVegetationModelPathForAsset(go, true);
            if (string.IsNullOrWhiteSpace(grassModelPath))
                grassModelPath = DefaultPlanetGrassModelPath;
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                BiomeName = biome.Name,
                IsGrass = true,
                BaseScale = (float)go.Transform.Scale.X,
                SurfaceDir = dir,
                YawDeg = SpawnYawFromLeaf(leaf, grassCount + 97),
                ModelPath = PlanetAssetIO.NormalizeAssetReference(grassModelPath),
                TexturePath = "",
                PrefabPath = go.PrefabPath ?? ""
            });
            grassCount++;
            globalGrassCount++;
            currentTotal++;
            spawnBudget--;
            LastSpawnedThisUpdate++;
        }

        while (treeCount < targetTrees && spawnBudget > 0 && currentTotal < hardCap)
        {
            if (UsePlanetAssetPlacements && _manualSpawnPass)
            {
                if (_assetPlacements.Count >= Math.Max(256, MaxStoredPlacements))
                    break;
                var p = BuildPlacementFromLeaf(leaf, biome, isGrass: false, treeCount + 17, profile);
                if (p == null || !PlacementHasUsableTreeAsset(p, _assetPlacements.Count))
                    continue;
                _assetPlacements.Add(p);
                RegisterAssetPlacementAt(_assetPlacements.Count - 1);
                treeCount++;
                continue;
            }
            var go = SpawnVegetationObject(leaf, biome, isGrass: false, treeCount + 17, profile);
            if (_deferSpawnForTemplate)
                break;
            if (go == null)
                continue;
            var dir = RadialDirFromLocalPosition(new SN.Vector3(
                (float)go.Transform.Position.X,
                (float)go.Transform.Position.Y,
                (float)go.Transform.Position.Z));
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                BiomeName = biome.Name,
                IsGrass = false,
                BaseScale = (float)go.Transform.Scale.X,
                SurfaceDir = dir,
                YawDeg = SpawnYawFromLeaf(leaf, treeCount + 17),
                ModelPath = PlanetAssetIO.NormalizeAssetReference(ResolveVegetationModelPathForAsset(go, false)),
                TexturePath = "",
                PrefabPath = PlanetAssetIO.NormalizeAssetReference(go.PrefabPath ?? "")
            });
            treeCount++;
            currentTotal++;
            spawnBudget--;
            LastSpawnedThisUpdate++;
        }
    }

    void UpdateLeafVitality(string key)
    {
        if (!_leafEntries.TryGetValue(key, out var entries)) return;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            // Mild wetness helps growth; heavy snow / storms still stress plants.
            float stress = Math.Clamp(_snow * 0.65f + MathF.Max(0f, _wetness - 0.55f) * 0.35f, 0f, 1f);
            float moistureHelp = Math.Clamp(_wetness * 0.35f, 0f, 0.35f);
            float growth = e.Biome.VegetationRegrowthRate
                * Math.Max(0.1f, e.Biome.SeasonalGrowthMultiplier)
                * (1f - stress + moistureHelp);
            float decay = e.Biome.VegetationDecayRate * stress;
            e.Vitality = Math.Clamp(e.Vitality + (growth - decay) * Math.Max(0.05f, UpdateIntervalSeconds), 0f, 1f);

            if (e.GameObject?.Behaviors?.OfType<Tree>().FirstOrDefault() is Tree t)
            {
                float lifeScale = 0.55f + e.Vitality * 0.65f;
                float scale = Math.Max(0.01f, e.BaseScale) * lifeScale;
                var s = e.GameObject.Transform.Scale;
                s.X = scale;
                s.Y = scale;
                s.Z = scale;
                e.GameObject.Transform.Scale = s;
                t.WindSway = Math.Clamp((e.IsGrass ? 1f : 0.65f) * _windMultiplier, 0f, 3f);
                t.WindSpeed = Math.Clamp((e.IsGrass ? 1.35f : 1f) * _windMultiplier, 0f, 4f);
            }
            else if (!e.IsGrass && e.GameObject?.Behaviors?.OfType<Tree>().FirstOrDefault() != null)
            {
                // Procedural trees only — imported meshes keep spawn scale/orientation stable.
                float lifeScale = 0.55f + e.Vitality * 0.65f;
                float scale = Math.Max(0.01f, e.BaseScale) * lifeScale;
                var s = e.GameObject.Transform.Scale;
                s.X = scale;
                s.Y = scale;
                s.Z = scale;
                e.GameObject.Transform.Scale = s;
            }

            if (e.GameObject?.Behaviors?.OfType<VegetationPainter>().FirstOrDefault() is VegetationPainter vp)
            {
                vp.WindStrength = Math.Clamp(0.35f + _windMultiplier * 0.5f, 0f, 3f);
                vp.WindSpeed = Math.Clamp(0.8f + _windMultiplier * 0.8f, 0f, 4f);
            }

            if (RemoveVegetationWhenVitalityExhausted && e.Vitality <= 0.02f)
            {
                if (e.IsGrass)
                    PlanetGpuGrass.RemovePatch(this, e.GpuGrassToken);
                e.GameObject.RemoveFromParent();
                entries.RemoveAt(i);
                LastDespawnedThisUpdate++;
            }
        }
    }

    void DespawnLeaf(string key)
    {
        if (!_leafEntries.TryGetValue(key, out var entries)) return;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsGrass)
                PlanetGpuGrass.RemovePatch(this, entries[i].GpuGrassToken);
            entries[i].GameObject.RemoveFromParent();
        }
        LastDespawnedThisUpdate += entries.Count;
        _leafEntries.Remove(key);
    }

    int CountActiveGrassInstances()
    {
        int count = 0;
        foreach (var group in _leafEntries.Values)
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].IsGrass)
                    count++;
            }
        }
        return count;
    }

    BiomeDefinition? ResolveBiomeByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || _terrain?.Config?.Biomes == null) return null;
        var biomes = _terrain.Config.Biomes;
        for (int i = 0; i < biomes.Length; i++)
            if (string.Equals(biomes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return biomes[i];
        return null;
    }

    GameObject? SpawnVegetationFromPlacement(PlanetVegetationPlacement p, int index)
    {
        _deferSpawnForTemplate = false;
        if (_terrain?.gameObject == null || _terrain.Config == null) return null;
        if (!p.IsGrass)
            EnrichTreePlacementFromProfile(p, index);
        var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
        var planetW = GetPlanetWorldMatrix();
        float treePad = GetTreeRadialOutwardPadding(p.IsGrass ? 1f : Math.Max(0.01f, p.Scale));
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var surfaceGrass = VegetationWorldPoint(_terrain, planetW, dir, 0f);
        var surfaceTree = VegetationWorldPoint(_terrain, planetW, dir, treePad);
        var placeUp = p.IsGrass
            ? ResolvePlanetGrassWorldUp(radialW, surfN)
            : ResolvePlanetTreeWorldUp(radialW, surfN);
        float yawDeg = p.YawDeg;
        var go = new GameObject(p.IsGrass ? $"AssetGrass_{index}" : $"AssetTree_{index}");
        AttachSpawnedInstance(go);
        if (!p.IsGrass)
            SetLocalCrustPosition(go, dir, treePad);

        string prefabPath = p.PrefabPath ?? "";
        if (string.IsNullOrWhiteSpace(prefabPath) && (p.ModelPath ?? "").EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            prefabPath = p.ModelPath;
        if (!string.IsNullOrWhiteSpace(prefabPath))
        {
            var prefab = Prefab.Load(prefabPath);
            if (prefab != null && prefab.Instantiate(_terrain.gameObject) is GameObject inst)
            {
                AttachSpawnedInstance(inst);
                SetLocalCrustPosition(inst, dir, p.IsGrass ? 0f : treePad);
                float s = Math.Max(0.01f, p.Scale);
                inst.Transform.Scale = new Vector3(s, s, s);
                if (p.IsGrass)
                    SeatGrassOnSurface(inst, surfaceGrass, radialW, placeUp, s);
                else
                {
                    SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, s);
                }
                if (p.IsGrass)
                    SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                go.RemoveFromParent();
                return inst;
            }
        }

        if (p.IsGrass)
        {
            float grassScale = Math.Max(0.01f, p.Scale);
            if (TrySpawnPlanetGrassBillboard(go, p, dir, grassScale, surfaceGrass, radialW, placeUp, yawDeg, index))
            return go;

            if (_deferSpawnForTemplate)
            {
                go.RemoveFromParent();
                return null;
            }

            go.RemoveFromParent();
            return null;
        }

        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
        {
            if (TrySpawnImportedVegetationMesh(go, p.ModelPath, isGrass: false, Math.Max(0.01f, p.Scale), surfaceTree, radialW, placeUp, yawDeg))
                return go;
            if (_deferSpawnForTemplate)
            {
                go.RemoveFromParent();
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(p.ModelPath) || !IsSupportedModelPath(p.ModelPath))
        {
            if (TrySpawnProceduralPlanetTree(go, Math.Max(0.85f, p.Scale), surfaceTree, radialW, placeUp, yawDeg))
                return go;
        }

        go.RemoveFromParent();
        return null;
    }

    GameObject? SpawnVegetationObject(QuadNode leaf, BiomeDefinition biome, bool isGrass, int seedOffset, VegetationProfile? profile)
    {
        _deferSpawnForTemplate = false;
        float u = Random01(HashLeaf(leaf, 11 + seedOffset));
        float v = Random01(HashLeaf(leaf, 31 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        if (!ShouldSpawnVegetationAt(dir) || !PassesGrowthAndPatchiness(biome, dir, isTree: !isGrass))
            return null;

        var planetW = GetPlanetWorldMatrix();
        float approxTreeScale = isGrass ? 1f : (biome.TreeMinScale + biome.TreeMaxScale) * 0.5f;
        float treePad = GetTreeRadialOutwardPadding(approxTreeScale);
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var surfaceGrass = VegetationWorldPoint(_terrain!, planetW, dir, 0f);
        var surfaceTree = VegetationWorldPoint(_terrain, planetW, dir, treePad);
        var placeUp = isGrass
            ? ResolvePlanetGrassWorldUp(radialW, surfN)
            : ResolvePlanetTreeWorldUp(radialW, surfN);

        var go = new GameObject(isGrass ? $"BiomeGrass_{leaf.Face}" : $"BiomeTree_{leaf.Face}");
        AttachSpawnedInstance(go);
        SetLocalCrustPosition(go, dir, isGrass ? 0f : treePad);
        float yawDeg = Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;

        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));
        if (!isGrass)
        {
            var flora = GetComponent<PlanetFloraSpawner>();
            if (flora != null && item != null && !flora.TryRegisterMesh(item.ModelPath))
            {
                go.RemoveFromParent();
                return null;
            }
        }
        string prefabPath = item?.PrefabPath ?? "";
        if (string.IsNullOrWhiteSpace(prefabPath) && !string.IsNullOrWhiteSpace(item?.ModelPath) && IsPrefabPath(item.ModelPath))
            prefabPath = item.ModelPath;
        if (!string.IsNullOrWhiteSpace(prefabPath))
        {
            var prefab = Prefab.Load(prefabPath);
            if (prefab != null && prefab.Instantiate(_terrain.gameObject!) is GameObject inst)
            {
                AttachSpawnedInstance(inst);
                SetLocalCrustPosition(inst, dir, isGrass ? 0f : treePad);
                float prefMinScale = isGrass ? biome.GrassMinScale : biome.TreeMinScale;
                float prefMaxScale = isGrass ? biome.GrassMaxScale : biome.TreeMaxScale;
                if (item != null)
                {
                    prefMinScale *= item.MinScale;
                    prefMaxScale *= item.MaxScale;
                }
                if (prefMaxScale < prefMinScale) (prefMaxScale, prefMinScale) = (prefMinScale, prefMaxScale);
                float prefScale = prefMinScale + (prefMaxScale - prefMinScale) * Random01(HashLeaf(leaf, 97 + seedOffset));
                inst.Transform.Scale = new Vector3(prefScale, prefScale, prefScale);
                if (isGrass)
                    SeatGrassOnSurface(inst, surfaceGrass, radialW, placeUp, prefScale);
                else
                {
                    SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, prefScale);
                }
                if (isGrass)
                    SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                go.RemoveFromParent();
                return inst;
            }
        }
        if (isGrass)
        {
            float baseScale = biome.GrassMinScale;
            float maxBaseScale = biome.GrassMaxScale;
            if (item != null)
            {
                baseScale *= item.MinScale;
                maxBaseScale *= item.MaxScale;
            }
            if (maxBaseScale < baseScale) (baseScale, maxBaseScale) = (maxBaseScale, baseScale);
            float grassScale = baseScale + (maxBaseScale - baseScale) * Random01(HashLeaf(leaf, 97 + seedOffset));
            grassScale = Math.Max(0.01f, grassScale);

            if (item != null && IsImageAssetPath(item.ModelPath))
            {
                var fake = new PlanetVegetationPlacement
                {
                    TexturePath = item.ModelPath,
                    ModelPath = item.ModelPath,
                    Scale = grassScale
                };
                if (TrySpawnPlanetGrassBillboard(go, fake, dir, grassScale, surfaceGrass, radialW, placeUp, yawDeg, _nextGpuGrassToken--))
                    return go;
            }
            if (_deferSpawnForTemplate)
            {
                go.RemoveFromParent();
                return null;
            }

            go.RemoveFromParent();
            return null;
        }

        bool wantsImportedTree = item != null && IsSupportedModelPath(item.ModelPath);
        if (wantsImportedTree)
        {
            float minScaleImported = biome.TreeMinScale * item!.MinScale;
            float maxScaleImported = biome.TreeMaxScale * item.MaxScale;
            if (maxScaleImported < minScaleImported)
                (maxScaleImported, minScaleImported) = (minScaleImported, maxScaleImported);
            float importedScale = minScaleImported + (maxScaleImported - minScaleImported) * Random01(HashLeaf(leaf, 97 + seedOffset));
            if (TrySpawnImportedVegetationMesh(go, item.ModelPath, isGrass: false, importedScale, surfaceTree, radialW, placeUp, yawDeg))
            return go;
            if (_deferSpawnForTemplate)
            {
                go.RemoveFromParent();
                return null;
            }
        }

        float procScale = biome.TreeMinScale + (biome.TreeMaxScale - biome.TreeMinScale) * Random01(HashLeaf(leaf, 97 + seedOffset));
        if (TrySpawnProceduralPlanetTree(go, Math.Max(0.85f, procScale), surfaceTree, radialW, placeUp, yawDeg))
            return go;

        go.RemoveFromParent();
        return null;
    }

    PlanetVegetationPlacement? BuildPlacementFromLeaf(QuadNode leaf, BiomeDefinition biome, bool isGrass, int seedOffset, VegetationProfile? profile)
    {
        float u = Random01(HashLeaf(leaf, 11 + seedOffset));
        float v = Random01(HashLeaf(leaf, 31 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        for (int attempt = 0; attempt < 6 && !ShouldSpawnVegetationAt(dir); attempt++)
        {
            u = Random01(HashLeaf(leaf, 11 + seedOffset + 17 * (attempt + 1)));
            v = Random01(HashLeaf(leaf, 31 + seedOffset + 29 * (attempt + 1)));
            dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        }
        if (!ShouldSpawnVegetationAt(dir))
            return null;
        float yawDeg = Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;
        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));

        string prefabPath = item?.PrefabPath ?? "";
        string modelPath = "";
        string texturePath = "";
        if (item != null && !string.IsNullOrWhiteSpace(item.ModelPath))
        {
            if (IsPrefabPath(item.ModelPath) && string.IsNullOrWhiteSpace(prefabPath))
                prefabPath = item.ModelPath;
            else if (isGrass && IsImageAssetPath(item.ModelPath))
                texturePath = item.ModelPath;
            else if (isGrass && !IsSupportedModelPath(item.ModelPath) && !IsPrefabPath(item.ModelPath))
                modelPath = DefaultPlanetGrassModelPath;
            else
                modelPath = item.ModelPath;
        }
        if (isGrass && string.IsNullOrWhiteSpace(texturePath) && string.IsNullOrWhiteSpace(modelPath))
            texturePath = DefaultPlanetGrassTexturePath;

        float minScale = isGrass ? biome.GrassMinScale : biome.TreeMinScale;
        float maxScale = isGrass ? biome.GrassMaxScale : biome.TreeMaxScale;
        if (item != null)
        {
            minScale *= item.MinScale;
            maxScale *= item.MaxScale;
        }
        if (maxScale < minScale) (maxScale, minScale) = (minScale, maxScale);
        float scale = minScale + (maxScale - minScale) * Random01(HashLeaf(leaf, 97 + seedOffset));

        return new PlanetVegetationPlacement
        {
            IsGrass = isGrass,
            BiomeName = biome.Name ?? "",
            PrefabPath = PlanetAssetIO.NormalizeAssetReference(prefabPath),
            ModelPath = PlanetAssetIO.NormalizeAssetReference(modelPath),
            TexturePath = PlanetAssetIO.NormalizeAssetReference(texturePath),
            DirX = dir.X,
            DirY = dir.Y,
            DirZ = dir.Z,
            Scale = Math.Max(0.01f, scale),
            YawDeg = yawDeg
        };
    }

    static float EstimateMeshVerticalExtent(Mesh? mesh)
    {
        var verts = mesh?.Vertices;
        if (verts == null || verts.Length == 0) return 0f;
        float minY = verts[0].Y, maxY = verts[0].Y;
        for (int i = 1; i < verts.Length; i++)
        {
            float y = verts[i].Y;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return MathF.Max(0f, maxY - minY);
    }

    static float EstimateMeshExtent(Mesh? mesh)
    {
        var verts = mesh?.Vertices;
        if (verts == null || verts.Length == 0) return 0f;
        float minX = verts[0].X, maxX = verts[0].X;
        float minY = verts[0].Y, maxY = verts[0].Y;
        float minZ = verts[0].Z, maxZ = verts[0].Z;
        for (int i = 1; i < verts.Length; i++)
        {
            float x = verts[i].X;
            float y = verts[i].Y;
            float z = verts[i].Z;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        float ex = MathF.Max(0f, maxX - minX);
        float ey = MathF.Max(0f, maxY - minY);
        float ez = MathF.Max(0f, maxZ - minZ);
        return MathF.Max(ex, MathF.Max(ey, ez));
    }

    bool TrySpawnImportedVegetationMesh(
        GameObject go,
        string modelPath,
        bool isGrass,
        float baseScale,
        SN.Vector3 surface,
        SN.Vector3 radialW,
        SN.Vector3 placeUp,
        float yawDeg)
    {
        if (!TrySetupImportedTree(go, modelPath, out float meshExtent))
            return false;

        float importedScale = Math.Max(0.01f, baseScale);
        float grassWorldH = Math.Clamp(Math.Max(GrassBaseHeight, 4.5f), 4.5f, 7.2f);
        float desiredHeight = isGrass
            ? Math.Max(1.8f, GrassBaseHeight * 2.4f)
            : Math.Max(16f, Math.Max(TreeBaseHeight * 2.2f, grassWorldH * 2.5f));
        if (meshExtent > 1e-4f)
        {
            float fit = desiredHeight / meshExtent;
            importedScale *= Math.Clamp(fit, 0.12f, 28f);
            float projected = meshExtent * importedScale;
            if (projected < desiredHeight * 0.5f)
                importedScale = (desiredHeight * 0.92f) / meshExtent;
        }

        importedScale = isGrass
            ? Math.Clamp(importedScale, 0.75f, 6f)
            : Math.Clamp(importedScale, 0.35f, 18f);
        go.Transform.Scale = new Vector3(importedScale, importedScale, importedScale);
        if (!isGrass)
            ApplyImportedTreeRenderSettingsRecursive(go);
        if (isGrass)
            ApplyImportedGrassCardCutoutRecursive(go);
        if (isGrass)
            SeatGrassOnSurface(go, surface, radialW, placeUp, importedScale);
        else
        {
            SetSurfaceAlignedRotation(go, placeUp, yawDeg);
            SinkTreeRootsToSurface(go, surface, radialW, placeUp, importedScale);
        }
        if (isGrass)
            SetSurfaceAlignedRotation(go, placeUp, yawDeg);
        ApplyImportedTreeMeshEulerCorrection(go);
        return true;
    }

    bool TrySetupImportedTree(GameObject go, string modelPath, out float meshExtent)
    {
        meshExtent = 0f;
        string? abs = ResolveTreeModelAbsPath(modelPath);
        if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
            return false;

        if (!TryGetReadyTreeTemplate(abs, out var tpl))
        {
            RequestTreeTemplateLoad(abs);
            if (_manualSpawnPass && !SceneService.PlayMode)
            {
                GetOrLoadTreeTemplate(abs);
                if (!TryGetReadyTreeTemplate(abs, out tpl))
                    return false;
            }
            else
            {
                _deferSpawnForTemplate = true;
                return false;
            }
        }

        if (tpl.Source != null)
        {
            AttachImportedTreeFromTemplate(tpl.Source, go);
            ApplyImportedTreeRenderSettingsRecursive(go);
            // Materials were resolved when the template was imported on a worker thread.
            meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateHierarchyVerticalExtent(go);
            if (meshExtent < 1e-3f)
                meshExtent = tpl.MeshExtent;
            DisablePlanetImportedTreeLod(go);
            return go.Behaviors.OfType<MeshFilter>().Any(mf => mf.Mesh != null)
                   || go.Children.Any(c => HasMeshFilterDeep(c));
        }

        // Prefer pre-baked flat template (model-space meshes, no FBX child offsets).
        if (!string.IsNullOrWhiteSpace(tpl.VisualHierarchyJson))
        {
            var inst = SceneSerialization.DeserializeGameObjectFromJson(tpl.VisualHierarchyJson);
            if (inst != null)
            {
                ClearImportedModelPathRecursive(inst);
                // Older cache entries may still carry FBX children; bake the clone, not the placed GO.
                if (inst.Children.Count > 0)
                    EnsurePlanetImportedTreeCrustLocal(inst);

                while (inst.Children.Count > 0)
                    go.AddChild(inst.Children[0]);

                var rootMeshBehaviors = new List<Behavior>();
                foreach (var b in inst.Behaviors)
                {
                    if (b is MeshFilter || b is MeshRenderer)
                        rootMeshBehaviors.Add(b);
                }
                foreach (var b in rootMeshBehaviors)
                {
                    inst.RemoveBehavior(b);
                    go.AddBehavior(b);
                }

                ApplyImportedTreeRenderSettingsRecursive(go);
                ResolveImportedTreeMaterialsRecursive(go);
                // Do NOT re-bake on go — it already has crust position/rotation and baking would
                // embed those into vertex data, then Collapse zeros the anchor → giant green shards.

                meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateHierarchyVerticalExtent(go);
                if (meshExtent < 1e-3f)
                    meshExtent = tpl.MeshExtent;

                DisablePlanetImportedTreeLod(go);
                return go.Behaviors.OfType<MeshFilter>().Any(mf => mf.Mesh != null)
                       || go.Children.Any(c => HasMeshFilterDeep(c));
            }
        }

        if (tpl.Mesh == null)
            return false;

        var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault() ?? go.AddBehavior<MeshFilter>();
        mf.Mesh = tpl.Mesh;
        mf.ModelPath = "";
        meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateMeshVerticalExtent(tpl.Mesh);

        var mr = go.Behaviors.OfType<MeshRenderer>().FirstOrDefault() ?? go.AddBehavior<MeshRenderer>();
        if (tpl.Material != null)
        {
            mr.MaterialPaths.Clear();
            mr.ResolvedMaterials.Clear();
            mr.Material = tpl.Material;
        }
        mr.DoubleSided = true;

        ResolveImportedTreeMaterialsRecursive(go);
        DisablePlanetImportedTreeLod(go);
        return true;
    }

    static void AttachImportedTreeFromTemplate(GameObject src, GameObject dest)
    {
        if (src == null || dest == null) return;

        CopyImportedMeshBehaviors(src, dest);
        var children = src.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child == null) continue;
            if (!string.IsNullOrEmpty(child.Name) &&
                child.Name.Contains("collider", StringComparison.OrdinalIgnoreCase))
                continue;
            var clone = new GameObject(child.Name);
            dest.AddChild(clone);
            clone.Transform.Position = new Vector3(0, 0, 0);
            clone.Transform.Rotation = new Vector3(0, 0, 0);
            clone.Transform.Scale = new Vector3(1, 1, 1);
            AttachImportedTreeFromTemplate(child, clone);
        }
    }

    static void CopyImportedMeshBehaviors(GameObject src, GameObject dest)
    {
        MeshFilter? pendingMf = null;
        foreach (var b in src.Behaviors)
        {
            if (b is MeshFilter f && f.Mesh != null)
            {
                pendingMf = f;
                continue;
            }
            if (b is not MeshRenderer r || pendingMf?.Mesh == null)
                continue;

            var newMf = dest.AddBehavior<MeshFilter>();
            newMf.Mesh = pendingMf.Mesh;
            newMf.ModelPath = "";
            var newMr = dest.AddBehavior<MeshRenderer>();
            newMr.DoubleSided = true;
            newMr.CastShadows = r.CastShadows;
            newMr.ReceiveShadows = r.ReceiveShadows;
            if (r.Material != null)
                newMr.Material = r.Material;
            for (int i = 0; i < r.MaterialPaths.Count; i++)
                newMr.MaterialPaths.Add(r.MaterialPaths[i]);
            pendingMf = null;
        }

        if (pendingMf?.Mesh == null)
            return;
        var loneMf = dest.AddBehavior<MeshFilter>();
        loneMf.Mesh = pendingMf.Mesh;
        loneMf.ModelPath = "";
        var loneMr = dest.AddBehavior<MeshRenderer>();
        loneMr.DoubleSided = true;
    }

    static void ResolveImportedTreeMaterialsRecursive(GameObject root)
    {
        foreach (var b in root.Behaviors)
        {
            if (b is MeshRenderer mr)
            {
                mr.DoubleSided = true;
                bool alreadyTextured = MeshRendererHasBoundAlbedo(mr);
                if (!alreadyTextured)
                {
                    try { mr.ResolveMaterials(); } catch { }
                }
                if (mr.Material != null)
                {
                    try
                    {
                        MaterialUtil.TryBindColAlphaSiblingMaps(mr.Material, ProjectService.Current?.RootPath);
                        MaterialUtil.EnsureOpaqueFoliageCutout(mr.Material);
                    }
                    catch { }
                    if (mr.Material.AlphaCutoff < 0.05f)
                        mr.Material.AlphaCutoff = 0.45f;
                }
            }
        }
        foreach (var c in root.Children)
            ResolveImportedTreeMaterialsRecursive(c);
    }

    /// <summary>
    /// SpeedTree / Pine_002 exports often ship a shared "billboards" material stub with no textures,
    /// plus separate billboard cards. Bind sibling TIFFs from Materials/ and drop broken cards.
    /// </summary>
    static void FixSpeedTreeImportedTreeMaterials(GameObject root, string modelAbsPath)
    {
        if (root == null || string.IsNullOrWhiteSpace(modelAbsPath)) return;
        string? modelDir = Path.GetDirectoryName(Path.GetFullPath(modelAbsPath));
        if (string.IsNullOrEmpty(modelDir)) return;
        string materialsDir = Path.Combine(modelDir, "Materials");
        if (!Directory.Exists(materialsDir)) return;

        string? branchesAlbedo = FindTextureFile(materialsDir, "Branches_Albedo", "Branches_albedo");
        string? trunkAlbedo = FindTextureFile(materialsDir, "trunk_Albedo", "trunk_albedo");
        string? branchesMask = FindTextureFile(materialsDir, "Branches_MaskMap", "Branches_maskmap");
        string? projectRoot = ProjectService.Current?.RootPath;

        void Walk(GameObject node)
        {
            MeshFilter? mf = null;
            MeshRenderer? mr = null;
            foreach (var b in node.Behaviors)
            {
                if (b is MeshFilter f) mf = f;
                else if (b is MeshRenderer r) mr = r;
            }

            bool nodeIsBillboard = IsTreeBillboardLodNodeName(node.Name);
            bool meshIsCard = mf != null && IsPaperThinBillboardMesh(mf.Mesh);

            if (mr != null && !MeshRendererHasBoundAlbedo(mr) && !meshIsCard && !nodeIsBillboard)
            {
                string hint = node.Name ?? "";
                string? tex = null;
                string? mask = null;
                if (hint.Contains("trunk", StringComparison.OrdinalIgnoreCase))
                    tex = trunkAlbedo;
                else
                {
                    tex = branchesAlbedo ?? trunkAlbedo;
                    mask = branchesMask;
                }

                if (!string.IsNullOrWhiteSpace(tex))
                    TryBindAlbedoOnMaterial(mr.Material, tex, projectRoot, mask);
            }

            if (mf != null && mr != null && !MeshRendererHasBoundAlbedo(mr) && (nodeIsBillboard || meshIsCard))
                RemoveMeshBehaviors(node);

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                Walk(child);
                if (child.Behaviors.Count == 0 && child.Children.Count == 0)
                    child.RemoveFromParent();
            }
        }

        Walk(root);
    }

    static string? FindTextureFile(string dir, params string[] baseNames)
    {
        string[] exts = { ".tif", ".tga", ".png", ".jpg", ".jpeg", ".webp" };
        for (int i = 0; i < baseNames.Length; i++)
        {
            for (int e = 0; e < exts.Length; e++)
            {
                string path = Path.Combine(dir, baseNames[i] + exts[e]);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    static void TryBindAlbedoOnMaterial(Material? mat, string absAlbedo, string? projectRoot, string? absMask = null)
    {
        if (mat == null || !File.Exists(absAlbedo)) return;
        try
        {
            var albedo = Texture2D.FromFile(absAlbedo);
            mat.Textures.Clear();
            string rel = !string.IsNullOrWhiteSpace(projectRoot)
                ? Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(absAlbedo)).Replace('\\', '/')
                : absAlbedo;
            mat.Textures.Add(new RuntimeTexSlot
            {
                Usage = "Albedo",
                Texture = albedo,
                SourcePath = rel,
                FaceMask = -1
            });

            if (!string.IsNullOrWhiteSpace(absMask) && File.Exists(absMask))
            {
                var mask = Texture2D.FromFile(absMask);
                string relMask = !string.IsNullOrWhiteSpace(projectRoot)
                    ? Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(absMask)).Replace('\\', '/')
                    : absMask;
                mat.Textures.Add(new RuntimeTexSlot
                {
                    Usage = "Opacity",
                    Texture = mask,
                    SourcePath = relMask,
                    FaceMask = -1
                });
            }
        }
        catch { }
    }

    static void RemoveMeshBehaviors(GameObject node)
    {
        var remove = new List<Behavior>(4);
        foreach (var b in node.Behaviors)
        {
            if (b is MeshFilter or MeshRenderer)
                remove.Add(b);
        }
        for (int i = 0; i < remove.Count; i++)
            node.RemoveBehavior(remove[i]);
    }

    static bool MeshRendererHasBoundAlbedo(MeshRenderer mr)
    {
        if (mr.Material?.Textures == null) return false;
        foreach (var s in mr.Material.Textures)
        {
            if (s is RuntimeTexSlot rts && rts.Texture != null)
                return true;
        }
        return false;
    }

    static void ApplyImportedGrassCardCutoutRecursive(GameObject root)
    {
        foreach (var b in root.Behaviors)
        {
            if (b is MeshRenderer mr && mr.Material != null)
            {
                mr.DoubleSided = true;
                try { MaterialUtil.EnsureVegetationCardCutout(mr.Material); } catch { }
            }
        }
        foreach (var c in root.Children)
            ApplyImportedGrassCardCutoutRecursive(c);
    }

    static void ConfigurePlanetGrassPainter(
        VegetationPainter painter,
        VegetationProfileItem? item,
        VegetationProfile? profile,
        string? placementTexture = null)
    {
        string? texture = null;

        void ConsiderTexture(string? path)
        {
            if (IsImageAssetPath(path))
                texture ??= path;
        }

        ConsiderTexture(placementTexture);
        ConsiderTexture(item?.ModelPath);
        ConsiderTexture(profile?.GrassModelPath);

        texture ??= DefaultPlanetGrassTexturePath;
        painter.TexturePath = PlanetAssetIO.NormalizeAssetReference(texture);
        // Planet grass streams as cross-blade billboards. Meadow FBX clumps import on the
        // main thread and often render as huge gray silhouettes when materials are not ready.
        painter.CustomMeshPath = "";
    }

    float ResolvePlanetGrassWorldHeight(float placementScale = 1f)
    {
        float r = _terrain?.Config?.EffectiveWorldRadius ?? 1000f;
        float scale = Math.Max(0.85f, placementScale);
        return Math.Clamp(Math.Max(GrassBaseHeight * 5f, r * 0.0045f) * scale, 4f, 14f);
    }

    void ApplyPlanetGrassScale(VegetationPainter painter, float placementScale = 1f)
    {
        float worldH = ResolvePlanetGrassWorldHeight(placementScale);
        painter.GrassHeight = worldH;
        painter.GrassWidth = Math.Clamp(worldH * 0.42f, 0.9f, 4f);
        painter.MinScale = Math.Max(1.15f, placementScale);
        painter.MaxScale = Math.Max(painter.MinScale + 0.2f, placementScale * 1.45f);
    }

    static bool IsImageAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".psd", StringComparison.OrdinalIgnoreCase);
    }

    static void DisablePlanetImportedTreeLod(GameObject go)
    {
        if (go.Behaviors.OfType<TreeLOD>().FirstOrDefault() is not TreeLOD treeLod)
            return;
        treeLod.Lod1Start = 0f;
        treeLod.Lod2Start = 0f;
        treeLod.ImpostorStart = 0f;
        treeLod.BillboardAtlas = null;
    }

    static bool TryReadGpuGrassToken(GameObject go, out int token)
    {
        token = 0;
        const string prefix = "GpuGrass_";
        var name = go?.Name;
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(name.AsSpan(prefix.Length), out token);
    }

    static bool HasMeshFilterDeep(GameObject g)
    {
        foreach (var b in g.Behaviors)
            if (b is MeshFilter mf && mf.Mesh != null)
                return true;
        foreach (var c in g.Children)
            if (HasMeshFilterDeep(c))
                return true;
        return false;
    }

    static void ApplyImportedTreeRenderSettingsRecursive(GameObject g)
    {
        foreach (var b in g.Behaviors)
        {
            if (b is MeshRenderer mr)
                mr.DoubleSided = true;
        }
        foreach (var c in g.Children)
            ApplyImportedTreeRenderSettingsRecursive(c);
    }

    static float EstimateHierarchyVerticalExtent(GameObject root)
    {
        float maxH = 0f;
        void Walk(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is MeshFilter mf && mf.Mesh != null)
                    maxH = MathF.Max(maxH, EstimateMeshVerticalExtent(mf.Mesh));
            }
            foreach (var c in node.Children)
                Walk(c);
        }
        Walk(root);
        return maxH;
    }

    static float EstimateHierarchyMeshExtent(GameObject root)
    {
        float maxE = 0f;
        void Walk(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is MeshFilter mf && mf.Mesh != null)
                    maxE = MathF.Max(maxE, EstimateMeshExtent(mf.Mesh));
            }
            foreach (var c in node.Children)
                Walk(c);
        }
        Walk(root);
        return maxE;
    }

    static bool TemplateHasVisual(ImportedTreeTemplate? tpl)
        => tpl != null && (tpl.Source != null || tpl.Mesh != null || !string.IsNullOrWhiteSpace(tpl.VisualHierarchyJson));

    static string TreeTemplateKey(string absModelPath, bool grassOrient)
        => Path.GetFullPath(absModelPath) + TreeTemplateCacheKeySuffix + (grassOrient ? "|zupy" : "");

    static bool TryGetReadyTreeTemplate(string absModelPath, out ImportedTreeTemplate tpl)
    {
        absModelPath = Path.GetFullPath(absModelPath);
        bool grassOrient = LooksLikeImportedGrassModel(absModelPath);
        string cacheKey = TreeTemplateKey(absModelPath, grassOrient);
        lock (s_treeTemplateLock)
        {
            if (s_treeTemplateCache.TryGetValue(cacheKey, out tpl!) && TemplateHasVisual(tpl))
                return true;
        }
        tpl = null!;
        return false;
    }

    static void RequestTreeTemplateLoad(string absModelPath)
    {
        if (string.IsNullOrWhiteSpace(absModelPath) || !File.Exists(absModelPath))
            return;
        absModelPath = Path.GetFullPath(absModelPath);
        if (IsExcessivelyHeavySourceModel(absModelPath))
            return;
        lock (s_treeTemplateLock)
        {
            bool grassOrient = LooksLikeImportedGrassModel(absModelPath);
            string cacheKey = TreeTemplateKey(absModelPath, grassOrient);
            if (s_treeTemplateCache.TryGetValue(cacheKey, out var existing))
            {
                if (TemplateHasVisual(existing))
                    return;
                s_treeTemplateCache.Remove(cacheKey);
            }
            if (!s_queuedTemplateLoads.Add(absModelPath))
                return;
        }
        s_pendingTemplateLoads.Enqueue(absModelPath);
        KickTemplateLoader();
    }

    static void KickTemplateLoader()
    {
        if (Interlocked.CompareExchange(ref s_templateLoaderRunning, 1, 0) != 0)
            return;
        Task.Run(TemplateLoaderLoop);
    }

    static void TemplateLoaderLoop()
    {
        try
        {
            while (s_pendingTemplateLoads.TryDequeue(out var path))
            {
                try { GetOrLoadTreeTemplate(path); }
                catch { /* import is best-effort; spawn retries next tick */ }
                lock (s_treeTemplateLock)
                    s_queuedTemplateLoads.Remove(path);
            }
        }
        finally
        {
            Interlocked.Exchange(ref s_templateLoaderRunning, 0);
            if (!s_pendingTemplateLoads.IsEmpty)
                KickTemplateLoader();
        }
    }

    void QueueProfileTemplateLoads()
    {
        foreach (var profile in _vegProfiles.Values)
        {
            if (profile == null) continue;
            QueueStoredModelLoad(profile.TreeModelPath);
            QueueItemModelLoads(profile.TreeItems);
        }
    }

    void QueuePlacementTemplateLoads()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int queued = 0;
        for (int t = 0; t < _treePlacementIdx.Count && queued < 8; t++)
        {
            int i = _treePlacementIdx[t];
            if ((uint)i >= (uint)_assetPlacements.Count) continue;
            var p = _assetPlacements[i];
            string path = ResolveTreeModelPathForPlacement(p, i);
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                continue;
            QueueStoredModelLoad(path);
            queued++;
        }
    }

    void QueueItemModelLoads(IEnumerable<VegetationProfileItem>? items)
    {
        if (items == null) return;
        foreach (var it in items)
        {
            if (it == null) continue;
            QueueStoredModelLoad(it.ModelPath);
        }
    }

    void QueueStoredModelLoad(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !IsSupportedModelPath(stored))
            return;
        string? abs = ResolveTreeModelAbsPath(stored);
        if (!string.IsNullOrWhiteSpace(abs))
            RequestTreeTemplateLoad(abs);
    }

    static ImportedTreeTemplate GetOrLoadTreeTemplate(string absModelPath)
    {
        absModelPath = Path.GetFullPath(absModelPath);
        bool grassOrient = LooksLikeImportedGrassModel(absModelPath);
        string cacheKey = absModelPath + TreeTemplateCacheKeySuffix + (grassOrient ? "|zupy" : "");
        lock (s_treeTemplateLock)
        {
            if (s_treeTemplateCache.TryGetValue(cacheKey, out var hit) && TemplateHasVisual(hit))
                return hit;
            s_treeTemplateCache.Remove(cacheKey);
        }

        var tpl = new ImportedTreeTemplate();
        if (IsExcessivelyHeavySourceModel(absModelPath))
            return tpl;

        try
        {
            var root = Importers.ModelImporter.ImportModel(absModelPath);
            if (!grassOrient)
                StripTreeBillboardMeshes(root);
            PreferBestLodPerMeshStem(root);
            EnsurePlanetImportedTreeCrustLocal(root, forceGrassZUp: false, isGrass: grassOrient);
            if (!grassOrient)
                FixSpeedTreeImportedTreeMaterials(root, absModelPath);
            ResolveImportedTreeMaterialsRecursive(root);
            int tris = CountHierarchyTriangles(root);
            if (tris <= 0)
                return tpl;

            // Keep the live baked hierarchy. JSON mesh DTOs omit UVs, which makes
            // alpha-tested pine needles disappear (GPU samples 0,0 = transparent).
            tpl.Source = root;
            tpl.MeshExtent = EstimateHierarchyVerticalExtent(root);

            var mf = FindFirstComponent<MeshFilter>(root);
            if (mf?.Mesh != null)
            {
                tpl.Mesh = mf.Mesh;
                if (tpl.MeshExtent < 1e-3f)
                    tpl.MeshExtent = EstimateMeshVerticalExtent(tpl.Mesh);
            }

            var mr = FindFirstComponent<MeshRenderer>(root);
            if (mr?.Material != null)
                tpl.Material = mr.Material;
            else if (mr != null && mr.MaterialPaths.Count > 0)
            {
                try { tpl.Material = ProjectService.MaterialsLoad(mr.MaterialPaths[0]); } catch { }
            }
        }
        catch { }

        if (!TemplateHasVisual(tpl))
            return tpl;

        lock (s_treeTemplateLock)
        {
            s_treeTemplateCache[cacheKey] = tpl;
            return tpl;
        }
    }

    static string? ResolveTreeModelAbsPath(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            if (Path.IsPathRooted(stored))
                return Path.GetFullPath(stored);
            var proj = ProjectService.Current;
            if (proj == null)
                return Path.GetFullPath(stored);
            string rel = stored.Replace('\\', '/');
            string fromRoot = Path.Combine(proj.RootPath, rel);
            if (File.Exists(fromRoot)) return fromRoot;
            if (!string.IsNullOrWhiteSpace(proj.AssetsPath))
            {
                string fromAssets = Path.Combine(proj.AssetsPath, rel);
                if (File.Exists(fromAssets)) return fromAssets;
            }
            return fromRoot;
        }
        catch { return null; }
    }

    static bool IsExcessivelyHeavySourceModel(string absPath)
    {
        try
        {
            var fi = new FileInfo(absPath);
            if (!fi.Exists) return true;
            if (fi.Length > MaxImportedTreeSourceBytes) return true;
            string n = absPath.Replace('\\', '/');
            return n.EndsWith("/Tree/Tree.obj", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("/Tree/Tree.fbx", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    static int CountMeshTriangles(Mesh? mesh)
    {
        if (mesh?.TriIndices == null) return 0;
        return mesh.TriIndices.Length / 3;
    }

    static int CountHierarchyTriangles(GameObject root)
    {
        int n = 0;
        void Walk(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is MeshFilter mf)
                    n += CountMeshTriangles(mf.Mesh);
            }
            foreach (var c in node.Children)
                Walk(c);
        }
        Walk(root);
        return n;
    }

    /// <summary>Lower rank = higher-quality mesh (LOD0 before LOD3).</summary>
    static int LodQualityRank(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        if (name.Contains("LOD3", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("LOD2", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("LOD1", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("LOD0", StringComparison.OrdinalIgnoreCase)) return 0;
        return -1;
    }

    /// <summary>Strip extra LOD meshes per stem; keep the highest-quality available (LOD0 before LOD3).</summary>
    static string LodStemKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        int idx = name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return name;
        return name.Substring(0, idx).TrimEnd('_', ' ');
    }

    static void PreferBestLodPerMeshStem(GameObject root)
    {
        if (root == null) return;

        var groups = new Dictionary<string, List<(GameObject go, int rank)>>(StringComparer.OrdinalIgnoreCase);
        void Collect(GameObject node)
        {
            int rank = LodQualityRank(node.Name);
            if (rank >= 0)
            {
                string stem = LodStemKey(node.Name);
                if (string.IsNullOrEmpty(stem))
                    stem = node.Name ?? "";
                if (!groups.TryGetValue(stem, out var list))
                {
                    list = new List<(GameObject, int)>();
                    groups[stem] = list;
                }
                list.Add((node, rank));
            }
            foreach (var c in node.Children)
                Collect(c);
        }
        Collect(root);
        if (groups.Count == 0) return;

        foreach (var list in groups.Values)
        {
            int want = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                int rank = list[i].rank;
                if (rank < want)
                    want = rank;
            }
            if (want == int.MaxValue)
                continue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].rank != want)
                    list[i].go.RemoveFromParent();
            }
        }
    }

    static bool IsTreeBillboardLodNodeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("billboard", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("impostor", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("imposter", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("LOD3", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void StripTreeBillboardMeshes(GameObject root)
    {
        if (root == null) return;
        void Walk(GameObject node)
        {
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (child == null) continue;
                if (IsTreeBillboardLodNodeName(child.Name))
                    child.RemoveFromParent();
                else
                    Walk(child);
            }
        }
        Walk(root);
    }

    static bool IsPaperThinBillboardMesh(Mesh? mesh)
    {
        var verts = mesh?.Vertices;
        if (verts == null || verts.Length < 3) return false;
        float minX = verts[0].X, maxX = verts[0].X;
        float minY = verts[0].Y, maxY = verts[0].Y;
        float minZ = verts[0].Z, maxZ = verts[0].Z;
        for (int i = 1; i < verts.Length; i++)
        {
            var v = verts[i];
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
            if (v.Z > maxZ) maxZ = v.Z;
        }
        float ex = MathF.Max(1e-5f, maxX - minX);
        float ey = MathF.Max(1e-5f, maxY - minY);
        float ez = MathF.Max(1e-5f, maxZ - minZ);
        float maxE = MathF.Max(ex, MathF.Max(ey, ez));
        float minE = MathF.Min(ex, MathF.Min(ey, ez));
        if (maxE < 1e-4f) return true;
        return minE / maxE < 0.07f;
    }

    static void StripPaperThinTreeMeshes(GameObject root)
    {
        if (root == null) return;
        void Walk(GameObject node)
        {
            bool stripCards = false;
            foreach (var b in node.Behaviors)
            {
                if (b is MeshFilter mf && IsPaperThinBillboardMesh(mf.Mesh))
                {
                    stripCards = true;
                    break;
                }
            }
            if (stripCards)
            {
                var remove = new List<Behavior>(4);
                foreach (var b in node.Behaviors)
                {
                    if (b is MeshFilter or MeshRenderer)
                        remove.Add(b);
                }
                for (int i = 0; i < remove.Count; i++)
                    node.RemoveBehavior(remove[i]);
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                Walk(child);
                bool hasMesh = false;
                foreach (var b in child.Behaviors)
                {
                    if (b is MeshFilter mf && mf.Mesh != null)
                    {
                        hasMesh = true;
                        break;
                    }
                }
                if (!hasMesh && child.Children.Count == 0 && child.Behaviors.Count == 0)
                    child.RemoveFromParent();
            }
        }
        Walk(root);
    }

    /// <summary>
    /// FBX node translations stay in source units after vertices are normalized to ~1.
    /// Baking into the root and putting feet at Y=0 keeps instances on the crust instead of in orbit.
    /// </summary>
    static void BakeImportedTreeToModelSpace(GameObject root, bool forceZUpToYUp = false, bool isGrass = false)
    {
        if (root == null) return;

        void BakeNode(GameObject node, SN.Matrix4x4 parentW)
        {
            var W = TransformUtil.WorldFromTransform(node.Transform) * parentW;
            foreach (var b in node.Behaviors)
            {
                if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                var verts = mf.Mesh.Vertices;
                var norms = mf.Mesh.Normals;
                SN.Matrix4x4.Invert(W, out var invW);
                var nMat = SN.Matrix4x4.Transpose(invW);
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = SN.Vector3.Transform(verts[i], W);
                if (norms != null)
                {
                    for (int i = 0; i < norms.Length; i++)
                    {
                        var n = SN.Vector3.TransformNormal(norms[i], nMat);
                        norms[i] = n.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
                    }
                }
            }
            foreach (var c in node.Children)
                BakeNode(c, W);
        }
        BakeNode(root, SN.Matrix4x4.Identity);

        void ResetXform(GameObject n)
        {
            n.Transform.Position = new Vector3(0, 0, 0);
            n.Transform.Rotation = new Vector3(0, 0, 0);
            n.Transform.Scale = new Vector3(1, 1, 1);
            foreach (var c in n.Children)
                ResetXform(c);
        }
        ResetXform(root);
        if (isGrass)
            ReorientGrassTuftToYUp(root);
        else
            ReorientImportedTreeMeshesToYUp(root, forceZUpToYUp);

        float minY = float.MaxValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        bool any = false;
        void Acc(GameObject node)
        {
            bool skip = !string.IsNullOrEmpty(node.Name) &&
                        node.Name.Contains("collider", StringComparison.OrdinalIgnoreCase);
            if (!skip)
            {
                foreach (var b in node.Behaviors)
                {
                    if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                    var verts = mf.Mesh.Vertices;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var v = verts[i];
                        any = true;
                        if (v.Y < minY) minY = v.Y;
                        if (v.X < minX) minX = v.X;
                        if (v.X > maxX) maxX = v.X;
                        if (v.Z < minZ) minZ = v.Z;
                        if (v.Z > maxZ) maxZ = v.Z;
                    }
                }
            }
            foreach (var c in node.Children)
                Acc(c);
        }
        Acc(root);
        if (!any) return;

        var shift = new SN.Vector3(-0.5f * (minX + maxX), -minY, -0.5f * (minZ + maxZ));
        void Shift(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                var verts = mf.Mesh.Vertices;
                for (int i = 0; i < verts.Length; i++)
                    verts[i] += shift;
            }
            foreach (var c in node.Children)
                Shift(c);
        }
        Shift(root);
    }

    /// <summary>
    /// Meadow FBX is Z-up (Unity prefab Rx(-90)). Tree "longest Z" reorient is wrong for squat
    /// clumps: width along Z looks like Z-up and flattens a Y-up tuft, or skips a real Z-up tuft.
    /// Height of a squat tuft is the smallest AABB axis — rotate only when that axis is Z.
    /// </summary>
    static void ReorientGrassTuftToYUp(GameObject root)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        bool any = false;
        void Acc(GameObject node)
        {
            bool skip = !string.IsNullOrEmpty(node.Name) &&
                        node.Name.Contains("collider", StringComparison.OrdinalIgnoreCase);
            if (!skip)
            {
                foreach (var b in node.Behaviors)
                {
                    if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                    var verts = mf.Mesh.Vertices;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var v = verts[i];
                        any = true;
                        if (v.X < minX) minX = v.X;
                        if (v.X > maxX) maxX = v.X;
                        if (v.Y < minY) minY = v.Y;
                        if (v.Y > maxY) maxY = v.Y;
                        if (v.Z < minZ) minZ = v.Z;
                        if (v.Z > maxZ) maxZ = v.Z;
                    }
                }
            }
            foreach (var c in node.Children)
                Acc(c);
        }
        Acc(root);
        if (!any) return;

        float ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
        // Already sitting on XZ (Y is height, even if squat) — but not if Z is clearly the trunk.
        if (ey <= ez && ey <= ex && ez < MathF.Max(ex, ey) * 1.12f)
            return;
        // Paper-thin XY cards: already Y-up billboards (Z is thickness).
        float footprint = MathF.Max(ex, ey);
        if (ez < footprint * 0.18f)
            return;
        // Height along Z (Megascans / Unity Z-up) — same Rx(-90) as the Unity prefab.
        if (ez >= ey && ez >= ex)
            RotateImportedVertsAroundX(root, -MathF.PI * 0.5f);
    }

    static void RotateImportedVertsAroundX(GameObject root, float radians)
    {
        var rot = SN.Matrix4x4.CreateRotationX(radians);
        SN.Matrix4x4.Invert(rot, out var invRot);
        var nRot = SN.Matrix4x4.Transpose(invRot);
        void Rot(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                var verts = mf.Mesh.Vertices;
                var norms = mf.Mesh.Normals;
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = SN.Vector3.Transform(verts[i], rot);
                if (norms != null)
                {
                    for (int i = 0; i < norms.Length; i++)
                    {
                        var n = SN.Vector3.TransformNormal(norms[i], nRot);
                        norms[i] = n.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
                    }
                }
            }
            foreach (var c in node.Children)
                Rot(c);
        }
        Rot(root);
    }

    static void RotateImportedVertsAroundZ(GameObject root, float radians)
    {
        var rot = SN.Matrix4x4.CreateRotationZ(radians);
        SN.Matrix4x4.Invert(rot, out var invRot);
        var nRot = SN.Matrix4x4.Transpose(invRot);
        void Rot(GameObject node)
        {
            foreach (var b in node.Behaviors)
            {
                if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                var verts = mf.Mesh.Vertices;
                var norms = mf.Mesh.Normals;
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = SN.Vector3.Transform(verts[i], rot);
                if (norms != null)
                {
                    for (int i = 0; i < norms.Length; i++)
                    {
                        var n = SN.Vector3.TransformNormal(norms[i], nRot);
                        norms[i] = n.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
                    }
                }
            }
            foreach (var c in node.Children)
                Rot(c);
        }
        Rot(root);
    }

    /// <summary>
    /// Planet spawn assumes baked local +Y is the trunk. Unity/Megascans FBX is often Z-up
    /// (sometimes X-up). Pick the single 90° rotation that makes the AABB tallest in Y.
    /// </summary>
    static void ReorientImportedTreeMeshesToYUp(GameObject root, bool forceZUpToYUp = false)
    {
        _ = forceZUpToYUp;
        if (!TryMeasureImportedAabb(root, out float ex, out float ey, out float ez))
            return;

        // Already standing: Y is the unique longest axis.
        if (ey >= ex && ey >= ez)
            return;

        // Z-up (Unity vegetation): Rx(-90) maps +Z → +Y.
        if (ez >= ex && ez > ey)
        {
            RotateImportedVertsAroundX(root, -MathF.PI * 0.5f);
            return;
        }

        // X-up: Rz(+90) maps +X → +Y.
        if (ex > ey && ex > ez)
            RotateImportedVertsAroundZ(root, MathF.PI * 0.5f);
    }

    static bool TryMeasureImportedAabb(GameObject root, out float ex, out float ey, out float ez)
    {
        ex = ey = ez = 0f;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        bool any = false;

        void Acc(GameObject node)
        {
            bool skip = !string.IsNullOrEmpty(node.Name) &&
                        node.Name.Contains("collider", StringComparison.OrdinalIgnoreCase);
            if (!skip)
            {
                foreach (var b in node.Behaviors)
                {
                    if (b is not MeshFilter mf || mf.Mesh?.Vertices == null) continue;
                    var verts = mf.Mesh.Vertices;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var v = verts[i];
                        any = true;
                        if (v.X < minX) minX = v.X;
                        if (v.X > maxX) maxX = v.X;
                        if (v.Y < minY) minY = v.Y;
                        if (v.Y > maxY) maxY = v.Y;
                        if (v.Z < minZ) minZ = v.Z;
                        if (v.Z > maxZ) maxZ = v.Z;
                    }
                }
            }
            foreach (var c in node.Children)
                Acc(c);
        }
        Acc(root);
        if (!any) return false;
        ex = maxX - minX;
        ey = maxY - minY;
        ez = maxZ - minZ;
        return true;
    }

    static void EnsurePlanetImportedTreeCrustLocal(GameObject root, bool forceGrassZUp = false, bool isGrass = false)
    {
        if (root == null) return;
        ClearImportedModelPathRecursive(root);
        BakeImportedTreeToModelSpace(root, forceGrassZUp, isGrass);
        CollapseImportedTreeToRoot(root);
    }

    static bool LooksLikeImportedGrassModel(string path) => VegetationProfileLibrary.IsGrassVegetationAsset(path);

    /// <summary>
    /// Merge imported FBX submeshes onto the spawn root with identity transforms so
    /// only the crust anchor moves the tree (no Assimp child translations in orbit).
    /// </summary>
    static void CollapseImportedTreeToRoot(GameObject root)
    {
        var parts = new List<(Mesh mesh, MeshRenderer? srcMr)>();

        void Collect(GameObject node)
        {
            bool skip = !string.IsNullOrEmpty(node.Name) &&
                        node.Name.Contains("collider", StringComparison.OrdinalIgnoreCase);
            if (!skip)
            {
                MeshFilter? mf = null;
                MeshRenderer? mr = null;
                foreach (var b in node.Behaviors)
                {
                    if (b is MeshFilter f) mf = f;
                    else if (b is MeshRenderer r) mr = r;
                }
                if (mf?.Mesh != null)
                    parts.Add((mf.Mesh, mr));
            }

            var ch = node.Children;
            for (int i = 0; i < ch.Count; i++)
                Collect(ch[i]);
        }
        Collect(root);

        while (root.Children.Count > 0)
            root.Children[0].RemoveFromParent();

        var remove = new List<Behavior>();
        foreach (var b in root.Behaviors)
        {
            if (b is MeshFilter or MeshRenderer)
                remove.Add(b);
        }
        for (int i = 0; i < remove.Count; i++)
            root.RemoveBehavior(remove[i]);

        for (int i = 0; i < parts.Count; i++)
        {
            var (mesh, srcMr) = parts[i];
            var mf = new MeshFilter { Mesh = mesh, ModelPath = "" };
            var mr = new MeshRenderer { DoubleSided = srcMr?.DoubleSided ?? true };
            if (srcMr?.Material != null)
                mr.Material = srcMr.Material;
            else if (srcMr != null && srcMr.MaterialPaths.Count > 0)
            {
                foreach (var p in srcMr.MaterialPaths)
                    mr.MaterialPaths.Add(p);
                try { mr.ResolveMaterials(); } catch { }
            }
            root.AddBehavior(mf);
            root.AddBehavior(mr);
        }

        root.Transform.Position = new Vector3(0, 0, 0);
        root.Transform.Rotation = new Vector3(0, 0, 0);
        root.Transform.Scale = new Vector3(1, 1, 1);
    }

    static void ClearImportedModelPathRecursive(GameObject node)
    {
        foreach (var b in node.Behaviors)
        {
            if (b is MeshFilter mf)
                mf.ModelPath = "";
        }
        foreach (var c in node.Children)
            ClearImportedModelPathRecursive(c);
    }

    static T? FindFirstComponent<T>(GameObject go) where T : Behavior
    {
        foreach (var b in go.Behaviors)
            if (b is T t) return t;
        foreach (var c in go.Children)
        {
            var t = FindFirstComponent<T>(c);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>
    /// Estimates terrain normal in <strong>world space</strong> from height samples (planet-local directions, same as meshes).
    /// </summary>
    static SN.Vector3 SamplePlanetSurfaceNormal(PlanetTerrain planet, SN.Matrix4x4 planetWorld, SN.Vector3 dir)
    {
        if (dir.LengthSquared() < 1e-8f)
            return LocalDirectionToWorld(planetWorld, SN.Vector3.UnitY);
        dir = SN.Vector3.Normalize(dir);

        var t = SN.Vector3.Cross(MathF.Abs(dir.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, dir);
        if (t.LengthSquared() < 1e-8f)
            t = SN.Vector3.UnitX;
        t = SN.Vector3.Normalize(t);
        var b = SN.Vector3.Normalize(SN.Vector3.Cross(dir, t));

        const float eps = 0.0018f;
        var dirT = SN.Vector3.Normalize(dir + t * eps);
        var dirB = SN.Vector3.Normalize(dir + b * eps);

        var p0 = planet.SampleVegetationAnchorLocal(dir);
        var pT = planet.SampleVegetationAnchorLocal(dirT);
        var pB = planet.SampleVegetationAnchorLocal(dirB);

        var p0w = SN.Vector3.Transform(p0, planetWorld);
        var pTw = SN.Vector3.Transform(pT, planetWorld);
        var pBw = SN.Vector3.Transform(pB, planetWorld);

        var n = SN.Vector3.Cross(pTw - p0w, pBw - p0w);
        if (n.LengthSquared() < 1e-8f)
            return LocalDirectionToWorld(planetWorld, dir);
        n = SN.Vector3.Normalize(n);
        var center = new SN.Vector3(planetWorld.M41, planetWorld.M42, planetWorld.M43);
        var outward = SafeNormalize(p0w - center, n);
        if (SN.Vector3.Dot(n, outward) < 0f)
            n = -n;
        return n;
    }

    static SN.Vector3 BlendRadialWithSurfaceNormal(SN.Vector3 radialDir, SN.Vector3 surfaceNormal, float t)
    {
        radialDir = SafeNormalize(radialDir, SN.Vector3.UnitY);
        surfaceNormal = SafeNormalize(surfaceNormal, radialDir);
        if (SN.Vector3.Dot(surfaceNormal, radialDir) < 0f)
            surfaceNormal = -surfaceNormal;
        var blended = SN.Vector3.Lerp(radialDir, surfaceNormal, Math.Clamp(t, 0f, 1f));
        return SafeNormalize(blended, radialDir);
    }

    /// <summary>
    /// Tree trunk “up” in world space: follows slope on gentle ground, falls back toward radial on cliffs where
    /// <c>radial ⟂ sampled normal</c> would otherwise produce a nearly horizontal blend.
    /// </summary>
    SN.Vector3 ResolvePlanetTreeWorldUp(SN.Vector3 radialW, SN.Vector3 surfNW)
    {
        radialW = SafeNormalize(radialW, SN.Vector3.UnitY);
        surfNW = SafeNormalize(surfNW, radialW);
        if (SN.Vector3.Dot(surfNW, radialW) < 0f)
            surfNW = -surfNW;

        float align = Math.Clamp(SN.Vector3.Dot(radialW, surfNW), 0f, 1f);
        // Squared: steep faces (low align) stay mostly radial; flat areas keep full blend strength.
        float slopeFollow = align * align;
        float t = Math.Clamp(TreeSurfaceNormalBlend, 0f, 1f) * slopeFollow;
        var blended = BlendRadialWithSurfaceNormal(radialW, surfNW, t);

        float maxTilt = Math.Clamp(TreeMaxTiltFromRadialDegrees, 4f, 62f);
        var up = ClampUpToMaxTiltFromRadial(radialW, blended, maxTilt);
        // Never grow into the rock: trunk “up” must stay in the same hemisphere as planet outward.
        if (SN.Vector3.Dot(up, radialW) < 0f)
            up = -up;
        return up;
    }

    /// <summary>
    /// Wide grass tufts must sit on the slope. Trees stay radial on cliffs; grass follows the
    /// sampled normal more as the slope steepens so clumps don't hover off the face.
    /// </summary>
    SN.Vector3 ResolvePlanetGrassWorldUp(SN.Vector3 radialW, SN.Vector3 surfNW)
    {
        radialW = SafeNormalize(radialW, SN.Vector3.UnitY);
        surfNW = SafeNormalize(surfNW, radialW);
        if (SN.Vector3.Dot(surfNW, radialW) < 0f)
            surfNW = -surfNW;

        float align = Math.Clamp(SN.Vector3.Dot(radialW, surfNW), 0f, 1f);
        // Follow the hillside, not planet radial — radial-up looks "standing vertical"
        // on coastal slopes and leaves the downhill side hovering.
        float t = Math.Clamp(0.88f + (1f - align) * 0.12f, 0.82f, 1f);
        var blended = BlendRadialWithSurfaceNormal(radialW, surfNW, t);
        var up = ClampUpToMaxTiltFromRadial(radialW, blended, 88f);
        if (SN.Vector3.Dot(up, radialW) < 0f)
            up = -up;
        return up;
    }

    static SN.Vector3 SlerpUnitVectors(SN.Vector3 a, SN.Vector3 b, float t)
    {
        a = SN.Vector3.Normalize(a);
        b = SN.Vector3.Normalize(b);
        t = Math.Clamp(t, 0f, 1f);
        float dot = Math.Clamp(SN.Vector3.Dot(a, b), -1f, 1f);
        float omega = MathF.Acos(dot);
        if (omega < 1e-4f || float.IsNaN(omega))
            return SafeNormalize(SN.Vector3.Lerp(a, b, t), a);
        float so = MathF.Sin(omega);
        float s0 = MathF.Sin((1f - t) * omega) / so;
        float s1 = MathF.Sin(t * omega) / so;
        return SafeNormalize(a * s0 + b * s1, a);
    }

    /// <summary>Constrains <paramref name="up"/> so its angle from <paramref name="radial"/> is at most <paramref name="maxTiltDeg"/>.</summary>
    static SN.Vector3 ClampUpToMaxTiltFromRadial(SN.Vector3 radial, SN.Vector3 up, float maxTiltDeg)
    {
        radial = SN.Vector3.Normalize(radial);
        up = SN.Vector3.Normalize(up);
        float d = Math.Clamp(SN.Vector3.Dot(up, radial), -1f, 1f);
        float ang = MathF.Acos(d);
        float maxR = maxTiltDeg * (MathF.PI / 180f);
        if (ang <= maxR || float.IsNaN(ang))
            return up;
        float u = maxR / ang;
        return SlerpUnitVectors(radial, up, u);
    }

    /// <summary>
    /// Orients local +Y along planet trunk-up using <see cref="TransformUtil.AlignLocalUp"/> — the same
    /// explicit rotation matrix path used by the player capsule on planets (stable on slopes, no Euler drift).
    /// </summary>
    static void SetSurfaceAlignedRotation(GameObject go, SN.Vector3 trunkUpWorld, float yawDeg)
    {
        var forwardHint = BuildTreeForwardHint(trunkUpWorld, yawDeg);
        SN.Vector3 up = trunkUpWorld;
        SN.Vector3 fwd = forwardHint;
        if (go.Parent != null)
        {
            var pw = SceneGraphUtil.AccumulateWorld(go.Parent);
            if (SN.Matrix4x4.Invert(pw, out var invPw))
            {
                up = SN.Vector3.TransformNormal(trunkUpWorld, invPw);
                fwd = SN.Vector3.TransformNormal(forwardHint, invPw);
            }
        }
        TransformUtil.AlignLocalUp(go.Transform, up, fwd);
    }

    static SN.Vector3 BuildTreeForwardHint(SN.Vector3 trunkUpWorld, float yawDeg)
    {
        var worldUp = SafeNormalize(trunkUpWorld, SN.Vector3.UnitY);
        var refAxis = ReferenceAxisLeastAligned(worldUp);
        var side = SN.Vector3.Cross(refAxis, worldUp);
        if (side.LengthSquared() < 1e-8f)
        {
            refAxis = MathF.Abs(refAxis.X) > 0.5f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
            side = SN.Vector3.Cross(refAxis, worldUp);
        }
        side = SN.Vector3.Normalize(side);
        var baseFwd = SN.Vector3.Normalize(SN.Vector3.Cross(worldUp, side));
        float yawRad = yawDeg * (MathF.PI / 180f);
        return SN.Vector3.Normalize(side * MathF.Cos(yawRad) + baseFwd * MathF.Sin(yawRad));
    }

    /// <summary>
    /// Legacy euler helper — prefer <see cref="SetSurfaceAlignedRotation"/>.
    /// </summary>
    static Vector3 SurfaceAlignedRotation(GameObject go, SN.Vector3 trunkUpWorld, float yawDeg)
    {
        var colBasis = BuildTreeAlignMatrixColumnBasis(trunkUpWorld, yawDeg);
        var mRow = SN.Matrix4x4.Transpose(colBasis);

        if (go.Parent == null)
            return TransformUtil.EulerDegreesFromRotationMatrix3x3(mRow);

        var pw = SceneGraphUtil.AccumulateWorld(go.Parent);
        var invParentRot = InvertRotation3x3(ExtractRotation3x3(pw));
        var localCombined = mRow * invParentRot;
        var rotOnly = OrthonormalizeRotationPart(localCombined);
        return TransformUtil.EulerDegreesFromRotationMatrix3x3(rotOnly);
    }

    /// <summary>Right-handed column basis: local +X, +Y (trunk up), +Z as world axes (column-vector math).</summary>
    static SN.Matrix4x4 BuildTreeAlignMatrixColumnBasis(SN.Vector3 trunkUpWorld, float yawDeg)
    {
        var worldUp = SafeNormalize(trunkUpWorld, SN.Vector3.UnitY);
        var refAxis = ReferenceAxisLeastAligned(worldUp);
        var side = SN.Vector3.Cross(refAxis, worldUp);
        if (side.LengthSquared() < 1e-8f)
        {
            refAxis = MathF.Abs(refAxis.X) > 0.5f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
            side = SN.Vector3.Cross(refAxis, worldUp);
        }
        side = SN.Vector3.Normalize(side);
        var fwd = SN.Vector3.Normalize(SN.Vector3.Cross(worldUp, side));
        float yawRad = yawDeg * (MathF.PI / 180f);
        float cy = MathF.Cos(yawRad), sy = MathF.Sin(yawRad);
        var xAxis = side * cy + fwd * sy;
        var zAxis = SN.Vector3.Normalize(SN.Vector3.Cross(xAxis, worldUp));

        return new SN.Matrix4x4(
            xAxis.X, worldUp.X, zAxis.X, 0f,
            xAxis.Y, worldUp.Y, zAxis.Y, 0f,
            xAxis.Z, worldUp.Z, zAxis.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    static SN.Vector3 ReferenceAxisLeastAligned(SN.Vector3 v)
    {
        v = SN.Vector3.Normalize(v);
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        if (ax <= ay && ax <= az) return SN.Vector3.UnitX;
        if (ay <= az) return SN.Vector3.UnitY;
        return SN.Vector3.UnitZ;
    }

    static SN.Matrix4x4 ExtractRotation3x3(SN.Matrix4x4 world)
    {
        var c0 = new SN.Vector3(world.M11, world.M21, world.M31);
        var c1 = new SN.Vector3(world.M12, world.M22, world.M32);
        var c2 = new SN.Vector3(world.M13, world.M23, world.M33);
        float l0 = c0.Length();
        float l1 = c1.Length();
        float l2 = c2.Length();
        if (l0 > 1e-6f) c0 /= l0;
        if (l1 > 1e-6f) c1 /= l1;
        if (l2 > 1e-6f) c2 /= l2;
        return OrthonormalizeRotationPart(new SN.Matrix4x4(
            c0.X, c1.X, c2.X, 0f,
            c0.Y, c1.Y, c2.Y, 0f,
            c0.Z, c1.Z, c2.Z, 0f,
            0f, 0f, 0f, 1f));
    }

    static SN.Matrix4x4 InvertRotation3x3(SN.Matrix4x4 rotation)
        => SN.Matrix4x4.Transpose(OrthonormalizeRotationPart(rotation));

    static SN.Vector3 RadialDirFromLocalPosition(SN.Vector3 localPos)
        => SafeNormalize(localPos, SN.Vector3.UnitY);

    static float SpawnYawFromLeaf(QuadNode leaf, int seedOffset)
        => Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;

    static SN.Matrix4x4 OrthonormalizeRotationPart(in SN.Matrix4x4 m)
    {
        var c0 = new SN.Vector3(m.M11, m.M21, m.M31);
        var c1 = new SN.Vector3(m.M12, m.M22, m.M32);
        var c2 = new SN.Vector3(m.M13, m.M23, m.M33);
        if (c0.LengthSquared() < 1e-10f)
            return SN.Matrix4x4.Identity;
        c0 = SN.Vector3.Normalize(c0);
        c1 -= c0 * SN.Vector3.Dot(c1, c0);
        if (c1.LengthSquared() < 1e-10f)
            c1 = SN.Vector3.Normalize(SN.Vector3.Cross(SN.Vector3.UnitY, c0));
        else
            c1 = SN.Vector3.Normalize(c1);
        c2 = SN.Vector3.Normalize(SN.Vector3.Cross(c0, c1));
        return new SN.Matrix4x4(
            c0.X, c1.X, c2.X, 0f,
            c0.Y, c1.Y, c2.Y, 0f,
            c0.Z, c1.Z, c2.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
    {
        float lsq = v.LengthSquared();
        if (lsq < 1e-8f) return fallback;
        return v / MathF.Sqrt(lsq);
    }

    BiomeDefinition ResolveLandVegetationBiome(BiomeDefinition sampled, SN.Vector3 dir)
    {
        if (_terrain?.Config == null) return sampled;
        if (!ShouldSpawnVegetationAt(dir))
            return sampled;

        float veg = Math.Max(sampled.VegetationDensity, 2f);
        float trees = Math.Max(sampled.TreeDensity, sampled.Name.Equals("Ocean", StringComparison.OrdinalIgnoreCase) ? 0f : 1f);
        if (UseUniversalLandVegetation && !string.IsNullOrWhiteSpace(UniversalVegetationProfileId))
        {
            return new BiomeDefinition
            {
                Name = sampled.Name,
                BiomeIndex = sampled.BiomeIndex,
                VegetationDensity = veg,
                TreeDensity = trees,
                VegetationProfileId = UniversalVegetationProfileId,
                GrassMinScale = sampled.GrassMinScale,
                GrassMaxScale = sampled.GrassMaxScale,
                TreeMinScale = sampled.TreeMinScale,
                TreeMaxScale = sampled.TreeMaxScale,
            };
        }

        if (sampled.VegetationDensity > 0.05f || sampled.TreeDensity > 0.05f)
            return sampled;
        return ResolveBiomeByName("Grassland")
            ?? ResolveBiomeByName("Forest")
            ?? sampled;
    }

    bool IsWaterPlanet()
    {
        if (_terrain == null || !_terrain.EnableWater || _terrain.Config == null)
            return false;
        return _terrain.SeaLevelFraction >= Math.Clamp(WaterPlanetSeaLevelFraction, 0.5f, 0.995f);
    }

    bool ShouldSpawnVegetationAt(SN.Vector3 dir)
    {
        if (_terrain?.Config == null) return true;
        if (!_terrain.EnableWater) return true;
        return IsDryLandForVegetation(dir);
    }

    bool IsDryLandForVegetation(SN.Vector3 dir)
    {
        if (_terrain?.Config == null) return true;
        if (!_terrain.EnableWater) return true;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);

        float crust = _terrain.WorldToLocalLength(_terrain.SampleHeightfieldRadius(dir));
        if (crust < 1e-3f)
            crust = _terrain.SampleVegetationAnchorLocal(dir).Length();

        var water = _terrain.SampleWaterSurface(dir);
        const float shorePad = 0.20f;
        // Trust the water mask for coasts. A global ocean-fill sphere clips dry hillside
        // benches that sit slightly below fill radius but above the actual shoreline.
        if (water.Mask >= 0.18f && water.Kind != PlanetWaterKind.Lava && crust < water.Radius - shorePad)
            return false;

        return IsAboveSea(dir);
    }

    bool LeafHasAnyDryLand(QuadNode leaf)
    {
        for (int i = 0; i < 9; i++)
        {
            if (ShouldSpawnVegetationAt(OffsetPatchDirection(leaf, i, 9)))
                return true;
        }
        return false;
    }

    void PopulateStoredPlacementsFromGrid()
    {
        if (_terrain?.Config == null) return;
        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();

        int cells = Math.Clamp(VegetationCellsPerFaceEdge, 8, 128);
        s_vegetationCellsPerFaceEdge = cells;
        int grassPerCell = Math.Max(6, MaxGrassClumpsPerLeaf);
        int treePerCell = Math.Max(1, MaxTreesPerLeaf / 2);
        int cap = Math.Max(256, MaxStoredPlacements);

        for (int face = 0; face < 6; face++)
        {
            for (int iu = 0; iu < cells; iu++)
            {
                for (int iv = 0; iv < cells; iv++)
                {
                    if (_assetPlacements.Count >= cap)
                        return;

                    var dir = StableCellCenterDirection(face, iu, iv);
                    int salt = face * 73856093 ^ iu * 19349663 ^ iv * 83492791;
                    if (!ShouldSpawnVegetationAt(dir))
                    {
                        dir = JitterCellDirection(face, iu, iv, cells, salt + 11);
                        if (!ShouldSpawnVegetationAt(dir))
                            continue;
                    }

                    var biome = SampleBiomeAtDir(dir) ?? _terrain.OceanBiome;
                    if (biome.SpawnWater && string.Equals(biome.Name, "Ocean", StringComparison.OrdinalIgnoreCase))
                        continue;
                    biome = ResolveLandVegetationBiome(biome, dir);
                    var profile = ResolveVegetationProfile(biome);
                    bool hasGrass = profile?.GrassItems?.Any(it => it != null && it.Weight > 0f) == true;
                    bool hasTrees = GetUsableTreeItems(profile).Count > 0;
                    if (!hasGrass && !hasTrees)
                        continue;

                    if (hasGrass)
                    {
                        for (int g = 0; g < grassPerCell && _assetPlacements.Count < cap; g++)
                        {
                            var placeDir = JitterCellDirection(face, iu, iv, cells, salt + 97 + g * 19);
                            var p = BuildPlacementAtDirection(placeDir, biome, isGrass: true, salt + g * 19, profile);
                            if (p == null) continue;
                            _assetPlacements.Add(p);
                            RegisterAssetPlacementAt(_assetPlacements.Count - 1);
                        }
                    }

                    if (hasTrees && ShouldSpawnVegetationAt(dir) && PassesGrowthAndPatchiness(biome, dir, isTree: true))
                    {
                        for (int t = 0; t < treePerCell && _assetPlacements.Count < cap; t++)
                        {
                            var placeDir = JitterCellDirection(face, iu, iv, cells, salt + 221 + t * 31);
                            var p = BuildPlacementAtDirection(placeDir, biome, isGrass: false, salt + 40 + t * 31, profile);
                            if (p == null || !PlacementHasUsableTreeAsset(p, _assetPlacements.Count))
                                continue;
                            _assetPlacements.Add(p);
                            RegisterAssetPlacementAt(_assetPlacements.Count - 1);
                        }
                    }
                }
            }
        }
    }

    SN.Vector3 JitterCellDirection(int face, int iu, int iv, int cells, int salt)
    {
        float u = (iu + 0.15f + Random01(salt + 3) * 0.7f) / cells;
        float v = (iv + 0.15f + Random01(salt + 9) * 0.7f) / cells;
        return CubeSphereMath.FaceUVToDirection(face, Math.Clamp(u, 0f, 1f), Math.Clamp(v, 0f, 1f));
    }

    BiomeDefinition? SampleBiomeAtDir(SN.Vector3 dir)
    {
        dir = SafeNormalize(dir, SN.Vector3.UnitY);
        float height = 0f;
        if (_terrain!.Config != null)
        {
            float r = _terrain.WorldToLocalLength(_terrain.SampleHeightfieldRadius(dir));
            height = r - _terrain.Config.Radius;
        }
        float alt = _terrain.Map?.NormalizeAltitude(height) ?? -1f;
        return _terrain.Map?.GetDominantBiome(dir, alt);
    }

    PlanetVegetationPlacement? BuildPlacementAtDirection(
        SN.Vector3 dir,
        BiomeDefinition biome,
        bool isGrass,
        int seedOffset,
        VegetationProfile? profile)
    {
        dir = SafeNormalize(dir, SN.Vector3.UnitY);
        if (!ShouldSpawnVegetationAt(dir))
            return null;
        if (!PassesGrowthAndPatchiness(biome, dir, isTree: !isGrass) && isGrass)
        {
            // Grass still belongs on dry land even if patch noise would thin it.
        }
        else if (!isGrass && !PassesGrowthAndPatchiness(biome, dir, isTree: true))
            return null;

        float yawDeg = Random01(seedOffset * 53) * 360f;
        var item = ChooseItem(profile, isGrass, seedOffset * 197);
        string prefabPath = item?.PrefabPath ?? "";
        string modelPath = "";
        string texturePath = "";
        if (item != null && !string.IsNullOrWhiteSpace(item.ModelPath))
        {
            if (IsPrefabPath(item.ModelPath) && string.IsNullOrWhiteSpace(prefabPath))
                prefabPath = item.ModelPath;
            else if (isGrass && IsImageAssetPath(item.ModelPath))
                texturePath = item.ModelPath;
            else if (isGrass && !IsSupportedModelPath(item.ModelPath) && !IsPrefabPath(item.ModelPath))
                modelPath = DefaultPlanetGrassModelPath;
            else
                modelPath = item.ModelPath;
        }
        if (isGrass && string.IsNullOrWhiteSpace(texturePath) && string.IsNullOrWhiteSpace(modelPath))
            texturePath = DefaultPlanetGrassTexturePath;

        float minScale = isGrass ? biome.GrassMinScale : biome.TreeMinScale;
        float maxScale = isGrass ? biome.GrassMaxScale : biome.TreeMaxScale;
        if (item != null)
        {
            minScale *= item.MinScale;
            maxScale *= item.MaxScale;
        }
        if (maxScale < minScale) (maxScale, minScale) = (minScale, maxScale);
        float scale = minScale + (maxScale - minScale) * Random01(seedOffset * 97);

        return new PlanetVegetationPlacement
        {
            IsGrass = isGrass,
            BiomeName = biome.Name ?? "",
            PrefabPath = PlanetAssetIO.NormalizeAssetReference(prefabPath),
            ModelPath = PlanetAssetIO.NormalizeAssetReference(modelPath),
            TexturePath = PlanetAssetIO.NormalizeAssetReference(texturePath),
            DirX = dir.X,
            DirY = dir.Y,
            DirZ = dir.Z,
            Scale = Math.Max(0.01f, scale),
            YawDeg = yawDeg
        };
    }

    void PruneSubmergedAssetPlacements()
    {
        if (_prunedWetPlacements || _terrain?.Config == null || _assetPlacements.Count == 0)
            return;
        // Walking 50k water samples at play start freezes the game.
        if (_assetPlacements.Count > 4000)
        {
            _prunedWetPlacements = true;
            return;
        }
        if (_terrain.Map == null && _terrain.EnableWater)
            return;
        _prunedWetPlacements = true;
        int kept = 0;
        for (int i = 0; i < _assetPlacements.Count; i++)
        {
            var p = _assetPlacements[i];
            var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
            if (!ShouldSpawnVegetationAt(dir))
                continue;
            if (kept != i)
                _assetPlacements[kept] = p;
            kept++;
        }
        if (kept == _assetPlacements.Count)
        {
            RebuildAssetPlacementAccel();
            return;
        }
        if (kept < _assetPlacements.Count)
            _assetPlacements.RemoveRange(kept, _assetPlacements.Count - kept);
        ClearLocalCarpetTrees();
        _carpetDirs.Clear();
        PlanetGpuGrass.ClearOwner(this);
        _assetActive.Clear();
        RebuildAssetPlacementAccel();
    }

    /// <summary>
    /// Honors VegetationPatchiness (clustered noise threshold), growth temp/moisture,
    /// and for trees slope + altitude treeline reject.
    /// </summary>
    bool PassesGrowthAndPatchiness(BiomeDefinition biome, SN.Vector3 dir, bool isTree)
    {
        if (_terrain == null) return true;

        float patchiness = Math.Clamp(biome.VegetationPatchiness, 0f, 1f);
        // Grass should carpet dry land. Keep only a light scatter so fields stay ~90% covered.
        float threshold = isTree ? patchiness * 0.55f : patchiness * 0.08f;
        float n = PlanetLifeStreaming.PatchNoise01(dir, isTree ? 917 : 311);
        if (n < threshold)
            return false;

        if (_terrain.Map != null)
        {
            float temp = _terrain.Map.GetTemperature(dir);
            float moist = _terrain.Map.GetMoisture(dir);
            float tMin = Math.Min(biome.GrowthTemperatureMin, biome.GrowthTemperatureMax);
            float tMax = Math.Max(biome.GrowthTemperatureMin, biome.GrowthTemperatureMax);
            float mMin = Math.Min(biome.GrowthMoistureMin, biome.GrowthMoistureMax);
            float mMax = Math.Max(biome.GrowthMoistureMin, biome.GrowthMoistureMax);
            if (temp < tMin || temp > tMax) return false;
            if (moist < mMin || moist > mMax) return false;
        }

        if (!isTree) return true;

        var planetW = GetPlanetWorldMatrix();
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        float align = Math.Clamp(SN.Vector3.Dot(SafeNormalize(surfN, radialW), SafeNormalize(radialW, SN.Vector3.UnitY)), -1f, 1f);
        float slopeDeg = MathF.Acos(align) * (180f / MathF.PI);
        float minSlope = biome.TreeMinSlope;
        float maxSlope = biome.TreeMaxSlope > 0f ? biome.TreeMaxSlope : 35f;
        if (slopeDeg < minSlope || slopeDeg > maxSlope)
            return false;

        float sea = _terrain.Config?.SeaLevel ?? 0.35f;
        float surf = _terrain.WorldToLocalLength(_terrain.SampleHeightfieldRadius(SafeNormalize(dir, SN.Vector3.UnitY)));
        float amp = MathF.Max(1f, _terrain.Config?.Biomes?.Length > 0
            ? MathF.Max(1f, biome.HeightAmplitude)
            : 50f);
        float alt01 = Math.Clamp((surf - sea) / amp, 0f, 1f);
        float minAlt = biome.TreeMinAltitude;
        float maxAlt = biome.TreeMaxAltitude > 0f ? biome.TreeMaxAltitude : 0.85f;
        if (alt01 < minAlt || alt01 > maxAlt)
            return false;

        return true;
    }

    bool IsAboveSea(SN.Vector3 dir)
    {
        if (_terrain?.Config == null) return true;
        float sea = _terrain.Config.SeaLevel;
        var n = SafeNormalize(dir, SN.Vector3.UnitY);
        float surf = _terrain.WorldToLocalLength(_terrain.SampleHeightfieldRadius(n));
        return surf >= sea + 0.12f;
    }

    BiomeDefinition? SampleLeafBiome(QuadNode leaf, int seedOffset)
    {
        float u = Random01(HashLeaf(leaf, 101 + seedOffset));
        float v = Random01(HashLeaf(leaf, 131 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        float height = 0f;
        if (_terrain!.Config != null)
        {
            float r = _terrain.WorldToLocalLength(_terrain.SampleHeightfieldRadius(dir));
            height = r - _terrain.Config.Radius;
        }
        float alt = _terrain.Map?.NormalizeAltitude(height) ?? -1f;
        return _terrain.Map?.GetDominantBiome(dir, alt);
    }

    VegetationProfile? ResolveVegetationProfile(BiomeDefinition biome)
    {
        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();
        if (UseUniversalLandVegetation
            && !string.IsNullOrWhiteSpace(UniversalVegetationProfileId)
            && _vegProfiles.TryGetValue(UniversalVegetationProfileId, out var universal))
            return universal;
        string id = string.IsNullOrWhiteSpace(biome.VegetationProfileId) ? "Default" : biome.VegetationProfileId;
        if (_vegProfiles.TryGetValue(id, out var p))
            return p;
        return _vegProfiles.TryGetValue("Default", out var def) ? def : null;
    }

    static float GetAverageDensityMultiplier(List<VegetationProfileItem>? items)
    {
        if (items == null || items.Count == 0) return 1f;
        float totalW = 0f;
        float weighted = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            float w = Math.Max(0f, it.Weight);
            totalW += w;
            weighted += w * Math.Clamp(it.DensityMultiplier, 0f, 3f);
        }
        if (totalW <= 1e-5f) return 1f;
        return weighted / totalW;
    }

    static VegetationProfileItem? ChooseItem(VegetationProfile? profile, bool isGrass, int seed)
    {
        var raw = isGrass ? profile?.GrassItems : profile?.TreeItems;
        if (raw == null || raw.Count == 0) return null;
        var items = raw;
        if (isGrass)
        {
            var models = new List<VegetationProfileItem>();
            for (int i = 0; i < raw.Count; i++)
            {
                var it = raw[i];
                if (it == null || it.Weight <= 0f) continue;
                if (IsUsableGrassModelItem(it))
                    models.Add(it);
            }
            if (models.Count == 0) return null;
            items = models;
        }
        else
        {
            var models = new List<VegetationProfileItem>();
            for (int i = 0; i < raw.Count; i++)
            {
                var it = raw[i];
                if (it == null || it.Weight <= 0f) continue;
                if (IsUsableTreeModelItem(it))
                    models.Add(it);
            }
            if (models.Count == 0) return null;
            items = models;
        }
        float total = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            total += Math.Max(0f, it.Weight) * Math.Clamp(it.DensityMultiplier, 0f, 3f);
        }
        if (total <= 1e-5f) return items[0];
        float r = Random01(seed) * total;
        float acc = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            acc += Math.Max(0f, it.Weight) * Math.Clamp(it.DensityMultiplier, 0f, 3f);
            if (r <= acc) return it;
        }
        return items[^1];
    }

    static bool IsUsableGrassModelItem(VegetationProfileItem it)
    {
        if (!string.IsNullOrWhiteSpace(it.PrefabPath) && IsPrefabPath(it.PrefabPath))
            return true;
        if (!string.IsNullOrWhiteSpace(it.ModelPath)
            && (IsSupportedModelPath(it.ModelPath) || IsPrefabPath(it.ModelPath) || IsImageAssetPath(it.ModelPath)))
            return true;
        return false;
    }

    static bool IsUsableTreeModelItem(VegetationProfileItem it)
    {
        if (!string.IsNullOrWhiteSpace(it.PrefabPath) && IsPrefabPath(it.PrefabPath))
            return true;
        if (!string.IsNullOrWhiteSpace(it.ModelPath) && IsSupportedModelPath(it.ModelPath))
            return true;
        return false;
    }

    static List<VegetationProfileItem> GetUsableTreeItems(VegetationProfile? profile)
    {
        var list = new List<VegetationProfileItem>(8);
        var raw = profile?.TreeItems;
        if (raw == null) return list;
        for (int i = 0; i < raw.Count; i++)
        {
            var it = raw[i];
            if (it != null && it.Weight > 0f && IsUsableTreeModelItem(it))
                list.Add(it);
        }
        return list;
    }

    static int HashPlacement(PlanetVegetationPlacement p, int index)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + index;
            h = h * 31 + (int)(p.DirX * 1000f);
            h = h * 31 + (int)(p.DirY * 1000f);
            h = h * 31 + (int)(p.DirZ * 1000f);
            return h;
        }
    }

    VegetationProfileItem? ResolveTreeItemForPlacement(PlanetVegetationPlacement p, int index)
    {
        var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
        var biome = ResolveBiomeByName(p.BiomeName);
        if (biome != null)
            biome = ResolveLandVegetationBiome(biome, dir);
        else if (_terrain != null)
            biome = ResolveLandVegetationBiome(_terrain.OceanBiome, dir);
        var profile = biome != null ? ResolveVegetationProfile(biome) : null;
        return ChooseItem(profile, isGrass: false, HashPlacement(p, index));
    }

    void EnrichTreePlacementFromProfile(PlanetVegetationPlacement p, int index)
    {
        bool needsModel = string.IsNullOrWhiteSpace(p.ModelPath) || !IsSupportedModelPath(p.ModelPath);
        bool needsPrefab = string.IsNullOrWhiteSpace(p.PrefabPath) || !IsPrefabPath(p.PrefabPath);
        if (!needsModel && !needsPrefab)
            return;

        var item = ResolveTreeItemForPlacement(p, index);
        if (item == null)
            return;
        if (needsPrefab && !string.IsNullOrWhiteSpace(item.PrefabPath))
            p.PrefabPath = PlanetAssetIO.NormalizeAssetReference(item.PrefabPath);
        if (needsModel && IsSupportedModelPath(item.ModelPath))
            p.ModelPath = PlanetAssetIO.NormalizeAssetReference(item.ModelPath);
    }

    bool PlacementHasUsableTreeAsset(PlanetVegetationPlacement p, int index)
    {
        if (p == null || p.IsGrass)
            return false;
        // Pine FBX paths in the profile are often missing on disk. Still accept
        // the placement — spawn uses a procedural tree when import cannot run.
        if (!string.IsNullOrWhiteSpace(p.PrefabPath) && IsPrefabPath(p.PrefabPath))
            return true;
        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
            return true;
        return true;
    }

    string? ResolveTreeModelPathForPlacement(PlanetVegetationPlacement p, int index)
    {
        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
            return p.ModelPath;
        var item = ResolveTreeItemForPlacement(p, index);
        return item != null && IsSupportedModelPath(item.ModelPath) ? item.ModelPath : null;
    }

    static List<VegetationProfileItem> GetUsableGrassItems(VegetationProfile? profile)
    {
        var list = new List<VegetationProfileItem>(8);
        var raw = profile?.GrassItems;
        if (raw == null) return list;
        for (int i = 0; i < raw.Count; i++)
        {
            var it = raw[i];
            if (it != null && it.Weight > 0f && IsUsableGrassModelItem(it))
                list.Add(it);
        }
        return list;
    }

    static VegetationProfileItem? ChooseGrassItem(VegetationProfile? profile, int seed, int batchIndex)
    {
        var items = GetUsableGrassItems(profile);
        if (items.Count == 0)
            return ChooseItem(profile, isGrass: true, seed);
        if (batchIndex < items.Count)
            return items[batchIndex];
        return ChooseItem(profile, isGrass: true, seed);
    }

    static SN.Vector3 OffsetPatchDirection(QuadNode leaf, int index, int count)
    {
        float uMid = (leaf.U0 + leaf.U1) * 0.5f;
        float vMid = (leaf.V0 + leaf.V1) * 0.5f;
        if (count <= 1)
            return CubeSphereMath.FaceUVToDirection(leaf.Face, uMid, vMid);

        float uSpan = MathF.Abs(leaf.U1 - leaf.U0);
        float vSpan = MathF.Abs(leaf.V1 - leaf.V0);
        float fu = Random01(HashLeaf(leaf, 11 + index * 17)) - 0.5f;
        float fv = Random01(HashLeaf(leaf, 31 + index * 17)) - 0.5f;
        float u = Math.Clamp(uMid + fu * uSpan * 0.42f, 0f, 1f);
        float v = Math.Clamp(vMid + fv * vSpan * 0.42f, 0f, 1f);
        return CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
    }

    static string ResolveGrassModelPath(string? itemModelPath, VegetationProfile? profile)
    {
        if (IsSupportedModelPath(itemModelPath))
            return itemModelPath!;
        if (profile != null && IsSupportedModelPath(profile.GrassModelPath))
            return profile.GrassModelPath;
        return DefaultPlanetGrassModelPath;
    }

    /// <summary>
    /// Model path to store in <see cref="PlanetVegetationPlacement"/> / <see cref="Entry"/>.
    /// Imported trees attach <see cref="MeshFilter"/> (with <see cref="MeshFilter.ModelPath"/>) on children
    /// (e.g. FBX sub-meshes), so root-only lookups miss the asset reference.
    /// </summary>
    static string ResolveVegetationModelPathForAsset(GameObject? go, bool isGrass)
    {
        if (go == null) return "";
            var painter = go.Behaviors.OfType<VegetationPainter>().FirstOrDefault();
        if (isGrass && !string.IsNullOrWhiteSpace(painter?.CustomMeshPath))
                return painter.CustomMeshPath.Trim();

        var tree = go.Behaviors.OfType<Tree>().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tree?.ModelPath))
            return tree.ModelPath.Trim();

        foreach (var b in go.Behaviors)
        {
            if (b is MeshFilter mf && !string.IsNullOrWhiteSpace(mf.ModelPath))
                return mf.ModelPath.Trim();
        }

        foreach (var c in go.Children)
        {
            var path = FindDescendantMeshModelPathRecursive(c);
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return "";
    }

    static string FindDescendantMeshModelPathRecursive(GameObject g)
    {
        foreach (var b in g.Behaviors)
        {
            if (b is MeshFilter mf && !string.IsNullOrWhiteSpace(mf.ModelPath))
                return mf.ModelPath.Trim();
        }

        foreach (var c in g.Children)
        {
            var path = FindDescendantMeshModelPathRecursive(c);
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return "";
    }

    static string ResolveVegetationTexturePathForAsset(GameObject? go)
    {
        if (go == null) return "";
        var painter = go.Behaviors.OfType<VegetationPainter>().FirstOrDefault();
        return string.IsNullOrWhiteSpace(painter?.TexturePath) ? "" : painter.TexturePath.Trim();
    }

    static bool IsSupportedModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".3ds";
    }

    static bool IsPrefabPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase);
    }

    SN.Vector3 ResolveCameraPosition()
    {
        if (SceneService.PlayMode)
        {
            var playCam = SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain)
                       ?? SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
            if (playCam != null && playCam.TryGetWorldLookRay(out var playOrigin, out _))
                return playOrigin;
        }

        if (_terrain != null && _terrain.LastCameraPosition.LengthSquared() > 1e-6f)
            return _terrain.LastCameraPosition;

        var cam = SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain)
               ?? SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
        if (cam != null && cam.TryGetWorldLookRay(out var origin, out _))
            return origin;
        return GetWorldCenter();
    }

    SN.Vector3 GetWorldCenter()
    {
        if (_terrain?.gameObject == null) return SN.Vector3.Zero;
        var world = SceneGraphUtil.AccumulateWorld(_terrain.gameObject);
        return new SN.Vector3(world.M41, world.M42, world.M43);
    }

    SN.Matrix4x4 GetPlanetWorldMatrix()
    {
        if (_terrain?.gameObject == null) return SN.Matrix4x4.Identity;
        return SceneGraphUtil.AccumulateWorld(_terrain.gameObject);
    }

    void EnsureRuntimeRoot()
    {
        if (_terrain?.gameObject == null)
            return;

        if (_runtimeRoot != null && _runtimeRoot.Parent == _terrain.gameObject)
            return;

        _runtimeRoot = _terrain.gameObject.Children.FirstOrDefault(c =>
            c.Name == RuntimeRootName && c.HideInHierarchy);

        if (_runtimeRoot == null)
        {
            _runtimeRoot = new GameObject(RuntimeRootName)
            {
                HideInHierarchy = true
            };
            _terrain.gameObject.AddChild(_runtimeRoot);
        }
    }

    void AttachSpawnedInstance(GameObject go)
    {
        if (go == null || _terrain?.gameObject == null)
            return;

        EnsureRuntimeRoot();
        var parent = _runtimeRoot ?? _terrain.gameObject;
        if (go.Parent != parent)
        {
            go.RemoveFromParent();
            parent.AddChild(go);
        }

        if (HideInstancesInHierarchy)
            go.HideInHierarchy = true;
    }

    GameObject? SpawnLeafGrassBatch(
        QuadNode leaf,
        BiomeDefinition biome,
        int clumpCount,
        VegetationProfile? profile,
        int seedSalt,
        VegetationProfileItem? item = null,
        SN.Vector3? patchDirOverride = null,
        int maxBladesCap = 0)
    {
        if (_terrain == null || clumpCount <= 0)
            return null;

        var leafDir = patchDirOverride ?? CubeSphereMath.FaceUVToDirection(leaf.Face, (leaf.U0 + leaf.U1) * 0.5f, (leaf.V0 + leaf.V1) * 0.5f);
        float patchRadius = PlanetGrassPatchRadius();
        int maxBlades = maxBladesCap > 0
            ? maxBladesCap
            : (PlayPerfLimited ? 12 : 160);
        int bladeCount = Math.Clamp(clumpCount * Math.Max(4, GrassBladesPerPatch), 8, maxBlades);

        var go = new GameObject($"BiomeGrassBatch_{leaf.Face}_{leaf.LodLevel}_{seedSalt & 255}");
        AttachSpawnedInstance(go);

        item ??= ChooseItem(profile, isGrass: true, seedSalt);
        float grassMinScale = Math.Max(1.0f, biome.GrassMinScale);
        float grassMaxScale = Math.Max(grassMinScale, biome.GrassMaxScale);
        if (item != null)
        {
            grassMinScale *= Math.Max(1.0f, item.MinScale);
            grassMaxScale *= Math.Max(1.0f, item.MaxScale);
        }
        if (grassMaxScale < grassMinScale)
            (grassMaxScale, grassMinScale) = (grassMinScale, grassMaxScale);

        int token = _nextGpuGrassToken--;
        go.Transform.Position = new Vector3(0, 0, 0);
        var center = ResolveGrassSeatLocal(leafDir);
        float localH = ResolveGpuGrassHeight(1f);
        string tex = PlanetGrassTextureCache.Pick(token);
        int placed = PlanetGpuGrass.RegisterPatch(
            this, token, center, leafDir, localH, 0f,
            Math.Max(0.35f, _terrain.WorldToLocalLength(patchRadius)),
            Math.Clamp(bladeCount, 6, 10), tex);
        if (placed <= 0)
        {
            go.RemoveFromParent();
            return null;
        }
        go.Name = $"GpuGrass_{token}";
        return go;
    }

    bool TrySpawnPlanetGrassBillboard(
        GameObject go,
        PlanetVegetationPlacement p,
        SN.Vector3 dir,
        float scale,
        SN.Vector3 surfaceGrass,
        SN.Vector3 radialW,
        SN.Vector3 placeUp,
        float yawDeg,
        int gpuToken)
    {
        if (_terrain == null) return false;
        string tex = !string.IsNullOrWhiteSpace(p.TexturePath) ? p.TexturePath.Trim() : "";
        if (string.IsNullOrWhiteSpace(tex) && IsImageAssetPath(p.ModelPath))
            tex = p.ModelPath.Trim();
        if (string.IsNullOrWhiteSpace(tex))
            tex = DefaultPlanetGrassTexturePath;

        var center = ResolveGrassSeatLocal(dir);
        float localH = ResolveGpuGrassHeight(scale);
        int blades = Math.Clamp(GrassBladesPerPatch, 6, 10);
        int placed = PlanetGpuGrass.RegisterPatch(
            this, gpuToken, center, dir, localH, yawDeg,
            Math.Max(0.35f, localH * 0.45f), blades, tex);
        if (placed > 0)
            go.Name = $"GpuGrass_{gpuToken}";
        return placed > 0;
    }

    void SetLocalCrustPosition(GameObject go, SN.Vector3 dir, float worldPad)
    {
        if (go == null || _terrain == null) return;
        var local = _terrain.SampleVegetationAnchorLocal(dir);
        if (worldPad != 0f)
            local += SafeNormalize(dir, SN.Vector3.UnitY) * _terrain.WorldToLocalLength(worldPad);
        go.Transform.Position = new Vector3(local.X, local.Y, local.Z);
    }

    float PlanetGrassPatchRadius()
        => Math.Max(0.55f, (_terrain?.Config?.EffectiveWorldRadius ?? 1000f) * 0.00115f);

    static SN.Vector3 VegetationWorldPoint(PlanetTerrain planet, SN.Matrix4x4 planetWorld, SN.Vector3 dir, float treePad)
    {
        var local = planet.SampleVegetationAnchorLocal(dir);
        var world = LocalSpherePointToWorld(planetWorld, local);
        if (treePad != 0f)
            world += LocalDirectionToWorld(planetWorld, dir) * treePad;
        return world;
    }

    /// <summary>Planet-local point (same space as chunk meshes: <c>FaceUVToDirection * radius</c>) → world.</summary>
    static SN.Vector3 LocalSpherePointToWorld(SN.Matrix4x4 planetWorld, SN.Vector3 localOffsetFromPlanetPivot)
        => SN.Vector3.Transform(localOffsetFromPlanetPivot, planetWorld);

    /// <summary>Unit direction in planet-local cube-sphere space → world (ignores translation).</summary>
    static SN.Vector3 LocalDirectionToWorld(SN.Matrix4x4 planetWorld, SN.Vector3 localDir)
    {
        var d = SN.Vector3.TransformNormal(localDir, planetWorld);
        return SafeNormalize(d, SN.Vector3.UnitY);
    }

    /// <summary>World-space meters to add to sampled radius for tree anchors (bias + scale-aware minimum).</summary>
    /// <param name="approximateUniformScale">Larger imported/biome tree scales need a bit more radial push so scaled feet clear the shell.</param>
    float GetTreeRadialOutwardPadding(float approximateUniformScale = 1f)
    {
        float bias = Math.Max(0f, TreeRadialSurfaceBias);
        float scalePad = Math.Clamp(approximateUniformScale * 0.025f, 0f, 0.25f);
        return bias + scalePad;
    }

    /// <summary>
    /// Plant a wide grass tuft on the visible heightfield crust. Tree root-sink also tucks along
    /// planet radial, which buries the uphill side of a clump and lifts the downhill side off cliffs.
    /// Density raycasts are not used while the visible mesh is the heightfield shell.
    /// </summary>
    void SeatGrassOnSurface(GameObject go, SN.Vector3 surfacePoint, SN.Vector3 radialOutward, SN.Vector3 grassUpWorld, float uniformScale)
    {
        if (go == null || _terrain?.gameObject == null) return;
        radialOutward = SafeNormalize(radialOutward, SN.Vector3.UnitY);
        var grassUp = SafeNormalize(grassUpWorld, radialOutward);
        if (SN.Vector3.Dot(grassUp, radialOutward) < 0f)
            grassUp = -grassUp;

        SceneGraphUtil.SetPositionWorld(go, surfacePoint);

        float planeH = 0f;
        float foot = Math.Clamp(uniformScale * 0.35f, 0.12f, 0.9f);
        var planetW = GetPlanetWorldMatrix();
        var center = new SN.Vector3(planetW.M41, planetW.M42, planetW.M43);
        var t1 = SN.Vector3.Cross(grassUp, radialOutward);
        if (t1.LengthSquared() < 1e-8f)
            t1 = SN.Vector3.Cross(grassUp, MathF.Abs(grassUp.Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX);
        t1 = SafeNormalize(t1, SN.Vector3.UnitX);
        var t2 = SafeNormalize(SN.Vector3.Cross(grassUp, t1), SN.Vector3.UnitZ);
        float hSum = 0f;
        int hCount = 0;
        void AccCrust(SN.Vector3 worldOffset)
        {
            var probe = surfacePoint + worldOffset;
            var local = _terrain.WorldToLocal(probe);
            if (local.LengthSquared() < 1e-10f) return;
            var dir = SN.Vector3.Normalize(local);
            var crustW = LocalSpherePointToWorld(planetW, _terrain.SampleVegetationAnchorLocal(dir));
            hSum += SN.Vector3.Dot(crustW - surfacePoint, grassUp);
            hCount++;
        }
        AccCrust(SN.Vector3.Zero);
        AccCrust(t1 * foot);
        AccCrust(-t1 * foot);
        AccCrust(t2 * foot);
        AccCrust(-t2 * foot);
        if (hCount > 0)
            planeH = hSum / hCount;

        var terrainW = SceneGraphUtil.AccumulateWorld(_terrain.gameObject);
        float minAlongUp = float.MaxValue;
        float maxAlongUp = float.MinValue;
        void Walk(GameObject node, SN.Matrix4x4 parentW)
        {
            if (!string.IsNullOrEmpty(node.Name) &&
                node.Name.Contains("collider", StringComparison.OrdinalIgnoreCase))
                return;
            var W = TransformUtil.WorldFromTransform(node.Transform) * parentW;
            foreach (var b in node.Behaviors)
            {
                if (b is not MeshFilter mf || !mf.Enabled) continue;
                var vtx = mf.Mesh?.Vertices;
                if (vtx == null || vtx.Length == 0) continue;
                for (int i = 0; i < vtx.Length; i++)
                {
                    var wp = SN.Vector3.Transform(vtx[i], W);
                    float h = SN.Vector3.Dot(wp - surfacePoint, grassUp);
                    if (h < minAlongUp) minAlongUp = h;
                    if (h > maxAlongUp) maxAlongUp = h;
                }
            }
            foreach (var c in node.Children)
                Walk(c, W);
        }
        Walk(go, terrainW);
        if (minAlongUp >= float.MaxValue - 1f) return;

        float tuftH = Math.Max(0.04f, maxAlongUp - minAlongUp);
        float embed = Math.Clamp(uniformScale * 0.008f, 0.002f, 0.014f);
        float delta = (planeH - embed) - minAlongUp;
        float maxShift = tuftH * 0.55f;
        delta = Math.Clamp(delta, -maxShift, maxShift);
        if (MathF.Abs(delta) < 1e-4f) return;

        var worldMat = SceneGraphUtil.AccumulateWorld(go);
        var origin = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
        SceneGraphUtil.SetPositionWorld(go, origin + grassUp * delta);
    }

    /// <summary>
    /// After scale/orientation are final, nudge the tree so mesh “feet” sit near the analytical anchor.
    /// Uses <paramref name="trunkUpWorld"/> (same as placement up) so tilted trees don’t pick the wrong extreme
    /// vertex along radial alone—then a radial pass matches the planet shell. Visible chunk meshes can still differ
    /// slightly from <see cref="PlanetTerrain.SampleSurfaceRadius"/> (LOD / isosurface), which can look like floating.
    /// </summary>
    void SinkTreeRootsToSurface(GameObject go, SN.Vector3 surfacePoint, SN.Vector3 radialOutward, SN.Vector3 trunkUpWorld, float uniformScale)
    {
        if (go == null || _terrain?.gameObject == null) return;
        radialOutward = SafeNormalize(radialOutward, SN.Vector3.UnitY);
        var trunkUp = SafeNormalize(trunkUpWorld, radialOutward);
        if (SN.Vector3.Dot(trunkUp, radialOutward) < 0f)
            trunkUp = -trunkUp;

        float embedUp = Math.Clamp(uniformScale * 0.05f, 0.02f, 0.45f);
        float targetMinUp = -embedUp;

        var terrainW = SceneGraphUtil.AccumulateWorld(_terrain.gameObject);

        float MinDotAlongAxis(SN.Vector3 axis)
        {
            axis = SafeNormalize(axis, radialOutward);
            float minV = float.MaxValue;
            void Walk(GameObject node, SN.Matrix4x4 parentW)
            {
                var W = TransformUtil.WorldFromTransform(node.Transform) * parentW;
                foreach (var b in node.Behaviors)
                {
                    if (b is not MeshFilter mf || !mf.Enabled) continue;
                    var vtx = mf.Mesh?.Vertices;
                    if (vtx == null || vtx.Length == 0) continue;
                    for (int i = 0; i < vtx.Length; i++)
                    {
                        var wp = SN.Vector3.Transform(vtx[i], W);
                        float h = SN.Vector3.Dot(wp - surfacePoint, axis);
                        if (h < minV) minV = h;
                    }
                }
                foreach (var c in node.Children)
                    Walk(c, W);
            }

            Walk(go, terrainW);
            return minV;
        }

        void ShiftAlong(SN.Vector3 axis, float targetMin)
        {
            axis = SafeNormalize(axis, radialOutward);
            float minV = MinDotAlongAxis(axis);
            if (minV >= float.MaxValue - 1f) return;
            float delta = targetMin - minV;
            if (MathF.Abs(delta) < 1e-4f) return;
            var worldMat = SceneGraphUtil.AccumulateWorld(go);
            var origin = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
            SceneGraphUtil.SetPositionWorld(go, origin + axis * delta);
        }

        // Seat feet on the visible crust along trunk up; avoid radial tuck (buried trunks on volumetric LOD).
        ShiftAlong(trunkUp, targetMinUp);
    }

    void ApplyImportedTreeMeshEulerCorrection(GameObject go)
    {
        var c = ImportedTreeMeshEulerCorrection;
        if (Math.Abs(c.X) < 1e-6 && Math.Abs(c.Y) < 1e-6 && Math.Abs(c.Z) < 1e-6)
            return;
        var extra = SN.Matrix4x4.CreateFromYawPitchRoll(
            TransformUtil.Deg2Rad((float)c.Y),
            TransformUtil.Deg2Rad((float)c.X),
            TransformUtil.Deg2Rad((float)c.Z));
        var current = go.Transform.TryGetExplicitRotationMatrix(out var r)
            ? r
            : SN.Matrix4x4.CreateFromQuaternion(go.Transform.GetRotationQuaternion());
        go.Transform.SetExplicitRotationMatrix(OrthonormalizeRotationPart(extra * current));
    }

    static int HashLeaf(QuadNode leaf, int salt)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + leaf.Face;
            string cell = BuildStableVegetationCellKey(leaf);
            for (int i = 0; i < cell.Length; i++)
                h = h * 31 + cell[i];
            h = h * 31 + salt;
            return h;
        }
    }

    static string BuildStableVegetationCellKey(QuadNode leaf)
        => BuildStableVegetationCellKey(leaf.Face, leaf.UCentre, leaf.VCentre);

    static string BuildStableVegetationCellKey(int face, float u, float v)
    {
        int cells = Math.Clamp(s_vegetationCellsPerFaceEdge, 8, 128);
        int iu = Math.Clamp((int)MathF.Floor(u * cells), 0, cells - 1);
        int iv = Math.Clamp((int)MathF.Floor(v * cells), 0, cells - 1);
        return $"{face}:{iu}:{iv}";
    }

    static bool TryParseStableVegetationCellKey(string key, out int face, out int iu, out int iv)
    {
        face = iu = iv = 0;
        if (string.IsNullOrEmpty(key)) return false;
        int c1 = key.IndexOf(':');
        int c2 = c1 >= 0 ? key.IndexOf(':', c1 + 1) : -1;
        if (c1 < 0 || c2 < 0) return false;
        return int.TryParse(key.AsSpan(0, c1), out face)
               && int.TryParse(key.AsSpan(c1 + 1, c2 - c1 - 1), out iu)
               && int.TryParse(key.AsSpan(c2 + 1), out iv);
    }

    static SN.Vector3 StableCellCenterDirection(int face, int iu, int iv)
    {
        int cells = Math.Clamp(s_vegetationCellsPerFaceEdge, 8, 128);
        float u = (iu + 0.5f) / cells;
        float v = (iv + 0.5f) / cells;
        return CubeSphereMath.FaceUVToDirection(face, u, v);
    }

    static float Random01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return (x & 0x00FFFFFF) / 16777215f;
        }
    }
}
