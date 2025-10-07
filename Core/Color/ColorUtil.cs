#nullable enable
using Avalonia.Media;

namespace Game_Engine.Core;

public static class ColorUtil
{
    public static uint PackBGRA(Color c) => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));

    // Unpack BGRA uint -> Color
    public static Color UnpackBGRA(uint p)
    {
        byte b = (byte)(p & 0xFF);
        byte g = (byte)((p >> 8) & 0xFF);
        byte r = (byte)((p >> 16) & 0xFF);
        byte a = (byte)((p >> 24) & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    public static Color ShadeColor(Color c, float s)
    {
        s = System.Math.Clamp(s, 0f, 1f);
        byte r = (byte)System.Math.Clamp(c.R * s, 0, 255);
        byte g = (byte)System.Math.Clamp(c.G * s, 0, 255);
        byte b = (byte)System.Math.Clamp(c.B * s, 0, 255);
        return Color.FromArgb(c.A, r, g, b);
    }

    public static Color MulColor(Color a, Color b)
    {
        byte r = (byte)((a.R * b.R) / 255);
        byte g = (byte)((a.G * b.G) / 255);
        byte bb = (byte)((a.B * b.B) / 255);
        return Color.FromArgb(255, r, g, bb);
    }

    // BGRA row lerp (for sky gradient)
    public static uint LerpBGRA(uint a, uint b, float t)
    {
        t = System.Math.Clamp(t, 0f, 1f);
        int ab = (int)(a & 0xFF), ag = (int)((a >> 8) & 0xFF), ar = (int)((a >> 16) & 0xFF), aa = (int)((a >> 24) & 0xFF);
        int bb = (int)(b & 0xFF), bg = (int)((b >> 8) & 0xFF), br = (int)((b >> 16) & 0xFF), ba = (int)((b >> 24) & 0xFF);
        int rb = (int)(ab + (bb - ab) * t);
        int rg = (int)(ag + (bg - ag) * t);
        int rr = (int)(ar + (br - ar) * t);
        int ra = (int)(aa + (ba - aa) * t);
        return (uint)(rb | (rg << 8) | (rr << 16) | (ra << 24));
    }

    /// “over” blend of a premultiplied source over an opaque BGRA dst (A=255).
    public static uint BlendOver(uint dstBGRA, Color src, float a /*0..1*/)
    {
        if (a <= 0f) return dstBGRA;
        if (a >= 1f) return PackBGRA(src);

        var dst = UnpackBGRA(dstBGRA);
        byte r = (byte)(src.R * a + dst.R * (1f - a));
        byte g = (byte)(src.G * a + dst.G * (1f - a));
        byte b = (byte)(src.B * a + dst.B * (1f - a));
        return PackBGRA(Color.FromRgb(r, g, b)); // keep A=255 in our buffers
    }

    public static Color AddColor(Color a, Color b) => Color.FromRgb(
            (byte)Math.Min(255, a.R + b.R),
            (byte)Math.Min(255, a.G + b.G),
            (byte)Math.Min(255, a.B + b.B));

    public static float Luma(Color c) => (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;

    public static Color AlphaOver(Color under, Color over)
    {
        float a = over.A / 255f;
        return Color.FromRgb(
            (byte)(over.R * a + under.R * (1f - a)),
            (byte)(over.G * a + under.G * (1f - a)),
            (byte)(over.B * a + under.B * (1f - a)));
    }
}
