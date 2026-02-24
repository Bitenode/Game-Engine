using System;
using System.Runtime.CompilerServices;

namespace Game_Engine.Core.Noise;

/// <summary>
/// Simplex noise implementation for 2D and 3D. Returns values in [-1, 1].
/// Based on Stefan Gustavson's simplex noise reference implementation.
/// </summary>
public sealed class SimplexNoise
{
    readonly byte[] _perm = new byte[512];
    readonly byte[] _perm12 = new byte[512];

    static readonly int[][] Grad3 =
    {
        new[]{1,1,0}, new[]{-1,1,0}, new[]{1,-1,0}, new[]{-1,-1,0},
        new[]{1,0,1}, new[]{-1,0,1}, new[]{1,0,-1}, new[]{-1,0,-1},
        new[]{0,1,1}, new[]{0,-1,1}, new[]{0,1,-1}, new[]{0,-1,-1},
    };

    public SimplexNoise(int seed = 0)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;

        var rng = new Random(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        for (int i = 0; i < 512; i++)
        {
            _perm[i] = p[i & 255];
            _perm12[i] = (byte)(_perm[i] % 12);
        }
    }

    const float F2 = 0.3660254037844386f;  // (sqrt(3)-1)/2
    const float G2 = 0.21132486540518713f; // (3-sqrt(3))/6
    const float F3 = 1f / 3f;
    const float G3 = 1f / 6f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Dot2(int[] g, float x, float y) => g[0] * x + g[1] * y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Dot3(int[] g, float x, float y, float z) => g[0] * x + g[1] * y + g[2] * z;

    public float Noise2D(float x, float y)
    {
        float s = (x + y) * F2;
        int i = FastFloor(x + s);
        int j = FastFloor(y + s);

        float t = (i + j) * G2;
        float X0 = i - t;
        float Y0 = j - t;
        float x0 = x - X0;
        float y0 = y - Y0;

        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; }
        else { i1 = 0; j1 = 1; }

        float x1 = x0 - i1 + G2;
        float y1 = y0 - j1 + G2;
        float x2 = x0 - 1f + 2f * G2;
        float y2 = y0 - 1f + 2f * G2;

        int ii = i & 255;
        int jj = j & 255;
        int gi0 = _perm12[ii + _perm[jj]];
        int gi1 = _perm12[ii + i1 + _perm[jj + j1]];
        int gi2 = _perm12[ii + 1 + _perm[jj + 1]];

        float n0, n1, n2;

        float t0 = 0.5f - x0 * x0 - y0 * y0;
        if (t0 < 0) n0 = 0f;
        else { t0 *= t0; n0 = t0 * t0 * Dot2(Grad3[gi0], x0, y0); }

        float t1 = 0.5f - x1 * x1 - y1 * y1;
        if (t1 < 0) n1 = 0f;
        else { t1 *= t1; n1 = t1 * t1 * Dot2(Grad3[gi1], x1, y1); }

        float t2 = 0.5f - x2 * x2 - y2 * y2;
        if (t2 < 0) n2 = 0f;
        else { t2 *= t2; n2 = t2 * t2 * Dot2(Grad3[gi2], x2, y2); }

        return 70f * (n0 + n1 + n2);
    }

    public float Noise3D(float x, float y, float z)
    {
        float s = (x + y + z) * F3;
        int i = FastFloor(x + s);
        int j = FastFloor(y + s);
        int k = FastFloor(z + s);

        float t = (i + j + k) * G3;
        float X0 = i - t, Y0 = j - t, Z0 = k - t;
        float x0 = x - X0, y0 = y - Y0, z0 = z - Z0;

        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0)      { i1=1; j1=0; k1=0; i2=1; j2=1; k2=0; }
            else if (x0 >= z0) { i1=1; j1=0; k1=0; i2=1; j2=0; k2=1; }
            else               { i1=0; j1=0; k1=1; i2=1; j2=0; k2=1; }
        }
        else
        {
            if (y0 < z0)       { i1=0; j1=0; k1=1; i2=0; j2=1; k2=1; }
            else if (x0 < z0)  { i1=0; j1=1; k1=0; i2=0; j2=1; k2=1; }
            else               { i1=0; j1=1; k1=0; i2=1; j2=1; k2=0; }
        }

        float x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
        float x2 = x0 - i2 + 2f*G3, y2 = y0 - j2 + 2f*G3, z2 = z0 - k2 + 2f*G3;
        float x3 = x0 - 1f + 3f*G3, y3 = y0 - 1f + 3f*G3, z3 = z0 - 1f + 3f*G3;

        int ii = i & 255, jj = j & 255, kk = k & 255;
        int gi0 = _perm12[ii + _perm[jj + _perm[kk]]];
        int gi1 = _perm12[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]];
        int gi2 = _perm12[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]];
        int gi3 = _perm12[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]];

        float n0, n1, n2, n3;

        float t0 = 0.6f - x0*x0 - y0*y0 - z0*z0;
        if (t0 < 0) n0 = 0f;
        else { t0 *= t0; n0 = t0 * t0 * Dot3(Grad3[gi0], x0, y0, z0); }

        float t1 = 0.6f - x1*x1 - y1*y1 - z1*z1;
        if (t1 < 0) n1 = 0f;
        else { t1 *= t1; n1 = t1 * t1 * Dot3(Grad3[gi1], x1, y1, z1); }

        float t2 = 0.6f - x2*x2 - y2*y2 - z2*z2;
        if (t2 < 0) n2 = 0f;
        else { t2 *= t2; n2 = t2 * t2 * Dot3(Grad3[gi2], x2, y2, z2); }

        float t3 = 0.6f - x3*x3 - y3*y3 - z3*z3;
        if (t3 < 0) n3 = 0f;
        else { t3 *= t3; n3 = t3 * t3 * Dot3(Grad3[gi3], x3, y3, z3); }

        return 32f * (n0 + n1 + n2 + n3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FastFloor(float x)
    {
        int xi = (int)x;
        return x < xi ? xi - 1 : xi;
    }
}
