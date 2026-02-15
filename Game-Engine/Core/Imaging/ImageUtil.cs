#nullable enable
namespace Game_Engine.Core;

public static class ImageUtil
{
    public static void Downsample2x(uint[] src, int srcW, int srcH, uint[] dst, int dstW, int dstH)
    {
        for (int y = 0; y < dstH; y++)
        {
            int sy = y * 2;
            int row0 = sy * srcW;
            int row1 = (sy + 1) * srcW;
            int di = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                int sx = x * 2;

                uint p00 = src[row0 + sx];
                uint p01 = src[row0 + sx + 1];
                uint p10 = src[row1 + sx];
                uint p11 = src[row1 + sx + 1];

                // Average BGRA (premul-safe if A is 255, which we use)
                int b = ((int)(p00 & 0xFF) + (int)(p01 & 0xFF) + (int)(p10 & 0xFF) + (int)(p11 & 0xFF)) >> 2;
                int g = (((int)(p00 >> 8) & 0xFF) + ((int)(p01 >> 8) & 0xFF) + ((int)(p10 >> 8) & 0xFF) + ((int)(p11 >> 8) & 0xFF)) >> 2;
                int r = (((int)(p00 >> 16) & 0xFF) + ((int)(p01 >> 16) & 0xFF) + ((int)(p10 >> 16) & 0xFF) + ((int)(p11 >> 16) & 0xFF)) >> 2;
                int a = (((int)(p00 >> 24) & 0xFF) + ((int)(p01 >> 24) & 0xFF) + ((int)(p10 >> 24) & 0xFF) + ((int)(p11 >> 24) & 0xFF)) >> 2;

                dst[di + x] = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            }
        }
    }

    /// Copy a small RGBA buffer into the big framebuffer at (dx,dy)
    public static void Blit(uint[] src, int sw, int sh, uint[] dst, int dw, int dh, int dx, int dy)
    {
        for (int y = 0; y < sh; y++)
            Array.Copy(src, y * sw, dst, (dy + y) * dw + dx, sw);
    }
}
