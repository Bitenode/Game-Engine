using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    public enum LightType { Directional, Point, Spot }

    public sealed class Light : Behavior
    {
        // Directional uses the GameObject's Transform forward for direction
        [Persist] public LightType Type { get; set; } = LightType.Directional;

        // Multiplies the diffuse strength (1 = default)
        [Persist] public float Intensity { get; set; } = 1.0f;

        // For Point lights (simple falloff)
        [Persist] public float Range { get; set; } = 10f;

        // For Spot lights
        [Persist] public float InnerAngle { get; set; } = 25f;   // degrees
        [Persist] public float OuterAngle { get; set; } = 35f;   // degrees

        // Color tint (used by the renderer for light color)
        [Persist] public Color Color { get; set; } = Colors.White;

        // Shadow casting
        [Persist] public bool CastShadows { get; set; } = true;

        // ── Multi-light registry ──
        private static readonly List<Light> _allLights = new(16);
        public static IReadOnlyList<Light> AllLights => _allLights;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_allLights.Contains(this)) _allLights.Add(this);
        }

        public override void OnDisable()
        {
            _allLights.Remove(this);
            base.OnDisable();
        }

        /// <summary>Get the world-space direction this light points (for directional and spot).</summary>
        public System.Numerics.Vector3 GetWorldDirection()
        {
            float yaw = (float)Transform.Rotation.Y * MathF.PI / 180f;
            float pitch = (float)Transform.Rotation.X * MathF.PI / 180f;
            return new System.Numerics.Vector3(
                -MathF.Sin(yaw) * MathF.Cos(pitch),
                -MathF.Sin(pitch),
                -MathF.Cos(yaw) * MathF.Cos(pitch));
        }

        /// <summary>Get world-space position.</summary>
        public System.Numerics.Vector3 GetWorldPosition()
            => new((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

        /// <summary>Get light color as a normalized RGB vector.</summary>
        public System.Numerics.Vector3 GetColorRGB()
            => new(Color.R / 255f * Intensity, Color.G / 255f * Intensity, Color.B / 255f * Intensity);
    }
}
