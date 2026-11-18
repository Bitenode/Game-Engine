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
        float inv = 1f / MathF.Max(1, res - 1);
        Span<float> weights = stackalloc float[8];

        for (int face = 0; face < 6; face++)
        {
            var fHeight = surface.Height[face];
            var fSplat0 = surface.Splat0[face];
            var fSplat1 = surface.Splat1[face];

            for (int y = 0; y < res; y++)
            {
                float v = y * inv;
                for (int x = 0; x < res; x++)
                {
                    float u = x * inv;
                    var dir = CubeSphereMath.FaceUVToDirection(face, u, v);
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
            }
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
