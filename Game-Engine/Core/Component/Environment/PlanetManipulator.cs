using System;
using System.Linq;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

public enum PlanetManipulationMode
{
    Dig,
    Build
}

[ComponentCategory("Environment")]
public sealed class PlanetManipulator : Behavior
{
    [Persist] public PlanetManipulationMode Mode { get; set; } = PlanetManipulationMode.Dig;
    [Persist] public float Radius { get; set; } = 12f;
    [Persist] public float Strength { get; set; } = 10f;
    [Persist] public float Falloff { get; set; } = 0.6f;
    [Persist] public float MaxRatePerSecond { get; set; } = 8f;
    [Persist] public bool AutoApply { get; set; } = false;

    float _cooldown;

    public override void Update()
    {
        if (!AutoApply) return;

        _cooldown -= Math.Max(0f, (float)Time.deltaTime);
        if (_cooldown > 0f) return;

        var target = ResolveTargetPlanet();
        if (target == null) return;

        var p = gameObject?.Transform?.Position;
        if (p == null) return;
        var origin = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
        var center = target.GetWorldCenter();
        var toCenter = center - origin;
        float maxDist = toCenter.Length() + Math.Max(target.Radius, 1f) * 4f;
        if (maxDist < 1f) maxDist = Math.Max(target.Radius, 1f) * 8f;

        PlanetDensityHit hit;
        bool gotHit = false;
        if (toCenter.LengthSquared() > 1e-6f)
        {
            var inward = SN.Vector3.Normalize(toCenter);
            gotHit = target.Raycast(origin, inward, maxDist, out hit)
                     || target.Raycast(origin, -inward, maxDist, out hit);
        }
        else
        {
            gotHit = target.Raycast(origin, SN.Vector3.UnitY, maxDist, out hit)
                     || target.Raycast(origin, -SN.Vector3.UnitY, maxDist, out hit);
        }

        if (!gotHit) return;
        ApplyAt(hit.Point, target);
    }

    public void ApplyAt(SN.Vector3 worldPos)
    {
        var target = ResolveTargetPlanet();
        if (target == null) return;
        ApplyAt(worldPos, target);
    }

    public void DigAt(SN.Vector3 worldPos, float? radius = null, float? strength = null)
    {
        var target = ResolveTargetPlanet();
        if (target == null) return;
        target.DigSphere(worldPos, Math.Max(0.05f, radius ?? Radius), Math.Max(0.01f, strength ?? Strength), Falloff);
        ConsumeRateBudget();
    }

    public void BuildAt(SN.Vector3 worldPos, float? radius = null, float? strength = null)
    {
        var target = ResolveTargetPlanet();
        if (target == null) return;
        target.BuildSphere(worldPos, Math.Max(0.05f, radius ?? Radius), Math.Max(0.01f, strength ?? Strength), Falloff);
        ConsumeRateBudget();
    }

    public void ApplyAt(SN.Vector3 worldPos, PlanetTerrain target)
    {
        float radius = Math.Max(0.05f, Radius);
        float strength = Math.Max(0.01f, Strength);
        if (Mode == PlanetManipulationMode.Dig)
            target.DigSphere(worldPos, radius, strength, Falloff);
        else
            target.BuildSphere(worldPos, radius, strength, Falloff);
        ConsumeRateBudget();
    }

    void ConsumeRateBudget()
    {
        float rate = Math.Max(0.1f, MaxRatePerSecond);
        _cooldown = 1f / rate;
    }

    PlanetTerrain? ResolveTargetPlanet()
    {
        var local = gameObject?.Behaviors.OfType<PlanetTerrain>().FirstOrDefault();
        if (local != null) return local;

        if (PlanetTerrain.ActivePlanets.Count == 0) return null;

        var p = gameObject?.Transform.Position;
        if (p == null)
            return PlanetTerrain.ActivePlanets[0];

        var origin = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
        PlanetTerrain? best = null;
        float bestDist = float.MaxValue;
        foreach (var planet in PlanetTerrain.ActivePlanets)
        {
            var centre = planet.gameObject?.Transform.Position;
            SN.Vector3 c = centre == null
                ? SN.Vector3.Zero
                : new SN.Vector3((float)centre.X, (float)centre.Y, (float)centre.Z);
            float d = SN.Vector3.DistanceSquared(c, origin);
            if (d < bestDist)
            {
                bestDist = d;
                best = planet;
            }
        }

        return best;
    }
}
