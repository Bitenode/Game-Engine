using System;
using Game_Engine.Core;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

public readonly struct PlanetChunkBuildResult
{
    public PlanetChunkBuildResult(TransvoxelMeshData mesh, VoxelChunk? chunk, TransvoxelMeshData? water = null)
    {
        Mesh = mesh;
        Chunk = chunk;
        Water = water;
    }

    public TransvoxelMeshData Mesh { get; }
    public VoxelChunk? Chunk { get; }
    public TransvoxelMeshData? Water { get; }
}

/// <summary>
/// Builds a leaf mesh. Coarse leaves use a spherical heightfield shell.
/// Fine leaves stack transvoxel shells from near the core to the surface
/// so caves exist throughout the planet, not only in a thin crust.
/// </summary>
public sealed class PlanetMeshGenerator
{
    readonly PlanetConfig _config;
    readonly BiomeMap _biomeMap;
    readonly PlanetVoxelEditStore? _editStore;
    readonly PlanetNoiseCache _noise;
    readonly DensityGenerator _densityGen;
    readonly PlanetDensitySampler _sampler;
    PlanetClimateAtlas? _climateAtlas;

    public PlanetDensitySampler Sampler => _sampler;
    public PlanetNoiseCache Noise => _noise;

    public PlanetMeshGenerator(PlanetConfig config, BiomeMap biomeMap, PlanetVoxelEditStore? editStore = null)
    {
        _config = config;
        _biomeMap = biomeMap;
        _editStore = editStore;
        _noise = PlanetNoiseCache.Create(config);
        _densityGen = new DensityGenerator(config, biomeMap, _noise);
        _sampler = new PlanetDensitySampler(config, biomeMap, _noise, editStore);
        // Ensure biome map has climate coupling even when constructed outside PlanetTerrain rebuild.
        _biomeMap.BindClimateCoupling(config, _noise.RiverPrimary, _noise.RiverMeander, _noise.RidgeNoise);
    }

    public void SetClimateAtlas(PlanetClimateAtlas? atlas)
    {
        _climateAtlas = atlas;
        _sampler.SetClimateAtlas(atlas);
        _densityGen.SetClimateAtlas(atlas);
    }

    public PlanetChunkBuildResult Generate(
        int face,
        float u0,
        float v0,
        float u1,
        float v1,
        int resolution,
        byte transitionMask = 0,
        int lodLevel = 0,
        int transitionStride = 0)
    {
        var water = GenerateWaterPatch(face, u0, v0, u1, v1, resolution, transitionMask, transitionStride);

        if (!ShouldUseVolumetric(face, u0, v0, u1, v1, resolution, lodLevel))
        {
            var shell = GenerateShell(face, u0, v0, u1, v1, resolution, transitionMask, transitionStride);
            return new PlanetChunkBuildResult(shell, null, water);
        }

        DensityGenerator.ComputeInteriorBounds(_config, _editStore, out float radialMin, out float radialMax);
        int voxelSize = 32;
        int layers = _config.EnableCaves
            ? DensityGenerator.RadialLayerCount(radialMin, radialMax, voxelSize)
            : 1;
        float usable = MathF.Max(8f, radialMax - radialMin);

        TransvoxelMeshData? combined = null;
        VoxelChunk? outerChunk = null;
        for (int layer = 0; layer < layers; layer++)
        {
            float t0 = layer / (float)layers;
            float t1 = (layer + 1) / (float)layers;
            float layerMin = radialMin + t0 * usable;
            float layerSpan = MathF.Max(8f, (radialMin + t1 * usable) - layerMin);
            var chunk = new VoxelChunk(voxelSize);
            _densityGen.Generate(
                chunk,
                face,
                u0,
                v0,
                u1,
                v1,
                lodLevel,
                _editStore,
                _sampler,
                layerMin,
                layerSpan);
            _editStore?.AccumulateIntoChunk(chunk);
            byte mask = layer == layers - 1 ? transitionMask : (byte)0;
            var layerMesh = Remesh(chunk, mask);
            if (combined == null)
                combined = layerMesh;
            else
                combined.Append(layerMesh);
            if (layer == layers - 1)
                outerChunk = chunk;
        }

        return new PlanetChunkBuildResult(combined ?? new TransvoxelMeshData(), outerChunk, water);
    }

    /// <summary>
    /// Water overlay on the same cube-sphere patch as the terrain leaf.
    /// Vertices sit on the water table (same sampler as underwater), so the
    /// shoreline matches the visible LOD instead of a planet-wide 48-subdiv mesh.
    /// </summary>
    public TransvoxelMeshData? GenerateWaterPatch(
        int face, float u0, float v0, float u1, float v1, int resolution,
        byte transitionMask = 0, int transitionStride = 0)
    {
        int size = Math.Max(1, resolution);
        int n = size + 1;
        float vertexSpacing = EstimateCell(face, u0, v0, u1, v1, resolution);
        // Rivers follow the bed, so adjacent verts can differ by many meters.
        // A tight span cull deleted those faces (holes in the river sheet).
        float maxSpan = MathF.Max(48f, vertexSpacing * 12f);
        float oceanFillR = PlanetWaterSampler.GetOceanFillRadius(_config);

        var positions = new SN.Vector3[n * n];
        var terrainPos = new SN.Vector3[n * n];
        var uvs = new SN.Vector2[n * n];
        var wet = new bool[n * n];
        var terrainRAt = new float[n * n];
        var waterRAt = new float[n * n];

        int ResolveBiomeIndex(string name)
        {
            var biomes = _config.Biomes;
            if (biomes == null || string.IsNullOrWhiteSpace(name))
                return 0;
            for (int i = 0; i < biomes.Length; i++)
            {
                if (string.Equals(biomes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        for (int iy = 0; iy < n; iy++)
        {
            float vt = (float)iy / size;
            float v = v0 + vt * (v1 - v0);
            if (iy == 0) v = v0;
            else if (iy == size) v = v1;
            for (int ix = 0; ix < n; ix++)
            {
                float ut = (float)ix / size;
                float u = u0 + ut * (u1 - u0);
                if (ix == 0) u = u0;
                else if (ix == size) u = u1;

                var dir = CubeSphereMath.FaceUVToDirection(face, u, v);
                float terrainR = _sampler.SampleEditedSurfaceRadius(dir, vertexSpacing);
                var sample = PlanetWaterSampler.SampleWaterSurface(
                    dir,
                    _config,
                    _biomeMap,
                    terrainR,
                    _noise.RiverPrimary,
                    _noise.RiverMeander,
                    ResolveBiomeIndex);

                int idx = iy * n + ix;
                terrainRAt[idx] = terrainR;
                terrainPos[idx] = dir * MathF.Max(1f, terrainR);
                if (sample.Mask >= 0.01f && sample.Radius > terrainR + 0.05f)
                {
                    // Oceans share one sea-level sphere. Lakes/ponds keep the
                    // sampler radius so they stay in the hole instead of flooding
                    // the continent at sea level. Rivers follow the bed.
                    float radius = sample.Kind == PlanetWaterKind.Ocean
                        ? oceanFillR
                        : sample.Kind == PlanetWaterKind.Lava
                            ? sample.Radius
                            : MathF.Max(sample.Radius, terrainR + 0.2f);
                    positions[idx] = dir * radius;
                    waterRAt[idx] = radius;
                    int packedId = sample.Kind == PlanetWaterKind.Lava
                        ? 6
                        : sample.Kind == PlanetWaterKind.River
                            ? 7
                            : Math.Clamp(sample.BodyIndex, 0, 5);
                    uvs[idx] = new SN.Vector2(
                        Math.Clamp(sample.ShoreBiomeIndex, 0, 7) + packedId * 8f,
                        Math.Clamp(sample.Mask, 0.35f, 1f));
                    wet[idx] = true;
                }
                else
                {
                    positions[idx] = terrainPos[idx];
                    waterRAt[idx] = 0f;
                    uvs[idx] = SN.Vector2.Zero;
                }
            }
        }

        // Match the terrain shell's T-junction ramps so water corners sit on
        // the same stretched LOD edge the player sees.
        SnapWaterTerrainLod(terrainPos, terrainRAt, n, size, transitionMask, transitionStride);
        for (int i = 0; i < terrainPos.Length; i++)
        {
            if (!wet[i] || (int)(uvs[i].X / 8f) >= 7)
                continue;
            var dir = terrainPos[i].LengthSquared() > 1e-8f
                ? SN.Vector3.Normalize(terrainPos[i])
                : SN.Vector3.UnitY;
            positions[i] = dir * waterRAt[i];
        }

        // Biome classification stops at Ocean/Beach cells. The visible crest is
        // further inland on the interpolated slope (biome height stretch). Walk
        // ocean water along that ramp until terrain actually leaves the sea.
        int sealPasses = vertexSpacing > 8f ? 4 : 7;
        for (int pass = 0; pass < sealPasses; pass++)
        {
            int grown = 0;
            var grow = new bool[n * n];
            for (int iy = 0; iy < n; iy++)
            {
                for (int ix = 0; ix < n; ix++)
                {
                    int idx = iy * n + ix;
                    if (wet[idx]) continue;
                    if (terrainRAt[idx] >= oceanFillR - 0.02f) continue;

                    bool nearOcean = false;
                    SN.Vector2 neighborUv = default;
                    for (int dy = -1; dy <= 1 && !nearOcean; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = ix + dx, ny = iy + dy;
                            if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                            int nidx = ny * n + nx;
                            if (!wet[nidx] || waterRAt[nidx] <= 1e-4f) continue;
                            if ((int)(uvs[nidx].X / 8f) >= 6) continue;
                            nearOcean = true;
                            neighborUv = uvs[nidx];
                            break;
                        }
                    }
                    if (!nearOcean) continue;

                    var dir = terrainPos[idx].LengthSquared() > 1e-8f
                        ? SN.Vector3.Normalize(terrainPos[idx])
                        : SN.Vector3.UnitY;
                    if (PlanetSurfaceUtility.SampleMagmaBowl(_config, dir) > 0.18f)
                        continue;
                    positions[idx] = dir * oceanFillR;
                    waterRAt[idx] = oceanFillR;
                    uvs[idx] = new SN.Vector2(neighborUv.X, MathF.Max(0.35f, neighborUv.Y));
                    grow[idx] = true;
                    grown++;
                }
            }
            for (int i = 0; i < grow.Length; i++)
            {
                if (grow[i])
                    wet[i] = true;
            }
            if (grown == 0)
                break;
        }

        var data = new TransvoxelMeshData();

        int AddVert(SN.Vector3 pos, SN.Vector2 uv)
        {
            int i = data.Positions.Count;
            data.Positions.Add(pos);
            var nrm = pos.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(pos) : SN.Vector3.UnitY;
            data.Normals.Add(nrm);
            data.UVs.Add(uv);
            return i;
        }

        var remap = new int[n * n];
        Array.Fill(remap, -1);
        for (int i = 0; i < positions.Length; i++)
        {
            if (!wet[i]) continue;
            remap[i] = AddVert(positions[i], uvs[i]);
        }

        if (data.Positions.Count < 3)
            return null;

        // Place the shore on the visible terrain-edge / sea-sphere intersection.
        // Direction-lerp onto the sphere misses the stretched biome ramp, so
        // water corners sat off the crest the player sees.
        int ShoreEdgeVert(int wetIdx, int dryIdx)
        {
            float wr = waterRAt[wetIdx] > 1e-4f ? waterRAt[wetIdx] : oceanFillR;
            var pW = terrainPos[wetIdx];
            var pD = terrainPos[dryIdx];
            if (pW.LengthSquared() < 1e-8f) pW = positions[wetIdx];
            if (pD.LengthSquared() < 1e-8f) pD = positions[dryIdx];
            float trW = terrainRAt[wetIdx];
            float trD = terrainRAt[dryIdx];
            bool riverEdge = (int)(uvs[wetIdx].X / 8f) >= 7;

            float t;
            bool lavaEdge = (int)(uvs[wetIdx].X / 8f) == 6;
            if (!TryTerrainEdgeSeaT(pW, pD, wr, trW, trD, out t))
            {
                if (trD < wr - 0.02f && !riverEdge && !lavaEdge)
                {
                    var dirD = pD.LengthSquared() > 1e-8f
                        ? SN.Vector3.Normalize(pD)
                        : SN.Vector3.Normalize(pW);
                    var uvFill = uvs[wetIdx];
                    uvFill.Y = MathF.Max(0.35f, uvFill.Y * 0.8f);
                    return AddVert(dirD * wr, uvFill);
                }
                t = lavaEdge ? 0.18f : (riverEdge ? 0.62f : 0.7f);
            }
            if (riverEdge)
                t = Math.Clamp(MathF.Max(t, 0.55f), 0.08f, 0.92f);
            if (lavaEdge)
                t = Math.Clamp(t, 0.04f, 0.35f);

            var shorePos = ProjectToRadius(SN.Vector3.Lerp(pW, pD, t), wr);
            var uv = uvs[wetIdx];
            uv.Y = MathF.Max(0.35f, uv.Y * 0.75f);
            return AddVert(shorePos, uv);
        }

        // Wind every tri so its face points outward (seam was flipping "up" on mixed cells).
        void AddTriIds(int ia, int ib, int ic)
        {
            if (ia < 0 || ib < 0 || ic < 0) return;
            var pa = data.Positions[ia];
            var pb = data.Positions[ib];
            var pc = data.Positions[ic];
            float ra = pa.Length(), rb = pb.Length(), rc = pc.Length();
            float span = MathF.Max(ra, MathF.Max(rb, rc)) - MathF.Min(ra, MathF.Min(rb, rc));
            if (span > maxSpan) return;
            var nrm = SN.Vector3.Cross(pb - pa, pc - pa);
            if (SN.Vector3.Dot(nrm, pa) < 0f)
                (ib, ic) = (ic, ib);
            data.Indices.Add(ia);
            data.Indices.Add(ib);
            data.Indices.Add(ic);
        }

        for (int iy = 0; iy < size; iy++)
        {
            for (int ix = 0; ix < size; ix++)
            {
                int a = iy * n + ix;
                int b = a + 1;
                int c = a + n;
                int d = c + 1;
                int mask = (wet[a] ? 1 : 0) | (wet[b] ? 2 : 0) | (wet[c] ? 4 : 0) | (wet[d] ? 8 : 0);
                if (mask == 0) continue;

                if (mask == 15)
                {
                    AddTriIds(remap[a], remap[b], remap[c]);
                    AddTriIds(remap[b], remap[d], remap[c]);
                    continue;
                }

                var poly = new int[8];
                int pc = 0;
                if (wet[a]) poly[pc++] = remap[a];
                if (wet[a] != wet[b])
                    poly[pc++] = ShoreEdgeVert(wet[a] ? a : b, wet[a] ? b : a);
                if (wet[b]) poly[pc++] = remap[b];
                if (wet[b] != wet[d])
                    poly[pc++] = ShoreEdgeVert(wet[b] ? b : d, wet[b] ? d : b);
                if (wet[d]) poly[pc++] = remap[d];
                if (wet[d] != wet[c])
                    poly[pc++] = ShoreEdgeVert(wet[d] ? d : c, wet[d] ? c : d);
                if (wet[c]) poly[pc++] = remap[c];
                if (wet[c] != wet[a])
                    poly[pc++] = ShoreEdgeVert(wet[c] ? c : a, wet[c] ? a : c);

                if (pc < 3) continue;
                for (int t = 1; t < pc - 1; t++)
                    AddTriIds(poly[0], poly[t], poly[t + 1]);
            }
        }

        return data.Indices.Count >= 3 ? data : null;
    }

    public TransvoxelMeshData Remesh(VoxelChunk chunk, byte transitionMask)
    {
        TransvoxelMesher.EnableTransitionCells = _config.EnableTransvoxelTransitions;
        var mesh = TransvoxelMesher.GenerateMesh(chunk, transitionMask);
        ApplyBiomeBlends(mesh);
        mesh.RecalculateNormals();
        return mesh;
    }

    void ApplyBiomeBlends(TransvoxelMeshData data)
    {
        for (int i = 0; i < data.Positions.Count; i++)
        {
            var p = data.Positions[i];
            float len = p.Length();
            var dir = len > 1e-8f ? p / len : SN.Vector3.UnitY;
            float alt = _biomeMap.NormalizeAltitude(len - _config.Radius);
            var blends = _biomeMap.GetBiomes(dir, alt);
            var blendIdx = SN.Vector4.Zero;
            var blendWt = SN.Vector4.Zero;
            for (int b = 0; b < blends.Length && b < 4; b++)
            {
                float idx = blends[b].Biome.BiomeIndex;
                float w = blends[b].Weight;
                switch (b)
                {
                    case 0: blendIdx.X = idx; blendWt.X = w; break;
                    case 1: blendIdx.Y = idx; blendWt.Y = w; break;
                    case 2: blendIdx.Z = idx; blendWt.Z = w; break;
                    case 3: blendIdx.W = idx; blendWt.W = w; break;
                }
            }
            if (blendWt.X + blendWt.Y + blendWt.Z + blendWt.W < 1e-4f)
            {
                blendIdx.X = 0;
                blendWt.X = 1f;
            }
            ApplyShoreSand(dir, len, ref blendIdx, ref blendWt);
            if (i < data.BlendIndices.Count) data.BlendIndices[i] = blendIdx;
            else data.BlendIndices.Add(blendIdx);
            if (i < data.BlendWeights.Count) data.BlendWeights[i] = blendWt;
            else data.BlendWeights.Add(blendWt);
        }
    }

    bool ShouldUseVolumetric(int face, float u0, float v0, float u1, float v1, int resolution, int lodLevel)
    {
        _ = lodLevel;
        float cell = EstimateCell(face, u0, v0, u1, v1, resolution);
        float maxCell = MathF.Max(8f, _config.VolumetricMaxCellSize);

        if (_editStore != null && (_editStore.SphereEditCount > 0 || _editStore.BakedCellCount > 0))
        {
            var dir = CubeSphereMath.FaceUVToDirection(face, (u0 + u1) * 0.5f, (v0 + v1) * 0.5f);
            var a = CubeSphereMath.FaceUVToDirection(face, u0, v0) * _config.Radius;
            var b = CubeSphereMath.FaceUVToDirection(face, u1, v0) * _config.Radius;
            var c = CubeSphereMath.FaceUVToDirection(face, u0, v1) * _config.Radius;
            float leafR = MathF.Max(MathF.Max(SN.Vector3.Distance(a, b), SN.Vector3.Distance(a, c)) * 0.75f, _config.Radius * 0.01f);
            if (_editStore.OverlapsSphere(dir * _config.Radius, leafR + _editStore.MaxRadius + 8f))
            {
                // Foot-scale play digs on coarse leaves: shell-only remesh is much faster.
                if (SceneService.PlayMode && _editStore.MaxRadius <= 2.5f && cell > maxCell)
                    return false;
                return true;
            }
        }

        if (!_config.EnableCaves)
            return false;

        return cell <= maxCell;
    }

    static void SnapWaterTerrainLod(
        SN.Vector3[] terrainPos, float[] terrainRAt,
        int n, int size, byte mask, int stridePacked)
    {
        if (mask == 0 || size < 2) return;

        void SnapCol(int ix, int stride)
        {
            if (stride < 2) stride = 2;
            for (int iy = 1; iy < size; iy++)
            {
                if (iy % stride == 0) continue;
                int y0 = (iy / stride) * stride;
                int y1 = Math.Min(size, y0 + stride);
                float t = (iy - y0) / (float)(y1 - y0);
                int i = iy * n + ix;
                var p = SN.Vector3.Lerp(terrainPos[y0 * n + ix], terrainPos[y1 * n + ix], t);
                terrainPos[i] = p;
                terrainRAt[i] = p.Length();
            }
        }

        void SnapRow(int iy, int stride)
        {
            if (stride < 2) stride = 2;
            for (int ix = 1; ix < size; ix++)
            {
                if (ix % stride == 0) continue;
                int x0 = (ix / stride) * stride;
                int x1 = Math.Min(size, x0 + stride);
                float t = (ix - x0) / (float)(x1 - x0);
                int i = iy * n + ix;
                var p = SN.Vector3.Lerp(terrainPos[iy * n + x0], terrainPos[iy * n + x1], t);
                terrainPos[i] = p;
                terrainRAt[i] = p.Length();
            }
        }

        if ((mask & 1) != 0) SnapCol(0, (stridePacked >> 0) & 0xFF);
        if ((mask & 2) != 0) SnapCol(size, (stridePacked >> 8) & 0xFF);
        if ((mask & 4) != 0) SnapRow(0, (stridePacked >> 16) & 0xFF);
        if ((mask & 8) != 0) SnapRow(size, (stridePacked >> 24) & 0xFF);
    }

    static bool TryTerrainEdgeSeaT(
        SN.Vector3 pW, SN.Vector3 pD, float wr, float trW, float trD, out float t)
    {
        var d = pD - pW;
        float a = SN.Vector3.Dot(d, d);
        if (a > 1e-10f)
        {
            float b = 2f * SN.Vector3.Dot(pW, d);
            float c = SN.Vector3.Dot(pW, pW) - wr * wr;
            float disc = b * b - 4f * a * c;
            if (disc >= 0f)
            {
                float s = MathF.Sqrt(disc);
                float inv = 0.5f / a;
                float t0 = (-b - s) * inv;
                float t1 = (-b + s) * inv;
                bool t0ok = t0 >= 0f && t0 <= 1f;
                bool t1ok = t1 >= 0f && t1 <= 1f;
                if (t0ok || t1ok)
                {
                    if (t0ok && t1ok)
                        t = trW <= trD ? MathF.Max(t0, t1) : MathF.Min(t0, t1);
                    else
                        t = t0ok ? t0 : t1;
                    t = Math.Clamp(t, 0.04f, 0.96f);
                    return true;
                }
            }
        }

        float denom = trD - trW;
        if (MathF.Abs(denom) < 1e-5f)
        {
            t = 0.7f;
            return trW < wr && trD > wr;
        }
        t = (wr - trW) / denom;
        if (t < 0f || t > 1f)
            return false;
        t = Math.Clamp(t, 0.04f, 0.96f);
        return true;
    }

    static SN.Vector3 ProjectToRadius(SN.Vector3 p, float radius)
    {
        float len = p.Length();
        if (len < 1e-8f)
            return SN.Vector3.UnitY * radius;
        return p * (radius / len);
    }

    float EstimateCell(int face, float u0, float v0, float u1, float v1, int resolution)
    {
        int size = Math.Max(1, resolution);
        var a = CubeSphereMath.FaceUVToDirection(face, u0, v0) * _config.Radius;
        var b = CubeSphereMath.FaceUVToDirection(face, u1, v0) * _config.Radius;
        var c = CubeSphereMath.FaceUVToDirection(face, u0, v1) * _config.Radius;
        float sizeU = SN.Vector3.Distance(a, b);
        float sizeV = SN.Vector3.Distance(a, c);
        return Math.Max(sizeU, sizeV) / size;
    }

    TransvoxelMeshData GenerateShell(int face, float u0, float v0, float u1, float v1, int resolution, byte transitionMask, int transitionStride)
    {
        var data = new TransvoxelMeshData();
        int n = resolution + 1;
        int size = Math.Max(1, resolution);
        float vertexSpacing = EstimateCell(face, u0, v0, u1, v1, resolution);

        for (int iy = 0; iy < n; iy++)
        {
            float vt = (float)iy / size;
            float v = v0 + vt * (v1 - v0);
            if (iy == 0) v = v0;
            else if (iy == size) v = v1;
            if (v0 <= 0f && iy == 0) v = 0f;
            if (v1 >= 1f && iy == size) v = 1f;
            for (int ix = 0; ix < n; ix++)
            {
                float ut = (float)ix / size;
                float u = u0 + ut * (u1 - u0);
                if (ix == 0) u = u0;
                else if (ix == size) u = u1;
                if (u0 <= 0f && ix == 0) u = 0f;
                if (u1 >= 1f && ix == size) u = 1f;

                var sphereDir = CubeSphereMath.FaceUVToDirection(face, u, v);
                // Always sample the authored surface (no LOD-scaled brush). Adjacent
                // chunks then agree on shared cube-sphere edges.
                float surfaceR = _sampler.SampleEditedSurfaceRadius(sphereDir, vertexSpacing);
                float alt = _biomeMap.NormalizeAltitude(surfaceR - _config.Radius);
                var blends = _biomeMap.GetBiomes(sphereDir, alt);
                var pos = sphereDir * surfaceR;
                var normal = EstimateShellNormal(sphereDir, vertexSpacing);

                var blendIdx = new SN.Vector4(0, 0, 0, 0);
                var blendWt = new SN.Vector4(0, 0, 0, 0);
                for (int b = 0; b < blends.Length && b < 4; b++)
                {
                    float idx = blends[b].Biome.BiomeIndex;
                    float w = blends[b].Weight;
                    switch (b)
                    {
                        case 0: blendIdx.X = idx; blendWt.X = w; break;
                        case 1: blendIdx.Y = idx; blendWt.Y = w; break;
                        case 2: blendIdx.Z = idx; blendWt.Z = w; break;
                        case 3: blendIdx.W = idx; blendWt.W = w; break;
                    }
                }
                if (blendWt.X + blendWt.Y + blendWt.Z + blendWt.W < 1e-4f)
                {
                    blendIdx.X = 0;
                    blendWt.X = 1f;
                }
                ApplyShoreSand(sphereDir, surfaceR, ref blendIdx, ref blendWt);

                data.Positions.Add(pos);
                data.Normals.Add(normal);
                data.UVs.Add(new SN.Vector2(ut, vt));
                data.BlendIndices.Add(blendIdx);
                data.BlendWeights.Add(blendWt);
            }
        }

        bool flip = false;
        if (n >= 2 && data.Positions.Count >= n + 1)
        {
            var pa = data.Positions[0];
            var pb = data.Positions[1];
            var pc = data.Positions[n];
            var nrm = SN.Vector3.Cross(pb - pa, pc - pa);
            var radial = pa.LengthSquared() > 1e-8f ? pa : SN.Vector3.UnitY;
            flip = SN.Vector3.Dot(nrm, radial) < 0f;
        }

        for (int iy = 0; iy < size; iy++)
        {
            for (int ix = 0; ix < size; ix++)
            {
                int a = iy * n + ix;
                int b = a + 1;
                int c = a + n;
                int d = c + 1;
                if (flip)
                {
                    data.Indices.Add(a);
                    data.Indices.Add(c);
                    data.Indices.Add(b);
                    data.Indices.Add(b);
                    data.Indices.Add(c);
                    data.Indices.Add(d);
                }
                else
                {
                    data.Indices.Add(a);
                    data.Indices.Add(b);
                    data.Indices.Add(c);
                    data.Indices.Add(b);
                    data.Indices.Add(d);
                    data.Indices.Add(c);
                }
            }
        }

        SnapLodTJunctions(data, n, size, transitionMask, transitionStride);
        // Inward skirts pull edge verts toward the core along world radius. After voxel
        // edits that is often visible as upright white fins above trenches — skip them.
        if (_editStore == null || (_editStore.SphereEditCount == 0 && _editStore.BakedCellCount == 0))
            AddInwardSkirts(data, n, size, flip, EstimateCell(face, u0, v0, u1, v1, resolution));
        return data;
    }

    /// <summary>
    /// Coarse neighbors only have even edge verts. Move the extra (odd) verts onto
    /// that edge so T-junctions do not open a crack.
    /// </summary>
    static void SnapLodTJunctions(TransvoxelMeshData data, int n, int size, byte mask, int stridePacked)
    {
        if (mask == 0 || size < 2) return;

        void SnapCol(int ix, int stride)
        {
            if (stride < 2) stride = 2;
            for (int iy = 1; iy < size; iy++)
            {
                if (iy % stride == 0) continue;
                int y0 = (iy / stride) * stride;
                int y1 = Math.Min(size, y0 + stride);
                float t = (iy - y0) / (float)(y1 - y0);
                int i = iy * n + ix;
                int a = y0 * n + ix;
                int b = y1 * n + ix;
                data.Positions[i] = SN.Vector3.Lerp(data.Positions[a], data.Positions[b], t);
                var nn = SN.Vector3.Lerp(data.Normals[a], data.Normals[b], t);
                float len = nn.Length();
                data.Normals[i] = len > 1e-8f ? nn / len : data.Normals[i];
            }
        }

        void SnapRow(int iy, int stride)
        {
            if (stride < 2) stride = 2;
            for (int ix = 1; ix < size; ix++)
            {
                if (ix % stride == 0) continue;
                int x0 = (ix / stride) * stride;
                int x1 = Math.Min(size, x0 + stride);
                float t = (ix - x0) / (float)(x1 - x0);
                int i = iy * n + ix;
                int a = iy * n + x0;
                int b = iy * n + x1;
                data.Positions[i] = SN.Vector3.Lerp(data.Positions[a], data.Positions[b], t);
                var nn = SN.Vector3.Lerp(data.Normals[a], data.Normals[b], t);
                float len = nn.Length();
                data.Normals[i] = len > 1e-8f ? nn / len : data.Normals[i];
            }
        }

        if ((mask & 1) != 0) SnapCol(0, (stridePacked >> 0) & 0xFF);
        if ((mask & 2) != 0) SnapCol(size, (stridePacked >> 8) & 0xFF);
        if ((mask & 4) != 0) SnapRow(0, (stridePacked >> 16) & 0xFF);
        if ((mask & 8) != 0) SnapRow(size, (stridePacked >> 24) & 0xFF);
    }

    /// <summary>
    /// Degenerate walls tucked toward the planet center along each patch edge.
    /// They hide leftover cracks without standing up as world-space fins.
    /// </summary>
    void AddInwardSkirts(TransvoxelMeshData data, int n, int size, bool flip, float cell)
    {
        float skirtLen = Math.Clamp(cell * 1.35f, 3f, MathF.Max(8f, _config.Radius * 0.012f));

        int AddSkirtVert(int src)
        {
            var p = data.Positions[src];
            float r = p.Length();
            var dir = r > 1e-8f ? p / r : SN.Vector3.UnitY;
            data.Positions.Add(dir * MathF.Max(r * 0.5f, r - skirtLen));
            data.Normals.Add(data.Normals[src]);
            data.UVs.Add(data.UVs[src]);
            data.BlendIndices.Add(data.BlendIndices[src]);
            data.BlendWeights.Add(data.BlendWeights[src]);
            return data.Positions.Count - 1;
        }

        void AddStrip(int[] edge, int[] skirt)
        {
            for (int i = 0; i < size; i++)
            {
                int e0 = edge[i], e1 = edge[i + 1], s0 = skirt[i], s1 = skirt[i + 1];
                if (flip)
                {
                    data.Indices.Add(e0); data.Indices.Add(s0); data.Indices.Add(e1);
                    data.Indices.Add(e1); data.Indices.Add(s0); data.Indices.Add(s1);
                }
                else
                {
                    data.Indices.Add(e0); data.Indices.Add(e1); data.Indices.Add(s0);
                    data.Indices.Add(e1); data.Indices.Add(s1); data.Indices.Add(s0);
                }
            }
        }

        var left = new int[n];
        var right = new int[n];
        var bottom = new int[n];
        var top = new int[n];
        var leftS = new int[n];
        var rightS = new int[n];
        var bottomS = new int[n];
        var topS = new int[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = i * n;
            right[i] = i * n + size;
            bottom[i] = i;
            top[i] = size * n + i;
            leftS[i] = AddSkirtVert(left[i]);
            rightS[i] = AddSkirtVert(right[i]);
            bottomS[i] = AddSkirtVert(bottom[i]);
            topS[i] = AddSkirtVert(top[i]);
        }

        AddStrip(left, leftS);
        AddStrip(right, rightS);
        AddStrip(bottom, bottomS);
        AddStrip(top, topS);
    }

    SN.Vector3 EstimateShellNormal(SN.Vector3 sphereDir, float vertexSpacing = 0f)
    {
        const float eps = 0.0025f;
        var t = SN.Vector3.Normalize(SN.Vector3.Cross(sphereDir, MathF.Abs(sphereDir.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY));
        var b = SN.Vector3.Normalize(SN.Vector3.Cross(sphereDir, t));
        var d0 = SN.Vector3.Normalize(sphereDir);
        var dT = SN.Vector3.Normalize(sphereDir + t * eps);
        var dB = SN.Vector3.Normalize(sphereDir + b * eps);
        float r0 = _sampler.SampleEditedSurfaceRadius(d0, vertexSpacing);
        float rT = _sampler.SampleEditedSurfaceRadius(dT, vertexSpacing);
        float rB = _sampler.SampleEditedSurfaceRadius(dB, vertexSpacing);
        var p0 = d0 * r0;
        var pT = dT * rT;
        var pB = dB * rB;
        var n = SN.Vector3.Cross(pT - p0, pB - p0);
        float len = n.Length();
        if (len < 1e-8f)
            return sphereDir;
        n /= len;
        if (SN.Vector3.Dot(n, sphereDir) < 0f)
            n = -n;
        return n;
    }

    void ApplyShoreSand(SN.Vector3 sphereDir, float terrainRadius, ref SN.Vector4 blendIdx, ref SN.Vector4 blendWt)
    {
        // Never stamp beach onto crust that sits below the local water table.
        var water = PlanetWaterSampler.SampleWaterSurface(
            sphereDir,
            _config,
            _biomeMap,
            terrainRadius,
            _noise.RiverPrimary,
            _noise.RiverMeander,
            FindBiomeIndex);
        if (water.Mask > 0.01f && terrainRadius < water.Radius - 0.25f)
            return;

        var sand = PlanetWaterSampler.SampleSandWeight(
            sphereDir,
            _config,
            _biomeMap,
            terrainRadius,
            _noise.RiverPrimary,
            _noise.RiverMeander,
            FindBiomeIndex);
        if (sand is not { weight: > 0.01f } s)
            return;
        // SampleSandWeight already applies ShoreClimateBias via BiomeMap moisture.
        PlanetWaterSampler.ApplySandBlend(ref blendIdx, ref blendWt, s.biomeIndex, s.weight);
    }

    int FindBiomeIndex(string name)
    {
        if (_config.Biomes == null || string.IsNullOrWhiteSpace(name))
            return -1;
        for (int i = 0; i < _config.Biomes.Length; i++)
        {
            if (string.Equals(_config.Biomes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
