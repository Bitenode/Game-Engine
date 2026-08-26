using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

public readonly struct PlanetChunkBuildResult
{
    public PlanetChunkBuildResult(TransvoxelMeshData mesh, VoxelChunk? chunk)
    {
        Mesh = mesh;
        Chunk = chunk;
    }

    public TransvoxelMeshData Mesh { get; }
    public VoxelChunk? Chunk { get; }
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
        if (!ShouldUseVolumetric(face, u0, v0, u1, v1, resolution, lodLevel))
        {
            var shell = GenerateShell(face, u0, v0, u1, v1, resolution, transitionMask, transitionStride);
            return new PlanetChunkBuildResult(shell, null);
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

        return new PlanetChunkBuildResult(combined ?? new TransvoxelMeshData(), outerChunk);
    }

    public TransvoxelMeshData Remesh(VoxelChunk chunk, byte transitionMask)
    {
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
            var blends = _biomeMap.GetBiomes(dir);
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
            if (i < data.BlendIndices.Count) data.BlendIndices[i] = blendIdx;
            else data.BlendIndices.Add(blendIdx);
            if (i < data.BlendWeights.Count) data.BlendWeights[i] = blendWt;
            else data.BlendWeights.Add(blendWt);
        }
    }

    bool ShouldUseVolumetric(int face, float u0, float v0, float u1, float v1, int resolution, int lodLevel)
    {
        _ = lodLevel;
        // Orbit / coarse leaves stay a smooth heightfield. Refined leaves
        // use stacked transvoxel shells from the core to the surface.
        if (!_config.EnableCaves)
            return false;
        float cell = EstimateCell(face, u0, v0, u1, v1, resolution);
        float maxCell = MathF.Max(8f, _config.VolumetricMaxCellSize);
        return cell <= maxCell;
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
                var blends = _biomeMap.GetBiomes(sphereDir);
                // Always sample the authored surface (no LOD-scaled brush). Adjacent
                // chunks then agree on shared cube-sphere edges.
                float surfaceR = _sampler.SampleEditedSurfaceRadius(sphereDir, 0f);
                var pos = sphereDir * surfaceR;
                var normal = EstimateShellNormal(sphereDir);

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

    SN.Vector3 EstimateShellNormal(SN.Vector3 sphereDir)
    {
        const float eps = 0.0025f;
        var t = SN.Vector3.Normalize(SN.Vector3.Cross(sphereDir, MathF.Abs(sphereDir.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY));
        var b = SN.Vector3.Normalize(SN.Vector3.Cross(sphereDir, t));
        var d0 = SN.Vector3.Normalize(sphereDir);
        var dT = SN.Vector3.Normalize(sphereDir + t * eps);
        var dB = SN.Vector3.Normalize(sphereDir + b * eps);
        float r0 = _sampler.SampleEditedSurfaceRadius(d0, 0f);
        float rT = _sampler.SampleEditedSurfaceRadius(dT, 0f);
        float rB = _sampler.SampleEditedSurfaceRadius(dB, 0f);
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
}
