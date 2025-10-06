#nullable enable
using Avalonia;

namespace Game_Engine.Core;

public static class Math2D
{
    public static double Distance(Point a, Point b)
        => System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double apx = p.X - a.X, apy = p.Y - a.Y;
        double denom = abx * abx + aby * aby;
        double t = denom > 1e-9 ? System.Math.Clamp((apx * abx + apy * aby) / denom, 0.0, 1.0) : 0.0;
        double cx = a.X + abx * t, cy = a.Y + aby * t;
        return System.Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
}
