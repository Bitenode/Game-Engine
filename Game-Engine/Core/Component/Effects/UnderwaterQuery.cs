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

                var center = planet.GetWorldCenter();
                var toPos = worldPos - center;
                float distToCenter = toPos.Length();
                if (distToCenter <= 1e-5f)
                    continue;

                float radiusScale = planet.GetWorldRadiusScale();
                float seaLevelWorld = planet.Config.SeaLevel * radiusScale;
                float depth = seaLevelWorld - distToCenter;
                if (depth <= bestDepth)
                    continue;

                var dir = toPos / distToCenter;
                float crustWorld = planet.SampleHeightfieldRadius(dir);
                // Flooded column: between the visible crust and the sea sphere.
                bool inOceanColumn = distToCenter >= crustWorld - 2f;
                // Sea often sits a little inside the heightfield (this planet ~16m).
                // Crossing that water mesh with the fly camera is still ocean;
                // deep interior / caves are not.
                bool clippedIntoSeaSphere = crustWorld > seaLevelWorld + 1f
                                            && distToCenter > seaLevelWorld - 32f;
                if (!inOceanColumn && !clippedIntoSeaSphere)
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
