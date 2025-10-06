#nullable enable
using System;
using SN = System.Numerics;
using Avalonia;

namespace Game_Engine.Core;

public static class Projection
{
    public const float NearEps = 0.001f;

    public static bool TryProjectViewPoint(
        SN.Vector3 pView, in SN.Matrix4x4 proj, Size size,
        out Point screen, out SN.Vector4 clip, out int outCode)
    {
        clip = SN.Vector4.Transform(new SN.Vector4(pView, 1), proj);
        if (clip.W < NearEps) { screen = default; outCode = 0; return false; }
        var ndc = clip / clip.W;
        screen = new Point(
            (ndc.X * 0.5f + 0.5f) * size.Width,
            (1 - (ndc.Y * 0.5f + 0.5f)) * size.Height);
        outCode = (ndc.X < -1 ? 1 : 0) | (ndc.X > 1 ? 2 : 0) |
                  (ndc.Y < -1 ? 4 : 0) | (ndc.Y > 1 ? 8 : 0) |
                  (ndc.Z < 0 ? 16 : 0) | (ndc.Z > 1 ? 32 : 0);
        return true;
    }

    public static bool ProjectWorldToScreen(
        SN.Vector3 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz,
        out Point screen, out SN.Vector3 viewPos)
    {
        viewPos = SN.Vector3.Transform(world, view);
        return TryProjectViewPoint(viewPos, proj, sz, out screen, out _, out _);
    }

    public static bool ProjectToScreenVP(
        SN.Vector3 pW, in SN.Matrix4x4 vp, Size sz, out Point p)
    {
        var clip = SN.Vector4.Transform(new SN.Vector4(pW, 1f), vp);
        if (clip.W <= 0f) { p = default; return false; }
        float invW = 1f / clip.W;
        double x = ((clip.X * invW) * 0.5 + 0.5) * sz.Width;
        double y = (1.0 - ((clip.Y * invW) * 0.5 + 0.5)) * sz.Height;
        p = new Point(x, y);
        return true;
    }

    

    public static bool ClipToNear(ref SN.Vector4 a, ref SN.Vector4 b)
    {
        bool ab = a.W < NearEps, bb = b.W < NearEps;
        if (ab && bb) return false;
        if (ab || bb)
        {
            var d = b - a; float t = (NearEps - a.W) / d.W; var p = a + t * d;
            if (ab) a = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
            else b = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
        }
        return true;
    }

    public static bool TryProjectSegment(
        SN.Vector3 A, SN.Vector3 B, in SN.Matrix4x4 vp, Size size,
        out Point p0, out Point p1)
    {
        var a = SN.Vector4.Transform(new SN.Vector4(A, 1), vp);
        var b = SN.Vector4.Transform(new SN.Vector4(B, 1), vp);
        if (!ClipToNear(ref a, ref b)) { p0 = default; p1 = default; return false; }
        var na = a / a.W; var nb = b / b.W;

        static int OutCode(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);

        if ((OutCode(na) & OutCode(nb)) != 0) { p0 = default; p1 = default; return false; }
        p0 = new Point((na.X * 0.5f + 0.5f) * size.Width,
                       (1 - (na.Y * 0.5f + 0.5f)) * size.Height);
        p1 = new Point((nb.X * 0.5f + 0.5f) * size.Width,
                       (1 - (nb.Y * 0.5f + 0.5f)) * size.Height);
        return true;
    }

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
        if (MathF.Abs(denom) < EPS) { hit = default; return false; }
        float t = SN.Vector3.Dot(p0 - ro, n) / denom;
        if (t < 0) { hit = default; return false; }
        hit = ro + rd * t;
        return true;
    }

    /// heuristic: screen-space radius in pixels for a local sphere (used by LOD)
    public static float EstimateProjectedRadiusPx(
        in SN.Matrix4x4 world, float radiusLocal,
        in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var centerW = SN.Vector3.Transform(SN.Vector3.Zero, world);
        var basisX = new SN.Vector3(world.M11, world.M12, world.M13);
        float sx = basisX.Length();
        float rWorld = radiusLocal * (sx <= 1e-6f ? 1f : sx);
        var edgeW = centerW + SN.Vector3.Normalize(basisX) * rWorld;

        if (!ProjectWorldToScreen(centerW, view, proj, sz, out var sc, out _) ||
            !ProjectWorldToScreen(edgeW, view, proj, sz, out var se, out _))
            return 32f;

        double dx = se.X - sc.X, dy = se.Y - sc.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }


    
}
