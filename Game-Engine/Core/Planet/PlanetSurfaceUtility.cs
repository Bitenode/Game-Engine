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
        height = ApplyIceSheetRaise(config, sphereDir, height, biomeMap);
        return height;
    }

    /// <summary>
    /// Raise cold polar crust slightly when IceSheet recipes are present.
    /// </summary>
    public static float ApplyIceSheetRaise(PlanetConfig config, SN.Vector3 sphereDir, float height, BiomeMap? biomeMap)
    {
        if (config.IceSheets is not { Length: > 0 } || biomeMap == null)
            return height;

        float polar = Math.Clamp((MathF.Abs(sphereDir.Y) - 0.86f) / 0.10f, 0f, 1f);
        if (polar <= 0.02f)
            return height;

        float alt = Math.Clamp(height / Math.Max(20f, 80f), 0f, 1f);
        float temp = biomeMap.GetTemperature(sphereDir, alt);
        float raise = 0f;
        for (int i = 0; i < config.IceSheets.Length; i++)
        {
            var s = config.IceSheets[i];
            if (temp > s.MaxTemperature)
                continue;
            float cold = Math.Clamp((s.MaxTemperature - temp) / Math.Max(0.05f, s.MaxTemperature), 0f, 1f);
            float cov = Math.Clamp(s.Coverage, 0f, 1f);
            raise = MathF.Max(raise, cold * cov * polar * Math.Clamp(s.Thickness, 0f, 80f));
        }
        return height + raise;
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
            float inland = Smooth01(0.48f, 0.76f, land);
            float r1 = 1f - MathF.Abs(n.Noise3D(
                sphereDir.X * 2.3f + 1.4f, sphereDir.Y * 2.3f, sphereDir.Z * 2.3f - 0.7f));
            float r2 = 1f - MathF.Abs(n.Noise3D(
                sphereDir.X * 6.1f - 4.2f, sphereDir.Y * 6.1f + 2.1f, sphereDir.Z * 6.1f));
            float ranges = r1 * r1 * (0.5f + 0.5f * r2 * r2);
            height += ranges * inland * 42f;

            // Inland massifs + strata. Keep this off the coastal band so LOD
            // does not turn high-frequency ridges into pyramid teeth.
            float massif = inland * Smooth01(0.22f, 0.62f, ranges);
            if (massif > 0.04f)
            {
                float m0 = 1f - MathF.Abs(n.Noise3D(
                    sphereDir.X * 1.08f - 3.1f, sphereDir.Y * 1.08f + 0.6f, sphereDir.Z * 1.08f + 2.2f));
                m0 *= m0;
                float m1 = 1f - MathF.Abs(n.Noise3D(
                    sphereDir.X * 3.55f + 8.4f, sphereDir.Y * 3.55f - 1.7f, sphereDir.Z * 3.55f));
                float crag = m0 * (0.58f + 0.42f * m1 * m1);
                height += crag * massif * 34f;

                float terrace = n.Noise3D(
                    sphereDir.X * 4.6f + 19f, sphereDir.Y * 4.6f, sphereDir.Z * 4.6f - 6f) * 0.5f + 0.5f;
                float step = MathF.Floor(terrace * 6f) / 6f;
                height += (step - 0.5f) * 8f * massif * crag;

                float fine = 1f - MathF.Abs(n.Noise3D(
                    sphereDir.X * 8.4f - 11f, sphereDir.Y * 8.4f + 4.1f, sphereDir.Z * 8.4f));
                height += fine * fine * crag * massif * 8f;
            }
        }

        // Volcanoes before craters so impact bowls cannot replace the caldera,
        // and so lava / cliff protection share one influence field.
        float volcanoInfluence = 0f;
        bool insideVolcanoCone = false;
        var volcanoes = config.Volcanoes;
        if (volcanoes != null && n != null)
        {
            for (int i = 0; i < volcanoes.Length; i++)
            {
                var v = volcanoes[i];
                if (!TryMatchInlandVolcano(config, n, sphereDir, v, i, out float t, out var hit))
                    continue;
                insideVolcanoCone = true;
                float peak = VolcanoHeightShape(t, v.CalderaRadius, v.Radius);
                float columnFade = VolcanoColumnFade(land);
                float add = v.Height * peak * hit.Inland * columnFade;
                height += add;
                if (peak * hit.Inland > volcanoInfluence)
                    volcanoInfluence = peak * hit.Inland;
            }
        }

        var craters = config.Craters;
        if (craters != null && n != null)
        {
            // Don't let impact craters dig out the caldera the lava lake sits in.
            float craterFade = 1f - Smooth01(0.08f, 0.40f, volcanoInfluence);
            if (craterFade > 0.02f)
            {
                for (int i = 0; i < craters.Length; i++)
                {
                    var c = craters[i];
                    float angR = FeatureAngularRadius(c.Radius, config.Radius);
                    if (!TryNearestFeature(n, sphereDir, angR, c.Density, 17.3f + i * 3.1f, out float t, out _))
                        continue;
                    float bowl = 1f - t;
                    height -= c.Depth * bowl * bowl * craterFade;
                    height += c.RimHeight * bowl * (1f - bowl) * 4f * craterFade;
                }
            }
        }

        var cliffs = config.Cliffs;
        // Never carve cliffs through a volcano — that shears the caldera and
        // parks the cone on the continent rim.
        if (cliffs != null && n != null && !insideVolcanoCone && land > 0.35f && land < 0.78f)
        {
            float cliffFade = 1f - Smooth01(0.02f, 0.18f, volcanoInfluence);
            if (cliffFade > 0.02f)
            {
                for (int i = 0; i < cliffs.Length; i++)
                {
                    var cl = cliffs[i];
                    float freq = MathF.Max(0.001f, cl.Frequency) * config.Radius * 0.55f;
                    float n0 = n.Noise3D(
                        sphereDir.X * freq + 9.1f,
                        sphereDir.Y * freq + 3.7f,
                        sphereDir.Z * freq + 5.2f);
                    float n1 = n.Noise3D(
                        sphereDir.X * freq * 2.1f - 2.4f,
                        sphereDir.Y * freq * 2.1f + 1.3f,
                        sphereDir.Z * freq * 2.1f + 4.8f);
                    float detail = (n0 * 0.5f + 0.5f) * 0.72f + (n1 * 0.5f + 0.5f) * 0.28f;
                    detail = detail * detail;
                    float drop = 1f - Smooth01(0.42f, 0.58f, land);
                    float lip = Smooth01(0.50f, 0.64f, land) * (1f - Smooth01(0.64f, 0.78f, land));
                    float wall = cl.Strength * cl.SlopeBias * cliffFade;
                    height -= drop * wall * (11f + detail * 3.5f);
                    height += lip * wall * (8f + detail * 4.5f);
                }
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
    /// True ocean basin only. Cliff-carved drops on the continent stay dry so
    /// the sea sheet cannot walk up the wall.
    /// </summary>
    public static bool IsOceanBasinColumn(PlanetConfig config, SN.Vector3 sphereDir)
    {
        if (config.Continents is not { Length: > 0 })
            return true;
        return SampleContinentLand(config, sphereDir) <= 0.38f;
    }

    /// <summary>
    /// 0–1 if this column is inside a volcano caldera bowl (not the outer cone).
    /// </summary>
    public static float SampleMagmaBowl(PlanetConfig config, SN.Vector3 sphereDir)
        => TryGetLavaLake(config, sphereDir, 0f, out _, out float mask, out _) ? mask : 0f;

    /// <summary>
    /// 0–1 rock mask for volcano cones and calderas (kills grass splat / flora).
    /// </summary>
    public static float SampleVolcanoRockMask(PlanetConfig config, SN.Vector3 sphereDir)
    {
        var n = config.GeologyNoise;
        if (n == null || config.Volcanoes == null || config.Volcanoes.Length == 0)
            return 0f;
        if (sphereDir.LengthSquared() < 1e-12f)
            return 0f;
        sphereDir = SN.Vector3.Normalize(sphereDir);

        float best = SampleMagmaBowl(config, sphereDir);
        float land = SampleContinentLand(config, sphereDir, n);
        float columnFade = VolcanoColumnFade(land);
        for (int i = 0; i < config.Volcanoes.Length; i++)
        {
            var v = config.Volcanoes[i];
            if (!TryMatchInlandVolcano(config, n, sphereDir, v, i, out float t, out var hit))
                continue;
            float peak = VolcanoHeightShape(t, v.CalderaRadius, v.Radius) * hit.Inland * columnFade;
            if (peak > best)
                best = peak;
        }
        return Math.Clamp(best, 0f, 1f);
    }

    /// <summary>
    /// Lava lake in the caldera hole only. Outer cone walls and rim rock stay dry.
    /// <paramref name="terrainRadius"/> 0 skips the below-lake test (mask-only queries).
    /// </summary>
    public static bool TryGetLavaLake(
        PlanetConfig config,
        SN.Vector3 sphereDir,
        float terrainRadius,
        out float lavaRadius,
        out float mask)
        => TryGetLavaLake(config, sphereDir, terrainRadius, out lavaRadius, out mask, out _);

    public static bool TryGetLavaLake(
        PlanetConfig config,
        SN.Vector3 sphereDir,
        float terrainRadius,
        out float lavaRadius,
        out float mask,
        out string? lavaBiomeName)
    {
        lavaRadius = 0f;
        mask = 0f;
        lavaBiomeName = null;
        var n = config.GeologyNoise;
        if (n == null || sphereDir.LengthSquared() < 1e-12f)
            return false;
        sphereDir = SN.Vector3.Normalize(sphereDir);
        float land = SampleContinentLand(config, sphereDir, n);
        if (land < 0.55f)
            return false;

        var volcanoes = config.Volcanoes;
        if (volcanoes == null || volcanoes.Length == 0)
            return false;

        // Lake surface fraction of volcano height (floor is ~0.18, rim is 1.0).
        const float lakePeak = 0.42f;
        float columnFade = VolcanoColumnFade(land);
        if (columnFade < 0.35f)
            return false;

        float best = 0f;
        float bestR = 0f;
        string? bestBiome = null;
        for (int i = 0; i < volcanoes.Length; i++)
        {
            var v = volcanoes[i];
            if (!TryMatchInlandVolcano(config, n, sphereDir, v, i, out float t, out var hit))
                continue;

            float caldera = VolcanoCalderaFraction(v.CalderaRadius, v.Radius);
            if (t >= caldera * 0.88f)
                continue;

            float peak = VolcanoHeightShape(t, v.CalderaRadius, v.Radius);
            if (peak >= lakePeak - 0.01f)
                continue;

            // Flat lake: reconstruct background + constant lake height from the
            // volcano center. Adding depth onto local crust draped lava on rims
            // whenever cliffs or the continent shelf sheared the bowl.
            float volcanoAdd = v.Height * peak * hit.Inland * columnFade;
            float lakeAdd = lakePeak * v.Height * hit.Inland;
            if (lakeAdd - volcanoAdd < 0.35f)
                continue;

            float lakeR = terrainRadius > 1f
                ? terrainRadius - volcanoAdd + lakeAdd
                : config.Radius + lakeAdd;

            if (terrainRadius > 1f && terrainRadius >= lakeR - 0.06f)
                continue;

            float inner = 1f - t / MathF.Max(1e-4f, caldera * 0.88f);
            if (inner > best)
            {
                best = inner;
                bestR = lakeR;
                bestBiome = string.IsNullOrWhiteSpace(v.LavaBiomeName) ? "Volcanic" : v.LavaBiomeName;
            }
        }

        if (best < 0.08f)
            return false;
        mask = Math.Clamp(best, 0f, 1f);
        lavaRadius = bestR > 1f ? bestR : config.Radius + 4f;
        lavaBiomeName = bestBiome;
        return true;
    }

    /// <summary>
    /// Continuous stratovolcano: rim height 1 at the caldera edge, depressed floor inside,
    /// smooth outer skirts (no discontinuous jump that offset lava from the bowl).
    /// </summary>
    static float VolcanoHeightShape(float t, float calderaRadius, float radius)
    {
        float caldera = VolcanoCalderaFraction(calderaRadius, radius);
        if (t < caldera)
        {
            float u = t / MathF.Max(1e-4f, caldera);
            // Floor 0.18 at center → 1.0 at rim (matches outer cone at the edge).
            return 0.18f + 0.82f * (u * u);
        }

        float o = (t - caldera) / MathF.Max(1e-4f, 1f - caldera);
        float skirt = MathF.Max(0f, 1f - o);
        return skirt * skirt;
    }

    static float VolcanoCalderaFraction(float calderaRadius, float radius)
        => Math.Clamp(calderaRadius / MathF.Max(0.01f, radius), 0.14f, 0.48f);

    static float VolcanoFeatureOffset(int index, int seed)
        => 41.7f + index * 2.4f + (seed & 1023) * 0.01f;

    /// <summary>
    /// Coastal cliff band is land 0.35–0.78. Centers must sit fully inland of that.
    /// </summary>
    static float VolcanoInlandMask(float land)
        => Smooth01(0.62f, 0.78f, land);

    /// <summary>Fade the cone before it reaches the coastal shelf / cliff face.</summary>
    static float VolcanoColumnFade(float land)
        => Smooth01(0.50f, 0.68f, land);

    readonly struct InlandVolcanoHit
    {
        public InlandVolcanoHit(float inland, float centerLand)
        {
            Inland = inland;
            CenterLand = centerLand;
        }

        public float Inland { get; }
        public float CenterLand { get; }
    }

    static bool TryMatchInlandVolcano(
        PlanetConfig config,
        SimplexNoise n,
        SN.Vector3 sphereDir,
        Game_Engine.Core.Biome.Graph.VolcanoRecipe v,
        int index,
        out float t,
        out InlandVolcanoHit hit)
    {
        t = 1f;
        hit = default;
        float angR = FeatureAngularRadius(v.Radius, config.Radius);
        float featOff = VolcanoFeatureOffset(index, v.Seed);
        if (!TryNearestInlandVolcanoFeature(
                config, n, sphereDir, angR, v.Density, featOff, v.CalderaRadius, v.Radius,
                out t, out var featDir))
            return false;

        float centerLand = SampleContinentLand(config, featDir, n);
        float inland = VolcanoInlandMask(centerLand);
        if (centerLand < 0.72f || inland < 0.2f)
            return false;
        hit = new InlandVolcanoHit(inland, centerLand);
        return true;
    }

    static bool TryNearestInlandVolcanoFeature(
        PlanetConfig config,
        SimplexNoise n,
        SN.Vector3 dir,
        float angRadius,
        float density,
        float offset,
        float calderaRadius,
        float volcanoRadius,
        out float t,
        out SN.Vector3 featDir)
    {
        t = 1f;
        featDir = dir;
        int count = Math.Clamp((int)(12f + Math.Clamp(density, 0.05f, 1f) * 48f), 12, 36);
        float best = 4f;
        var bestFeat = dir;
        bool found = false;
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
            if (!VolcanoCenterFitsInland(config, n, feat, angRadius, calderaRadius, volcanoRadius))
                continue;

            float d = MathF.Acos(Math.Clamp(SN.Vector3.Dot(dir, feat), -1f, 1f));
            if (d < best)
            {
                best = d;
                bestFeat = feat;
                found = true;
            }
        }

        if (!found || best > angRadius)
            return false;
        featDir = bestFeat;
        t = best / MathF.Max(1e-4f, angRadius);
        return true;
    }

    static bool VolcanoCenterFitsInland(
        PlanetConfig config,
        SimplexNoise n,
        SN.Vector3 featDir,
        float angRadius,
        float calderaRadius,
        float volcanoRadius)
    {
        if (SampleContinentLand(config, featDir, n) < 0.72f)
            return false;

        var seed = MathF.Abs(featDir.Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
        var t0 = SN.Vector3.Cross(seed, featDir);
        if (t0.LengthSquared() < 1e-10f)
            return true;
        t0 = SN.Vector3.Normalize(t0);
        var t1 = SN.Vector3.Cross(featDir, t0);

        // Only the caldera must stay off the cliff/ocean. Requiring the full
        // cone plus a wide margin rejected every candidate on this planet.
        float caldera = VolcanoCalderaFraction(calderaRadius, volcanoRadius);
        float ring = MathF.Max(0.016f, angRadius * caldera * 1.2f);
        float cr = MathF.Cos(ring);
        float sr = MathF.Sin(ring);
        for (int k = 0; k < 6; k++)
        {
            float a = k * (MathF.PI / 3f);
            var p = SN.Vector3.Normalize(featDir * cr + (t0 * MathF.Cos(a) + t1 * MathF.Sin(a)) * sr);
            if (SampleContinentLand(config, p, n) < 0.58f)
                return false;
        }
        return true;
    }

    static float FeatureAngularRadius(float radius, float planetRadius)
        => radius > 1.5f
            ? radius / MathF.Max(1f, planetRadius)
            : MathF.Max(0.02f, radius);

    static bool TryNearestFeature(
        SimplexNoise n, SN.Vector3 dir, float angRadius, float density, float offset,
        out float t, out float ang)
        => TryNearestFeature(n, dir, angRadius, density, offset, out t, out ang, out _);

    static bool TryNearestFeature(
        SimplexNoise n, SN.Vector3 dir, float angRadius, float density, float offset,
        out float t, out float ang, out SN.Vector3 featDir)
    {
        t = 1f;
        ang = 99f;
        featDir = dir;
        int count = Math.Clamp((int)(6f + Math.Clamp(density, 0.05f, 1f) * 36f), 5, 24);
        float best = 4f;
        var bestFeat = dir;
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
            if (d < best)
            {
                best = d;
                bestFeat = feat;
            }
        }

        if (best > angRadius)
            return false;
        featDir = bestFeat;
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
