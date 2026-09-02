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

    /// <summary>
    /// Coastal / basin band for water fill. Upper bound stays near Radius so
    /// fillFraction cannot climb mid-elevation hills (mountain amp must not apply).
    /// </summary>
    public static (float min, float max) GetOceanTerrainRange(PlanetConfig config)
    {
        float amp = 5f;
        if (config.Biomes != null)
        {
            for (int i = 0; i < config.Biomes.Length; i++)
            {
                var b = config.Biomes[i];
                if (b.SpawnWater || string.Equals(b.Name, "Beach", StringComparison.OrdinalIgnoreCase))
                    amp = MathF.Max(amp, MathF.Min(b.HeightAmplitude, 12f));
            }
        }
        // High end at Radius: fillFraction 0.55 sits near the basin lip so the
        // water sheet can stretch to the ocean cutout walls (not stop meters inland).
        return (config.Radius - amp, config.Radius + 1.25f);
    }

    public static float FillRadius(PlanetConfig config, float fillFraction)
    {
        var (min, max) = GetTerrainRange(config);
        return min + Math.Clamp(fillFraction, 0f, 1f) * (max - min);
    }

    public static float FillOceanRadius(PlanetConfig config, float fillFraction)
    {
        var (min, max) = GetOceanTerrainRange(config);
        return min + Math.Clamp(fillFraction, 0f, 1f) * (max - min);
    }

    public static bool UsesMultiLevelWater(PlanetConfig config)
        => config.WaterBodies is { Length: > 0 };

    public static float GetOceanFillRadius(PlanetConfig config)
    {
        float fillFraction = 0.55f;
        var bodies = config.WaterBodies;
        if (bodies != null)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i].Kind == PlanetWaterBodyKind.Ocean)
                {
                    fillFraction = bodies[i].FillFraction;
                    break;
                }
            }
        }

        float amp = 5f;
        if (config.Biomes != null)
        {
            for (int i = 0; i < config.Biomes.Length; i++)
            {
                var b = config.Biomes[i];
                if (b.SpawnWater)
                    amp = MathF.Max(amp, MathF.Min(b.HeightAmplitude, 10f));
            }
        }

        // 0.5 = Radius. Small nudge only — one constant sphere for the whole planet.
        float t = Math.Clamp(fillFraction, 0f, 1f);
        return config.Radius + (t - 0.5f) * MathF.Max(4f, amp * 0.75f);
    }

    public static float ResolveSeaLevel(PlanetConfig config, float seaLevelFraction)
    {
        if (UsesMultiLevelWater(config))
            return GetOceanFillRadius(config);

        return FillOceanRadius(config, seaLevelFraction);
    }

    public static float SampleRiverMask(
        SN.Vector3 sphereDir,
        PlanetWaterPath path,
        SimplexNoise? primary,
        SimplexNoise? meander,
        BiomeMap? biomeMap,
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
        if (allowed == null || allowed.Length == 0 || biomeMap == null)
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
        if (body.Kind == PlanetWaterBodyKind.Ocean)
        {
            float oceanW = body.MaskBiomes is { Length: > 0 }
                ? MaskBiomeWeight(blends, body.MaskBiomes)
                : SpawnWaterWeight(blends);
            // Beach only extends an existing ocean shoreline — never creates ocean alone.
            if (oceanW <= 0.05f)
                return oceanW;
            return MathF.Max(oceanW, AllowedBiomeWeight(blends, new[] { "Beach" }));
        }
        if (body.MaskBiomes is { Length: > 0 })
            return MaskBiomeWeight(blends, body.MaskBiomes);
        return 1f;
    }

    public static float ApplyWaterCarving(
        float baseHeight,
        SN.Vector3 sphereDir,
        PlanetConfig config,
        BiomeMap biomeMap,
        SimplexNoise? riverPrimary,
        SimplexNoise? riverMeander,
        PlanetClimateAtlas? climateAtlas = null)
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

        // Optional compile-time flow-accumulation channels (bake once on the height LUT).
        if (config.UseFlowAccumulationRivers && climateAtlas is { HasFlowRivers: true })
        {
            float flow = climateAtlas.SampleFlowRiver(sphereDir);
            if (flow > 0.01f)
                height -= config.FlowRiverDepth * Smooth01(0f, 1f, flow);
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
        if (PlanetSurfaceUtility.TryGetLavaLake(config, sphereDir, terrainRadius, out float lavaR, out float magma)
            && magma > 0.18f
            && lavaR > terrainRadius + 0.2f)
        {
            int shoreIdx = resolveBiomeIndex("Mountains");
            if (shoreIdx < 0) shoreIdx = resolveBiomeIndex("Beach");
            if (shoreIdx < 0) shoreIdx = 0;
            return new PlanetWaterSurfaceSample(
                lavaR, Math.Clamp(magma, 0.4f, 1f), shoreIdx, PlanetWaterKind.Lava, 6);
        }

        // Altitude matters: Ocean is MaxAltitude ~0.2. Ignoring it classifies wet
        // midland as Ocean and floods hillsides around the deep basins.
        float alt = biomeMap.NormalizeAltitude(terrainRadius - config.Radius);
        var blends = biomeMap.GetBiomes(sphereDir, alt);

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
        float bestScore = 0f;
        float oceanFillR = GetOceanFillRadius(config);

        var bodies = config.WaterBodies!;
        for (int i = 0; i < bodies.Length && i < MaxWaterBodies; i++)
        {
            var body = bodies[i];
            float match = BodyMatchWeight(body, blends);
            if (match <= 0.05f)
                continue;

            float fillR = oceanFillR;
            if (body.Kind == PlanetWaterBodyKind.Ocean)
            {
                // Continents stay dry even when the classifier still says Ocean.
                float land = PlanetSurfaceUtility.SampleContinentLand(config, sphereDir);
                if (config.Continents is { Length: > 0 } && land > 0.38f)
                    continue;
                if (alt > 0.11f)
                    continue;
            }
            else
            {
                // Inland water sits in the hole — it must not fill up to global
                // sea level or every grassland valley becomes a second ocean.
                float hole = oceanFillR - terrainRadius;
                if (hole < body.MinBasinDepth)
                    continue;
                fillR = terrainRadius + MathF.Min(4.5f, hole * 0.22f);
            }
            if (terrainRadius >= fillR)
                continue;

            float basinDepth = fillR - terrainRadius;
            if (body.Kind != PlanetWaterBodyKind.Ocean && basinDepth < 0.4f)
                continue;

            float mask = body.Kind == PlanetWaterBodyKind.Ocean
                ? Math.Clamp(match, 0.35f, 1f)
                : Math.Clamp(match * basinDepth / MathF.Max(1f, body.MinBasinDepth * 2f), 0.25f, 1f);

            int shoreIdx = resolveBiomeIndex(body.ShoreBiomeName);
            if (shoreIdx < 0) shoreIdx = 0;

            // Score by how well the body fits this column — not by who has the
            // highest water table (that always picked lakes at mountain mid-height).
            float score = match * (1f + basinDepth * 0.05f);
            if (body.Kind == PlanetWaterBodyKind.Ocean)
                score += 0.5f;
            if (score >= bestScore)
            {
                bestScore = score;
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
                    riverR = terrainRadius + 0.35f
                        + (oceanFillR - terrainRadius - 0.35f) * riverMask * 0.25f;

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
                    riverR = terrainRadius + 0.35f;
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
                float bandW = MathF.Max(0f, bank - core);
                bandW = MathF.Max(bandW, core * (1f - core) * 2f);
                if (bandW > bestW)
                {
                    bestW = bandW;
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

                float fillR = body.Kind == PlanetWaterBodyKind.Ocean
                    ? GetOceanFillRadius(config)
                    : FillOceanRadius(config, body.FillFraction);
                float band = MathF.Max(2f, body.ShoreWidth * MathF.Max(1f, config.Radius));

                // Beach sand is dry shoreline only. Anything submerged is covered by
                // the water mesh — painting Beach there is the tan "fake water" users see.
                if (terrainRadius < fillR - 0.25f)
                    continue;

                float distAbove = terrainRadius - fillR;
                float shoreW = 1f - Math.Clamp(distAbove / band, 0f, 1f);
                shoreW = Smooth01(0.2f, 1f, shoreW);
                if (shoreW > bestW)
                {
                    bestW = shoreW;
                    int idx = resolveBiomeIndex(body.ShoreBiomeName);
                    if (idx >= 0) bestIdx = idx;
                }
            }
        }
        else
        {
            float dist = MathF.Abs(terrainRadius - config.SeaLevel);
            float band = MathF.Max(2f, 0.03f * MathF.Max(1f, config.Radius));
            float seaShoreW = Smooth01(0.2f, 1f, 1f - Math.Clamp(dist / band, 0f, 1f));
            if (SpawnWaterWeight(blends) > 0.05f)
                bestW = MathF.Max(bestW, seaShoreW);
        }

        if (bestW <= 0.02f)
            return null;

        float climateScale = biomeMap.SampleShoreClimateWeight(sphereDir);
        float finalW = Math.Clamp(bestW * 0.9f * climateScale, 0f, 1f);
        if (finalW <= 0.02f)
            return null;
        return (bestIdx, finalW);
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
