using System;
using System.Runtime.CompilerServices;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Utility math for mapping between a unit cube (6 faces) and a unit sphere.
/// Face indices: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z.
/// </summary>
public static class CubeSphereMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SN.Vector3 CubeToSphere(SN.Vector3 p)
    {
        float x2 = p.X * p.X;
        float y2 = p.Y * p.Y;
        float z2 = p.Z * p.Z;
        return new SN.Vector3(
            p.X * MathF.Sqrt(MathF.Max(0f, 1f - y2 * 0.5f - z2 * 0.5f + y2 * z2 / 3f)),
            p.Y * MathF.Sqrt(MathF.Max(0f, 1f - z2 * 0.5f - x2 * 0.5f + z2 * x2 / 3f)),
            p.Z * MathF.Sqrt(MathF.Max(0f, 1f - x2 * 0.5f - y2 * 0.5f + x2 * y2 / 3f))
        );
    }

    public static SN.Vector3 FaceUVToCube(int face, float u, float v)
    {
        float a = u * 2f - 1f;
        float b = v * 2f - 1f;
        return face switch
        {
            0 => new SN.Vector3(1f, b, -a),
            1 => new SN.Vector3(-1f, b, a),
            2 => new SN.Vector3(a, 1f, -b),
            3 => new SN.Vector3(a, -1f, b),
            4 => new SN.Vector3(a, b, 1f),
            5 => new SN.Vector3(-a, b, -1f),
            _ => new SN.Vector3(0f, 1f, 0f),
        };
    }

    public static SN.Vector3 FaceUVToDirection(int face, float u, float v)
    {
        var cubePoint = FaceUVToCube(face, u, v);
        cubePoint = SnapCubePoint(cubePoint);
        return CubeToSphere(cubePoint);
    }

    /// <summary>Snap axes that sit on a cube face so shared edges match exactly.</summary>
    public static SN.Vector3 SnapCubePoint(SN.Vector3 p)
    {
        const float e = 1e-5f;
        if (MathF.Abs(MathF.Abs(p.X) - 1f) <= e) p.X = MathF.Sign(p.X);
        if (MathF.Abs(MathF.Abs(p.Y) - 1f) <= e) p.Y = MathF.Sign(p.Y);
        if (MathF.Abs(MathF.Abs(p.Z) - 1f) <= e) p.Z = MathF.Sign(p.Z);
        return p;
    }

    /// <summary>Map a UV that walked off a face onto the adjacent cube face.</summary>
    public static (int Face, float U, float V) WrapFaceUV(int face, float u, float v)
    {
        if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
            return (face, u, v);

        var p = FaceUVToCube(face, u, v);
        float m = MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z)));
        if (m > 1e-8f) p /= m;
        float len = p.Length();
        if (len < 1e-8f) return (face, Math.Clamp(u, 0f, 1f), Math.Clamp(v, 0f, 1f));
        return SphereToCube(p / len);
    }

    public static (SN.Vector3 Tangent, SN.Vector3 Bitangent, SN.Vector3 Normal) GetFaceBasis(int face)
    {
        return face switch
        {
            0 => (new SN.Vector3(0, 0, -1), new SN.Vector3(0, 1, 0), new SN.Vector3(1, 0, 0)),
            1 => (new SN.Vector3(0, 0, 1), new SN.Vector3(0, 1, 0), new SN.Vector3(-1, 0, 0)),
            2 => (new SN.Vector3(1, 0, 0), new SN.Vector3(0, 0, -1), new SN.Vector3(0, 1, 0)),
            3 => (new SN.Vector3(1, 0, 0), new SN.Vector3(0, 0, 1), new SN.Vector3(0, -1, 0)),
            4 => (new SN.Vector3(1, 0, 0), new SN.Vector3(0, 1, 0), new SN.Vector3(0, 0, 1)),
            5 => (new SN.Vector3(-1, 0, 0), new SN.Vector3(0, 1, 0), new SN.Vector3(0, 0, -1)),
            _ => (new SN.Vector3(1, 0, 0), new SN.Vector3(0, 1, 0), new SN.Vector3(0, 0, 1)),
        };
    }

    /// <summary>
    /// Exact inverse of <see cref="FaceUVToDirection"/>. <see cref="SphereToCube"/> is a
    /// plain central projection and does NOT invert the Everitt warp in
    /// <see cref="CubeToSphere"/> — it lands up to ~4% of a face away. Use this when
    /// indexing anything laid out in generation UV (chunk vertex grids, node bounds).
    /// </summary>
    public static (int Face, float U, float V) SphereToCubeExact(SN.Vector3 dir)
    {
        float ax = MathF.Abs(dir.X);
        float ay = MathF.Abs(dir.Y);
        float az = MathF.Abs(dir.Z);

        // Minor components s1/s2 relative to the major axis; the warp keeps the
        // major axis, so face selection by max component is still valid.
        int face;
        float s1, s2;
        if (ax >= ay && ax >= az)
        {
            face = dir.X > 0 ? 0 : 1;
            s1 = dir.X > 0 ? -dir.Z : dir.Z;
            s2 = dir.Y;
        }
        else if (ay >= ax && ay >= az)
        {
            face = dir.Y > 0 ? 2 : 3;
            s1 = dir.X;
            s2 = dir.Y > 0 ? -dir.Z : dir.Z;
        }
        else
        {
            face = dir.Z > 0 ? 4 : 5;
            s1 = dir.Z > 0 ? dir.X : -dir.X;
            s2 = dir.Y;
        }

        InvertEverittFace(s1, s2, out float a, out float b);
        return (face, (a + 1f) * 0.5f, (b + 1f) * 0.5f);
    }

    /// <summary>
    /// Given the two minor sphere components on a face, recover the cube-face
    /// coordinates in [-1,1] (Nowell's closed-form inverse of the Everitt warp).
    /// </summary>
    static void InvertEverittFace(float s1, float s2, out float c1, out float c2)
    {
        double x = s1, y = s2;
        double a2 = x * x * 2.0;
        double b2 = y * y * 2.0;
        double inner = -a2 + b2 - 3.0;
        double disc = inner * inner - 12.0 * a2;
        double innersqrt = -Math.Sqrt(Math.Max(0.0, disc));
        const double isqrt2 = 0.70710678118654752;

        double cx = Math.Abs(x) < 1e-9 ? 0.0 : Math.Sqrt(Math.Max(0.0, innersqrt + a2 - b2 + 3.0)) * isqrt2;
        double cy = Math.Abs(y) < 1e-9 ? 0.0 : Math.Sqrt(Math.Max(0.0, innersqrt - a2 + b2 + 3.0)) * isqrt2;
        if (x < 0) cx = -cx;
        if (y < 0) cy = -cy;
        c1 = (float)Math.Clamp(cx, -1.0, 1.0);
        c2 = (float)Math.Clamp(cy, -1.0, 1.0);
    }

    public static (int Face, float U, float V) SphereToCube(SN.Vector3 dir)
    {
        float ax = MathF.Abs(dir.X);
        float ay = MathF.Abs(dir.Y);
        float az = MathF.Abs(dir.Z);

        int face;
        float a, b;

        if (ax >= ay && ax >= az)
        {
            float inv = 1f / ax;
            if (dir.X > 0) { face = 0; a = -dir.Z * inv; b = dir.Y * inv; }
            else { face = 1; a = dir.Z * inv; b = dir.Y * inv; }
        }
        else if (ay >= ax && ay >= az)
        {
            float inv = 1f / ay;
            if (dir.Y > 0) { face = 2; a = dir.X * inv; b = -dir.Z * inv; }
            else { face = 3; a = dir.X * inv; b = dir.Z * inv; }
        }
        else
        {
            float inv = 1f / az;
            if (dir.Z > 0) { face = 4; a = dir.X * inv; b = dir.Y * inv; }
            else { face = 5; a = -dir.X * inv; b = dir.Y * inv; }
        }

        return (face, (a + 1f) * 0.5f, (b + 1f) * 0.5f);
    }
}
