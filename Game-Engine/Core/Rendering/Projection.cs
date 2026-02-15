#nullable enable
using System;
using System.Runtime.CompilerServices;
using SN = System.Numerics;
using Avalonia;

namespace Game_Engine.Core
{
    public static class Projection
    {
        public const float NearEps = 0.001f;

        // --------------------------------------------------------------------
        // Fast path: all-float math; only cast to double at the very end
        // --------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryProjectViewPoint(
            SN.Vector3 pView, in SN.Matrix4x4 proj, Size size,
            out Point screen, out SN.Vector4 clip, out int outCode)
        {
            // clip = P * (x,y,z,1)
            clip = SN.Vector4.Transform(new SN.Vector4(pView, 1f), proj);
            if (clip.W < NearEps) { screen = default(Point); outCode = 0; return false; }

            float invW = 1f / clip.W;
            // NDC in floats
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            float ndcZ = clip.Z * invW;

            // screen (float math) then one cast to double per coord
            float w = (float)size.Width;
            float h = (float)size.Height;
            float sx = (ndcX * 0.5f + 0.5f) * w;
            float sy = (1f - (ndcY * 0.5f + 0.5f)) * h;
            screen = new Point((double)sx, (double)sy);

            outCode =
                (ndcX < -1f ? 1 : 0) | (ndcX > 1f ? 2 : 0) |
                (ndcY < -1f ? 4 : 0) | (ndcY > 1f ? 8 : 0) |
                (ndcZ < 0f ? 16 : 0) | (ndcZ > 1f ? 32 : 0);
            return true;
        }

        // View*point -> Project using P (kept for compatibility)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ProjectWorldToScreen(
            SN.Vector3 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz,
            out Point screen, out SN.Vector3 viewPos)
        {
            viewPos = SN.Vector3.Transform(world, view);
            return TryProjectViewPoint(viewPos, proj, sz, out screen, out _, out _);
        }

        // Best performance: pass precomputed viewProj (VP)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ProjectToScreenVP(
            SN.Vector3 pW, in SN.Matrix4x4 vp, Size sz, out Point p)
        {
            var clip = SN.Vector4.Transform(new SN.Vector4(pW, 1f), vp);
            if (clip.W <= 0f) { p = default(Point); return false; }
            float invW = 1f / clip.W;

            float w = (float)sz.Width;
            float h = (float)sz.Height;

            float sx = ((clip.X * invW) * 0.5f + 0.5f) * w;
            float sy = (1f - ((clip.Y * invW) * 0.5f + 0.5f)) * h;
            p = new Point((double)sx, (double)sy);
            return true;
        }

        // --------------------------------------------------------------------
        // Clipping helpers
        // --------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ClipToNear(ref SN.Vector4 a, ref SN.Vector4 b)
        {
            bool ab = a.W < NearEps, bb = b.W < NearEps;
            if (ab && bb) return false;
            if (ab || bb)
            {
                var d = b - a;
                float t = (NearEps - a.W) / d.W;
                var p = a + t * d;
                if (ab) a = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
                else b = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int OutCode(in SN.Vector4 n)
        {
            return
                (n.X < -1f ? 1 : 0) | (n.X > 1f ? 2 : 0) |
                (n.Y < -1f ? 4 : 0) | (n.Y > 1f ? 8 : 0) |
                (n.Z < 0f ? 16 : 0) | (n.Z > 1f ? 32 : 0);
        }

        public static bool TryProjectSegment(
            SN.Vector3 A, SN.Vector3 B, in SN.Matrix4x4 vp, Size size,
            out Point p0, out Point p1)
        {
            var a = SN.Vector4.Transform(new SN.Vector4(A, 1f), vp);
            var b = SN.Vector4.Transform(new SN.Vector4(B, 1f), vp);
            if (!ClipToNear(ref a, ref b)) { p0 = default(Point); p1 = default(Point); return false; }

            float invWa = 1f / a.W;
            float invWb = 1f / b.W;
            var na = new SN.Vector4(a.X * invWa, a.Y * invWa, a.Z * invWa, 1f);
            var nb = new SN.Vector4(b.X * invWb, b.Y * invWb, b.Z * invWb, 1f);

            if ((OutCode(na) & OutCode(nb)) != 0) { p0 = default(Point); p1 = default(Point); return false; }

            float w = (float)size.Width;
            float h = (float)size.Height;

            float x0 = (na.X * 0.5f + 0.5f) * w;
            float y0 = (1f - (na.Y * 0.5f + 0.5f)) * h;
            float x1 = (nb.X * 0.5f + 0.5f) * w;
            float y1 = (1f - (nb.Y * 0.5f + 0.5f)) * h;

            p0 = new Point((double)x0, (double)y0);
            p1 = new Point((double)x1, (double)y1);
            return true;
        }

        /// <summary>
        /// Heuristic: screen-space radius in pixels for a local-space sphere (used by LOD).
        /// </summary>
        public static float EstimateProjectedRadiusPx(
            in SN.Matrix4x4 world, float radiusLocal,
            in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
        {
            // centerW and "edge" displaced along local +X transformed by world.
            var centerW = SN.Vector3.Transform(SN.Vector3.Zero, world);

            // world basis X (already scaled by world)
            var basisX = new SN.Vector3(world.M11, world.M12, world.M13);

            // NO normalize/sqrt: edgeW = center + basisX * radiusLocal
            var edgeW = centerW + basisX * radiusLocal;

            Point sc, se; SN.Vector3 _;
            if (!ProjectWorldToScreen(centerW, view, proj, sz, out sc, out _) ||
                !ProjectWorldToScreen(edgeW, view, proj, sz, out se, out _))
                return 32f;

            double dx = se.X - sc.X;
            double dy = se.Y - sc.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
