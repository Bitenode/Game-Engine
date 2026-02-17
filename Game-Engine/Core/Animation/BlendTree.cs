#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core.Animation
{
    /// <summary>Type of blend tree.</summary>
    public enum BlendTreeType
    {
        Simple1D,   // Blend between clips based on a single float parameter
        SimpleDirectional2D,  // 2D blend based on X/Y parameters (directional)
        FreeformDirectional2D // 2D blend with arbitrary positions
    }

    /// <summary>
    /// A child entry in a blend tree — one animation clip with a blend position.
    /// </summary>
    public sealed class BlendTreeChild
    {
        public BoneAnimationClip? Clip { get; set; }
        public AnimationClip? PropertyClip { get; set; }
        public float Threshold { get; set; }         // 1D position
        public SN.Vector2 Position { get; set; }     // 2D position
        public float Speed { get; set; } = 1f;
    }

    /// <summary>
    /// Blend tree node — blends between multiple animation clips based on parameters.
    /// Supports 1D blending (walk/run by speed) and 2D blending (strafe by direction/speed).
    /// Can be used as a state in the Animator state machine.
    /// </summary>
    public sealed class BlendTree
    {
        public string Name { get; set; } = "BlendTree";
        public BlendTreeType Type { get; set; } = BlendTreeType.Simple1D;

        /// <summary>Parameter name for 1D blending or X-axis of 2D.</summary>
        public string ParameterX { get; set; } = "Speed";

        /// <summary>Parameter name for Y-axis of 2D blending.</summary>
        public string ParameterY { get; set; } = "Direction";

        public List<BlendTreeChild> Children { get; set; } = new();

        /// <summary>
        /// Compute blend weights for each child given the current parameter values.
        /// Returns an array of weights (same length as Children) that sum to 1.
        /// </summary>
        public float[] ComputeWeights(float paramX, float paramY = 0f)
        {
            if (Children.Count == 0) return Array.Empty<float>();

            var weights = new float[Children.Count];

            switch (Type)
            {
                case BlendTreeType.Simple1D:
                    Compute1DWeights(paramX, weights);
                    break;
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                    Compute2DWeights(paramX, paramY, weights);
                    break;
            }

            return weights;
        }

        private void Compute1DWeights(float param, float[] weights)
        {
            if (Children.Count == 1)
            {
                weights[0] = 1f;
                return;
            }

            // Sort children by threshold for correct interpolation
            // (assumes they're already sorted)
            for (int i = 0; i < Children.Count - 1; i++)
            {
                float lo = Children[i].Threshold;
                float hi = Children[i + 1].Threshold;

                if (param >= lo && param <= hi)
                {
                    float range = hi - lo;
                    if (range < 1e-6f)
                    {
                        weights[i] = 0.5f;
                        weights[i + 1] = 0.5f;
                    }
                    else
                    {
                        float t = (param - lo) / range;
                        weights[i] = 1f - t;
                        weights[i + 1] = t;
                    }
                    return;
                }
            }

            // Outside range — clamp to nearest
            if (param <= Children[0].Threshold)
                weights[0] = 1f;
            else
                weights[Children.Count - 1] = 1f;
        }

        private void Compute2DWeights(float px, float py, float[] weights)
        {
            // Inverse distance weighting in 2D parameter space
            var paramPos = new SN.Vector2(px, py);
            float totalWeight = 0f;

            for (int i = 0; i < Children.Count; i++)
            {
                float dist = SN.Vector2.Distance(paramPos, Children[i].Position);
                if (dist < 1e-6f)
                {
                    // Exactly on a child position
                    Array.Clear(weights, 0, weights.Length);
                    weights[i] = 1f;
                    return;
                }
                weights[i] = 1f / (dist * dist); // Inverse square distance
                totalWeight += weights[i];
            }

            // Normalize
            if (totalWeight > 0f)
            {
                for (int i = 0; i < weights.Length; i++)
                    weights[i] /= totalWeight;
            }
        }

        /// <summary>
        /// Sample all bone poses from the blend tree at the given time.
        /// Blends between child clips using computed weights.
        /// </summary>
        public BonePose[]? SampleBones(int boneCount, float time, float paramX, float paramY = 0f)
        {
            if (Children.Count == 0 || boneCount == 0) return null;

            var weights = ComputeWeights(paramX, paramY);
            BonePose[]? result = null;

            for (int i = 0; i < Children.Count; i++)
            {
                if (weights[i] < 0.001f || Children[i].Clip == null) continue;

                float clipTime = time * Children[i].Speed;
                float duration = Children[i].Clip.Duration;
                if (duration > 0f && Children[i].Clip.Loop)
                    clipTime %= duration;

                var poses = Children[i].Clip.SampleAllBones(boneCount, clipTime);

                if (result == null)
                {
                    result = new BonePose[boneCount];
                    for (int b = 0; b < boneCount; b++)
                        result[b] = BonePoseBlend.Scale(poses[b], weights[i]);
                }
                else
                {
                    for (int b = 0; b < boneCount && b < poses.Length; b++)
                        result[b] = BonePoseBlend.Add(result[b], BonePoseBlend.Scale(poses[b], weights[i]));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Animation layer — allows compositing multiple animation sources
    /// with optional masking (e.g., upper body combat + lower body locomotion).
    /// </summary>
    public sealed class AnimationLayer
    {
        public string Name { get; set; } = "Base Layer";

        /// <summary>Blend weight for this layer (0 = inactive, 1 = full).</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>If true, this layer's output is added to the previous layer (additive blending).</summary>
        public bool IsAdditive { get; set; } = false;

        /// <summary>
        /// Bone mask — if non-null, only bones in this set are affected by this layer.
        /// Use bone indices. Null means all bones are affected.
        /// </summary>
        public HashSet<int>? BoneMask { get; set; }

        /// <summary>The current bone animation clip playing on this layer.</summary>
        public BoneAnimationClip? CurrentClip { get; set; }

        /// <summary>The blend tree for this layer (alternative to a single clip).</summary>
        public BlendTree? BlendTree { get; set; }

        /// <summary>Current playback time for this layer.</summary>
        public float Time { get; set; }

        /// <summary>Playback speed multiplier.</summary>
        public float Speed { get; set; } = 1f;

        /// <summary>
        /// Apply this layer's poses onto the existing pose array.
        /// </summary>
        public void Apply(BonePose[] poses, int boneCount, float paramX = 0f, float paramY = 0f)
        {
            BonePose[]? layerPoses = null;

            if (BlendTree != null)
            {
                layerPoses = BlendTree.SampleBones(boneCount, Time, paramX, paramY);
            }
            else if (CurrentClip != null)
            {
                layerPoses = CurrentClip.SampleAllBones(boneCount, Time);
            }

            if (layerPoses == null) return;

            for (int i = 0; i < boneCount && i < layerPoses.Length; i++)
            {
                // Skip masked bones
                if (BoneMask != null && !BoneMask.Contains(i))
                    continue;

                if (IsAdditive)
                {
                    // Additive: add the layer's delta to the base pose
                    poses[i].Position += layerPoses[i].Position * Weight;
                    poses[i].Rotation = SN.Quaternion.Slerp(
                        poses[i].Rotation,
                        poses[i].Rotation * layerPoses[i].Rotation,
                        Weight);
                }
                else
                {
                    // Override: blend between base and layer
                    poses[i] = BonePose.Lerp(poses[i], layerPoses[i], Weight);
                }
            }
        }
    }

    /// <summary>
    /// Root motion extractor — computes the delta position and rotation
    /// from the root bone's animation to drive character movement.
    /// </summary>
    public static class RootMotion
    {
        /// <summary>
        /// Extract root motion delta between two time points in an animation clip.
        /// </summary>
        /// <param name="clip">The bone animation clip.</param>
        /// <param name="rootBoneIndex">Index of the root bone (usually 0).</param>
        /// <param name="prevTime">Previous frame's playback time.</param>
        /// <param name="currentTime">Current frame's playback time.</param>
        /// <returns>World-space position delta and rotation delta.</returns>
        public static (SN.Vector3 positionDelta, SN.Quaternion rotationDelta) Extract(
            BoneAnimationClip clip, int rootBoneIndex, float prevTime, float currentTime)
        {
            var track = clip.GetTrack(rootBoneIndex);
            if (track == null)
                return (SN.Vector3.Zero, SN.Quaternion.Identity);

            var prevPose = track.Sample(prevTime);
            var currPose = track.Sample(currentTime);

            var posDelta = currPose.Position - prevPose.Position;

            // Rotation delta = inverse(prev) * current
            var rotDelta = SN.Quaternion.Inverse(prevPose.Rotation) * currPose.Rotation;

            return (posDelta, rotDelta);
        }

        /// <summary>
        /// Apply root motion to a Transform — moves the character based on root bone animation.
        /// </summary>
        public static void ApplyToTransform(Transform transform, SN.Vector3 posDelta, SN.Quaternion rotDelta)
        {
            // Apply position delta (rotated by the character's current orientation)
            float yaw = (float)transform.Rotation.Y * MathF.PI / 180f;
            var forward = new SN.Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
            var right = new SN.Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

            var worldDelta = right * posDelta.X + SN.Vector3.UnitY * posDelta.Y + forward * posDelta.Z;

            transform.Position = new Vector3(
                transform.Position.X + worldDelta.X,
                transform.Position.Y + worldDelta.Y,
                transform.Position.Z + worldDelta.Z);

            // Apply rotation delta (only Y rotation for ground-based characters)
            var euler = QuaternionToEuler(rotDelta);
            transform.Rotation = new Vector3(
                transform.Rotation.X,
                transform.Rotation.Y + euler.Y * (180f / MathF.PI),
                transform.Rotation.Z);
        }

        private static SN.Vector3 QuaternionToEuler(SN.Quaternion q)
        {
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

    /// <summary>Helper methods for BonePose blending in blend trees.</summary>
    public static class BonePoseBlend
    {
        /// <summary>Scale all components of a pose by a scalar weight.</summary>
        public static BonePose Scale(BonePose pose, float weight)
        {
            return new BonePose
            {
                Position = pose.Position * weight,
                Rotation = SN.Quaternion.Slerp(SN.Quaternion.Identity, pose.Rotation, weight),
                Scale = SN.Vector3.Lerp(SN.Vector3.One, pose.Scale, weight)
            };
        }

        /// <summary>Add two weighted poses together (for accumulation during blending).</summary>
        public static BonePose Add(BonePose a, BonePose b)
        {
            return new BonePose
            {
                Position = a.Position + b.Position,
                Rotation = SN.Quaternion.Normalize(new SN.Quaternion(
                    a.Rotation.X + b.Rotation.X,
                    a.Rotation.Y + b.Rotation.Y,
                    a.Rotation.Z + b.Rotation.Z,
                    a.Rotation.W + b.Rotation.W)),
                Scale = a.Scale + b.Scale - SN.Vector3.One
            };
        }
    }
}
