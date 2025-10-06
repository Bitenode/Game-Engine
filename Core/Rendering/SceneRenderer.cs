#nullable enable
using System.Collections.Generic;
using System.Linq;
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
        var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = System.Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mr.Wireframe) continue;
            if (mf.Mesh is null) continue;
            if (MaterialUtil.IsRendererTransparent(mr)) continue; // opaque only

            var mesh = MeshLod.EnsureProceduralLod(mf, world, view, proj, new Size(W, H));

            var matProp = mr.GetType().GetProperty("Material",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var mat = matProp?.GetValue(mr) as Material;

            Rasterizer.RasterizeMeshSolidZ(mesh, world, view, proj,
                color, zbuf, W, H,
                mr.Color, mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, mr.ReceiveShadows, mr.DoubleSided, mr.InvertFrontFace,
                transparentPass: false);
        }

        foreach (var child in go.Children)
            DrawNodeSolidZ(child, view, proj, world,
                color, zbuf, W, H, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange, shadow);
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

        var items = new List<(float ndcZ, SN.Matrix4x4 world, MeshFilter mf, MeshRenderer mr, Material? mat)>();

        void Gather(GameObject node, in SN.Matrix4x4 parentW)
        {
            var world = parentW * TransformUtil.WorldFromTransform(node.Transform);

            var filters = node.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = node.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int n = System.Math.Min(filters.Count, renderers.Count);

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];
                if (mr.Wireframe || mf.Mesh is null) continue;
                if (!MaterialUtil.IsRendererTransparent(mr)) continue;

                _ = MeshLod.EnsureProceduralLod(mf, world, v, p, new Size(W, H));

                var clip = SN.Vector4.Transform(new SN.Vector4(SN.Vector3.Transform(SN.Vector3.Zero, world), 1f), v * p);
                if (clip.W <= 0f) continue;
                float ndcZ = clip.Z / clip.W;

                var matProp = mr.GetType().GetProperty("Material",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Material;

                items.Add((ndcZ, world, mf, mr, mat));
            }

            foreach (var c in node.Children)
                Gather(c, world);
        }

        Gather(go, parentWorld);
        items.Sort((a, b) => b.ndcZ.CompareTo(a.ndcZ)); // back-to-front

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
