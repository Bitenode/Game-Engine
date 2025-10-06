#nullable enable
using Avalonia;
using Avalonia.Media;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class OverlayPrimitives
{
    public static void DrawLine3D(
        DrawingContext ctx, SN.Matrix4x4 vp, Size size,
        SN.Vector3 a, SN.Vector3 b, Color c, double th = 1)
    {
        if (!Projection.TryProjectSegment(a, b, vp, size, out var s0, out var s1)) return;
        ctx.DrawLine(new Pen(new SolidColorBrush(c), th), s0, s1);
    }
}
