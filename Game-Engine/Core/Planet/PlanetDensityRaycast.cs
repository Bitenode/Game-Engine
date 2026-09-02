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
/// March budget for <see cref="PlanetDensityRaycast"/>. Player probes use
/// <see cref="Gameplay"/>; editor picking keeps <see cref="Editor"/>.
/// </summary>
public readonly struct PlanetDensityProbeQuality
{
    public readonly int MaxSteps;
    public readonly int RefineIters;

    public PlanetDensityProbeQuality(int maxSteps, int refineIters)
    {
        MaxSteps = maxSteps < 1 ? 1 : maxSteps;
        RefineIters = refineIters < 1 ? 1 : refineIters;
    }

    public static PlanetDensityProbeQuality Editor { get; } = new(96, 10);
    public static PlanetDensityProbeQuality Gameplay { get; } = new(32, 4);

    public static PlanetDensityProbeQuality Resolve(in PlanetDensityProbeQuality quality)
        => quality.MaxSteps <= 0 ? Editor : quality;
}

/// <summary>
/// Sphere-marches <see cref="PlanetDensitySampler.SampleDensity"/> (same field as meshing).
/// Works on outer crust, cave floors/walls/ceilings, and any hemisphere.
/// </summary>
public static class PlanetDensityRaycast
{
    public static bool Raycast(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float maxWorldDistance,
        out PlanetDensityHit hit,
        PlanetDensityProbeQuality quality = default)
        => Spherecast(sampler, planetCenter, worldScale, worldOrigin, worldDirection, 0f, maxWorldDistance, out hit, quality);

    public static bool Spherecast(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float worldRadius,
        float maxWorldDistance,
        out PlanetDensityHit hit,
        PlanetDensityProbeQuality quality = default)
    {
        hit = default;
        float lenSq = worldDirection.LengthSquared();
        if (lenSq < 1e-12f)
            return false;

        quality = PlanetDensityProbeQuality.Resolve(quality);
        float scale = PlanetSpace.SanitizeScale(worldScale);
        var localOrigin = PlanetSpace.WorldToLocal(worldOrigin, planetCenter, scale);
        var localDir = worldDirection / MathF.Sqrt(lenSq);
        float maxLocal = PlanetSpace.WorldToLocalLength(MathF.Max(0f, maxWorldDistance), scale);
        float localRadius = PlanetSpace.WorldToLocalLength(MathF.Max(0f, worldRadius), scale);

        if (!March(sampler, localOrigin, localDir, localRadius, maxLocal, quality, out float tLocal, out bool inside))
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
        out SN.Vector3 localNormal,
        PlanetDensityProbeQuality quality = default)
    {
        localPoint = SN.Vector3.Zero;
        localNormal = SN.Vector3.UnitY;
        if (sphereDir.LengthSquared() < 1e-12f)
            return false;

        quality = PlanetDensityProbeQuality.Resolve(quality);
        sphereDir = SN.Vector3.Normalize(sphereDir);
        float maxAmp = DensityGenerator.MaxAmplitude(config);
        float cave = DensityGenerator.MaxCaveDepth(config);
        float iso = MathF.Max(16f, config.VoxelIsoSearchRange);
        float outerR = config.Radius + maxAmp + iso + 32f;
        float maxDist = maxAmp + cave + iso + MathF.Max(config.CaveDepth, 32f) + 64f;

        var origin = sphereDir * outerR;
        if (!March(sampler, origin, -sphereDir, 0f, maxDist, quality, out float t, out _))
            return false;

        localPoint = origin - sphereDir * t;
        localNormal = sampler.SampleDensityGradient(localPoint);
        if (SN.Vector3.Dot(localNormal, sphereDir) < 0f)
            localNormal = -localNormal;
        return true;
    }

    /// <summary>
    /// First density zero-crossing along the ray. Unlike <see cref="Raycast"/>,
    /// does not treat "origin already in rock" as a hit at t=0.
    /// </summary>
    public static bool RaycastIsoCrossing(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        SN.Vector3 worldOrigin,
        SN.Vector3 worldDirection,
        float maxWorldDistance,
        out PlanetDensityHit hit,
        PlanetDensityProbeQuality quality = default)
    {
        hit = default;
        float lenSq = worldDirection.LengthSquared();
        if (lenSq < 1e-12f)
            return false;

        quality = PlanetDensityProbeQuality.Resolve(quality);
        float scale = PlanetSpace.SanitizeScale(worldScale);
        var localOrigin = PlanetSpace.WorldToLocal(worldOrigin, planetCenter, scale);
        var localDir = worldDirection / MathF.Sqrt(lenSq);
        float maxLocal = PlanetSpace.WorldToLocalLength(MathF.Max(0f, maxWorldDistance), scale);

        float d0 = sampler.SampleDensity(localOrigin);
        bool inside = d0 <= 0f;
        if (!MarchIso(sampler, localOrigin, localDir, maxLocal, quality, out float tLocal))
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

    static bool MarchIso(
        PlanetDensitySampler sampler,
        SN.Vector3 origin,
        SN.Vector3 dir,
        float maxDist,
        PlanetDensityProbeQuality quality,
        out float hitT)
    {
        hitT = 0f;
        if (maxDist <= 1e-8f)
            return false;

        float d0 = sampler.SampleDensity(origin);
        float minStep = 0.08f;
        float maxStep = MathF.Max(1.5f, maxDist / 32f);
        float t = 0f;
        float prevD = d0;
        float prevT = 0f;

        for (int i = 0; i < quality.MaxSteps && t < maxDist; i++)
        {
            float step = Math.Clamp(MathF.Abs(prevD) * 0.35f + 0.05f, minStep, maxStep);
            float nextT = MathF.Min(maxDist, t + step);
            var p = origin + dir * nextT;
            float d = sampler.SampleDensity(p);

            if ((prevD > 0f && d <= 0f) || (prevD <= 0f && d > 0f))
            {
                hitT = RefineIso(sampler, origin, dir, prevT, nextT, prevD > 0f, quality);
                return true;
            }

            prevD = d;
            prevT = nextT;
            t = nextT;
        }

        return false;
    }

    static float RefineIso(
        PlanetDensitySampler sampler,
        SN.Vector3 origin,
        SN.Vector3 dir,
        float t0,
        float t1,
        bool airToSolid,
        PlanetDensityProbeQuality quality)
    {
        float lo = t0;
        float hi = t1;
        for (int i = 0; i < quality.RefineIters; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float d = sampler.SampleDensity(origin + dir * mid);
            bool solid = d <= 0f;
            if (airToSolid)
            {
                if (solid) hi = mid;
                else lo = mid;
            }
            else
            {
                if (solid) lo = mid;
                else hi = mid;
            }
        }
        return (lo + hi) * 0.5f;
    }

    public static bool ResolvePenetration(
        PlanetDensitySampler sampler,
        SN.Vector3 planetCenter,
        float worldScale,
        ref SN.Vector3 worldPos,
        float worldClearance,
        int maxIters = 10)
    {
        float scale = PlanetSpace.SanitizeScale(worldScale);
        var local = PlanetSpace.WorldToLocal(worldPos, planetCenter, scale);
        float clearance = PlanetSpace.WorldToLocalLength(MathF.Max(0f, worldClearance), scale);
        bool moved = false;
        int iters = Math.Clamp(maxIters, 1, 16);

        for (int i = 0; i < iters; i++)
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
        PlanetDensityProbeQuality quality,
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

        for (int i = 0; i < quality.MaxSteps && t < maxDist; i++)
        {
            float step = Math.Clamp(MathF.Abs(prevD), minStep, maxStep);
            float nextT = MathF.Min(maxDist, t + step);
            var p = origin + dir * nextT;
            float d = sampler.SampleDensity(p) - radius;

            if (prevD > 0f && d <= 0f)
            {
                hitT = Refine(sampler, origin, dir, radius, prevT, nextT, quality);
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
        float tSolid,
        PlanetDensityProbeQuality quality)
    {
        float lo = tAir;
        float hi = tSolid;
        for (int i = 0; i < quality.RefineIters; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float d = sampler.SampleDensity(origin + dir * mid) - radius;
            if (d <= 0f) hi = mid;
            else lo = mid;
        }
        return (lo + hi) * 0.5f;
    }
}
