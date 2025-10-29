#nullable enable
using System;
using Avalonia;
using Avalonia.Media;
using SN = System.Numerics;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>
    /// Terrain editor gizmos (draw-only). Keep this free of editor state/UI; pass data in.
    /// Mirrors the pattern used by ColliderGizmos.
    /// </summary>
    public static class TerrainGizmos
    {
        /// <summary>
        /// Draws a simple, lightweight outline for a Terrain (top-down XZ footprint) and
        /// a faint inner guide. Coloring changes if selected.
        /// </summary>
        /// <param name="ctx">Avalonia drawing ctx</param>
        /// <param name="viewProj">View * Projection matrix</param>
        /// <param name="sz">Viewport size (pixels)</param>
        /// <param name="world">World matrix of the terrain GameObject</param>
        /// <param name="terrain">Terrain component</param>
        /// <param name="highlight">True if the owning GO is selected</param>
        public static void Draw(
            DrawingContext ctx,
            in SN.Matrix4x4 viewProj,
            Size sz,
            in SN.Matrix4x4 world,
            Terrain terrain,
            bool highlight)
        {
            if (terrain == null) return;

            // Visual style
            var mainColor = highlight ? Colors.Gold : Color.FromArgb(192, 72, 184, 72); // soft green when idle
            var guideColor = Color.FromArgb(96, mainColor.R, mainColor.G, mainColor.B);
            var mainPen = new Pen(new SolidColorBrush(mainColor), highlight ? 2 : 1);
            var guidePen = new Pen(new SolidColorBrush(guideColor), 1);

            // Local-space rectangle on XZ plane that matches the configured size.
            float hx = Math.Max(0.0001f, terrain.SizeX) * 0.5f;
            float hz = Math.Max(0.0001f, terrain.SizeZ) * 0.5f;

            // Corners in LOCAL space at y=0 (the mesh rises/falls; for a gizmo, the footprint is enough)
            var p0 = new SN.Vector3(-hx, 0, -hz);
            var p1 = new SN.Vector3(hx, 0, -hz);
            var p2 = new SN.Vector3(hx, 0, hz);
            var p3 = new SN.Vector3(-hx, 0, hz);

            // Transform to WORLD
            p0 = SN.Vector3.Transform(p0, world);
            p1 = SN.Vector3.Transform(p1, world);
            p2 = SN.Vector3.Transform(p2, world);
            p3 = SN.Vector3.Transform(p3, world);

            // Project & draw outer rectangle
            Point s0, s1, s2, s3;
            if (!Core.Projection.ProjectToScreenVP(p0, viewProj, sz, out s0)) return;
            if (!Core.Projection.ProjectToScreenVP(p1, viewProj, sz, out s1)) return;
            if (!Core.Projection.ProjectToScreenVP(p2, viewProj, sz, out s2)) return;
            if (!Core.Projection.ProjectToScreenVP(p3, viewProj, sz, out s3)) return;

            ctx.DrawLine(mainPen, s0, s1);
            ctx.DrawLine(mainPen, s1, s2);
            ctx.DrawLine(mainPen, s2, s3);
            ctx.DrawLine(mainPen, s3, s0);

            // A faint cross (guides) to hint terrain center/orientation
            var c = new SN.Vector3(0, 0, 0);
            c = SN.Vector3.Transform(c, world);
            Point sc, sx, szp;
            var xTip = SN.Vector3.Transform(new SN.Vector3(hx, 0, 0), world);
            var zTip = SN.Vector3.Transform(new SN.Vector3(0, 0, hz), world);
            if (Core.Projection.ProjectToScreenVP(c, viewProj, sz, out sc) &&
                Core.Projection.ProjectToScreenVP(xTip, viewProj, sz, out sx) &&
                Core.Projection.ProjectToScreenVP(zTip, viewProj, sz, out szp))
            {
                ctx.DrawLine(guidePen, sc, sx);
                ctx.DrawLine(guidePen, sc, szp);
            }
        }

        /// <summary>
        /// Optional helper to draw a circular "brush" at a world point with a given radius.
        /// You can call this from SceneView if/when you have a world hit position for the cursor.
        /// </summary>
        public static void DrawBrush(
    DrawingContext ctx,
    in SN.Matrix4x4 viewProj,
    Size sz,
    SN.Vector3 centerWorld,
    float radiusWorld,
    Color ringColor,
    int segments = 64)
        {
            if (radiusWorld <= 0 || segments < 8) return;

            var pen = new Pen(new SolidColorBrush(ringColor), 2);
            Point first = default, prev = default;
            bool ok = false;

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float ang = t * (float)(2 * Math.PI);
                float x = radiusWorld * (float)Math.Cos(ang);
                float z = radiusWorld * (float)Math.Sin(ang);
                var w = new SN.Vector3(centerWorld.X + x, centerWorld.Y, centerWorld.Z + z);

                if (!Core.Projection.ProjectToScreenVP(w, viewProj, sz, out var s))
                    continue;

                if (!ok)
                {
                    first = prev = s; // seed the strip
                    ok = true;
                }
                else
                {
                    ctx.DrawLine(pen, prev, s);
                    prev = s;
                }
            }

            // (optional) ensure closure if you change the loop to < segments
            // if (ok) ctx.DrawLine(pen, prev, first);
        }

        public static void DrawBrushWithFalloff(
    DrawingContext ctx,
    in SN.Matrix4x4 viewProj,
    Size sz,
    SN.Vector3 centerWorld,
    float radiusWorld,
    float falloff01,
    float strength01,
    Color outerColor,
    Color innerColor,
    int segments = 64)
        {
            if (radiusWorld <= 0) return;

            // outer ring = brush size
            DrawBrush(ctx, viewProj, sz, centerWorld, radiusWorld, outerColor, segments);

            // inner ring = start of soft area (falloff width)
            float innerR = radiusWorld * Math.Max(0f, 1f - falloff01);
            if (innerR > 1e-3f)
                DrawBrush(ctx, viewProj, sz, centerWorld, innerR, innerColor, segments);

            // optional: faint crosshair with opacity = strength
            if (Core.Projection.ProjectToScreenVP(centerWorld, viewProj, sz, out var sc))
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(
                    (byte)(40 + 180 * Math.Clamp(strength01, 0f, 1f)), 255, 255, 255)), 1);
                double r = 4 + 6 * strength01;
                ctx.DrawLine(pen, new Point(sc.X - r, sc.Y), new Point(sc.X + r, sc.Y));
                ctx.DrawLine(pen, new Point(sc.X, sc.Y - r), new Point(sc.X, sc.Y + r));
            }
        }


    }
}
