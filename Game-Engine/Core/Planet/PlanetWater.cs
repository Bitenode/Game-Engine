using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Low-resolution spherical water shell used from orbit and as the atmosphere proxy mesh.
/// Close-up water is generated per terrain chunk so it matches LOD and the shoreline.
/// </summary>
public sealed class PlanetWater
{
    public Mesh? WaterMesh { get; private set; }
    public float SeaLevelRadius { get; }

    public PlanetWater(float seaLevelRadius, int subdivisions = 56)
    {
        SeaLevelRadius = MathF.Max(1f, seaLevelRadius);
        int subdiv = Math.Max(8, subdivisions);
        BuildUniformSphere(subdiv);
    }

    void BuildUniformSphere(int subdivisions)
    {
        int vertsPerFace = (subdivisions + 1) * (subdivisions + 1);
        var vertices = new SN.Vector3[vertsPerFace * 6];
        var normals = new SN.Vector3[vertices.Length];
        var uvs = new SN.Vector2[vertices.Length];
        var indices = new List<int>(subdivisions * subdivisions * 6 * 6);

        int vertIdx = 0;
        for (int face = 0; face < 6; face++)
        {
            int faceBase = vertIdx;
            for (int y = 0; y <= subdivisions; y++)
            {
                float v = (float)y / subdivisions;
                for (int x = 0; x <= subdivisions; x++)
                {
                    float u = (float)x / subdivisions;
                    SN.Vector3 dir = CubeSphereMath.FaceUVToDirection(face, u, v);
                    vertices[vertIdx] = dir * SeaLevelRadius;
                    normals[vertIdx] = dir;
                    uvs[vertIdx] = new SN.Vector2(0f, 1f);
                    vertIdx++;
                }
            }

            int rowLen = subdivisions + 1;
            for (int y = 0; y < subdivisions; y++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int a = faceBase + y * rowLen + x;
                    int b = a + 1;
                    int c = a + rowLen;
                    int d = c + 1;
                    indices.Add(a); indices.Add(b); indices.Add(c);
                    indices.Add(b); indices.Add(d); indices.Add(c);
                }
            }
        }

        WaterMesh = new Mesh(vertices, Array.Empty<int>(), indices.ToArray())
        {
            Normals = normals,
            UVs = uvs
        };
    }
}
