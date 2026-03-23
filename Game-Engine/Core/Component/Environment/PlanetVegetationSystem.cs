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
    [Persist] public bool AutoSpawn { get; set; } = false;
    [Persist] public int MaxTrackedLeaves { get; set; } = 48;
    [Persist] public float UpdateIntervalSeconds { get; set; } = 0.5f;
    [Persist] public float ActiveDistanceMultiplier { get; set; } = 2.2f;
    [Persist] public int MaxTreesPerLeaf { get; set; } = 3;
    [Persist] public int MaxGrassClumpsPerLeaf { get; set; } = 6;
    [Persist] public float TreeBaseHeight { get; set; } = 3f;
    [Persist] public float GrassBaseHeight { get; set; } = 0.55f;
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
    [Persist] public int MaxRuntimeGrassPatches { get; set; } = 72;
    [Persist] public int MaxAssetSpawnsPerUpdate { get; set; } = 2;
    [Persist] public int GrassBladesPerPatch { get; set; } = 10;
    [Persist] public int MaxActiveAssetGrassPatches { get; set; } = 36;
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
    /// The meshed shell is often slightly outside the analytical sample; without this, trunks sit inside the visible ground.
    /// </summary>
    [Persist] public float TreeRadialSurfaceBias { get; set; } = 0.48f;

    /// <summary>
    /// Added to <see cref="Transform.Rotation"/> after planet alignment when spawning <strong>imported</strong> tree meshes
    /// (biome/asset .fbx/.obj paths). Example: <c>(180,0,0)</c> if the asset’s trunk grows along <c>-Y</c> in file space.
    /// Prefab instances are unchanged—bake corrections into the prefab if needed.
    /// </summary>
    [Persist] public Vector3 ImportedTreeMeshEulerCorrection { get; set; } = new Vector3(0, 0, 0);

    /// <summary>
    /// When true, vegetation groups are removed if their quad-leaf key is not in the current
    /// streaming set. Keys include LOD level and UV bounds, which change whenever the planet
    /// quadtree splits or merges — causing constant despawn/respawn and "nothing sticks".
    /// Leave false (default) for stable vegetation; enable only if you need aggressive memory culling.
    /// </summary>
    [Persist] public bool CullVegetationWhenLeafNotActive { get; set; } = false;

    /// <summary>
    /// When true, instances are removed after ecosystem vitality drops to zero (weather/decay).
    /// When false (default), vitality still affects scale/wind but plants are not deleted.
    /// </summary>
    [Persist] public bool RemoveVegetationWhenVitalityExhausted { get; set; } = false;

    public int ActiveLeafGroups => _leafEntries.Count;
    public int ActiveVegetationInstances => _leafEntries.Values.Sum(v => v.Count);
    /// <summary>Count of entries deserialized from the .planet asset (before proximity spawn).</summary>
    public int StoredPlacementCount => _assetPlacements.Count;
    public int LastSpawnedThisUpdate { get; private set; }
    public int LastDespawnedThisUpdate { get; private set; }

    readonly Dictionary<string, List<Entry>> _leafEntries = new();
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);
    PlanetTerrain? _terrain;
    float _updateAccum;
    float _wetness;
    float _snow;
    float _windMultiplier = 1f;
    bool _manualSpawnPass;
    readonly List<PlanetVegetationPlacement> _assetPlacements = new();
    int _assetSpawnCursor;
    readonly Dictionary<int, Entry> _assetActive = new();

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
        /// <summary>Full import root serialized for cloning (multi-part trunk + foliage).</summary>
        public string? VisualHierarchyJson;
        public Mesh? Mesh;
        public Material? Material;
        public float MeshExtent;
    }

    static readonly Dictionary<string, ImportedTreeTemplate> s_treeTemplateCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object s_treeTemplateLock = new();
    /// <summary>Suffix so template cache invalidates when import/spawn pipeline changes.</summary>
    const string TreeTemplateCacheKeySuffix = "|hier_v2";

    public override void Awake()
    {
        _terrain = GetComponent<PlanetTerrain>();
        _vegProfiles = VegetationProfileLibrary.LoadAll();
    }

    public override void Update()
    {
        if (!AutoSpawn) return;
        if (_terrain == null || _terrain.ChunkManager == null || _terrain.gameObject == null || _terrain.Config == null) return;

        _updateAccum += Math.Max(0f, (float)Time.deltaTime);
        if (_updateAccum < Math.Max(0.05f, UpdateIntervalSeconds))
            return;
        _updateAccum = 0f;

        if (UsePlanetAssetPlacements && _assetPlacements.Count > 0)
        {
            SpawnFromAssetPlacements(clearExisting: false);
            return;
        }

        RefreshVegetation();
    }

    public override void OnDestroy()
    {
        foreach (var group in _leafEntries.Values)
            for (int i = 0; i < group.Count; i++)
                group[i].GameObject.RemoveFromParent();
        _leafEntries.Clear();
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
        UsePlanetAssetPlacements = data?.UseStoredPlacements == true
            || (AutoUseSavedPlacementsWhenPresent && hasPlacements);
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
        int maxLeaves = fullPopulate ? leaves.Count : Math.Max(8, MaxTrackedLeaves);
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count && activeKeys.Count < maxLeaves; i++)
        {
            var leaf = leaves[i];
            var leafCenter = LocalSpherePointToWorld(planetW, leaf.WorldCentre(worldRadius));
            if (!fullPopulate && SN.Vector3.DistanceSquared(camPos, leafCenter) > maxDistSq)
                continue;

            string key = $"{leaf.Face}:{leaf.LodLevel}:{leaf.U0:F4}:{leaf.V0:F4}:{leaf.U1:F4}:{leaf.V1:F4}";
            activeKeys.Add(key);
            EnsureLeafEntries(leaf, key);
            UpdateLeafVitality(key);
        }

        if (CullVegetationWhenLeafNotActive)
        {
            var stale = new List<string>();
            foreach (var k in _leafEntries.Keys)
                if (!activeKeys.Contains(k))
                    stale.Add(k);
            for (int i = 0; i < stale.Count; i++)
                DespawnLeaf(stale[i]);
        }
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
            if (go == null) continue;
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
            float surfR = _terrain.SampleSurfaceRadius(dir);
            var wp = LocalSpherePointToWorld(planetW, dir * surfR);
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
        int spawnBudget = fullPopulate
            ? Math.Max(64, hardCap - currentTotal)
            : Math.Max(8, _terrain.Config.MaxVegetationSpawnsPerUpdate) - LastSpawnedThisUpdate;
        if (spawnBudget <= 0) return;

        var sample = SampleLeafBiome(leaf, seedOffset: 0);
        var biome = sample ?? _terrain.OceanBiome;
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
        if (_manualSpawnPass)
        {
            // Manual spawn should always materialize visible content when profile items exist.
            if (targetTrees <= 0 && hasTreeItems && treeCapPerLeaf > 0)
                targetTrees = 1;
            if (targetGrass <= 0 && hasGrassItems && grassCapPerLeaf > 0)
                targetGrass = 1;
        }

        int treeCount = entries.Count(e => !e.IsGrass);
        int grassCount = entries.Count(e => e.IsGrass);
        int globalGrassCount = _leafEntries.Values.Sum(v => v.Count(e => e.IsGrass));
        var center = GetWorldCenter();

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

        while (grassCount < targetGrass && spawnBudget > 0 && currentTotal < hardCap)
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
            var dir = SafeNormalize(new SN.Vector3(
                (float)go.Transform.Position.X - center.X,
                (float)go.Transform.Position.Y - center.Y,
                (float)go.Transform.Position.Z - center.Z), SN.Vector3.UnitY);
            var painter = go.Behaviors.OfType<VegetationPainter>().FirstOrDefault();
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                BiomeName = biome.Name,
                IsGrass = true,
                BaseScale = (float)go.Transform.Scale.X,
                SurfaceDir = dir,
                YawDeg = (float)go.Transform.Rotation.Y,
                ModelPath = PlanetAssetIO.NormalizeAssetReference(ResolveVegetationModelPathForAsset(go, true)),
                TexturePath = painter?.TexturePath ?? "",
                PrefabPath = go.PrefabPath ?? ""
            });
            grassCount++;
            globalGrassCount++;
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
        float surfR = _terrain.SampleSurfaceRadius(dir);
        float treePad = GetTreeRadialOutwardPadding(p.IsGrass ? 1f : Math.Max(0.01f, p.Scale));
        var surfaceGrass = LocalSpherePointToWorld(planetW, dir * surfR);
        var surfaceTree = LocalSpherePointToWorld(planetW, dir * (surfR + treePad));
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var placeUp = ResolvePlanetTreeWorldUp(radialW, surfN);
        float yawDeg = p.YawDeg;
        var biome = ResolveBiomeByName(p.BiomeName) ?? _terrain.OceanBiome;
        var go = new GameObject(p.IsGrass ? $"AssetGrass_{index}" : $"AssetTree_{index}");
        _terrain.gameObject.AddChild(go);
        SceneGraphUtil.SetPositionWorld(go, p.IsGrass ? surfaceGrass : surfaceTree);
        go.Transform.Rotation = p.IsGrass ? new Vector3(0f, 0f, 0f) : SurfaceAlignedRotation(go, placeUp, yawDeg);

        string prefabPath = p.PrefabPath ?? "";
        if (string.IsNullOrWhiteSpace(prefabPath) && (p.ModelPath ?? "").EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            prefabPath = p.ModelPath;
        if (!string.IsNullOrWhiteSpace(prefabPath))
        {
            var prefab = Prefab.Load(prefabPath);
            if (prefab != null && prefab.Instantiate(_terrain.gameObject) is GameObject inst)
            {
                SceneGraphUtil.SetPositionWorld(inst, p.IsGrass ? surfaceGrass : surfaceTree);
                inst.Transform.Rotation = p.IsGrass ? new Vector3(0f, 0f, 0f) : SurfaceAlignedRotation(inst, placeUp, yawDeg);
                float s = Math.Max(0.01f, p.Scale);
                inst.Transform.Scale = new Vector3(s, s, s);
                if (!p.IsGrass)
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, s);
                go.RemoveFromParent();
                return inst;
            }
        }

        if (p.IsGrass)
        {
            go.HideInHierarchy = true;
            var painter = go.AddBehavior<VegetationPainter>();
            painter.ActiveType = VegetationType.Grass;
            painter.RandomRotation = true;
            painter.Density = 1f;
            painter.MinScale = 0.9f;
            painter.MaxScale = 1.4f;
            painter.GrassHeight = Math.Clamp(Math.Max(0.28f, GrassBaseHeight * 1.25f), 0.28f, 1.40f);
            painter.GrassWidth = Math.Clamp(Math.Max(0.03f, GrassBaseHeight * 0.42f), 0.03f, 0.32f);
            painter.WindStrength = 0.8f;
            painter.WindSpeed = 1.25f;
            // Planet scale: 0.35× radius culled almost all patches while exploring the surface.
            float planetFade = Math.Max(500f, _terrain.Config.EffectiveWorldRadius * 2.75f);
            painter.FadeEndDistance = planetFade;
            painter.FadeStartDistance = planetFade * 0.65f;
            if (!string.IsNullOrWhiteSpace(p.TexturePath))
                painter.TexturePath = p.TexturePath;
            if (!string.IsNullOrWhiteSpace(p.ModelPath))
                painter.CustomMeshPath = p.ModelPath;
            float patchRadius = Math.Max(0.4f, _terrain.Config.EffectiveWorldRadius * 0.0035f);
            painter.BuildOnPlanetPatch(_terrain, dir, patchRadius, Math.Clamp(GrassBladesPerPatch, 4, 24));
            go.Transform.Scale = new Vector3(1f, 1f, 1f);
            return go;
        }

        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
        {
            float importedScale = Math.Max(0.01f, p.Scale);
            if (TrySetupImportedTree(go, p.ModelPath, out float meshExtent))
            {
                if (meshExtent > 1e-3f)
                {
                    float desired = Math.Max(1.2f, TreeBaseHeight * 0.9f);
                    float up = desired / meshExtent;
                    if (up > 1f)
                        importedScale *= Math.Min(up, 24f);
                }
                go.Transform.Scale = new Vector3(importedScale, importedScale, importedScale);
                ApplyImportedTreeMeshEulerCorrection(go);
                SinkTreeRootsToSurface(go, surfaceTree, radialW, placeUp, importedScale);
                return go;
            }
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
        if (!string.IsNullOrWhiteSpace(p.ModelPath) && IsSupportedModelPath(p.ModelPath))
            tree.ModelPath = p.ModelPath;
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
        float surfR = _terrain!.SampleSurfaceRadius(dir);
        var planetW = GetPlanetWorldMatrix();
        float approxTreeScale = isGrass ? 1f : (biome.TreeMinScale + biome.TreeMaxScale) * 0.5f;
        float treePad = GetTreeRadialOutwardPadding(approxTreeScale);
        var surfaceGrass = LocalSpherePointToWorld(planetW, dir * surfR);
        var surfaceTree = LocalSpherePointToWorld(planetW, dir * (surfR + treePad));
        var radialW = LocalDirectionToWorld(planetW, dir);
        var surfN = SamplePlanetSurfaceNormal(_terrain, planetW, dir);
        var placeUp = ResolvePlanetTreeWorldUp(radialW, surfN);

        var go = new GameObject(isGrass ? $"BiomeGrass_{leaf.Face}" : $"BiomeTree_{leaf.Face}");
        _terrain.gameObject!.AddChild(go);
        SceneGraphUtil.SetPositionWorld(go, isGrass ? surfaceGrass : surfaceTree);
        float yawDeg = Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;
        go.Transform.Rotation = isGrass ? new Vector3(0f, 0f, 0f) : SurfaceAlignedRotation(go, placeUp, yawDeg);

        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));
        string prefabPath = item?.PrefabPath ?? "";
        if (string.IsNullOrWhiteSpace(prefabPath) && !string.IsNullOrWhiteSpace(item?.ModelPath) && IsPrefabPath(item.ModelPath))
            prefabPath = item.ModelPath;
        if (!string.IsNullOrWhiteSpace(prefabPath))
        {
            var prefab = Prefab.Load(prefabPath);
            if (prefab != null && prefab.Instantiate(_terrain.gameObject!) is GameObject inst)
            {
                SceneGraphUtil.SetPositionWorld(inst, isGrass ? surfaceGrass : surfaceTree);
                inst.Transform.Rotation = isGrass ? new Vector3(0f, 0f, 0f) : SurfaceAlignedRotation(inst, placeUp, yawDeg);
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
                if (!isGrass)
                    SinkTreeRootsToSurface(inst, surfaceTree, radialW, placeUp, prefScale);
                go.RemoveFromParent();
                return inst;
            }
        }
        if (isGrass)
        {
            go.HideInHierarchy = true;
            // Grass patch geometry is already oriented per-blade in world space.
            // Keep the parent GO neutral to avoid extra local-space skew.
            go.Transform.Rotation = new Vector3(0f, 0f, 0f);

            float baseScale = biome.GrassMinScale;
            float maxBaseScale = biome.GrassMaxScale;
            if (item != null)
            {
                baseScale *= item.MinScale;
                maxBaseScale *= item.MaxScale;
            }
            if (maxBaseScale < baseScale) (baseScale, maxBaseScale) = (maxBaseScale, baseScale);

            var painter = go.AddBehavior<VegetationPainter>();
            painter.ActiveType = VegetationType.Grass;
            painter.RandomRotation = true;
            painter.Density = 1f;
            // Drive blade scale directly from biome/profile and avoid tiny, hard-to-see grass.
            painter.MinScale = Math.Clamp(baseScale * 0.95f, 0.55f, 1.60f);
            painter.MaxScale = Math.Clamp(Math.Max(painter.MinScale + 0.15f, maxBaseScale * 1.15f), 0.85f, 2.20f);
            painter.GrassHeight = Math.Clamp(Math.Max(0.28f, GrassBaseHeight * 1.25f), 0.28f, 1.40f);
            painter.GrassWidth = Math.Clamp(Math.Max(0.03f, GrassBaseHeight * 0.42f), 0.03f, 0.32f);
            painter.WindStrength = 0.8f;
            painter.WindSpeed = 1.25f;
            float planetFadeSpawn = Math.Max(500f, _terrain.Config!.EffectiveWorldRadius * 2.75f);
            painter.FadeEndDistance = planetFadeSpawn;
            painter.FadeStartDistance = planetFadeSpawn * 0.65f;
            if (item != null && !string.IsNullOrWhiteSpace(item.ModelPath))
            {
                if (IsSupportedModelPath(item.ModelPath))
                {
                    painter.CustomMeshPath = item.ModelPath;
                    painter.TexturePath = "";
                }
                else
                {
                    // Image-driven grass should flow through TexturePath to avoid mesh-path ambiguity.
                    painter.TexturePath = item.ModelPath;
                    painter.CustomMeshPath = "";
                }
            }

            float patchRadius = Math.Max(0.4f, _terrain.Config.EffectiveWorldRadius * 0.0035f);
            int bladeCount = Math.Clamp(GrassBladesPerPatch, 4, 24);
            painter.BuildOnPlanetPatch(_terrain, dir, patchRadius, bladeCount);
            go.Transform.Scale = new Vector3(1f, 1f, 1f);
            return go;
        }

        bool wantsImportedTree = item != null && IsSupportedModelPath(item.ModelPath);
        if (wantsImportedTree && TrySetupImportedTree(go, item!.ModelPath, out float importedMeshExtent))
        {
            float minScaleImported = biome.TreeMinScale * item!.MinScale;
            float maxScaleImported = biome.TreeMaxScale * item.MaxScale;
            if (maxScaleImported < minScaleImported)
            {
                float t2 = maxScaleImported;
                maxScaleImported = minScaleImported;
                minScaleImported = t2;
            }
            float importedScale = minScaleImported + (maxScaleImported - minScaleImported) * Random01(HashLeaf(leaf, 97 + seedOffset));
            if (importedMeshExtent > 1e-3f)
            {
                float desired = Math.Max(1.2f, TreeBaseHeight * 0.9f);
                float up = desired / importedMeshExtent;
                if (up > 1f)
                    importedScale *= Math.Min(up, 24f);
            }
            go.Transform.Scale = new Vector3(importedScale, importedScale, importedScale);
            ApplyImportedTreeMeshEulerCorrection(go);
            SinkTreeRootsToSurface(go, surfaceTree, radialW, placeUp, importedScale);
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

        if (item != null && IsSupportedModelPath(item.ModelPath))
        {
            tree.ModelPath = item.ModelPath;
        }

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
        if (item != null && IsSupportedModelPath(item.ModelPath))
        {
            // Upscale-only normalization: boosts tiny imported models without shrinking larger ones.
            var mesh = go.Behaviors.OfType<MeshFilter>().FirstOrDefault()?.Mesh;
            float meshExtent = EstimateMeshExtent(mesh);
            if (meshExtent > 1e-3f)
            {
                float desired = Math.Max(1.2f, TreeBaseHeight * 0.9f);
                float up = desired / meshExtent;
                if (up > 1f)
                    scale *= Math.Min(up, 24f);
            }
        }
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
            else if (isGrass && !IsSupportedModelPath(item.ModelPath))
                texturePath = item.ModelPath;
            else
                modelPath = item.ModelPath;
        }

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

    bool TrySetupImportedTree(GameObject go, string modelPath, out float meshExtent)
    {
        meshExtent = 0f;
        string? abs = ResolveTreeModelAbsPath(modelPath);
        if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
            return false;

        var tpl = GetOrLoadTreeTemplate(abs);
        string normPath = PlanetAssetIO.NormalizeAssetReference(modelPath);

        // Prefer full hierarchy so FBX/OBJ with separate trunk + leaf meshes all render.
        if (!string.IsNullOrWhiteSpace(tpl.VisualHierarchyJson))
        {
            var inst = SceneSerialization.DeserializeGameObjectFromJson(tpl.VisualHierarchyJson);
            if (inst != null)
            {
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

                ApplyImportedTreeModelPathRecursive(go, normPath);
                ApplyImportedTreeRenderSettingsRecursive(go);

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
        mf.ModelPath = normPath;
        meshExtent = tpl.MeshExtent > 1e-3f ? tpl.MeshExtent : EstimateMeshExtent(tpl.Mesh);

        var mr = go.Behaviors.OfType<MeshRenderer>().FirstOrDefault() ?? go.AddBehavior<MeshRenderer>();
        if (tpl.Material != null)
        {
            mr.MaterialPaths.Clear();
            mr.ResolvedMaterials.Clear();
            mr.Material = tpl.Material;
        }
        mr.DoubleSided = true;

        DisablePlanetImportedTreeLod(go);
        return true;
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

    static void ApplyImportedTreeModelPathRecursive(GameObject g, string normPath)
    {
        foreach (var b in g.Behaviors)
        {
            if (b is MeshFilter mf)
                mf.ModelPath = normPath;
        }
        foreach (var c in g.Children)
            ApplyImportedTreeModelPathRecursive(c, normPath);
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
        string cacheKey = absModelPath + TreeTemplateCacheKeySuffix;
        lock (s_treeTemplateLock)
        {
            if (s_treeTemplateCache.TryGetValue(cacheKey, out var hit))
                return hit;
        }

        var tpl = new ImportedTreeTemplate();
        try
        {
            var root = Importers.ModelImporter.ImportModel(absModelPath);
            // Warm global mesh cache so deserializing each tree instance does not ImportModel again.
            SceneSerialization.PrimeMeshPartsCacheFromImportRoot(absModelPath, root);
            try
            {
                tpl.VisualHierarchyJson = SceneSerialization.SerializeGameObjectToJson(root, includeAll: true);
            }
            catch { /* fallback to single-mesh */ }

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

        var p0 = dir * planet.SampleSurfaceRadius(dir);
        var pT = dirT * planet.SampleSurfaceRadius(dirT);
        var pB = dirB * planet.SampleSurfaceRadius(dirB);

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
        var items = isGrass ? profile?.GrassItems : profile?.TreeItems;
        if (items == null || items.Count == 0) return null;
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

    /// <summary>
    /// Model path to store in <see cref="PlanetVegetationPlacement"/> / <see cref="Entry"/>.
    /// Imported trees attach <see cref="MeshFilter"/> (with <see cref="MeshFilter.ModelPath"/>) on children
    /// (e.g. FBX sub-meshes), so root-only lookups miss the asset reference.
    /// </summary>
    static string ResolveVegetationModelPathForAsset(GameObject? go, bool isGrass)
    {
        if (go == null) return "";
        if (isGrass)
        {
            var painter = go.Behaviors.OfType<VegetationPainter>().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(painter?.CustomMeshPath))
                return painter.CustomMeshPath.Trim();
            return "";
        }

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
        float auto = 0.12f;
        if (_terrain?.Config != null)
            auto = Math.Clamp(_terrain.Config.EffectiveWorldRadius * 0.002f, 0.08f, 3.2f);
        float s = Math.Max(0.01f, approximateUniformScale);
        float scaleBump = Math.Clamp(s * 0.014f, 0f, 0.95f);
        return Math.Max(0f, TreeRadialSurfaceBias) + auto + scaleBump;
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

        float embedUp = Math.Clamp(uniformScale * 0.055f, 0.02f, 0.48f);
        float embedR = Math.Clamp(uniformScale * 0.06f, 0.025f, 0.52f);
        float targetMinUp = -embedUp;
        float targetMinRadial = -embedR - Math.Clamp(uniformScale * 0.02f, 0f, 0.38f);

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

        // 1) Bottom of mesh along trunk +Y (world), 2) tuck vs analytical shell along radial, 3) re-seat after radial move.
        ShiftAlong(trunkUp, targetMinUp);
        ShiftAlong(radialOutward, targetMinRadial);
        ShiftAlong(trunkUp, targetMinUp);
    }

    void ApplyImportedTreeMeshEulerCorrection(GameObject go)
    {
        var c = ImportedTreeMeshEulerCorrection;
        if (Math.Abs(c.X) < 1e-6 && Math.Abs(c.Y) < 1e-6 && Math.Abs(c.Z) < 1e-6)
            return;
        var r = go.Transform.Rotation;
        go.Transform.Rotation = new Vector3(r.X + c.X, r.Y + c.Y, r.Z + c.Z);
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
