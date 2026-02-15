#nullable enable
using System;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class TransformUtil
{
    public static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);

    /// S * R(yaw,pitch,roll) * T (matches your SceneView order)
    public static SN.Matrix4x4 WorldFromTransform(Transform t)
    {
        var s = SN.Matrix4x4.CreateScale((float)t.Scale.X, (float)t.Scale.Y, (float)t.Scale.Z);
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(Deg2Rad(t.Rotation.Y), Deg2Rad(t.Rotation.X), Deg2Rad(t.Rotation.Z));
        var tr = SN.Matrix4x4.CreateTranslation((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z);
        return s * r * tr;
    }

    /// Forward (-Z) using the same yaw/pitch/roll convention as above.
    public static SN.Vector3 ForwardFrom(Transform t)
    {
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(Deg2Rad(t.Rotation.Y), Deg2Rad(t.Rotation.X), Deg2Rad(t.Rotation.Z));
        var f = SN.Vector3.TransformNormal(new SN.Vector3(0, 0, -1), r);
        return SN.Vector3.Normalize(f);
    }
}
