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

        float height = 0f;
        for (int b = 0; b < blends.Length && b < 4; b++)
        {
            var biome = blends[b].Biome;
            float w = blends[b].Weight;
            int idx = Math.Clamp(biome.BiomeIndex, 0, biomeNoises.Length - 1);
            float sample = biomeNoises[idx].Sample3D(nx, ny, nz);
            if (biome.NoiseMode == "Ridged") sample = sample * 0.7f - 0.3f;
            else if (biome.NoiseMode == "Billow") sample = sample * 0.8f;
            height += biome.HeightAmplitude * sample * w;
        }

        if (erosionNoise != null && blends.Length > 0)
        {
            float totalErosion = 0f;
            for (int b = 0; b < blends.Length && b < 4; b++)
            {
                var biome = blends[b].Biome;
                if (biome.ErosionStrength <= 0f) continue;
                // Scale coordinates instead of mutating Frequency so shared caches are thread-safe.
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

        return height;
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
            waterCarve.RiverMeander);
    }
}
