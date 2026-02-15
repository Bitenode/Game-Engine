#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core;

/// <summary>Maximum bones supported by the GPU skinning shader.</summary>
public static class SkeletonLimits
{
    public const int MaxBones = 128;
}

/// <summary>A single bone in a skeleton hierarchy.</summary>
public sealed class Bone
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public int ParentIndex { get; set; } = -1;

    /// <summary>Inverse bind-pose matrix (transforms from mesh-space to bone-local-space).</summary>
    public SN.Matrix4x4 OffsetMatrix { get; set; } = SN.Matrix4x4.Identity;

    /// <summary>Local bind-pose transform of this bone (from Assimp node). Used as default when no animation.</summary>
    public SN.Matrix4x4 LocalBindTransform { get; set; } = SN.Matrix4x4.Identity;

    /// <summary>Child bone indices.</summary>
    public int[] Children { get; set; } = Array.Empty<int>();
}

/// <summary>A complete skeleton (bone hierarchy) shared by one or more skinned meshes.</summary>
public sealed class Skeleton
{
    public Bone[] Bones { get; }
    public int[] RootBoneIndices { get; }

    private readonly Dictionary<string, int> _nameToIndex;

    public int BoneCount => Bones.Length;

    public Skeleton(Bone[] bones, int[] rootBoneIndices)
    {
        Bones = bones;
        RootBoneIndices = rootBoneIndices;
        _nameToIndex = new Dictionary<string, int>(bones.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bones.Length; i++)
            _nameToIndex[bones[i].Name] = i;
    }

    /// <summary>Find a bone by name. Returns -1 if not found.</summary>
    public int FindBone(string name)
        => _nameToIndex.TryGetValue(name, out var idx) ? idx : -1;

    /// <summary>Try to get a bone by name.</summary>
    public bool TryGetBone(string name, out Bone bone)
    {
        if (_nameToIndex.TryGetValue(name, out var idx))
        {
            bone = Bones[idx];
            return true;
        }
        bone = null!;
        return false;
    }
}

/// <summary>Per-bone local transform (result of sampling a bone animation).</summary>
public struct BonePose
{
    public SN.Vector3 Position;
    public SN.Quaternion Rotation;
    public SN.Vector3 Scale;

    public static BonePose Identity => new()
    {
        Position = SN.Vector3.Zero,
        Rotation = SN.Quaternion.Identity,
        Scale = SN.Vector3.One
    };

    public SN.Matrix4x4 ToMatrix()
    {
        return SN.Matrix4x4.CreateScale(Scale)
             * SN.Matrix4x4.CreateFromQuaternion(Rotation)
             * SN.Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>Blend between two poses.</summary>
    public static BonePose Lerp(in BonePose a, in BonePose b, float t)
    {
        return new BonePose
        {
            Position = SN.Vector3.Lerp(a.Position, b.Position, t),
            Rotation = SN.Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Scale = SN.Vector3.Lerp(a.Scale, b.Scale, t)
        };
    }
}
