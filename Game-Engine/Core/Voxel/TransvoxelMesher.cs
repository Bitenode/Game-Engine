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
    /// Instant kill switch for LOD transition cells. <c>PlanetConfig.EnableTransvoxelTransitions</c>
    /// is copied onto this before remesh. Set false if a case still fans.
    /// </summary>
    public static bool EnableTransitionCells { get; set; } = true;

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

        if (EnableTransitionCells && transitionMask != 0)
            GenerateTransitionCells(chunk, transitionMask, data);

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
    /// Each cell samples the 13 Lengyel corners on the shared face (9 high-res + 4 coarse)
    /// and emits from TransitionVertexData — never ring interpolation.
    /// Bit 0=-U, 1=+U, 2=-V, 3=+V.
    /// </summary>
    static void GenerateTransitionCells(VoxelChunk chunk, byte mask, TransvoxelMeshData data)
    {
        int size = chunk.Size;
        if ((mask & 1) != 0) ProcessTransitionFace(chunk, data, size, 0);
        if ((mask & 2) != 0) ProcessTransitionFace(chunk, data, size, 1);
        if ((mask & 4) != 0) ProcessTransitionFace(chunk, data, size, 2);
        if ((mask & 8) != 0) ProcessTransitionFace(chunk, data, size, 3);
    }

    static void ProcessTransitionFace(VoxelChunk chunk, TransvoxelMeshData data, int size, int edge)
    {
        // 2x2 high-res cells per transition cell (3x3 high-res samples on the face).
        bool invertFace = edge == 1 || edge == 2;
        for (int radial = 0; radial < size; radial += 2)
        {
            for (int along = 0; along < size; along += 2)
                ProcessOneTransitionCell(chunk, data, size, edge, along, radial, invertFace);
        }
    }

    static void ProcessOneTransitionCell(
        VoxelChunk chunk, TransvoxelMeshData data, int size, int edge,
        int along, int radial, bool invertFace)
    {
        Span<float> densities = stackalloc float[13];
        Span<byte> materials = stackalloc byte[13];
        Span<float> gridX = stackalloc float[13];
        Span<float> gridY = stackalloc float[13];
        Span<float> gridZ = stackalloc float[13];

        int caseCode = 0;
        for (int i = 0; i < 13; i++)
        {
            var (s, t) = MarchingCubesTables.TransitionCornerST[i];
            MapTransitionCorner(edge, size, along + s, radial + t, out int x, out int y, out int z);
            densities[i] = chunk.Sample(x, y, z);
            materials[i] = chunk.GetMaterial(x, y, z);
            gridX[i] = x;
            gridY[i] = y;
            gridZ[i] = z;
            // Case index uses the 9 high-res samples only (512 Lengyel cases).
            if (i < 9 && densities[i] < 0f)
                caseCode |= 1 << i;
        }

        if (caseCode == 0 || caseCode == 511)
            return;

        var vertexData = MarchingCubesTables.TransitionVertexData[caseCode];
        if (vertexData.Length == 0)
            return;

        byte cellClassByte = MarchingCubesTables.TransitionCellClass[caseCode];
        int classIndex = cellClassByte & 0x7F;
        if ((uint)classIndex >= (uint)MarchingCubesTables.TransitionCellData.Length)
            return;

        ref readonly var cellData = ref MarchingCubesTables.TransitionCellData[classIndex];
        int vertCount = cellData.VertexCount;
        int triCount = cellData.TriangleCount;
        if (vertCount == 0 || triCount == 0 || vertexData.Length < vertCount)
            return;

        bool invert = ((cellClassByte & 0x80) != 0) ^ invertFace;
        Span<int> localToGlobal = stackalloc int[vertCount];

        for (int i = 0; i < vertCount; i++)
        {
            ushort vd = vertexData[i];
            int cornerA = (vd >> 4) & 0x0F;
            int cornerB = vd & 0x0F;
            if ((uint)cornerA > 12 || (uint)cornerB > 12)
                return;

            float dA = densities[cornerA];
            float dB = densities[cornerB];
            float t = (MathF.Abs(dA - dB) > 1e-6f) ? dA / (dA - dB) : 0.5f;
            t = Math.Clamp(t, 0f, 1f);

            float px = gridX[cornerA] + t * (gridX[cornerB] - gridX[cornerA]);
            float py = gridY[cornerA] + t * (gridY[cornerB] - gridY[cornerA]);
            float pz = gridZ[cornerA] + t * (gridZ[cornerB] - gridZ[cornerA]);

            SN.Vector3 worldPos = chunk.GridToWorld(px, py, pz);
            SN.Vector3 normal = ComputeGradient(chunk, px, py, pz);

            byte matA = materials[cornerA];
            byte matB = materials[cornerB];
            byte dominantMat = (t < 0.5f) ? matA : matB;

            int globalIdx = data.Positions.Count;
            data.Positions.Add(worldPos);
            data.Normals.Add(normal);
            data.UVs.Add(new SN.Vector2(px / chunk.Size, py / chunk.Size));
            data.BlendIndices.Add(new SN.Vector4(dominantMat, matA == matB ? dominantMat : (matA == dominantMat ? matB : matA), 0, 0));
            data.BlendWeights.Add(new SN.Vector4(1f, 0f, 0f, 0f));
            localToGlobal[i] = globalIdx;
        }

        for (int tri = 0; tri < triCount; tri++)
        {
            int i0 = cellData.VertexIndex[tri * 3 + 0];
            int i1 = cellData.VertexIndex[tri * 3 + 1];
            int i2 = cellData.VertexIndex[tri * 3 + 2];
            if ((uint)i0 >= (uint)vertCount || (uint)i1 >= (uint)vertCount || (uint)i2 >= (uint)vertCount)
                continue;

            if (invert)
            {
                data.Indices.Add(localToGlobal[i0]);
                data.Indices.Add(localToGlobal[i2]);
                data.Indices.Add(localToGlobal[i1]);
            }
            else
            {
                data.Indices.Add(localToGlobal[i0]);
                data.Indices.Add(localToGlobal[i1]);
                data.Indices.Add(localToGlobal[i2]);
            }
        }
    }

    static void MapTransitionCorner(int edge, int size, int along, int radial, out int x, out int y, out int z)
    {
        along = Math.Clamp(along, 0, size);
        radial = Math.Clamp(radial, 0, size);
        switch (edge)
        {
            case 0: // -U / -X
                x = 0; y = along; z = radial; break;
            case 1: // +U / +X
                x = size; y = along; z = radial; break;
            case 2: // -V / -Y
                x = along; y = 0; z = radial; break;
            default: // +V / +Y
                x = along; y = size; z = radial; break;
        }
    }
}
