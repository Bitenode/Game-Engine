#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Network transform component — synchronizes a GameObject's position,
    /// rotation, and scale over the network with smooth interpolation.
    /// Automatically sends updates when the local transform changes (on the authority side)
    /// and smoothly interpolates to received state (on the non-authority side).
    /// </summary>
    [Require(typeof(NetworkIdentity))]
    public sealed class NetworkTransform : Behavior
    {
        // ── Configuration ──
        /// <summary>Interpolation speed (higher = snappier, lower = smoother).</summary>
        [Persist] public float InterpolationSpeed { get; set; } = 15f;

        /// <summary>Minimum position change before sending an update (threshold).</summary>
        [Persist] public float PositionThreshold { get; set; } = 0.01f;

        /// <summary>Minimum rotation change (degrees) before sending an update.</summary>
        [Persist] public float RotationThreshold { get; set; } = 0.5f;

        /// <summary>Sync rate in updates per second.</summary>
        [Persist] public float SyncRate { get; set; } = 20f;

        /// <summary>Enable position syncing.</summary>
        [Persist] public bool SyncPosition { get; set; } = true;

        /// <summary>Enable rotation syncing.</summary>
        [Persist] public bool SyncRotation { get; set; } = true;

        /// <summary>Enable scale syncing.</summary>
        [Persist] public bool SyncScale { get; set; } = false;

        // ── Target state (received from network) ──
        private Vector3 _targetPosition;
        private Vector3 _targetRotation;
        private Vector3 _targetScale;
        private bool _hasTarget;

        // ── Last sent state (for delta detection) ──
        private Vector3 _lastSentPosition;
        private Vector3 _lastSentRotation;
        private float _sendTimer;

        public override void Start()
        {
            _targetPosition = Transform.Position;
            _targetRotation = Transform.Rotation;
            _targetScale = Transform.Scale;
            _lastSentPosition = Transform.Position;
            _lastSentRotation = Transform.Rotation;
        }

        public override void Update()
        {
            var identity = GetComponent<NetworkIdentity>();
            if (identity == null) return;

            if (identity.HasAuthority)
            {
                // Authority side: detect changes and mark dirty
                // (Actual sending is handled by NetworkManager.BroadcastState)
            }
            else if (_hasTarget)
            {
                // Non-authority side: interpolate toward the target state
                float t = Math.Clamp(InterpolationSpeed * Time.deltaTime, 0f, 1f);

                if (SyncPosition)
                    Transform.Position = LerpVector3(Transform.Position, _targetPosition, t);

                if (SyncRotation)
                    Transform.Rotation = LerpRotation(Transform.Rotation, _targetRotation, t);

                if (SyncScale)
                    Transform.Scale = LerpVector3(Transform.Scale, _targetScale, t);
            }
        }

        /// <summary>
        /// Set the target state from a network update.
        /// Called by NetworkIdentity.DeserializeState.
        /// </summary>
        internal void SetTargetState(Vector3 position, Vector3 rotation, Vector3 scale)
        {
            _targetPosition = position;
            _targetRotation = rotation;
            _targetScale = scale;
            _hasTarget = true;
        }

        private static Vector3 LerpVector3(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        private static Vector3 LerpRotation(Vector3 a, Vector3 b, float t)
        {
            // Lerp rotation with shortest-path handling for each axis
            return new Vector3(
                LerpAngle(a.X, b.X, t),
                LerpAngle(a.Y, b.Y, t),
                LerpAngle(a.Z, b.Z, t));
        }

        private static double LerpAngle(double a, double b, float t)
        {
            double diff = ((b - a + 540) % 360) - 180;
            return a + diff * t;
        }
    }
}
