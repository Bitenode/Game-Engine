#nullable enable
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class MeshUtil
{
    // Approx local bounding radius (for any mesh)
    public static float ApproxLocalRadius(Mesh m)
    {
        float r2 = 0f;
        var v = m.Vertices;
        for (int i = 0; i < v.Length; i++)
        {
            float d2 = v[i].X * v[i].X + v[i].Y * v[i].Y + v[i].Z * v[i].Z;
            if (d2 > r2) r2 = d2;
        }
        return MathF.Sqrt(r2);
    }

    // Approx cylinder/cone parameters from geometry
    public static (float radius, float height) ApproxRadialAndHeight(Mesh m)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, r = 0f;
        foreach (var p in m.Vertices)
        {
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            float rr = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            if (rr > r) r = rr;
        }
        return (r, maxY - minY);
    }
}
