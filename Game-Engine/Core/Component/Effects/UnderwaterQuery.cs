using SN = System.Numerics;

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

                var p = planet.gameObject.Transform.Position;
                var center = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
                var toPos = worldPos - center;
                float distToCenter = toPos.Length();
                if (distToCenter <= 1e-5f)
                    continue;

                float waterMask = planet.SampleWaterMask(toPos / distToCenter);
                if (waterMask < 0.35f)
                    continue;

                float depth = planet.Config.SeaLevel - distToCenter;
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
