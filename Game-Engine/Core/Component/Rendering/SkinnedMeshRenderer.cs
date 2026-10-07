#nullable enable
using System;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// A MeshRenderer that performs GPU skinning using bone matrices from an Animator.
/// Extends MeshRenderer so the SceneRenderer can treat it as a normal MeshRenderer
/// while also uploading bone matrices when detected.
/// </summary>
[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : MeshRenderer
{
    /// <summary>
    /// Marker so MaterialRebind and other systems know not to replace
    /// the material that was set by the ModelImporter.
    /// </summary>
    public bool MaterialIsFromImporter { get; set; }
    /// <summary>The skeleton hierarchy this renderer is bound to.</summary>
    public Skeleton? Skeleton { get; set; }

    /// <summary>Final bone matrices ready for the GPU (model-space result of global * inverseBindPose).</summary>
    public SN.Matrix4x4[]? BoneMatrices { get; private set; }

    /// <summary>Optional root bone GameObject for transform overrides.</summary>
    public GameObject? RootBone { get; set; }

    /// <summary>True if BoneMatrices were computed at least once.</summary>
    public bool HasValidBoneMatrices => BoneMatrices != null && BoneMatrices.Length > 0;

    // Cached references
    private Animator? _animator;
    private bool _animatorSearched;

    // LateUpdate, the draw and the shadow pass all asked for bone matrices. Re-solve
    // only when the animator pose or the skeleton actually changed.
    private SN.Matrix4x4[]? _globalScratch;
    private Animator? _solvedAnimator;
    private Skeleton? _solvedSkeleton;
    private int _solvedPoseVersion = int.MinValue;

    public override void Start()
    {
        base.Start();
        TryRecoverSkeleton();
        FindAnimator();
        _animator?.EnsureBuilt();
        ComputeBoneMatrices();
    }

    public override void LateUpdate()
    {
        base.LateUpdate();
        ComputeBoneMatrices();
    }

    /// <summary>
    /// Called by the SceneRenderer just before drawing to ensure bone matrices
    /// are ready even in Scene View where Start/LateUpdate don't run.
    /// </summary>
    public void EnsureBoneMatrices()
    {
        TryRecoverSkeleton();

        // Always re-search for the Animator if we don't have one yet.
        // Don't rely on _animatorSearched because the hierarchy may have
        // changed since the last search (e.g. Animator added later).
        if (_animator == null)
            FindAnimator();

        // Ensure the Animator's state machine is built from DTOs
        // (same fix as the AnimationPanel — Start() doesn't run in editor)
        _animator?.EnsureBuilt();

        ComputeBoneMatrices();
    }

    private void FindAnimator()
    {
        _animatorSearched = true;
        _animator = null;
        var go = gameObject;
        while (go != null)
        {
            var anim = go.Behaviors?.OfType<Animator>().FirstOrDefault();
            if (anim != null)
            {
                _animator = anim;
                return;
            }
            go = go.Parent;
        }
    }

    /// <summary>
    /// Bind <see cref="Skeleton"/> from the sibling <see cref="MeshFilter"/>'s mesh.
    /// Skeleton is not persisted; the mesh rebuilt from <c>ModelPath</c> is the source of truth.
    /// Always sync when the mesh instance or its skeleton reference changes (avoids stale binding after load order or re-import).
    /// </summary>
    private void TryRecoverSkeleton()
    {
        var mf = gameObject?.Behaviors?.OfType<MeshFilter>().FirstOrDefault();
        var mesh = mf?.Mesh;
        if (mesh == null) return;

        var meshSkel = mesh.Skeleton;
        if (meshSkel == null)
            return;

        if (!ReferenceEquals(Skeleton, meshSkel))
        {
            Skeleton = meshSkel;
            _animator = null;
            _animatorSearched = false;
            BoneMatrices = null;
        }
    }

    /// <summary>Compute final bone matrices from the Animator's current bone pose.</summary>
    public void ComputeBoneMatrices()
    {
        if (Skeleton == null || Skeleton.BoneCount == 0)
        {
            BoneMatrices = null;
            return;
        }

        // Find the Animator if not yet searched
        if (!_animatorSearched)
            FindAnimator();

        int boneCount = Skeleton.BoneCount;
        var bonePoses = _animator?.CurrentBonePose;
        int poseVersion = _animator?.PoseVersion ?? -1;

        if (BoneMatrices != null && BoneMatrices.Length == boneCount
            && ReferenceEquals(_solvedAnimator, _animator)
            && ReferenceEquals(_solvedSkeleton, Skeleton)
            && _solvedPoseVersion == poseVersion)
            return;

        // Allocate / resize
        if (BoneMatrices == null || BoneMatrices.Length != boneCount)
            BoneMatrices = new SN.Matrix4x4[boneCount];

        // Compute global transforms walking the hierarchy
        if (_globalScratch == null || _globalScratch.Length != boneCount)
            _globalScratch = new SN.Matrix4x4[boneCount];
        var globalTransforms = _globalScratch;
        _solvedAnimator = _animator;
        _solvedSkeleton = Skeleton;
        _solvedPoseVersion = poseVersion;

        for (int i = 0; i < boneCount; i++)
        {
            var bone = Skeleton.Bones[i];

            // Local pose: from Animator bone poses, or the bone's local bind-pose
            // transform (from the Assimp node). Using Identity would collapse all bones
            // to the origin; using LocalBindTransform preserves the T-pose shape.
            SN.Matrix4x4 local;
            if (bonePoses != null && i < bonePoses.Length && bonePoses[i].IsSet)
            {
                // Full joint pose. Translation and rotation are both required:
                // the FBX stores them on separate pivot nodes and the importer
                // composes them back into this one local transform.
                var pose = bonePoses[i];
                var s = pose.Scale;
                if (MathF.Abs(s.X) < 1e-4f) s.X = 1f;
                if (MathF.Abs(s.Y) < 1e-4f) s.Y = 1f;
                if (MathF.Abs(s.Z) < 1e-4f) s.Z = 1f;
                local = SN.Matrix4x4.CreateScale(s)
                      * SN.Matrix4x4.CreateFromQuaternion(pose.Rotation)
                      * SN.Matrix4x4.CreateTranslation(pose.Position);
            }
            else
                local = bone.LocalBindTransform;

            // Hierarchy order matches the scene graph: childWorld = local * parentWorld
            // (see SceneRenderer / TransformUtil — same convention as row-vector-style accumulation).
            if (bone.ParentIndex >= 0 && bone.ParentIndex < boneCount)
                globalTransforms[i] = local * globalTransforms[bone.ParentIndex];
            else
                globalTransforms[i] = local;

            // Combined matrix is uploaded so GLSL (mat4 * vec4) matches SN (vec * mat); keep offset on the left.
            BoneMatrices[i] = bone.OffsetMatrix * globalTransforms[i];
        }
    }
}
