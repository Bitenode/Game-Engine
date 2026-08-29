using System;
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

            // Planar water volumes.
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

            // Planet oceans, lakes, ponds, and rivers.
            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var planet = PlanetTerrain.ActivePlanets[i];
                if (planet?.gameObject == null || !planet.IsActiveAndEnabled || !planet.EnableWater || planet.Config == null)
                    continue;

                var center = planet.GetWorldCenter();
                var toPos = worldPos - center;
                float distToCenter = toPos.Length();
                if (distToCenter <= 1e-5f)
                    continue;

                float radiusScale = planet.GetWorldRadiusScale();
                var dir = toPos / distToCenter;
                float crustWorld = planet.SampleHeightfieldRadius(dir);

                if (planet.TrySampleWorldDensity(worldPos, out float density))
                {
                    // Negative density is solid crust. Do not treat buried cameras as swimming.
                    if (density <= 0f)
                        continue;
                }

                var waterSample = planet.SampleWaterSurface(dir);
                if (waterSample.Mask < 0.2f)
                    continue;

                float waterLevelWorld = waterSample.Radius * radiusScale;
                float depth = waterLevelWorld - distToCenter;
                // Require a real submersion so looking down a bank / grazing the
                // surface cannot flip the full-screen underwater post.
                if (depth < 0.35f || depth <= bestDepth)
                    continue;

                // Must be in the open water column (above the bed, below the surface).
                // The old 32 m "clipped into water" test fired on dry slopes.
                if (distToCenter < crustWorld - 0.75f)
                    continue;
                if (crustWorld > waterLevelWorld + 0.5f)
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
    }
}
