#nullable enable
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Audio listener component — represents the "ears" in the scene.
    /// Typically attached to the main camera's GameObject.
    /// Only one AudioListener should be active at a time.
    /// </summary>
    [ComponentCategory("Audio")]
    public sealed class AudioListener : Behavior
    {
        /// <summary>Master volume for this listener (0..1).</summary>
        [Persist] public float Volume { get; set; } = 1f;

        public override void OnEnable()
        {
            base.OnEnable();
            AudioManager.SetListener(this);
        }

        public override void OnDisable()
        {
            AudioManager.ClearListener(this);
            base.OnDisable();
        }

        /// <summary>Get world position of this listener.</summary>
        public SN.Vector3 GetWorldPosition()
            => new((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

        /// <summary>Get world-space right direction for stereo panning.</summary>
        public SN.Vector3 GetWorldRight()
        {
            // Compute right from rotation
            float yaw = (float)Transform.Rotation.Y * MathF.PI / 180f;
            return new SN.Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        }

        /// <summary>Get world-space forward direction.</summary>
        public SN.Vector3 GetWorldForward()
        {
            float yaw = (float)Transform.Rotation.Y * MathF.PI / 180f;
            return new SN.Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        }
    }
}
