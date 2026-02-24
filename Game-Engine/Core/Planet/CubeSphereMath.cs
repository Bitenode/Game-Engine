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

    public static SN.Vector3 FaceUVToDirection(int face, float u, float v)
    {
        float a = u * 2f - 1f;
        float b = v * 2f - 1f;

        SN.Vector3 cubePoint = face switch
        {
            0 => new SN.Vector3(1f, b, -a),
            1 => new SN.Vector3(-1f, b, a),
            2 => new SN.Vector3(a, 1f, -b),
            3 => new SN.Vector3(a, -1f, b),
            4 => new SN.Vector3(a, b, 1f),
            5 => new SN.Vector3(-a, b, -1f),
            _ => new SN.Vector3(0f, 1f, 0f),
        };

        return CubeToSphere(cubePoint);
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
