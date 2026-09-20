#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics;

/// <summary>
/// Local-space spatial hash of a mesh's triangles. Built once per geometry version
/// so rigidbodies can test tens of nearby triangles instead of the whole mesh.
/// </summary>
public sealed class MeshSpatialIndex
{
    const int LargeCellSpan = 64;
    const int Bias = 1 << 20;

    static readonly ConditionalWeakTable<Mesh, MeshSpatialIndex> Cache = new();

    bool _built;
    int _geomVersion = int.MinValue;
    float _originX, _originY, _originZ;
    float _invCell;
    readonly Dictionary<long, int> _cellSlot = new();
    int[] _cellStart = Array.Empty<int>();
    int[] _cellCount = Array.Empty<int>();
    int[] _cellTris = Array.Empty<int>();
    int[] _largeTris = Array.Empty<int>();
    int[] _stamp = Array.Empty<int>();
    int _stampValue;

    public static MeshSpatialIndex For(Mesh mesh)
    {
        var index = Cache.GetValue(mesh, static _ => new MeshSpatialIndex());
        index.EnsureBuilt(mesh);
        return index;
    }

    public void QueryWorldAabb(in SN.Matrix4x4 world, SN.Vector3 worldMin, SN.Vector3 worldMax, List<int> triStarts)
    {
        if (!SN.Matrix4x4.Invert(world, out var inv))
        {
            QueryAll(triStarts);
            return;
        }

        GeometryQueries.TransformAabb(worldMin, worldMax, inv, out var localMin, out var localMax);
        const float pad = 1e-3f;
        localMin -= new SN.Vector3(pad);
        localMax += new SN.Vector3(pad);
        QueryLocalAabb(localMin, localMax, triStarts);
    }

    public void QueryLocalAabb(SN.Vector3 min, SN.Vector3 max, List<int> triStarts)
    {
        if (_stamp.Length == 0 && _largeTris.Length == 0)
            return;

        _stampValue++;
        if (_stampValue == int.MaxValue)
        {
            Array.Clear(_stamp, 0, _stamp.Length);
            _stampValue = 1;
        }

        for (int i = 0; i < _largeTris.Length; i++)
            AddUnique(triStarts, _largeTris[i]);

        int x0 = Cell(min.X, _originX);
        int y0 = Cell(min.Y, _originY);
        int z0 = Cell(min.Z, _originZ);
        int x1 = Cell(max.X, _originX);
        int y1 = Cell(max.Y, _originY);
        int z1 = Cell(max.Z, _originZ);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_cellSlot.TryGetValue(Pack(x, y, z), out int slot))
                continue;
            int start = _cellStart[slot];
            int count = _cellCount[slot];
            for (int i = 0; i < count; i++)
                AddUnique(triStarts, _cellTris[start + i]);
        }
    }

    void QueryAll(List<int> triStarts)
    {
        for (int i = 0; i < _largeTris.Length; i++)
            triStarts.Add(_largeTris[i]);
        for (int i = 0; i < _cellTris.Length; i++)
            triStarts.Add(_cellTris[i]);
    }

    void AddUnique(List<int> dest, int triStart)
    {
        int id = triStart / 3;
        if ((uint)id >= (uint)_stamp.Length)
        {
            dest.Add(triStart);
            return;
        }
        if (_stamp[id] == _stampValue)
            return;
        _stamp[id] = _stampValue;
        dest.Add(triStart);
    }

    void EnsureBuilt(Mesh mesh)
    {
        if (_built && _geomVersion == mesh.GeometryVersion)
            return;
        Rebuild(mesh);
        _built = true;
    }

    void Rebuild(Mesh mesh)
    {
        _geomVersion = mesh.GeometryVersion;
        _cellSlot.Clear();
        _cellStart = Array.Empty<int>();
        _cellCount = Array.Empty<int>();
        _cellTris = Array.Empty<int>();
        _largeTris = Array.Empty<int>();
        _stamp = Array.Empty<int>();

        var vtx = mesh.Vertices;
        var tri = mesh.TriIndices;
        if (vtx == null || tri == null || tri.Length < 3)
            return;

        int triCount = tri.Length / 3;
        _stamp = new int[triCount];

        mesh.GetLocalBounds(out var bmin, out var bmax);
        var extent = bmax - bmin;
        float maxDim = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        if (maxDim < 1e-4f) maxDim = 1f;

        float edge = EstimateEdge(vtx, tri, triCount);
        float cell = edge * 2f;
        float minCell = maxDim / 128f;
        float maxCell = maxDim / 4f;
        if (maxCell < minCell) maxCell = minCell;
        cell = Math.Clamp(cell, MathF.Max(minCell, 0.05f), MathF.Max(maxCell, 0.05f));
        _invCell = 1f / cell;
        _originX = bmin.X;
        _originY = bmin.Y;
        _originZ = bmin.Z;

        var counts = new Dictionary<long, int>();
        var large = new List<int>();
        CollectCells(vtx, tri, counts, large, countOnly: true, dest: null!);

        _largeTris = large.ToArray();
        int slotCount = counts.Count;
        _cellStart = new int[slotCount];
        _cellCount = new int[slotCount];
        int total = 0;
        int slot = 0;
        foreach (var kv in counts)
        {
            _cellSlot[kv.Key] = slot;
            _cellStart[slot] = total;
            total += kv.Value;
            slot++;
        }
        _cellTris = new int[total];

        CollectCells(vtx, tri, counts, large, countOnly: false, dest: _cellCount);
    }

    void CollectCells(
        SN.Vector3[] vtx, int[] tri,
        Dictionary<long, int> counts, List<int> large,
        bool countOnly, int[] dest)
    {
        if (!countOnly)
            Array.Clear(dest, 0, dest.Length);

        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            int ia = tri[t], ib = tri[t + 1], ic = tri[t + 2];
            if ((uint)ia >= (uint)vtx.Length || (uint)ib >= (uint)vtx.Length || (uint)ic >= (uint)vtx.Length)
                continue;

            var a = vtx[ia];
            var b = vtx[ib];
            var c = vtx[ic];
            var min = SN.Vector3.Min(a, SN.Vector3.Min(b, c));
            var max = SN.Vector3.Max(a, SN.Vector3.Max(b, c));

            int x0 = Cell(min.X, _originX);
            int y0 = Cell(min.Y, _originY);
            int z0 = Cell(min.Z, _originZ);
            int x1 = Cell(max.X, _originX);
            int y1 = Cell(max.Y, _originY);
            int z1 = Cell(max.Z, _originZ);
            int span = (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > LargeCellSpan)
            {
                if (countOnly)
                    large.Add(t);
                continue;
            }

            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                long key = Pack(x, y, z);
                if (countOnly)
                {
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
                else
                {
                    int s = _cellSlot[key];
                    _cellTris[_cellStart[s] + dest[s]] = t;
                    dest[s]++;
                }
            }
        }
    }

    static float EstimateEdge(SN.Vector3[] vtx, int[] tri, int triCount)
    {
        int samples = Math.Min(triCount, 256);
        if (samples <= 0) return 1f;
        float acc = 0f;
        int taken = 0;
        for (int s = 0; s < samples; s++)
        {
            int t = (s * triCount / samples) * 3;
            int ia = tri[t], ib = tri[t + 1], ic = tri[t + 2];
            if ((uint)ia >= (uint)vtx.Length || (uint)ib >= (uint)vtx.Length || (uint)ic >= (uint)vtx.Length)
                continue;
            var a = vtx[ia];
            var b = vtx[ib];
            var c = vtx[ic];
            acc += MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
            taken++;
        }
        return taken > 0 ? acc / taken : 1f;
    }

    int Cell(float v, float origin) => (int)MathF.Floor((v - origin) * _invCell);

    static long Pack(int x, int y, int z)
        => ((long)(x + Bias) << 42) | ((long)(y + Bias) << 21) | (uint)(z + Bias);
}
