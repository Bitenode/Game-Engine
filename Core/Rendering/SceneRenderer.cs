#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class SceneRenderer
{
    // OPAQUE pass (per node, recursive)
    public static void DrawNodeSolidZ(
        GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
        in SN.Matrix4x4 parentWorld,
        uint[] color, float[] zbuf, int W, int H,
        SN.Vector3 L, float DiffuseK, float Ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        ShadowMap? shadow)
    {
        
        SN.Matrix4x4 v = view;
        SN.Matrix4x4 p = proj;

        var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

        // Gather without LINQ allocations
        var filters = new List<MeshFilter>(8);
        var renderers = new List<MeshRenderer>(8);
        foreach (var b in go.Behaviors)
        {
            if (b is MeshFilter f && f.Enabled) filters.Add(f);
            else if (b is MeshRenderer r && r.Enabled) renderers.Add(r);
        }

        int n = filters.Count < renderers.Count ? filters.Count : renderers.Count;

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];

            if (mr.Wireframe) continue;
            var srcMesh = mf.Mesh;
            if (srcMesh is null) continue;
            if (MaterialUtil.IsRendererTransparent(mr)) continue; // opaque only

            //  LOD path
            var mesh = MeshLod.EnsureProceduralLod(mf, world, v, p, new Size(W, H));

            // get material
            var matProp = mr.GetType().GetProperty(
                "Material",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mat = matProp?.GetValue(mr) as Material;

            Rasterizer.RasterizeMeshSolidZ(
                mesh, world, v, p,
                color, zbuf, W, H,
                mr.Color, mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, mr.ReceiveShadows, mr.DoubleSided, mr.InvertFrontFace,
                transparentPass: false);
        }

        // recurse
        foreach (var child in go.Children)
        {
            DrawNodeSolidZ(
                child, v, p, world,
                color, zbuf, W, H,
                L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange, shadow);
        }
    }




    // TRANSPARENT pass: gather, sort back-to-front, render
    public static void DrawNodeSolidZ_QueueTransparent(
    GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    in SN.Matrix4x4 parentWorld,
    uint[] color, float[] zbuf, int W, int H,
    SN.Vector3 L, float DiffuseK, float Ambient,
    bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
    ShadowMap? shadow)
    {
        SN.Matrix4x4 v = view;
        SN.Matrix4x4 p = proj;

        var items = new List<(float sortZ, SN.Matrix4x4 world, MeshFilter mf, MeshRenderer mr, Material? mat)>();

        void Gather(GameObject node, in SN.Matrix4x4 parentW)
        {
            var world = parentW * TransformUtil.WorldFromTransform(node.Transform);

            var filters = node.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = node.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int n = Math.Min(filters.Count, renderers.Count);

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];
                if (mr.Wireframe || mf.Mesh is null) continue;
                if (!MaterialUtil.IsRendererTransparent(mr)) continue;

                // LOD
                _ = MeshLod.EnsureProceduralLod(mf, world, v, p, new Size(W, H));

                // Robust sort key: farthest view-space Z of the mesh’s vertices
                float sortZ = FarthestViewZ(mf.Mesh, world, v);

                var matProp = mr.GetType().GetProperty("Material",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Material;

                items.Add((sortZ, world, mf, mr, mat));
            }

            foreach (var c in node.Children)
                Gather(c, world);
        }

        Gather(go, parentWorld);

        // Back-to-front: farthest (largest positive view-Z) first
        items.Sort((a, b) => b.sortZ.CompareTo(a.sortZ));

        foreach (var it in items)
        {
            var mesh = it.mf.Mesh!;
            Rasterizer.RasterizeMeshSolidZ(mesh, it.world, v, p,
                color, zbuf, W, H,
                it.mr.Color, it.mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, it.mr.ReceiveShadows, it.mr.DoubleSided, it.mr.InvertFrontFace,
                transparentPass: true);
        }
    }

    // Uses *all* vertices for reliable ordering (opt: sample a subset if needed)
    private static float FarthestViewZ(Mesh mesh, in SN.Matrix4x4 world, in SN.Matrix4x4 view)
    {
        if (mesh.Vertices == null || mesh.Vertices.Length == 0) return float.NegativeInfinity;
        SN.Matrix4x4 wv = world * view;
        float maxZ = float.NegativeInfinity;
        var vtx = mesh.Vertices;
        for (int i = 0; i < vtx.Length; i++)
        {
            var vView = SN.Vector3.Transform(vtx[i], wv);
            if (vView.Z > maxZ) maxZ = vView.Z;
        }
        return maxZ;
    }


    // SHADOW depth pass (per node, recursive)
    public static void DrawNodeDepth(
        GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
        in SN.Matrix4x4 parentWorld,
        float[] depth, int W, int H)
    {
        var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = System.Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mf.Mesh != null && mr.CastShadows)
                Rasterizer.RasterizeDepth(mf.Mesh, world, view, proj, depth, W, H, doubleSided: true);
        }

        foreach (var ch in go.Children)
            DrawNodeDepth(ch, view, proj, world, depth, W, H);
    }
}
