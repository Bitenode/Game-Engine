#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics;

/// <summary>Shared primitive tests used by CollisionWorld and body simulation.</summary>
public static class GeometryQueries
{
    public static bool RayAABB(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 min, SN.Vector3 max,
        out float tHit, out SN.Vector3 normal)
    {
        tHit = 0;
        normal = SN.Vector3.Zero;
        float tmin = float.NegativeInfinity;
        float tmax = float.PositiveInfinity;
        int hitAxis = 0;
        bool hitMin = false;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
            float bmin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float bmax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < bmin || o > bmax) return false;
            }
            else
            {
                float t1 = (bmin - o) / d;
                float t2 = (bmax - o) / d;
                bool swapped = false;
                if (t1 > t2) { (t1, t2) = (t2, t1); swapped = true; }
                if (t1 > tmin) { tmin = t1; hitAxis = axis; hitMin = !swapped; }
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return false;
            }
        }

        if (tmax < 0) return false;
        tHit = tmin >= 0 ? tmin : tmax;
        normal = hitAxis switch
        {
            0 => hitMin ? -SN.Vector3.UnitX : SN.Vector3.UnitX,
            1 => hitMin ? -SN.Vector3.UnitY : SN.Vector3.UnitY,
            _ => hitMin ? -SN.Vector3.UnitZ : SN.Vector3.UnitZ,
        };
        return true;
    }

    /// <summary>Möller–Trumbore. Returns true when the ray hits the triangle at t ≥ 0.</summary>
    public static bool RayTriangle(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c,
        out float t, bool twoSided = true)
    {
        t = 0f;
        const float eps = 1e-8f;
        var e1 = b - a;
        var e2 = c - a;
        var p = SN.Vector3.Cross(rd, e2);
        float det = SN.Vector3.Dot(e1, p);
        if (twoSided)
        {
            if (MathF.Abs(det) < eps) return false;
        }
        else if (det < eps) return false;

        float inv = 1f / det;
        var s = ro - a;
        float u = SN.Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = SN.Vector3.Cross(s, e1);
        float v = SN.Vector3.Dot(rd, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = SN.Vector3.Dot(e2, q) * inv;
        return t >= 0f;
    }

    public static bool PointInTriangleExpanded(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, float expand)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;
        float d00 = SN.Vector3.Dot(v0, v0);
        float d01 = SN.Vector3.Dot(v0, v1);
        float d11 = SN.Vector3.Dot(v1, v1);
        float d20 = SN.Vector3.Dot(v2, v0);
        float d21 = SN.Vector3.Dot(v2, v1);
        float denom = d00 * d11 - d01 * d01;
        if (MathF.Abs(denom) < 1e-12f) return false;
        float v = (d11 * d20 - d01 * d21) / denom;
        float w = (d00 * d21 - d01 * d20) / denom;
        float u = 1f - v - w;
        float e = expand;
        return u >= -e && v >= -e && w >= -e;
    }

    public static bool SphereTriangle(SN.Vector3 center, float radius, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c,
        out SN.Vector3 normal, out float penetration)
    {
        normal = SN.Vector3.Zero;
        penetration = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var n = SN.Vector3.Cross(e1, e2);
        float nLen = n.Length();
        if (nLen < 1e-8f) return false;
        n /= nLen;
        float dist = SN.Vector3.Dot(center - a, n);
        if (MathF.Abs(dist) > radius) return false;
        var proj = center - n * dist;
        if (!PointInTriangleExpanded(proj, a, b, c, radius * 0.15f / Math.Max(nLen, 1e-4f)))
            return false;
        penetration = radius - MathF.Abs(dist);
        normal = dist >= 0f ? n : -n;
        return penetration >= 0f;
    }

    /// <summary>Swept sphere vs triangle (ray from origin along dir with radius inflate via plane offset).</summary>
    public static bool SphereCastTriangle(SN.Vector3 origin, SN.Vector3 dir, float radius,
        SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, float maxDist, out float t, out SN.Vector3 normal)
    {
        t = 0f;
        normal = SN.Vector3.Zero;
        var e1 = b - a;
        var e2 = c - a;
        var n = SN.Vector3.Cross(e1, e2);
        float nLen = n.Length();
        if (nLen < 1e-8f) return false;
        n /= nLen;
        float denom = SN.Vector3.Dot(dir, n);
        if (MathF.Abs(denom) < 1e-8f) return false;
        float signed = SN.Vector3.Dot(origin - a, n);
        float offset = signed >= 0f ? radius : -radius;
        t = (offset - signed) / denom;
        if (t < 0f || t > maxDist) return false;
        var hit = origin + dir * t - n * offset;
        if (!PointInTriangleExpanded(hit, a, b, c, radius * 0.25f)) return false;
        normal = signed >= 0f ? n : -n;
        return true;
    }

    public static bool CapsuleCastTriangle(SN.Vector3 p0, SN.Vector3 p1, float radius, SN.Vector3 dir,
        SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, float maxDist, out float t, out SN.Vector3 normal)
    {
        // Approximate: sphere-cast from both capsule ends and the midpoint.
        t = maxDist + 1f;
        normal = SN.Vector3.Zero;
        bool hit = false;
        SN.Vector3[] starts = { p0, p1, (p0 + p1) * 0.5f };
        for (int i = 0; i < starts.Length; i++)
        {
            if (SphereCastTriangle(starts[i], dir, radius, a, b, c, maxDist, out float ti, out var ni) && ti < t)
            {
                t = ti;
                normal = ni;
                hit = true;
            }
        }
        return hit;
    }
}
