using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Generates a spherical water shell mesh for the planet ocean.
/// The mesh is a subdivided cube-sphere at the sea level radius.
/// </summary>
public sealed class PlanetWater
{
    public Mesh? WaterMesh { get; private set; }
    public float SeaLevelRadius { get; }

    readonly int _subdivisions;
    readonly Func<SN.Vector3, float>? _waterMaskSampler;
    readonly Func<SN.Vector3, float>? _shoreBiomeSampler;
    readonly float _waterThreshold;

    public PlanetWater(
        float seaLevelRadius,
        int subdivisions = 40,
        Func<SN.Vector3, float>? waterMaskSampler = null,
        Func<SN.Vector3, float>? shoreBiomeSampler = null,
        float waterThreshold = 0.35f)
    {
        SeaLevelRadius = seaLevelRadius;
        _subdivisions = Math.Max(4, subdivisions);
        _waterMaskSampler = waterMaskSampler;
        _shoreBiomeSampler = shoreBiomeSampler;
        _waterThreshold = Math.Clamp(waterThreshold, 0f, 1f);
        BuildMesh();
    }

    void BuildMesh()
    {
        int vertsPerFace = (_subdivisions + 1) * (_subdivisions + 1);
        int totalVerts = vertsPerFace * 6;
        var vertices = new SN.Vector3[totalVerts];
        var normals = new SN.Vector3[totalVerts];
        var uvs = new SN.Vector2[totalVerts];
        var waterMask = new float[totalVerts];
        var indices = new List<int>(_subdivisions * _subdivisions * 6 * 6);

        int vertIdx = 0;

        for (int face = 0; face < 6; face++)
        {
            int faceBase = vertIdx;

            for (int y = 0; y <= _subdivisions; y++)
            {
                float v = (float)y / _subdivisions;
                for (int x = 0; x <= _subdivisions; x++)
                {
                    float u = (float)x / _subdivisions;
                    SN.Vector3 dir = CubeSphereMath.FaceUVToDirection(face, u, v);
                    SN.Vector3 pos = dir * SeaLevelRadius;
                    float mask = _waterMaskSampler?.Invoke(dir) ?? 1f;
                    float shoreBiome = _shoreBiomeSampler?.Invoke(dir) ?? 0f;

                    vertices[vertIdx] = pos;
                    normals[vertIdx] = dir;
                    // UV.x stores dominant shore biome index, UV.y stores water mask.
                    uvs[vertIdx] = new SN.Vector2(shoreBiome, mask);
                    waterMask[vertIdx] = mask;
                    vertIdx++;
                }
            }

            int rowLen = _subdivisions + 1;
            for (int y = 0; y < _subdivisions; y++)
            {
                for (int x = 0; x < _subdivisions; x++)
                {
                    int a = faceBase + y * rowLen + x;
                    int b = a + 1;
                    int c = a + rowLen;
                    int d = c + 1;

                    bool wet = waterMask[a] >= _waterThreshold
                               || waterMask[b] >= _waterThreshold
                               || waterMask[c] >= _waterThreshold
                               || waterMask[d] >= _waterThreshold;
                    if (!wet)
                        continue;

                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);

                    indices.Add(b);
                    indices.Add(d);
                    indices.Add(c);
                }
            }
        }

        WaterMesh = new Mesh(vertices, Array.Empty<int>(), indices.ToArray());
        WaterMesh.Normals = normals;
        WaterMesh.UVs = uvs;
    }
}
