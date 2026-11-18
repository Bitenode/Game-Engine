#nullable enable
using System;
using Game_Engine.Core.Biome;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Bakes <see cref="PlanetSurfaceCubemap"/> height + 8-weight splat from the biome graph.
/// Climate LUTs remain <see cref="PlanetClimateAtlas.Bake"/> (companion, not mesh-driving).
/// </summary>
public static class PlanetCubemapBaker
{
    public readonly struct BakeResult
    {
        public BakeResult(PlanetSurfaceCubemap surface, PlanetClimateAtlas climate)
        {
            Surface = surface;
            Climate = climate;
        }

        public PlanetSurfaceCubemap Surface { get; }
        public PlanetClimateAtlas Climate { get; }
    }

    public static BakeResult Bake(
        PlanetConfig config,
        BiomeMap biomeMap,
        PlanetNoiseCache? noise,
        int resolution = PlanetSurfaceCubemap.DefaultResolution,
        PlanetSurfaceCubemap? preserveDeltasFrom = null)
    {
        var surface = BakeSurface(config, biomeMap, noise, resolution, preserveDeltasFrom);
        int climateRes = Math.Min(256, surface.Resolution);
        var climate = PlanetClimateAtlas.Bake(config, biomeMap, noise, climateRes);
        return new BakeResult(surface, climate);
    }

    public static PlanetSurfaceCubemap BakeSurface(
        PlanetConfig config,
        BiomeMap biomeMap,
        PlanetNoiseCache? noise,
        int resolution = PlanetSurfaceCubemap.DefaultResolution,
        PlanetSurfaceCubemap? preserveDeltasFrom = null)
    {
        resolution = Math.Clamp(resolution, PlanetSurfaceCubemap.MinResolution, PlanetSurfaceCubemap.MaxResolution);
        var surface = new PlanetSurfaceCubemap(resolution, config.RecipeHash);

        int res = surface.Resolution;

        // Every texel is worked out from its own direction alone, so rows can be baked on
        // several cores and the result is the same whatever order they finish in. Cores
        // are left for the game thread and the chunk jobs that run while a bake is under way.
        var options = new System.Threading.Tasks.ParallelOptions
        {
#if XBOX_UWP
            // The console bakes during the first seconds of play, with chunk jobs
            // and the frame itself competing for the same cores.
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2 - 1)
#else
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2)
#endif
        };

        for (int face = 0; face < 6; face++)
        {
            int faceIndex = face;
            var fHeight = surface.Height[face];
            var fSplat0 = surface.Splat0[face];
            var fSplat1 = surface.Splat1[face];

            System.Threading.Tasks.Parallel.For(0, res, options, y =>
            {
                Span<float> weights = stackalloc float[8];
                for (int x = 0; x < res; x++)
                {
                    // The direction this texel is read back for, not the chunk-grid direction.
                    // PlanetDensitySampler works out the same texels on demand before the
                    // bake is ready, so both must take the direction from this one place.
                    var dir = PlanetSurfaceCubemap.TexelDirection(res, faceIndex, x, y);
                    int idx = y * res + x;

                    float height = 0f;
                    if (noise != null)
                    {
                        height = PlanetSurfaceUtility.SampleHeight(
                            config, biomeMap,
                            noise.BiomeNoises, noise.ErosionNoise,
                            noise.RidgeNoise, noise.BasinNoise, dir);
                    }

                    fHeight[idx] = height;

                    float alt = biomeMap.NormalizeAltitude(height);
                    var blends = biomeMap.GetBiomes(dir, alt);
                    WriteNormalizedSplat(blends, weights);
                    int s = idx * 4;
                    fSplat0[s] = weights[0];
                    fSplat0[s + 1] = weights[1];
                    fSplat0[s + 2] = weights[2];
                    fSplat0[s + 3] = weights[3];
                    fSplat1[s] = weights[4];
                    fSplat1[s + 1] = weights[5];
                    fSplat1[s + 2] = weights[6];
                    fSplat1[s + 3] = weights[7];
                }
            });
        }

        if (preserveDeltasFrom != null)
            surface.CopyHeightDeltasFrom(preserveDeltasFrom);

        surface.MarkBaseHeightsReady();
        surface.BumpVersion();
        return surface;
    }

    /// <summary>
    /// Map top biome blends into 8 splat slots by <see cref="BiomeDefinition.BiomeIndex"/>.
    /// Unused layers stay 0; weights are renormalized.
    /// </summary>
    public static void WriteNormalizedSplat(BiomeBlend[] blends, Span<float> weights)
    {
        for (int i = 0; i < 8; i++)
            weights[i] = 0f;

        if (blends == null || blends.Length == 0)
        {
            weights[0] = 1f;
            return;
        }

        float sum = 0f;
        for (int b = 0; b < blends.Length && b < 4; b++)
        {
            int slot = Math.Clamp((int)blends[b].Biome.BiomeIndex, 0, 7);
            float w = MathF.Max(0f, blends[b].Weight);
            weights[slot] += w;
            sum += w;
        }

        if (sum < 1e-5f)
        {
            weights[0] = 1f;
            return;
        }

        float inv = 1f / sum;
        for (int i = 0; i < 8; i++)
            weights[i] *= inv;
    }
}
