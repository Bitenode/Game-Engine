using System;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Planet collider that uses per-point terrain height sampling for actual
    /// mesh-conforming collision. The gizmo draws at min/max terrain radii
    /// to show the collision shell. Actual collision is handled by
    /// <see cref="PlanetTerrain.SampleSurfaceRadius"/> in the physics components.
    /// </summary>
    [ComponentCategory("Physics")]
    public sealed class PlanetCollider : Collider
    {
        [Persist] public float RadiusOverride { get; set; } = 0f;

        /// <summary>Max possible radius (base + max amplitude). Used for AABB and outer gizmo ring.</summary>
        public float MaxRadius
        {
            get
            {
                if (RadiusOverride > 0f) return RadiusOverride;
                var pt = gameObject?.Behaviors.OfType<PlanetTerrain>().FirstOrDefault();
                if (pt?.Config != null)
                {
                    float maxAmp = 0f;
                    foreach (var b in pt.Config.Biomes)
                        maxAmp = Math.Max(maxAmp, b.HeightAmplitude);
                    return pt.Config.Radius + maxAmp;
                }
                return 1000f;
            }
        }

        /// <summary>Base planet radius (no terrain offset). Used for inner gizmo ring.</summary>
        public float BaseRadius
        {
            get
            {
                var pt = gameObject?.Behaviors.OfType<PlanetTerrain>().FirstOrDefault();
                return pt?.Config?.Radius ?? 1000f;
            }
        }

        /// <summary>Kept for backward compat -- returns MaxRadius.</summary>
        public float EffectiveRadius => MaxRadius;

        /// <summary>World-space center of the planet collider sphere.</summary>
        public SN.Vector3 WorldCenter
        {
            get
            {
                if (gameObject == null) return SN.Vector3.Zero;
                var W = SceneGraphUtil.AccumulateWorld(gameObject);
                return new SN.Vector3(W.M41, W.M42, W.M43);
            }
        }

        public override AABB GetWorldAABB()
        {
            var center = WorldCenter;
            float r = MaxRadius;
            var ext = new SN.Vector3(r, r, r);
            return new AABB(center - ext, center + ext);
        }
    }
}
