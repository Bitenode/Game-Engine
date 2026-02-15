#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Animation
{
    /// <summary>
    /// Two-bone IK solver for arms and legs.
    /// Given a chain of 3 joints (root, mid, tip) and a target position,
    /// computes the rotations needed to reach the target.
    /// Uses the analytic two-bone approach (law of cosines).
    /// </summary>
    public static class TwoBoneIK
    {
        /// <summary>
        /// Solve two-bone IK for a 3-joint chain.
        /// </summary>
        /// <param name="root">World position of the root joint (e.g., shoulder/hip).</param>
        /// <param name="mid">World position of the mid joint (e.g., elbow/knee).</param>
        /// <param name="tip">World position of the tip/end effector (e.g., hand/foot).</param>
        /// <param name="target">Desired world position for the tip.</param>
        /// <param name="poleTarget">Pole target for controlling elbow/knee direction.</param>
        /// <param name="newMid">Output: solved mid joint position.</param>
        /// <param name="newTip">Output: solved tip position.</param>
        /// <returns>True if a valid solution was found.</returns>
        public static bool Solve(
            SN.Vector3 root, SN.Vector3 mid, SN.Vector3 tip,
            SN.Vector3 target, SN.Vector3 poleTarget,
            out SN.Vector3 newMid, out SN.Vector3 newTip)
        {
            float upperLen = SN.Vector3.Distance(root, mid);
            float lowerLen = SN.Vector3.Distance(mid, tip);
            float targetDist = SN.Vector3.Distance(root, target);

            // Clamp target distance to the reachable range
            float maxReach = upperLen + lowerLen - 0.001f;
            float minReach = MathF.Abs(upperLen - lowerLen) + 0.001f;

            if (targetDist > maxReach)
            {
                // Fully extended — just point toward target
                var dir = SN.Vector3.Normalize(target - root);
                newMid = root + dir * upperLen;
                newTip = newMid + dir * lowerLen;
                return true;
            }

            if (targetDist < minReach)
                targetDist = minReach;

            // Law of cosines to find the angle at the mid joint
            // a = upperLen, b = lowerLen, c = targetDist
            float cosAngle = (upperLen * upperLen + lowerLen * lowerLen - targetDist * targetDist)
                           / (2f * upperLen * lowerLen);
            cosAngle = MathF.Max(-1f, MathF.Min(1f, cosAngle));

            // Angle at root (between root->target and root->mid)
            float cosRootAngle = (upperLen * upperLen + targetDist * targetDist - lowerLen * lowerLen)
                               / (2f * upperLen * targetDist);
            cosRootAngle = MathF.Max(-1f, MathF.Min(1f, cosRootAngle));
            float rootAngle = MathF.Acos(cosRootAngle);

            // Direction from root to target
            var toTarget = SN.Vector3.Normalize(target - root);

            // Create a plane normal using the pole target to determine bend direction
            var toPole = poleTarget - root;
            var planeNormal = SN.Vector3.Cross(toTarget, toPole);
            float planeLen = planeNormal.Length();

            if (planeLen < 1e-6f)
            {
                // Pole target is colinear with root->target, use a fallback axis
                planeNormal = GetPerpendicular(toTarget);
            }
            else
            {
                planeNormal /= planeLen;
            }

            // Rotate the toTarget direction by rootAngle around the plane normal
            var rotAxis = SN.Vector3.Normalize(planeNormal);
            var midDir = RotateAround(toTarget, rotAxis, rootAngle);

            newMid = root + midDir * upperLen;
            newTip = target; // The tip should reach the target

            // Verify the tip position is actually reachable from the new mid
            var midToTarget = target - newMid;
            float midToTargetDist = midToTarget.Length();
            if (midToTargetDist > 1e-6f)
                newTip = newMid + (midToTarget / midToTargetDist) * lowerLen;

            return true;
        }

        /// <summary>Rotate a vector around an axis by the given angle (radians).</summary>
        private static SN.Vector3 RotateAround(SN.Vector3 v, SN.Vector3 axis, float angle)
        {
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            float dot = SN.Vector3.Dot(axis, v);
            var cross = SN.Vector3.Cross(axis, v);
            return v * cos + cross * sin + axis * dot * (1f - cos);
        }

        /// <summary>Get a vector perpendicular to the given direction.</summary>
        private static SN.Vector3 GetPerpendicular(SN.Vector3 dir)
        {
            if (MathF.Abs(dir.Y) < 0.99f)
                return SN.Vector3.Normalize(SN.Vector3.Cross(dir, SN.Vector3.UnitY));
            return SN.Vector3.Normalize(SN.Vector3.Cross(dir, SN.Vector3.UnitX));
        }
    }

    /// <summary>
    /// FABRIK (Forward And Backward Reaching Inverse Kinematics) solver.
    /// Works with chains of arbitrary length. Iteratively converges on a solution.
    /// Useful for tentacles, tails, spine chains, and multi-joint IK.
    /// </summary>
    public static class FABRIK
    {
        /// <summary>
        /// Solve IK for a chain of joints using the FABRIK algorithm.
        /// </summary>
        /// <param name="joints">Array of joint world positions (modified in place). Index 0 is the root.</param>
        /// <param name="target">Desired position for the last joint (end effector).</param>
        /// <param name="tolerance">Distance threshold for convergence.</param>
        /// <param name="maxIterations">Maximum solver iterations.</param>
        /// <returns>True if the target was reached within tolerance.</returns>
        public static bool Solve(SN.Vector3[] joints, SN.Vector3 target,
                                  float tolerance = 0.01f, int maxIterations = 10)
        {
            if (joints == null || joints.Length < 2) return false;

            int n = joints.Length;

            // Pre-compute segment lengths
            float[] lengths = new float[n - 1];
            float totalLength = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                lengths[i] = SN.Vector3.Distance(joints[i], joints[i + 1]);
                totalLength += lengths[i];
            }

            float distToTarget = SN.Vector3.Distance(joints[0], target);

            // Unreachable — stretch toward target
            if (distToTarget > totalLength)
            {
                var dir = SN.Vector3.Normalize(target - joints[0]);
                for (int i = 1; i < n; i++)
                    joints[i] = joints[i - 1] + dir * lengths[i - 1];
                return false;
            }

            var rootPos = joints[0]; // Anchor the root

            for (int iter = 0; iter < maxIterations; iter++)
            {
                float endDist = SN.Vector3.Distance(joints[n - 1], target);
                if (endDist < tolerance)
                    return true;

                // ── Forward pass: move end effector to target, work backward ──
                joints[n - 1] = target;
                for (int i = n - 2; i >= 0; i--)
                {
                    var dir = SN.Vector3.Normalize(joints[i] - joints[i + 1]);
                    joints[i] = joints[i + 1] + dir * lengths[i];
                }

                // ── Backward pass: fix root position, work forward ──
                joints[0] = rootPos;
                for (int i = 1; i < n; i++)
                {
                    var dir = SN.Vector3.Normalize(joints[i] - joints[i - 1]);
                    joints[i] = joints[i - 1] + dir * lengths[i - 1];
                }
            }

            return SN.Vector3.Distance(joints[n - 1], target) < tolerance;
        }

        /// <summary>
        /// Constrained FABRIK with per-joint angle limits (cone constraint).
        /// </summary>
        public static bool SolveConstrained(SN.Vector3[] joints, SN.Vector3 target,
                                             float[] maxAngles,
                                             float tolerance = 0.01f, int maxIterations = 10)
        {
            if (joints == null || joints.Length < 2) return false;

            // First solve unconstrained
            bool reached = Solve(joints, target, tolerance, maxIterations);

            // Then apply angle constraints
            if (maxAngles != null && maxAngles.Length >= joints.Length - 2)
            {
                for (int i = 1; i < joints.Length - 1; i++)
                {
                    var prev = joints[i - 1];
                    var curr = joints[i];
                    var next = joints[i + 1];

                    var toPrev = SN.Vector3.Normalize(prev - curr);
                    var toNext = SN.Vector3.Normalize(next - curr);

                    float angle = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, SN.Vector3.Dot(toPrev, toNext))));
                    float maxAngle = maxAngles[i - 1] * MathF.PI / 180f;

                    if (angle < MathF.PI - maxAngle)
                    {
                        // Constrain the angle
                        var axis = SN.Vector3.Cross(toPrev, toNext);
                        float axisLen = axis.Length();
                        if (axisLen > 1e-6f)
                        {
                            axis /= axisLen;
                            float targetAngle = MathF.PI - maxAngle;
                            float cos = MathF.Cos(targetAngle);
                            float sin = MathF.Sin(targetAngle);
                            float dot = SN.Vector3.Dot(axis, toPrev);
                            var cross = SN.Vector3.Cross(axis, toPrev);
                            var constrainedDir = toPrev * cos + cross * sin + axis * dot * (1f - cos);
                            float segLen = SN.Vector3.Distance(curr, next);
                            joints[i + 1] = curr - constrainedDir * segLen;
                        }
                    }
                }
            }

            return reached;
        }
    }

    /// <summary>
    /// Look-At IK constraint: rotates a bone to face a target.
    /// Useful for head tracking, turret aiming, etc.
    /// </summary>
    public static class LookAtIK
    {
        /// <summary>
        /// Compute the rotation needed to make a bone's forward direction point at a target.
        /// </summary>
        /// <param name="bonePosition">World position of the bone.</param>
        /// <param name="target">World position to look at.</param>
        /// <param name="currentForward">Current forward direction of the bone.</param>
        /// <param name="maxAngleDeg">Maximum rotation allowed per frame (degrees). 0 = unlimited.</param>
        /// <returns>Quaternion rotation to apply to the bone.</returns>
        public static SN.Quaternion ComputeRotation(
            SN.Vector3 bonePosition, SN.Vector3 target,
            SN.Vector3 currentForward, float maxAngleDeg = 0f)
        {
            var toTarget = SN.Vector3.Normalize(target - bonePosition);
            float dot = SN.Vector3.Dot(currentForward, toTarget);
            dot = MathF.Max(-1f, MathF.Min(1f, dot));

            float angle = MathF.Acos(dot);
            if (angle < 1e-6f)
                return SN.Quaternion.Identity;

            if (maxAngleDeg > 0f)
            {
                float maxRad = maxAngleDeg * MathF.PI / 180f;
                angle = MathF.Min(angle, maxRad);
            }

            var axis = SN.Vector3.Cross(currentForward, toTarget);
            float axisLen = axis.Length();
            if (axisLen < 1e-6f)
                return SN.Quaternion.Identity;

            axis /= axisLen;
            return SN.Quaternion.CreateFromAxisAngle(axis, angle);
        }
    }
}
