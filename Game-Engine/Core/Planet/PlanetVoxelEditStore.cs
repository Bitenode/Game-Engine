using System;
using System.Collections.Generic;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Voxel edit overlays for planets.
/// Stores signed-density delta brushes in planet-local unscaled space
/// (see <see cref="PlanetSpace"/>). Positive values remove terrain (dig),
/// negative values add terrain (build).
/// </summary>
public sealed class PlanetVoxelEditStore
{
    public const int BakeStrokeThreshold = int.MaxValue;
    public const float DefaultBakeCellSize = 1f;

    readonly List<VoxelSphereEdit> _sphereEdits = new();
    readonly Dictionary<(int X, int Y, int Z), float> _bakedCells = new();
    readonly object _gate = new();
    float _bakedCellSize = DefaultBakeCellSize;
    float _maxRadius;

    /// <summary>
    /// When false (default), each stroke is stored independently so save/reload
    /// matches the authored brushes. Runtime coalescing is opt-in only.
    /// </summary>
    public bool CoalesceOnAdd { get; set; }

    public int SphereEditCount
    {
        get
        {
            lock (_gate) return _sphereEdits.Count;
        }
    }

    public int BakedCellCount
    {
        get
        {
            lock (_gate) return _bakedCells.Count;
        }
    }

    /// <summary>Largest stored brush radius in planet-local units (includes baked history).</summary>
    public float MaxRadius
    {
        get
        {
            lock (_gate) return _maxRadius;
        }
    }

    public readonly struct VoxelSphereEdit
    {
        public SN.Vector3 Center { get; init; }
        public float Radius { get; init; }
        public float DensityDelta { get; init; }
        public float Falloff { get; init; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sphereEdits.Clear();
            _bakedCells.Clear();
            _maxRadius = 0f;
        }
    }

    public void AddSphere(SN.Vector3 center, float radius, float densityDelta, float falloff)
    {
        if (radius <= 0.001f || MathF.Abs(densityDelta) <= 1e-6f)
            return;

        var edit = new VoxelSphereEdit
        {
            Center = center,
            Radius = radius,
            DensityDelta = densityDelta,
            Falloff = Math.Clamp(falloff, 0f, 1f),
        };

        lock (_gate)
        {
            if (CoalesceOnAdd && TryCoalesce(edit))
            {
                RecalcMaxRadiusLocked();
                return;
            }

            _sphereEdits.Add(edit);
            _maxRadius = MathF.Max(_maxRadius, edit.Radius);
        }
    }

    /// <summary>True if any stroke or baked cell overlaps a planet-local sphere.</summary>
    public bool OverlapsSphere(SN.Vector3 localCenter, float radius)
    {
        if (radius <= 0f) return false;
        float r = radius;
        lock (_gate)
        {
            if (_sphereEdits.Count == 0 && _bakedCells.Count == 0)
                return false;

            for (int i = 0; i < _sphereEdits.Count; i++)
            {
                var e = _sphereEdits[i];
                float max = r + e.Radius;
                if (SN.Vector3.DistanceSquared(localCenter, e.Center) <= max * max)
                    return true;
            }

            if (_bakedCells.Count == 0)
                return false;

            float cs = _bakedCellSize;
            float reach = r + cs * 1.5f;
            float reachSq = reach * reach;
            foreach (var kv in _bakedCells)
            {
                if (MathF.Abs(kv.Value) <= 1e-8f) continue;
                var p = new SN.Vector3(
                    (kv.Key.X + 0.5f) * cs,
                    (kv.Key.Y + 0.5f) * cs,
                    (kv.Key.Z + 0.5f) * cs);
                if (SN.Vector3.DistanceSquared(localCenter, p) <= reachSq)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Sample accumulated density delta at a planet-local unscaled point.</summary>
    public float SampleDensityDelta(SN.Vector3 localPos, float minInfluenceRadius = 0f)
    {
        lock (_gate)
        {
            float total = 0f;
            for (int i = 0; i < _sphereEdits.Count; i++)
                total += EvaluateSphere(_sphereEdits[i], localPos, minInfluenceRadius);
            total += SampleBakedLocked(localPos);
            return total;
        }
    }

    /// <summary>
    /// Height-field sample: brush influence uses arc length on the sphere so strokes
    /// still affect the crust after the surface has moved radially.
    /// </summary>
    public float SampleHeightDelta(SN.Vector3 localPos, float minInfluenceRadius = 0f)
    {
        lock (_gate)
        {
            float total = 0f;
            for (int i = 0; i < _sphereEdits.Count; i++)
                total += EvaluateSphereOnShell(_sphereEdits[i], localPos, minInfluenceRadius);
            total += SampleBakedLocked(localPos);
            return total;
        }
    }

    /// <summary>Add every stored brush and baked cell into a density grid (one lock for the whole chunk).</summary>
    public void AccumulateIntoChunk(VoxelChunk chunk)
    {
        lock (_gate)
        {
            if (_sphereEdits.Count == 0 && _bakedCells.Count == 0)
                return;

            int n = chunk.SamplesPerAxis;
            for (int z = 0; z < n; z++)
            {
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        var localPos = chunk.GridToWorld(x, y, z);
                        float total = 0f;
                        for (int i = 0; i < _sphereEdits.Count; i++)
                            total += EvaluateSphere(_sphereEdits[i], localPos);
                        total += SampleBakedLocked(localPos);
                        if (total != 0f)
                            chunk.Set(x, y, z, chunk.Sample(x, y, z) + total);
                    }
                }
            }
        }
    }

    public static float EvaluateSphere(in VoxelSphereEdit edit, SN.Vector3 localPos, float minInfluenceRadius = 0f)
    {
        var toPoint = localPos - edit.Center;
        float dist = toPoint.Length();
        float radius = MathF.Max(edit.Radius, minInfluenceRadius);
        if (dist >= radius) return 0f;

        float t = 1f - (dist / radius);
        float weight = edit.Falloff switch
        {
            <= 0.001f => 1f,
            >= 0.999f => t,
            _ => MathF.Pow(t, 1f + edit.Falloff * 3f),
        };
        return edit.DensityDelta * weight;
    }

    public static float EvaluateSphereOnShell(in VoxelSphereEdit edit, SN.Vector3 localPos, float minInfluenceRadius = 0f)
    {
        float pLen = localPos.Length();
        float cLen = edit.Center.Length();
        if (pLen < 1e-5f || cLen < 1e-5f)
            return EvaluateSphere(edit, localPos, minInfluenceRadius);

        var np = localPos / pLen;
        var nc = edit.Center / cLen;
        float dot = Math.Clamp(SN.Vector3.Dot(np, nc), -1f, 1f);
        float arc = MathF.Acos(dot) * cLen;
        float radius = MathF.Max(edit.Radius, minInfluenceRadius);
        if (arc >= radius) return 0f;

        // Linear-ish weight over the (possibly widened) radius so coarse shell verts
        // still move. Height stays DensityDelta — only the footprint grows.
        float t = 1f - (arc / radius);
        float weight = t * t * (3f - 2f * t);
        return edit.DensityDelta * weight;
    }

    /// <summary>
    /// Rasterize live strokes onto a sparse cell grid and clear the stroke list.
    /// Call before save when the stroke list would grow without bound.
    /// </summary>
    public void BakeStrokesToCells(float cellSize = DefaultBakeCellSize)
    {
        if (cellSize <= 1e-4f)
            cellSize = DefaultBakeCellSize;

        lock (_gate)
        {
            BakeStrokesLocked(cellSize);
        }
    }

    /// <summary>
    /// Snapshot for persistence. Does not coalesce. If the live stroke count exceeds
    /// <see cref="BakeStrokeThreshold"/>, strokes are baked into sparse cells first.
    /// </summary>
    public PlanetVoxelEditAsset ExportAsset(bool bakeIfOverThreshold = true)
    {
        lock (_gate)
        {
            // Keep authored spheres. Baking 1m cells from a 12m brush explodes the sidecar
            // and used to force a broken transvoxel remesh of the facing leaf.

            var strokes = new PlanetVoxelSphereStroke[_sphereEdits.Count];
            for (int i = 0; i < _sphereEdits.Count; i++)
            {
                var e = _sphereEdits[i];
                strokes[i] = new PlanetVoxelSphereStroke
                {
                    X = e.Center.X,
                    Y = e.Center.Y,
                    Z = e.Center.Z,
                    Radius = e.Radius,
                    DensityDelta = e.DensityDelta,
                    Falloff = e.Falloff,
                };
            }

            var cells = new PlanetVoxelBakedCell[_bakedCells.Count];
            int ci = 0;
            foreach (var kv in _bakedCells)
            {
                cells[ci++] = new PlanetVoxelBakedCell
                {
                    IX = kv.Key.X,
                    IY = kv.Key.Y,
                    IZ = kv.Key.Z,
                    Delta = kv.Value,
                };
            }

            return new PlanetVoxelEditAsset
            {
                Version = PlanetVoxelEditAsset.CurrentVersion,
                Space = PlanetVoxelEditAsset.PlanetLocalUnscaledSpace,
                BakedCellSize = _bakedCellSize > 0f ? _bakedCellSize : DefaultBakeCellSize,
                MaxRadius = _maxRadius,
                Strokes = strokes,
                BakedCells = cells,
            };
        }
    }

    /// <summary>Replace live state from a sidecar asset (planet-local). Does not append.</summary>
    public void LoadFromAsset(PlanetVoxelEditAsset? asset)
    {
        lock (_gate)
        {
            _sphereEdits.Clear();
            _bakedCells.Clear();
            _maxRadius = 0f;
            _bakedCellSize = DefaultBakeCellSize;
            if (asset == null)
                return;

            if (!string.IsNullOrWhiteSpace(asset.Space) &&
                !string.Equals(asset.Space, PlanetVoxelEditAsset.PlanetLocalUnscaledSpace, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Planet voxel edits must be in {PlanetVoxelEditAsset.PlanetLocalUnscaledSpace}, got '{asset.Space}'.");
            }

            if (asset.BakedCellSize > 1e-4f)
                _bakedCellSize = asset.BakedCellSize;

            var strokes = asset.Strokes;
            if (strokes != null)
            {
                for (int i = 0; i < strokes.Length; i++)
                {
                    var s = strokes[i];
                    if (s == null || s.Radius <= 0.001f || MathF.Abs(s.DensityDelta) <= 1e-6f)
                        continue;
                    _sphereEdits.Add(new VoxelSphereEdit
                    {
                        Center = new SN.Vector3(s.X, s.Y, s.Z),
                        Radius = s.Radius,
                        DensityDelta = s.DensityDelta,
                        Falloff = Math.Clamp(s.Falloff, 0f, 1f),
                    });
                    _maxRadius = MathF.Max(_maxRadius, s.Radius);
                }
            }

            var cells = asset.BakedCells;
            if (cells != null)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    var c = cells[i];
                    if (c == null || MathF.Abs(c.Delta) <= 1e-8f)
                        continue;
                    _bakedCells[(c.IX, c.IY, c.IZ)] = c.Delta;
                }
            }

            if (asset.MaxRadius > _maxRadius)
                _maxRadius = asset.MaxRadius;
            RecalcMaxRadiusLocked();
        }
    }

    bool TryCoalesce(in VoxelSphereEdit incoming)
    {
        for (int i = _sphereEdits.Count - 1; i >= 0 && i >= _sphereEdits.Count - 8; i--)
        {
            var existing = _sphereEdits[i];
            if (MathF.Sign(existing.DensityDelta) != MathF.Sign(incoming.DensityDelta))
                continue;

            float mergeR = MathF.Min(existing.Radius, incoming.Radius) * 0.3f;
            if (SN.Vector3.DistanceSquared(existing.Center, incoming.Center) > mergeR * mergeR)
                continue;

            float absA = MathF.Abs(existing.DensityDelta);
            float absB = MathF.Abs(incoming.DensityDelta);
            float weight = absA + absB + 1e-5f;
            float cap = MathF.Max(2.5f, MathF.Max(existing.Radius, incoming.Radius) * 0.2f);
            float summed = existing.DensityDelta + incoming.DensityDelta;
            var merged = new VoxelSphereEdit
            {
                Center = (existing.Center * absA + incoming.Center * absB) / weight,
                Radius = MathF.Max(existing.Radius, incoming.Radius),
                DensityDelta = Math.Clamp(summed, -cap, cap),
                Falloff = (existing.Falloff + incoming.Falloff) * 0.5f,
            };
            _sphereEdits[i] = merged;
            return true;
        }

        return false;
    }

    void BakeStrokesLocked(float cellSize)
    {
        _bakedCellSize = cellSize;
        float inv = 1f / cellSize;
        for (int i = 0; i < _sphereEdits.Count; i++)
        {
            var edit = _sphereEdits[i];
            _maxRadius = MathF.Max(_maxRadius, edit.Radius);
            var c = edit.Center;
            float r = edit.Radius;
            int x0 = (int)MathF.Floor((c.X - r) * inv);
            int y0 = (int)MathF.Floor((c.Y - r) * inv);
            int z0 = (int)MathF.Floor((c.Z - r) * inv);
            int x1 = (int)MathF.Floor((c.X + r) * inv);
            int y1 = (int)MathF.Floor((c.Y + r) * inv);
            int z1 = (int)MathF.Floor((c.Z + r) * inv);

            for (int iz = z0; iz <= z1; iz++)
            {
                for (int iy = y0; iy <= y1; iy++)
                {
                    for (int ix = x0; ix <= x1; ix++)
                    {
                        var pos = new SN.Vector3(
                            (ix + 0.5f) * cellSize,
                            (iy + 0.5f) * cellSize,
                            (iz + 0.5f) * cellSize);
                        float delta = EvaluateSphere(edit, pos);
                        if (MathF.Abs(delta) <= 1e-8f)
                            continue;
                        var key = (ix, iy, iz);
                        _bakedCells.TryGetValue(key, out float existing);
                        float next = existing + delta;
                        if (MathF.Abs(next) <= 1e-8f)
                            _bakedCells.Remove(key);
                        else
                            _bakedCells[key] = next;
                    }
                }
            }
        }

        _sphereEdits.Clear();
    }

    float SampleBakedLocked(SN.Vector3 localPos)
    {
        if (_bakedCells.Count == 0)
            return 0f;

        float cell = _bakedCellSize > 1e-4f ? _bakedCellSize : DefaultBakeCellSize;
        float inv = 1f / cell;
        float fx = localPos.X * inv - 0.5f;
        float fy = localPos.Y * inv - 0.5f;
        float fz = localPos.Z * inv - 0.5f;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int z0 = (int)MathF.Floor(fz);
        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;

        float c000 = GetBaked(x0, y0, z0);
        float c100 = GetBaked(x0 + 1, y0, z0);
        float c010 = GetBaked(x0, y0 + 1, z0);
        float c110 = GetBaked(x0 + 1, y0 + 1, z0);
        float c001 = GetBaked(x0, y0, z0 + 1);
        float c101 = GetBaked(x0 + 1, y0, z0 + 1);
        float c011 = GetBaked(x0, y0 + 1, z0 + 1);
        float c111 = GetBaked(x0 + 1, y0 + 1, z0 + 1);

        float c00 = c000 + (c100 - c000) * tx;
        float c10 = c010 + (c110 - c010) * tx;
        float c01 = c001 + (c101 - c001) * tx;
        float c11 = c011 + (c111 - c011) * tx;
        float c0 = c00 + (c10 - c00) * ty;
        float c1 = c01 + (c11 - c01) * ty;
        return c0 + (c1 - c0) * tz;
    }

    float GetBaked(int x, int y, int z)
    {
        return _bakedCells.TryGetValue((x, y, z), out float d) ? d : 0f;
    }

    void RecalcMaxRadiusLocked()
    {
        float max = 0f;
        for (int i = 0; i < _sphereEdits.Count; i++)
            max = MathF.Max(max, _sphereEdits[i].Radius);
        _maxRadius = MathF.Max(_maxRadius, max);
    }
}
