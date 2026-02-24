using System;
using System.Collections.Generic;
using Game_Engine.Core.Noise;
using SN = System.Numerics;

namespace Game_Engine.Core.Biome;

/// <summary>Biome + blend weight pair.</summary>
public struct BiomeBlend
{
    public BiomeDefinition Biome;
    public float Weight;
}

/// <summary>
/// Maps any point on the planet surface to a weighted blend of biomes using
/// a Whittaker-style diagram (temperature vs. moisture) with optional altitude
/// dependence, noise-distorted edges, and graph-driven parameters.
/// </summary>
public sealed class BiomeMap
{
    readonly SimplexNoise _tempNoise;
    readonly SimplexNoise _moistNoise;
    readonly SimplexNoise _edgeNoise;
    readonly BiomeDefinition[] _biomes;
    readonly float _noiseScale;

    readonly float _tempLatWeight;
    readonly float _tempNoiseWeight;
    readonly float _moistureNoiseScale;
    readonly float _altitudeWeight;
    readonly float _edgeDistortionFreq;
    readonly float _edgeDistortionAmp;

    public BiomeMap(int seed, BiomeDefinition[]? biomes = null, float noiseScale = 2f,
        float tempLatWeight = 1f, float tempNoiseWeight = 0.15f,
        float moistureNoiseScale = 3f, float altitudeWeight = 0.3f,
        float edgeDistortionFreq = 0.01f, float edgeDistortionAmp = 0.1f)
    {
        _tempNoise = new SimplexNoise(seed + 5000);
        _moistNoise = new SimplexNoise(seed + 6000);
        _edgeNoise = new SimplexNoise(seed + 7000);
        _biomes = biomes ?? BiomeDefinition.AllPresets;
        _noiseScale = noiseScale;
        _tempLatWeight = tempLatWeight;
        _tempNoiseWeight = tempNoiseWeight;
        _moistureNoiseScale = moistureNoiseScale;
        _altitudeWeight = altitudeWeight;
        _edgeDistortionFreq = edgeDistortionFreq;
        _edgeDistortionAmp = edgeDistortionAmp;
    }

    /// <summary>
    /// Get blended biomes at a point on the unit sphere (no altitude).
    /// Returns up to 4 biomes sorted by weight descending.
    /// </summary>
    public BiomeBlend[] GetBiomes(SN.Vector3 sphereDir) => GetBiomes(sphereDir, -1f);

    /// <summary>
    /// Get blended biomes at a point on the unit sphere with normalized altitude.
    /// Altitude in [0,1] where 0=lowest terrain, 1=highest. Pass -1 to ignore altitude.
    /// </summary>
    public BiomeBlend[] GetBiomes(SN.Vector3 sphereDir, float altitude)
    {
        float temperature = GetTemperature(sphereDir);
        float moisture = GetMoisture(sphereDir);

        // Phase 2a: noise-distorted edges
        if (_edgeDistortionAmp > 0f)
        {
            float edgeScale = _edgeDistortionFreq * _noiseScale * 10f;
            float tOff = _edgeNoise.Noise3D(sphereDir.X * edgeScale,
                                              sphereDir.Y * edgeScale,
                                              sphereDir.Z * edgeScale) * _edgeDistortionAmp;
            float mOff = _edgeNoise.Noise3D(sphereDir.X * edgeScale + 100f,
                                              sphereDir.Y * edgeScale + 100f,
                                              sphereDir.Z * edgeScale + 100f) * _edgeDistortionAmp;
            temperature = Math.Clamp(temperature + tOff, 0f, 1f);
            moisture = Math.Clamp(moisture + mOff, 0f, 1f);
        }

        var candidates = new List<(BiomeDefinition biome, float dist)>();

        foreach (var b in _biomes)
        {
            float tempCenter = (b.MinTemperature + b.MaxTemperature) * 0.5f;
            float moistCenter = (b.MinMoisture + b.MaxMoisture) * 0.5f;

            bool inRange = temperature >= b.MinTemperature && temperature <= b.MaxTemperature
                        && moisture >= b.MinMoisture && moisture <= b.MaxMoisture;

            float dt = temperature - tempCenter;
            float dm = moisture - moistCenter;
            float dist = MathF.Sqrt(dt * dt + dm * dm);

            // Phase 1c: altitude-based biome preference
            if (altitude >= 0f && _altitudeWeight > 0f)
            {
                float altCenter = (b.MinAltitude + b.MaxAltitude) * 0.5f;
                float altRange = (b.MaxAltitude - b.MinAltitude) * 0.5f + 0.01f;
                float altDist = MathF.Abs(altitude - altCenter) / altRange;
                dist += altDist * _altitudeWeight;

                bool altInRange = altitude >= b.MinAltitude && altitude <= b.MaxAltitude;
                if (altInRange) dist *= 0.7f;
            }

            if (inRange)
                dist *= 0.25f;

            candidates.Add((b, dist));
        }

        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        int count = Math.Min(4, candidates.Count);
        var results = new BiomeBlend[count];

        if (count == 0) return results;
        if (count == 1)
        {
            results[0] = new BiomeBlend { Biome = candidates[0].biome, Weight = 1f };
            return results;
        }

        // Sharper weighting: use squared inverse distance so the closest biome
        // dominates strongly, preventing all biomes from blending into mud.
        float totalInvDist = 0f;
        for (int i = 0; i < count; i++)
        {
            float invDist = 1f / (candidates[i].dist + 0.01f);
            invDist *= invDist;
            results[i] = new BiomeBlend { Biome = candidates[i].biome, Weight = invDist };
            totalInvDist += invDist;
        }

        for (int i = 0; i < count; i++)
            results[i].Weight /= totalInvDist;

        return results;
    }

    /// <summary>Get the dominant (highest-weight) biome at a sphere direction.</summary>
    public BiomeDefinition GetDominantBiome(SN.Vector3 sphereDir)
    {
        var blends = GetBiomes(sphereDir);
        return blends.Length > 0 ? blends[0].Biome : BiomeDefinition.Grassland;
    }

    public float GetTemperature(SN.Vector3 dir)
    {
        float latitude = MathF.Abs(dir.Y);
        float baseTemp = 1f - latitude * _tempLatWeight;
        float noise = _tempNoise.Noise3D(dir.X * _noiseScale, dir.Y * _noiseScale, dir.Z * _noiseScale);
        return Math.Clamp(baseTemp + noise * _tempNoiseWeight, 0f, 1f);
    }

    public float GetMoisture(SN.Vector3 dir)
    {
        float mScale = _noiseScale * _moistureNoiseScale;
        float noise = _moistNoise.Noise3D(dir.X * mScale, dir.Y * mScale, dir.Z * mScale);
        return Math.Clamp((noise + 1f) * 0.5f, 0f, 1f);
    }
}
