#nullable enable
using System;
using SN = System.Numerics;
using Avalonia.Media;

namespace Game_Engine.Core;

public static class Grid
{
    public static void OverlayInfiniteGrid(in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                         uint[] color, float[] zbuf, int W, int H,
                         float step = 1f, int majorEvery = 5)
    {
        // colors
        uint minor = ColorUtil.PackBGRA(Color.FromRgb(0x30, 0x30, 0x30));
        uint major = ColorUtil.PackBGRA(Color.FromRgb(0x48, 0x48, 0x48));
        uint axis = ColorUtil.PackBGRA(Color.FromRgb(0x60, 0x60, 0x60));

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        // camera world position (for distance fade)
        SN.Matrix4x4.Invert(view, out var invView);
        var cam = SN.Vector3.Transform(SN.Vector3.Zero, invView);

        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float ny = 1f - ((y + 0.5f) / H) * 2f;         // [-1..+1]

            for (int x = 0; x < W; x++)
            {
                float nx = ((x + 0.5f) / W) * 2f - 1f;     // [-1..+1]

                // ray in WORLD space
                var n4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0f, 1f), invVP);
                var f4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1f, 1f), invVP);
                var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
                var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
                var dir = SN.Vector3.Normalize(f3 - n3);

                // intersect y=0 ground plane in front of the near point
                const float EPS = 1e-6f;
                if (MathF.Abs(dir.Y) < EPS) continue;
                float t = -n3.Y / dir.Y;
                if (t <= 0f) continue;

                var p = n3 + dir * t; // world hit

                // project for proper z
                var clip = SN.Vector4.Transform(new SN.Vector4(p, 1f), vp);
                if (clip.W <= 0f) continue;
                float z = (clip.Z / clip.W);
                int idx = row + x;
                if (z >= zbuf[idx]) continue; // something nearer already there

                // grid shading (thin lines that fade with distance)
                float gx = p.X / step, gz = p.Z / step;
                float wx = MathF.Abs(gx - MathF.Round(gx));
                float wz = MathF.Abs(gz - MathF.Round(gz));
                float distToLine = MathF.Min(wx, wz);        // 0 at line, ~0.5 mid-cell

                // line width in "cell" units (slightly widens up close)
                float w = 0.015f + 0.0025f * MathF.Min(40f, t);
                float alpha = Math.Clamp((w - distToLine) / w, 0f, 1f);

                // distance fade so it doesn’t clutter the horizon
                float d = SN.Vector3.Distance(cam, p);
                float fade = 1f / (1f + 0.12f * d);
                alpha *= fade;

                if (alpha <= 0f) continue;

                // choose color: axis (x/z == 0), major every N, else minor
                int ix = (int)MathF.Round(gx);
                int iz = (int)MathF.Round(gz);
                bool onAxis = (ix == 0) || (iz == 0);
                bool onMajor = (ix % majorEvery == 0) || (iz % majorEvery == 0);
                uint col = onAxis ? axis : (onMajor ? major : minor);

                // blend over sky; write z so meshes in front occlude it
                color[idx] = ColorUtil.LerpBGRA(color[idx], col, alpha);
                zbuf[idx] = z;
            }
        }
    }
}
