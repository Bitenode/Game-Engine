using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

public static class PlanetSurfaceUtility
{
    public static float SampleHeight(
        PlanetConfig config,
        BiomeMap biomeMap,
        FractalNoise[] biomeNoises,
        FractalNoise? erosionNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        SN.Vector3 sphereDir)
    {
        float radius = config.Radius;
        var blends = biomeMap.GetBiomes(sphereDir);
        float nx = sphereDir.X * radius;
        float ny = sphereDir.Y * radius;
        float nz = sphereDir.Z * radius;

        float Accumulate(BiomeBlend[] src)
        {
            float h = 0f;
            for (int b = 0; b < src.Length && b < 4; b++)
            {
                var biome = src[b].Biome;
                float w = src[b].Weight;
                int idx = Math.Clamp(biome.BiomeIndex, 0, biomeNoises.Length - 1);
                float sample = biomeNoises[idx].Sample3D(nx, ny, nz);
                if (biome.NoiseMode == "Ridged") sample = sample * 0.7f - 0.3f;
                else if (biome.NoiseMode == "Billow") sample = sample * 0.8f;
                // Compress biome amplitude so Ocean(5) next to Mountains(85)
                // cannot build a one-triangle pyramid. Ranges come from geology.
                float amp = biome.HeightAmplitude;
                amp = 10f + (amp - 10f) * 0.42f;
                h += amp * sample * w;
            }
            return h;
        }

        float height = Accumulate(blends);
        float altitude = biomeMap.NormalizeAltitude(height);
        blends = biomeMap.GetBiomes(sphereDir, altitude);
        height = Accumulate(blends);

        if (erosionNoise != null && blends.Length > 0)
        {
            float totalErosion = 0f;
            for (int b = 0; b < blends.Length && b < 4; b++)
            {
                var biome = blends[b].Biome;
                if (biome.ErosionStrength <= 0f) continue;
                float freq = biome.ErosionFrequency;
                float e = Math.Clamp(erosionNoise.Sample3D(nx * freq, ny * freq, nz * freq), 0f, 1f);
                totalErosion += e * biome.ErosionStrength * 5f * blends[b].Weight;
            }
            height -= totalErosion;
        }

        if (ridgeNoise != null && config.RidgeStrength > 0f)
        {
            float ridge = Math.Clamp(ridgeNoise.Sample3D(nx, ny, nz), 0f, 1f);
            height += ridge * config.RidgeStrength * 24f;
        }

        if (basinNoise != null && config.BasinStrength > 0f)
        {
            float basin = 1f - Math.Clamp(basinNoise.Sample3D(nx, ny, nz), 0f, 1f);
            basin *= basin;
            height -= basin * config.BasinStrength * 18f;
        }

        height = ApplyGraphGeology(config, sphereDir, height);
        return height;
    }

    /// <summary>
    /// Continent shelves, craters, volcanoes, and coastal cliffs from the biome graph.
    /// Ocean water then sits in the carved basins instead of a sphere over the land.
    /// </summary>
    public static float ApplyGraphGeology(PlanetConfig config, SN.Vector3 sphereDir, float height)
    {
        if (sphereDir.LengthSquared() < 1e-12f)
            return height;
        sphereDir = SN.Vector3.Normalize(sphereDir);

        var n = config.GeologyNoise;
        float land = SampleContinentLand(config, sphereDir, n);
        float oceanFloor = -10f;
        if (config.Continents is { Length: > 0 })
        {
            // Narrow coastal band: a real cliff face instead of a 200 m ramp
            // that LOD turns into two triangles.
            float shelf = MathF.Max(height, 8f);
            float k = Smooth01(0.50f, 0.57f, land);
            height = oceanFloor + (shelf - oceanFloor) * k;
        }

        if (n != null && land > 0.42f)
        {
            float r1 = 1f - MathF.Abs(n.Noise3D(
                sphereDir.X * 2.3f + 1.4f, sphereDir.Y * 2.3f, sphereDir.Z * 2.3f - 0.7f));
            float r2 = 1f - MathF.Abs(n.Noise3D(
                sphereDir.X * 6.1f - 4.2f, sphereDir.Y * 6.1f + 2.1f, sphereDir.Z * 6.1f));
            float ranges = r1 * r1 * (0.5f + 0.5f * r2 * r2);
            height += ranges * Smooth01(0.48f, 0.72f, land) * 42f;
        }

        var craters = config.Craters;
        if (craters != null && n != null)
        {
            for (int i = 0; i < craters.Length; i++)
            {
                var c = craters[i];
                float angR = FeatureAngularRadius(c.Radius, config.Radius);
                if (!TryNearestFeature(n, sphereDir, angR, c.Density, 17.3f + i * 3.1f, out float t, out _))
                    continue;
                float bowl = 1f - t;
                height -= c.Depth * bowl * bowl;
                height += c.RimHeight * bowl * (1f - bowl) * 4f;
            }
        }

        var volcanoes = config.Volcanoes;
        if (volcanoes != null && n != null)
        {
            for (int i = 0; i < volcanoes.Length; i++)
            {
                var v = volcanoes[i];
                float angR = FeatureAngularRadius(v.Radius, config.Radius);
                if (!TryNearestFeature(n, sphereDir, angR, v.Density, 41.7f + i * 2.4f, out float t, out _))
                    continue;
                float caldera = Math.Clamp(v.CalderaRadius / MathF.Max(0.01f, v.Radius), 0.08f, 0.42f);
                float peak = t < caldera
                    ? 0.28f + 0.22f * (t / caldera)
                    : MathF.Max(0f, 1f - (t - caldera) / MathF.Max(1e-4f, 1f - caldera));
                peak *= peak;
                height += v.Height * peak * MathF.Max(0.35f, land);
            }
        }

        var cliffs = config.Cliffs;
        if (cliffs != null && n != null && land > 0.35f && land < 0.78f)
        {
            for (int i = 0; i < cliffs.Length; i++)
            {
                var cl = cliffs[i];
                float freq = MathF.Max(0.001f, cl.Frequency) * config.Radius;
                float ridged = 1f - MathF.Abs(n.Noise3D(
                    sphereDir.X * freq + 9.1f,
                    sphereDir.Y * freq + 3.7f,
                    sphereDir.Z * freq + 5.2f));
                // Ocean side drops, land lip rises — a vertical-ish escarpment.
                float drop = 1f - Smooth01(0.40f, 0.54f, land);
                float lip = Smooth01(0.52f, 0.68f, land) * (1f - Smooth01(0.68f, 0.80f, land));
                float wall = cl.Strength * cl.SlopeBias;
                height -= drop * wall * (14f + ridged * 6f);
                height += lip * wall * (10f + ridged * 16f);
            }
        }

        return height;
    }

    public static float SampleContinentLand(PlanetConfig config, SN.Vector3 sphereDir, SimplexNoise? noise = null)
    {
        var continents = config.Continents;
        if (continents == null || continents.Length == 0)
            return 1f;

        noise ??= config.GeologyNoise;
        if (noise == null)
            return 1f;

        float land = 0f;
        for (int i = 0; i < continents.Length; i++)
        {
            var c = continents[i];
            float freq = MathF.Max(0.0002f, c.Frequency) * config.Radius;
            float ox = i * 13.7f;
            float v = noise.Noise3D(
                sphereDir.X * freq + ox,
                sphereDir.Y * freq + ox * 0.4f,
                sphereDir.Z * freq) * 0.5f + 0.5f;
            float t = Math.Clamp(c.Threshold, 0.05f, 0.95f);
            float mask = Smooth01(t - 0.04f, t + 0.04f, v) * Math.Clamp(c.Strength, 0f, 2f);
            if (mask > land) land = mask;
        }
        return Math.Clamp(land, 0f, 1f);
    }

    /// <summary>
    /// 0–1 if this column is inside a volcano caldera bowl (not the outer cone).
    /// </summary>
    public static float SampleMagmaBowl(PlanetConfig config, SN.Vector3 sphereDir)
        => TryGetLavaLake(config, sphereDir, 0f, out _, out float mask) ? mask : 0f;

    /// <summary>
    /// Lava lake in the caldera hole only. Outer cone walls and inner rim
    /// above the pool stay dry. <paramref name="terrainRadius"/> 0 skips the
    /// below-lake test (used to keep ocean out of the crater).
    /// </summary>
    public static bool TryGetLavaLake(
        PlanetConfig config,
        SN.Vector3 sphereDir,
        float terrainRadius,
        out float lavaRadius,
        out float mask)
    {
        lavaRadius = 0f;
        mask = 0f;
        var n = config.GeologyNoise;
        if (n == null || sphereDir.LengthSquared() < 1e-12f)
            return false;
        sphereDir = SN.Vector3.Normalize(sphereDir);
        float land = SampleContinentLand(config, sphereDir, n);
        if (land < 0.35f)
            return false;

        var volcanoes = config.Volcanoes;
        if (volcanoes == null || volcanoes.Length == 0)
            return false;

        float landK = MathF.Max(0.35f, land);
        float best = 0f;
        float bestR = 0f;
        for (int i = 0; i < volcanoes.Length; i++)
        {
            var v = volcanoes[i];
            float angR = FeatureAngularRadius(v.Radius, config.Radius);
            if (!TryNearestFeature(n, sphereDir, angR, v.Density, 41.7f + i * 2.4f, out float t, out _))
                continue;

            // Same caldera fraction as the height cone. Do not fill past the rim.
            float caldera = Math.Clamp(v.CalderaRadius / MathF.Max(0.01f, v.Radius), 0.08f, 0.42f);
            if (t >= caldera * 0.92f)
                continue;

            // Match ApplyGraphGeology bowl: floor 0.28, interior rim 0.50.
            float peak = 0.28f + 0.22f * (t / MathF.Max(1e-4f, caldera));
            float volcanoAdd = v.Height * peak * landK;
            float backgroundR = terrainRadius > 1f
                ? terrainRadius - volcanoAdd
                : config.Radius;
            // Pool just below the interior rim so inner walls stay rock.
            float lakeR = backgroundR + v.Height * landK * 0.46f;
            if (terrainRadius > 1f && terrainRadius >= lakeR - 0.25f)
                continue;

            float inner = 1f - t / MathF.Max(1e-4f, caldera * 0.92f);
            if (inner > best)
            {
                best = inner;
                bestR = lakeR;
            }
        }

        if (best < 0.18f)
            return false;
        mask = Math.Clamp(best, 0f, 1f);
        lavaRadius = bestR > 1f ? bestR : config.Radius + 4f;
        return true;
    }

    static float FeatureAngularRadius(float radius, float planetRadius)
        => radius > 1.5f
            ? radius / MathF.Max(1f, planetRadius)
            : MathF.Max(0.02f, radius);

    static bool TryNearestFeature(
        SimplexNoise n, SN.Vector3 dir, float angRadius, float density, float offset,
        out float t, out float ang)
    {
        t = 1f;
        ang = 99f;
        int count = Math.Clamp((int)(6f + Math.Clamp(density, 0.05f, 1f) * 36f), 5, 24);
        float best = 4f;
        for (int i = 0; i < count; i++)
        {
            float z = 1f - 2f * (i + 0.5f) / count;
            float rr = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            float theta = i * 2.3999632f + offset;
            var feat = new SN.Vector3(MathF.Cos(theta) * rr, z, MathF.Sin(theta) * rr);
            float jx = n.Noise3D(feat.X * 3.1f + offset, feat.Y * 3.1f, feat.Z * 3.1f);
            float jy = n.Noise3D(feat.Y * 3.1f, feat.Z * 3.1f + offset, feat.X * 3.1f);
            float jz = n.Noise3D(feat.Z * 3.1f + offset, feat.X * 3.1f, feat.Y * 3.1f);
            feat = SN.Vector3.Normalize(feat + new SN.Vector3(jx, jy, jz) * 0.18f);
            float d = MathF.Acos(Math.Clamp(SN.Vector3.Dot(dir, feat), -1f, 1f));
            if (d < best) best = d;
        }

        if (best > angRadius)
            return false;
        ang = best;
        t = best / MathF.Max(1e-4f, angRadius);
        return true;
    }

    static float Smooth01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float SampleHeight(
        PlanetConfig config,
        BiomeMap biomeMap,
        FractalNoise[] biomeNoises,
        FractalNoise? erosionNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        SN.Vector3 sphereDir,
        PlanetWaterCarveContext? waterCarve)
    {
        float height = SampleHeight(
            config, biomeMap, biomeNoises, erosionNoise, ridgeNoise, basinNoise, sphereDir);
        if (waterCarve == null)
            return height;

        return PlanetWaterSampler.ApplyWaterCarving(
            height,
            sphereDir,
            waterCarve.Config,
            biomeMap,
            waterCarve.RiverPrimary,
            waterCarve.RiverMeander,
            waterCarve.ClimateAtlas);
    }
}
