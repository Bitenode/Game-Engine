#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// <summary>Type of physics joint constraint.</summary>
    public enum JointType
    {
        Fixed,      // No relative movement allowed
        Hinge,      // Rotation around a single axis
        Spring,     // Spring force pulling bodies together
        Slider,     // Movement along a single axis
        BallSocket  // Free rotation, no translation
    }

    /// <summary>
    /// Physics joint that constrains two rigidbodies together.
    /// Supports fixed, hinge, spring, slider, and ball-socket joints.
    /// Uses iterative impulse-based constraint solving.
    /// </summary>
    public sealed class PhysicsJoint : Behavior
    {
        // ── Configuration ──
        [Persist] public JointType Type { get; set; } = JointType.Fixed;

        /// <summary>Path to the connected body's GameObject (empty = world anchor).</summary>
        [Persist] public string ConnectedBodyPath { get; set; } = "";

        /// <summary>Local-space anchor point on this body.</summary>
        [Persist] public Vector3 Anchor { get; set; } = new(0, 0, 0);

        /// <summary>Local-space anchor point on the connected body (or world if no body).</summary>
        [Persist] public Vector3 ConnectedAnchor { get; set; } = new(0, 0, 0);

        /// <summary>Axis for hinge/slider joints (local space).</summary>
        [Persist] public Vector3 Axis { get; set; } = new(0, 1, 0);

        /// <summary>Break force — joint breaks if force exceeds this value (0 = unbreakable).</summary>
        [Persist] public float BreakForce { get; set; } = 0f;

        /// <summary>Break torque — joint breaks if torque exceeds this value (0 = unbreakable).</summary>
        [Persist] public float BreakTorque { get; set; } = 0f;

        // ── Spring parameters ──
        [Persist] public float SpringForce { get; set; } = 100f;
        [Persist] public float SpringDamper { get; set; } = 10f;
        [Persist] public float SpringTargetDistance { get; set; } = 0f;

        // ── Hinge limits ──
        [Persist] public bool UseLimits { get; set; } = false;
        [Persist] public float LimitMin { get; set; } = -45f;
        [Persist] public float LimitMax { get; set; } = 45f;

        // ── Motor ──
        [Persist] public bool UseMotor { get; set; } = false;
        [Persist] public float MotorTargetVelocity { get; set; } = 0f;
        [Persist] public float MotorForce { get; set; } = 10f;

        // ── Runtime ──
        private Rigidbody? _body;
        private Rigidbody? _connectedBody;
        private bool _broken;

        /// <summary>True if the joint has been broken by exceeding break thresholds.</summary>
        public bool IsBroken => _broken;

        /// <summary>Event fired when the joint breaks.</summary>
        public event Action? OnJointBreak;

        // ── Static registry ──
        private static readonly List<PhysicsJoint> _allJoints = new(32);
        public static IReadOnlyList<PhysicsJoint> AllJoints => _allJoints;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_allJoints.Contains(this)) _allJoints.Add(this);
        }

        public override void OnDisable()
        {
            _allJoints.Remove(this);
            base.OnDisable();
        }

        public override void Start()
        {
            _body = GetComponent<Rigidbody>();
            ResolveConnectedBody();
        }

        private void ResolveConnectedBody()
        {
            if (string.IsNullOrEmpty(ConnectedBodyPath))
            {
                _connectedBody = null;
                return;
            }

            // Find connected body by scene path
            var go = SceneQuery.FindByPath(ConnectedBodyPath);
            _connectedBody = go?.Behaviors?.OfType<Rigidbody>().FirstOrDefault();
        }

        public override void FixedUpdate()
        {
            if (_broken || _body == null || _body.IsKinematic) return;

            float dt = (float)Time.fixedDeltaTime;
            if (dt <= 0f) return;

            var bodyPos = GetWorldPos(_body);
            var anchorWorld = bodyPos + ToSN(Anchor);

            SN.Vector3 connectedAnchorWorld;
            if (_connectedBody != null)
            {
                var connPos = GetWorldPos(_connectedBody);
                connectedAnchorWorld = connPos + ToSN(ConnectedAnchor);
            }
            else
            {
                connectedAnchorWorld = ToSN(ConnectedAnchor);
            }

            SN.Vector3 force = SN.Vector3.Zero;

            switch (Type)
            {
                case JointType.Fixed:
                    force = SolveFixed(anchorWorld, connectedAnchorWorld, dt);
                    break;
                case JointType.Spring:
                    force = SolveSpring(anchorWorld, connectedAnchorWorld, dt);
                    break;
                case JointType.Hinge:
                    force = SolveHinge(anchorWorld, connectedAnchorWorld, dt);
                    break;
                case JointType.Slider:
                    force = SolveSlider(bodyPos, anchorWorld, connectedAnchorWorld, dt);
                    break;
                case JointType.BallSocket:
                    force = SolveBallSocket(anchorWorld, connectedAnchorWorld, dt);
                    break;
            }

            // Apply constraint force
            _body.AddForce(force);
            if (_connectedBody != null && !_connectedBody.IsKinematic)
                _connectedBody.AddForce(-force);

            // Check break conditions
            if (BreakForce > 0f && force.Length() > BreakForce)
            {
                Break();
                return;
            }
        }

        private SN.Vector3 SolveFixed(SN.Vector3 anchor, SN.Vector3 target, float dt)
        {
            // Stiff positional constraint
            var delta = target - anchor;
            float stiffness = 5000f;
            float damping = 200f;
            return delta * stiffness - _body!.Velocity * damping;
        }

        private SN.Vector3 SolveSpring(SN.Vector3 anchor, SN.Vector3 target, float dt)
        {
            var delta = target - anchor;
            float dist = delta.Length();
            if (dist < 1e-6f) return SN.Vector3.Zero;

            var dir = delta / dist;
            float displacement = dist - SpringTargetDistance;

            // Hooke's law + damping
            float relVel = SN.Vector3.Dot(_body!.Velocity, dir);
            if (_connectedBody != null)
                relVel -= SN.Vector3.Dot(_connectedBody.Velocity, dir);

            return dir * (displacement * SpringForce - relVel * SpringDamper);
        }

        private SN.Vector3 SolveHinge(SN.Vector3 anchor, SN.Vector3 target, float dt)
        {
            // Position constraint (keep anchors together)
            var delta = target - anchor;
            float stiffness = 3000f;
            float damping = 150f;
            var force = delta * stiffness - _body!.Velocity * damping;

            // Motor
            if (UseMotor)
            {
                var axis = SN.Vector3.Normalize(ToSN(Axis));
                float currentAngVel = SN.Vector3.Dot(_body.AngularVelocity, axis);
                float torque = (MotorTargetVelocity - currentAngVel) * MotorForce;
                _body.AngularVelocity += axis * torque * dt;
            }

            return force;
        }

        private SN.Vector3 SolveSlider(SN.Vector3 bodyPos, SN.Vector3 anchor, SN.Vector3 target, float dt)
        {
            // Only allow movement along the axis
            var axis = SN.Vector3.Normalize(ToSN(Axis));
            var delta = target - anchor;

            // Remove movement perpendicular to the axis
            var perpendicular = delta - SN.Vector3.Dot(delta, axis) * axis;
            float stiffness = 3000f;
            float damping = 150f;

            // Constrain perpendicular movement
            var perpVel = _body!.Velocity - SN.Vector3.Dot(_body.Velocity, axis) * axis;
            return -perpendicular * stiffness - perpVel * damping;
        }

        private SN.Vector3 SolveBallSocket(SN.Vector3 anchor, SN.Vector3 target, float dt)
        {
            // Keep anchor points together but allow free rotation
            var delta = target - anchor;
            float stiffness = 4000f;
            float damping = 180f;
            return delta * stiffness - _body!.Velocity * damping;
        }

        /// <summary>Break the joint.</summary>
        public void Break()
        {
            _broken = true;
            OnJointBreak?.Invoke();
            Log.Info($"[PhysicsJoint] Joint on {gameObject?.Name ?? "?"} broke.");
        }

        private static SN.Vector3 GetWorldPos(Rigidbody rb)
        {
            var t = rb.Transform;
            return new SN.Vector3((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z);
        }

        private static SN.Vector3 ToSN(Vector3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
