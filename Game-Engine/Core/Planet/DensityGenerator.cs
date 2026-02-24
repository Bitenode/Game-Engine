using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Fills a VoxelChunk with density values on a spherical shell.
/// Each (x,y) grid cell maps to a UV position on the cube-sphere face,
/// and z maps to a radial distance from the planet center.
/// Density &lt; 0 = solid, &gt; 0 = air.  The isosurface is density == 0.
/// </summary>
public sealed class DensityGenerator
{
    readonly FractalNoise _heightNoise;
    readonly FractalNoise _caveNoise;
    readonly FractalNoise _caveWormNoise;
    readonly BiomeMap _biomeMap;
    readonly PlanetConfig _config;

    public DensityGenerator(PlanetConfig config, BiomeMap biomeMap)
    {
        _config = config;
        _biomeMap = biomeMap;

        _heightNoise = new FractalNoise(config.Seed)
        {
            Octaves = 6,
            Lacunarity = 2.0f,
            Persistence = 0.5f,
            Mode = FractalMode.FBM,
        };

        _caveNoise = new FractalNoise(config.Seed + 1000)
        {
            Octaves = 3,
            Lacunarity = 2.0f,
            Persistence = 0.5f,
            Mode = FractalMode.Ridged,
        };

        _caveWormNoise = new FractalNoise(config.Seed + 2000)
        {
            Octaves = 2,
            Lacunarity = 2.5f,
            Persistence = 0.45f,
            Mode = FractalMode.Ridged,
        };
    }

    /// <summary>Max height displacement across all biomes.</summary>
    float MaxAmplitude()
    {
        float max = 0f;
        foreach (var b in _config.Biomes)
            max = MathF.Max(max, b.HeightAmplitude);
        return max;
    }

    public void Generate(VoxelChunk chunk, int face, float u0, float v0, float u1, float v1, int lodLevel)
    {
        int n = chunk.SamplesPerAxis;
        int size = chunk.Size;
        float radius = _config.Radius;

        float maxAmp = MaxAmplitude();
        float radialSpan = maxAmp * 2.5f + 20f;
        float radialBase = radius - maxAmp * 1.3f - 10f;

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

                    float blendedHeight = 0f;
                    float blendedCaveFreq = 0f;
                    float blendedCaveDensity = 0f;
                    bool anyCaves = false;
                    byte dominantMat = 0;
                    float dominantWeight = 0f;

                    for (int b = 0; b < blends.Length; b++)
                    {
                        var biome = blends[b].Biome;
                        float w = blends[b].Weight;

                        _heightNoise.Frequency = biome.NoiseFrequency;
                        _heightNoise.Lacunarity = biome.NoiseLacunarity;
                        float heightSample = _heightNoise.Sample3D(
                            sphereDir.X * radius,
                            sphereDir.Y * radius,
                            sphereDir.Z * radius);
                        blendedHeight += (biome.HeightAmplitude * heightSample) * w;

                        if (_config.EnableCaves && biome.CaveDensity > 0)
                        {
                            blendedCaveFreq += biome.CaveFrequency * w;
                            blendedCaveDensity += biome.CaveDensity * w;
                            anyCaves = true;
                        }

                        if (w > dominantWeight)
                        {
                            dominantWeight = w;
                            dominantMat = biome.BiomeIndex;
                        }
                    }

                    float surfaceRadius = radius + blendedHeight;
                    float density = rDist - surfaceRadius;

                    if (anyCaves && blendedCaveDensity > 0.01f)
                    {
                        SN.Vector3 worldPos = sphereDir * rDist;
                        float cx = worldPos.X * blendedCaveFreq;
                        float cy = worldPos.Y * blendedCaveFreq;
                        float cz = worldPos.Z * blendedCaveFreq;

                        float cave1 = _caveNoise.Sample3D(cx, cy, cz);
                        float cave2 = _caveWormNoise.Sample3D(cx * 1.5f, cy * 1.5f, cz * 1.5f);
                        float caveMask = cave1 * cave2;

                        float depthFactor = Math.Clamp((surfaceRadius - rDist) / (radius * 0.05f), 0f, 1f);
                        float caveCarve = caveMask * blendedCaveDensity * depthFactor;

                        if (caveCarve > 0.35f)
                            density = Math.Max(density, caveCarve - 0.35f);
                    }

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
