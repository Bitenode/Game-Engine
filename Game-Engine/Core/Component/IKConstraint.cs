#nullable enable
using System;
using Game_Engine.Core.Animation;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Type of IK constraint to apply.</summary>
    public enum IKMode
    {
        TwoBone,    // Arm/leg IK (3 joints)
        LookAt,     // Head/turret look-at
        FABRIK      // Multi-joint chain IK
    }

    /// <summary>
    /// IK constraint component — applies inverse kinematics to animated bones.
    /// Supports two-bone IK (arms/legs), look-at constraints (head tracking),
    /// and FABRIK chains (tails, spines, tentacles).
    /// Runs in LateUpdate after animation so it overrides bone poses.
    /// </summary>
    public sealed class IKConstraint : Behavior
    {
        // ── Mode ──
        [Persist] public IKMode Mode { get; set; } = IKMode.TwoBone;

        // ── Target ──
        /// <summary>Name of the target GameObject to reach/look at.</summary>
        [Persist] public string TargetName { get; set; } = "";

        /// <summary>Name of the pole target GameObject (for two-bone IK bend direction).</summary>
        [Persist] public string PoleTargetName { get; set; } = "";

        // ── Bone references (by name) ──
        /// <summary>Root bone name (e.g., "UpperArm", "Thigh").</summary>
        [Persist] public string RootBoneName { get; set; } = "";

        /// <summary>Mid bone name (e.g., "Forearm", "Calf").</summary>
        [Persist] public string MidBoneName { get; set; } = "";

        /// <summary>Tip bone name (e.g., "Hand", "Foot").</summary>
        [Persist] public string TipBoneName { get; set; } = "";

        // ── Parameters ──
        /// <summary>Blend weight (0 = no IK, 1 = full IK).</summary>
        [Persist] public float Weight { get; set; } = 1f;

        /// <summary>Maximum look-at angle in degrees (LookAt mode only).</summary>
        [Persist] public float MaxAngle { get; set; } = 90f;

        /// <summary>Number of FABRIK joints in the chain (FABRIK mode only).</summary>
        [Persist] public int ChainLength { get; set; } = 4;

        /// <summary>FABRIK solver iterations.</summary>
        [Persist] public int Iterations { get; set; } = 10;

        /// <summary>Convergence tolerance.</summary>
        [Persist] public float Tolerance { get; set; } = 0.01f;

        // ── Runtime cache ──
        private GameObject? _targetGO;
        private GameObject? _poleTargetGO;

        public override void Start()
        {
            CacheTargets();
        }

        private void CacheTargets()
        {
            _targetGO = string.IsNullOrEmpty(TargetName) ? null : SceneQuery.FindByName(TargetName);
            _poleTargetGO = string.IsNullOrEmpty(PoleTargetName) ? null : SceneQuery.FindByName(PoleTargetName);
        }

        public override void LateUpdate()
        {
            if (Weight <= 0f) return;

            // Re-cache targets if they went stale
            if (_targetGO == null && !string.IsNullOrEmpty(TargetName))
                CacheTargets();

            if (_targetGO == null) return;

            var targetPos = GetWorldPos(_targetGO);

            switch (Mode)
            {
                case IKMode.TwoBone:
                    SolveTwoBone(targetPos);
                    break;
                case IKMode.LookAt:
                    SolveLookAt(targetPos);
                    break;
                case IKMode.FABRIK:
                    SolveFABRIK(targetPos);
                    break;
            }
        }

        private void SolveTwoBone(SN.Vector3 target)
        {
            // For two-bone IK, we need to find the animator and modify bone poses
            var animator = GetComponent<Animator>();
            if (animator?.CurrentBonePose == null) return;

            // Find bone indices by name
            int rootIdx = FindBoneIndex(animator, RootBoneName);
            int midIdx = FindBoneIndex(animator, MidBoneName);
            int tipIdx = FindBoneIndex(animator, TipBoneName);

            if (rootIdx < 0 || midIdx < 0 || tipIdx < 0) return;

            var poses = animator.CurrentBonePose;
            if (rootIdx >= poses.Length || midIdx >= poses.Length || tipIdx >= poses.Length) return;

            var rootPos = poses[rootIdx].Position;
            var midPos = poses[midIdx].Position;
            var tipPos = poses[tipIdx].Position;

            var poleTarget = _poleTargetGO != null
                ? GetWorldPos(_poleTargetGO)
                : midPos + SN.Vector3.UnitZ; // Default pole

            if (TwoBoneIK.Solve(rootPos, midPos, tipPos, target, poleTarget,
                                 out var newMid, out var newTip))
            {
                // Blend between original and IK solution
                poses[midIdx].Position = SN.Vector3.Lerp(midPos, newMid, Weight);
                poses[tipIdx].Position = SN.Vector3.Lerp(tipPos, newTip, Weight);
            }
        }

        private void SolveLookAt(SN.Vector3 target)
        {
            // Simple look-at: rotate this GameObject toward the target
            var myPos = GetWorldPos(gameObject!);
            var forward = GetForward();

            var rot = LookAtIK.ComputeRotation(myPos, target, forward, MaxAngle);

            if (Weight < 1f)
                rot = SN.Quaternion.Slerp(SN.Quaternion.Identity, rot, Weight);

            // Apply rotation to the Transform
            var euler = QuaternionToEuler(rot);
            var cur = Transform.Rotation;
            Transform.Rotation = new Vector3(
                cur.X + euler.X * (180f / MathF.PI),
                cur.Y + euler.Y * (180f / MathF.PI),
                cur.Z + euler.Z * (180f / MathF.PI));
        }

        private void SolveFABRIK(SN.Vector3 target)
        {
            // Build a chain from this object and its children
            var chain = new SN.Vector3[ChainLength];
            var gameObjects = new GameObject?[ChainLength];

            var current = gameObject;
            for (int i = 0; i < ChainLength && current != null; i++)
            {
                chain[i] = GetWorldPos(current);
                gameObjects[i] = current;
                current = current.Children.Count > 0 ? current.Children[0] : null;
            }

            if (FABRIK.Solve(chain, target, Tolerance, Iterations))
            {
                // Apply solved positions (blended)
                for (int i = 1; i < ChainLength; i++)
                {
                    if (gameObjects[i] == null) break;
                    var original = GetWorldPos(gameObjects[i]);
                    var solved = chain[i];
                    var blended = SN.Vector3.Lerp(original, solved, Weight);
                    gameObjects[i].Transform.Position = new Vector3(blended.X, blended.Y, blended.Z);
                }
            }
        }

        private int FindBoneIndex(Animator animator, string boneName)
        {
            if (string.IsNullOrEmpty(boneName) || animator.CurrentBonePose == null) return -1;
            // Bone names are typically stored in the BoneAnimationClip
            // For now, use a simple index lookup by name
            // This would need to be connected to the actual bone hierarchy
            var states = animator.States;
            foreach (var state in states.Values)
            {
                if (state.BoneClip != null)
                {
                    for (int i = 0; i < state.BoneClip.Tracks.Count; i++)
                    {
                        if (state.BoneClip.Tracks[i].BoneName == boneName)
                            return state.BoneClip.Tracks[i].BoneIndex;
                    }
                }
            }
            return -1;
        }

        private SN.Vector3 GetForward()
        {
            float yaw = (float)Transform.Rotation.Y * MathF.PI / 180f;
            return new SN.Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        }

        private static SN.Vector3 GetWorldPos(GameObject go)
            => new((float)go.Transform.Position.X, (float)go.Transform.Position.Y, (float)go.Transform.Position.Z);

        private static SN.Vector3 QuaternionToEuler(SN.Quaternion q)
        {
            // Convert quaternion to Euler angles (roll, pitch, yaw)
            float sinr_cosp = 2f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float pitch = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

            float siny_cosp = 2f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

            return new SN.Vector3(roll, pitch, yaw);
        }
    }
}
