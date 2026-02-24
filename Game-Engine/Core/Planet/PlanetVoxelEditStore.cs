using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Runtime-only voxel edit overlays for planets.
/// Stores signed-density delta brushes; positive values remove terrain (dig),
/// negative values add terrain (build).
/// </summary>
public sealed class PlanetVoxelEditStore
{
    readonly List<VoxelSphereEdit> _sphereEdits = new();
    readonly object _gate = new();

    public int SphereEditCount
    {
        get
        {
            lock (_gate) return _sphereEdits.Count;
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
        lock (_gate) _sphereEdits.Clear();
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
            if (!TryCoalesce(edit))
                _sphereEdits.Add(edit);
        }
    }

    public float SampleDensityDelta(SN.Vector3 worldPos)
    {
        lock (_gate)
        {
            if (_sphereEdits.Count == 0)
                return 0f;

            float total = 0f;
            for (int i = 0; i < _sphereEdits.Count; i++)
                total += EvaluateSphere(_sphereEdits[i], worldPos);
            return total;
        }
    }

    static float EvaluateSphere(in VoxelSphereEdit edit, SN.Vector3 worldPos)
    {
        var toPoint = worldPos - edit.Center;
        float dist = toPoint.Length();
        if (dist >= edit.Radius) return 0f;

        float t = 1f - (dist / edit.Radius);
        float weight = edit.Falloff switch
        {
            <= 0.001f => 1f,
            >= 0.999f => t,
            _ => MathF.Pow(t, 1f + edit.Falloff * 3f),
        };
        return edit.DensityDelta * weight;
    }

    bool TryCoalesce(in VoxelSphereEdit incoming)
    {
        for (int i = _sphereEdits.Count - 1; i >= 0 && i >= _sphereEdits.Count - 8; i--)
        {
            var existing = _sphereEdits[i];
            if (MathF.Sign(existing.DensityDelta) != MathF.Sign(incoming.DensityDelta))
                continue;

            float maxDist = existing.Radius + incoming.Radius;
            if (SN.Vector3.DistanceSquared(existing.Center, incoming.Center) > maxDist * maxDist)
                continue;

            float absA = MathF.Abs(existing.DensityDelta);
            float absB = MathF.Abs(incoming.DensityDelta);
            float weight = absA + absB + 1e-5f;
            var merged = new VoxelSphereEdit
            {
                Center = (existing.Center * absA + incoming.Center * absB) / weight,
                Radius = MathF.Max(existing.Radius, incoming.Radius),
                DensityDelta = existing.DensityDelta + incoming.DensityDelta,
                Falloff = (existing.Falloff + incoming.Falloff) * 0.5f,
            };
            _sphereEdits[i] = merged;
            return true;
        }

        return false;
    }
}
