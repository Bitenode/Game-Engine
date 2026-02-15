#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    /// <summary>
    /// Terrain editor gizmos — collects world-space line segments for GL rendering.
    /// All methods append vertex data (x,y,z pairs for GL_LINES) to a List&lt;float&gt;.
    /// </summary>
    public static class TerrainGizmos
    {
        /// <summary>
        /// Collect a circle ring on the XZ plane at a given world center and radius.
        /// </summary>
        public static void CollectCircle(
            List<float> verts,
            SN.Vector3 centerWorld,
            float radiusWorld,
            int segments = 64)
        {
            if (radiusWorld <= 0 || segments < 8) return;

            SN.Vector3 prev = default;
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float ang = t * MathF.Tau;
                float x = radiusWorld * MathF.Cos(ang);
                float z = radiusWorld * MathF.Sin(ang);
                var pt = new SN.Vector3(centerWorld.X + x, centerWorld.Y, centerWorld.Z + z);

                if (i > 0)
                    Line(verts, prev, pt);

                prev = pt;
            }
        }

        /// <summary>
        /// Collect a small crosshair at the center of the brush.
        /// </summary>
        public static void CollectCrosshair(
            List<float> verts,
            SN.Vector3 centerWorld,
            float armLength)
        {
            if (armLength <= 0) return;

            // X axis arms
            Line(verts,
                new SN.Vector3(centerWorld.X - armLength, centerWorld.Y, centerWorld.Z),
                new SN.Vector3(centerWorld.X + armLength, centerWorld.Y, centerWorld.Z));
            // Z axis arms
            Line(verts,
                new SN.Vector3(centerWorld.X, centerWorld.Y, centerWorld.Z - armLength),
                new SN.Vector3(centerWorld.X, centerWorld.Y, centerWorld.Z + armLength));
        }

        /// <summary>
        /// Collect the full brush gizmo: outer ring, inner (falloff) ring, and center crosshair.
        /// </summary>
        public static void CollectBrushWithFalloff(
            List<float> outerVerts,
            List<float> innerVerts,
            List<float> crosshairVerts,
            SN.Vector3 centerWorld,
            float radiusWorld,
            float falloff01,
            int segments = 64)
        {
            if (radiusWorld <= 0) return;

            // Outer ring = full brush size
            CollectCircle(outerVerts, centerWorld, radiusWorld, segments);

            // Inner ring = start of falloff area
            float innerR = radiusWorld * Math.Max(0f, 1f - falloff01);
            if (innerR > 1e-3f)
                CollectCircle(innerVerts, centerWorld, innerR, segments);

            // Crosshair at center (small, fixed world-space size relative to brush)
            float arm = Math.Max(0.15f, radiusWorld * 0.06f);
            CollectCrosshair(crosshairVerts, centerWorld, arm);
        }

        static void Line(List<float> verts, SN.Vector3 a, SN.Vector3 b)
        {
            verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
            verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
        }
    }
}
