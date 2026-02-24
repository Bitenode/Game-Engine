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
    readonly PlanetVoxelEditStore? _editStore;

    public PlanetMeshGenerator(PlanetConfig config, BiomeMap biomeMap, PlanetVoxelEditStore? editStore = null)
    {
        _config = config;
        _biomeMap = biomeMap;
        _editStore = editStore;
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

        var ridgeNoise = _config.RidgeStrength > 0f
            ? new FractalNoise(seed + 7100)
            {
                Octaves = 4,
                Frequency = _config.MacroFrequency,
                Persistence = 0.5f,
                Mode = FractalMode.Ridged,
            }
            : null;

        var basinNoise = _config.BasinStrength > 0f
            ? new FractalNoise(seed + 7200)
            {
                Octaves = 3,
                Frequency = _config.MacroFrequency,
                Persistence = 0.55f,
                Mode = FractalMode.FBM,
            }
            : null;

        var densitySampler = new PlanetDensitySampler(
            _config,
            _biomeMap,
            biomeNoises,
            erosionNoise,
            caveNoise,
            ridgeNoise,
            basinNoise,
            _editStore);

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

                float blendedHeight = PlanetSurfaceUtility.SampleHeight(
                    _config,
                    _biomeMap,
                    biomeNoises,
                    erosionNoise,
                    caveNoise,
                    ridgeNoise,
                    basinNoise,
                    sphereDir);

                float baseSurfaceR = radius + blendedHeight;
                float surfaceR = FindSurfaceRadiusOnRay(sphereDir, baseSurfaceR, densitySampler);
                var pos = sphereDir * surfaceR;
                var normal = EstimateNormal(pos, densitySampler);

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
                data.Normals.Add(normal);
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

    float FindSurfaceRadiusOnRay(SN.Vector3 sphereDir, float baseSurfaceR, PlanetDensitySampler sampler)
    {
        float searchRange = Math.Max(1f, _config.VoxelIsoSearchRange);
        int steps = Math.Max(8, _config.VoxelIsoSearchSteps);
        float innerR = Math.Max(1f, baseSurfaceR - searchRange);
        float outerR = baseSurfaceR + searchRange;

        float prevR = outerR;
        float prevD = sampler.SampleDensity(sphereDir * prevR);
        float stepSize = (outerR - innerR) / steps;

        for (int i = 1; i <= steps; i++)
        {
            float currR = outerR - i * stepSize;
            float currD = sampler.SampleDensity(sphereDir * currR);
            if (prevD >= 0f && currD <= 0f)
                return RefineCrossing(sphereDir, sampler, prevR, currR, prevD, currD);
            prevR = currR;
            prevD = currD;
        }

        return baseSurfaceR;
    }

    static float RefineCrossing(
        SN.Vector3 sphereDir,
        PlanetDensitySampler sampler,
        float outerR,
        float innerR,
        float outerD,
        float innerD)
    {
        float loR = innerR;
        float hiR = outerR;
        float loD = innerD;
        float hiD = outerD;

        for (int i = 0; i < 6; i++)
        {
            float midR = (loR + hiR) * 0.5f;
            float midD = sampler.SampleDensity(sphereDir * midR);
            if (midD <= 0f)
            {
                loR = midR;
                loD = midD;
            }
            else
            {
                hiR = midR;
                hiD = midD;
            }
        }

        float denom = hiD - loD;
        if (MathF.Abs(denom) <= 1e-6f)
            return (loR + hiR) * 0.5f;
        float t = Math.Clamp((-loD) / denom, 0f, 1f);
        return loR + (hiR - loR) * t;
    }

    static SN.Vector3 EstimateNormal(SN.Vector3 worldPos, PlanetDensitySampler sampler)
    {
        const float eps = 0.35f;
        float dx = sampler.SampleDensity(worldPos + new SN.Vector3(eps, 0, 0)) - sampler.SampleDensity(worldPos - new SN.Vector3(eps, 0, 0));
        float dy = sampler.SampleDensity(worldPos + new SN.Vector3(0, eps, 0)) - sampler.SampleDensity(worldPos - new SN.Vector3(0, eps, 0));
        float dz = sampler.SampleDensity(worldPos + new SN.Vector3(0, 0, eps)) - sampler.SampleDensity(worldPos - new SN.Vector3(0, 0, eps));

        var g = new SN.Vector3(dx, dy, dz);
        float len = g.Length();
        if (len <= 1e-6f)
            return SN.Vector3.Normalize(worldPos);
        return g / len;
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
