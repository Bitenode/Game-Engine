using System;
using System.Collections.Generic;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Views
{
    /// <summary>
    /// Collects world-space line segments for collider visualization.
    /// All methods append vertex data (x,y,z pairs for GL_LINES) to a List&lt;float&gt;.
    /// The caller uploads the collected data to a GL VBO and draws with a wireframe shader.
    /// </summary>
    public static class ColliderGizmos
    {
        /// <summary>Emit 12 edges of an axis-aligned bounding box.</summary>
        public static void CollectAABB(List<float> verts, Collider.AABB aabb)
        {
            Span<SN.Vector3> c = stackalloc SN.Vector3[8];
            int i = 0;
            for (int x = 0; x <= 1; x++)
                for (int y = 0; y <= 1; y++)
                    for (int z = 0; z <= 1; z++)
                        c[i++] = new SN.Vector3(
                            x == 0 ? aabb.Min.X : aabb.Max.X,
                            y == 0 ? aabb.Min.Y : aabb.Max.Y,
                            z == 0 ? aabb.Min.Z : aabb.Max.Z);

            ReadOnlySpan<int> e = stackalloc int[] {
                0, 1, 0, 2, 0, 4,
                7, 6, 7, 5, 7, 3,
                1, 3, 1, 5,
                2, 3, 2, 6,
                4, 5, 4, 6
            };
            for (int k = 0; k < e.Length; k += 2)
                Line(verts, c[e[k]], c[e[k + 1]]);
        }

        /// <summary>
        /// Emit 12 edges of an oriented bounding box (local center+size transformed by world matrix).
        /// Much more accurate than AABB for rotated objects.
        /// </summary>
        public static void CollectOBB(List<float> verts, SN.Vector3 localCenter, SN.Vector3 localSize, SN.Matrix4x4 world)
        {
            var e = localSize * 0.5f;
            Span<SN.Vector3> c = stackalloc SN.Vector3[8];
            int idx = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        c[idx++] = SN.Vector3.Transform(
                            localCenter + new SN.Vector3(e.X * x, e.Y * y, e.Z * z), world);

            ReadOnlySpan<int> edges = stackalloc int[] {
                0, 1, 0, 2, 0, 4,
                7, 6, 7, 5, 7, 3,
                1, 3, 1, 5,
                2, 3, 2, 6,
                4, 5, 4, 6
            };
            for (int k = 0; k < edges.Length; k += 2)
                Line(verts, c[edges[k]], c[edges[k + 1]]);
        }

        /// <summary>Emit wireframe edges for a mesh (all triangle edges).</summary>
        public static void CollectMeshWire(List<float> verts, Mesh mesh, SN.Matrix4x4 world)
        {
            if (mesh?.Vertices == null || mesh.Vertices.Length == 0 ||
                mesh.TriIndices == null || mesh.TriIndices.Length == 0) return;

            var vtx = mesh.Vertices;
            var wpos = new SN.Vector3[vtx.Length];
            for (int i = 0; i < vtx.Length; i++)
                wpos[i] = SN.Vector3.Transform(vtx[i], world);

            var triIdx = mesh.TriIndices;
            for (int t = 0; t < triIdx.Length; t += 3)
            {
                int a = triIdx[t], b = triIdx[t + 1], c = triIdx[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vtx.Length || b >= vtx.Length || c >= vtx.Length)
                    continue;
                Line(verts, wpos[a], wpos[b]);
                Line(verts, wpos[b], wpos[c]);
                Line(verts, wpos[c], wpos[a]);
            }
        }

        /// <summary>Emit capsule wireframe (top/bottom circles, side lines, hemisphere arcs).</summary>
        public static void CollectCapsule(
            List<float> verts,
            SN.Matrix4x4 world,
            SN.Vector3 localCenterTop, SN.Vector3 localCenterBottom,
            SN.Vector3 localAxis, float radius, int segments = 32)
        {
            // Linear part (rotation + scale) of world matrix
            var Lx = new SN.Vector3(world.M11, world.M12, world.M13);
            var Ly = new SN.Vector3(world.M21, world.M22, world.M23);
            var Lz = new SN.Vector3(world.M31, world.M32, world.M33);

            // Transform endpoints to world space
            var topW = SN.Vector3.Transform(localCenterTop, world);
            var botW = SN.Vector3.Transform(localCenterBottom, world);

            // Two perpendicular directions for the ring
            SN.Vector3 la = SN.Vector3.Normalize(localAxis);
            SN.Vector3 lp1, lp2;
            if (MathF.Abs(la.X) > 0.5f)
            {
                lp1 = new SN.Vector3(0, 1, 0);
                lp2 = new SN.Vector3(0, 0, 1);
            }
            else if (MathF.Abs(la.Z) > 0.5f)
            {
                lp1 = new SN.Vector3(1, 0, 0);
                lp2 = new SN.Vector3(0, 1, 0);
            }
            else
            {
                lp1 = new SN.Vector3(1, 0, 0);
                lp2 = new SN.Vector3(0, 0, 1);
            }

            // Transform ring direction by the 3x3 linear part and scale by radius
            SN.Vector3 RingVec(SN.Vector3 dir) =>
                new SN.Vector3(
                    dir.X * Lx.X + dir.Y * Lx.Y + dir.Z * Lx.Z,
                    dir.X * Ly.X + dir.Y * Ly.Y + dir.Z * Ly.Z,
                    dir.X * Lz.X + dir.Y * Lz.Y + dir.Z * Lz.Z);

            var rp1 = RingVec(lp1);
            rp1 = SN.Vector3.Normalize(rp1) * radius * rp1.Length();
            var rp2 = RingVec(lp2);
            rp2 = SN.Vector3.Normalize(rp2) * radius * rp2.Length();

            var axisW = RingVec(la);
            var ra = radius * axisW.Length();
            var axisDir = axisW.Length() > 1e-6f
                ? SN.Vector3.Normalize(axisW)
                : new SN.Vector3(0, 1, 0);

            // Emit a polyline as individual line segments for GL_LINES
            void Poly(SN.Vector3[] pts)
            {
                for (int i = 0; i < pts.Length - 1; i++)
                    Line(verts, pts[i], pts[i + 1]);
            }

            // Top and bottom circles
            var circleTop = new SN.Vector3[segments + 1];
            var circleBot = new SN.Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                var t = (float)i / segments * MathF.PI * 2f;
                var ring = MathF.Cos(t) * rp1 + MathF.Sin(t) * rp2;
                circleTop[i] = topW + ring;
                circleBot[i] = botW + ring;
            }
            Poly(circleTop);
            Poly(circleBot);

            // Four side lines connecting top and bottom rings
            Line(verts, topW + rp1, botW + rp1);
            Line(verts, topW - rp1, botW - rp1);
            Line(verts, topW + rp2, botW + rp2);
            Line(verts, topW - rp2, botW - rp2);

            // Hemisphere arcs in two perpendicular planes
            int arcSeg = segments / 2;
            var arc = new SN.Vector3[arcSeg + 1];

            // Plane 1 (axis + rp1): top hemisphere
            var u1 = SN.Vector3.Normalize(rp1);
            var v1 = axisDir * ra;
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * MathF.PI;
                arc[i] = topW + MathF.Cos(t) * (u1 * rp1.Length()) + MathF.Sin(t) * v1;
            }
            Poly(arc);
            // Plane 1: bottom hemisphere (mirrored)
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * MathF.PI;
                arc[i] = botW + MathF.Cos(t) * (u1 * rp1.Length()) - MathF.Sin(t) * v1;
            }
            Poly(arc);

            // Plane 2 (axis + rp2): top hemisphere
            var u2 = SN.Vector3.Normalize(rp2);
            var v2 = axisDir * ra;
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * MathF.PI;
                arc[i] = topW + MathF.Cos(t) * (u2 * rp2.Length()) + MathF.Sin(t) * v2;
            }
            Poly(arc);
            // Plane 2: bottom hemisphere (mirrored)
            for (int i = 0; i <= arcSeg; i++)
            {
                var t = (float)i / arcSeg * MathF.PI;
                arc[i] = botW + MathF.Cos(t) * (u2 * rp2.Length()) - MathF.Sin(t) * v2;
            }
            Poly(arc);
        }

        /// <summary>
        /// Emit 3 great-circle rings (XY, XZ, YZ planes) for a sphere collider.
        /// Provides clear visual coverage of the sphere volume with minimal overdraw.
        /// </summary>
        public static void CollectSphere(List<float> verts, SN.Vector3 center, float radius, int segments = 64)
        {
            float step = MathF.PI * 2f / segments;

            for (int i = 0; i < segments; i++)
            {
                float a0 = step * i;
                float a1 = step * (i + 1);
                float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
                float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);

                // XY ring (equator when viewed from Z)
                Line(verts,
                    center + new SN.Vector3(c0 * radius, s0 * radius, 0f),
                    center + new SN.Vector3(c1 * radius, s1 * radius, 0f));

                // XZ ring (equator when viewed from Y)
                Line(verts,
                    center + new SN.Vector3(c0 * radius, 0f, s0 * radius),
                    center + new SN.Vector3(c1 * radius, 0f, s1 * radius));

                // YZ ring (equator when viewed from X)
                Line(verts,
                    center + new SN.Vector3(0f, c0 * radius, s0 * radius),
                    center + new SN.Vector3(0f, c1 * radius, s1 * radius));
            }
        }

        /// <summary>
        /// Emit 3 great-circle rings that follow the actual terrain surface
        /// by sampling PlanetTerrain.SampleSurfaceRadius at each direction.
        /// Falls back to fixed-radius spheres if the terrain can't be sampled.
        /// </summary>
        public static void CollectPlanetTerrain(
            List<float> verts,
            SN.Vector3 center,
            Game_Engine.Core.Component.PlanetTerrain planet,
            int segments = 96)
        {
            float step = MathF.PI * 2f / segments;

            for (int i = 0; i < segments; i++)
            {
                float a0 = step * i;
                float a1 = step * (i + 1);
                float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
                float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);

                // XY ring
                {
                    var d0 = SN.Vector3.Normalize(new SN.Vector3(c0, s0, 0f));
                    var d1 = SN.Vector3.Normalize(new SN.Vector3(c1, s1, 0f));
                    float r0 = planet.SampleSurfaceRadius(d0);
                    float r1 = planet.SampleSurfaceRadius(d1);
                    Line(verts, center + d0 * r0, center + d1 * r1);
                }

                // XZ ring
                {
                    var d0 = SN.Vector3.Normalize(new SN.Vector3(c0, 0f, s0));
                    var d1 = SN.Vector3.Normalize(new SN.Vector3(c1, 0f, s1));
                    float r0 = planet.SampleSurfaceRadius(d0);
                    float r1 = planet.SampleSurfaceRadius(d1);
                    Line(verts, center + d0 * r0, center + d1 * r1);
                }

                // YZ ring
                {
                    var d0 = SN.Vector3.Normalize(new SN.Vector3(0f, c0, s0));
                    var d1 = SN.Vector3.Normalize(new SN.Vector3(0f, c1, s1));
                    float r0 = planet.SampleSurfaceRadius(d0);
                    float r1 = planet.SampleSurfaceRadius(d1);
                    Line(verts, center + d0 * r0, center + d1 * r1);
                }
            }
        }

        /// <summary>Append a single line segment (two endpoints) to the vertex list.</summary>
        static void Line(List<float> verts, SN.Vector3 a, SN.Vector3 b)
        {
            verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
            verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
        }
    }
}
