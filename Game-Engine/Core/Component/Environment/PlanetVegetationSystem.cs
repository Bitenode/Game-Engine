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
using Game_Engine.Core.Biome.Graph;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

[ComponentCategory("Environment")]
[Require(typeof(PlanetTerrain))]
public sealed class PlanetVegetationSystem : Behavior
{
    public const string RuntimeRootName = "PlanetVegetation_Runtime";

    static PlanetVegetationSystem()
    {
        ProjectService.ProjectClosed += ResetStaticTemplateCaches;
    }

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
    /// <summary>World height (m) of a scale-1 GPU grass clump. Placement scale multiplies it.</summary>
    [Persist] public float GrassBaseHeight { get; set; } = 1.05f;
    /// <summary>Card width relative to its height. Above 1 keeps short grass covering the ground.</summary>
    [Persist] public float GrassWidthScale { get; set; } = 1f;
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
    /// After blending radial with the slope normal, limits how far trunk "up" can tilt away from planet radial.
    /// Keeps trees upright on cliffs where the sampled normal is nearly tangent to the sphere (avoids sideways / -90 deg roll artifacts).
    /// </summary>
    [Persist] public float TreeMaxTiltFromRadialDegrees { get; set; } = 28f;

    /// <summary>
    /// Extra distance along the planet radial beyond <see cref="PlanetTerrain.SampleSurfaceRadius"/> when placing trees.
    /// Keep this small (centimeters). Large values plus radius-scaled auto padding make trees hover above the crust.
    /// </summary>
    [Persist] public float TreeRadialSurfaceBias { get; set; } = 0.06f;

    /// <summary>
    /// Added to <see cref="Transform.Rotation"/> after planet alignment when spawning <strong>imported</strong> tree meshes
    /// (biome/asset .fbx/.obj paths). Example: <c>(180,0,0)</c> if the asset's trunk grows along <c>-Y</c> in file space.
    /// Prefab instances are unchanged - bake corrections into the prefab if needed.
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
    /// <summary>Fixed grid per cube face - vegetation keys ignore LOD splits so plants do not pop when chunks refine.</summary>
    [Persist] public int VegetationCellsPerFaceEdge { get; set; } = 72;
    static int s_vegetationCellsPerFaceEdge = 72;
    /// <summary>Despawn farther out than spawn so nearby grass/trees do not flicker at the stream boundary.</summary>
    const float VegetationCullDistanceHysteresis = 1.45f;

    readonly Dictionary<string, List<Entry>> _leafEntries = new();
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);
    PlanetTerrain? _terrain;
    bool _honoredFreshWorld;
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
    long _localTreeScanAt;
    bool _carpetUsedFallbackTrees;
    readonly HashSet<int> _carpetProceduralTreeTokens = new();
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
    readonly List<string> _coralModels = new();
    readonly Dictionary<int, GameObject> _coralPatches = new();
    float _coralTick;
    readonly List<string> _readyTreeScratch = new();
    int _readyTreeVersion = -1;
    int _readyTreeModelCount = -1;
    readonly List<int> _carpetStale = new();
    readonly record struct CarpetSeat(float R, float Yaw, float PatchR, int Blades);
    /// <summary>Planted seat per carpet token; re-validated against the live mesh so LOD swaps do not leave grass floating.</summary>
    readonly Dictionary<int, CarpetSeat> _carpetSeatR = new();
    readonly List<int> _carpetDrifted = new();
    /// <summary>Grid cells that failed the dry-land/slope test. Skipped without re-sampling until cleared.</summary>
    readonly HashSet<int> _carpetRejected = new();
    const int MaxCarpetRejected = 60000;
    int _carpetSeq;
    SN.Vector3 _carpetCamDir = new(float.NaN);
    SN.Vector3 _treeCamDir = new(float.NaN);
    /// <summary>
    /// One distance band of the grass carpet. Cells are fixed to the planet (face-UV grid), so
    /// walking never re-plants a patch beside an existing one, and a band's ring walk resumes
    /// across frames instead of restarting at the player's feet every frame.
    /// </summary>
    sealed class CarpetBand
    {
        public int Id;
        public float Spacing;
        public float RMin;
        public float RMax;
        public float PatchScale;
        public int Blades;
        public bool SingleCard;
        /// <summary>Restart the walk around the player once they are this far from its centre.</summary>
        public float RestartM;
        /// <summary>A settled band stays idle until the player moves this far.</summary>
        public float SettleM;
        public SN.Vector3 Center = new(float.NaN);
        public SN.Vector3 T;
        public SN.Vector3 B;
        public int Ring;
        public int K;
        public int Rings;
        public int Added;
        public bool Cut;
        public bool Settled;
        public SN.Vector3 SettledDir;
        /// <summary>
        /// Centre and range (metres on the centre's tangent plane) of the last pass that left
        /// every cell it covers planted or ruled out. Later passes skip probes inside it, so a
        /// pass only walks the strip the player has moved into instead of the whole band.
        /// NaN when there is none on record. A patch removed inside it clears it.
        /// </summary>
        public SN.Vector3 DoneCenter = new(float.NaN);
        public float DoneLo;
        public float DoneHi;
        /// <summary>This pass skips the region on record. Fixed when the pass begins.</summary>
        public bool SkipDone;
        /// <summary>Passes in a row that skipped. Every so often one walks everything again.</summary>
        public int SkipPasses;
        /// <summary>
        /// Whole-range walks still owed before skipping starts. The probes of one walk miss
        /// about one cell in a hundred (the lattice is not the cell grid); a second walk from
        /// another centre reaches those, as every re-walk used to.
        /// </summary>
        public int FullPassesOwed = CarpetFullPassesAfterReset;
        /// <summary>Centre of the last whole-range walk counted against <see cref="FullPassesOwed"/>.</summary>
        public SN.Vector3 FullCenter = new(float.NaN);
        /// <summary>This pass met a cell it could not decide: no chunk mesh yet, or its card still decoding.</summary>
        public bool Undecided;
        /// <summary>A patch this pass covers was removed while the pass was running.</summary>
        public bool Tainted;
        /// <summary>Farthest the camera got from the pass centre while the pass ran, in metres.</summary>
        public float CamDrift;
    }

    // Sparser, wider patches further out; the far band draws one card per blade.
    readonly CarpetBand[] _carpetBands =
    {
        new() { Id = 0, Spacing = 3.2f, RMin = 0f, RMax = 95f, Blades = 12, PatchScale = 1f, RestartM = 4f, SettleM = 1.5f },
        new() { Id = 1, Spacing = 4.2f, RMin = 92f, RMax = 200f, Blades = 9, PatchScale = 1.45f, RestartM = 24f, SettleM = 6f },
        new() { Id = 2, Spacing = 6f, RMin = 196f, RMax = 400f, Blades = 6, PatchScale = 2f, SingleCard = true, RestartM = 48f, SettleM = 12f },
    };
    const int TreeCellBand = 3;
    readonly float[] _bandKeepLo2 = new float[4];
    readonly float[] _bandKeepHi2 = new float[4];
    int _carpetTexStamp = -1;
    /// <summary>Last walking direction (planet-local), so recycling frees grass behind the player first.</summary>
    SN.Vector3 _carpetMoveDir = new(float.NaN);
    bool _treeSettled;
    SN.Vector3 _treeSettledDir = new(float.NaN);
    int _carpetReseatCursor;
    /// <summary>Last <see cref="QuadNode.VisibleEpoch"/> this system looked at.</summary>
    int _vegSurfaceEpoch = -1;
    /// <summary>Anchor height each local tree / saved-placement tree was planted at.</summary>
    readonly Dictionary<int, float> _treeShellR = new();
    readonly Dictionary<int, float> _assetTreeShellR = new();
    /// <summary>Terrain samples (dry-land tests) still allowed this frame. Rejections are cached, so a walk converges over frames.</summary>
    int _carpetSamplesLeft;
    /// <summary>Stopwatch tick after which the carpet stops seating new patches this frame.</summary>
    long _carpetDeadline;
    const double CarpetFrameBudgetSec = 0.002;
    /// <summary>Where far grass / trees were last released, so standing still never churns the cap.</summary>
    SN.Vector3 _carpetRecycleDir = new(float.NaN);
    /// <summary>Where and when patches outside their band were last swept out.</summary>
    SN.Vector3 _carpetDropDir = new(float.NaN);
    int _carpetDropFrame;
    Camera? _playCam;
    bool _playCamIsMain;
    float _playCamFoundAt = float.NegativeInfinity;
    SN.Vector3 _treeRecycleDir = new(float.NaN);
    static readonly float[] s_treeRecycleTiersM = { 200f, 150f, 120f, 90f };
    /// <summary>Planet-local centre direction and cos(angular radius) of each <see cref="VegetationClearZone"/>.</summary>
    readonly List<(SN.Vector3 Dir, float MinDot)> _clearZones = new();
    int _clearZoneVersion = -1;
    float _clearZoneRefreshSec;
    float[] _grassDensityByBiome = Array.Empty<float>();
    BiomeDefinition[]? _grassDensitySource;

    enum VegKind
    {
        Grass,
        Tree,
        Bush,
        Rock,
        Underwater,
        Vein
    }

    sealed class Entry
    {
        public GameObject GameObject = null!;
        public BiomeDefinition Biome = null!;
        public bool IsGrass;
        public VegKind Kind = VegKind.Tree;
        public float BaseScale = 1f;
        public float Vitality = 1f;
        public SN.Vector3 SurfaceDir;
        public float YawDeg;
        public string BiomeName = "";
        public string PrefabPath = "";
        public string ModelPath = "";
        public string TexturePath = "";
        public int GpuGrassToken;
        public float SeatRadius;
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
    const int MaxTreeTemplateCacheEntries = 32;
    /// <summary>Suffix so template cache invalidates when import/spawn pipeline changes.</summary>
    const string TreeTemplateCacheKeySuffix = "|hier_v30_pine002";
    const string DefaultPlanetGrassTexturePath = "Assets/Standard Assets/Planet Vegetation/Simple Grass_01.psd";
    const string DefaultPlanetGrassModelPath = "Assets/Standard Assets/Planet Vegetation/Grass/Meadow_Grass_01_Var4.FBX";
    const int MaxImportedTreeTriangles = 48000;
    const long MaxImportedTreeSourceBytes = 1_800_000;
    static readonly ConcurrentQueue<string> s_pendingTemplateLoads = new();
    static readonly HashSet<string> s_queuedTemplateLoads = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Models that imported to nothing drawable. Keyed with the file stamp so a replaced file is tried again.</summary>
    static readonly Dictionary<string, long> s_treeTemplateUnusable = new(StringComparer.OrdinalIgnoreCase);
    static int s_templateLoaderRunning;
    bool _deferSpawnForTemplate;

    /// <summary>Bumped whenever a tree template is added or dropped, so "which models are ready" is rebuilt only then.</summary>
    static int s_treeTemplateVersion;

    static void ResetStaticTemplateCaches()
    {
        lock (s_treeTemplateLock)
        {
            s_treeTemplateCache.Clear();
            s_queuedTemplateLoads.Clear();
            s_treeTemplateUnusable.Clear();
            Interlocked.Increment(ref s_treeTemplateVersion);
        }
        while (s_pendingTemplateLoads.TryDequeue(out _)) { }
        s_treeFileSeen.Clear();
        s_treeTemplateKeys.Clear();
        PlanetGrassTextureCache.Clear();
    }

    bool PlayPerfLimited => SceneService.PlayMode && !VegetationStressTest;
#if XBOX_UWP
    // Dev Mode reboots when the first planet frames decode 8×2k biomes + warm 50k placements + fauna FBX.
    bool ConsoleTight => true;
#else
    bool ConsoleTight => false;
#endif

    public override void Awake() => EnsureInitialized();

    public override void PostDeserialize() => EnsureInitialized();

    public override void OnEnable()
    {
        EnsureInitialized();
        HonorFreshWorld();
        if (AutoUseSavedPlacementsWhenPresent && _assetPlacements.Count > 0)
            UsePlanetAssetPlacements = true;
        if (AutoSpawn && UsePlanetAssetPlacements && _assetPlacements.Count > 0)
            WarmSpawnAfterDeferredImport();
    }

    public override void Start()
    {
        EnsureInitialized();
        HonorFreshWorld();
        if (AutoUseSavedPlacementsWhenPresent && _assetPlacements.Count > 0)
            UsePlanetAssetPlacements = true;
        PruneSubmergedAssetPlacements();
        if (UsePlanetAssetPlacements && _assetPlacements.Count > 0 && AutoSpawn)
            WarmSpawnAfterDeferredImport();
    }

    /// <summary>
    /// A menu-created world must not reuse the template's baked forest.
    /// Procedural streaming then follows the new seed.
    /// </summary>
    void HonorFreshWorld()
    {
        if (_honoredFreshWorld) return;
        var terrain = _terrain ?? GetComponent<PlanetTerrain>();
        if (terrain == null || !terrain.FreshWorldLaunch) return;
        _terrain = terrain;
        _honoredFreshWorld = true;
        _assetPlacements.Clear();
        UsePlanetAssetPlacements = false;
        AutoSpawn = true;
        RebuildAssetPlacementAccel();
    }

    void EnsureInitialized()
    {
        _terrain ??= GetComponent<PlanetTerrain>();
        if (_vegProfiles.Count == 0)
        {
            try { _vegProfiles = VegetationProfileLibrary.LoadAll(); }
            catch (Exception ex)
            {
                // Creating the profiles folder throws in a read-only package. Without this
                // catch, every frame retries, logs, and never reaches the texture upload.
                Log.Warning("[PlanetVegetation] Profiles unavailable: " + ex.Message);
                _vegProfiles = new Dictionary<string, VegetationProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Default"] = new VegetationProfile { Id = "Default" }
                };
            }
        }
        s_vegetationCellsPerFaceEdge = Math.Clamp(VegetationCellsPerFaceEdge, 8, 128);
        if (!s_activeSystems.Contains(this))
            s_activeSystems.Add(this);
        EnsureRuntimeRoot();
        if (!_queuedProfileTemplates)
        {
#if XBOX_UWP
            // FBX imports during the ground-texture decode are what froze the first seconds.
            if (!SceneRenderer.BiomeTexturesStreaming)
#endif
            {
                QueueProfileTemplateLoads();
                _queuedProfileTemplates = true;
            }
        }
        PlanetGrassTextureCache.EnsureCatalog();
    }

    /// <summary>
    /// Scene View has no play-mode behavior tick - call each render frame with the active camera position.
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
        HonorFreshWorld();
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

        RefreshClearZones(dt, Math.Max(1.7f, ResolveGpuGrassHeight(1f) * 0.38f));
        NoteSurfaceEpoch();
        // Carpet must run every frame. The asset-stream interval (0.10s+)
        // is what made walking grass trickle in.
        TickLocalGpuGrassCarpet(cameraPos, dt);
#if XBOX_UWP
        // Trees, coral, and placed models are the imports that stacked on the
        // ground-texture decode. Grass cards stay; the models wait one beat.
        if (SceneRenderer.BiomeTexturesStreaming)
            return;
#endif
        TickLocalTrees(cameraPos, dt);
        ReseatQueuedAssets();
        TickUnderwaterCoral(cameraPos, dt);

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
        // Console must keep despawning behind the player; a 35 m gate left the
        // whole walked trail resident until the freeze.
        float moveThreshSq = ConsoleTight
            ? 6f * 6f
            : PlayPerfLimited ? 35f * 35f : VegRefreshMoveThresholdSq;
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

    /// <summary>
    /// Remove live grass/trees whose seat sits inside a dig/build brush so they
    /// do not float over excavated crust. Streaming will refill dry land later.
    /// </summary>
    public void NotifySurfaceEdit(SN.Vector3 worldCenter, float worldRadius)
    {
        EnsureInitialized();
        if (_terrain?.Config == null) return;
        var local = _terrain.WorldToLocal(worldCenter);
        float len = local.Length();
        if (len < 1e-5f) return;
        var hitDir = local / len;

        float planetR = Math.Max(1f, len);
        float brushLocal = _terrain.WorldToLocalLength(Math.Max(0.75f, worldRadius));
        // Expand past brush so patch footprints / tall blades over the rim clear too.
        float clearR = brushLocal + Math.Max(2.5f, planetR * 0.004f);
        float ang = Math.Clamp(clearR / planetR, 0.002f, 0.45f);
        float minDot = MathF.Cos(ang);

        _carpetStale.Clear();
        foreach (var kv in _carpetDirs)
        {
            if (SN.Vector3.Dot(kv.Value, hitDir) >= minDot)
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveCarpetPatch(_carpetStale[i]);
        // Dig/build changed the crust here; let those cells be re-tested.
        _carpetRejected.Clear();
        UnsettleCarpetBands();
        _treeSettled = false;

        _carpetStale.Clear();
        foreach (var kv in _carpetTreeDirs)
        {
            if (SN.Vector3.Dot(kv.Value, hitDir) >= minDot)
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveLocalCarpetTree(_carpetStale[i]);

        _staleAssetIndices.Clear();
        foreach (var kv in _assetActive)
        {
            var e = kv.Value;
            var dir = e.SurfaceDir.LengthSquared() > 1e-8f
                ? SN.Vector3.Normalize(e.SurfaceDir)
                : SN.Vector3.UnitY;
            if (SN.Vector3.Dot(dir, hitDir) < minDot)
                continue;
            _staleAssetIndices.Add(kv.Key);
        }
        for (int i = 0; i < _staleAssetIndices.Count; i++)
        {
            int idx = _staleAssetIndices[i];
            if (!_assetActive.TryGetValue(idx, out var old))
                continue;
            if (old.IsGrass)
                PlanetGpuGrass.RemovePatch(this, old.GpuGrassToken != 0 ? old.GpuGrassToken : idx);
            else
                old.GameObject?.RemoveFromParent();
            _assetActive.Remove(idx);
        }

        // Also drop procedural leaf instances planted in the brush cone.
        foreach (var group in _leafEntries.Values)
        {
            for (int i = group.Count - 1; i >= 0; i--)
            {
                var e = group[i];
                var dir = e.SurfaceDir.LengthSquared() > 1e-8f
                    ? SN.Vector3.Normalize(e.SurfaceDir)
                    : SN.Vector3.UnitY;
                if (SN.Vector3.Dot(dir, hitDir) < minDot)
                    continue;
                if (e.IsGrass && e.GpuGrassToken != 0)
                    PlanetGpuGrass.RemovePatch(this, e.GpuGrassToken);
                ForgetScatterRock(e);
                e.GameObject?.RemoveFromParent();
                group.RemoveAt(i);
            }
        }
    }

    public override void OnDestroy()
    {
        s_activeSystems.Remove(this);
        ClearLocalCarpetTrees();
        ClearCarpetBookkeeping();
        PlanetGpuGrass.ClearOwner(this);
        GetComponent<PlanetScatterRenderer>()?.ClearInstances();
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
        // Follow day/night exactly - do not floor into "always dusk" values.
        sunIntensity = Math.Clamp(atmo?.SunIntensity ?? 1f, 0.02f, 2f);
        atmoAmbient = Math.Clamp(atmo?.Ambient ?? 0.18f, 0.02f, 1f);
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
            ClearCarpetBookkeeping();
            PlanetGpuGrass.ClearOwner(this);
            ClearAssetActive();
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
    {
        if (ConsoleTight)
            return 24;
        return PlayPerfLimited ? PlayMaxDespawnsPerRefresh : MaxDespawnsPerRefresh;
    }

    int ResolveActiveTreeCap()
    {
        int configured = Math.Max(8, MaxActiveAssetTrees);
        if (ConsoleTight)
            return Math.Clamp(configured, 6, 12);
        if (!PlayPerfLimited)
            return configured;
        return Math.Clamp(configured, 24, 96);
    }

    int ResolveActiveGrassCap()
    {
        int configured = Math.Max(4, MaxActiveAssetGrassPatches);
        if (ConsoleTight)
            return Math.Clamp(configured, 24, 64);
        if (!PlayPerfLimited)
            return Math.Min(configured, 1024);
        return Math.Clamp(configured, 96, 280);
    }

    float ResolveAssetActivationDistance(float worldRadius)
    {
        // Chord along the crust from the camera's surface projection.
        // Play used to clamp this to 180-340 m, which missed every stored
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
            ClearCarpetBookkeeping();
            PlanetGpuGrass.ClearOwner(this);
            ClearAssetActive();
        }

        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;
        int budget = ConsoleTight
            ? Math.Clamp(budgetOverride ?? 4, 1, 6)
            : _manualSpawnPass
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
        long spawnBudgetTicks = (long)((ConsoleTight ? 1.5 : 4.0) * System.Diagnostics.Stopwatch.Frequency / 1000.0);
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
            if (ConsoleTight && treeSpawnsThisTick >= 1)
                continue;
            if (!ConsoleTight && PlayPerfLimited && treeSpawnsThisTick >= 8)
                continue;
            if (InsideClearZone(SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY)))
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
            RememberTreeGround(idx, entry.SurfaceDir, _assetTreeShellR);
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
        // Skip entirely during the first ~3s on console — hydrate/OnEnable warm spawn
        // was stacking with biome decode and rebooting Dev Mode before SwapBuffers.
        if (ConsoleTight && Time.frameCount < 90)
            return;
        EnsureInitialized();
        int budget = ConsoleTight ? 2 : 8;
        SpawnFromAssetPlacements(clearExisting: false, budgetOverride: budget);
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
        if (InsideClearZone(dir))
            return false;
        var center = ResolveGrassSeatLocal(dir);
        float localH = ResolveGpuGrassHeight(p.Scale);
        float localPatch = Math.Max(1.7f, ResolveGpuGrassHeight(1f) * 0.45f);
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
            SeatRadius = center.Length(),
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
    bool TryPrepareCarpetClump(SN.Vector3 dir, out SN.Vector3 center, out SN.Vector3 upLocal, out SN.Vector3 surfaceNormal)
    {
        center = default;
        upLocal = SN.Vector3.UnitY;
        surfaceNormal = SN.Vector3.UnitY;
        if (_terrain?.Config == null)
            return false;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);

        // The cubemap crust is what the chunk mesh is built from and costs one bilerp.
        // The carved crust and water table run river noise, so only chunks that
        // generated a water patch pay for them.
        bool baked = _terrain.HasBakedStandSurface;
        float crust = CarpetCrust(dir);
        if (crust < 1f)
            return false;

        PlanetWaterSurfaceSample water = default;
        bool wet = false;
        if (_terrain.EnableWater)
        {
            if (crust < _terrain.Config.SeaLevel - 0.25f)
                return false;
            wet = !baked || _terrain.HasWaterChunkAt(dir);
            if (wet)
            {
                if (baked)
                    crust = _terrain.SampleLocalCrustRadius(dir);
                water = _terrain.SampleWaterSurface(dir);
                if (water.Mask >= 0.22f && water.Kind != PlanetWaterKind.Lava && crust < water.Radius - 0.35f)
                    return false;
                if (crust < _terrain.Config.SeaLevel - 0.25f)
                    return false;
            }
        }

        float seatR = ResolveCarpetSeatRadius(dir, crust);
        var seat = dir * seatR;
        if (wet && water.Mask >= 0.18f && water.Kind != PlanetWaterKind.Lava && seatR < water.Radius - 0.25f)
            return false;

        // Slope from the same (visible-mesh) surface the seat uses. Mixing analytical
        // neighbours with a rendered centre gave wrong tilts on hills.
        CarpetTangentBasis(dir, out var t, out var b);
        float eps = Math.Max(2.4f, crust * 0.0024f);
        float step = eps / Math.Max(crust, 1f);
        var dT = SafeNormalize(dir + t * step, dir);
        var dB = SafeNormalize(dir + b * step, dir);
        float rT = ResolveCarpetSeatRadius(dT, crust);
        float rB = ResolveCarpetSeatRadius(dB, crust);
        var p0 = seat;
        var pT = dT * rT;
        var pB = dB * rB;
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

        surfaceNormal = n;
        // Sink a little more on slopes: blade roots below the surface read as
        // grass, roots above it read as floating.
        float embed = _terrain.WorldToLocalLength(0.05f + (1f - align) * 0.45f);
        center = seat - dir * embed;
        float follow = Math.Clamp(0.58f + (1f - align) * 0.75f, 0.58f, 0.96f);
        upLocal = SafeNormalize(SN.Vector3.Lerp(dir, n, follow), dir);
        return true;
    }

    bool TryPrepareCarpetClump(SN.Vector3 dir, out SN.Vector3 center, out SN.Vector3 upLocal)
        => TryPrepareCarpetClump(dir, out center, out upLocal, out _);

    /// <summary>Patch footprint shrinks on slopes so edge blades stay near the surface plane.</summary>
    static float SlopePatchRadius(float patchR, SN.Vector3 dir, SN.Vector3 surfaceNormal)
    {
        float align = Math.Clamp(SN.Vector3.Dot(dir, surfaceNormal), 0f, 1f);
        return patchR * Math.Clamp(align * align, 0.45f, 1f);
    }

    /// <summary>Radius the grass sits at: the visible chunk mesh when it exists and is sane, else the heightfield.</summary>
    float ResolveCarpetSeatRadius(SN.Vector3 dir, out float crust)
    {
        crust = CarpetCrust(dir);
        return ResolveCarpetSeatRadius(dir, crust);
    }

    /// <summary>Planet-local crust from the baked cubemap (plus digs); live crust before the bake lands.</summary>
    float CarpetCrust(SN.Vector3 dir)
    {
        if (_terrain!.HasBakedStandSurface)
            return _terrain.WorldToLocalLength(_terrain.SampleEditedCubemapWorldRadius(dir));
        return _terrain.SampleLocalCrustRadius(dir);
    }

    float ResolveCarpetSeatRadius(SN.Vector3 dir, float crust)
    {
        float rendR = _terrain!.SampleRenderedCrustLocal(dir).Length();
        if (rendR <= 1f)
            return crust;
        // Hills from a LOD swap can sit well off the cubemap. Follow that mesh
        // unless the sample is garbage (cave/sky), so grass resets with the ground.
        float slack = Math.Max(12f, _terrain.WorldToLocalLength(64f));
        if (MathF.Abs(rendR - crust) <= slack)
            return rendR;
        return crust;
    }

    float ResolveGpuGrassHeight(float scale)
    {
        // GrassBaseHeight and the placement scale set the height. A 4.5 m floor used to
        // override both, so lowering grass in the scene or the .planet did nothing.
        float worldH = Math.Clamp(GrassBaseHeight, 0.25f, 7.2f) * Math.Clamp(scale, 0.4f, 2.5f);
        return Math.Max(_terrain!.WorldToLocalLength(worldH), 0.2f);
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
        return PlanetGrassTextureCache.Pick(p == null ? _carpetSeq++ : HashPlacement(p));
    }

    static int HashPlacement(PlanetVegetationPlacement p)
        => HashCode.Combine(p.DirX, p.DirY, p.DirZ, p.TexturePath ?? "", p.ModelPath ?? "");

    void TickLocalGpuGrassCarpet(SN.Vector3 cameraPos, float dt)
    {
        if (_terrain?.Config == null) return;
        PlanetGrassTextureCache.EnsureCarpetMix();
        if (_carpetDirs.Count == 0 && PlanetGrassTextureCache.ReadyMixCount() < 10)
            return;

        var camLocal = _terrain.WorldToLocal(cameraPos);
        if (camLocal.LengthSquared() < 1e-6f) return;
        var camDir = SN.Vector3.Normalize(camLocal);
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        if (CameraTooHighForCarpet(camLocal, camDir))
        {
            // Orbit / far scene camera: the nadir carpet is invisible from up there
            // and used to leave stale clumps on whatever side the camera last faced.
            ClearCarpetPatches();
            _carpetCamDir = camDir;
            return;
        }
        // Desktop keeps a far hilltop carpet. Console must stay near the player or
        // walking freezes once thousands of patches stay resident.
        float outer = ConsoleTight
            ? Math.Clamp(radius * 0.28f, 180f, 240f)
            : Math.Clamp(radius * 0.40f, 260f, 400f);
        _carpetBands[2].RMax = outer;
        if (ConsoleTight)
        {
            _carpetBands[1].RMax = Math.Min(200f, outer * 0.9f);
            _carpetBands[0].RMax = Math.Min(_carpetBands[0].RMax, 95f);
        }
        var prevDir = float.IsNaN(_carpetCamDir.X) ? camDir : _carpetCamDir;
        var step = camDir - prevDir;
        float stepM = step.Length() * radius;
        // Walking speed, not metres per frame: at 5-8 FPS a walk is under a metre a frame,
        // which used to count as standing still.
        bool moving = stepM > 0.05f && stepM / Math.Max(dt, 1e-3f) > 1.5f;
        if (moving)
            _carpetMoveDir = SN.Vector3.Normalize(step);
        _carpetCamDir = camDir;

        float h = ResolveGpuGrassHeight(1f);
        float patchR = Math.Max(1.7f, h * 0.38f);
        // The sweep visits every patch. Each band keeps a margin of six metres and more
        // past its range, so it only has to run once the player has moved a step.
        int frameNow = Time.frameCount;
        float sinceDropM = float.IsNaN(_carpetDropDir.X)
            ? float.MaxValue
            : (camDir - _carpetDropDir).Length() * radius;
        if (sinceDropM >= 1f || frameNow - _carpetDropFrame >= 30 || frameNow < _carpetDropFrame)
        {
            DropCarpetOutsideBands(camDir, radius);
            _carpetDropDir = camDir;
            _carpetDropFrame = frameNow;
        }

        // Grass planted on a coarse LOD floats once that chunk becomes a hill. Patches on
        // chunks whose mesh just changed are queued and checked first. The rolling slice
        // behind it covers anything a change did not announce.
        ReseatQueuedCarpet(
            checkBudget: ConsoleTight ? 48 : 128,
            fixBudget: ConsoleTight ? 8 : 24,
            h);
        ReseatDriftedCarpet(
            checkBudget: ConsoleTight ? (moving ? 8 : 12) : (moving ? 16 : 48),
            fixBudget: ConsoleTight ? 3 : 6,
            h);

        int cap = ConsoleTight ? 1100 : (PlayPerfLimited ? 18000 : 22000);
        // Each new patch is a few cubemap/mesh samples; the frame deadline bounds the rest.
        int perTick = ConsoleTight
            ? (moving ? 28 : 16)
            : moving
                ? (PlayPerfLimited ? 360 : 420)
                : (PlayPerfLimited ? 300 : 360);
        // Until the surface cubemap bake lands, every candidate costs ~6 full
        // biome-graph noise evaluations. Trickle instead of freezing the first seconds.
        if (!_terrain.HasBakedStandSurface)
            perTick = Math.Min(perTick, ConsoleTight ? 32 : 120);
        // Bound rejections too: an all-ocean heading is otherwise ~70k water tests in one frame.
        _carpetSamplesLeft = _terrain.HasBakedStandSurface ? perTick * 3 : 160;
        _carpetDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(System.Diagnostics.Stopwatch.Frequency * (ConsoleTight ? 0.0035 : CarpetFrameBudgetSec));

        // Release grass behind the player before planting more. Console used to wait
        // until 3500 patches filled, which froze after a short walk.
        int softCap = ConsoleTight ? (int)(cap * 0.8f) : cap - perTick;
        int slack = cap - _carpetDirs.Count;
        float sinceRecycleM = float.IsNaN(_carpetRecycleDir.X)
            ? float.MaxValue
            : (camDir - _carpetRecycleDir).Length() * radius;
        float recycleStepM = ConsoleTight ? 8f : 28f;
        float minKeepM = ConsoleTight ? 90f : 110f;
        if (_carpetDirs.Count > softCap || (slack < perTick && sinceRecycleM > recycleStepM))
        {
            int need = Math.Max(perTick, _carpetDirs.Count - softCap);
            RecycleFarCarpet(camDir, radius, need, minKeepM, outer);
            _carpetRecycleDir = camDir;
            slack = cap - _carpetDirs.Count;
        }

        if (_carpetRejected.Count > MaxCarpetRejected)
        {
            _carpetRejected.Clear();
            UnsettleCarpetBands();
        }
        // Wake settled bands once when the mix first becomes plantable. Bands can settle
        // empty while waiting for cards; without this the carpet stays blank until you move.
        // Do not unsettle on every new decode — that restarts planting and looks like flicker.
        int mixReady = PlanetGrassTextureCache.ReadyMixCount();
        if (_carpetTexStamp < 10 && mixReady >= 10)
        {
            _carpetTexStamp = mixReady;
            UnsettleCarpetBands();
        }
        else if (mixReady > _carpetTexStamp)
            _carpetTexStamp = mixReady;

        if (slack <= 0) return;
        int want = Math.Min(slack, perTick);
        // Near band first: a frame cut short by the deadline still leaves grass at the feet.
        for (int i = 0; i < _carpetBands.Length && want > 0 && _carpetSamplesLeft > 0; i++)
            want -= StepCarpetBand(_carpetBands[i], camDir, radius, h, patchR, want);
    }

    /// <summary>
    /// Continue <paramref name="band"/>'s ring walk around the player, planting unvisited cells.
    /// Returns the number of patches added this frame.
    /// </summary>
    int StepCarpetBand(CarpetBand band, SN.Vector3 camDir, float radius, float height, float basePatchR, int want)
    {
        if (want <= 0)
            return 0;
        if (band.Settled)
        {
            if ((camDir - band.SettledDir).Length() * radius <= band.SettleM)
                return 0;
            band.Settled = false;
        }
        if (float.IsNaN(band.Center.X) || band.Ring > band.Rings
            || (camDir - band.Center).Length() * radius > band.RestartM)
            BeginCarpetBandPass(band, camDir);

        float camDrift = (camDir - band.Center).Length() * radius;
        if (camDrift > band.CamDrift)
            band.CamDrift = camDrift;

        // Probes are denser than the cells so every cell in range is hit at least once.
        float probe = band.Spacing * 0.7f;
        float rMin2 = band.RMin * band.RMin;
        float rMax2 = band.RMax * band.RMax;
        float camMax = band.RMax * 1.05f / radius;
        float camMax2 = camMax * camMax;
        int cells = CarpetCellsPerEdge(radius, band.Spacing);
        float patchR = basePatchR * band.PatchScale;
        var origin = band.Center * radius;
        int added = 0;
        int visited = 0;

        // The region an earlier pass left fully decided, as seen from this pass's centre.
        // A probe's distance from that region's centre D, measured on D's tangent plane the
        // way that pass measured its own range, is R * tan(angle), and
        // tan^2 = (|P|^2 |D|^2 - (P.D)^2) / (P.D)^2 for the probe point P = R C + T px + B py.
        // Both products are polynomials in px and py. They are kept in doubles: the
        // difference is a few metres squared taken from the planet's radius squared.
        // The region is taken in by a cell and a half at each edge: a cell that straddles
        // the edge may have been reached only by a probe that was out of range.
        bool skipDone = band.SkipDone && !float.IsNaN(band.DoneCenter.X);
        float doneLo = 0f, doneHi = 0f, doneArc = 0f;
        double doneLo2 = 0, doneHi2 = 0, dd = 1;
        double pdC = 0, pdT = 0, pdB = 0;
        double ppCC = 0, ppTT = 0, ppBB = 0, ppCT = 0, ppCB = 0, ppTB = 0;
        double radius2 = (double)radius * radius;
        if (skipDone)
        {
            float edge = band.Spacing * 1.5f;
            doneLo = band.DoneLo > 0f ? band.DoneLo + edge : 0f;
            doneHi = band.DoneHi - edge;
            var done = band.DoneCenter;
            float chord = (band.Center - done).Length();
            if (doneHi <= doneLo || chord > 1f)
                skipDone = false;
            else
            {
                doneLo2 = (double)doneLo * doneLo;
                doneHi2 = (double)doneHi * doneHi;
                doneArc = 2f * MathF.Asin(chord * 0.5f);
                dd = Dot64(done, done);
                pdC = radius * Dot64(band.Center, done);
                pdT = Dot64(band.T, done);
                pdB = Dot64(band.B, done);
                ppCC = radius2 * Dot64(band.Center, band.Center);
                ppTT = Dot64(band.T, band.T);
                ppBB = Dot64(band.B, band.B);
                ppCT = 2.0 * radius * Dot64(band.Center, band.T);
                ppCB = 2.0 * radius * Dot64(band.Center, band.B);
                ppTB = 2.0 * Dot64(band.T, band.B);
            }
        }

        for (; band.Ring <= band.Rings; band.Ring++, band.K = 0)
        {
            if (band.Ring * probe > band.RMax)
            {
                band.Ring = band.Rings + 1;
                break;
            }
            // Whole ring inside the hole (mid/far annuli): nothing to plant here.
            if ((band.Ring + 1) * probe * 1.42f < band.RMin)
                continue;
            // Whole ring inside the decided region. Its probes sit between ring * probe and
            // that times root two from this pass's centre, and the two centres are doneArc apart.
            if (skipDone && band.K == 0)
            {
                float farArc = MathF.Atan(band.Ring * probe * 1.4143f / radius) + doneArc;
                float nearArc = MathF.Atan(band.Ring * probe / radius) - doneArc;
                if (farArc < 1.2f && radius * MathF.Tan(farArc) <= doneHi
                    && (doneLo <= 0f || (nearArc > 0f && radius * MathF.Tan(nearArc) >= doneLo)))
                    continue;
            }
            int perim = band.Ring == 0 ? 1 : band.Ring * 8;
            while (band.K < perim)
            {
                if (added >= want)
                    return added;
                if ((++visited & 63) == 0 && CarpetPastDeadline())
                {
                    band.Cut = true;
                    return added;
                }
                RingCell(band.Ring, band.K++, out int ix, out int iy);
                float px = ix * probe;
                float py = iy * probe;
                float d2 = px * px + py * py;
                if (d2 < rMin2 || d2 > rMax2)
                    continue;
                if (skipDone)
                {
                    double pd = pdC + px * pdT + py * pdB;
                    if (pd > 0.0)
                    {
                        double pp = ppCC + px * (px * ppTT + ppCT + py * ppTB) + py * (py * ppBB + ppCB);
                        double fromDone2 = radius2 * (pp * dd - pd * pd) / (pd * pd);
                        if (fromDone2 >= doneLo2 && fromDone2 <= doneHi2)
                            continue;
                    }
                }

                var probeDir = SafeNormalize(origin + band.T * px + band.B * py, band.Center);
                // Cheap hash checks first - the terrain sample below is the expensive part.
                int token = CarpetCellToken(band.Id, probeDir, cells, out int face, out int iu, out int iv);
                if (_carpetDirs.ContainsKey(token) || _carpetRejected.Contains(token))
                    continue;
                var dir = CarpetCellDir(token, face, iu, iv, cells);
                if ((dir - camDir).LengthSquared() > camMax2 || InsideClearZone(dir))
                    continue;

                string? tex = PlanetGrassTextureCache.TryPickReady(token);
                if (string.IsNullOrWhiteSpace(tex))
                {
                    // Its card is still decoding: the cell is neither planted nor ruled out.
                    band.Undecided = true;
                    continue;
                }

                if (_carpetSamplesLeft-- <= 0 || CarpetOutOfTime())
                {
                    band.K--;
                    band.Cut = true;
                    return added;
                }
                float density = CarpetGrassDensity(dir);
                if (density < 0f)
                {
                    // Chunk mesh not built yet: try again on a later pass.
                    band.Cut = true;
                    band.Undecided = true;
                    continue;
                }
                if (CarpetCellRoll(token) >= density || !TryPrepareCarpetClump(dir, out var center, out var upLocal, out var surfN))
                {
                    _carpetRejected.Add(token);
                    continue;
                }

                float yaw = CarpetCellYaw(token);
                float pr = SlopePatchRadius(patchR, dir, surfN);
                if (PlanetGpuGrass.RegisterPatch(this, token, center, upLocal, height, yaw, pr, band.Blades, tex, surfN, band.SingleCard) <= 0)
                {
                    band.Undecided = true;
                    continue;
                }
                AddCarpetPatch(token, dir, center, yaw, patchR, band.Blades);
                band.Added++;
                added++;
            }
        }

        // Every probe in range was walked by this pass or lies in the region an earlier
        // pass decided, so the whole range around this centre is decided now. The camera
        // must have stayed near the centre: cells are also limited to 1.05 x range from the
        // camera, and past 4% drift that limit starts to leave out cells this pass covers.
        if (!band.Undecided && !band.Tainted && band.CamDrift <= band.RMax * 0.04f)
        {
            band.DoneCenter = band.Center;
            band.DoneLo = band.RMin;
            band.DoneHi = band.RMax;
            // A whole-range walk counts once per place: from the same centre it lands
            // its probes on the same cells again.
            if (!band.SkipDone && band.FullPassesOwed > 0
                && (float.IsNaN(band.FullCenter.X)
                    || (band.Center - band.FullCenter).Length() * radius >= band.Spacing * 0.5f))
            {
                band.FullPassesOwed--;
                band.FullCenter = band.Center;
            }
        }

        // A full pass that found (almost) nothing new: idle until the player moves on.
        if (!band.Cut && band.Added <= 2)
        {
            band.Settled = true;
            band.SettledDir = camDir;
        }
        band.Ring = band.Rings + 1;
        return added;
    }

    /// <summary>Every this many passes the walk visits each probe again, whatever is on record as decided.</summary>
    const int CarpetFullPassEvery = 32;
    /// <summary>Whole-range walks, from different centres, before a band starts skipping what it has decided.</summary>
    const int CarpetFullPassesAfterReset = 2;

    static void BeginCarpetBandPass(CarpetBand band, SN.Vector3 camDir)
    {
        band.Center = camDir;
        CarpetTangentBasis(camDir, out band.T, out band.B);
        float probe = band.Spacing * 0.7f;
        band.Rings = Math.Clamp((int)MathF.Ceiling(band.RMax / probe) + 1, 2, 400);
        band.Ring = 0;
        band.K = 0;
        band.Added = 0;
        band.Cut = false;
        band.Undecided = false;
        band.Tainted = false;
        band.CamDrift = 0f;
        band.SkipDone = !float.IsNaN(band.DoneCenter.X)
            && band.FullPassesOwed <= 0
            && band.SkipPasses < CarpetFullPassEvery;
        band.SkipPasses = band.SkipDone ? band.SkipPasses + 1 : 0;
    }

    void UnsettleCarpetBands()
    {
        for (int i = 0; i < _carpetBands.Length; i++)
        {
            _carpetBands[i].Settled = false;
            ForgetCarpetDone(_carpetBands[i]);
        }
    }

    void ResetCarpetBands()
    {
        for (int i = 0; i < _carpetBands.Length; i++)
        {
            var band = _carpetBands[i];
            band.Settled = false;
            band.Center = new SN.Vector3(float.NaN);
            band.Ring = 0;
            band.K = 0;
            band.Rings = 0;
            ForgetCarpetDone(band);
        }
    }

    /// <summary>Cells in the band may have become plantable again: the next pass walks every probe.</summary>
    static void ForgetCarpetDone(CarpetBand band)
    {
        band.DoneCenter = new SN.Vector3(float.NaN);
        band.FullCenter = new SN.Vector3(float.NaN);
        band.FullPassesOwed = CarpetFullPassesAfterReset;
        // A pass under way may already have skipped or walked past the cells in question.
        band.Tainted = true;
    }

    /// <summary>
    /// A planted cell is about to be removed. If it lies in the range its band has on
    /// record as decided, or in the range of the pass under way, neither can be relied on:
    /// the cell is plantable again and nothing else would walk back to it.
    /// </summary>
    void NoteCarpetCellFreed(int token)
    {
        var band = _carpetBands[Math.Min(CarpetTokenBand(token), _carpetBands.Length - 1)];
        bool haveDone = !float.IsNaN(band.DoneCenter.X);
        bool inPass = !float.IsNaN(band.Center.X) && !band.Tainted;
        if (!haveDone && !inPass)
            return;
        if (_terrain?.Config == null || !_carpetDirs.TryGetValue(token, out var dir))
        {
            ForgetCarpetDone(band);
            return;
        }
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        // The cell's point is within a cell and a half of any probe that reached it.
        float reach = band.Spacing * 1.5f;
        if (haveDone && WithinCarpetRange(dir, band.DoneCenter, radius, band.DoneLo - reach, band.DoneHi + reach))
            ForgetCarpetDone(band);
        else if (inPass && WithinCarpetRange(dir, band.Center, radius, band.RMin - reach, band.RMax + reach))
            band.Tainted = true;
    }

    /// <summary>Is <paramref name="dir"/> between <paramref name="lo"/> and <paramref name="hi"/> metres from <paramref name="center"/>, measured on the tangent plane at the centre?</summary>
    static bool WithinCarpetRange(SN.Vector3 dir, SN.Vector3 center, float radius, float lo, float hi)
    {
        double along = Dot64(dir, center);
        if (along <= 0.0)
            return false;
        double along2 = along * along;
        double dist2 = (double)radius * radius * (Dot64(dir, dir) * Dot64(center, center) - along2) / along2;
        if (dist2 > (double)hi * hi)
            return false;
        return lo <= 0f || dist2 >= (double)lo * lo;
    }

    static double Dot64(SN.Vector3 a, SN.Vector3 b)
        => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;

    /// <summary>Each band keeps only its own distance range; the neighbouring band plants the rest.</summary>
    void DropCarpetOutsideBands(SN.Vector3 camDir, float radius)
    {
        for (int i = 0; i < _bandKeepLo2.Length; i++)
        {
            var band = _carpetBands[Math.Min(i, _carpetBands.Length - 1)];
            float lo = band.RMin > 0f ? Math.Max(0f, band.RMin * 0.8f - 2f) / radius : 0f;
            float hi = (band.RMax * 1.15f + 6f) / radius;
            _bandKeepLo2[i] = lo * lo;
            _bandKeepHi2[i] = hi * hi;
        }
        _carpetStale.Clear();
        foreach (var kv in _carpetDirs)
        {
            int b = CarpetTokenBand(kv.Key);
            float d2 = (camDir - kv.Value).LengthSquared();
            if (d2 > _bandKeepHi2[b] || d2 < _bandKeepLo2[b])
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveCarpetPatch(_carpetStale[i]);
    }

    /// <summary>Cells per cube-face edge for a planet-fixed grid of roughly <paramref name="spacing"/> metres.</summary>
    static int CarpetCellsPerEdge(float radius, float spacing)
        => Math.Clamp((int)MathF.Round(radius * (MathF.PI * 0.5f) / Math.Max(0.5f, spacing)), 8, 4095);

    /// <summary>
    /// Token of the planet-fixed cell containing <paramref name="dir"/>. Uses the exact inverse of
    /// the terrain's face warp so cells are near-equal area (central projection crowds the corners).
    /// </summary>
    static int CarpetCellToken(int band, SN.Vector3 dir, int cells, out int face, out int iu, out int iv)
    {
        var (f, u, v) = CubeSphereMath.SphereToCubeExact(dir);
        face = f;
        iu = Math.Clamp((int)(u * cells), 0, cells - 1);
        iv = Math.Clamp((int)(v * cells), 0, cells - 1);
        return unchecked((int)0xC0000000) | ((band & 3) << 27) | ((face & 7) << 24) | (iu << 12) | iv;
    }

    static int CarpetTokenBand(int token) => (token >> 27) & 3;

    /// <summary>Jittered, deterministic position inside a cell.</summary>
    static SN.Vector3 CarpetCellDir(int token, int face, int iu, int iv, int cells)
    {
        uint h = CellHash((uint)token);
        float ju = 0.15f + 0.7f * ((h & 0xFFFF) * (1f / 65536f));
        float jv = 0.15f + 0.7f * ((h >> 16) * (1f / 65536f));
        float inv = 1f / cells;
        return CubeSphereMath.FaceUVToDirection(face, (iu + ju) * inv, (iv + jv) * inv);
    }

    static float CarpetCellRoll(int token) => (CellHash((uint)token ^ 0x68E31DA4u) & 0xFFFFFF) * (1f / 16777216f);

    static float CarpetCellYaw(int token) => (CellHash((uint)token ^ 0xB5297A4Du) & 0xFFFF) * (360f / 65536f);

    static uint CellHash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>
    /// Grass coverage 0..1 at <paramref name="dir"/> from the biome blend the visible chunk was
    /// shaded with (biome Vegetation Density). -1 while that chunk has no mesh yet.
    /// </summary>
    float CarpetGrassDensity(SN.Vector3 dir)
    {
        if (!_terrain!.TrySampleSurfaceBiomeBlend(dir, out var idx, out var wt))
            return -1f;
        var table = GrassDensityTable();
        float sum = 0f;
        float dens = 0f;
        AccumulateGrassDensity(table, idx.X, wt.X, ref sum, ref dens);
        AccumulateGrassDensity(table, idx.Y, wt.Y, ref sum, ref dens);
        AccumulateGrassDensity(table, idx.Z, wt.Z, ref sum, ref dens);
        AccumulateGrassDensity(table, idx.W, wt.W, ref sum, ref dens);
        return sum > 1e-4f ? Math.Clamp(dens / sum, 0f, 1f) : 1f;
    }

    static void AccumulateGrassDensity(float[] table, float index, float weight, ref float sum, ref float dens)
    {
        if (weight <= 1e-3f)
            return;
        int i = (int)MathF.Round(index);
        dens += weight * ((uint)i < (uint)table.Length ? table[i] : 1f);
        sum += weight;
    }

    /// <summary>Vegetation Density per <see cref="BiomeDefinition.BiomeIndex"/>; all 1 when no biome sets one.</summary>
    float[] GrassDensityTable()
    {
        var biomes = _terrain?.Config?.Biomes;
        if (ReferenceEquals(biomes, _grassDensitySource) && _grassDensityByBiome.Length > 0)
            return _grassDensityByBiome;
        _grassDensitySource = biomes;
        var table = new float[256];
        Array.Fill(table, 1f);
        bool any = false;
        if (biomes != null)
        {
            for (int i = 0; i < biomes.Length; i++)
                any |= biomes[i] != null && biomes[i].VegetationDensity > 0f;
            if (any)
            {
                for (int i = 0; i < biomes.Length; i++)
                {
                    var b = biomes[i];
                    if (b != null)
                        table[b.BiomeIndex] = Math.Max(0f, b.VegetationDensity);
                }
            }
        }
        _grassDensityByBiome = table;
        return table;
    }

    /// <summary>
    /// Rebuild the clear-zone list when zones change (and once a second, for moved props),
    /// then drop grass and trees already standing inside them.
    /// </summary>
    void RefreshClearZones(float dt, float marginLocal)
    {
        _clearZoneRefreshSec -= dt;
        var zones = VegetationClearZone.Active;
        if (_clearZoneVersion == VegetationClearZone.Version && (_clearZoneRefreshSec > 0f || zones.Count == 0))
            return;
        _clearZoneVersion = VegetationClearZone.Version;
        _clearZoneRefreshSec = 1f;
        int before = ClearZoneSignature();
        _clearZones.Clear();
        if (_terrain == null)
            return;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z == null || !z.IsActiveAndEnabled || !IsAttachedToScene(z.gameObject))
                continue;
            var local = _terrain.WorldToLocal(z.WorldCenter);
            float len = local.Length();
            if (len < 1e-3f)
                continue;
            float ang = (_terrain.WorldToLocalLength(z.Radius) + marginLocal) / len;
            _clearZones.Add((local / len, MathF.Cos(Math.Clamp(ang, 0f, 0.5f))));
        }
        // A zone that moved, shrank or went away leaves cells the carpet skipped while it
        // was there. They are plantable now, so no band may treat its range as decided.
        if (ClearZoneSignature() != before)
        {
            for (int i = 0; i < _carpetBands.Length; i++)
                ForgetCarpetDone(_carpetBands[i]);
        }
        if (_clearZones.Count > 0)
            ClearVegetationInZones();
    }

    int ClearZoneSignature()
    {
        var hash = new HashCode();
        hash.Add(_clearZones.Count);
        for (int i = 0; i < _clearZones.Count; i++)
        {
            hash.Add(_clearZones[i].Dir);
            hash.Add(_clearZones[i].MinDot);
        }
        return hash.ToHashCode();
    }

    static bool IsAttachedToScene(GameObject? go)
    {
        if (go == null)
            return false;
        var root = go;
        while (root.Parent != null)
            root = root.Parent;
        foreach (var r in SceneService.Root)
        {
            if (ReferenceEquals(r, root))
                return true;
        }
        return false;
    }

    bool InsideClearZone(SN.Vector3 dir)
    {
        for (int i = 0; i < _clearZones.Count; i++)
        {
            if (SN.Vector3.Dot(dir, _clearZones[i].Dir) >= _clearZones[i].MinDot)
                return true;
        }
        return false;
    }

    void ClearVegetationInZones()
    {
        _carpetStale.Clear();
        foreach (var kv in _carpetDirs)
        {
            if (InsideClearZone(kv.Value))
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveCarpetPatch(_carpetStale[i]);

        _carpetStale.Clear();
        foreach (var kv in _carpetTreeDirs)
        {
            if (InsideClearZone(kv.Value))
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveLocalCarpetTree(_carpetStale[i]);

        _staleAssetIndices.Clear();
        foreach (var kv in _assetActive)
        {
            var e = kv.Value;
            if (e.SurfaceDir.LengthSquared() > 1e-8f && InsideClearZone(SN.Vector3.Normalize(e.SurfaceDir)))
                _staleAssetIndices.Add(kv.Key);
        }
        for (int i = 0; i < _staleAssetIndices.Count; i++)
        {
            int idx = _staleAssetIndices[i];
            if (!_assetActive.Remove(idx, out var old))
                continue;
            if (old.IsGrass)
                PlanetGpuGrass.RemovePatch(this, old.GpuGrassToken != 0 ? old.GpuGrassToken : idx);
            else
                old.GameObject?.RemoveFromParent();
        }
    }

    /// <summary>Time-only deadline check for walks that mostly skip already-planted cells.</summary>
    bool CarpetPastDeadline()
    {
        if (System.Diagnostics.Stopwatch.GetTimestamp() <= _carpetDeadline)
            return false;
        _carpetSamplesLeft = 0;
        return true;
    }

    /// <summary>
    /// Checked every few samples. Out of time also zeroes the sample budget so the
    /// walk is treated as cut short and the carpet does not mark itself settled.
    /// </summary>
    bool CarpetOutOfTime()
    {
        if ((_carpetSamplesLeft & 7) != 0)
            return false;
        if (System.Diagnostics.Stopwatch.GetTimestamp() <= _carpetDeadline)
            return false;
        _carpetSamplesLeft = 0;
        return true;
    }

    void AddCarpetPatch(int token, SN.Vector3 dir, SN.Vector3 centerLocal, float yaw, float patchR, int blades)
    {
        _carpetDirs[token] = dir;
        _carpetSeatR[token] = new CarpetSeat(centerLocal.Length(), yaw, patchR, blades);
    }

    void RemoveCarpetPatch(int token)
    {
        NoteCarpetCellFreed(token);
        PlanetGpuGrass.RemovePatch(this, token);
        _carpetDirs.Remove(token);
        _carpetSeatR.Remove(token);
    }

    const float CarpetMaxCameraAltitudeM = 700f;

    bool CameraTooHighForCarpet(SN.Vector3 camLocal, SN.Vector3 camDir)
    {
        if (_terrain == null) return true;
        float crustUnderCam = CarpetCrust(camDir);
        float altLocal = camLocal.Length() - MathF.Max(1f, crustUnderCam);
        return altLocal > _terrain.WorldToLocalLength(CarpetMaxCameraAltitudeM);
    }

    /// <summary>Remove only the carpet's GPU patches (asset-placement patches belong to the stream).</summary>
    void ClearCarpetPatches()
    {
        if (_carpetDirs.Count == 0)
            return;
        foreach (var token in _carpetDirs.Keys)
            PlanetGpuGrass.RemovePatch(this, token);
        ClearCarpetBookkeeping();
    }

    void ClearCarpetBookkeeping()
    {
        _carpetDirs.Clear();
        _carpetSeatR.Clear();
        _carpetRejected.Clear();
        _reseatNear.Clear();
        _reseatFar.Clear();
        _reseatQueued.Clear();
        ResetCarpetBands();
        _treeSettled = false;
    }

    /// <summary>Forget every live saved-placement instance, with what was remembered about its ground.</summary>
    void ClearAssetActive()
    {
        _assetActive.Clear();
        _assetTreeShellR.Clear();
        _reseatAssets.Clear();
        _reseatAssetsQueued.Clear();
    }

    /// <summary>
    /// Re-check a slice of planted patches against the current mesh. Drifted ones are
    /// re-registered in place (same token, dir, yaw) so grass snaps down/up without
    /// visibly relocating; only patches that are no longer valid land are removed.
    /// </summary>
    void ReseatDriftedCarpet(int checkBudget, int fixBudget, float height)
    {
        int n = _carpetDirs.Count;
        if (n == 0 || checkBudget <= 0 || _terrain == null)
            return;
        // Before the bake lands every sample is full live noise — not worth it.
        if (!_terrain.HasBakedStandSurface)
            return;
        float tol = CarpetSeatTolerance();

        _carpetDrifted.Clear();
        if (_carpetReseatCursor >= n)
            _carpetReseatCursor = 0;
        int start = _carpetReseatCursor;
        int end = Math.Min(n, start + checkBudget);
        int i = 0;
        foreach (var kv in _carpetDirs)
        {
            if (i >= end) break;
            if (i++ < start) continue;
            if (!_carpetSeatR.TryGetValue(kv.Key, out var seat))
                continue;
            float expect = ResolveCarpetSeatRadius(kv.Value, out _);
            if (expect <= 1f)
                continue;
            if (MathF.Abs(expect - seat.R) > tol)
                _carpetDrifted.Add(kv.Key);
        }
        _carpetReseatCursor = end >= n ? 0 : end;

        int fixes = Math.Min(fixBudget, _carpetDrifted.Count);
        for (int k = 0; k < fixes; k++)
        {
            int token = _carpetDrifted[k];
            if (_carpetDirs.TryGetValue(token, out var dir) && _carpetSeatR.TryGetValue(token, out var seat))
                ReseatCarpetPatch(token, dir, seat, height);
        }
    }

    /// <summary>Only real LOD-swap drift (~half a metre+), not bilinear stand-grid noise.</summary>
    float CarpetSeatTolerance() => Math.Max(0.5f, _terrain!.WorldToLocalLength(0.45f));

    /// <summary>Register the patch again where the ground is now. A patch that is no longer valid land is removed.</summary>
    void ReseatCarpetPatch(int token, SN.Vector3 dir, CarpetSeat seat, float height)
    {
        string? tex = PlanetGrassTextureCache.TryPickReady(token);
        bool singleCard = _carpetBands[Math.Min(CarpetTokenBand(token), _carpetBands.Length - 1)].SingleCard;
        if (string.IsNullOrWhiteSpace(tex)
            || !TryPrepareCarpetClump(dir, out var center, out var upLocal, out var surfN)
            || PlanetGpuGrass.RegisterPatch(this, token, center, upLocal, height, seat.Yaw,
                SlopePatchRadius(seat.PatchR, dir, surfN), seat.Blades, tex, surfN, singleCard) <= 0)
        {
            RemoveCarpetPatch(token);
            return;
        }
        _carpetSeatR[token] = seat with { R = center.Length() };
    }

    // ── Following the ground when a chunk's mesh changes ──
    //
    // The chunk manager records which patch of which cube face was rebuilt, split or
    // merged. Only the grass and trees standing on those patches are looked at again,
    // a budgeted few per frame. Walking over the whole carpet to find them, every
    // frame the terrain was still streaming, cost more than planting it.

    /// <summary>Cells per cube-face edge in <see cref="_changedCells"/>: one bit per cell, a row per ulong.</summary>
    const int ChangeGrid = 64;
    readonly ulong[] _changedCells = new ulong[6 * ChangeGrid];
    readonly List<QuadNode.VisibleChange> _surfaceChanges = new();
    /// <summary>Carpet tokens waiting for a seat check. Near band first, it is the grass at the player's feet.</summary>
    readonly Queue<int> _reseatNear = new();
    readonly Queue<int> _reseatFar = new();
    readonly HashSet<int> _reseatQueued = new();
    readonly Queue<int> _reseatAssets = new();
    readonly HashSet<int> _reseatAssetsQueued = new();

    void NoteSurfaceEpoch()
    {
        int epoch = QuadNode.VisibleEpoch;
        int since = _vegSurfaceEpoch;
        if (epoch == since)
            return;
        _vegSurfaceEpoch = epoch;
        // Nothing was planted against the ground before the first look.
        if (since < 0 || _terrain?.Config == null)
            return;
        if (_carpetDirs.Count == 0 && _carpetTreeDirs.Count == 0 && _assetActive.Count == 0)
            return;

        _surfaceChanges.Clear();
        if (QuadNode.TryReadVisibleChanges(since, _surfaceChanges))
        {
            Array.Clear(_changedCells);
            for (int i = 0; i < _surfaceChanges.Count; i++)
                MarkChangedCells(_surfaceChanges[i]);
        }
        else
        {
            // More changes than the log holds. Treat the whole planet as changed.
            Array.Fill(_changedCells, ulong.MaxValue);
        }

        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        Span<float> invCells = stackalloc float[4];
        for (int b = 0; b < 4; b++)
            invCells[b] = 1f / CarpetCellsPerEdge(radius, _carpetBands[Math.Min(b, _carpetBands.Length - 1)].Spacing);

        foreach (var kv in _carpetDirs)
        {
            // The token is the patch's cell on the same face/UV grid the chunks use.
            int token = kv.Key;
            int band = CarpetTokenBand(token);
            int face = (token >> 24) & 7;
            float u = (((token >> 12) & 0xFFF) + 0.5f) * invCells[band];
            float v = ((token & 0xFFF) + 0.5f) * invCells[band];
            if (!CellChanged(face, u, v) || !_reseatQueued.Add(token))
                continue;
            (band == 0 ? _reseatNear : _reseatFar).Enqueue(token);
        }

        // Few enough to move right away.
        foreach (var kv in _carpetTreeDirs)
        {
            var (face, u, v) = CubeSphereMath.SphereToCubeExact(kv.Value);
            if (CellChanged(face, u, v) && _carpetTrees.TryGetValue(kv.Key, out var go))
                SlideOntoVisibleGround(go, kv.Value, kv.Key, _treeShellR);
        }

        foreach (var kv in _assetActive)
        {
            var d = kv.Value.SurfaceDir;
            if (d.LengthSquared() < 1e-8f)
                continue;
            var (face, u, v) = CubeSphereMath.SphereToCubeExact(SN.Vector3.Normalize(d));
            if (CellChanged(face, u, v) && _reseatAssetsQueued.Add(kv.Key))
                _reseatAssets.Enqueue(kv.Key);
        }
    }

    void MarkChangedCells(in QuadNode.VisibleChange change)
    {
        if ((uint)change.Face >= 6)
            return;
        int u0 = Math.Clamp((int)(change.U0 * ChangeGrid), 0, ChangeGrid - 1);
        int u1 = Math.Clamp((int)(change.U1 * ChangeGrid), 0, ChangeGrid - 1);
        int v0 = Math.Clamp((int)(change.V0 * ChangeGrid), 0, ChangeGrid - 1);
        int v1 = Math.Clamp((int)(change.V1 * ChangeGrid), 0, ChangeGrid - 1);
        int width = u1 - u0 + 1;
        ulong row = (width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL) << u0;
        int at = change.Face * ChangeGrid;
        for (int v = v0; v <= v1; v++)
            _changedCells[at + v] |= row;
    }

    bool CellChanged(int face, float u, float v)
    {
        if ((uint)face >= 6)
            return false;
        int cu = Math.Clamp((int)(u * ChangeGrid), 0, ChangeGrid - 1);
        int cv = Math.Clamp((int)(v * ChangeGrid), 0, ChangeGrid - 1);
        return ((_changedCells[face * ChangeGrid + cv] >> cu) & 1UL) != 0UL;
    }

    /// <summary>Check queued carpet patches against the mesh now on screen and re-seat the ones that moved.</summary>
    void ReseatQueuedCarpet(int checkBudget, int fixBudget, float height)
    {
        if (_reseatNear.Count == 0 && _reseatFar.Count == 0)
            return;
        if (_terrain == null || !_terrain.HasBakedStandSurface)
            return;
        float tol = CarpetSeatTolerance();
        int fixes = 0;
        while (checkBudget > 0)
        {
            var queue = _reseatNear.Count > 0 ? _reseatNear : _reseatFar;
            if (queue.Count == 0)
                break;
            int token = queue.Peek();
            if (!_carpetDirs.TryGetValue(token, out var dir) || !_carpetSeatR.TryGetValue(token, out var seat))
            {
                // Recycled or removed since it was queued.
                queue.Dequeue();
                _reseatQueued.Remove(token);
                continue;
            }
            checkBudget--;
            float expect = ResolveCarpetSeatRadius(dir, out _);
            if (expect <= 1f || MathF.Abs(expect - seat.R) <= tol)
            {
                queue.Dequeue();
                _reseatQueued.Remove(token);
                continue;
            }
            // It moved, but this frame's re-seats are spent. It stays at the front for the next frame.
            if (fixes >= fixBudget)
                break;
            queue.Dequeue();
            _reseatQueued.Remove(token);
            fixes++;
            ReseatCarpetPatch(token, dir, seat, height);
        }
    }

    /// <summary>Saved-placement grass and trees whose chunk changed: a few per frame.</summary>
    void ReseatQueuedAssets()
    {
        if (_reseatAssets.Count == 0 || _terrain == null)
            return;
        float tol = CarpetSeatTolerance();
        int budget = ConsoleTight ? 6 : 16;
        while (budget-- > 0 && _reseatAssets.Count > 0)
        {
            int key = _reseatAssets.Dequeue();
            _reseatAssetsQueued.Remove(key);
            if (!_assetActive.TryGetValue(key, out var e))
                continue;
            if (e.SurfaceDir.LengthSquared() < 1e-8f)
                continue;
            var dir = SN.Vector3.Normalize(e.SurfaceDir);
            if (!e.IsGrass)
            {
                if (e.GameObject != null)
                    SlideOntoVisibleGround(e.GameObject, dir, key, _assetTreeShellR);
                continue;
            }
            // Only patches planted by TryRegisterCheapGpuGrass carry a seat, keyed by placement index.
            if (e.SeatRadius <= 1f || (uint)key >= (uint)_assetPlacements.Count)
                continue;
            var center = ResolveGrassSeatLocal(dir);
            float seatR = center.Length();
            if (seatR <= 1f || MathF.Abs(seatR - e.SeatRadius) <= tol)
                continue;
            // The same registration it was planted with, on the ground as it is now.
            var p = _assetPlacements[key];
            if (PlanetGpuGrass.RegisterPatch(
                    this, e.GpuGrassToken, center, dir, ResolveGpuGrassHeight(p.Scale), p.YawDeg,
                    Math.Max(1.7f, ResolveGpuGrassHeight(1f) * 0.45f),
                    Math.Clamp(GrassBladesPerPatch, 8, 12), ResolveGrassTexturePath(p)) > 0)
                e.SeatRadius = seatR;
        }
    }

    /// <summary>
    /// Remember the anchor height a tree was just planted at, so a later change to the
    /// mesh under it can move the tree by exactly as much as its anchor moved.
    /// </summary>
    void RememberTreeGround(int key, SN.Vector3 dir, Dictionary<int, float> anchors)
    {
        if (_terrain == null)
            return;
        float anchor = _terrain.SampleVegetationAnchorLocal(SafeNormalize(dir, SN.Vector3.UnitY)).Length();
        if (anchor > 1f)
            anchors[key] = anchor;
        else
            anchors.Remove(key);
    }

    /// <summary>
    /// Move a planted tree along its radial by however much its anchor moved since it
    /// was planted (or last moved). The anchor is the same sample the tree was seated
    /// with, so the roots keep the depth they were sunk to. A tree with no remembered
    /// anchor is left where it is and remembered from here on.
    /// </summary>
    void SlideOntoVisibleGround(GameObject go, SN.Vector3 dir, int key, Dictionary<int, float> anchors)
    {
        if (go == null || _terrain == null)
            return;
        dir = SafeNormalize(dir, SN.Vector3.UnitY);
        float anchor = _terrain.SampleVegetationAnchorLocal(dir).Length();
        if (anchor <= 1f)
            return;
        if (!anchors.TryGetValue(key, out float prev) || prev <= 1f)
        {
            anchors[key] = anchor;
            return;
        }

        float delta = anchor - prev;
        if (MathF.Abs(delta) <= Math.Max(0.45f, _terrain.WorldToLocalLength(0.4f)))
            return;
        var pos = go.Transform.Position;
        var shifted = new SN.Vector3((float)pos.X, (float)pos.Y, (float)pos.Z) + dir * delta;
        go.Transform.Position = new Vector3(shifted.X, shifted.Y, shifted.Z);
        anchors[key] = anchor;
    }

    /// <summary>k-th cell on the perimeter of square ring <paramref name="ring"/> (8*ring cells, ring 0 = origin).</summary>
    static void RingCell(int ring, int k, out int ix, out int iy)
    {
        if (ring == 0) { ix = 0; iy = 0; return; }
        int side = k / (2 * ring);
        int off = k - side * 2 * ring;
        switch (side)
        {
            case 0: ix = -ring + off; iy = -ring; break;
            case 1: ix = ring; iy = -ring + off; break;
            case 2: ix = ring - off; iy = ring; break;
            default: ix = -ring; iy = ring - off; break;
        }
    }

    static void CarpetTangentBasis(SN.Vector3 dir, out SN.Vector3 t, out SN.Vector3 b)
    {
        t = SN.Vector3.Cross(MathF.Abs(dir.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, dir);
        if (t.LengthSquared() < 1e-8f) t = SN.Vector3.UnitX;
        t = SN.Vector3.Normalize(t);
        b = SN.Vector3.Normalize(SN.Vector3.Cross(dir, t));
    }

    /// <summary>
    /// Remove up to <paramref name="need"/> grass patches, farthest distance tier first and,
    /// within a tier, patches behind the walking direction before the ones ahead.
    /// </summary>
    void RecycleFarCarpet(SN.Vector3 camDir, float radius, int need, float minKeepM, float outer)
    {
        if (need <= 0 || _carpetDirs.Count == 0)
            return;
        bool haveMove = !float.IsNaN(_carpetMoveDir.X);
        int removed = 0;
        for (int t = 0; t < 4 && removed < need; t++)
        {
            float tierM = Math.Max(minKeepM, outer * (0.95f - t * 0.15f)) / radius;
            float tierSq = tierM * tierM;
            for (int pass = 0; pass < 2 && removed < need; pass++)
            {
                bool behindOnly = pass == 0 && haveMove;
                if (pass == 1 && !haveMove)
                    break;
                _carpetStale.Clear();
                foreach (var kv in _carpetDirs)
                {
                    var delta = kv.Value - camDir;
                    if (delta.LengthSquared() <= tierSq)
                        continue;
                    if (behindOnly && SN.Vector3.Dot(delta, _carpetMoveDir) > 0f)
                        continue;
                    _carpetStale.Add(kv.Key);
                    if (removed + _carpetStale.Count >= need)
                        break;
                }
                for (int i = 0; i < _carpetStale.Count; i++)
                    RemoveCarpetPatch(_carpetStale[i]);
                removed += _carpetStale.Count;
            }
        }
    }

    /// <summary>Remove up to <paramref name="need"/> local trees, farthest distance tier first.</summary>
    void RecycleFarTrees(SN.Vector3 camDir, float radius, int need, float minKeepM)
    {
        if (need <= 0 || _carpetTreeDirs.Count == 0)
            return;
        int removed = 0;
        for (int t = 0; t < s_treeRecycleTiersM.Length && removed < need; t++)
        {
            float tierM = Math.Max(minKeepM, s_treeRecycleTiersM[t]) / radius;
            float tierSq = tierM * tierM;
            _carpetStale.Clear();
            foreach (var kv in _carpetTreeDirs)
            {
                if ((camDir - kv.Value).LengthSquared() <= tierSq)
                    continue;
                _carpetStale.Add(kv.Key);
                if (removed + _carpetStale.Count >= need)
                    break;
            }
            for (int i = 0; i < _carpetStale.Count; i++)
                RemoveLocalCarpetTree(_carpetStale[i]);
            removed += _carpetStale.Count;
        }
        _treeSettled = false;
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
            _carpetSeatR.Remove(token);
            removed++;
        }
    }

    void TickLocalTrees(SN.Vector3 cameraPos, float dt)
    {
        if (_terrain?.Config == null) return;
        EnsureLocalTreeModels();
        if (!WaitForProfileTreeFbxs())
            return;

        var camLocal = _terrain.WorldToLocal(cameraPos);
        if (camLocal.LengthSquared() < 1e-6f) return;
        var camDir = SN.Vector3.Normalize(camLocal);
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        if (CameraTooHighForCarpet(camLocal, camDir))
        {
            ClearLocalCarpetTrees();
            _treeCamDir = camDir;
            return;
        }
        float outer = ConsoleTight
            ? Math.Clamp(radius * 0.14f, 90f, 130f)
            : Math.Clamp(radius * 0.22f, 160f, 240f);
        var prevDir = float.IsNaN(_treeCamDir.X) ? camDir : _treeCamDir;
        var step = camDir - prevDir;
        float stepM = step.Length() * radius;
        bool moving = stepM > 0.05f && stepM / Math.Max(dt, 1e-3f) > 1.5f;
        var fillDir = moving
            ? SafeNormalize(camDir + SafeNormalize(step, camDir) * (40f / radius), camDir)
            : camDir;
        _treeCamDir = camDir;

        float keep = moving ? outer * 1.05f : outer * 1.25f;
        float maxDirDeltaSq = (keep / radius) * (keep / radius);
        _carpetStale.Clear();
        foreach (var kv in _carpetTreeDirs)
        {
            if ((camDir - kv.Value).LengthSquared() > maxDirDeltaSq)
                _carpetStale.Add(kv.Key);
        }
        for (int i = 0; i < _carpetStale.Count; i++)
            RemoveLocalCarpetTree(_carpetStale[i]);
        if (_carpetStale.Count > 0 || moving)
            _treeSettled = false;

        int cap = ConsoleTight ? 14 : (PlayPerfLimited ? 56 : 80);
        // Each tree is a cloned model plus surface sampling. Five to eight a frame stalled
        // the walk; the cap is unchanged so the forest fills in over a second or two.
        int perTick = ConsoleTight ? 1 : (moving ? 3 : 2);
        long treeDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(System.Diagnostics.Stopwatch.Frequency * 0.002);

        // Free trees behind the player before the hard cap fills.
        int softCap = ConsoleTight ? Math.Max(8, (int)(cap * 0.7f)) : cap - perTick;
        int slack = cap - _carpetTrees.Count;
        float sinceRecycleM = float.IsNaN(_treeRecycleDir.X)
            ? float.MaxValue
            : (camDir - _treeRecycleDir).Length() * radius;
        if (_carpetTrees.Count > softCap || (slack < perTick && sinceRecycleM > (ConsoleTight ? 8f : 15f)))
        {
            int need = Math.Max(perTick, _carpetTrees.Count - softCap);
            RecycleFarTrees(camDir, radius, need, minKeepM: ConsoleTight ? 55f : 90f);
            _treeRecycleDir = camDir;
            slack = cap - _carpetTrees.Count;
        }
        if (slack <= 0) return;
        int want = Math.Min(slack, perTick);

        if (!moving && _treeSettled)
        {
            bool drifted = float.IsNaN(_treeSettledDir.X)
                || (camDir - _treeSettledDir).Length() * radius > 1.0f;
            if (!drifted)
                return;
            _treeSettled = false;
        }

        CarpetTangentBasis(fillDir, out var t, out var b);
        // Planet-fixed 22 m cells, same as the grass: a camera-relative lattice re-planted
        // a second tree a metre from each existing one every time the player moved.
        const float spacing = 22f;
        float probe = spacing * 0.7f;
        int cells = CarpetCellsPerEdge(radius, spacing);
        int n = Math.Clamp((int)MathF.Ceiling(outer / probe) + 1, 4, 40);
        float keepOut = 16f / radius;
        float reach = outer / radius;
        int added = 0;
        int spawnFailed = 0;
        int samplesLeft = _terrain.HasBakedStandSurface ? 160 : 24;
        var origin = fillDir * radius;
        for (int ring = 0; ring <= n && added < want && samplesLeft > 0; ring++)
        {
            int perim = ring == 0 ? 1 : ring * 8;
            for (int k = 0; k < perim && added < want && samplesLeft > 0; k++)
            {
                RingCell(ring, k, out int ix, out int iy);
                float px = ix * probe;
                float py = iy * probe;
                if (px * px + py * py > outer * outer)
                    continue;
                var probeDir = SafeNormalize(origin + t * px + b * py, fillDir);
                int token = CarpetCellToken(TreeCellBand, probeDir, cells, out int face, out int iu, out int iv);
                if (_carpetTrees.ContainsKey(token) || _carpetRejected.Contains(token))
                    continue;
                var dir = CarpetCellDir(token, face, iu, iv, cells);
                float dc2 = (dir - camDir).LengthSquared();
                if (dc2 < keepOut * keepOut || dc2 > reach * reach || InsideClearZone(dir))
                    continue;
                samplesLeft--;
                if (!IsCheapDryLandForCarpet(dir))
                {
                    _carpetRejected.Add(token);
                    continue;
                }
                if (!TrySpawnLocalCarpetTree(token, dir))
                {
                    // Usually "model still importing": keep walking next frame.
                    spawnFailed++;
                    continue;
                }
                added++;
                if (System.Diagnostics.Stopwatch.GetTimestamp() > treeDeadline)
                {
                    samplesLeft = 0;
                    break;
                }
            }
        }

        if (!moving && added == 0 && spawnFailed == 0 && samplesLeft > 0)
        {
            _treeSettled = true;
            _treeSettledDir = camDir;
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
        _carpetProceduralTreeTokens.Clear();
        _treeShellR.Clear();
        _carpetUsedFallbackTrees = false;
    }

    void RemoveProceduralCarpetTrees()
    {
        if (_carpetProceduralTreeTokens.Count == 0)
            return;
        foreach (var token in _carpetProceduralTreeTokens.ToArray())
            RemoveLocalCarpetTree(token);
        _carpetProceduralTreeTokens.Clear();
    }

    void RemoveLocalCarpetTree(int token)
    {
        if (_carpetTrees.TryGetValue(token, out var go))
            go?.RemoveFromParent();
        _carpetTrees.Remove(token);
        _carpetTreeDirs.Remove(token);
        _carpetProceduralTreeTokens.Remove(token);
        _treeShellR.Remove(token);
    }

    void EnsureLocalTreeModels()
    {
        if (_localTreeScanDone && _localTreeModels.Count > 0)
            return;
        if (ProjectService.Current == null)
            return;
        // No profile model on disk yet (procedural trees only, or assets still copying):
        // look again every two seconds. A scan probes the disk for every profile entry,
        // and with nothing found the tree and coral ticks both ran it every frame.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_localTreeScanDone && now - _localTreeScanAt < System.Diagnostics.Stopwatch.Frequency * 2)
            return;
        _localTreeScanAt = now;
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
            // Underwater profile meshes stay out of the land tree carpet.
            if (string.Equals(profile.Id, "Ocean", StringComparison.OrdinalIgnoreCase))
            {
                RememberCoralModels(profile);
                continue;
            }
            AddProfile(profile);
        }
    }

    void RememberCoralModels(VegetationProfile profile)
    {
        var items = profile.TreeItems;
        if (items == null) return;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null || string.IsNullOrWhiteSpace(it.ModelPath)) continue;
            if (!IsCoralAsset(it.ModelPath) && !IsSupportedModelPath(it.ModelPath)) continue;
            string path = PlanetAssetIO.NormalizeAssetReference(it.ModelPath);
            bool have = false;
            for (int c = 0; c < _coralModels.Count; c++)
            {
                if (string.Equals(_coralModels[c], path, StringComparison.OrdinalIgnoreCase))
                {
                    have = true;
                    break;
                }
            }
            if (!have)
            {
                _coralModels.Add(path);
                string? abs = ResolveTreeModelAbsPath(path);
                if (!string.IsNullOrWhiteSpace(abs))
                    RequestTreeTemplateLoad(abs);
            }
        }
    }

    static bool IsCoralAsset(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string n = path.Replace('\\', '/');
        return n.Contains("/coral", StringComparison.OrdinalIgnoreCase)
            || n.Contains("coral_", StringComparison.OrdinalIgnoreCase);
    }

    bool WaitForProfileTreeFbxs()
    {
        if (_localTreeModels.Count == 0)
            return true;

        int ready = CountReadyLocalTreeModels();
        if (ready > 0)
        {
            // Drop only procedural stand-ins. Clearing the whole carpet made real trees
            // vanish and reappear every time another FBX finished.
            if (_carpetUsedFallbackTrees)
            {
                RemoveProceduralCarpetTrees();
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
        // A synchronous FBX import on the game thread is a multi-second freeze at
        // scene start. Only fall back to it once the background loader has gone
        // idle without producing a template (i.e. it actually failed).
        if (_treeImportWaitSec >= 4f && Volatile.Read(ref s_templateLoaderRunning) == 0 && s_pendingTemplateLoads.IsEmpty)
        {
            _treeImportWaitSec = 0f;
#if !XBOX_UWP
            // Importing an FBX here runs on the game thread. On the Xbox player that
            // call does not return and the frame stops after the background loads finish.
            TryForceLoadProfileTreesOnMainThread();
#endif
        }

        // Profile has real FBXs - do not plant white procedural stand-ins.
        return CountReadyLocalTreeModels() > 0;
    }

    int CountReadyLocalTreeModels()
    {
        RefreshReadyTreeModels();
        return _readyTreeScratch.Count;
    }

    /// <summary>
    /// Fill <see cref="_readyTreeScratch"/> with the profile models whose template has loaded.
    /// Rebuilt only when a template was added or dropped. The tree tick asks every frame, and
    /// each ask used to resolve every model's path and build its cache key again.
    /// </summary>
    void RefreshReadyTreeModels()
    {
        int version = Volatile.Read(ref s_treeTemplateVersion);
        if (version == _readyTreeVersion && _readyTreeModelCount == _localTreeModels.Count)
            return;
        _readyTreeVersion = version;
        _readyTreeModelCount = _localTreeModels.Count;
        _readyTreeScratch.Clear();
        for (int i = 0; i < _localTreeModels.Count; i++)
        {
            string? abs = ResolveTreeModelAbsPath(_localTreeModels[i]);
            if (!string.IsNullOrWhiteSpace(abs) && TryGetReadyTreeTemplate(abs, out _))
                _readyTreeScratch.Add(_localTreeModels[i]);
        }
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
        if (IsCoralAsset(stored))
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
        // Integer hash: Fract(token * 0.127f) on a ~1e9 token has no fractional bits, so
        // every tree got the same scale and faced the same way.
        float scale = 1.15f + (CellHash((uint)token ^ 0x3C6EF372u) & 0xFFFF) * (0.45f / 65536f);
        float treePad = GetTreeRadialOutwardPadding(scale);
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var surface = VegetationWorldPoint(_terrain, planetW, dir, treePad);
        var placeUp = ResolvePlanetTreeWorldUp(radialW, surfN);
        float yaw = CarpetCellYaw(token);

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
            {
                _carpetUsedFallbackTrees = true;
                _carpetProceduralTreeTokens.Add(token);
            }
        }
        if (!spawned)
        {
            go.RemoveFromParent();
            return false;
        }

        _carpetTrees[token] = go;
        _carpetTreeDirs[token] = dir;
        RememberTreeGround(token, dir, _treeShellR);
        return true;
    }

    /// <summary>Underwater profile meshes. Plant a few on the seabed near the camera.</summary>
    void TickUnderwaterCoral(SN.Vector3 cameraPos, float dt)
    {
        if (_terrain?.Config == null || !_terrain.EnableWater) return;
        if (_coralModels.Count == 0)
            EnsureLocalTreeModels();
        if (_coralModels.Count == 0) return;

        _coralTick += dt;
        if (_coralTick < 0.4f) return;
        _coralTick = 0f;

        var camLocal = _terrain.WorldToLocal(cameraPos);
        if (camLocal.LengthSquared() < 1e-6f) return;
        var camDir = SN.Vector3.Normalize(camLocal);
        float radius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);

        float keep = 90f;
        float maxDirDeltaSq = (keep / radius) * (keep / radius);
        var drop = new List<int>();
        foreach (var kv in _coralPatches)
        {
            var dir = RadialDirFromLocalPosition(new SN.Vector3(
                (float)kv.Value.Transform.Position.X,
                (float)kv.Value.Transform.Position.Y,
                (float)kv.Value.Transform.Position.Z));
            if ((camDir - dir).LengthSquared() > maxDirDeltaSq)
                drop.Add(kv.Key);
        }
        for (int i = 0; i < drop.Count; i++)
        {
            if (_coralPatches.TryGetValue(drop[i], out var old))
                old?.RemoveFromParent();
            _coralPatches.Remove(drop[i]);
        }

        const int cap = 28;
        if (_coralPatches.Count >= cap) return;

        CarpetTangentBasis(camDir, out var t, out var b);
        float probe = 14f;
        int added = 0;
        float seaTop = CoralSeaTop(out float carveMax);
        for (int ring = 1; ring <= 6 && added < 2 && _coralPatches.Count < cap; ring++)
        {
            int perim = ring * 6;
            for (int k = 0; k < perim && added < 2; k++)
            {
                float ang = (k + ring * 0.37f) / perim * MathF.Tau;
                float dist = ring * probe;
                var dir = SafeNormalize(camDir * radius + t * (MathF.Cos(ang) * dist) + b * (MathF.Sin(ang) * dist), camDir);
                int token = CarpetCellToken(9, dir, 48, out _, out _, out _);
                if (_coralPatches.ContainsKey(token)) continue;
