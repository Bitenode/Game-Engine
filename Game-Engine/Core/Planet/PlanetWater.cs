using System;
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

    public PlanetWater(float seaLevelRadius, int subdivisions = 40)
    {
        SeaLevelRadius = seaLevelRadius;
        _subdivisions = Math.Max(4, subdivisions);
        BuildMesh();
    }

    void BuildMesh()
    {
        int vertsPerFace = (_subdivisions + 1) * (_subdivisions + 1);
        int totalVerts = vertsPerFace * 6;
        int trisPerFace = _subdivisions * _subdivisions * 6;
        int totalTris = trisPerFace * 6;

        var vertices = new SN.Vector3[totalVerts];
        var normals = new SN.Vector3[totalVerts];
        var uvs = new SN.Vector2[totalVerts];
        var indices = new int[totalTris];

        int vertIdx = 0;
        int triIdx = 0;

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

                    vertices[vertIdx] = pos;
                    normals[vertIdx] = dir;
                    uvs[vertIdx] = new SN.Vector2(u, v);
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

                    indices[triIdx++] = a;
                    indices[triIdx++] = b;
                    indices[triIdx++] = c;

                    indices[triIdx++] = b;
                    indices[triIdx++] = d;
                    indices[triIdx++] = c;
                }
            }
        }

        WaterMesh = new Mesh(vertices, Array.Empty<int>(), indices);
        WaterMesh.Normals = normals;
        WaterMesh.UVs = uvs;
    }
}
