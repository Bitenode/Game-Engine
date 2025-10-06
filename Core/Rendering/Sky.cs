#nullable enable
using System;
using Avalonia;
using Avalonia.Media;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class Sky
{
    /// Fill the framebuffer with a world-up gradient/optional lat-long sky (far-plane z).
    public static void FillWorldUp(
        uint[] color, float[] zbuf, int W, int H,
        in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
        Color topCol, Color botCol,
        SN.Vector3? sunDirWorld = null,
        Texture2D? skyTex = null,
        float skyTexBlend = 0f,
        float skyYawDegrees = 0f,
        float seamFeather = 0f,
        bool keyOutNearBlack = false,
        float keyLuma = 0.03f,
        float zWriteNdc = 1.0f)
    {
        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        static float Smooth01(float t) => t <= 0 ? 0 : (t >= 1 ? 1 : t * t * (3 - 2 * t));
        static Color LerpColor(Color a, Color b, float t)
        {
            t = Clamp01(t);
            byte r = (byte)(a.R + (b.R - a.R) * t);
            byte g = (byte)(a.G + (b.G - a.G) * t);
            byte bb = (byte)(a.B + (b.B - a.B) * t);
            byte aa = (byte)(a.A + (b.A - a.A) * t);
            return Color.FromArgb(aa, r, g, bb);
        }

        uint top = ColorUtil.PackBGRA(topCol);
        uint bot = ColorUtil.PackBGRA(botCol);

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        var worldUp = SN.Vector3.UnitY;

        // optional sun highlight
        SN.Vector3 sun = SN.Vector3.Zero;
        bool useSun = false;
        if (sunDirWorld.HasValue)
        {
            sun = SN.Vector3.Normalize(sunDirWorld.Value);
            useSun = sun.LengthSquared() > 0.5f;
        }

        bool useTex = skyTex != null && skyTexBlend > 0.0001f;

        float yawRad = skyYawDegrees * (MathF.PI / 180f);
        var yawM = SN.Matrix4x4.CreateFromAxisAngle(worldUp, yawRad);

        float poleEps = (useTex && skyTex!.Height > 1) ? (0.5f / skyTex.Height) : 0f;
        if (useTex && seamFeather <= 0f) seamFeather = MathF.Max(1f / (skyTex!.Width * 2f), 0.0015f);

        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float ny = 1f - ((y + 0.5f) / H) * 2f;   // [-1..+1]

            for (int x = 0; x < W; x++)
            {
                float nx = ((x + 0.5f) / W) * 2f - 1f; // [-1..+1]

                // World-ray
                var n4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0f, 1f), invVP);
                var f4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1f, 1f), invVP);
                var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
                var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
                var dir = SN.Vector3.Normalize(f3 - n3);
                if (yawRad != 0f) dir = SN.Vector3.Transform(dir, yawM);

                // base gradient (+ optional sun)
                float t = Clamp01(0.5f + 0.5f * SN.Vector3.Dot(dir, worldUp));
                if (useSun)
                {
                    float sunGlow = MathF.Pow(MathF.Max(0f, SN.Vector3.Dot(dir, sun)), 64f);
                    t = Clamp01(t + sunGlow * 0.08f);
                }
                uint pix = ColorUtil.LerpBGRA(bot, top, t);

                // optional lat-long texture overlay
                if (useTex)
                {
                    float u = 0.5f + MathF.Atan2(dir.X, -dir.Z) / (2f * MathF.PI);
                    u = u - MathF.Floor(u);
                    float v = 0.5f - MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) / MathF.PI;
                    v = Math.Clamp(v, poleEps, 1f - poleEps);

                    Color samp;
                    if (seamFeather > 0f)
                    {
                        float d = MathF.Min(u, 1f - u);
                        if (d < seamFeather)
                        {
                            float k = Smooth01(d / seamFeather);
                            float uOther = (u < 0.5f) ? (u + 1f) : (u - 1f);
                            var a = TextureSampling.SamplePMRepeat(skyTex!, u, v);
                            var b = TextureSampling.SamplePMRepeat(skyTex!, uOther, v);
                            samp = LerpColor(b, a, k);
                        }
                        else samp = TextureSampling.SamplePMRepeat(skyTex!, u, v);
                    }
                    else samp = TextureSampling.SamplePMRepeat(skyTex!, u, v);

                    if (keyOutNearBlack)
                    {
                        float luma = (0.2126f * samp.R + 0.7152f * samp.G + 0.0722f * samp.B) / 255f;
                        if (luma <= keyLuma) samp = Color.FromArgb(0, samp.R, samp.G, samp.B);
                    }

                    float w = skyTexBlend * (samp.A / 255f);
                    var sampRGB = Color.FromRgb(samp.R, samp.G, samp.B);
                    pix = ColorUtil.LerpBGRA(pix, ColorUtil.PackBGRA(sampRGB), w);
                }

                color[row + x] = pix;
                zbuf[row + x] = Math.Clamp(zWriteNdc, 0f, 1f);
            }
        }
    }
}
