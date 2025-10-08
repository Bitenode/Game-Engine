using System;
using SN = System.Numerics;
using Avalonia.Media;
using Avalonia;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Views
{
    public static class ColliderGizmos
    {
        public static void DrawAABB(DrawingContext ctx, SN.Matrix4x4 vp, Size sz, Collider.AABB aabb, Color color, float thickness = 1f)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness <= 0 ? 1 : thickness);

            // 8 corners
            var c = new SN.Vector3[8];
            int i = 0;
            for (int x = 0; x <= 1; x++)
                for (int y = 0; y <= 1; y++)
                    for (int z = 0; z <= 1; z++)
                        c[i++] = new SN.Vector3(
                            x == 0 ? aabb.Min.X : aabb.Max.X,
                            y == 0 ? aabb.Min.Y : aabb.Max.Y,
                            z == 0 ? aabb.Min.Z : aabb.Max.Z);

            // index pairs for 12 edges
            int[] e = {
                0,1, 0,2, 0,4,
                7,6, 7,5, 7,3,
                1,3, 1,5,
                2,3, 2,6,
                4,5, 4,6
            };

            for (int k = 0; k < e.Length; k += 2)
            {
                if (!Core.Projection.ProjectToScreenVP(c[e[k]], vp, sz, out var s0)) continue;
                if (!Core.Projection.ProjectToScreenVP(c[e[k + 1]], vp, sz, out var s1)) continue;
                ctx.DrawLine(pen, s0, s1);
            }
        }
    }
}
