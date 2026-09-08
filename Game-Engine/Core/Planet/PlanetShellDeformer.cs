using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Instant play-mode planet edits by moving existing shell mesh vertices — no chunk rebuild.
/// </summary>
static class PlanetShellDeformer
{
    public static bool TryDeformMesh(
        Game_Engine.Core.Mesh mesh,
        PlanetDensitySampler sampler,
        SN.Vector3 localEditCenter,
        float localInfluenceRadius,
        float vertexSpacing)
    {
        if (!mesh.IsPlanetMesh && (mesh.Vertices == null || mesh.Vertices.Length == 0))
            return false;

        var verts = mesh.Vertices;
        if (verts == null || verts.Length == 0)
            return false;

        // Always deform nearby verts — don't skip just because the chunk is dense.
        float reach = MathF.Max(localInfluenceRadius * 3f, MathF.Max(vertexSpacing * 2f, 4f));
        float reachSq = reach * reach;
        bool changed = false;

        for (int i = 0; i < verts.Length; i++)
        {
            var p = verts[i];
            if (SN.Vector3.DistanceSquared(p, localEditCenter) > reachSq)
                continue;

            float lenSq = p.LengthSquared();
            if (lenSq < 1e-10f)
                continue;

            var dir = p / MathF.Sqrt(lenSq);
            float newR = sampler.SampleEditedSurfaceRadius(dir, vertexSpacing);
            var np = dir * newR;
            if (SN.Vector3.DistanceSquared(p, np) <= 1e-8f)
                continue;

            verts[i] = np;
            changed = true;
        }

        if (!changed)
            return false;

        mesh.NotifyVerticesChanged();
        return true;
    }

    public static float EstimateVertexSpacing(QuadNode node, float planetRadius, int chunkSize)
    {
        int size = Math.Max(1, chunkSize);
        var a = CubeSphereMath.FaceUVToDirection(node.Face, node.U0, node.V0) * planetRadius;
        var b = CubeSphereMath.FaceUVToDirection(node.Face, node.U1, node.V0) * planetRadius;
        var c = CubeSphereMath.FaceUVToDirection(node.Face, node.U0, node.V1) * planetRadius;
        float sizeU = SN.Vector3.Distance(a, b);
        float sizeV = SN.Vector3.Distance(a, c);
        return MathF.Max(sizeU, sizeV) / size;
    }
}
