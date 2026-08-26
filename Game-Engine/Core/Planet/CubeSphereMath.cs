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
