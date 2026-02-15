#nullable enable
using Avalonia;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class Picking
{
    public static void BuildPickRay(
        Point pt, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz,
        out SN.Vector3 ro, out SN.Vector3 rd)
    {
        float x = (float)(pt.X / sz.Width * 2 - 1);
        float y = (float)(1 - pt.Y / sz.Height * 2);
        var np = new SN.Vector3(x, y, 0f);
        var fp = new SN.Vector3(x, y, 1f);

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var inv);
        var n4 = SN.Vector4.Transform(new SN.Vector4(np, 1), inv);
        var f4 = SN.Vector4.Transform(new SN.Vector4(fp, 1), inv);
        var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
        var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
        ro = n3;
        rd = SN.Vector3.Normalize(f3 - n3);
    }

    public static bool RayIntersectPlane(
        SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 n, SN.Vector3 p0, out SN.Vector3 hit)
    {
        const float EPS = 1e-6f;
        float denom = SN.Vector3.Dot(rd, n);
        if (System.MathF.Abs(denom) < EPS) { hit = default; return false; }
        float t = SN.Vector3.Dot(p0 - ro, n) / denom;
        if (t < 0) { hit = default; return false; }
        hit = ro + rd * t;
        return true;
    }

    /// <summary>Test ray against a sphere. Returns the distance t along the ray, or float.MaxValue if no hit.</summary>
    public static float RayIntersectSphere(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 center, float radius)
    {
        var oc = ro - center;
        float a = SN.Vector3.Dot(rd, rd);
        float b = 2f * SN.Vector3.Dot(oc, rd);
        float c = SN.Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - 4f * a * c;
        if (disc < 0f) return float.MaxValue;
        float sqrtDisc = System.MathF.Sqrt(disc);
        float t0 = (-b - sqrtDisc) / (2f * a);
        float t1 = (-b + sqrtDisc) / (2f * a);
        if (t0 > 0f) return t0;
        if (t1 > 0f) return t1;
        return float.MaxValue;
    }

    /// <summary>Test ray against an AABB. Returns the distance t, or float.MaxValue if no hit.</summary>
    public static float RayIntersectAABB(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 min, SN.Vector3 max)
    {
        float tmin = float.NegativeInfinity;
        float tmax = float.PositiveInfinity;

        for (int i = 0; i < 3; i++)
        {
            float origin = i == 0 ? ro.X : (i == 1 ? ro.Y : ro.Z);
            float dir = i == 0 ? rd.X : (i == 1 ? rd.Y : rd.Z);
            float bmin = i == 0 ? min.X : (i == 1 ? min.Y : min.Z);
            float bmax = i == 0 ? max.X : (i == 1 ? max.Y : max.Z);

            if (System.MathF.Abs(dir) < 1e-8f)
            {
                if (origin < bmin || origin > bmax) return float.MaxValue;
            }
            else
            {
                float t1 = (bmin - origin) / dir;
                float t2 = (bmax - origin) / dir;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = System.MathF.Max(tmin, t1);
                tmax = System.MathF.Min(tmax, t2);
                if (tmin > tmax) return float.MaxValue;
            }
        }

        return tmin > 0f ? tmin : (tmax > 0f ? tmax : float.MaxValue);
    }
}
