#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    public static class SceneRenderer
    {
        // ---------- Cached reflection (avoids per-call lookup) ----------
        private static readonly PropertyInfo s_meshRendererMaterialProp =
            typeof(MeshRenderer).GetProperty("Material",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // ---------- Mesh bounding sphere cache (computed once per Mesh) ----------
        private struct Sphere { public SN.Vector3 Center; public float Radius; }
        private static readonly Dictionary<Mesh, Sphere> s_meshSpheres = new Dictionary<Mesh, Sphere>(1024);

        private static Sphere GetMeshSphere(Mesh m)
        {
            Sphere s;
            if (s_meshSpheres.TryGetValue(m, out s))
                return s;

            var vtx = m.Vertices;
            if (vtx == null || vtx.Length == 0)
                return new Sphere { Center = SN.Vector3.Zero, Radius = 0f };

            // Center = average, Radius = max distance (cheap, good enough for culling)
            SN.Vector3 c = SN.Vector3.Zero;
            for (int i = 0; i < vtx.Length; i++) c += vtx[i];
            c /= vtx.Length;

            float r2 = 0f;
            for (int i = 0; i < vtx.Length; i++)
            {
                var d = vtx[i] - c;
                float d2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                if (d2 > r2) r2 = d2;
            }

            s = new Sphere { Center = c, Radius = (float)System.Math.Sqrt(r2) };
            s_meshSpheres[m] = s;
            return s;
        }

        // ---------- Frustum planes (Ax + By + Cz + D >= 0 inside) ----------
        private struct Plane { public float A, B, C, D; }
        private static void ExtractFrustumPlanes(in SN.Matrix4x4 viewProj, Plane[] planes /*length 6*/)
        {
            // Gribb/Hartmann
            // Left
            planes[0].A = viewProj.M14 + viewProj.M11;
            planes[0].B = viewProj.M24 + viewProj.M21;
            planes[0].C = viewProj.M34 + viewProj.M31;
            planes[0].D = viewProj.M44 + viewProj.M41;
            // Right
            planes[1].A = viewProj.M14 - viewProj.M11;
            planes[1].B = viewProj.M24 - viewProj.M21;
            planes[1].C = viewProj.M34 - viewProj.M31;
            planes[1].D = viewProj.M44 - viewProj.M41;
            // Bottom
            planes[2].A = viewProj.M14 + viewProj.M12;
            planes[2].B = viewProj.M24 + viewProj.M22;
            planes[2].C = viewProj.M34 + viewProj.M32;
            planes[2].D = viewProj.M44 + viewProj.M42;
            // Top
            planes[3].A = viewProj.M14 - viewProj.M12;
            planes[3].B = viewProj.M24 - viewProj.M22;
            planes[3].C = viewProj.M34 - viewProj.M32;
            planes[3].D = viewProj.M44 - viewProj.M42;
            // Near
            planes[4].A = viewProj.M13;
            planes[4].B = viewProj.M23;
            planes[4].C = viewProj.M33;
            planes[4].D = viewProj.M43;
            // Far
            planes[5].A = viewProj.M14 - viewProj.M13;
            planes[5].B = viewProj.M24 - viewProj.M23;
            planes[5].C = viewProj.M34 - viewProj.M33;
            planes[5].D = viewProj.M44 - viewProj.M43;

            // Normalize
            for (int i = 0; i < 6; i++)
            {
                float invLen = 1f / (float)System.Math.Sqrt(
                    planes[i].A * planes[i].A +
                    planes[i].B * planes[i].B +
                    planes[i].C * planes[i].C);
                planes[i].A *= invLen;
                planes[i].B *= invLen;
                planes[i].C *= invLen;
                planes[i].D *= invLen;
            }
        }

        private static bool SphereInsideFrustum(ref Sphere s, in SN.Matrix4x4 world, Plane[] planes)
        {
            SN.Vector3 cW = SN.Vector3.Transform(s.Center, world);
            float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
            float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
            float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
            float rW = s.Radius * System.Math.Max(sx, System.Math.Max(sy, sz));

            for (int i = 0; i < 6; i++)
            {
                float d = planes[i].A * cW.X + planes[i].B * cW.Y + planes[i].C * cW.Z + planes[i].D;
                if (d < -rW) return false;
            }
            return true;
        }

        // ---------- Small reusable buffers to avoid per-node allocations ----------
        [ThreadStatic] private static List<MeshFilter> t_filters;
        [ThreadStatic] private static List<MeshRenderer> t_renderers;
        [ThreadStatic] private static List<TransItem> t_transItems;

        private static List<MeshFilter> Filters => t_filters ?? (t_filters = new List<MeshFilter>(8));
        private static List<MeshRenderer> Renderers => t_renderers ?? (t_renderers = new List<MeshRenderer>(8));

        private struct TransItem
        {
            public float SortZ;
            public SN.Matrix4x4 World;
            public MeshFilter MF;
            public MeshRenderer MR;
            public Material? Mat;
        }
        private static List<TransItem> TransItems => t_transItems ?? (t_transItems = new List<TransItem>(128));

        // =========================================================================================
        // OPAQUE pass (per node, recursive), with object-level frustum culling
        // =========================================================================================
        public static void DrawNodeSolidZ(
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            in SN.Matrix4x4 parentWorld,
            uint[] color, float[] zbuf, int W, int H,
            SN.Vector3 L, float DiffuseK, float Ambient,
            bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
            ShadowMap? shadow)
        {
            // Precompute frustum once (view*proj)
            var viewProj = view * proj;
            var planes = new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            DrawNodeSolidZ_Internal(go, view, proj, parentWorld, planes,
                color, zbuf, W, H, L, DiffuseK, Ambient, lightIsPoint, lightPosW, lightRange, shadow);
        }

        private static void DrawNodeSolidZ_Internal(
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            in SN.Matrix4x4 parentWorld, Plane[] planes,
            uint[] color, float[] zbuf, int W, int H,
            SN.Vector3 L, float DiffuseK, float Ambient,
            bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
            ShadowMap? shadow)
        {
            var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

            var filters = Filters; filters.Clear();
            var renderers = Renderers; renderers.Clear();

            var behaviors = go.Behaviors;
            for (int bi = 0; bi < behaviors.Count; bi++)
            {
                var b = behaviors[bi];
                var f = b as MeshFilter;
                if (f != null && f.Enabled) { filters.Add(f); continue; }
                var r = b as MeshRenderer;
                if (r != null && r.Enabled) { renderers.Add(r); continue; }
            }

            int n = filters.Count < renderers.Count ? filters.Count : renderers.Count;

            SN.Matrix4x4 v = view;
            SN.Matrix4x4 p = proj;
            Size surface = new Size(W, H);

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];

                if (mr.Wireframe) continue;
                var srcMesh = mf.Mesh;
                if (srcMesh == null) continue;
                if (MaterialUtil.IsRendererTransparent(mr)) continue; // opaque pass only

                // Frustum cull
                var sph = GetMeshSphere(srcMesh);
                if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                // LOD
                var mesh = MeshLod.EnsureProceduralLod(mf, world, v, p, surface);
                if (mesh == null) continue;

                // Material (safe cast)
                var matObj = s_meshRendererMaterialProp != null ? s_meshRendererMaterialProp.GetValue(mr) : null;
                var mat = matObj as Material;

                Rasterizer.RasterizeMeshSolidZ(
                    mesh, world, v, p,
                    color, zbuf, W, H,
                    mr.Color, mat, L, DiffuseK, Ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadow, mr.ReceiveShadows, mr.DoubleSided, mr.InvertFrontFace,
                    transparentPass: false);
            }

            var ch = go.Children;
            for (int ci = 0; ci < ch.Count; ci++)
            {
                DrawNodeSolidZ_Internal(
                    ch[ci], view, proj, world, planes,
                    color, zbuf, W, H,
                    L, DiffuseK, Ambient,
                    lightIsPoint, lightPosW, lightRange, shadow);
            }
        }

        // =========================================================================================
        // TRANSPARENT pass: gather, sort back-to-front, render
        // =========================================================================================
        public static void DrawNodeSolidZ_QueueTransparent(
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            in SN.Matrix4x4 parentWorld,
            uint[] color, float[] zbuf, int W, int H,
            SN.Vector3 L, float DiffuseK, float Ambient,
            bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
            ShadowMap? shadow)
        {
            var viewProj = view * proj;
            var planes = new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            var items = TransItems; items.Clear();

            GatherTransparent(go, parentWorld, view, proj, planes, items, W, H);

            // Back-to-front
            items.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var mesh = it.MF.Mesh;
                if (mesh == null) continue;

                Rasterizer.RasterizeMeshSolidZ(
                    mesh, it.World, view, proj,
                    color, zbuf, W, H,
                    it.MR.Color, it.Mat, L, DiffuseK, Ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadow, it.MR.ReceiveShadows, it.MR.DoubleSided, it.MR.InvertFrontFace,
                    transparentPass: true);
            }
        }

        private static void GatherTransparent(
            GameObject node, in SN.Matrix4x4 parentWorld,
            in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            Plane[] planes, List<TransItem> items, int W, int H)
        {
            var world = parentWorld * TransformUtil.WorldFromTransform(node.Transform);

            var filters = Filters; filters.Clear();
            var renderers = Renderers; renderers.Clear();

            var behaviors = node.Behaviors;
            for (int bi = 0; bi < behaviors.Count; bi++)
            {
                var b = behaviors[bi];
                var f = b as MeshFilter;
                if (f != null && f.Enabled) { filters.Add(f); continue; }
                var r = b as MeshRenderer;
                if (r != null && r.Enabled) { renderers.Add(r); continue; }
            }

            int n = filters.Count < renderers.Count ? filters.Count : renderers.Count;

            SN.Matrix4x4 wv = world * view; // for view-space center

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];

                if (mr.Wireframe) continue;
                var mesh = mf.Mesh;
                if (mesh == null) continue;
                if (!MaterialUtil.IsRendererTransparent(mr)) continue;

                // Frustum cull
                var sph = GetMeshSphere(mesh);
                if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                // Ensure LOD selection mirrors opaque path
                _ = MeshLod.EnsureProceduralLod(mf, world, view, proj, new Size(W, H));

                // Sort key: view-space center Z + scaled radius (farthest first)
                var centerV = SN.Vector3.Transform(sph.Center, wv);
                float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                float rScale = sph.Radius * System.Math.Max(sx, System.Math.Max(sy, sz));
                float sortZ = centerV.Z + rScale;

                var matObj = s_meshRendererMaterialProp != null ? s_meshRendererMaterialProp.GetValue(mr) : null;
                var mat = matObj as Material;

                items.Add(new TransItem
                {
                    SortZ = sortZ,
                    World = world,
                    MF = mf,
                    MR = mr,
                    Mat = mat
                });
            }

            var ch = node.Children;
            for (int ci = 0; ci < ch.Count; ci++)
                GatherTransparent(ch[ci], world, view, proj, planes, items, W, H);
        }

        // =========================================================================================
        // SHADOW depth pass (per node, recursive)
        // =========================================================================================
        public static void DrawNodeDepth(
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            in SN.Matrix4x4 parentWorld,
            float[] depth, int W, int H)
        {
            var viewProj = view * proj;
            var planes = new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            DrawNodeDepth_Internal(go, view, proj, parentWorld, planes, depth, W, H);
        }

        private static void DrawNodeDepth_Internal(
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            in SN.Matrix4x4 parentWorld,
            Plane[] planes,
            float[] depth, int W, int H)
        {
            var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

            var filters = Filters; filters.Clear();
            var renderers = Renderers; renderers.Clear();

            var behaviors = go.Behaviors;
            for (int bi = 0; bi < behaviors.Count; bi++)
            {
                var b = behaviors[bi];
                var f = b as MeshFilter;
                if (f != null && f.Enabled) { filters.Add(f); continue; }
                var r = b as MeshRenderer;
                if (r != null && r.Enabled) { renderers.Add(r); continue; }
            }

            int n = filters.Count < renderers.Count ? filters.Count : renderers.Count;

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];
                var mesh = mf.Mesh;
                if (mesh == null) continue;
                if (!mr.CastShadows) continue;

                var sph = GetMeshSphere(mesh);
                if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                Rasterizer.RasterizeDepth(mesh, world, view, proj, depth, W, H, doubleSided: true);
            }

            var ch = go.Children;
            for (int ci = 0; ci < ch.Count; ci++)
                DrawNodeDepth_Internal(ch[ci], view, proj, world, planes, depth, W, H);
        }
    }
}