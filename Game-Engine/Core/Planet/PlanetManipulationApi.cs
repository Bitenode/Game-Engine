using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Convenience scripting API for runtime planet digging/building.
/// </summary>
public static class PlanetManipulationApi
{
    public static bool DigSphere(SN.Vector3 worldCenter, float radius, float strength, float falloff = 0.6f, PlanetTerrain? planet = null)
    {
        var target = planet ?? FindNearestPlanet(worldCenter);
        if (target == null) return false;
        target.DigSphere(worldCenter, radius, strength, falloff);
        return true;
    }

    public static bool BuildSphere(SN.Vector3 worldCenter, float radius, float strength, float falloff = 0.6f, PlanetTerrain? planet = null)
    {
        var target = planet ?? FindNearestPlanet(worldCenter);
        if (target == null) return false;
        target.BuildSphere(worldCenter, radius, strength, falloff);
        return true;
    }

    public static PlanetTerrain? FindNearestPlanet(SN.Vector3 worldPos)
    {
        PlanetTerrain? best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
        {
            var planet = PlanetTerrain.ActivePlanets[i];
            var t = planet.gameObject?.Transform?.Position;
            var center = t == null
                ? SN.Vector3.Zero
                : new SN.Vector3((float)t.X, (float)t.Y, (float)t.Z);
            float d = SN.Vector3.DistanceSquared(center, worldPos);
            if (d < bestDist)
            {
                bestDist = d;
                best = planet;
            }
        }
        return best;
    }
}
