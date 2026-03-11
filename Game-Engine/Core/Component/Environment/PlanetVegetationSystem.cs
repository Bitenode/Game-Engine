#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

[ComponentCategory("Environment")]
[Require(typeof(PlanetTerrain))]
public sealed class PlanetVegetationSystem : Behavior
{
    [Persist] public bool AutoSpawn { get; set; } = false;
    [Persist] public int MaxTrackedLeaves { get; set; } = 96;
    [Persist] public float UpdateIntervalSeconds { get; set; } = 0.5f;
    [Persist] public float ActiveDistanceMultiplier { get; set; } = 2.2f;
    [Persist] public int MaxTreesPerLeaf { get; set; } = 5;
    [Persist] public int MaxGrassClumpsPerLeaf { get; set; } = 10;
    [Persist] public float TreeBaseHeight { get; set; } = 3f;
    [Persist] public float GrassBaseHeight { get; set; } = 0.55f;

    public int ActiveLeafGroups => _leafEntries.Count;
    public int ActiveVegetationInstances => _leafEntries.Values.Sum(v => v.Count);
    public int LastSpawnedThisUpdate { get; private set; }
    public int LastDespawnedThisUpdate { get; private set; }

    readonly Dictionary<string, List<Entry>> _leafEntries = new();
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);
    PlanetTerrain? _terrain;
    float _updateAccum;
    float _wetness;
    float _snow;
    float _windMultiplier = 1f;

    sealed class Entry
    {
        public GameObject GameObject = null!;
        public BiomeDefinition Biome = null!;
        public bool IsGrass;
        public float Vitality = 1f;
    }

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

    void RefreshVegetation()
    {
        LastSpawnedThisUpdate = 0;
        LastDespawnedThisUpdate = 0;

        var cfg = _terrain!.Config!;
        var leaves = _terrain.ChunkManager!.GetRenderableLeaves();
        if (leaves.Count == 0) return;

        var center = GetWorldCenter();
        var camPos = ResolveCameraPosition();
        var localCam = camPos - center;
        float worldRadius = Math.Max(1f, cfg.EffectiveWorldRadius);
        float maxDist = worldRadius * Math.Max(0.5f, ActiveDistanceMultiplier);
        float maxDistSq = maxDist * maxDist;

        leaves.Sort((a, b) =>
            SN.Vector3.DistanceSquared(localCam, a.WorldCentre(worldRadius))
            .CompareTo(SN.Vector3.DistanceSquared(localCam, b.WorldCentre(worldRadius))));

        int maxLeaves = Math.Max(8, MaxTrackedLeaves);
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count && activeKeys.Count < maxLeaves; i++)
        {
            var leaf = leaves[i];
            var leafCenter = leaf.WorldCentre(worldRadius);
            if (SN.Vector3.DistanceSquared(localCam, leafCenter) > maxDistSq)
                continue;

            string key = $"{leaf.Face}:{leaf.LodLevel}:{leaf.U0:F4}:{leaf.V0:F4}:{leaf.U1:F4}:{leaf.V1:F4}";
            activeKeys.Add(key);
            EnsureLeafEntries(leaf, key);
            UpdateLeafVitality(key);
        }

        var stale = new List<string>();
        foreach (var k in _leafEntries.Keys)
            if (!activeKeys.Contains(k))
                stale.Add(k);
        for (int i = 0; i < stale.Count; i++)
            DespawnLeaf(stale[i]);
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

        int spawnBudget = Math.Max(8, _terrain.Config.MaxVegetationSpawnsPerUpdate) - LastSpawnedThisUpdate;
        if (spawnBudget <= 0) return;

        var sample = SampleLeafBiome(leaf, seedOffset: 0);
        var biome = sample ?? _terrain.OceanBiome;
        var profile = ResolveVegetationProfile(biome);

        float treeDensityMul = GetAverageDensityMultiplier(profile?.TreeItems);
        float grassDensityMul = GetAverageDensityMultiplier(profile?.GrassItems);
        int targetTrees = Math.Clamp((int)MathF.Round(biome.TreeDensity * treeDensityMul * MaxTreesPerLeaf), 0, MaxTreesPerLeaf);
        int targetGrass = Math.Clamp((int)MathF.Round(biome.VegetationDensity * grassDensityMul * MaxGrassClumpsPerLeaf), 0, MaxGrassClumpsPerLeaf);

        int treeCount = entries.Count(e => !e.IsGrass);
        int grassCount = entries.Count(e => e.IsGrass);

        while (treeCount < targetTrees && spawnBudget > 0 && currentTotal < hardCap)
        {
            var go = SpawnVegetationObject(leaf, biome, isGrass: false, treeCount + 17, profile);
            entries.Add(new Entry { GameObject = go, Biome = biome, IsGrass = false });
            treeCount++;
            currentTotal++;
            spawnBudget--;
            LastSpawnedThisUpdate++;
        }

        while (grassCount < targetGrass && spawnBudget > 0 && currentTotal < hardCap)
        {
            var go = SpawnVegetationObject(leaf, biome, isGrass: true, grassCount + 97, profile);
            entries.Add(new Entry { GameObject = go, Biome = biome, IsGrass = true });
            grassCount++;
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
                float scale = 0.55f + e.Vitality * 0.65f;
                var s = e.GameObject.Transform.Scale;
                s.X = scale;
                s.Y = scale;
                s.Z = scale;
                e.GameObject.Transform.Scale = s;
                t.WindSway = Math.Clamp((e.IsGrass ? 1f : 0.65f) * _windMultiplier, 0f, 3f);
                t.WindSpeed = Math.Clamp((e.IsGrass ? 1.35f : 1f) * _windMultiplier, 0f, 4f);
            }

            if (e.Vitality <= 0.02f)
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

    GameObject SpawnVegetationObject(QuadNode leaf, BiomeDefinition biome, bool isGrass, int seedOffset, VegetationProfile? profile)
    {
        float u = Random01(HashLeaf(leaf, 11 + seedOffset));
        float v = Random01(HashLeaf(leaf, 31 + seedOffset));
        var dir = CubeSphereMath.FaceUVToDirection(leaf.Face, u, v);
        float surfR = _terrain!.SampleSurfaceRadius(dir);
        var center = GetWorldCenter();
        var pos = center + dir * surfR;

        var go = new GameObject(isGrass ? $"BiomeGrass_{leaf.Face}" : $"BiomeTree_{leaf.Face}");
        go.Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);
        float yaw = Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;
        go.Transform.Rotation = new Vector3(0, yaw, 0);

        var tree = go.AddBehavior<Tree>();
        tree.IsVegetation = true;
        tree.Shape = isGrass ? CanopyShape.Cone : CanopyShape.Sphere;
        tree.TrunkHeight = isGrass ? GrassBaseHeight * 0.35f : TreeBaseHeight * (0.8f + Random01(HashLeaf(leaf, 73 + seedOffset)) * 0.5f);
        tree.TrunkRadiusBottom = isGrass ? 0.025f : 0.16f;
        tree.TrunkRadiusTop = isGrass ? 0.012f : 0.06f;
        tree.CanopyHeight = isGrass ? GrassBaseHeight : tree.TrunkHeight * 0.9f;
        tree.CanopyRadius = isGrass ? 0.08f : tree.TrunkHeight * 0.35f;
        tree.CanopySegments = isGrass ? 5 : 9;
        tree.WindSway = isGrass ? 0.95f : 0.55f;
        tree.WindSpeed = isGrass ? 1.35f : 0.95f;

        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));
        if (item != null && !string.IsNullOrWhiteSpace(item.ModelPath))
        {
            tree.ModelPath = item.ModelPath;
        }

        tree.RebuildTree();

        float minScale = isGrass ? biome.GrassMinScale : biome.TreeMinScale;
        float maxScale = isGrass ? biome.GrassMaxScale : biome.TreeMaxScale;
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

        _terrain.gameObject!.AddChild(go);
        return go;
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
