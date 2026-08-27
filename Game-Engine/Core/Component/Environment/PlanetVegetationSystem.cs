#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    [Persist] public int MaxGrassClumpsPerLeaf { get; set; } = 6;
    [Persist] public float TreeBaseHeight { get; set; } = 7f;
    [Persist] public float GrassBaseHeight { get; set; } = 0.85f;
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
    [Persist] public int MaxRuntimeGrassPatches { get; set; } = 288;
    [Persist] public int MaxAssetSpawnsPerUpdate { get; set; } = 2;
    [Persist] public int GrassBladesPerPatch { get; set; } = 20;
    [Persist] public int MaxActiveAssetGrassPatches { get; set; } = 96;
    [Persist] public int MaxActiveAssetTrees { get; set; } = 96;
    [Persist] public float AssetPlacementActivationDistanceMultiplier { get; set; } = 1.25f;
    [Persist] public int MaxStoredPlacements { get; set; } = 12000;

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

    public GameObject? RuntimeDrawRoot => _runtimeRoot;
    public GameObject? TerrainGameObject => _terrain?.gameObject;

    public int ActiveLeafGroups => _leafEntries.Count;
    public int ActiveVegetationInstances => _leafEntries.Values.Sum(v => v.Count);
    /// <summary>Count of entries deserialized from the .planet asset (before proximity spawn).</summary>
    public int StoredPlacementCount => _assetPlacements.Count;
    public int LastSpawnedThisUpdate { get; private set; }
    public int LastDespawnedThisUpdate { get; private set; }

    const int MaxDespawnsPerRefresh = 10;
    const float VegRefreshMoveThresholdSq = 100f; // 10 m

    readonly Dictionary<string, List<Entry>> _leafEntries = new();
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);
    PlanetTerrain? _terrain;
    float _updateAccum;
    float _wetness;
    float _snow;
    float _windMultiplier = 1f;
    bool _manualSpawnPass;
    SN.Vector3 _lastVegCamPos = new(float.NaN);
    readonly List<PlanetVegetationPlacement> _assetPlacements = new();
    int _assetSpawnCursor;
    readonly Dictionary<int, Entry> _assetActive = new();
    GameObject? _runtimeRoot;

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
    const string TreeTemplateCacheKeySuffix = "|hier_v19_grasstuft";
    const string DefaultPlanetGrassTexturePath = "Assets/textures/Grass/Simple Grass_01.psd";
    const string DefaultPlanetGrassModelPath = "Assets/fbx/textures/Grass/Meadow_Grass_01_Var4.FBX";
    const int MaxImportedTreeTriangles = 48000;
    const long MaxImportedTreeSourceBytes = 1_800_000;

    public override void Awake()
    {
        _terrain = GetComponent<PlanetTerrain>();
        _vegProfiles = VegetationProfileLibrary.LoadAll();
        if (!s_activeSystems.Contains(this))
            s_activeSystems.Add(this);
        EnsureRuntimeRoot();
    }

    public override void Update()
    {
        if (!AutoSpawn) return;
        if (_terrain == null || _terrain.ChunkManager == null || _terrain.gameObject == null || _terrain.Config == null) return;

        float dt = Math.Max(0f, (float)Time.deltaTime);
        if (dt <= 1e-6f) dt = 1f / 60f;
        _updateAccum += dt;
        if (_updateAccum < Math.Max(0.05f, UpdateIntervalSeconds))
            return;
        _updateAccum = 0f;

        var camPos = ResolveCameraPosition();
        if (!float.IsNaN(_lastVegCamPos.X))
        {
            bool pendingWork = _manualSpawnPass
                || (_terrain.ChunkManager?.PendingCompletedJobs ?? 0) > 0
                || (_terrain.ChunkManager?.ActiveJobs ?? 0) > 0;
            if (!pendingWork && SN.Vector3.DistanceSquared(camPos, _lastVegCamPos) < VegRefreshMoveThresholdSq
                && ActiveVegetationInstances > 0 && !UsePlanetAssetPlacements)
            {
                return;
            }
        }
        _lastVegCamPos = camPos;

        if (UsePlanetAssetPlacements && _assetPlacements.Count > 0)
            SpawnFromAssetPlacements(clearExisting: false);

        RefreshVegetation();
        if (LastSpawnedThisUpdate > 0 || LastDespawnedThisUpdate > 0)
            SceneService.NotifyChanged();
    }

    public override void OnDestroy()
    {
        s_activeSystems.Remove(this);
        foreach (var group in _leafEntries.Values)
            for (int i = 0; i < group.Count; i++)
                group[i].GameObject.RemoveFromParent();
        _leafEntries.Clear();
        _runtimeRoot?.RemoveFromParent();
        _runtimeRoot = null;
    }

    public void ApplyWeather(float wetness, float snowCoverage, float windMultiplier)
    {
        _wetness = Math.Clamp(wetness, 0f, 1f);
        _snow = Math.Clamp(snowCoverage, 0f, 1f);
        _windMultiplier = Math.Max(0f, windMultiplier);
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
        bool hasPlacements = _assetPlacements.Count > 0;
        UsePlanetAssetPlacements = AutoUseSavedPlacementsWhenPresent && hasPlacements;
        if (AutoSpawnWhenUsingSavedPlacements && UsePlanetAssetPlacements && hasPlacements)
            AutoSpawn = true;

        _terrain ??= GetComponent<PlanetTerrain>();
        // Drop disk snapshot only once memory matches an intentional import result.
        if (_assetPlacements.Count > 0)
            _terrain?.ReleaseVegetationDiskSnapshotAfterImport();
        else if (data != null && data.Placements != null && data.Placements.Length == 0)
            _terrain?.ReleaseVegetationDiskSnapshotAfterImport();
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
            _assetActive.Clear();
            LastDespawnedThisUpdate = 0;
        }

        bool spawned = false;
        _manualSpawnPass = true;
        try
        {
            if (UsePlanetAssetPlacements && _assetPlacements.Count > 0)
            {
                int? warmBudget = _manualSpawnPass
                    ? Math.Clamp(Math.Max(_assetPlacements.Count, 512), 128, 2048)
                    : null;
                SpawnFromAssetPlacements(clearExisting, warmBudget);
            }
            else
            {
                if (UsePlanetAssetPlacements)
                {
                    _assetPlacements.Clear();
                    _assetSpawnCursor = 0;
                }
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

    void RefreshVegetation()
    {
        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;

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
        float maxDist = worldRadius * Math.Max(0.5f, ActiveDistanceMultiplier);
        float maxDistSq = maxDist * maxDist;

        leaves.Sort((a, b) =>
        {
            var aw = LocalSpherePointToWorld(planetW, a.WorldCentre(worldRadius));
            var bw = LocalSpherePointToWorld(planetW, b.WorldCentre(worldRadius));
            return SN.Vector3.DistanceSquared(camPos, aw).CompareTo(SN.Vector3.DistanceSquared(camPos, bw));
        });

        bool fullPopulate = _manualSpawnPass && FullBiomePopulate;
        int maxLeaves = fullPopulate ? leaves.Count : Math.Max(16, MaxTrackedLeaves);
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        int spawnTickCap = ResolveSpawnTickCap(fullPopulate);
        for (int i = 0; i < leaves.Count && activeKeys.Count < maxLeaves; i++)
        {
            if (!fullPopulate && LastSpawnedThisUpdate >= spawnTickCap)
                break;

            var leaf = leaves[i];
            var leafCenter = LocalSpherePointToWorld(planetW, leaf.WorldCentre(worldRadius));
            if (!fullPopulate && SN.Vector3.DistanceSquared(camPos, leafCenter) > maxDistSq)
                continue;

            string key = $"{leaf.Face}:{leaf.LodLevel}:{leaf.U0:F4}:{leaf.V0:F4}:{leaf.U1:F4}:{leaf.V1:F4}";
            activeKeys.Add(key);
            EnsureLeafEntries(leaf, key);
            UpdateLeafVitality(key);
            if (!fullPopulate && LastSpawnedThisUpdate >= spawnTickCap)
                break;
        }

        if (CullVegetationWhenLeafNotActive)
        {
            var stale = new List<string>();
            foreach (var k in _leafEntries.Keys)
                if (!activeKeys.Contains(k))
                    stale.Add(k);
            for (int i = 0; i < stale.Count && LastDespawnedThisUpdate < MaxDespawnsPerRefresh; i++)
                DespawnLeaf(stale[i]);
        }
    }

    int ResolveSpawnTickCap(bool fullPopulate)
    {
        if (fullPopulate || _manualSpawnPass)
            return Math.Max(16, _terrain?.Config?.MaxVegetationSpawnsPerUpdate ?? 16);
        int configCap = Math.Max(1, _terrain?.Config?.MaxVegetationSpawnsPerUpdate ?? 4);
        return SceneService.PlayMode ? Math.Min(2, configCap) : Math.Min(6, configCap);
    }

    void SpawnFromAssetPlacements(bool clearExisting, int? budgetOverride = null)
    {
        if (_terrain?.Config == null) return;
        if (clearExisting)
        {
            foreach (var group in _leafEntries.Values)
                for (int i = 0; i < group.Count; i++)
                    group[i].GameObject.RemoveFromParent();
            _leafEntries.Clear();
            _assetSpawnCursor = 0;
            _assetActive.Clear();
        }

        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;
        int budget = Math.Clamp(budgetOverride ?? MaxAssetSpawnsPerUpdate, 1, 2048);
        const string key = "asset";
        if (!_leafEntries.TryGetValue(key, out var entries))
        {
            entries = new List<Entry>();
            _leafEntries[key] = entries;
        }

        float worldRadius = Math.Max(1f, _terrain.Config.EffectiveWorldRadius);
        float maxDist = worldRadius * Math.Max(0.2f, AssetPlacementActivationDistanceMultiplier);
        float maxDistSq = maxDist * maxDist;
        var camPos = ResolveCameraPosition();

        var wantGrass = CollectNearestPlacementIndices(
            isGrass: true,
            maxCount: Math.Max(4, MaxActiveAssetGrassPatches),
            maxDistSq: maxDistSq,
            cameraPos: camPos);
        var wantTrees = CollectNearestPlacementIndices(
            isGrass: false,
            maxCount: Math.Max(8, MaxActiveAssetTrees),
            maxDistSq: maxDistSq,
            cameraPos: camPos);

        // Stable union for despawn checks (order does not matter).
        var wantedSet = new HashSet<int>(wantGrass);
        wantedSet.UnionWith(wantTrees);

        // Despawn no-longer-needed placements first to free memory quickly.
        var stale = new List<int>();
        foreach (var kv in _assetActive)
            if (!wantedSet.Contains(kv.Key))
                stale.Add(kv.Key);
        for (int i = 0; i < stale.Count; i++)
        {
            int idx = stale[i];
            if (_assetActive.TryGetValue(idx, out var old))
            {
                old.GameObject.RemoveFromParent();
                _assetActive.Remove(idx);
                LastDespawnedThisUpdate++;
            }
        }

        // Grass first: with tiny per-frame budgets (default MaxAssetSpawnsPerUpdate = 2), arbitrary
        // mixed iteration could starve grass for a long time after load. Lists are disjoint (IsGrass).
        var spawnOrder = new List<int>(wantGrass.Count + wantTrees.Count);
        spawnOrder.AddRange(wantGrass);
        spawnOrder.AddRange(wantTrees);

        // Spawn only a small budget per tick unless caller overrides (manual spawn / deferred hydrate).
        foreach (var idx in spawnOrder)
        {
            if (budget <= 0) break;
            if (_assetActive.ContainsKey(idx)) continue;
            if (idx < 0 || idx >= _assetPlacements.Count) continue;
            var p = _assetPlacements[idx];
            if (p.IsGrass && _assetActive.Count(e => e.Value.IsGrass) >= Math.Max(4, MaxActiveAssetGrassPatches))
                continue;
            var go = SpawnVegetationFromPlacement(p, idx);
            if (go == null || go.Parent == null) continue;
            if (p.IsGrass && !HasMeshFilterDeep(go))
            {
                go.RemoveFromParent();
                continue;
            }
            var entry = new Entry
            {
                GameObject = go,
                Biome = ResolveBiomeByName(p.BiomeName) ?? _terrain.OceanBiome,
                BiomeName = p.BiomeName ?? "",
                IsGrass = p.IsGrass,
                BaseScale = Math.Max(0.01f, p.Scale),
                Vitality = 1f,
                SurfaceDir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY),
                YawDeg = p.YawDeg,
                PrefabPath = p.PrefabPath ?? "",
                ModelPath = p.ModelPath ?? "",
                TexturePath = p.TexturePath ?? "",
            };
            _assetActive[idx] = entry;
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
        int warm = Math.Clamp(Math.Max(_assetPlacements.Count, 256), 96, 1024);
        SpawnFromAssetPlacements(clearExisting: false, budgetOverride: warm);
    }

    List<int> CollectNearestPlacementIndices(bool isGrass, int maxCount, float maxDistSq, SN.Vector3 cameraPos)
    {
        var result = new List<int>(Math.Max(0, maxCount));
        if (_assetPlacements.Count == 0 || maxCount <= 0 || _terrain == null)
            return result;

        var planetW = GetPlanetWorldMatrix();
        var bestIdx = new int[maxCount];
        var bestDist = new float[maxCount];
        int count = 0;
        for (int k = 0; k < maxCount; k++) bestDist[k] = float.MaxValue;

        for (int i = 0; i < _assetPlacements.Count; i++)
        {
            var p = _assetPlacements[i];
            if (p.IsGrass != isGrass) continue;
            var dir = SafeNormalize(new SN.Vector3(p.DirX, p.DirY, p.DirZ), SN.Vector3.UnitY);
            var wp = VegetationWorldPoint(_terrain, planetW, dir, treePad: 0f);
            float d2 = SN.Vector3.DistanceSquared(cameraPos, wp);
            if (d2 > maxDistSq) continue;

            if (count < maxCount)
            {
                bestIdx[count] = i;
                bestDist[count] = d2;
                count++;
                continue;
            }

            int farI = 0;
            float farD = bestDist[0];
            for (int j = 1; j < maxCount; j++)
            {
                if (bestDist[j] <= farD) continue;
                farD = bestDist[j];
                farI = j;
            }
            if (d2 < farD)
            {
                bestIdx[farI] = i;
                bestDist[farI] = d2;
            }
        }

        for (int i = 0; i < count; i++)
            result.Add(bestIdx[i]);
        return result;
    }

    void EnsureLeafEntries(QuadNode leaf, string key)
    {
        if (!_leafEntries.TryGetValue(key, out var entries))
        {
            entries = new List<Entry>();
            _leafEntries[key] = entries;
        }

        if (_terrain?.Config == null) return;
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
        biome = ResolveLandVegetationBiome(biome, leafDir);
        var profile = ResolveVegetationProfile(biome);

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
        int targetTrees = Math.Clamp((int)MathF.Round(biome.TreeDensity * treeDensityMul * treeCapPerLeaf), 0, treeCapPerLeaf);
        int targetGrass = Math.Clamp((int)MathF.Round(biome.VegetationDensity * grassDensityMul * grassCapPerLeaf), 0, grassCapPerLeaf);
        bool hasTreeItems = profile?.TreeItems?.Any(it => it != null && it.Weight > 0f) == true;
        bool hasGrassItems = profile?.GrassItems?.Any(it => it != null && it.Weight > 0f) == true;
        if (IsAboveSea(leafDir))
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
            if (grassCount == 0 && targetGrass > 0 && spawnBudget > 0 && currentTotal < hardCap
                && globalGrassCount < Math.Max(8, MaxRuntimeGrassPatches))
            {
                if (UsePlanetAssetPlacements && _manualSpawnPass)
                {
                    if (_assetPlacements.Count < Math.Max(256, MaxStoredPlacements))
                    {
                        var p = BuildPlacementFromLeaf(leaf, biome, isGrass: true, grassCount + 97, profile);
                        _assetPlacements.Add(p);
                        grassCount++;
                    }
                }
                else
                {
                    int clumps = Math.Min(targetGrass, Math.Max(1, spawnBudget));
                    var go = SpawnLeafGrassBatch(leaf, biome, clumps, profile, HashLeaf(leaf, 97));
                    if (go != null && HasMeshFilterDeep(go))
                    {
                        var batchDir = CubeSphereMath.FaceUVToDirection(leaf.Face, (leaf.U0 + leaf.U1) * 0.5f, (leaf.V0 + leaf.V1) * 0.5f);
                        entries.Add(new Entry
                        {
                            GameObject = go,
                            Biome = biome,
                            BiomeName = biome.Name,
                            IsGrass = true,
                            BaseScale = 1f,
                            SurfaceDir = batchDir,
                            YawDeg = 0f,
                            ModelPath = "",
                            TexturePath = DefaultPlanetGrassTexturePath,
                            PrefabPath = ""
                        });
                        grassCount = 1;
                        globalGrassCount++;
                        currentTotal++;
                        spawnBudget--;
                        LastSpawnedThisUpdate++;
                    }
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
                _assetPlacements.Add(p);
                grassCount++;
                continue;
            }
            if (globalGrassCount >= Math.Max(8, MaxRuntimeGrassPatches))
                break;
            var go = SpawnVegetationObject(leaf, biome, isGrass: true, grassCount + 97, profile);
            if (go.Parent == null || !HasMeshFilterDeep(go))
            {
                grassCount++;
                spawnBudget--;
                continue;
            }
            var dir = SafeNormalize(new SN.Vector3(
                (float)go.Transform.Position.X - center.X,
                (float)go.Transform.Position.Y - center.Y,
                (float)go.Transform.Position.Z - center.Z), SN.Vector3.UnitY);
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
                YawDeg = (float)go.Transform.Rotation.Y,
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
                _assetPlacements.Add(p);
                treeCount++;
                continue;
            }
            var go = SpawnVegetationObject(leaf, biome, isGrass: false, treeCount + 17, profile);
            var dir = SafeNormalize(new SN.Vector3(
                (float)go.Transform.Position.X - center.X,
                (float)go.Transform.Position.Y - center.Y,
                (float)go.Transform.Position.Z - center.Z), SN.Vector3.UnitY);
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                BiomeName = biome.Name,
                IsGrass = false,
                BaseScale = (float)go.Transform.Scale.X,
                SurfaceDir = dir,
                YawDeg = (float)go.Transform.Rotation.Y,
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
            float harshness = Math.Clamp(_snow * 0.6f + _wetness * 0.2f, 0f, 1f);
            float growth = e.Biome.VegetationRegrowthRate * Math.Max(0.1f, e.Biome.SeasonalGrowthMultiplier) * (1f - harshness);
            float decay = e.Biome.VegetationDecayRate * harshness;
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
            else if (!e.IsGrass && e.GameObject != null)
            {
                // Imported-tree fast path may not include Tree behavior;
                // still apply vitality-driven scale.
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
            entries[i].GameObject.RemoveFromParent();
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
        if (_terrain?.gameObject == null || _terrain.Config == null) return null;
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
        SetLocalCrustPosition(go, dir, p.IsGrass ? 0f : treePad);
        SetSurfaceAlignedRotation(go, placeUp, yawDeg);

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
                SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                float s = Math.Max(0.01f, p.Scale);
                inst.Transform.Scale = new Vector3(s, s, s);
                if (p.IsGrass)
                    SeatGrassOnSurface(inst, surfaceGrass, radialW, placeUp, s);
                else
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, s);
                SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
                go.RemoveFromParent();
                return inst;
            }
        }

        if (p.IsGrass)
        {
            float minScale = Math.Max(0.01f, p.Scale);
            string grassModel = ResolveGrassModelPath(p.ModelPath, null);
            if (TrySpawnImportedVegetationMesh(go, grassModel, isGrass: true, minScale, surfaceGrass, radialW, placeUp, yawDeg))
                return go;

            go.RemoveFromParent();
            return go;
        }

        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
        {
            if (TrySpawnImportedVegetationMesh(go, p.ModelPath, isGrass: false, Math.Max(0.01f, p.Scale), surfaceTree, radialW, placeUp, yawDeg))
                return go;
        }

        var tree = go.AddBehavior<Tree>();
        tree.IsVegetation = true;
        tree.Shape = CanopyShape.Sphere;
        tree.TrunkHeight = TreeBaseHeight;
        tree.TrunkRadiusBottom = 0.16f;
        tree.TrunkRadiusTop = 0.06f;
        tree.CanopyHeight = tree.TrunkHeight * 0.9f;
        tree.CanopyRadius = tree.TrunkHeight * 0.35f;
        tree.CanopySegments = 9;
        tree.WindSway = 0.55f;
        tree.WindSpeed = 0.95f;
        tree.RebuildTree();
        float scale = Math.Max(0.01f, p.Scale);
        go.Transform.Scale = new Vector3(scale, scale, scale);
        SinkTreeRootsToSurface(go, surfaceTree, radialW, placeUp, scale);
        return go;
    }

    GameObject SpawnVegetationObject(QuadNode leaf, BiomeDefinition biome, bool isGrass, int seedOffset, VegetationProfile? profile)
    {
        float u = Random01(HashLeaf(leaf, 11 + seedOffset));
        float v = Random01(HashLeaf(leaf, 31 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
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
        SetSurfaceAlignedRotation(go, placeUp, yawDeg);

        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));
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
                SetSurfaceAlignedRotation(inst, placeUp, yawDeg);
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
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, prefScale);
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

            string grassModel = ResolveGrassModelPath(item?.ModelPath, profile);
            if (TrySpawnImportedVegetationMesh(go, grassModel, isGrass: true, grassScale, surfaceGrass, radialW, placeUp, yawDeg))
                return go;

            go.RemoveFromParent();
            return go;
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
        }

        var tree = go.AddBehavior<Tree>();
        tree.IsVegetation = true;
        tree.Shape = CanopyShape.Sphere;
        tree.TrunkHeight = TreeBaseHeight * (0.8f + Random01(HashLeaf(leaf, 73 + seedOffset)) * 0.5f);
        tree.TrunkRadiusBottom = 0.16f;
        tree.TrunkRadiusTop = 0.06f;
        tree.CanopyHeight = tree.TrunkHeight * 0.9f;
        tree.CanopyRadius = tree.TrunkHeight * 0.35f;
        tree.CanopySegments = 9;
        tree.WindSway = 0.55f;
        tree.WindSpeed = 0.95f;

        tree.RebuildTree();
        if (go.Behaviors.OfType<TreeLOD>().FirstOrDefault() is TreeLOD lod)
        {
            // Planet vegetation keeps full meshes by default; billboard impostors
            // are tuned for flat-terrain Y-up trees and can look wrong on planets.
            lod.Lod1Start = 0f;
            lod.Lod2Start = 0f;
            lod.ImpostorStart = 0f;
            lod.BillboardAtlas = null;
        }

        float minScale = biome.TreeMinScale;
        float maxScale = biome.TreeMaxScale;
        if (item != null)
        {
            minScale *= item.MinScale;
            maxScale *= item.MaxScale;
        }
        if (maxScale < minScale)
        {
            float t = maxScale;
            maxScale = minScale;
            minScale = t;
        }
        float scale = minScale + (maxScale - minScale) * Random01(HashLeaf(leaf, 97 + seedOffset));
        go.Transform.Scale = new Vector3(scale, scale, scale);
        SinkTreeRootsToSurface(go, surfaceTree, radialW, placeUp, scale);

        return go;
    }

    PlanetVegetationPlacement BuildPlacementFromLeaf(QuadNode leaf, BiomeDefinition biome, bool isGrass, int seedOffset, VegetationProfile? profile)
    {
        float u = Random01(HashLeaf(leaf, 11 + seedOffset));
        float v = Random01(HashLeaf(leaf, 31 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
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
        if (meshExtent > 1e-3f)
        {
            float desired = isGrass
                ? Math.Max(0.4f, GrassBaseHeight * 1.65f)
                : Math.Max(5.5f, TreeBaseHeight * 1.35f);
            float fit = desired / meshExtent;
            if (isGrass)
                importedScale *= Math.Clamp(fit, 0.05f, 12f);
            else if (fit > 1f)
                importedScale *= Math.Min(fit, 24f);
            else if (fit < 0.4f)
                importedScale *= Math.Max(fit, 0.15f);
        }

        importedScale = isGrass
            ? Math.Clamp(importedScale, 0.18f, 3.6f)
            : Math.Clamp(importedScale, 0.08f, 18f);
        go.Transform.Scale = new Vector3(importedScale, importedScale, importedScale);
        ResolveImportedTreeMaterialsRecursive(go);
        if (isGrass)
            ApplyImportedGrassCardCutoutRecursive(go);
        SetSurfaceAlignedRotation(go, placeUp, yawDeg);
        ApplyImportedTreeMeshEulerCorrection(go);
        if (isGrass)
            SeatGrassOnSurface(go, surface, radialW, placeUp, importedScale);
        else
            SinkTreeRootsToSurface(go, surface, radialW, placeUp, importedScale);
        return true;
    }

    bool TrySetupImportedTree(GameObject go, string modelPath, out float meshExtent)
    {
        meshExtent = 0f;
        string? abs = ResolveTreeModelAbsPath(modelPath);
        if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
            return false;

        var tpl = GetOrLoadTreeTemplate(abs);

        if (tpl.Source != null)
        {
            AttachImportedTreeFromTemplate(tpl.Source, go);
            ApplyImportedTreeRenderSettingsRecursive(go);
            ResolveImportedTreeMaterialsRecursive(go);
            meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateHierarchyMeshExtent(go);
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

                meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateHierarchyMeshExtent(go);
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
        meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateMeshExtent(tpl.Mesh);

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
        string? meshPath = null;
        string? texture = null;

        void ConsiderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (IsSupportedModelPath(path))
                meshPath ??= path;
            else if (IsImageAssetPath(path))
                texture ??= path;
        }

        ConsiderPath(placementTexture);
        ConsiderPath(item?.ModelPath);
        if (profile != null)
            ConsiderPath(profile.GrassModelPath);

        texture ??= DefaultPlanetGrassTexturePath;
        painter.TexturePath = PlanetAssetIO.NormalizeAssetReference(texture);
        painter.CustomMeshPath = IsSupportedModelPath(meshPath)
            ? PlanetAssetIO.NormalizeAssetReference(meshPath!)
            : "";
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

    static ImportedTreeTemplate GetOrLoadTreeTemplate(string absModelPath)
    {
        absModelPath = Path.GetFullPath(absModelPath);
        bool grassOrient = LooksLikeImportedGrassModel(absModelPath);
        string cacheKey = absModelPath + TreeTemplateCacheKeySuffix + (grassOrient ? "|zupy" : "");
        lock (s_treeTemplateLock)
        {
            if (s_treeTemplateCache.TryGetValue(cacheKey, out var hit))
                return hit;
        }

        var tpl = new ImportedTreeTemplate();
        if (IsExcessivelyHeavySourceModel(absModelPath))
        {
            lock (s_treeTemplateLock)
            {
                if (!s_treeTemplateCache.ContainsKey(cacheKey))
                    s_treeTemplateCache[cacheKey] = tpl;
                return s_treeTemplateCache[cacheKey];
            }
        }

        try
        {
            var root = Importers.ModelImporter.ImportModel(absModelPath);
            PreferBestLodPerMeshStem(root);
            EnsurePlanetImportedTreeCrustLocal(root, forceGrassZUp: false, isGrass: grassOrient);
            ResolveImportedTreeMaterialsRecursive(root);
            int tris = CountHierarchyTriangles(root);
            if (tris <= 0)
            {
                lock (s_treeTemplateLock)
                {
                    if (!s_treeTemplateCache.ContainsKey(cacheKey))
                        s_treeTemplateCache[cacheKey] = tpl;
                    return s_treeTemplateCache[cacheKey];
                }
            }

            // Keep the live baked hierarchy. JSON mesh DTOs omit UVs, which makes
            // alpha-tested pine needles disappear (GPU samples 0,0 = transparent).
            tpl.Source = root;
            tpl.MeshExtent = EstimateHierarchyMeshExtent(root);

            var mf = FindFirstComponent<MeshFilter>(root);
            if (mf?.Mesh != null)
            {
                tpl.Mesh = mf.Mesh;
                if (tpl.MeshExtent < 1e-3f)
                    tpl.MeshExtent = EstimateMeshExtent(tpl.Mesh);
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

        lock (s_treeTemplateLock)
        {
            if (!s_treeTemplateCache.ContainsKey(cacheKey))
                s_treeTemplateCache[cacheKey] = tpl;
            return s_treeTemplateCache[cacheKey];
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

    /// <summary>Higher rank = cheaper mesh (LOD2/LOD3). Unnamed nodes are shared and kept.</summary>
    static int LodQualityRank(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        if (name.Contains("LOD3", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("LOD2", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("LOD1", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("LOD0", StringComparison.OrdinalIgnoreCase)) return 0;
        return -1;
    }

    /// <summary>Strip LOD1/LOD2 duplicates per mesh stem; keep LOD0 for each part (trunk + foliage).</summary>
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
            int best = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
                if (list[i].rank < best)
                    best = list[i].rank;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].rank != best)
                    list[i].go.RemoveFromParent();
            }
        }
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
        // Already sitting on XZ (Y is height, even if squat).
        if (ey <= ez && ey <= ex)
            return;
        // Paper-thin XY cards: already Y-up billboards (Z is thickness).
        float footprint = MathF.Max(ex, ey);
        if (ez < footprint * 0.18f)
            return;
        // Height along Z (Megascans / Unity Z-up) — same Rx(-90) as the Unity prefab.
        if (ez <= ey && ez <= ex)
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

    /// <summary>Many vegetation FBX files are Z-up; planet trees expect baked mesh +Y as trunk.</summary>
    static void ReorientImportedTreeMeshesToYUp(GameObject root, bool forceZUpToYUp = false)
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
        if (!forceZUpToYUp)
        {
            if (ez <= ey * 1.12f || ez <= ex * 1.05f)
                return;
        }
        else if (ey > ez * 1.15f)
        {
            // Already taller in Y than Z — extra Rx(-90) would flatten the clump onto XZ.
            return;
        }

        var rot = SN.Matrix4x4.CreateRotationX(-MathF.PI * 0.5f);
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

    static void EnsurePlanetImportedTreeCrustLocal(GameObject root, bool forceGrassZUp = false, bool isGrass = false)
    {
        if (root == null) return;
        ClearImportedModelPathRecursive(root);
        BakeImportedTreeToModelSpace(root, forceGrassZUp, isGrass);
        CollapseImportedTreeToRoot(root);
    }

    static bool LooksLikeImportedGrassModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string n = path.Replace('\\', '/').ToLowerInvariant();
        return n.Contains("grass") || n.Contains("meadow") || n.Contains("weed") || n.Contains("fern") || n.Contains("turf");
    }

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
        // Steep faces: follow the sampled normal so a wide tuft lies on the slope
        // instead of standing radial and hovering downhill.
        float t = Math.Clamp(0.40f + (1f - align) * 0.60f, 0.38f, 1f);
        var blended = BlendRadialWithSurfaceNormal(radialW, surfNW, t);
        var up = ClampUpToMaxTiltFromRadial(radialW, blended, 78f);
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
    /// Orients local +Y along planet trunk-up in <strong>world space</strong>, with yaw around that axis.
    /// System.Numerics (and this engine’s <see cref="TransformUtil.WorldFromTransform"/>) apply rotations as
    /// <strong>row vectors</strong>: <c>v' = TransformNormal(v, R)</c> uses <strong>rows</strong> of R, so local +Y maps to the
    /// <strong>second row</strong> of R. We therefore build an orthonormal <em>column</em> basis [X|Y|Z] and
    /// <see cref="SN.Matrix4x4.Transpose"/> it before Euler extraction. Without this, trees followed a bogus
    /// world-Y–like frame and looked random across the sphere.
    /// Converts world alignment to <strong>local</strong> rotation when <paramref name="go"/> has a parent (e.g. rotated planet).
    /// </summary>
    /// <summary>
    /// Orients local +Y along planet trunk-up using an explicit rotation matrix (Euler garbles this on slopes).
    /// </summary>
    static void SetSurfaceAlignedRotation(GameObject go, SN.Vector3 trunkUpWorld, float yawDeg)
    {
        var mWorld = SN.Matrix4x4.Transpose(BuildTreeAlignMatrixColumnBasis(trunkUpWorld, yawDeg));
        if (go.Parent != null)
        {
            var pw = SceneGraphUtil.AccumulateWorld(go.Parent);
            if (SN.Matrix4x4.Invert(pw, out var invPw))
                mWorld = OrthonormalizeRotationPart(mWorld * invPw);
        }
        go.Transform.SetExplicitRotationMatrix(OrthonormalizeRotationPart(mWorld));
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
        if (!SN.Matrix4x4.Invert(pw, out var invPw))
            return TransformUtil.EulerDegreesFromRotationMatrix3x3(mRow);

        // Row order: v * M_child * M_parent — desired world rotation R_w gives R_child = R_w * M_parent^{-1}.
        var localCombined = mRow * invPw;
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
        if (!IsAboveSea(dir))
            return sampled;
        if (sampled.VegetationDensity > 0.05f || sampled.TreeDensity > 0.05f)
            return sampled;
        return ResolveBiomeByName("Grassland")
            ?? ResolveBiomeByName("Forest")
            ?? sampled;
    }

    bool IsAboveSea(SN.Vector3 dir)
    {
        if (_terrain?.Config == null) return true;
        float sea = _terrain.Config.SeaLevel;
        float surf = _terrain.SampleLocalCrustRadius(SafeNormalize(dir, SN.Vector3.UnitY));
        return surf >= sea + 1.5f;
    }

    BiomeDefinition? SampleLeafBiome(QuadNode leaf, int seedOffset)
    {
        float u = Random01(HashLeaf(leaf, 101 + seedOffset));
        float v = Random01(HashLeaf(leaf, 131 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        return _terrain!.Map?.GetDominantBiome(dir);
    }

    VegetationProfile? ResolveVegetationProfile(BiomeDefinition biome)
    {
        if (_vegProfiles.Count == 0)
            _vegProfiles = VegetationProfileLibrary.LoadAll();
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
        if (!string.IsNullOrWhiteSpace(it.ModelPath) && (IsSupportedModelPath(it.ModelPath) || IsPrefabPath(it.ModelPath)))
            return true;
        return false;
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
        if (_terrain != null && _terrain.LastCameraPosition.LengthSquared() > 1e-6f)
            return _terrain.LastCameraPosition;
        var cam = SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain)
               ?? SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
        return cam != null
            ? new SN.Vector3((float)cam.Transform.Position.X, (float)cam.Transform.Position.Y, (float)cam.Transform.Position.Z)
            : GetWorldCenter();
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

    GameObject? SpawnLeafGrassBatch(QuadNode leaf, BiomeDefinition biome, int clumpCount, VegetationProfile? profile, int seedSalt)
    {
        if (_terrain == null || clumpCount <= 0)
            return null;

        var leafDir = CubeSphereMath.FaceUVToDirection(leaf.Face, (leaf.U0 + leaf.U1) * 0.5f, (leaf.V0 + leaf.V1) * 0.5f);
        float patchRadius = PlanetGrassPatchRadius() * MathF.Sqrt(Math.Clamp(clumpCount, 1, 32));
        int maxBlades = SceneService.PlayMode ? 48 : 160;
        int bladeCount = Math.Clamp(clumpCount * Math.Max(4, GrassBladesPerPatch), 8, maxBlades);

        var go = new GameObject($"BiomeGrassBatch_{leaf.Face}_{leaf.LodLevel}");
        AttachSpawnedInstance(go);

        var painter = go.AddBehavior<VegetationPainter>();
        painter.GrassHeight = GrassBaseHeight;
        painter.RandomRotation = true;
        painter.WindStrength = Math.Clamp(0.35f + _windMultiplier * 0.5f, 0f, 3f);
        painter.WindSpeed = Math.Clamp(0.8f + _windMultiplier * 0.8f, 0f, 4f);
        var item = ChooseItem(profile, isGrass: true, seedSalt);
        ConfigurePlanetGrassPainter(painter, item, profile);

        int placed = painter.BuildOnPlanetPatch(_terrain, leafDir, patchRadius, bladeCount, sourceLeaf: leaf, notifyScene: false);
        if (placed <= 0)
        {
            go.RemoveFromParent();
            return null;
        }

        for (int i = 0; i < go.Children.Count; i++)
        {
            if (HideInstancesInHierarchy)
                go.Children[i].HideInHierarchy = true;
        }

        return go;
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
        => Math.Max(1.6f, (_terrain?.Config?.EffectiveWorldRadius ?? 1000f) * 0.0035f);

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
            h = h * 31 + leaf.LodLevel;
            h = h * 31 + (int)(leaf.U0 * 100000f);
            h = h * 31 + (int)(leaf.V0 * 100000f);
            h = h * 31 + salt;
            return h;
        }
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
