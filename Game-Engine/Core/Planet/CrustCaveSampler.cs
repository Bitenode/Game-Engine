#nullable enable
using System;
using Game_Engine.Core.Biome;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Crust-band cave occupancy under the heightfield surface.
/// Solid rock fills <c>[surfaceR - CaveDepth, surfaceR]</c>; worm noise + dig edits carve air.
/// Density convention matches the rest of the planet: negative = solid, positive = air.
/// </summary>
public sealed class CrustCaveSampler
{
    readonly PlanetConfig _config;
    readonly BiomeMap _biomeMap;
    readonly PlanetNoiseCache _noise;
    readonly PlanetVoxelEditStore? _caveEdits;
    PlanetSurfaceCubemap? _surface;

    public CrustCaveSampler(
        PlanetConfig config,
        BiomeMap biomeMap,
        PlanetNoiseCache noise,
        PlanetVoxelEditStore? caveEdits = null)
    {
        _config = config;
        _biomeMap = biomeMap;
        _noise = noise;
        _caveEdits = caveEdits;
    }

    public void SetSurfaceCubemap(PlanetSurfaceCubemap? surface) => _surface = surface;

    public float SampleSurfaceRadius(SN.Vector3 sphereDir)
    {
        if (_surface != null)
            return _surface.SampleEditedSurfaceRadius(_config.Radius, sphereDir);
        return _config.Radius;
    }

    /// <summary>
    /// Occupancy density in the crust band. Above the surface → air.
    /// Below the band floor → treated as solid wall (no deep interior caves).
    /// </summary>
    public float SampleOccupancy(SN.Vector3 localPos)
    {
        float len = localPos.Length();
        if (len < 1e-5f)
            return 1f;

        var dir = localPos / len;
        float surfaceR = SampleSurfaceRadius(dir);
        float caveDepth = MathF.Max(8f, DensityGenerator.MaxCaveDepth(_config));
        float bandFloor = MathF.Max(16f, surfaceR - caveDepth);

        // Outside the planet → air
        if (len >= surfaceR)
            return len - surfaceR;

        // Below crust band → solid (no core caves)
        if (len < bandFloor)
            return len - bandFloor;

        // Inside band: start solid, carve caves
        float density = len - surfaceR; // negative (solid) under surface
        density = ApplyCaveCarve(localPos, dir, surfaceR, density);

        if (_caveEdits != null)
            density += _caveEdits.SampleDensityDelta(localPos);

        return density;
    }

    public SN.Vector3 SampleGradient(SN.Vector3 localPos, float epsilon = 0.45f)
    {
        float e = MathF.Max(0.05f, epsilon);
        var ex = new SN.Vector3(e, 0f, 0f);
        var ey = new SN.Vector3(0f, e, 0f);
        var ez = new SN.Vector3(0f, 0f, e);
        var g = new SN.Vector3(
            SampleOccupancy(localPos + ex) - SampleOccupancy(localPos - ex),
            SampleOccupancy(localPos + ey) - SampleOccupancy(localPos - ey),
            SampleOccupancy(localPos + ez) - SampleOccupancy(localPos - ez));
        float len = g.Length();
        if (len < 1e-8f)
        {
            float r = localPos.Length();
            return r > 1e-6f ? localPos / r : SN.Vector3.UnitY;
        }
        return g / len;
    }

    /// <summary>True when the outer surface opens into a cave (hole punch).</summary>
    public bool IsSurfaceMouth(SN.Vector3 sphereDir, float probeDepth = 4f)
    {
        if (!_config.EnableCaves)
            return false;
        float surfaceR = SampleSurfaceRadius(sphereDir);
        var probe = sphereDir * MathF.Max(1f, surfaceR - probeDepth);
        return SampleOccupancy(probe) > 0.05f;
    }

    float ApplyCaveCarve(SN.Vector3 localPos, SN.Vector3 sphereDir, float surfaceRadius, float density)
    {
        if (!_config.EnableCaves)
            return density;

        var blends = _biomeMap.GetBiomes(sphereDir);
        float blendedCaveDensity = 0f;
        bool anyCaves = false;
        for (int b = 0; b < blends.Length; b++)
        {
            var biome = blends[b].Biome;
            float w = blends[b].Weight;
            if (biome.CavesEnabled && biome.CaveDensity > 0.01f)
            {
                blendedCaveDensity += biome.CaveDensity * w;
                anyCaves = true;
            }
        }

        if (!anyCaves || blendedCaveDensity <= 0.01f)
            return density;

        float below = surfaceRadius - localPos.Length();
        // Keep a thin solid roof so shell holes only open where worms break through.
        float mouth = Smooth01(2f, 14f, below);
        float depthFrac = Math.Clamp(below / MathF.Max(1f, DensityGenerator.MaxCaveDepth(_config)), 0f, 1f);

        var warp = new SN.Vector3(
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f + 11f, localPos.Y * 0.0014f, localPos.Z * 0.0014f),
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f, localPos.Y * 0.0014f + 19f, localPos.Z * 0.0014f),
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f, localPos.Y * 0.0014f, localPos.Z * 0.0014f + 7f));
        var p = localPos + warp * 28f;

        float smallN = _noise.CaveWormNoise.Sample3D(p.X * 0.0052f, p.Y * 0.0052f, p.Z * 0.0052f);
        float small = Smooth01(0.50f, 0.74f, smallN);

        float medN = _noise.CaveWormNoise.Sample3D(p.X * 0.0028f + 17f, p.Y * 0.0028f, p.Z * 0.0028f);
        float medium = Smooth01(0.48f, 0.70f, medN);

        float cavernN = _noise.CaveCavernNoise.Sample3D(p.X * 0.0009f, p.Y * 0.0009f, p.Z * 0.0009f);
        cavernN = Math.Clamp(cavernN * 0.5f + 0.5f, 0f, 1f);
        float caverns = Smooth01(0.50f, 0.66f, cavernN);
        caverns *= 0.72f + 0.35f * depthFrac;

        float open = MathF.Max(small * 0.82f, MathF.Max(medium * 0.75f, caverns));
        open *= Math.Clamp(0.65f + blendedCaveDensity * 0.45f, 0.65f, 1.1f);
        float detail = _noise.CaveDetailNoise.Sample3D(p.X * 0.018f, p.Y * 0.018f, p.Z * 0.018f);
        open = Math.Clamp(open + detail * 0.06f * open, 0f, 1.15f);
        open *= mouth;

        if (open > 0.16f)
            density = Math.Max(density, open * 1.15f - 0.05f);

        return density;
    }

    static float Smooth01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
