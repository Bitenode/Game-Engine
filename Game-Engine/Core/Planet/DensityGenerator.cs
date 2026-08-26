using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Fills a VoxelChunk with density on one radial shell of a cube-sphere cone.
/// Each (x,y) maps to UV on the cube-sphere face; z maps to radius.
/// Density &lt; 0 = solid, &gt; 0 = air.  The isosurface is density == 0.
/// </summary>
public sealed class DensityGenerator
{
    /// <summary>Minimum brush radius reserved in the crust so the first paint stroke has volume.</summary>
    public const float DefaultBrushReserve = 16f;

    readonly BiomeMap _biomeMap;
    readonly PlanetConfig _config;
    readonly PlanetNoiseCache _noise;

    public DensityGenerator(PlanetConfig config, BiomeMap biomeMap, PlanetNoiseCache? noise = null)
    {
        _config = config;
        _biomeMap = biomeMap;
        _noise = noise ?? PlanetNoiseCache.Create(config);
    }

    public static float MaxAmplitude(PlanetConfig config)
    {
        float max = 0f;
        foreach (var b in config.Biomes)
            max = MathF.Max(max, b.HeightAmplitude);
        return max;
    }

    public static float MaxCaveDepth(PlanetConfig config)
    {
        float max = MathF.Max(280f, MathF.Max(0f, config.CaveDepth));
        if (config.Biomes == null) return max;
        foreach (var b in config.Biomes)
        {
            if (config.EnableCaves && b.CavesEnabled && b.CaveDensity > 0.01f)
                max = MathF.Max(max, b.CaveDepth);
        }
        return max;
    }

    /// <summary>
    /// Solid fill from near the core out past the surface. Keep a small hollow
    /// at r=0 so cube-sphere samples do not collapse.
    /// </summary>
    public static void ComputeInteriorBounds(
        PlanetConfig config,
        PlanetVoxelEditStore? edits,
        out float radialMin,
        out float radialMax)
    {
        float maxAmp = MaxAmplitude(config);
        float brush = MathF.Max(edits?.MaxRadius ?? 0f, DefaultBrushReserve);
        float outward = maxAmp * 0.85f + brush * 0.35f + 12f;
        radialMax = config.Radius + outward;
        radialMin = MathF.Max(16f, config.Radius * 0.04f);
    }

    public static void ComputeCrustBounds(
        PlanetConfig config,
        PlanetVoxelEditStore? edits,
        out float radialBase,
        out float radialSpan)
    {
        ComputeInteriorBounds(config, edits, out radialBase, out float radialMax);
        radialSpan = MathF.Max(8f, radialMax - radialBase);
    }

    /// <summary>
    /// How many radial voxel shells to stack so each shell keeps ~10 m cells
    /// instead of stretching one 32³ grid from the core to the surface.
    /// </summary>
    public static int RadialLayerCount(float radialMin, float radialMax, int voxelSize)
    {
        float span = MathF.Max(8f, radialMax - radialMin);
        float layerSpan = MathF.Max(48f, voxelSize * 10f);
        return Math.Clamp((int)MathF.Ceiling(span / layerSpan), 1, 4);
    }

    public void Generate(
        VoxelChunk chunk,
        int face,
        float u0,
        float v0,
        float u1,
        float v1,
        int lodLevel,
        PlanetVoxelEditStore? edits = null,
        PlanetDensitySampler? sampler = null,
        float? radialBaseOverride = null,
        float? radialSpanOverride = null)
    {
        int n = chunk.SamplesPerAxis;
        int size = chunk.Size;
        float radius = _config.Radius;

        ComputeInteriorBounds(_config, edits, out float radialMin, out float radialMax);
        float radialBase = radialBaseOverride ?? radialMin;
        float radialSpan = radialSpanOverride ?? MathF.Max(8f, radialMax - radialBase);

        float uStep = (u1 - u0) / size;
        float vStep = (v1 - v0) / size;

        var centreDir = CubeSphereMath.FaceUVToDirection(face, (u0 + u1) * 0.5f, (v0 + v1) * 0.5f);
        var (tangent, bitangent, _) = CubeSphereMath.GetFaceBasis(face);

        chunk.CellSize = EstimateCellSize(face, u0, v0, u1, v1, radius, size);
        chunk.CellSizeZ = radialSpan / size;

        chunk.BasisX = tangent;
        chunk.BasisY = bitangent;
        chunk.BasisZ = centreDir;
        chunk.WorldOrigin = centreDir * radialBase
            - tangent * (size * 0.5f * chunk.CellSize)
            - bitangent * (size * 0.5f * chunk.CellSize);
        chunk.LodLevel = lodLevel;
        chunk.CustomGridToWorld = (x, y, z) =>
        {
            float inv = 1f / size;
            float u = u0 + x * inv * (u1 - u0);
            float v = v0 + y * inv * (v1 - v0);
            float r = radialBase + z * inv * radialSpan;
            return CubeSphereMath.FaceUVToDirection(face, u, v) * r;
        };

        for (int z = 0; z < n; z++)
        {
            float rT = (float)z / size;
            float rDist = radialBase + rT * radialSpan;

            for (int y = 0; y < n; y++)
            {
                float v = v0 + y * vStep;
                for (int x = 0; x < n; x++)
                {
                    float u = u0 + x * uStep;

                    SN.Vector3 sphereDir = CubeSphereMath.FaceUVToDirection(face, u, v);

                    BiomeBlend[] blends = _biomeMap.GetBiomes(sphereDir);

                    SN.Vector3 localPos = sphereDir * rDist;
                    float density = sampler != null
                        ? sampler.SampleProceduralDensity(localPos)
                        : localPos.Length() - (radius + PlanetSurfaceUtility.SampleHeight(
                            _config,
                            _biomeMap,
                            _noise.BiomeNoises,
                            _noise.ErosionNoise,
                            _noise.RidgeNoise,
                            _noise.BasinNoise,
                            sphereDir));
                    byte dominantMat = 0;
                    float dominantWeight = 0f;

                    for (int b = 0; b < blends.Length; b++)
                    {
                        var biome = blends[b].Biome;
                        float w = blends[b].Weight;
                        if (w > dominantWeight)
                        {
                            dominantWeight = w;
                            dominantMat = biome.BiomeIndex;
                        }
                    }

                    if (sampler != null)
                        density = sampler.ApplyCaveCarve(localPos, density);

                    chunk.Set(x, y, z, density);
                    chunk.SetMaterial(x, y, z, dominantMat);
                }
            }
        }
    }

    static float EstimateCellSize(int face, float u0, float v0, float u1, float v1, float radius, int chunkSize)
    {
        var a = CubeSphereMath.FaceUVToDirection(face, u0, v0) * radius;
        var b = CubeSphereMath.FaceUVToDirection(face, u1, v0) * radius;
        var c = CubeSphereMath.FaceUVToDirection(face, u0, v1) * radius;
        float sizeU = SN.Vector3.Distance(a, b);
        float sizeV = SN.Vector3.Distance(a, c);
        return Math.Max(sizeU, sizeV) / chunkSize;
    }
}
