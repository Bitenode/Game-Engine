using System;
using System.Collections.Generic;
using Game_Engine.Core.Noise;
using Game_Engine.Core.Planet;
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
/// Includes altitude lapse, water moisture, and rain-shadow coupling.
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
    readonly float _altitudeSeaLevel;
    readonly float _altitudeMaxHeight;
    readonly float _heightAmplitudeRef;

    PlanetConfig? _config;
    SimplexNoise? _riverPrimary;
    SimplexNoise? _riverMeander;
    FractalNoise? _ridgeNoise;

    /// <summary>When true (BiomeSelect wired), altitude weighs more in classification.</summary>
    public bool UseSelectClassifier { get; set; }

    public BiomeMap(int seed, BiomeDefinition[]? biomes = null, float noiseScale = 2f,
        float tempLatWeight = 1f, float tempNoiseWeight = 0.15f,
        float moistureNoiseScale = 3f, float altitudeWeight = 0.3f,
        float edgeDistortionFreq = 0.01f, float edgeDistortionAmp = 0.1f,
        float altitudeSeaLevel = 0f, float altitudeMaxHeight = 1f, float heightAmplitudeRef = 50f)
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
        _altitudeSeaLevel = altitudeSeaLevel;
        _altitudeMaxHeight = altitudeMaxHeight;
        _heightAmplitudeRef = heightAmplitudeRef > 1f ? heightAmplitudeRef : 50f;
    }

    /// <summary>Normalize terrain height (offset from planet radius, meters) to [0,1].</summary>
    public float NormalizeAltitude(float heightFromRadius)
    {
        float maxH = _altitudeMaxHeight;
        if (maxH <= 1.001f)
            maxH = _heightAmplitudeRef * MathF.Max(maxH, 0.01f);
        float span = MathF.Max(maxH - _altitudeSeaLevel, 1e-4f);
        return Math.Clamp((heightFromRadius - _altitudeSeaLevel) / span, 0f, 1f);
    }

    /// <summary>
    /// Bind water / ridge samples used by moisture coupling. Safe to call before climate atlas bake.
    /// Does not sample full terrain height (avoids recursion through <see cref="PlanetSurfaceUtility.SampleHeight"/>).
    /// </summary>
    public void BindClimateCoupling(
        PlanetConfig? config,
        SimplexNoise? riverPrimary = null,
        SimplexNoise? riverMeander = null,
        FractalNoise? ridgeNoise = null)
    {
        _config = config;
        _riverPrimary = riverPrimary;
        _riverMeander = riverMeander;
        _ridgeNoise = ridgeNoise;
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
        float temperature = GetTemperature(sphereDir, altitude);
        float moisture = GetMoisture(sphereDir, altitude);

        // Noise-distorted edges
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

            // Altitude-based biome preference
            float altW = _altitudeWeight;
            if (UseSelectClassifier)
                altW = MathF.Max(altW, 0.45f);

            if (altitude >= 0f && altW > 0f)
            {
                float altCenter = (b.MinAltitude + b.MaxAltitude) * 0.5f;
                float altRange = (b.MaxAltitude - b.MinAltitude) * 0.5f + 0.01f;
                float altDist = MathF.Abs(altitude - altCenter) / altRange;
                dist += altDist * altW;

                bool altInRange = altitude >= b.MinAltitude && altitude <= b.MaxAltitude;
                if (altInRange) dist *= 0.7f;
                else if (UseSelectClassifier) dist *= 1.35f;
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
    public BiomeDefinition GetDominantBiome(SN.Vector3 sphereDir) => GetDominantBiome(sphereDir, -1f);

    public BiomeDefinition GetDominantBiome(SN.Vector3 sphereDir, float altitude)
    {
        var blends = GetBiomes(sphereDir, altitude);
        return blends.Length > 0 ? blends[0].Biome : BiomeDefinition.Grassland;
    }

    public float GetTemperature(SN.Vector3 dir) => GetTemperature(dir, -1f);

    /// <summary>
    /// Latitude + noise temperature, plus altitude lapse when <paramref name="altitude"/> is in [0,1].
    /// </summary>
    public float GetTemperature(SN.Vector3 dir, float altitude)
    {
        float latitude = MathF.Abs(dir.Y);
        float baseTemp = 1f - latitude * _tempLatWeight;
        float noise = _tempNoise.Noise3D(dir.X * _noiseScale, dir.Y * _noiseScale, dir.Z * _noiseScale);
        float temp = baseTemp + noise * _tempNoiseWeight;

        if (_config != null)
            temp += _config.TemperatureBias + SampleLatitudeBandBias(dir, wantTemp: true);

        if (altitude >= 0f)
        {
            float lapse = _config?.AltitudeLapseRate ?? 0.35f;
            temp -= altitude * Math.Clamp(lapse, 0f, 1.5f);
        }

        return Math.Clamp(temp, 0f, 1f);
    }

    public float GetMoisture(SN.Vector3 dir) => GetMoisture(dir, -1f);

    /// <summary>
    /// Base moisture noise + water proximity boost − rain-shadow on ridge lee sides.
    /// </summary>
    public float GetMoisture(SN.Vector3 dir, float altitude)
    {
        float mScale = _noiseScale * _moistureNoiseScale;
        float noise = _moistNoise.Noise3D(dir.X * mScale, dir.Y * mScale, dir.Z * mScale);
        float moist = (noise + 1f) * 0.5f;

        if (_config != null)
            moist += _config.MoistureBias + SampleLatitudeBandBias(dir, wantTemp: false);

        moist += SampleWaterMoistureBoost(dir);
        moist -= SampleRainShadow(dir, altitude);

        return Math.Clamp(moist, 0f, 1f);
    }

    /// <summary>0–1 climate moisture used to bias shore sand (no biome recursion).</summary>
    public float SampleShoreClimateWeight(SN.Vector3 dir, float altitude = -1f)
    {
        float moist = GetMoisture(dir, altitude);
        float bias = _config?.ShoreClimateBias ?? 0.35f;
        // Wet climates thicken the sand band; arid climates thin it.
        return Math.Clamp(0.55f + (moist - 0.5f) * 2f * bias, 0.25f, 1.35f);
    }

    float SampleWaterMoistureBoost(SN.Vector3 dir)
    {
        if (_config == null)
            return 0f;

        float boost = Math.Clamp(_config.WaterMoistureBoost, 0f, 1f);
        if (boost <= 1e-4f)
            return 0f;

        float prox = 0f;
        var paths = _config.WaterPaths;
        if (paths is { Length: > 0 } && _riverPrimary != null)
        {
            for (int i = 0; i < paths.Length && i < PlanetWaterSampler.MaxWaterPaths; i++)
            {
                // Skip biome filter to avoid GetBiomes → GetMoisture recursion.
                float mask = PlanetWaterSampler.SampleRiverMask(
                    dir, paths[i], _riverPrimary, _riverMeander, biomeMap: null);
                prox = MathF.Max(prox, mask);
            }
        }
        else if (_config.HasRiver && _riverPrimary != null)
        {
            float mask = PlanetWaterSampler.SampleRiverMask(
                dir,
                new PlanetWaterPath
                {
                    Width = MathF.Max(_config.RiverWidth * 2.5f, 0.04f),
                    Depth = _config.RiverDepth,
                    Frequency = _config.RiverFrequency,
                    Meander = _config.RiverMeander,
                    AllowedBiomes = Array.Empty<string>()
                },
                _riverPrimary,
                _riverMeander,
                biomeMap: null);
            prox = MathF.Max(prox, mask);
        }

        var bodies = _config.WaterBodies;
        if (bodies is { Length: > 0 })
        {
            for (int i = 0; i < bodies.Length && i < PlanetWaterSampler.MaxWaterBodies; i++)
            {
                var body = bodies[i];
                // Cheap coastal / lake moisture: ocean and lakes always contribute a soft halo.
                float kindBoost = body.Kind switch
                {
                    PlanetWaterBodyKind.Ocean => 0.55f,
                    PlanetWaterBodyKind.Lake => 0.75f,
                    PlanetWaterBodyKind.Pond => 0.45f,
                    _ => 0.4f
                };
                // Soft angular noise halo around water-friendly latitudes / basins.
                float halo = 0.5f + 0.5f * _moistNoise.Noise3D(
                    dir.X * 4.2f + i * 17.1f,
                    dir.Y * 4.2f,
                    dir.Z * 4.2f + body.FillFraction * 9f);
                float fillBias = 1f - MathF.Abs(body.FillFraction - 0.5f) * 0.4f;
                prox = MathF.Max(prox, kindBoost * halo * fillBias * MathF.Max(0.35f, body.ShoreWidth * 4f));
            }
        }

        return prox * boost;
    }

    float SampleRainShadow(SN.Vector3 dir, float altitude)
    {
        if (_config == null)
            return 0f;

        float strength = Math.Clamp(_config.RainShadowStrength, 0f, 1.5f);
        float ridge = Math.Max(0f, _config.RidgeStrength);
        if (strength <= 1e-4f || (ridge <= 1e-4f && _ridgeNoise == null))
            return 0f;

        // Prevailing wind along +X in tangent space (constant planet wind for bake cost).
        var wind = SN.Vector3.Normalize(new SN.Vector3(1f, 0.15f, 0.35f));
        // Project wind onto tangent plane at dir.
        float along = SN.Vector3.Dot(wind, dir);
        var windTan = wind - dir * along;
        if (windTan.LengthSquared() < 1e-8f)
            windTan = SN.Vector3.Normalize(SN.Vector3.Cross(dir, SN.Vector3.UnitY));
        else
            windTan = SN.Vector3.Normalize(windTan);

        const float step = 0.012f;
        float hHere = RoughOrographicHeight(dir, altitude);
        float hUp = RoughOrographicHeight(SN.Vector3.Normalize(dir - windTan * step), altitude);
        float hDown = RoughOrographicHeight(SN.Vector3.Normalize(dir + windTan * step), altitude);

        // Lee side: downhill along wind (hUp > hHere > hDown) with a ridge nearby.
        float drop = Math.Clamp((hUp - hDown) * 0.5f, 0f, 1f);
        float ridgePresence = Math.Clamp(ridge * 0.35f + hHere * 0.65f, 0f, 1f);
        return drop * ridgePresence * strength;
    }

    float RoughOrographicHeight(SN.Vector3 dir, float altitude)
    {
        float h = altitude >= 0f ? altitude : 0.45f;
        if (_ridgeNoise != null && _config != null && _config.RidgeStrength > 0f)
        {
            float r = Math.Clamp(_ridgeNoise.Sample3D(
                dir.X * _config.Radius,
                dir.Y * _config.Radius,
                dir.Z * _config.Radius), 0f, 1f);
            h = Math.Clamp(h * 0.35f + r * Math.Clamp(_config.RidgeStrength, 0f, 2f) * 0.5f, 0f, 1f);
        }
        else
        {
            // Fallback: large-scale moisture noise as a stand-in ridge field.
            float n = _moistNoise.Noise3D(dir.X * 1.7f, dir.Y * 1.7f, dir.Z * 1.7f);
            h = Math.Clamp(h * 0.5f + (n * 0.5f + 0.5f) * 0.5f, 0f, 1f);
        }
        return h;
    }

    float SampleLatitudeBandBias(SN.Vector3 dir, bool wantTemp)
    {
        var bands = _config?.LatitudeBands;
        if (bands == null || bands.Length == 0)
            return 0f;

        float lat = Math.Clamp(dir.Y, -1f, 1f);
        float bestW = float.MaxValue;
        int best = -1;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            if (lat < b.MinLatitude || lat > b.MaxLatitude)
                continue;
            float w = b.MaxLatitude - b.MinLatitude;
            if (w < bestW)
            {
                bestW = w;
                best = i;
            }
        }
        if (best < 0)
            return 0f;
        return wantTemp ? bands[best].TemperatureBias : bands[best].MoistureBias;
    }
}
