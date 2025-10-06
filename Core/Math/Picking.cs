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


}
