using System;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace Game_Engine.Core
{
    public static class ColorUtil
    {
        // ---------- Packing ----------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackBGRA(Color c) => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color UnpackBGRA(uint p)
        {
            byte b = (byte)(p & 0xFF);
            byte g = (byte)((p >> 8) & 0xFF);
            byte r = (byte)((p >> 16) & 0xFF);
            byte a = (byte)((p >> 24) & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }

        // ---------- Convenience creators  ----------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color FromRGBA(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color FromRGBA(float r, float g, float b, float a = 1f)
        {
            // clamp to 0..1 then map to bytes
            if (r < 0f) r = 0f; else if (r > 1f) r = 1f;
            if (g < 0f) g = 0f; else if (g > 1f) g = 1f;
            if (b < 0f) b = 0f; else if (b > 1f) b = 1f;
            if (a < 0f) a = 0f; else if (a > 1f) a = 1f;
            return Color.FromArgb(
                (byte)(a * 255f + 0.5f),
                (byte)(r * 255f + 0.5f),
                (byte)(g * 255f + 0.5f),
                (byte)(b * 255f + 0.5f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color FromBGRA(byte b, byte g, byte r, byte a = 255) => Color.FromArgb(a, r, g, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        // ---------- Integer helpers ----------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Mul255Int(int x, int k)
        {
            int t = x * k + 128;        // exact, correctly rounded (x*k)/255
            return (t + (t >> 8)) >> 8;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] private static byte Mul255Byte(byte x, int k) => (byte)Mul255Int(x, k);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));

        // ---------- Fast shading / multiply ----------
        public static Color ShadeColor(Color c, float s)
        {
            int k = (int)(s <= 0f ? 0 : (s >= 1f ? 255 : (s * 255f + 0.5f)));
            byte r = Mul255Byte(c.R, k);
            byte g = Mul255Byte(c.G, k);
            byte b = Mul255Byte(c.B, k);
            return Color.FromArgb(c.A, r, g, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ShadeBGRA(uint bgra, byte k /*0..255*/)
        {
            int b = (int)(bgra & 0xFF);
            int g = (int)((bgra >> 8) & 0xFF);
            int r = (int)((bgra >> 16) & 0xFF);
            int a = (int)((bgra >> 24) & 0xFF);
            r = Mul255Int(r, k); g = Mul255Int(g, k); b = Mul255Int(b, k);
            return (uint)(b | (g << 8) | (r << 16) | (a << 24));
        }

        public static Color MulColor(Color a, Color b)
        {
            byte r = Mul255Byte(a.R, b.R);
            byte g = Mul255Byte(a.G, b.G);
            byte bb = Mul255Byte(a.B, b.B);
            return Color.FromArgb(255, r, g, bb);
        }

        // ---------- Lerp ----------
        public static uint LerpBGRA(uint a, uint b, float t)
        {
            int ti = (int)(t <= 0f ? 0 : (t >= 1f ? 255 : (t * 255f + 0.5f)));
            return LerpBGRA(a, b, (byte)ti);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint LerpBGRA(uint a, uint b, byte t /*0..255*/)
        {
            int inv = 255 - t;

            int ab = (int)(a & 0xFF), ag = (int)((a >> 8) & 0xFF), ar = (int)((a >> 16) & 0xFF), aa = (int)((a >> 24) & 0xFF);
            int bb = (int)(b & 0xFF), bg = (int)((b >> 8) & 0xFF), br = (int)((b >> 16) & 0xFF), ba = (int)((b >> 24) & 0xFF);

            int rb = Mul255Int(ab, inv) + Mul255Int(bb, t); if (rb > 255) rb = 255;
            int rg = Mul255Int(ag, inv) + Mul255Int(bg, t); if (rg > 255) rg = 255;
            int rr = Mul255Int(ar, inv) + Mul255Int(br, t); if (rr > 255) rr = 255;
            int ra = Mul255Int(aa, inv) + Mul255Int(ba, t); if (ra > 255) ra = 255;

            return (uint)(rb | (rg << 8) | (rr << 16) | (ra << 24));
        }

        // ---------- Blending ----------
        public static uint BlendOver(uint dstBGRA, Color src, float a /*0..1*/)
        {
            if (a <= 0f) return dstBGRA;
            if (a >= 1f) return PackBGRA(src);
            byte ai = (byte)(a * 255f + 0.5f);
            uint srcBGRA = PackBGRA(src);
            return BlendOverBGRA(dstBGRA, srcBGRA, ai);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint BlendOverBGRA(uint dstBGRA, uint srcBGRA, byte a /*0..255*/)
        {
            if (a == 0) return dstBGRA;
            if (a == 255) return srcBGRA;

            int inv = 255 - a;

            int db = (int)(dstBGRA & 0xFF);
            int dg = (int)((dstBGRA >> 8) & 0xFF);
            int dr = (int)((dstBGRA >> 16) & 0xFF);

            int sb = (int)(srcBGRA & 0xFF);
            int sg = (int)((srcBGRA >> 8) & 0xFF);
            int sr = (int)((srcBGRA >> 16) & 0xFF);

            int rb = Mul255Int(sb, a) + Mul255Int(db, inv); if (rb > 255) rb = 255;
            int rg = Mul255Int(sg, a) + Mul255Int(dg, inv); if (rg > 255) rg = 255;
            int rr = Mul255Int(sr, a) + Mul255Int(dr, inv); if (rr > 255) rr = 255;

            return (uint)(rb | (rg << 8) | (rr << 16) | (0xFF << 24));
        }

        // ---------- Misc ----------
        public static Color AddColor(Color a, Color b) => Color.FromRgb(
            ClampByte(a.R + b.R),
            ClampByte(a.G + b.G),
            ClampByte(a.B + b.B));

        public static float Luma(Color c) => (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;

        public static Color AlphaOver(Color under, Color over)
        {
            int a = over.A, inv = 255 - a;
            byte r = ClampByte(Mul255Int(over.R, a) + Mul255Int(under.R, inv));
            byte g = ClampByte(Mul255Int(over.G, a) + Mul255Int(under.G, inv));
            byte b = ClampByte(Mul255Int(over.B, a) + Mul255Int(under.B, inv));
            return Color.FromRgb(r, g, b);
        }
    }
}
