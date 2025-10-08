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

        public static void DrawMeshWire(
            DrawingContext ctx,
            SN.Matrix4x4 vp,
            Size sz,
            Game_Engine.Core.Mesh mesh,
            SN.Matrix4x4 world,
            Color color,
            float thickness = 1f)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Vertices.Length == 0 ||
                mesh.TriIndices == null || mesh.TriIndices.Length == 0) return;

            var pen = new Pen(new SolidColorBrush(color), thickness <= 0 ? 1 : thickness);

            // transform all positions once
            var vtx = mesh.Vertices;
            var wpos = new Point[vtx.Length];
            var vis = new bool[vtx.Length];

            for (int i = 0; i < vtx.Length; i++)
            {
                var p = SN.Vector3.Transform(vtx[i], world);
                if (Game_Engine.Core.Projection.ProjectToScreenVP(p, vp, sz, out var sp))
                {
                    vis[i] = true;
                    wpos[i] = sp;
                }
            }

            // draw triangle edges (skip if any endpoint didn’t project)
            var idx = mesh.TriIndices;
            for (int t = 0; t < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vtx.Length || b >= vtx.Length || c >= vtx.Length) continue;
                if (!(vis[a] && vis[b] && vis[c])) continue;

                ctx.DrawLine(pen, wpos[a], wpos[b]);
                ctx.DrawLine(pen, wpos[b], wpos[c]);
                ctx.DrawLine(pen, wpos[c], wpos[a]);
            }
        }

        public static void DrawCapsule(
            DrawingContext ctx,
            SN.Matrix4x4 world, SN.Matrix4x4 vp, Size sz,
            SN.Vector3 localCenterTop, SN.Vector3 localCenterBottom,
            SN.Vector3 localAxis, float radius,
            Color color, float thickness = 1f, int segments = 32)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness <= 0 ? 1 : thickness);

            // linear part (scale/rotation) of world matrix
            var Lx = new SN.Vector3(world.M11, world.M12, world.M13);
            var Ly = new SN.Vector3(world.M21, world.M22, world.M23);
            var Lz = new SN.Vector3(world.M31, world.M32, world.M33);

            // transform endpoints
            var topW = SN.Vector3.Transform(localCenterTop, world);
            var botW = SN.Vector3.Transform(localCenterBottom, world);

            // world axis & two perpendiculars (choose from local basis)
            SN.Vector3 la = SN.Vector3.Normalize(localAxis);
            SN.Vector3 lp1, lp2;
            if (Math.Abs(la.X) > 0.5f) { lp1 = new SN.Vector3(0, 1, 0); lp2 = new SN.Vector3(0, 0, 1); }
            else if (Math.Abs(la.Z) > 0.5f) { lp1 = new SN.Vector3(1, 0, 0); lp2 = new SN.Vector3(0, 1, 0); }
            else { lp1 = new SN.Vector3(1, 0, 0); lp2 = new SN.Vector3(0, 0, 1); }

            // transform the perpendicular ring directions by L and scale by radius
            SN.Vector3 RingVec(SN.Vector3 dir) =>
                new SN.Vector3(
                    dir.X * Lx.X + dir.Y * Lx.Y + dir.Z * Lx.Z,
                    dir.X * Ly.X + dir.Y * Ly.Y + dir.Z * Ly.Z,
                    dir.X * Lz.X + dir.Y * Lz.Y + dir.Z * Lz.Z);

            var rp1 = RingVec(lp1); rp1 = SN.Vector3.Normalize(rp1) * radius * rp1.Length();
            var rp2 = RingVec(lp2); rp2 = SN.Vector3.Normalize(rp2) * radius * rp2.Length();

            // axis extent for the hemispheres after scaling
            var axisW = RingVec(la);
            var ra = radius * axisW.Length();           // “radius” along the axis after non-uniform scale
            var axisDir = axisW.Length() > 1e-6f ? SN.Vector3.Normalize(axisW) : new SN.Vector3(0, 1, 0);

            // draw a polyline
            void Poly(ReadOnlySpan<SN.Vector3> pts)
            {
                Point? prev = null;
                for (int i = 0; i < pts.Length; i++)
                {
                    if (!Game_Engine.Core.Projection.ProjectToScreenVP(pts[i], vp, sz, out var s)) continue;
                    if (prev.HasValue) ctx.DrawLine(pen, prev.Value, s);
                    prev = s;
                }
            }

            // circles at top and bottom
            var circleTop = new SN.Vector3[segments + 1];
            var circleBot = new SN.Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                var t = (float)(i) / segments * (float)Math.PI * 2f;
                var ring = (float)Math.Cos(t) * rp1 + (float)Math.Sin(t) * rp2;
                circleTop[i] = topW + ring;
                circleBot[i] = botW + ring;
            }
            Poly(circleTop);
            Poly(circleBot);

            // cylinder “side lines” at 0/90/180/270 degrees
            SN.Vector3[] sideA = { topW + rp1, botW + rp1 };
            SN.Vector3[] sideB = { topW - rp1, botW - rp1 };
            SN.Vector3[] sideC = { topW + rp2, botW + rp2 };
            SN.Vector3[] sideD = { topW - rp2, botW - rp2 };
            Poly(sideA); Poly(sideB); Poly(sideC); Poly(sideD);

            // great-circle arcs (two planes: axis+rp1, axis+rp2), top and bottom hemispheres
            int arcSeg = segments / 2;
            var arc1 = new SN.Vector3[arcSeg + 1];
            var arc2 = new SN.Vector3[arcSeg + 1];

            // plane (axis, rp1): top hemisphere
            var u1 = SN.Vector3.Normalize(rp1);
            var v1 = axisDir * ra;
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * (float)Math.PI;
                arc1[i] = topW + (float)Math.Cos(t) * (u1 * rp1.Length()) + (float)Math.Sin(t) * v1;
            }
            Poly(arc1);
            // bottom hemisphere (mirror)
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * (float)Math.PI;
                arc2[i] = botW + (float)Math.Cos(t) * (u1 * rp1.Length()) - (float)Math.Sin(t) * v1;
            }
            Poly(arc2);

            // plane (axis, rp2): draw the other two arcs
            var u2 = SN.Vector3.Normalize(rp2);
            var v2 = axisDir * ra;
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * (float)Math.PI;
                arc1[i] = topW + (float)Math.Cos(t) * (u2 * rp2.Length()) + (float)Math.Sin(t) * v2;
            }
            Poly(arc1);
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * (float)Math.PI;
                arc2[i] = botW + (float)Math.Cos(t) * (u2 * rp2.Length()) - (float)Math.Sin(t) * v2;
            }
            Poly(arc2);
        }
    }
}
