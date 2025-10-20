using ComputeSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Game_Engine.Core
{
    // Screen-space triangle used by the GPU pre-Z pass (z in [0,1])
    public readonly partial struct TriSS
    {
        public readonly float ax, ay, az, bx, by, bz, cx, cy, cz;
        public readonly float A01, B01, C01, A12, B12, C12, A20, B20, C20, invArea;
        public readonly int minX, minY, maxX, maxY;

        public TriSS(
            float ax, float ay, float az01,
            float bx, float by, float bz01,
            float cx, float cy, float cz01,
            int minX, int minY, int maxX, int maxY)
        {
            this.ax = ax; this.ay = ay; this.az = az01;
            this.bx = bx; this.by = by; this.bz = bz01;
            this.cx = cx; this.cy = cy; this.cz = cz01;

            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area == 0f) area = 1e-20f;
            invArea = 1f / area;

            A01 = ay - by; B01 = bx - ax; C01 = -(A01 * ax + B01 * ay);
            A12 = by - cy; B12 = cx - bx; C12 = -(A12 * bx + B12 * by);
            A20 = cy - ay; B20 = ax - cx; C20 = -(A20 * cx + B20 * cy);

            this.minX = minX; this.minY = minY; this.maxX = maxX; this.maxY = maxY;
        }
    }

    // Clears the packed 24-bit z buffer to "far"
    [AutoConstructor]
    [ThreadGroupSize(256, 1, 1)]
    internal readonly partial struct ClearKernel : IComputeShader
    {
        public readonly ReadWriteBuffer<uint> zb;
        public void Execute()
        {
            int i = ThreadIds.X;
            if ((uint)i < (uint)zb.Length)
                zb[i] = 0x00FFFFFFu; // 1.0 in 24-bit
        }
    }

    // Per-triangle, bbox raster; atomic min into packed z
    [AutoConstructor]
    [ThreadGroupSize(64, 1, 1)]
    internal readonly partial struct ZOnlyKernel : IComputeShader
    {
        public readonly ReadOnlyBuffer<TriSS> tris;
        public readonly ReadWriteBuffer<uint> zb;
        public readonly int W, H;

        public void Execute()
        {
            int i = ThreadIds.X;
            if ((uint)i >= (uint)tris.Length) return;

            var t = tris[i];
            int minX = Hlsl.Clamp(t.minX, 0, W - 1);
            int maxX = Hlsl.Clamp(t.maxX, 0, W - 1);
            int minY = Hlsl.Clamp(t.minY, 0, H - 1);
            int maxY = Hlsl.Clamp(t.maxY, 0, H - 1);
            if (minX > maxX || minY > maxY) return;

            const float EPS = 1e-3f;

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                float px = minX + 0.5f;

                float w0 = t.A12 * px + t.B12 * py + t.C12;
                float w1 = t.A20 * px + t.B20 * py + t.C20;
                float w2 = t.A01 * px + t.B01 * py + t.C01;

                int idx = y * W + minX;

                for (int x = minX; x <= maxX; x++, idx++, px += 1f, w0 += t.A12, w1 += t.A20, w2 += t.A01)
                {
                    bool insidePos = (w0 >= -EPS && w1 >= -EPS && w2 >= -EPS);
                    bool insideNeg = (w0 <= EPS && w1 <= EPS && w2 <= EPS);
                    if (!(insidePos || insideNeg)) continue;

                    float b0 = w0 * t.invArea;
                    float b1 = w1 * t.invArea;
                    float z01 = b0 * t.az + b1 * t.bz + (1f - b0 - b1) * t.cz;

                    uint dz = (uint)Hlsl.Clamp(z01 * 16777215.0f + 0.5f, 0.0f, 16777215.0f);
                    Hlsl.InterlockedMin(ref zb[idx], dz);
                }
            }
        }
    }
}

