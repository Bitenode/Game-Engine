using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SN = System.Numerics;

namespace Game_Engine.Core.Voxel;

/// <summary>
/// Result of meshing a VoxelChunk with the Transvoxel algorithm.
/// Contains all data needed to create an engine Mesh plus per-vertex biome blend info.
/// </summary>
public sealed class TransvoxelMeshData
{
    public List<SN.Vector3> Positions = new();
    public List<SN.Vector3> Normals = new();
    public List<SN.Vector2> UVs = new();
    public List<int> Indices = new();

    public List<SN.Vector4> BlendIndices = new();
    public List<SN.Vector4> BlendWeights = new();

    public bool IsEmpty => Positions.Count == 0;

    /// <summary>Append another mesh, shifting indices so both stay valid.</summary>
    public void Append(TransvoxelMeshData other)
    {
        if (other == null || other.IsEmpty)
            return;

        int origin = Positions.Count;
        Positions.AddRange(other.Positions);
        Normals.AddRange(other.Normals);
        UVs.AddRange(other.UVs);
        BlendIndices.AddRange(other.BlendIndices);
        BlendWeights.AddRange(other.BlendWeights);
        for (int i = 0; i < other.Indices.Count; i++)
            Indices.Add(other.Indices[i] + origin);
    }

    /// <summary>Replace stored normals with area-weighted face normals.</summary>
    public void RecalculateNormals()
    {
        int vertCount = Positions.Count;
        if (vertCount == 0) return;

        var normals = new SN.Vector3[vertCount];
        for (int i = 0; i < Indices.Count; i += 3)
        {
            int ia = Indices[i], ib = Indices[i + 1], ic = Indices[i + 2];
            if ((uint)ia >= (uint)vertCount || (uint)ib >= (uint)vertCount || (uint)ic >= (uint)vertCount)
                continue;

            var e1 = Positions[ib] - Positions[ia];
            var e2 = Positions[ic] - Positions[ia];
            var fn = SN.Vector3.Cross(e1, e2);
            normals[ia] += fn;
            normals[ib] += fn;
            normals[ic] += fn;
        }

        if (Normals.Count != vertCount)
        {
            Normals.Clear();
            for (int i = 0; i < vertCount; i++)
                Normals.Add(SN.Vector3.UnitY);
        }

        for (int i = 0; i < vertCount; i++)
        {
            float len = normals[i].Length();
            Normals[i] = len > 1e-8f ? normals[i] / len : SN.Vector3.UnitY;
        }
    }

    public Game_Engine.Core.Mesh ToEngineMesh()
    {
        var mesh = new Game_Engine.Core.Mesh(
            Positions.ToArray(),
            Array.Empty<int>(),
            Indices.ToArray()
        );
        mesh.Normals = Normals.ToArray();
        mesh.UVs = UVs.ToArray();
        mesh.PlanetBlendIndices = BlendIndices.ToArray();
        mesh.PlanetBlendWeights = BlendWeights.ToArray();
        return mesh;
    }

    /// <summary>Binary format for network replication (server → client).</summary>
    public byte[] SerializeToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteVec3List(w, Positions);
        WriteVec3List(w, Normals);
        WriteVec2List(w, UVs);
        w.Write(Indices.Count);
        for (int i = 0; i < Indices.Count; i++)
            w.Write(Indices[i]);
        WriteVec4List(w, BlendIndices);
        WriteVec4List(w, BlendWeights);
        return ms.ToArray();
    }

    public static TransvoxelMeshData? DeserializeFromBytes(byte[]? data)
    {
        if (data == null || data.Length < 8) return null;
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var d = new TransvoxelMeshData();
            ReadVec3List(r, d.Positions);
            ReadVec3List(r, d.Normals);
            ReadVec2List(r, d.UVs);
            int nIdx = r.ReadInt32();
            if (nIdx < 0 || nIdx > 10_000_000) return null;
            var idx = d.Indices;
            for (int i = 0; i < nIdx; i++)
                idx.Add(r.ReadInt32());
            ReadVec4List(r, d.BlendIndices);
            ReadVec4List(r, d.BlendWeights);
            return d;
        }
        catch
        {
            return null;
        }
    }

    static void WriteVec3List(BinaryWriter w, List<SN.Vector3> list)
    {
        w.Write(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            w.Write(list[i].X);
            w.Write(list[i].Y);
            w.Write(list[i].Z);
        }
    }

    static void WriteVec2List(BinaryWriter w, List<SN.Vector2> list)
    {
        w.Write(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            w.Write(list[i].X);
            w.Write(list[i].Y);
        }
    }

    static void WriteVec4List(BinaryWriter w, List<SN.Vector4> list)
    {
        w.Write(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            w.Write(list[i].X);
            w.Write(list[i].Y);
            w.Write(list[i].Z);
            w.Write(list[i].W);
        }
    }

    static void ReadVec3List(BinaryReader r, List<SN.Vector3> list)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000_000) throw new InvalidDataException();
        for (int i = 0; i < n; i++)
            list.Add(new SN.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
    }

    static void ReadVec2List(BinaryReader r, List<SN.Vector2> list)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000_000) throw new InvalidDataException();
        for (int i = 0; i < n; i++)
            list.Add(new SN.Vector2(r.ReadSingle(), r.ReadSingle()));
    }

    static void ReadVec4List(BinaryReader r, List<SN.Vector4> list)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000_000) throw new InvalidDataException();
        for (int i = 0; i < n; i++)
            list.Add(new SN.Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
    }
}

/// <summary>
/// Generates a mesh from a VoxelChunk using the Transvoxel algorithm (regular cells).
/// Transition cells on flagged edges handle LOD seam stitching.
/// </summary>
public static class TransvoxelMesher
{
    /// <summary>
    /// Generate a mesh from a voxel chunk. The transition mask indicates which of the
    /// 4 edges (±U, ±V) border a coarser LOD neighbor and need transition cells.
    /// </summary>
    public static TransvoxelMeshData GenerateMesh(VoxelChunk chunk, byte transitionMask = 0)
    {
        var data = new TransvoxelMeshData();
        int size = chunk.Size;

        // [z, y, x, edge] — must include X. A 2D (z,y) cache is overwritten by the
        // last X in the row, then -Y reuse pulls those verts and rips horizontal fans
        // across the whole leaf (the shredded rectangle on the facing cube face).
        var reuseCache = new int[size + 1, size + 1, size + 1, 4];
        for (int z = 0; z < size + 1; z++)
            for (int y = 0; y < size + 1; y++)
                for (int x = 0; x < size + 1; x++)
                    for (int r = 0; r < 4; r++)
                        reuseCache[z, y, x, r] = -1;

        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    ProcessRegularCell(chunk, x, y, z, data, reuseCache);
                }
            }
        }

        // Transition tables are not wired to real edge pairs yet (verts were
        // interpolated around a 9-sample ring). That fans extra shredded tris
        // on the facing leaf. Regular cells stitch enough once reuse includes X.
        if (transitionMask != 0)
        {
            // Intentionally skipped until GenerateTransitionEdge uses Transvoxel vertex data.
        }

        return data;
    }

    static void ProcessRegularCell(VoxelChunk chunk, int x, int y, int z,
        TransvoxelMeshData data, int[,,,] reuseCache)
    {
        Span<float> cornerDensity = stackalloc float[8];
        Span<byte> cornerMat = stackalloc byte[8];
        int caseCode = 0;

        for (int i = 0; i < 8; i++)
        {
            var off = MarchingCubesTables.CornerOffset[i];
            int cx = x + off.X;
            int cy = y + off.Y;
            int cz = z + off.Z;
            cornerDensity[i] = chunk.Sample(cx, cy, cz);
            cornerMat[i] = chunk.GetMaterial(cx, cy, cz);
            if (cornerDensity[i] < 0f)
                caseCode |= (1 << i);
        }

        if (caseCode == 0 || caseCode == 255)
            return;

        int cellClass = MarchingCubesTables.RegularCellClass[caseCode];
        ref readonly var cellData = ref MarchingCubesTables.RegularCellData[cellClass];
        var vertexData = MarchingCubesTables.RegularVertexData[caseCode];

        int vertCount = cellData.VertexCount;
        int triCount = cellData.TriangleCount;

        Span<int> localToGlobal = stackalloc int[vertCount];

        for (int i = 0; i < vertCount; i++)
        {
            ushort vd = vertexData[i];
            int cornerA = (vd >> 4) & 0x0F;
            int cornerB = vd & 0x0F;

            int reuseDir = (vd >> 12) & 0x0F;
            int reuseIdx = (vd >> 8) & 0x0F;

            // Skip transvoxel vertex reuse on spherical chunks: the Lengyel reuse
            // cache still fans triangles across a leaf even with an XYZ key.

            float dA = cornerDensity[cornerA];
            float dB = cornerDensity[cornerB];
            float t = (MathF.Abs(dA - dB) > 1e-6f) ? dA / (dA - dB) : 0.5f;
            t = Math.Clamp(t, 0f, 1f);

            var offA = MarchingCubesTables.CornerOffset[cornerA];
            var offB = MarchingCubesTables.CornerOffset[cornerB];

            float px = x + offA.X + t * (offB.X - offA.X);
            float py = y + offA.Y + t * (offB.Y - offA.Y);
            float pz = z + offA.Z + t * (offB.Z - offA.Z);

            SN.Vector3 worldPos = chunk.GridToWorld(px, py, pz);
            SN.Vector3 normal = ComputeGradient(chunk, px, py, pz);

            float triU = px / chunk.Size;
            float triV = py / chunk.Size;

            byte matA = cornerMat[cornerA];
            byte matB = cornerMat[cornerB];
            byte dominantMat = (t < 0.5f) ? matA : matB;

            int globalIdx = data.Positions.Count;
            data.Positions.Add(worldPos);
            data.Normals.Add(normal);
            data.UVs.Add(new SN.Vector2(triU, triV));
            data.BlendIndices.Add(new SN.Vector4(dominantMat, matA == matB ? dominantMat : (matA == dominantMat ? matB : matA), 0, 0));
            data.BlendWeights.Add(new SN.Vector4(1f, 0f, 0f, 0f));

            localToGlobal[i] = globalIdx;
            StoreReuseVertex(reuseDir, reuseIdx, x, y, z, globalIdx, reuseCache, chunk.Size);
        }

        for (int t = 0; t < triCount; t++)
        {
            int i0 = cellData.VertexIndex[t * 3 + 0];
            int i1 = cellData.VertexIndex[t * 3 + 1];
            int i2 = cellData.VertexIndex[t * 3 + 2];
            data.Indices.Add(localToGlobal[i0]);
            data.Indices.Add(localToGlobal[i1]);
            data.Indices.Add(localToGlobal[i2]);
        }
    }

    static int TryReuseVertex(int reuseDir, int reuseIdx, int x, int y, int z,
        int[,,,] cache, int size)
    {
        if (reuseDir == 0) return -1;

        int dx = (reuseDir & 1) != 0 ? -1 : 0;
        int dy = (reuseDir & 2) != 0 ? -1 : 0;
        int dz = (reuseDir & 4) != 0 ? -1 : 0;

        int nx = x + dx;
        int ny = y + dy;
        int nz = z + dz;

        if (nx < 0 || ny < 0 || nz < 0) return -1;
        if (nx > size || ny > size || nz > size) return -1;
        if (reuseIdx >= 4) return -1;

        return cache[nz, ny, nx, reuseIdx];
    }

    static void StoreReuseVertex(int reuseDir, int reuseIdx, int x, int y, int z,
        int globalIdx, int[,,,] cache, int size)
    {
        if (reuseIdx >= 4) return;
        if ((uint)x > (uint)size || (uint)y > (uint)size || (uint)z > (uint)size)
            return;
        cache[z, y, x, reuseIdx] = globalIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static SN.Vector3 ComputeGradient(VoxelChunk chunk, float fx, float fy, float fz)
    {
        int ix = (int)fx;
        int iy = (int)fy;
        int iz = (int)fz;

        int maxI = chunk.SamplesPerAxis - 2;
        ix = Math.Clamp(ix, 1, maxI);
        iy = Math.Clamp(iy, 1, maxI);
        iz = Math.Clamp(iz, 1, maxI);

        float dx = chunk.Sample(ix + 1, iy, iz) - chunk.Sample(ix - 1, iy, iz);
        float dy = chunk.Sample(ix, iy + 1, iz) - chunk.Sample(ix, iy - 1, iz);
        float dz = chunk.Sample(ix, iy, iz + 1) - chunk.Sample(ix, iy, iz - 1);

        var grad = new SN.Vector3(dx, dy, dz);
        float len = grad.Length();
        return len > 1e-6f ? -grad / len : SN.Vector3.UnitY;
    }

    /// <summary>
    /// Generate transition cells along the edges flagged in the transition mask.
    /// These cells have a half-resolution sampling pattern to match the coarser neighbor.
    /// Bit 0=-U edge, 1=+U, 2=-V, 3=+V.
    /// </summary>
    static void GenerateTransitionCells(VoxelChunk chunk, byte mask, TransvoxelMeshData data)
    {
        int size = chunk.Size;

        if ((mask & 1) != 0) GenerateTransitionEdge(chunk, data, size, 0);
        if ((mask & 2) != 0) GenerateTransitionEdge(chunk, data, size, 1);
        if ((mask & 4) != 0) GenerateTransitionEdge(chunk, data, size, 2);
        if ((mask & 8) != 0) GenerateTransitionEdge(chunk, data, size, 3);
    }

    /// <summary>
    /// Generate transition geometry along one edge. Uses a simplified approach:
    /// for each pair of high-res cells along the edge, create an intermediate
    /// "transition cell" that bridges the half-res neighbor.
    /// edge: 0=-X, 1=+X, 2=-Y, 3=+Y (in the XY plane of the chunk, Z is radial).
    /// </summary>
    static void GenerateTransitionEdge(VoxelChunk chunk, TransvoxelMeshData data, int size, int edge)
    {
        for (int z = 0; z < size; z++)
        {
            for (int along = 0; along < size; along += 2)
            {
                int a0 = along;
                int a1 = Math.Min(along + 1, size - 1);
                int a2 = Math.Min(along + 2, size);

                int fixedHi, fixedLo;
                switch (edge)
                {
                    case 0: fixedHi = 0; fixedLo = 0; break;
                    case 1: fixedHi = size; fixedLo = size; break;
                    case 2: fixedHi = 0; fixedLo = 0; break;
                    case 3: fixedHi = size; fixedLo = size; break;
                    default: continue;
                }

                var samples = new float[9];
                var positions = new SN.Vector3[9];

                if (edge <= 1)
                {
                    int fx = fixedHi;
                    FillTransitionSamples(chunk, fx, a0, a1, a2, z, true, samples, positions);
                }
                else
                {
                    int fy = fixedHi;
                    FillTransitionSamples(chunk, a0, fy, a1, a2, z, false, samples, positions);
                }

                int caseCode = 0;
                for (int i = 0; i < 9; i++)
                {
                    if (samples[i] < 0f)
                        caseCode |= (1 << i);
                }

                if (caseCode == 0 || caseCode == 511)
                    continue;

                EmitTransitionTriangles(chunk, data, caseCode, samples, positions);
            }
        }
    }

    static void FillTransitionSamples(VoxelChunk chunk, int x0, int y0, int y1, int y2, int z,
        bool fixedIsX, float[] samples, SN.Vector3[] positions)
    {
        int n = chunk.SamplesPerAxis;

        if (fixedIsX)
        {
            int fx = Math.Clamp(x0, 0, n - 1);
            int cy0 = Math.Clamp(y0, 0, n - 1);
            int cy1 = Math.Clamp(y1, 0, n - 1);
            int cy2 = Math.Clamp(y2, 0, n - 1);
            int cz0 = Math.Clamp(z, 0, n - 1);
            int cz1 = Math.Clamp(z + 1, 0, n - 1);

            samples[0] = chunk.Sample(fx, cy0, cz0); positions[0] = chunk.GridToWorld(fx, cy0, cz0);
            samples[1] = chunk.Sample(fx, cy1, cz0); positions[1] = chunk.GridToWorld(fx, cy1, cz0);
            samples[2] = chunk.Sample(fx, cy2, cz0); positions[2] = chunk.GridToWorld(fx, cy2, cz0);
            samples[3] = chunk.Sample(fx, cy0, cz1); positions[3] = chunk.GridToWorld(fx, cy0, cz1);
            samples[4] = chunk.Sample(fx, cy1, cz1); positions[4] = chunk.GridToWorld(fx, cy1, cz1);
            samples[5] = chunk.Sample(fx, cy2, cz1); positions[5] = chunk.GridToWorld(fx, cy2, cz1);

            float midY01 = (cy0 + cy1) * 0.5f;
            float midY12 = (cy1 + cy2) * 0.5f;
            float midZ = (cz0 + cz1) * 0.5f;

            samples[6] = (samples[0] + samples[1]) * 0.5f; positions[6] = chunk.GridToWorld(fx, midY01, cz0);
            samples[7] = (samples[1] + samples[2]) * 0.5f; positions[7] = chunk.GridToWorld(fx, midY12, cz0);
            samples[8] = (samples[0] + samples[3]) * 0.5f; positions[8] = chunk.GridToWorld(fx, cy0, midZ);
        }
        else
        {
            int fy = Math.Clamp(y0, 0, n - 1);
            int cx0 = Math.Clamp(x0, 0, n - 1);
            int cx1 = Math.Clamp(y1, 0, n - 1);
            int cx2 = Math.Clamp(y2, 0, n - 1);
            int cz0 = Math.Clamp(z, 0, n - 1);
            int cz1 = Math.Clamp(z + 1, 0, n - 1);

            samples[0] = chunk.Sample(cx0, fy, cz0); positions[0] = chunk.GridToWorld(cx0, fy, cz0);
            samples[1] = chunk.Sample(cx1, fy, cz0); positions[1] = chunk.GridToWorld(cx1, fy, cz0);
            samples[2] = chunk.Sample(cx2, fy, cz0); positions[2] = chunk.GridToWorld(cx2, fy, cz0);
            samples[3] = chunk.Sample(cx0, fy, cz1); positions[3] = chunk.GridToWorld(cx0, fy, cz1);
            samples[4] = chunk.Sample(cx1, fy, cz1); positions[4] = chunk.GridToWorld(cx1, fy, cz1);
            samples[5] = chunk.Sample(cx2, fy, cz1); positions[5] = chunk.GridToWorld(cx2, fy, cz1);

            float midX01 = (cx0 + cx1) * 0.5f;
            float midX12 = (cx1 + cx2) * 0.5f;
            float midZ = (cz0 + cz1) * 0.5f;

            samples[6] = (samples[0] + samples[1]) * 0.5f; positions[6] = chunk.GridToWorld(midX01, fy, cz0);
            samples[7] = (samples[1] + samples[2]) * 0.5f; positions[7] = chunk.GridToWorld(midX12, fy, cz0);
            samples[8] = (samples[0] + samples[3]) * 0.5f; positions[8] = chunk.GridToWorld(cx0, fy, midZ);
        }
    }

    static void EmitTransitionTriangles(VoxelChunk chunk, TransvoxelMeshData data,
        int caseCode, float[] samples, SN.Vector3[] positions)
    {
        byte cellClassByte = caseCode < MarchingCubesTables.TransitionCellClass.Length
            ? MarchingCubesTables.TransitionCellClass[caseCode]
            : (byte)0;

        int classIndex = cellClassByte & 0x7F;
        if (classIndex >= MarchingCubesTables.TransitionCellData.Length)
            return;

        ref readonly var cellData = ref MarchingCubesTables.TransitionCellData[classIndex];
        int triCount = cellData.TriangleCount;
        int vertCount = cellData.VertexCount;

        if (triCount == 0 || vertCount == 0) return;

        bool invert = (cellClassByte & 0x80) != 0;
        int baseVertex = data.Positions.Count;

        for (int v = 0; v < vertCount; v++)
        {
            int sA = v % 9;
            int sB = (v + 1) % 9;

            float dA = samples[sA];
            float dB = samples[sB];
            float t = (MathF.Abs(dA - dB) > 1e-6f) ? dA / (dA - dB) : 0.5f;
            t = Math.Clamp(t, 0f, 1f);

            SN.Vector3 pos = SN.Vector3.Lerp(positions[sA], positions[sB], t);
            SN.Vector3 normal = SN.Vector3.Normalize(pos);
            if (normal.LengthSquared() < 0.5f)
                normal = SN.Vector3.UnitY;

            data.Positions.Add(pos);
            data.Normals.Add(normal);
            data.UVs.Add(new SN.Vector2(pos.X * 0.01f, pos.Z * 0.01f));
            data.BlendIndices.Add(new SN.Vector4(0, 0, 0, 0));
            data.BlendWeights.Add(new SN.Vector4(1, 0, 0, 0));
        }

        for (int t = 0; t < triCount; t++)
        {
            int i0 = cellData.VertexIndex[t * 3 + 0];
            int i1 = cellData.VertexIndex[t * 3 + 1];
            int i2 = cellData.VertexIndex[t * 3 + 2];

            if (i0 >= vertCount || i1 >= vertCount || i2 >= vertCount)
                continue;

            if (invert)
            {
                data.Indices.Add(baseVertex + i0);
                data.Indices.Add(baseVertex + i2);
                data.Indices.Add(baseVertex + i1);
            }
            else
            {
                data.Indices.Add(baseVertex + i0);
                data.Indices.Add(baseVertex + i1);
                data.Indices.Add(baseVertex + i2);
            }
        }
    }
}
