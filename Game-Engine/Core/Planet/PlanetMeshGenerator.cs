using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

public sealed class PlanetMeshGenerator
{
    readonly BiomeMap _biomeMap;
    readonly PlanetConfig _config;

    public PlanetMeshGenerator(PlanetConfig config, BiomeMap biomeMap)
    {
        _config = config;
        _biomeMap = biomeMap;
    }

    static FractalMode ParseMode(string mode) => mode switch
    {
        "Ridged" => FractalMode.Ridged,
        "Billow" => FractalMode.Billow,
        _ => FractalMode.FBM,
    };

    public TransvoxelMeshData Generate(int face, float u0, float v0, float u1, float v1, int resolution)
    {
        var data = new TransvoxelMeshData();
        int n = resolution + 1;
        float radius = _config.Radius;
        int seed = _config.Seed;

        var biomes = _config.Biomes;
        var biomeNoises = new FractalNoise[biomes.Length];
        for (int i = 0; i < biomes.Length; i++)
        {
            biomeNoises[i] = new FractalNoise(seed)
            {
                Octaves = biomes[i].NoiseOctaves,
                Frequency = biomes[i].NoiseFrequency,
                Lacunarity = biomes[i].NoiseLacunarity,
                Persistence = 0.5f,
                Mode = ParseMode(biomes[i].NoiseMode),
            };
        }

        var caveNoise = (_config.EnableCaves)
            ? new FractalNoise(seed + 9000)
            {
                Octaves = 3,
                Frequency = _config.CaveFrequency,
                Persistence = 0.5f,
                Mode = FractalMode.Ridged,
            }
            : null;

        var erosionNoise = new FractalNoise(seed + 8000)
        {
            Octaves = 4,
            Persistence = 0.45f,
            Mode = FractalMode.Ridged,
        };

        for (int iy = 0; iy < n; iy++)
        {
            float vt = (float)iy / resolution;
            float v = v0 + vt * (v1 - v0);

            for (int ix = 0; ix < n; ix++)
            {
                float ut = (float)ix / resolution;
                float u = u0 + ut * (u1 - u0);

                var sphereDir = CubeSphereMath.FaceUVToDirection(face, u, v);
                var blends = _biomeMap.GetBiomes(sphereDir);

                float nx = sphereDir.X * radius;
                float ny = sphereDir.Y * radius;
                float nz = sphereDir.Z * radius;

                float blendedHeight = SampleBlendedHeight(blends, sphereDir, radius, biomeNoises);

                if (blends.Length > 0)
                {
                    float totalErosion = 0f;
                    for (int b = 0; b < blends.Length && b < 4; b++)
                    {
                        var biome = blends[b].Biome;
                        if (biome.ErosionStrength > 0f)
                        {
                            erosionNoise.Frequency = biome.ErosionFrequency;
                            float e = erosionNoise.Sample3D(nx, ny, nz);
                            e = Math.Clamp(e, 0f, 1f);
                            totalErosion += e * biome.ErosionStrength * 5f * blends[b].Weight;
                        }
                    }
                    blendedHeight -= totalErosion;
                }

                if (caveNoise != null && blends.Length > 0)
                {
                    var dominant = blends[0].Biome;
                    if (dominant.CavesEnabled)
                    {
                        float caveSample = caveNoise.Sample3D(nx, ny, nz);
                        caveSample = Math.Clamp(caveSample, 0f, 1f);
                        if (caveSample > _config.CaveThreshold)
                        {
                            float caveIntensity = (caveSample - _config.CaveThreshold) / (1f - _config.CaveThreshold);
                            blendedHeight -= caveIntensity * Math.Min(dominant.CaveDepth, 8f);
                        }
                    }
                }

                float surfaceR = radius + blendedHeight;
                var pos = sphereDir * surfaceR;

                var blendIdx = new SN.Vector4(0, 0, 0, 0);
                var blendWt = new SN.Vector4(0, 0, 0, 0);
                for (int b = 0; b < blends.Length && b < 4; b++)
                {
                    switch (b)
                    {
                        case 0: blendIdx.X = blends[b].Biome.BiomeIndex; blendWt.X = blends[b].Weight; break;
                        case 1: blendIdx.Y = blends[b].Biome.BiomeIndex; blendWt.Y = blends[b].Weight; break;
                        case 2: blendIdx.Z = blends[b].Biome.BiomeIndex; blendWt.Z = blends[b].Weight; break;
                        case 3: blendIdx.W = blends[b].Biome.BiomeIndex; blendWt.W = blends[b].Weight; break;
                    }
                }

                data.Positions.Add(pos);
                data.Normals.Add(sphereDir);
                data.UVs.Add(new SN.Vector2(ut, vt));
                data.BlendIndices.Add(blendIdx);
                data.BlendWeights.Add(blendWt);
            }
        }

        for (int iy = 0; iy < resolution; iy++)
        {
            for (int ix = 0; ix < resolution; ix++)
            {
                int a = iy * n + ix;
                int b = a + 1;
                int c = a + n;
                int d = c + 1;

                data.Indices.Add(a);
                data.Indices.Add(b);
                data.Indices.Add(c);

                data.Indices.Add(b);
                data.Indices.Add(d);
                data.Indices.Add(c);
            }
        }

        RecalcNormals(data);
        return data;
    }

    float SampleBlendedHeight(BiomeBlend[] blends, SN.Vector3 sphereDir, float radius, FractalNoise[] biomeNoises)
    {
        float blendedHeight = 0f;
        float nx = sphereDir.X * radius;
        float ny = sphereDir.Y * radius;
        float nz = sphereDir.Z * radius;

        for (int b = 0; b < blends.Length && b < 4; b++)
        {
            var biome = blends[b].Biome;
            float w = blends[b].Weight;

            int noiseIdx = Math.Clamp(biome.BiomeIndex, 0, biomeNoises.Length - 1);
            float sample = biomeNoises[noiseIdx].Sample3D(nx, ny, nz);

            var mode = ParseMode(biome.NoiseMode);
            if (mode == FractalMode.Ridged)
                sample = sample * 0.7f - 0.3f;
            else if (mode == FractalMode.Billow)
                sample = sample * 0.8f;

            blendedHeight += biome.HeightAmplitude * sample * w;
        }

        return blendedHeight;
    }

    static void RecalcNormals(TransvoxelMeshData meshData)
    {
        var normals = new SN.Vector3[meshData.Positions.Count];
        var indices = meshData.Indices;
        var positions = meshData.Positions;

        for (int i = 0; i < indices.Count; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            if (ia >= positions.Count || ib >= positions.Count || ic >= positions.Count) continue;

            var e1 = positions[ib] - positions[ia];
            var e2 = positions[ic] - positions[ia];
            var fn = SN.Vector3.Cross(e1, e2);
            normals[ia] += fn;
            normals[ib] += fn;
            normals[ic] += fn;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            float len = normals[i].Length();
            meshData.Normals[i] = len > 1e-8f ? normals[i] / len : SN.Vector3.UnitY;
        }
    }
}
