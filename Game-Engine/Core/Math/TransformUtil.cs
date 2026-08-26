#nullable enable
using System;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class TransformUtil
{
    public static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);

    /// <summary>
    /// Recovers <see cref="Transform.Rotation"/> degrees (pitch=X, yaw=Y, roll=Z) from a pure 3×3 rotation
    /// (orthogonal, +1 determinant). <see cref="SN.Matrix4x4.CreateFromYawPitchRoll"/> uses the same composition
    /// as DirectX-style <c>Rz(roll) * Rx(pitch) * Ry(yaw)</c>, so there is no stable closed-form inverse for all
    /// orientations; this uses a short gradient solve (fine for spawn-time use).
    /// Multiple random restarts reduce bad local minima that showed up as ~±90° Euler spikes on steep alignments.
    /// </summary>
    public static Vector3 EulerDegreesFromRotationMatrix3x3(in SN.Matrix4x4 rotation)
    {
        ReadOnlySpan<(float y, float p, float r)> seeds =
        [
            (0f, 0f, 0f),
            (MathF.PI, 0f, 0f),
            (0f, MathF.PI * 0.5f, 0f),
            (0f, -MathF.PI * 0.5f, 0f),
            (0f, 0f, MathF.PI),
            (MathF.PI * 0.5f, MathF.PI * 0.25f, MathF.PI * 0.5f),
        ];

        float bestY = 0f, bestP = 0f, bestR = 0f;
        float bestCost = float.MaxValue;

        for (int s = 0; s < seeds.Length; s++)
        {
            float y = seeds[s].y, p = seeds[s].p, r = seeds[s].r;
            float lr = 0.65f;
            const float h = 1e-4f;
            const int iters = 100;
            for (int it = 0; it < iters; it++)
            {
                float c0 = YprMatrixCost(in rotation, y, p, r);
                if (c0 < 1e-12f)
                {
                    bestY = y; bestP = p; bestR = r; bestCost = c0;
                    goto picked;
                }

                float gy = (YprMatrixCost(in rotation, y + h, p, r) - YprMatrixCost(in rotation, y - h, p, r)) / (2f * h);
                float gp = (YprMatrixCost(in rotation, y, p + h, r) - YprMatrixCost(in rotation, y, p - h, r)) / (2f * h);
                float gr = (YprMatrixCost(in rotation, y, p, r + h) - YprMatrixCost(in rotation, y, p, r - h)) / (2f * h);
                y -= lr * gy;
                p -= lr * gp;
                r -= lr * gr;
                if (it % 40 == 39)
                    lr *= 0.65f;
            }

            float cf = YprMatrixCost(in rotation, y, p, r);
            if (cf < bestCost)
            {
                bestCost = cf;
                bestY = y; bestP = p; bestR = r;
            }
        }

    picked:
        const double r2d = 180.0 / Math.PI;
        return new Vector3(bestP * r2d, bestY * r2d, bestR * r2d);
    }

    static float YprMatrixCost(in SN.Matrix4x4 target, float yaw, float pitch, float roll)
    {
        var m = SN.Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        float dx, c = 0f;
        dx = m.M11 - target.M11; c += dx * dx;
        dx = m.M12 - target.M12; c += dx * dx;
        dx = m.M13 - target.M13; c += dx * dx;
        dx = m.M21 - target.M21; c += dx * dx;
        dx = m.M22 - target.M22; c += dx * dx;
        dx = m.M23 - target.M23; c += dx * dx;
        dx = m.M31 - target.M31; c += dx * dx;
        dx = m.M32 - target.M32; c += dx * dx;
        dx = m.M33 - target.M33; c += dx * dx;
        return c;
    }

    /// S * R * T (matches your SceneView order)
    public static SN.Matrix4x4 WorldFromTransform(Transform t)
    {
        var s = SN.Matrix4x4.CreateScale((float)t.Scale.X, (float)t.Scale.Y, (float)t.Scale.Z);
        var r = t.TryGetExplicitRotationMatrix(out var explicitR)
            ? explicitR
            : SN.Matrix4x4.CreateFromQuaternion(t.GetRotationQuaternion());
        var tr = SN.Matrix4x4.CreateTranslation((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z);
        return s * r * tr;
    }

    /// <summary>
    /// Sets transform euler so local +Y matches <paramref name="worldUp"/> and local -Z
    /// matches the tangent of <paramref name="worldForwardHint"/>.
    /// </summary>
    public static void AlignLocalUp(Transform t, SN.Vector3 worldUp, SN.Vector3 worldForwardHint)
    {
        float upLen = worldUp.Length();
        var up = upLen > 1e-8f ? worldUp / upLen : SN.Vector3.UnitY;

        var fwd = worldForwardHint - up * SN.Vector3.Dot(worldForwardHint, up);
        if (fwd.LengthSquared() <= 1e-8f)
        {
            var seed = MathF.Abs(up.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
            fwd = SN.Vector3.Cross(seed, up);
        }
        float fwdLen = fwd.Length();
        fwd = fwdLen > 1e-8f ? fwd / fwdLen : SN.Vector3.UnitZ;

        var right = SN.Vector3.Cross(fwd, up);
        float rightLen = right.Length();
        right = rightLen > 1e-8f ? right / rightLen : SN.Vector3.UnitX;
        fwd = SN.Vector3.Normalize(SN.Vector3.Cross(up, right));

        // Rows = images of local X,Y,Z. Local -Z is look forward.
        var m = new SN.Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            -fwd.X, -fwd.Y, -fwd.Z, 0f,
            0f, 0f, 0f, 1f);

        t.SetExplicitRotationMatrix(m);
    }

    /// Forward (-Z) using the same rotation as the world matrix.
    public static SN.Vector3 ForwardFrom(Transform t)
    {
        var f = SN.Vector3.TransformNormal(new SN.Vector3(0, 0, -1), SN.Matrix4x4.CreateFromQuaternion(t.GetRotationQuaternion()));
        return SN.Vector3.Normalize(f);
    }
}
