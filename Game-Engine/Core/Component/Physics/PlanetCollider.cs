using System;
using System.Linq;
using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Broad-phase AABB for the planet. Contact is handled by density
    /// raycast/spherecast on <see cref="PlanetTerrain"/> (caves and outer crust).
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
                    float scale = 1f;
                    if (gameObject != null)
                    {
                        var world = SceneGraphUtil.AccumulateWorld(gameObject);
                        float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                        float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                        float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                        scale = MathF.Max(0.0001f, (sx + sy + sz) / 3f);
                    }
                    return (pt.Config.Radius + maxAmp) * scale;
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
                if (pt?.Config == null) return 1000f;
                float scale = 1f;
                if (gameObject != null)
                {
                    var world = SceneGraphUtil.AccumulateWorld(gameObject);
                    float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                    float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                    float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                    scale = MathF.Max(0.0001f, (sx + sy + sz) / 3f);
                }
                return pt.Config.Radius * scale;
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
