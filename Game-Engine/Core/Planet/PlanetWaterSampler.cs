using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

public static class PlanetWaterSampler
{
    public const int MaxWaterBodies = 8;
    public const int MaxWaterPaths = 8;

    public static (float min, float max) GetTerrainRange(PlanetConfig config)
    {
        float maxAmp = 0f;
        if (config.Biomes != null)
        {
            foreach (var b in config.Biomes)
                maxAmp = MathF.Max(maxAmp, b.HeightAmplitude);
        }
        return (config.Radius - maxAmp, config.Radius + maxAmp);
    }

    public static float FillRadius(PlanetConfig config, float fillFraction)
    {
        var (min, max) = GetTerrainRange(config);
        return min + Math.Clamp(fillFraction, 0f, 1f) * (max - min);
    }

    public static bool UsesMultiLevelWater(PlanetConfig config)
        => config.WaterBodies is { Length: > 0 };

    public static float GetOceanFillRadius(PlanetConfig config)
    {
        float authored = config.SeaLevel;
        var bodies = config.WaterBodies;
        if (bodies != null)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i].Kind == PlanetWaterBodyKind.Ocean)
                {
                    authored = FillRadius(config, bodies[i].FillFraction);
                    break;
                }
            }
        }

        // Ocean / beach live near Radius (±small amp). A fill taken from the
        // global mountain range (e.g. 0.38 of Radius±80) sits below the ocean
        // floor, so the mesh never meets the shoreline even though the
        // underwater query can still hit isolated basins / rivers.
        float oceanAmp = 5f;
        if (config.Biomes != null)
        {
            for (int i = 0; i < config.Biomes.Length; i++)
            {
                var b = config.Biomes[i];
                if (b.SpawnWater)
                    oceanAmp = MathF.Max(oceanAmp, b.HeightAmplitude);
            }
        }

        float minCover = config.Radius + oceanAmp + 1.5f;
        if (authored > 1f)
            return MathF.Max(authored, minCover);
        return minCover;
    }

    public static float ResolveSeaLevel(PlanetConfig config, float seaLevelFraction)
    {
        if (UsesMultiLevelWater(config))
            return GetOceanFillRadius(config);

        var (min, max) = GetTerrainRange(config);
        float authored = min + Math.Clamp(seaLevelFraction, 0f, 1f) * (max - min);
        return MathF.Max(authored, GetOceanFillRadius(config));
    }

    public static float SampleRiverMask(
        SN.Vector3 sphereDir,
        PlanetWaterPath path,
        SimplexNoise? primary,
        SimplexNoise? meander,
        BiomeMap biomeMap,
        float widthOverride = -1f)
    {
        if (primary == null)
            return 0f;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        float freq = MathF.Max(0.0001f, path.Frequency);
        float width = MathF.Max(0.001f, widthOverride > 0f ? widthOverride : path.Width);

        float n1 = primary.Noise3D(
            sphereDir.X * freq,
            sphereDir.Y * freq,
            sphereDir.Z * freq);
        float n2 = meander != null
            ? meander.Noise3D(
                sphereDir.X * freq * 1.9f + 33.7f,
                sphereDir.Y * freq * 1.9f + 77.2f,
                sphereDir.Z * freq * 1.9f + 19.4f)
            : 0f;

        float line = MathF.Abs(n1 + n2 * Math.Clamp(path.Meander, 0f, 2f));
        float riverWater = 1f - Math.Clamp(line / width, 0f, 1f);
        if (riverWater <= 0f)
            return 0f;

        var allowed = path.AllowedBiomes;
        if (allowed == null || allowed.Length == 0)
            return riverWater;

        return riverWater * AllowedBiomeWeight(biomeMap.GetBiomes(sphereDir), allowed);
    }

    public static float SampleLegacyRiverMask(
        SN.Vector3 sphereDir,
        PlanetConfig config,
        SimplexNoise? primary,
        SimplexNoise? meander,
        BiomeMap biomeMap)
    {
        if (!config.HasRiver || primary == null)
            return 0f;

        return SampleRiverMask(sphereDir, new PlanetWaterPath
        {
            Width = config.RiverWidth,
            Depth = config.RiverDepth,
            Frequency = config.RiverFrequency,
            Meander = config.RiverMeander,
            AllowedBiomes = config.RiverAllowedBiomes ?? Array.Empty<string>()
        }, primary, meander, biomeMap);
    }

    static float AllowedBiomeWeight(BiomeBlend[] blends, string[] names)
    {
        float allowedWeight = 0f;
        for (int i = 0; i < blends.Length; i++)
        {
            for (int j = 0; j < names.Length; j++)
            {
                if (string.Equals(blends[i].Biome.Name, names[j], StringComparison.OrdinalIgnoreCase))
                {
                    allowedWeight += blends[i].Weight;
                    break;
                }
            }
        }
        return Math.Clamp(allowedWeight * 1.5f, 0f, 1f);
    }

    static float SpawnWaterWeight(BiomeBlend[] blends)
    {
        float w = 0f;
        for (int i = 0; i < blends.Length; i++)
        {
            if (blends[i].Biome.SpawnWater)
                w += blends[i].Weight;
        }
        return Math.Clamp(w, 0f, 1f);
    }

    static float MaskBiomeWeight(BiomeBlend[] blends, string[] maskBiomes)
    {
        if (maskBiomes == null || maskBiomes.Length == 0)
            return 1f;
        return AllowedBiomeWeight(blends, maskBiomes);
    }

    static float BodyMatchWeight(PlanetWaterBody body, BiomeBlend[] blends)
    {
        if (body.MaskBiomes is { Length: > 0 })
            return MaskBiomeWeight(blends, body.MaskBiomes);
        if (body.Kind == PlanetWaterBodyKind.Ocean)
            return SpawnWaterWeight(blends);
        return 1f;
    }

    public static float ApplyWaterCarving(
        float baseHeight,
        SN.Vector3 sphereDir,
        PlanetConfig config,
        BiomeMap biomeMap,
        SimplexNoise? riverPrimary,
        SimplexNoise? riverMeander)
    {
        float height = baseHeight;
        var paths = config.WaterPaths;
        if (paths is { Length: > 0 })
        {
            for (int i = 0; i < paths.Length && i < MaxWaterPaths; i++)
            {
                var path = paths[i];
                float mask = SampleRiverMask(sphereDir, path, riverPrimary, riverMeander, biomeMap);
                if (mask <= 0f) continue;
                height -= path.Depth * Smooth01(0f, 1f, mask);
            }
        }
        else if (config.HasRiver)
        {
            float mask = SampleLegacyRiverMask(sphereDir, config, riverPrimary, riverMeander, biomeMap);
            if (mask > 0f)
                height -= config.RiverDepth * Smooth01(0f, 1f, mask);
        }

        var bodies = config.WaterBodies;
        if (bodies is { Length: > 0 })
        {
            float terrainR = config.Radius + height;
            var blends = biomeMap.GetBiomes(sphereDir);
            for (int i = 0; i < bodies.Length && i < MaxWaterBodies; i++)
            {
                var body = bodies[i];
                if (body.Kind == PlanetWaterBodyKind.Ocean)
                    continue;
                if (BodyMatchWeight(body, blends) <= 0.05f)
                    continue;

                float fillR = FillRadius(config, body.FillFraction);
                float basinDepth = fillR - terrainR;
                if (basinDepth < body.MinBasinDepth)
                    continue;

                height -= MathF.Min(4f, MathF.Max(0.5f, body.MinBasinDepth * 0.2f));
            }
        }

        return height;
    }

    public static PlanetWaterSurfaceSample SampleWaterSurface(
        SN.Vector3 sphereDir,
        PlanetConfig config,
        BiomeMap biomeMap,
        float terrainRadius,
        SimplexNoise? riverPrimary,
        SimplexNoise? riverMeander,
        Func<string, int> resolveBiomeIndex)
    {
        if (sphereDir.LengthSquared() < 1e-8f)
            return PlanetWaterSurfaceSample.Empty;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        var blends = biomeMap.GetBiomes(sphereDir);

        if (!UsesMultiLevelWater(config))
        {
            float biomeWater = SpawnWaterWeight(blends);
            float riverWater = SampleLegacyRiverMask(sphereDir, config, riverPrimary, riverMeander, biomeMap);
            float mask = Math.Clamp(Math.Max(biomeWater, riverWater), 0f, 1f);
            if (mask <= 0.01f)
                return PlanetWaterSurfaceSample.Empty;

            float radius = config.SeaLevel;
            if (riverWater > biomeWater)
            {
                // Carving already lowered the bed; water sits on the carved surface.
                radius = terrainRadius + 0.35f;
            }

            if (radius <= terrainRadius + 0.05f)
                return PlanetWaterSurfaceSample.Empty;

            int shoreIdx = resolveBiomeIndex("Beach");
            if (shoreIdx < 0) shoreIdx = 0;
            return new PlanetWaterSurfaceSample(
                radius,
                mask,
                shoreIdx,
                riverWater > biomeWater ? PlanetWaterKind.River : PlanetWaterKind.Ocean,
                0);
        }

        float bestRadius = 0f;
        float bestMask = 0f;
        int bestShore = 0;
        PlanetWaterKind bestKind = PlanetWaterKind.None;
        int bestBodyIndex = -1;
        float oceanFillR = GetOceanFillRadius(config);

        var bodies = config.WaterBodies!;
        for (int i = 0; i < bodies.Length && i < MaxWaterBodies; i++)
        {
            var body = bodies[i];
            float match = BodyMatchWeight(body, blends);
            if (match <= 0.05f)
                continue;

            float fillR = body.Kind == PlanetWaterBodyKind.Ocean
                ? GetOceanFillRadius(config)
                : FillRadius(config, body.FillFraction);
            if (terrainRadius >= fillR)
                continue;

            float basinDepth = fillR - terrainRadius;
            if (body.Kind != PlanetWaterBodyKind.Ocean && basinDepth < body.MinBasinDepth)
                continue;

            // Ocean is a geometric flood of anything below the water table so the
            // shoreline follows terrain, not the Ocean-biome mask (which stops
            // short of beaches and made the mesh look detached).
            if (body.Kind == PlanetWaterBodyKind.Ocean)
                match = MathF.Max(match, 0.45f);

            float mask = body.Kind == PlanetWaterBodyKind.Ocean
                ? Math.Clamp(match, 0.35f, 1f)
                : Math.Clamp(match * basinDepth / MathF.Max(1f, body.MinBasinDepth * 2f), 0.25f, 1f);

            int shoreIdx = resolveBiomeIndex(body.ShoreBiomeName);
            if (shoreIdx < 0) shoreIdx = 0;

            if (fillR > bestRadius)
            {
                bestRadius = fillR;
                bestMask = mask;
                bestShore = shoreIdx;
                bestKind = body.Kind switch
                {
                    PlanetWaterBodyKind.Lake => PlanetWaterKind.Lake,
                    PlanetWaterBodyKind.Pond => PlanetWaterKind.Pond,
                    _ => PlanetWaterKind.Ocean
                };
                bestBodyIndex = i;
            }
        }

        var paths = config.WaterPaths;
        if (paths is { Length: > 0 })
        {
            for (int i = 0; i < paths.Length && i < MaxWaterPaths; i++)
            {
                var path = paths[i];
                float riverMask = SampleRiverMask(sphereDir, path, riverPrimary, riverMeander, biomeMap);
                if (riverMask <= 0f)
                    continue;

                float riverR = terrainRadius + 0.35f;
                if (path.FlowToOcean && terrainRadius < oceanFillR)
                    riverR = oceanFillR;

                int sandIdx = resolveBiomeIndex(path.SandBiomeName);
                if (sandIdx < 0) sandIdx = bestShore;

                if (riverR >= bestRadius || bestKind == PlanetWaterKind.None)
                {
                    bestRadius = riverR;
                    bestMask = MathF.Max(bestMask, riverMask);
                    bestShore = sandIdx;
                    bestKind = PlanetWaterKind.River;
                    bestBodyIndex = i;
                }
            }
        }
        else if (config.HasRiver)
        {
            float riverMask = SampleLegacyRiverMask(sphereDir, config, riverPrimary, riverMeander, biomeMap);
            if (riverMask > 0f)
            {
                float riverR = terrainRadius + 0.35f;
                if (terrainRadius < oceanFillR)
                    riverR = oceanFillR;
                if (riverR >= bestRadius || bestKind == PlanetWaterKind.None)
                {
                    bestRadius = riverR;
                    bestMask = MathF.Max(bestMask, riverMask);
                    bestKind = PlanetWaterKind.River;
                }
            }
        }

        if (bestMask <= 0.01f || bestRadius <= terrainRadius + 0.05f)
            return PlanetWaterSurfaceSample.Empty;

        return new PlanetWaterSurfaceSample(bestRadius, bestMask, bestShore, bestKind, bestBodyIndex);
    }

    public static (int biomeIndex, float weight)? SampleSandWeight(
        SN.Vector3 sphereDir,
        PlanetConfig config,
        BiomeMap biomeMap,
        float terrainRadius,
        SimplexNoise? riverPrimary,
        SimplexNoise? riverMeander,
        Func<string, int> resolveBiomeIndex)
    {
        if (sphereDir.LengthSquared() < 1e-8f)
            return null;

        sphereDir = SN.Vector3.Normalize(sphereDir);
        var blends = biomeMap.GetBiomes(sphereDir);
        float bestW = 0f;
        int bestIdx = resolveBiomeIndex("Beach");
        if (bestIdx < 0) bestIdx = 0;

        var paths = config.WaterPaths;
        if (paths is { Length: > 0 })
        {
            for (int i = 0; i < paths.Length && i < MaxWaterPaths; i++)
            {
                var path = paths[i];
                float core = SampleRiverMask(sphereDir, path, riverPrimary, riverMeander, biomeMap);
                float sandW = MathF.Max(path.Width, path.SandWidth);
                float bank = SampleRiverMask(sphereDir, path, riverPrimary, riverMeander, biomeMap, sandW);
                float w = MathF.Max(0f, bank - core);
                w = MathF.Max(w, core * (1f - core) * 2f);
                if (w > bestW)
                {
                    bestW = w;
                    int idx = resolveBiomeIndex(path.SandBiomeName);
                    if (idx >= 0) bestIdx = idx;
                }
            }
        }
        else if (config.HasRiver)
        {
            var legacy = new PlanetWaterPath
            {
                Width = config.RiverWidth,
                Depth = config.RiverDepth,
                Frequency = config.RiverFrequency,
                Meander = config.RiverMeander,
                AllowedBiomes = config.RiverAllowedBiomes ?? Array.Empty<string>(),
                SandWidth = MathF.Max(config.RiverWidth * 2.5f, 0.04f),
                SandBiomeName = "Beach"
            };
            float core = SampleRiverMask(sphereDir, legacy, riverPrimary, riverMeander, biomeMap);
            float bank = SampleRiverMask(sphereDir, legacy, riverPrimary, riverMeander, biomeMap, legacy.SandWidth);
            bestW = MathF.Max(0f, bank - core);
            bestW = MathF.Max(bestW, core * (1f - core) * 2f);
        }

        var bodies = config.WaterBodies;
        if (bodies is { Length: > 0 })
        {
            for (int i = 0; i < bodies.Length && i < MaxWaterBodies; i++)
            {
                var body = bodies[i];
                if (BodyMatchWeight(body, blends) <= 0.05f && body.Kind != PlanetWaterBodyKind.Ocean)
                    continue;

                float fillR = FillRadius(config, body.FillFraction);
                float dist = MathF.Abs(terrainRadius - fillR);
                float band = MathF.Max(2f, body.ShoreWidth * MathF.Max(1f, config.Radius));
                float w = 1f - Math.Clamp(dist / band, 0f, 1f);
                w = Smooth01(0.2f, 1f, w);
                if (w > bestW)
                {
                    bestW = w;
                    int idx = resolveBiomeIndex(body.ShoreBiomeName);
                    if (idx >= 0) bestIdx = idx;
                }
            }
        }
        else
        {
            float dist = MathF.Abs(terrainRadius - config.SeaLevel);
            float band = MathF.Max(2f, 0.03f * MathF.Max(1f, config.Radius));
            float w = Smooth01(0.2f, 1f, 1f - Math.Clamp(dist / band, 0f, 1f));
            if (SpawnWaterWeight(blends) > 0.05f)
                bestW = MathF.Max(bestW, w);
        }

        if (bestW <= 0.02f)
            return null;
        return (bestIdx, Math.Clamp(bestW * 0.9f, 0f, 1f));
    }

    public static void ApplySandBlend(ref SN.Vector4 blendIdx, ref SN.Vector4 blendWt, int sandIndex, float sandWeight)
    {
        sandWeight = Math.Clamp(sandWeight, 0f, 1f);
        if (sandWeight < 0.01f)
            return;

        float keep = 1f - sandWeight;
        blendWt *= keep;
        blendIdx = new SN.Vector4(sandIndex, blendIdx.X, blendIdx.Y, blendIdx.Z);
        blendWt = new SN.Vector4(sandWeight, blendWt.X, blendWt.Y, blendWt.Z);
        float sum = blendWt.X + blendWt.Y + blendWt.Z + blendWt.W;
        if (sum > 1e-5f)
            blendWt /= sum;
    }

    static float Smooth01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
