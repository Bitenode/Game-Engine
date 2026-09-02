using System;
using System.Linq;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Planet;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Unified underwater query for both planar Water and spherical planet oceans.
    /// </summary>
    public readonly struct UnderwaterState
    {
        public float Depth { get; init; }
        public SN.Vector3 Tint { get; init; }
        public float FogDensity { get; init; }
        public float CausticStrength { get; init; }
        public float Distortion { get; init; }
        public float Buoyancy { get; init; }
        public float Drag { get; init; }
    }

    public static class UnderwaterQuery
    {
        public static UnderwaterState? GetState(SN.Vector3 worldPos)
        {
            UnderwaterState? best = null;
            float bestDepth = 0f;

            var planarWater = Water.GetUnderwaterWater(worldPos);
            if (planarWater != null)
            {
                float surfaceY = planarWater.SampleHeight(worldPos.X, worldPos.Z);
                float depth = surfaceY - worldPos.Y;
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    best = new UnderwaterState
                    {
                        Depth = depth,
                        Tint = planarWater.UnderwaterTint,
                        FogDensity = planarWater.UnderwaterFogDensity,
                        CausticStrength = planarWater.UnderwaterCausticStrength,
                        Distortion = planarWater.UnderwaterDistortion,
                        Buoyancy = planarWater.UnderwaterBuoyancy,
                        Drag = planarWater.UnderwaterDrag
                    };
                }
            }

            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var planet = PlanetTerrain.ActivePlanets[i];
                if (planet?.gameObject == null || !planet.IsActiveAndEnabled || !planet.EnableWater || planet.Config == null)
                    continue;

                if (!PlanetSwimFxActive())
                    continue;

                var center = planet.GetWorldCenter();
                var toPos = worldPos - center;
                float distToCenter = toPos.Length();
                if (distToCenter <= 1e-5f)
                    continue;

                var dir = toPos / distToCenter;
                var waterSample = planet.SampleWaterSurface(dir);
                if (waterSample.Kind == PlanetWaterKind.Lava)
                    continue;

                float scale = planet.GetWorldRadiusScale();
                float waterLevelWorld = waterSample.Mask >= 0.04f ? waterSample.Radius * scale : 0f;
                if (waterLevelWorld < 1f)
                    continue;

                float crustWorld = planet.SampleCollisionRadius(dir);
                // Camera vs the water table at the crust — not the seabed and not
                // the swim capsule test (that only went true on the ocean floor).
                float depth = waterLevelWorld - distToCenter;
                if (depth < 0.28f || depth <= bestDepth)
                    continue;
                if (distToCenter < crustWorld - 2.5f)
                    continue;

                var tintSource = ResolveWaterTint(planet, waterSample);
                bestDepth = depth;
                best = new UnderwaterState
                {
                    Depth = depth,
                    Tint = new SN.Vector3(tintSource.UnderwaterTintR, tintSource.UnderwaterTintG, tintSource.UnderwaterTintB),
                    FogDensity = tintSource.UnderwaterFogDensity,
                    CausticStrength = tintSource.UnderwaterCausticStrength,
                    Distortion = tintSource.UnderwaterDistortion,
                    Buoyancy = tintSource.UnderwaterBuoyancy,
                    Drag = tintSource.UnderwaterDrag
                };
            }

            return best;
        }

        static Biome.BiomeDefinition ResolveWaterTint(PlanetTerrain planet, PlanetWaterSurfaceSample sample)
        {
            var config = planet.Config;
            if (config?.WaterBodies is { Length: > 0 }
                && sample.BodyIndex >= 0
                && sample.BodyIndex < config.WaterBodies.Length
                && sample.Kind is PlanetWaterKind.Ocean or PlanetWaterKind.Lake or PlanetWaterKind.Pond)
            {
                var body = config.WaterBodies[sample.BodyIndex];
                return new Biome.BiomeDefinition
                {
                    UnderwaterTintR = body.DeepR,
                    UnderwaterTintG = body.DeepG,
                    UnderwaterTintB = body.DeepB,
                    UnderwaterFogDensity = planet.OceanBiome.UnderwaterFogDensity,
                    UnderwaterCausticStrength = planet.OceanBiome.UnderwaterCausticStrength,
                    UnderwaterDistortion = planet.OceanBiome.UnderwaterDistortion,
                    UnderwaterBuoyancy = planet.OceanBiome.UnderwaterBuoyancy,
                    UnderwaterDrag = planet.OceanBiome.UnderwaterDrag
                };
            }

            return planet.OceanBiome;
        }

        /// <summary>
        /// Planet underwater post only while a player is actually diving/submerged.
        /// Scene-view cameras with no player still get camera-based water FX.
        /// </summary>
        public static bool PlanetSwimFxActive()
        {
            var players = SceneQuery.FindBehaviors<RigidbodyPlayer>();
            bool any = false;
            foreach (var p in players)
            {
                if (p == null || !p.IsActiveAndEnabled)
                    continue;
                any = true;
                if (p.IsPlanetSwimming && p.IsPlanetSubmerged)
                    return true;
            }
            return !any;
        }

        /// <summary>True when a live player is under the planet water surface.</summary>
        public static bool AnyPlayerPlanetSubmerged()
        {
            foreach (var p in SceneQuery.FindBehaviors<RigidbodyPlayer>())
            {
                if (p != null && p.IsActiveAndEnabled && p.IsPlanetSubmerged)
                    return true;
            }
            return false;
        }
    }
}
