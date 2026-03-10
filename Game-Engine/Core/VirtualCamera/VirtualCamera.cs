#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.VirtualCamera
{
    /// <summary>Body module type for the virtual camera.</summary>
    public enum BodyMode
    {
        FixedPosition,
        FollowTarget,
        OrbitalFollow,
        DollyTrack
    }

    /// <summary>Aim module type for the virtual camera.</summary>
    public enum AimMode
    {
        None,
        LookAtTarget,
        ComposerAim,
        FreeLook
    }

    /// <summary>
    /// Virtual Camera component. Defines camera position/rotation behavior
    /// through body and aim modules. The CameraBrain blends between active
    /// virtual cameras based on priority.
    /// </summary>
    public sealed class VirtualCamera : Behavior
    {
        // ── Priority ──
        /// <summary>Higher priority cameras take precedence. Default = 10.</summary>
        [Persist] public int Priority { get; set; } = 10;

        // ── Body Settings ──
        [Persist] public BodyMode Body { get; set; } = BodyMode.FollowTarget;

        /// <summary>Target GameObject name to follow/look at.</summary>
        [Persist] public string TargetName { get; set; } = "";

        /// <summary>Offset from the target in local space.</summary>
        [Persist] public Vector3 FollowOffset { get; set; } = new Vector3(0, 5, -10);

        /// <summary>Damping factor for follow movement (0 = instant, higher = smoother).</summary>
        [Persist] public float FollowDamping { get; set; } = 2f;

        // Orbital settings
        [Persist] public float OrbitalDistance { get; set; } = 10f;
        [Persist] public float OrbitalYaw { get; set; } = 0f;
        [Persist] public float OrbitalPitch { get; set; } = 30f;
        [Persist] public float OrbitalDamping { get; set; } = 2f;

        /// <summary>Name of the DollyPath component to follow.</summary>
        [Persist] public string DollyPathName { get; set; } = "";
        [Persist] public float DollyPosition { get; set; } = 0f;
        [Persist] public float DollySpeed { get; set; } = 1f;

        // ── Aim Settings ──
        [Persist] public AimMode Aim { get; set; } = AimMode.LookAtTarget;

        /// <summary>Screen-space target position for ComposerAim (0-1 range, 0.5 = center).</summary>
        [Persist] public float ComposerScreenX { get; set; } = 0.5f;
        [Persist] public float ComposerScreenY { get; set; } = 0.4f;

        /// <summary>Aim damping (0 = instant snap, higher = smoother rotation).</summary>
        [Persist] public float AimDamping { get; set; } = 3f;

        // ── Blend Settings ──
        [Persist] public float BlendDuration { get; set; } = 1f;

        // ── Runtime State ──
        private SN.Vector3 _currentPosition;
        private SN.Quaternion _currentRotation = SN.Quaternion.Identity;
        private bool _initialized;

        /// <summary>The computed world position for this virtual camera.</summary>
        public SN.Vector3 ComputedPosition => _currentPosition;

        /// <summary>The computed world rotation for this virtual camera.</summary>
        public SN.Quaternion ComputedRotation => _currentRotation;

        // ── Registry ──
        private static readonly System.Collections.Generic.List<VirtualCamera> _allVCams = new(8);
        public static System.Collections.Generic.IReadOnlyList<VirtualCamera> All => _allVCams;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_allVCams.Contains(this)) _allVCams.Add(this);
            _allVCams.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public override void OnDisable()
        {
            _allVCams.Remove(this);
            base.OnDisable();
        }

        public static void ClearAll() => _allVCams.Clear();

        public override void LateUpdate()
        {
            var target = FindTarget();
            SN.Vector3 targetPos = target != null
                ? new SN.Vector3((float)target.Transform.Position.X, (float)target.Transform.Position.Y, (float)target.Transform.Position.Z)
                : SN.Vector3.Zero;

            if (!_initialized)
            {
                _currentPosition = ComputeBodyPosition(targetPos);
                _initialized = true;
            }

            // Body
            SN.Vector3 desiredPos = ComputeBodyPosition(targetPos);
            float bodyDamp = GetBodyDamping();
            if (bodyDamp > 0.001f)
                _currentPosition = SN.Vector3.Lerp(_currentPosition, desiredPos, 1f - MathF.Exp(-bodyDamp * Time.deltaTime));
            else
                _currentPosition = desiredPos;

            // Aim
            SN.Quaternion desiredRot = ComputeAimRotation(targetPos);
            float aimDamp = AimDamping;
            if (aimDamp > 0.001f)
                _currentRotation = SN.Quaternion.Slerp(_currentRotation, desiredRot, 1f - MathF.Exp(-aimDamp * Time.deltaTime));
            else
                _currentRotation = desiredRot;
        }

        private SN.Vector3 ComputeBodyPosition(SN.Vector3 targetPos)
        {
            switch (Body)
            {
                case BodyMode.FixedPosition:
                    return new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

                case BodyMode.FollowTarget:
                    return targetPos + new SN.Vector3((float)FollowOffset.X, (float)FollowOffset.Y, (float)FollowOffset.Z);

                case BodyMode.OrbitalFollow:
                    float yawRad = OrbitalYaw * MathF.PI / 180f;
                    float pitchRad = OrbitalPitch * MathF.PI / 180f;
                    float x = OrbitalDistance * MathF.Cos(pitchRad) * MathF.Sin(yawRad);
                    float y = OrbitalDistance * MathF.Sin(pitchRad);
                    float z = OrbitalDistance * MathF.Cos(pitchRad) * MathF.Cos(yawRad);
                    return targetPos + new SN.Vector3(x, y, z);

                case BodyMode.DollyTrack:
                    var dolly = DollyPath.FindByName(DollyPathName);
                    if (dolly != null)
                    {
                        DollyPosition += DollySpeed * Time.deltaTime;
                        if (dolly.IsLoop)
                            DollyPosition %= 1f;
                        else
                            DollyPosition = Math.Clamp(DollyPosition, 0f, 1f);
                        return dolly.Evaluate(DollyPosition);
                    }
                    return new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

                default:
                    return SN.Vector3.Zero;
            }
        }

        private float GetBodyDamping()
        {
            return Body switch
            {
                BodyMode.FollowTarget => FollowDamping,
                BodyMode.OrbitalFollow => OrbitalDamping,
                _ => 0f
            };
        }

        private SN.Quaternion ComputeAimRotation(SN.Vector3 targetPos)
        {
            switch (Aim)
            {
                case AimMode.LookAtTarget:
                case AimMode.ComposerAim:
                    SN.Vector3 dir = targetPos - _currentPosition;
                    if (dir.LengthSquared() < 0.0001f) return _currentRotation;
                    dir = SN.Vector3.Normalize(dir);
                    return LookRotation(dir, SN.Vector3.UnitY);

                case AimMode.FreeLook:
                case AimMode.None:
                default:
                    return _currentRotation;
            }
        }

        private GameObject? FindTarget()
        {
            if (string.IsNullOrEmpty(TargetName)) return null;
            foreach (var root in SceneService.Root)
            {
                var found = FindByName(root, TargetName);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject? FindByName(GameObject go, string name)
        {
            if (go.Name == name) return go;
            foreach (var child in go.Children)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static SN.Quaternion LookRotation(SN.Vector3 forward, SN.Vector3 up)
        {
            forward = SN.Vector3.Normalize(forward);
            up = up.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(up) : SN.Vector3.UnitY;

            float alignment = MathF.Abs(SN.Vector3.Dot(forward, up));
            if (alignment > 0.999f)
            {
                var alt = MathF.Abs(forward.Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                up = SN.Vector3.Normalize(alt - forward * SN.Vector3.Dot(alt, forward));
            }

            var right = SN.Vector3.Cross(up, forward);
            if (right.LengthSquared() <= 1e-8f)
                right = SN.Vector3.Cross(SN.Vector3.UnitX, forward);
            right = SN.Vector3.Normalize(right);
            up = SN.Vector3.Cross(forward, right);

            var m = new SN.Matrix4x4(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0,
                0, 0, 0, 1);
            return SN.Quaternion.CreateFromRotationMatrix(m);
        }
    }
}
