#nullable enable
using Avalonia.Media;

namespace Game_Engine.Core;

public static class TextureSampling
{
    // Bilinear sample with REPEAT addressing (premul-aware).
    public static Color SamplePMRepeat(Texture2D tex, float u, float v)
    {
        if (tex.Width <= 0 || tex.Height <= 0)
            return Color.FromArgb(255, 255, 255, 255);

        // Wrap into [0,1)
        u = u - System.MathF.Floor(u);
        v = v - System.MathF.Floor(v);

        // Pixel space, -0.5 so that u==0 samples centered on texel 0 and can lerp across seam.
        float px = u * tex.Width - 0.5f;
        float py = v * tex.Height - 0.5f;

        int x0 = FloorMod((int)System.MathF.Floor(px), tex.Width);
        int y0 = FloorMod((int)System.MathF.Floor(py), tex.Height);
        int x1 = (x0 + 1) % tex.Width;
        int y1 = (y0 + 1) % tex.Height;

        float tx = px - System.MathF.Floor(px);
        float ty = py - System.MathF.Floor(py);

        static void Premul(byte[] d, int i, out float r, out float g, out float b, out float a)
        {
            a = d[i + 3] / 255f;
            float R = d[i + 0] / 255f, G = d[i + 1] / 255f, B = d[i + 2] / 255f;
            r = R * a; g = G * a; b = B * a;
        }

        int i00 = (y0 * tex.Width + x0) * 4;
        int i01 = (y0 * tex.Width + x1) * 4;
        int i10 = (y1 * tex.Width + x0) * 4;
        int i11 = (y1 * tex.Width + x1) * 4;

        Premul(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
        Premul(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
        Premul(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
        Premul(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);

        float r0 = r00 * (1 - tx) + r01 * tx;
        float g0 = g00 * (1 - tx) + g01 * tx;
        float b0 = b00 * (1 - tx) + b01 * tx;
        float a0 = a00 * (1 - tx) + a01 * tx;

        float r1 = r10 * (1 - tx) + r11 * tx;
        float g1 = g10 * (1 - tx) + g11 * tx;
        float b1 = b10 * (1 - tx) + b11 * tx;
        float a1 = a10 * (1 - tx) + a11 * tx;

        float r = r0 * (1 - ty) + r1 * ty;
        float g = g0 * (1 - ty) + g1 * ty;
        float b = b0 * (1 - ty) + b1 * ty;
        float a = a0 * (1 - ty) + a1 * ty;

        if (a > 1e-6f) { r /= a; g /= a; b /= a; } else { r = g = b = 0f; }

        return Color.FromArgb(
            (byte)System.Math.Clamp((int)(a * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
    }

    // Bilinear sample with CLAMP addressing (premul-aware).
    public static Color SamplePMClamped(Texture2D tex, float u, float v)
    {
        if (tex.Width <= 0 || tex.Height <= 0) return Color.FromArgb(255, 255, 255, 255);
        v = 1f - v; // flip V

        float maxX = tex.Width - 1, maxY = tex.Height - 1;
        float epsU = tex.Width > 1 ? (0.5f / maxX) : 0f, epsV = tex.Height > 1 ? (0.5f / maxY) : 0f;
        u = System.Math.Clamp(u, epsU, 1f - epsU);
        v = System.Math.Clamp(v, epsV, 1f - epsV);

        float px = u * maxX, py = v * maxY;
        int x0 = (int)System.MathF.Floor(px), y0 = (int)System.MathF.Floor(py);
        int x1 = System.Math.Min(x0 + 1, tex.Width - 1);
        int y1 = System.Math.Min(y0 + 1, tex.Height - 1);
        float tx = px - x0, ty = py - y0;

        static void Premul(byte[] d, int i, out float r, out float g, out float b, out float a)
        { a = d[i + 3] / 255f; float R = d[i + 0] / 255f, G = d[i + 1] / 255f, B = d[i + 2] / 255f; r = R * a; g = G * a; b = B * a; }

        int i00 = (y0 * tex.Width + x0) * 4, i01 = (y0 * tex.Width + x1) * 4;
        int i10 = (y1 * tex.Width + x0) * 4, i11 = (y1 * tex.Width + x1) * 4;
        Premul(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
        Premul(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
        Premul(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
        Premul(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);

        float r0 = r00 * (1 - tx) + r01 * tx, g0 = g00 * (1 - tx) + g01 * tx, b0 = b00 * (1 - tx) + b01 * tx, a0 = a00 * (1 - tx) + a01 * tx;
        float r1 = r10 * (1 - tx) + r11 * tx, g1 = g10 * (1 - tx) + g11 * tx, b1 = b10 * (1 - tx) + b11 * tx, a1 = a10 * (1 - tx) + a11 * tx;
        float r = r0 * (1 - ty) + r1 * ty, g = g0 * (1 - ty) + g1 * ty, b = b0 * (1 - ty) + b1 * ty, a = a0 * (1 - ty) + a1 * ty;

        if (a > 1e-6f) { r /= a; g /= a; b /= a; } else { r = g = b = 0f; }
        return Color.FromArgb(
            (byte)System.Math.Clamp((int)(a * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
            (byte)System.Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
    }

    static int FloorMod(int x, int m) => (x % m + m) % m;
}
