using System;
using SN = System.Numerics;
using Game_Engine.Core;

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

            // Planet oceans.
            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var planet = PlanetTerrain.ActivePlanets[i];
                if (planet?.gameObject == null || !planet.IsActiveAndEnabled || !planet.EnableWater || planet.Config == null)
                    continue;

                var world = SceneGraphUtil.AccumulateWorld(planet.gameObject);
                var center = new SN.Vector3(world.M41, world.M42, world.M43);
                var toPos = worldPos - center;
                float distToCenter = toPos.Length();
                if (distToCenter <= 1e-5f)
                    continue;

                float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                float radiusScale = MathF.Max(0.0001f, (sx + sy + sz) / 3f);
                float seaLevelWorld = planet.Config.SeaLevel * radiusScale;
                float depth = seaLevelWorld - distToCenter;
                if (depth <= bestDepth)
                    continue;

                var ocean = planet.OceanBiome;
                bestDepth = depth;
                best = new UnderwaterState
                {
                    Depth = depth,
                    Tint = new SN.Vector3(ocean.UnderwaterTintR, ocean.UnderwaterTintG, ocean.UnderwaterTintB),
                    FogDensity = ocean.UnderwaterFogDensity,
                    CausticStrength = ocean.UnderwaterCausticStrength,
                    Distortion = ocean.UnderwaterDistortion,
                    Buoyancy = ocean.UnderwaterBuoyancy,
                    Drag = ocean.UnderwaterDrag
                };
            }

            return best;
        }
    }
}
