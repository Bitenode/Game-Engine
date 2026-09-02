#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Noise;

namespace Game_Engine.Core.Biome.Graph;

/// <summary>
/// Compile-time / bake-time sample context for evaluating float ports.
/// Not used per-voxel at runtime — only while compiling a <see cref="BiomeGraphResult"/>.
/// </summary>
public readonly struct BiomeEvalContext
{
    public float Latitude { get; init; }
    public float Longitude { get; init; }
    public float Altitude { get; init; }
    public float SphereX { get; init; }
    public float SphereY { get; init; }
    public float SphereZ { get; init; }
    public int Seed { get; init; }

    public static BiomeEvalContext EquatorMid => new()
    {
        Latitude = 0f,
        Longitude = 0f,
        Altitude = 0.35f,
        SphereX = 1f,
        SphereY = 0f,
        SphereZ = 0f,
        Seed = 0
    };
}

/// <summary>
/// Walks graph connections and evaluates float math/noise/blend nodes at compile time.
/// </summary>
public static class BiomeGraphEvaluator
{
    /// <summary>Compile-time float walk. Alias used by <see cref="BiomeGraph.EvaluateFloat"/>.</summary>
    public static float EvaluateFloat(BiomePort? port, in BiomeEvalContext ctx, Dictionary<string, float>? cache = null) =>
        EvaluatePort(port, in ctx, cache);

    public static float EvaluatePort(BiomePort? port, in BiomeEvalContext ctx, Dictionary<string, float>? cache = null)
    {
        if (port == null) return 0f;
        if (!port.IsOutput)
        {
            if (port.Connection != null)
                return EvaluatePort(port.Connection, in ctx, cache);
            return port.DefaultValue.Length > 0 ? port.DefaultValue[0] : 0f;
        }

        cache ??= new Dictionary<string, float>(64);
        string key = port.Owner.Id + ":" + port.Name;
        if (cache.TryGetValue(key, out float hit))
            return hit;

        float value = EvaluateOutput(port.Owner, port, in ctx, cache);
        cache[key] = value;
        return value;
    }

    public static float EvaluateInput(BiomeNode node, int index, in BiomeEvalContext ctx, Dictionary<string, float>? cache = null)
    {
        if (index < 0 || index >= node.Inputs.Count)
            return 0f;
        return EvaluatePort(node.Inputs[index], in ctx, cache);
    }

    static float EvaluateOutput(BiomeNode node, BiomePort port, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        switch (node)
        {
            case BiomeCoordinateNode:
                return port.Name switch
                {
                    "Latitude" => ctx.Latitude,
                    "Longitude" => ctx.Longitude,
                    "Altitude" => ctx.Altitude,
                    _ => ctx.Latitude
                };

            case BiomeNoiseNode nn:
            {
                float freq = MathF.Max(0.00001f, ResolvePropOrInput(nn, 0, nn.Frequency, in ctx, cache));
                int octaves = Math.Clamp((int)ResolvePropOrInput(nn, 1, nn.Octaves, in ctx, cache), 1, 12);
                var noise = new SimplexNoise(ctx.Seed + nn.Seed + 17);
                float x = ctx.SphereX * freq * 1000f;
                float y = ctx.SphereY * freq * 1000f;
                float z = ctx.SphereZ * freq * 1000f;
                float amp = 1f, sum = 0f, norm = 0f;
                float lac = 2f, pers = 0.5f;
                for (int o = 0; o < octaves; o++)
                {
                    float n = noise.Noise3D(x, y, z);
                    if (nn.NoiseMode == "Ridged") n = 1f - MathF.Abs(n);
                    else if (nn.NoiseMode == "Billow") n = MathF.Abs(n);
                    sum += n * amp;
                    norm += amp;
                    amp *= pers;
                    x *= lac; y *= lac; z *= lac;
                }
                return norm > 1e-6f ? sum / norm : 0f;
            }

            case BiomeTemperatureNode tn:
            {
                float latW = ResolvePropOrInput(tn, 0, tn.LatitudeWeight, in ctx, cache);
                float noiseW = ResolvePropOrInput(tn, 1, tn.NoiseWeight, in ctx, cache);
                float lat = 1f - MathF.Abs(ctx.Latitude);
                var noise = new SimplexNoise(ctx.Seed + 5000);
                float n = noise.Noise3D(ctx.SphereX * 2f, ctx.SphereY * 2f, ctx.SphereZ * 2f) * 0.5f + 0.5f;
                return Math.Clamp(lat * latW * (1f - noiseW) + n * noiseW, 0f, 1f);
            }

            case BiomeMoistureNode mn:
            {
                float scale = ResolvePropOrInput(mn, 0, mn.NoiseScale, in ctx, cache);
                var noise = new SimplexNoise(ctx.Seed + 6000);
                float n = noise.Noise3D(ctx.SphereX * scale, ctx.SphereY * scale, ctx.SphereZ * scale);
                return Math.Clamp(n * 0.5f + 0.5f, 0f, 1f);
            }

            case BiomeHeightNode hn:
            {
                float noise = EvaluateInput(hn, 0, in ctx, cache);
                float baseH = ResolvePropOrInput(hn, 1, hn.BaseHeight, in ctx, cache);
                float amp = ResolvePropOrInput(hn, 2, hn.Amplitude, in ctx, cache);
                return baseH + noise * amp;
            }

            case BiomeAltitudeNode an:
            {
                float raw = EvaluateInput(an, 0, in ctx, cache);
                float sea = ResolvePropOrInput(an, 1, an.SeaLevel, in ctx, cache);
                float maxH = MathF.Max(1e-4f, ResolvePropOrInput(an, 2, an.MaxHeight, in ctx, cache));
                return Math.Clamp((raw - sea) / maxH, 0f, 1f);
            }

            case BiomeBlendNode:
            {
                float a = EvaluateInput(node, 0, in ctx, cache);
                float b = EvaluateInput(node, 1, in ctx, cache);
                float w = Math.Clamp(EvaluateInput(node, 2, in ctx, cache), 0f, 1f);
                return a * (1f - w) + b * w;
            }

            case BiomeMathNode math:
            {
                float a = EvaluateInput(math, 0, in ctx, cache);
                float b = EvaluateInput(math, 1, in ctx, cache);
                return math.Operation switch
                {
                    BiomeMathOp.Add => a + b,
                    BiomeMathOp.Subtract => a - b,
                    BiomeMathOp.Multiply => a * b,
                    BiomeMathOp.Divide => MathF.Abs(b) < 1e-8f ? 0f : a / b,
                    BiomeMathOp.Clamp => Math.Clamp(a, 0f, b > 0f ? b : 1f),
                    BiomeMathOp.Smoothstep => Smoothstep(0f, b > 0f ? b : 1f, a),
                    BiomeMathOp.Remap => Remap(a, 0f, 1f, 0f, b),
                    BiomeMathOp.Min => MathF.Min(a, b),
                    BiomeMathOp.Max => MathF.Max(a, b),
                    _ => a + b
                };
            }

            case BiomeMaskNode mask:
            {
                float a = EvaluateInput(mask, 0, in ctx, cache);
                float b = EvaluateInput(mask, 1, in ctx, cache);
                return mask.BlendMode switch
                {
                    BiomeMaskBlendMode.Add => Math.Clamp(a + b, 0f, 1f),
                    BiomeMaskBlendMode.Multiply => a * b,
                    BiomeMaskBlendMode.Screen => 1f - (1f - a) * (1f - b),
                    BiomeMaskBlendMode.Overlay => a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b),
                    BiomeMaskBlendMode.Subtract => Math.Clamp(a - b, 0f, 1f),
                    _ => a * b
                };
            }

            case BiomeErosionNode en:
            {
                float strength = ResolvePropOrInput(en, 0, en.Strength, in ctx, cache);
                float freq = ResolvePropOrInput(en, 1, en.Frequency, in ctx, cache);
                var noise = new SimplexNoise(ctx.Seed + 9000);
                float e = noise.Noise3D(ctx.SphereX * freq * 1000f, ctx.SphereY * freq * 1000f, ctx.SphereZ * freq * 1000f);
                return Math.Clamp((e * 0.5f + 0.5f) * strength, 0f, 1f);
            }

            case BiomeSlopeNode sn:
                return sn.SlopeScale * 0.5f;

            case BiomeCaveNode cn:
                return ResolvePropOrInput(cn, 1, cn.Threshold, in ctx, cache);

            case BiomeSelectNode:
            {
                float t = EvaluateInput(node, 0, in ctx, cache);
                float m = EvaluateInput(node, 1, in ctx, cache);
                float a = EvaluateInput(node, 2, in ctx, cache);
                // Pack a continuous classifier hint; runtime BiomeMap uses T/M/A separately.
                return Math.Clamp(t * 0.45f + m * 0.35f + a * 0.2f, 0f, 1f);
            }

            case BiomeRiverNode rn:
                return port.Name == "RiverDepth" ? rn.RiverDepth : rn.RiverWidth;

            case BiomeContinentNode cn:
            {
                float freq = ResolvePropOrInput(cn, 0, cn.Frequency, in ctx, cache);
                float thresh = ResolvePropOrInput(cn, 1, cn.Threshold, in ctx, cache);
                var noise = new SimplexNoise(ctx.Seed + cn.Seed + 11000);
                float n = noise.Noise3D(ctx.SphereX * freq * 1000f, ctx.SphereY * freq * 1000f, ctx.SphereZ * freq * 1000f);
                return n * 0.5f + 0.5f >= thresh ? cn.Strength : 0f;
            }

            case BiomeCraterNode crater:
                return -ResolvePropOrInput(crater, 1, crater.Depth, in ctx, cache) * crater.Density;

            case BiomeVolcanoNode volcano:
                return ResolvePropOrInput(volcano, 1, volcano.Height, in ctx, cache) * volcano.Density;

            case BiomeCliffNode cliff:
                return ResolvePropOrInput(cliff, 0, cliff.Strength, in ctx, cache) * cliff.SlopeBias;

            case BiomeDomainWarpNode warp:
            {
                float value = EvaluateInput(warp, 0, in ctx, cache);
                float strength = ResolvePropOrInput(warp, 1, warp.Strength, in ctx, cache);
                var noise = new SimplexNoise(ctx.Seed + warp.Seed + 12000);
                float w = noise.Noise3D(ctx.SphereX * warp.Frequency * 1000f, ctx.SphereY * warp.Frequency * 1000f, ctx.SphereZ * warp.Frequency * 1000f);
                return value + w * strength;
            }

            case BiomeClimateNode climate:
            {
                float t = EvaluateInput(climate, 0, in ctx, cache);
                float m = EvaluateInput(climate, 1, in ctx, cache);
                float a = EvaluateInput(climate, 2, in ctx, cache);
                float cooled = t - a * climate.AltitudeLapse;
                return Math.Clamp(cooled * climate.LatitudeWeight * 0.5f + m * climate.MoistureWeight * 0.5f, 0f, 1f);
            }

            case BiomeRainShadowNode rs:
            {
                float moisture = EvaluateInput(rs, 0, in ctx, cache);
                float strength = ResolvePropOrInput(rs, 1, rs.Strength, in ctx, cache);
                return Math.Clamp(moisture * (1f - strength * 0.5f), 0f, 1f);
            }

            case BiomeSeasonNode season:
                return port.Name == "SnowLine" ? season.SnowLineAltitude : season.GrowthMultiplier;

            case BiomeLatitudeBandNode band:
            {
                bool inside = ctx.Latitude >= band.MinLatitude && ctx.Latitude <= band.MaxLatitude;
                return inside ? 1f : 0f;
            }

            case BiomeFloraLayerNode flora:
                return flora.GrassDensity * 0.5f + flora.TreeDensity * 0.5f;

            case BiomeScatterLayerNode scatter:
                return scatter.RockDensity;

            case BiomeFaunaLayerNode fauna:
                return fauna.Density;

            case BiomeUnderwaterLifeNode uw:
                return uw.KelpDensity;

            case BiomeResourceVeinNode vein:
                return vein.Density;

            case BiomeAtmosphereNode atm:
                return atm.RayleighStrength;

            case BiomeWeatherProfileNode wp:
                return wp.RainChance;

            case BiomeCloudLayerNode cloud:
                return ResolvePropOrInput(cloud, 0, cloud.Coverage, in ctx, cache);

            case BiomeIceSheetNode ice:
                return ice.Coverage;

            case BiomeWetlandNode wet:
                return wet.ReedDensity;

            default:
                if (port.DefaultValue.Length > 0)
                    return port.DefaultValue[0];
                return 0f;
        }
    }

    static float ResolvePropOrInput(BiomeNode node, int inputIndex, float prop, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        if (inputIndex < node.Inputs.Count && node.Inputs[inputIndex].Connection != null)
            return EvaluateInput(node, inputIndex, in ctx, cache);
        return prop;
    }

    static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-6f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static float Remap(float v, float inMin, float inMax, float outMin, float outMax)
    {
        float t = (v - inMin) / MathF.Max(1e-6f, inMax - inMin);
        return outMin + t * (outMax - outMin);
    }
}
