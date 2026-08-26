using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>World-space contact against the volumetric planet density field.</summary>
public struct PlanetDensityHit
{
    public SN.Vector3 Point;
    public SN.Vector3 Normal;
    public float Distance;
    public bool StartedInside;
}

/// <summary>
/// Sphere-marches <see cref="PlanetDensitySampler.SampleDensity"/> (same field as meshing).
/// Works on outer crust, cave floors/walls/ceilings, and any hemisphere.
/// </summary>
public static class PlanetDensityRaycast
{
    const int MaxSteps = 96;
    const int RefineIters = 10;

    public static bool Raycast(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float maxWorldDistance,
        out PlanetDensityHit hit)
        => Spherecast(sampler, planetCenter, worldScale, worldOrigin, worldDirection, 0f, maxWorldDistance, out hit);

    public static bool Spherecast(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float worldRadius,
        float maxWorldDistance,
        out PlanetDensityHit hit)
    {
        hit = default;
        float lenSq = worldDirection.LengthSquared();
        if (lenSq < 1e-12f)
            return false;

        float scale = PlanetSpace.SanitizeScale(worldScale);
        var localOrigin = PlanetSpace.WorldToLocal(worldOrigin, planetCenter, scale);
        var localDir = worldDirection / MathF.Sqrt(lenSq);
        float maxLocal = PlanetSpace.WorldToLocalLength(MathF.Max(0f, maxWorldDistance), scale);
        float localRadius = PlanetSpace.WorldToLocalLength(MathF.Max(0f, worldRadius), scale);

        if (!March(sampler, localOrigin, localDir, localRadius, maxLocal, out float tLocal, out bool inside))
            return false;

        var localPoint = localOrigin + localDir * tLocal;
        var localNormal = sampler.SampleDensityGradient(localPoint);
        if (SN.Vector3.Dot(localNormal, localDir) > 0f)
            localNormal = -localNormal;

        hit = new PlanetDensityHit
        {
            Point = PlanetSpace.LocalToWorld(localPoint, planetCenter, scale),
            Normal = localNormal,
            Distance = PlanetSpace.LocalToWorldLength(tLocal, scale),
            StartedInside = inside
        };
        return true;
    }

    /// <summary>
    /// First air→solid crossing walking inward along <paramref name="sphereDir"/>,
    /// including painted pits and cave mouths open to space.
    /// </summary>
    public static bool TrySampleLocalIsosurface(
        PlanetDensitySampler sampler,
        PlanetConfig config,
        SN.Vector3 sphereDir,
        out SN.Vector3 localPoint,
        out SN.Vector3 localNormal)
    {
        localPoint = SN.Vector3.Zero;
        localNormal = SN.Vector3.UnitY;
        if (sphereDir.LengthSquared() < 1e-12f)
            return false;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        float maxAmp = DensityGenerator.MaxAmplitude(config);
        float cave = DensityGenerator.MaxCaveDepth(config);
        float iso = MathF.Max(16f, config.VoxelIsoSearchRange);
        float outerR = config.Radius + maxAmp + iso + 32f;
        float maxDist = maxAmp + cave + iso + MathF.Max(config.CaveDepth, 32f) + 64f;

        var origin = sphereDir * outerR;
        if (!March(sampler, origin, -sphereDir, 0f, maxDist, out float t, out _))
            return false;

        localPoint = origin - sphereDir * t;
        localNormal = sampler.SampleDensityGradient(localPoint);
        if (SN.Vector3.Dot(localNormal, sphereDir) < 0f)
            localNormal = -localNormal;
        return true;
    }

    public static bool ResolvePenetration(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        ref SN.Vector3 worldPos,
        float worldClearance)
    {
        float scale = PlanetSpace.SanitizeScale(worldScale);
        var local = PlanetSpace.WorldToLocal(worldPos, planetCenter, scale);
        float clearance = PlanetSpace.WorldToLocalLength(MathF.Max(0f, worldClearance), scale);
        bool moved = false;

        for (int i = 0; i < 10; i++)
        {
            float d = sampler.SampleDensity(local);
            if (d >= clearance)
                break;

            var n = sampler.SampleDensityGradient(local);
            float push = clearance - d + 0.02f;
            local += n * push;
            moved = true;
        }

        if (moved)
            worldPos = PlanetSpace.LocalToWorld(local, planetCenter, scale);
        return moved;
    }

    static bool March(
        PlanetDensitySampler sampler,
        SN.Vector3 origin,
        SN.Vector3 dir,
        float radius,
        float maxDist,
        out float hitT,
        out bool startedInside)
    {
        hitT = 0f;
        float d0 = sampler.SampleDensity(origin) - radius;
        startedInside = d0 <= 0f;
        if (startedInside)
        {
            hitT = 0f;
            return true;
        }

        if (maxDist <= 1e-8f)
            return false;

        float minStep = MathF.Max(0.08f, radius * 0.15f);
        float maxStep = MathF.Max(1.5f, maxDist / 24f);
        float t = 0f;
        float prevD = d0;
        float prevT = 0f;

        for (int i = 0; i < MaxSteps && t < maxDist; i++)
        {
            float step = Math.Clamp(MathF.Abs(prevD), minStep, maxStep);
            float nextT = MathF.Min(maxDist, t + step);
            var p = origin + dir * nextT;
            float d = sampler.SampleDensity(p) - radius;

            if (prevD > 0f && d <= 0f)
            {
                hitT = Refine(sampler, origin, dir, radius, prevT, nextT);
                return true;
            }

            prevD = d;
            prevT = nextT;
            t = nextT;
        }

        return false;
    }

    static float Refine(
        PlanetDensitySampler sampler,
        SN.Vector3 origin,
        SN.Vector3 dir,
        float radius,
        float tAir,
        float tSolid)
    {
        float lo = tAir;
        float hi = tSolid;
        for (int i = 0; i < RefineIters; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float d = sampler.SampleDensity(origin + dir * mid) - radius;
            if (d <= 0f) hi = mid;
            else lo = mid;
        }
        return (lo + hi) * 0.5f;
    }
}
