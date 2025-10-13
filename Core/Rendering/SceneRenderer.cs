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

        SN.Matrix4x4 invV;
        SN.Vector3 camPos = SN.Vector3.Zero;
        if (SN.Matrix4x4.Invert(view, out invV))
            camPos = invV.Translation;

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = System.Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mr.Wireframe) continue;
            if (mf.Mesh is null) continue;
            if (MaterialUtil.IsRendererTransparent(mr)) continue; // opaque-only pass here

            // NEW: LOD/Impostor selection
            var tree = go.Behaviors.OfType<Game_Engine.Core.Component.TreeLOD>().FirstOrDefault(b => b.Enabled);
            var sourceMesh = mf.Mesh;
            if (tree != null)
            {
                // distance from object origin to camera
                var objPosW = SN.Vector3.Transform(SN.Vector3.Zero, world);
                float dist = (objPosW - camPos).Length();

                // Decide LOD mesh or billboard
                var chosen = tree.PickMeshOrNullForBillboard(dist, sourceMesh);
                if (chosen == null)
                {
                    // Billboard path (opaque pass). Many billboards are alpha, so
                    // if yours is transparent you’ll also hit the transparent pass below.
                    // This opaque draw is harmless for atlases with mostly opaque pixels.
                    DrawTreeBillboard(tree, objPosW, camPos, world, view, proj,
                                      mr, /*tint*/ mr.Color,
                                      color, zbuf, W, H,
                                      L, DiffuseK, Ambient,
                                      lightIsPoint, lightPosW, lightRange,
                                      shadow);
                    continue; // skip mesh draw
                }
                sourceMesh = chosen;
            }

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

        // Camera position (world)
        SN.Matrix4x4 invV;
        SN.Vector3 camPos = SN.Vector3.Zero;
        if (SN.Matrix4x4.Invert(v, out invV))
            camPos = invV.Translation;

        // NOTE: include selected Mesh and billboard info in the item
        var items = new List<(float ndcZ, SN.Matrix4x4 world, Mesh mesh, MeshRenderer mr, Material? mat, bool billboard, Game_Engine.Core.Component.TreeLOD tree)>();

        void Gather(GameObject node, in SN.Matrix4x4 parentW)
        {
            var world = parentW * TransformUtil.WorldFromTransform(node.Transform);

            var filters = node.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = node.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int n = System.Math.Min(filters.Count, renderers.Count);

            // Optional TreeLOD on this node
            var tree = node.Behaviors.OfType<Game_Engine.Core.Component.TreeLOD>().FirstOrDefault(b => b.Enabled);

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];
                if (mr.Wireframe || mf.Mesh is null) continue;
                if (!MaterialUtil.IsRendererTransparent(mr)) continue;

                // Start with any procedural upgrade your engine wants to do
                var ensured = MeshLod.EnsureProceduralLod(mf, world, v, p, new Size(W, H));
                var selected = ensured;
                bool asBillboard = false;

                if (tree != null)
                {
                    var objPosW = SN.Vector3.Transform(SN.Vector3.Zero, world);
                    float dist = (objPosW - camPos).Length();

                    var choice = tree.PickMeshOrNullForBillboard(dist, ensured);
                    if (choice == null)
                        asBillboard = true;
                    else
                        selected = choice;
                }

                // Depth key for sorting (object origin)
                var clip = SN.Vector4.Transform(new SN.Vector4(SN.Vector3.Transform(SN.Vector3.Zero, world), 1f), v * p);
                if (clip.W <= 0f) continue;
                float ndcZ = clip.Z / clip.W;

                var matProp = mr.GetType().GetProperty("Material",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Material;

                items.Add((ndcZ, world, selected, mr, mat, asBillboard, tree));
            }

            foreach (var c in node.Children)
                Gather(c, world);
        }

        Gather(go, parentWorld);
        items.Sort((a, b) => b.ndcZ.CompareTo(a.ndcZ)); // far -> near

        foreach (var it in items)
        {
            if (it.billboard && it.tree != null)
            {
                // Render billboard in the transparent queue
                var objPosW = SN.Vector3.Transform(SN.Vector3.Zero, it.world);
                DrawTreeBillboard(
                    it.tree, objPosW, camPos,
                    it.world, v, p,
                    it.mr, it.mr.Color,
                    color, zbuf, W, H,
                    L, DiffuseK, Ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadow
                );
                continue;
            }

            // Normal transparent mesh draw
            Rasterizer.RasterizeMeshSolidZ(it.mesh, it.world, v, p,
                color, zbuf, W, H,
                it.mr.Color, it.mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, it.mr.ReceiveShadows, it.mr.DoubleSided, it.mr.InvertFrontFace,
                transparentPass: true);
        }
    }

    private static void DrawTreeBillboard(
        Game_Engine.Core.Component.TreeLOD tree,
        SN.Vector3 objPosW, SN.Vector3 camPos,
        in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
        MeshRenderer mr, Avalonia.Media.Color tint,
        uint[] color, float[] zbuf, int W, int H,
        SN.Vector3 L, float DiffuseK, float Ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        ShadowMap? shadow)
    {
        // Require an atlas to draw
        var atlas = tree.BillboardAtlas;
        if (atlas == null) return;

        // Upright billboard basis
        SN.Vector3 up = tree.UprightYAxis ? SN.Vector3.UnitY : new SN.Vector3(0, 1, 0);
        var toCam = camPos - objPosW;
        if (tree.UprightYAxis) toCam.Y = 0f; // keep upright
        float len = toCam.Length();
        if (len < 1e-6f) toCam = new SN.Vector3(0, 0, 1);
        else toCam /= len;

        var right = SN.Vector3.Normalize(SN.Vector3.Cross(up, toCam));
        up = SN.Vector3.Normalize(SN.Vector3.Cross(toCam, right)); // re-orthogonalize

        // Size in world units
        float h = Math.Max(0.001f, tree.BillboardHeight);
        float w = Math.Max(0.001f, h * tree.BillboardWidthMul);
        float hx = 0.5f * w, hy = 0.5f * h;

        // 4 corners in WORLD space (centered on object)
        var p0 = objPosW + (-right * hx) + (up * hy); // TL
        var p1 = objPosW + (right * hx) + (up * hy); // TR
        var p2 = objPosW + (right * hx) + (-up * hy); // BR
        var p3 = objPosW + (-right * hx) + (-up * hy); // BL

        // Normals toward camera (same for all verts)
        var n = toCam; // facing camera

        // Atlas slice UVs
        int cols = Math.Max(1, tree.AtlasCols);
        int rows = Math.Max(1, tree.AtlasRows);
        int yawSlice = tree.ComputeYawSlice(camPos, objPosW);
        if (yawSlice < 0) yawSlice = 0;
        if (yawSlice >= cols) yawSlice = yawSlice % cols;

        int s = yawSlice; // single row for now
        int c = s % cols;
        int r = s / cols;
        float du = 1f / cols;
        float dv = 1f / rows;
        float u0 = c * du, v0 = r * dv;
        float u1 = u0 + du, v1 = v0 + dv;

        // Build a tiny mesh on the fly in WORLD SPACE
        var v = new SN.Vector3[4] { p0, p1, p2, p3 };
        var t = new int[6] { 0, 1, 2, 0, 2, 3 };
        var m = new Mesh(v, new int[0], t);
        m.UVs = new SN.Vector2[4] {
        new SN.Vector2(u0, v0),
        new SN.Vector2(u1, v0),
        new SN.Vector2(u1, v1),
        new SN.Vector2(u0, v1)
    };
        m.Normals = new SN.Vector3[4] { n, n, n, n };

        // Pull Material from the renderer via reflection (your code style)
        var matProp = mr.GetType().GetProperty("Material",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var mat = matProp != null ? (Material)matProp.GetValue(mr) : null;

        // Draw with world=Identity since our verts are already in world space
        Rasterizer.RasterizeMeshSolidZ(
            m,
            SN.Matrix4x4.Identity, view, proj,
            color, zbuf, W, H,
            tint, mat,
            L, DiffuseK, Ambient,
            lightIsPoint, lightPosW, lightRange,
            shadow,               
            receiveShadows: false,
            doubleSided: true,
            invertFrontFace: false,
            transparentPass: MaterialUtil.IsRendererTransparent(mr)
        );
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
