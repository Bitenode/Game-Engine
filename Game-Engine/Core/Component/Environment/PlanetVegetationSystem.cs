#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
    [Persist] public bool FullBiomePopulate { get; set; } = true;

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
    bool _manualSpawnPass;

    sealed class Entry
    {
        public GameObject GameObject = null!;
        public BiomeDefinition Biome = null!;
        public bool IsGrass;
        public float BaseScale = 1f;
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
            LastDespawnedThisUpdate = 0;
        }

        _manualSpawnPass = true;
        try
        {
            RefreshVegetation();
        }
        finally
        {
            _manualSpawnPass = false;
        }
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

        bool fullPopulate = _manualSpawnPass && FullBiomePopulate;
        int maxLeaves = fullPopulate ? leaves.Count : Math.Max(8, MaxTrackedLeaves);
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count && activeKeys.Count < maxLeaves; i++)
        {
            var leaf = leaves[i];
            var leafCenter = leaf.WorldCentre(worldRadius);
            if (!fullPopulate && SN.Vector3.DistanceSquared(localCam, leafCenter) > maxDistSq)
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

        while (treeCount < targetTrees && spawnBudget > 0 && currentTotal < hardCap)
        {
            var go = SpawnVegetationObject(leaf, biome, isGrass: false, treeCount + 17, profile);
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                IsGrass = false,
                BaseScale = (float)go.Transform.Scale.X
            });
            treeCount++;
            currentTotal++;
            spawnBudget--;
            LastSpawnedThisUpdate++;
        }

        while (grassCount < targetGrass && spawnBudget > 0 && currentTotal < hardCap)
        {
            var go = SpawnVegetationObject(leaf, biome, isGrass: true, grassCount + 97, profile);
            entries.Add(new Entry
            {
                GameObject = go,
                Biome = biome,
                IsGrass = true,
                BaseScale = (float)go.Transform.Scale.X
            });
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

            if (e.GameObject?.Behaviors?.OfType<VegetationPainter>().FirstOrDefault() is VegetationPainter vp)
            {
                vp.WindStrength = Math.Clamp(0.35f + _windMultiplier * 0.5f, 0f, 3f);
                vp.WindSpeed = Math.Clamp(0.8f + _windMultiplier * 0.8f, 0f, 4f);
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
        _terrain.gameObject!.AddChild(go);
        SceneGraphUtil.SetPositionWorld(go, pos);
        float yawDeg = Random01(HashLeaf(leaf, 53 + seedOffset)) * 360f;
        go.Transform.Rotation = SurfaceAlignedRotation(dir, yawDeg);

        var item = ChooseItem(profile, isGrass, HashLeaf(leaf, 197 + seedOffset));
        if (isGrass)
        {
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
            painter.FadeEndDistance = Math.Max(80f, _terrain.Config!.EffectiveWorldRadius * 0.35f);
            painter.FadeStartDistance = painter.FadeEndDistance * 0.6f;
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
            int bladeCount = 20 + (int)(Random01(HashLeaf(leaf, 211 + seedOffset)) * 16f);
            painter.BuildOnPlanetPatch(_terrain, dir, patchRadius, bladeCount);
            go.Transform.Scale = new Vector3(1f, 1f, 1f);
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

        return go;
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

    static Vector3 SurfaceAlignedRotation(SN.Vector3 up, float yawDeg)
    {
        up = SafeNormalize(up, SN.Vector3.UnitY);
        var alignAxis = SN.Vector3.Cross(SN.Vector3.UnitY, up);
        float axisLen = alignAxis.Length();
        SN.Quaternion qAlign;
        if (axisLen < 1e-5f)
        {
            qAlign = SN.Vector3.Dot(SN.Vector3.UnitY, up) >= 0f
                ? SN.Quaternion.Identity
                : SN.Quaternion.CreateFromAxisAngle(SN.Vector3.UnitX, MathF.PI);
        }
        else
        {
            alignAxis /= axisLen;
            float dot = Math.Clamp(SN.Vector3.Dot(SN.Vector3.UnitY, up), -1f, 1f);
            float angle = MathF.Acos(dot);
            qAlign = SN.Quaternion.CreateFromAxisAngle(alignAxis, angle);
        }
        var qYaw = SN.Quaternion.CreateFromAxisAngle(up, yawDeg * (MathF.PI / 180f));
        var q = SN.Quaternion.Normalize(qAlign * qYaw);
        var e = QuaternionToEuler(q);
        return new Vector3(e.X, e.Y, e.Z);
    }

    static SN.Vector3 QuaternionToEuler(SN.Quaternion q)
    {
        float sinr_cosp = 2f * (q.W * q.X + q.Y * q.Z);
        float cosr_cosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float pitch = MathF.Atan2(sinr_cosp, cosr_cosp) * (180f / MathF.PI);

        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
        float yaw = MathF.Abs(sinp) >= 1f ? MathF.CopySign(90f, sinp) : MathF.Asin(sinp) * (180f / MathF.PI);

        float siny_cosp = 2f * (q.W * q.Z + q.X * q.Y);
        float cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float roll = MathF.Atan2(siny_cosp, cosy_cosp) * (180f / MathF.PI);

        return new SN.Vector3(pitch, yaw, roll);
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

    static bool IsSupportedModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".3ds";
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
