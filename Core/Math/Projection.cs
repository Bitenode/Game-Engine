#nullable enable
using Avalonia;
using SN = System.Numerics;

namespace Game_Engine.Core.MathB;

public static class Projection
{
    private const float NearEps = 0.001f;

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

    public static bool ProjectToScreen(
        SN.Vector3 world, SN.Matrix4x4 view, SN.Matrix4x4 proj,
        Size sz, out Point screen, out SN.Vector3 viewPos)
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

    public static bool TryProjectSegment(
        SN.Vector3 A, SN.Vector3 B, SN.Matrix4x4 vp, Size size,
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

    private static bool ClipToNear(ref SN.Vector4 a, ref SN.Vector4 b)
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
}
