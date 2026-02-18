#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core.VirtualCamera
{
    /// <summary>Blend type for transitioning between virtual cameras.</summary>
    public enum BlendType
    {
        Cut,
        EaseInOut,
        Linear
    }

    /// <summary>
    /// Camera Brain component. Attach to the main Camera to enable virtual camera blending.
    /// Automatically blends between the highest-priority active VirtualCamera.
    /// </summary>
    [Require(typeof(Component.Camera))]
    public sealed class CameraBrain : Behavior
    {
        [Persist] public BlendType DefaultBlend { get; set; } = BlendType.EaseInOut;
        [Persist] public float DefaultBlendDuration { get; set; } = 1f;

        private VirtualCamera? _activeVCam;
        private VirtualCamera? _previousVCam;
        private float _blendTime;
        private float _blendDuration;
        private bool _isBlending;

        /// <summary>The currently active virtual camera.</summary>
        public VirtualCamera? ActiveVirtualCamera => _activeVCam;

        public override void LateUpdate()
        {
            // Find highest-priority active virtual camera
            var vcams = VirtualCamera.All;
            VirtualCamera? best = null;
            for (int i = 0; i < vcams.Count; i++)
            {
                if (vcams[i].IsActiveAndEnabled)
                {
                    best = vcams[i];
                    break; // already sorted by priority descending
                }
            }

            if (best != _activeVCam)
            {
                _previousVCam = _activeVCam;
                _activeVCam = best;
                _blendTime = 0f;
                _blendDuration = best?.BlendDuration ?? DefaultBlendDuration;
                _isBlending = _previousVCam != null && _blendDuration > 0.001f;
            }

            if (_activeVCam == null) return;

            SN.Vector3 targetPos;
            SN.Quaternion targetRot;

            if (_isBlending && _previousVCam != null)
            {
                _blendTime += Time.deltaTime;
                float t = Math.Clamp(_blendTime / _blendDuration, 0f, 1f);

                // Apply easing
                t = DefaultBlend switch
                {
                    BlendType.EaseInOut => SmoothStep(t),
                    BlendType.Cut => 1f,
                    _ => t
                };

                targetPos = SN.Vector3.Lerp(_previousVCam.ComputedPosition, _activeVCam.ComputedPosition, t);
                targetRot = SN.Quaternion.Slerp(_previousVCam.ComputedRotation, _activeVCam.ComputedRotation, t);

                if (_blendTime >= _blendDuration)
                    _isBlending = false;
            }
            else
            {
                targetPos = _activeVCam.ComputedPosition;
                targetRot = _activeVCam.ComputedRotation;
            }

            // Apply to the owning Transform
            var tr = Transform;
            tr.Position = new Vector3(targetPos.X, targetPos.Y, targetPos.Z);

            // Convert quaternion to euler angles
            var euler = QuaternionToEuler(targetRot);
            tr.Rotation = new Vector3(euler.X, euler.Y, euler.Z);
        }

        private static float SmoothStep(float t) => t * t * (3f - 2f * t);

        private static SN.Vector3 QuaternionToEuler(SN.Quaternion q)
        {
            float sinr_cosp = 2f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float pitch = MathF.Atan2(sinr_cosp, cosr_cosp) * (180f / MathF.PI);

            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float yaw;
            if (MathF.Abs(sinp) >= 1f)
                yaw = MathF.CopySign(90f, sinp);
            else
                yaw = MathF.Asin(sinp) * (180f / MathF.PI);

            float siny_cosp = 2f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float roll = MathF.Atan2(siny_cosp, cosy_cosp) * (180f / MathF.PI);

            return new SN.Vector3(pitch, yaw, roll);
        }
    }
}
