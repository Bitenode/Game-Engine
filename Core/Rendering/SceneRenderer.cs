#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Silk.NET.OpenGL;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering.GPU;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    /// <summary>
    /// GPU scene renderer. Traverses the scene graph and issues OpenGL draw calls.
    /// Replaces the old CPU-based rasterizer pipeline.
    /// </summary>
    public static class SceneRenderer
    {
        // ---------- Frustum culling ----------
        private struct Sphere { public SN.Vector3 Center; public float Radius; }
        private static readonly Dictionary<Mesh, Sphere> s_meshSpheres = new(1024);

        private static Sphere GetMeshSphere(Mesh m)
        {
            if (s_meshSpheres.TryGetValue(m, out var s)) return s;
            var vtx = m.Vertices;
            if (vtx == null || vtx.Length == 0)
                return new Sphere { Center = SN.Vector3.Zero, Radius = 0f };

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

            s = new Sphere { Center = c, Radius = (float)Math.Sqrt(r2) };
            s_meshSpheres[m] = s;
            return s;
        }

        private struct Plane { public float A, B, C, D; }

        private static void ExtractFrustumPlanes(in SN.Matrix4x4 viewProj, Plane[] planes)
        {
            planes[0] = new Plane { A = viewProj.M14 + viewProj.M11, B = viewProj.M24 + viewProj.M21, C = viewProj.M34 + viewProj.M31, D = viewProj.M44 + viewProj.M41 };
            planes[1] = new Plane { A = viewProj.M14 - viewProj.M11, B = viewProj.M24 - viewProj.M21, C = viewProj.M34 - viewProj.M31, D = viewProj.M44 - viewProj.M41 };
            planes[2] = new Plane { A = viewProj.M14 + viewProj.M12, B = viewProj.M24 + viewProj.M22, C = viewProj.M34 + viewProj.M32, D = viewProj.M44 + viewProj.M42 };
            planes[3] = new Plane { A = viewProj.M14 - viewProj.M12, B = viewProj.M24 - viewProj.M22, C = viewProj.M34 - viewProj.M32, D = viewProj.M44 - viewProj.M42 };
            planes[4] = new Plane { A = viewProj.M13, B = viewProj.M23, C = viewProj.M33, D = viewProj.M43 };
            planes[5] = new Plane { A = viewProj.M14 - viewProj.M13, B = viewProj.M24 - viewProj.M23, C = viewProj.M34 - viewProj.M33, D = viewProj.M44 - viewProj.M43 };

            for (int i = 0; i < 6; i++)
            {
                float invLen = 1f / (float)Math.Sqrt(
                    planes[i].A * planes[i].A + planes[i].B * planes[i].B + planes[i].C * planes[i].C);
                planes[i].A *= invLen; planes[i].B *= invLen; planes[i].C *= invLen; planes[i].D *= invLen;
            }
        }

        private static bool SphereInsideFrustum(ref Sphere s, in SN.Matrix4x4 world, Plane[] planes)
        {
            SN.Vector3 cW = SN.Vector3.Transform(s.Center, world);
            float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
            float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
            float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
            float rW = s.Radius * Math.Max(sx, Math.Max(sy, sz));

            for (int i = 0; i < 6; i++)
            {
                float d = planes[i].A * cW.X + planes[i].B * cW.Y + planes[i].C * cW.Z + planes[i].D;
                if (d < -rW) return false;
            }
            return true;
        }

        // ---------- Draw item for sorting ----------
        private struct DrawItem
        {
            public float SortZ;
            public SN.Matrix4x4 World;
            public MeshFilter MF;
            public MeshRenderer MR;
            public Material? Mat;
            public bool IsTransparent;
        }

        // ---------- GPU RENDER ENTRY POINT ----------

        /// <summary>
        /// Render the entire scene using GPU draw calls.
        /// Must be called within an active GL context.
        /// </summary>
        public static void RenderGPU(
            GL gl,
            ShaderProgram standardShader,
            ShaderProgram depthShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 lightDir,
            float diffuseK,
            float ambient,
            bool lightIsPoint,
            SN.Vector3 lightPosW,
            float lightRange,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP,
            SN.Vector3 camPos,
            SN.Vector3 sunShineDir = default)
        {
            var viewProj = view * proj;
            var planes = new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            // Gather all visible draw items
            var opaqueItems = new List<DrawItem>(256);
            var transparentItems = new List<DrawItem>(64);

            foreach (var root in SceneService.Root)
            {
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);
            }

            // --- OPAQUE PASS ---
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            standardShader.Use();
            SetLightUniforms(standardShader, lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange);
            standardShader.SetMatrix4("uView", view);
            standardShader.SetMatrix4("uProj", proj);
            standardShader.SetVector3("uCamPos", camPos);

            if (shadowFBO?.DepthTexture != null)
            {
                standardShader.SetInt("uHasShadow", 1);
                standardShader.SetMatrix4("uShadowVP", shadowVP);
                standardShader.SetFloat("uShadowBias", 0.008f);
                standardShader.SetVector3("uSunDir", sunShineDir);
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture3);
                standardShader.SetTexture("uShadowMap", 3);
            }
            else
            {
                standardShader.SetInt("uHasShadow", 0);
            }

            foreach (var item in opaqueItems)
            {
                DrawMeshItem(gl, standardShader, cache, item);
            }

            // --- TRANSPARENT PASS (back-to-front) ---
            if (transparentItems.Count > 0)
            {
                transparentItems.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false); // don't write depth for transparent

                foreach (var item in transparentItems)
                {
                    DrawMeshItem(gl, standardShader, cache, item);
                }

                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
            }
        }

        /// <summary>
        /// Render the scene depth-only for shadow map generation.
        /// </summary>
        public static void RenderShadowPass(
            GL gl,
            ShaderProgram depthShader,
            ResourceCache cache,
            in SN.Matrix4x4 lightVP)
        {
            var planes = new Plane[6];
            ExtractFrustumPlanes(lightVP, planes);

            depthShader.Use();

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);

            foreach (var root in SceneService.Root)
            {
                RenderShadowNode(gl, depthShader, cache, root, SN.Matrix4x4.Identity, lightVP, planes);
            }

            // Restore default back-face culling after shadow pass
            gl.CullFace(TriangleFace.Back);
        }

        // ---------- Internal traversal ----------

        private static void GatherDrawItems(
            GameObject go,
            in SN.Matrix4x4 parentWorld,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            Plane[] planes,
            List<DrawItem> opaque,
            List<DrawItem> transparent)
        {
            var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

            // Iterate behaviors once, pairing MeshFilters with MeshRenderers in order.
            // Avoids per-call list allocations — use index tracking instead.
            var behaviors = go.Behaviors;
            int nextMR = 0; // tracks which MeshRenderer index to pair with next MeshFilter
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is MeshFilter f && f.Enabled)
                {
                    // Find the matching MeshRenderer (next enabled one at or after nextMR)
                    MeshRenderer? mr = null;
                    for (int j = nextMR; j < behaviors.Count; j++)
                    {
                        if (behaviors[j] is MeshRenderer r && r.Enabled)
                        {
                            mr = r;
                            nextMR = j + 1;
                            break;
                        }
                    }
                    if (mr == null || mr.Wireframe) continue;

                    var mesh = f.Mesh;
                    if (mesh == null) continue;

                    var sph = GetMeshSphere(mesh);
                    if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                    // LOD
                    var surface = new Size(1920, 1080);
                    var lodMesh = MeshLod.EnsureProceduralLod(f, world, view, proj, surface);
                    if (lodMesh == null) continue;

                    // Direct property access — no reflection, no pixel scanning.
                    var mat = mr.Material;
                    bool isTransparent = mat?.Transparent == true || mr.Color.A < 255;

                    float sortZ = 0f;
                    if (isTransparent)
                    {
                        var wv = world * view;
                        var centerV = SN.Vector3.Transform(sph.Center, wv);
                        sortZ = centerV.Z;
                    }

                    var item = new DrawItem
                    {
                        SortZ = sortZ,
                        World = world,
                        MF = f,
                        MR = mr,
                        Mat = mat,
                        IsTransparent = isTransparent
                    };

                    if (isTransparent)
                        transparent.Add(item);
                    else
                        opaque.Add(item);
                }
            }

            var ch = go.Children;
            for (int i = 0; i < ch.Count; i++)
                GatherDrawItems(ch[i], world, view, proj, planes, opaque, transparent);
        }

        private static void DrawMeshItem(
            GL gl,
            ShaderProgram shader,
            ResourceCache cache,
            in DrawItem item)
        {
            var mesh = item.MF.Mesh;
            if (mesh == null) return;

            var gpuMesh = cache.GetMesh(mesh);

            // Model matrix
            shader.SetMatrix4("uModel", item.World);

            // Normal matrix = transpose(inverse(model))
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            var normalMatrix = SN.Matrix4x4.Transpose(invWorld);
            shader.SetMatrix4("uNormalMatrix", normalMatrix);

            // Material properties
            var mat = item.Mat;
            float r = 1f, g2 = 1f, b = 1f, a = 1f;
            float roughness = 0.5f, metallic = 0f, alphaCutoff = 0.5f;
            bool transparent = item.IsTransparent;
            bool doubleSided = item.MR.DoubleSided;

            if (mat != null)
            {
                r = mat.BaseColor.R / 255f;
                g2 = mat.BaseColor.G / 255f;
                b = mat.BaseColor.B / 255f;
                a = mat.BaseColor.A / 255f;
                roughness = mat.Roughness;
                metallic = mat.Metallic;
                alphaCutoff = mat.AlphaCutoff;
                transparent = mat.Transparent || transparent;
            }

            // Apply MeshRenderer tint
            var tint = item.MR.Color;
            r *= tint.R / 255f;
            g2 *= tint.G / 255f;
            b *= tint.B / 255f;
            a *= tint.A / 255f;

            // For blended transparency: if the material says transparent but computed
            // alpha is fully opaque (no texture alpha / no base-color alpha), apply a
            // sensible default so the user can actually see through it.
            if (transparent && a >= 1.0f)
                a = 0.35f;

            // For blended transparency, don't aggressively discard fragments.
            // Alpha-test (high cutoff) is only for opaque-pass cutout materials.
            if (transparent)
                alphaCutoff = 0.01f;

            shader.SetVector4("uBaseColor", r, g2, b, a);
            shader.SetFloat("uRoughness", roughness);
            shader.SetFloat("uMetallic", metallic);
            shader.SetFloat("uAlphaCutoff", alphaCutoff);
            shader.SetInt("uTransparent", transparent ? 1 : 0);
            shader.SetInt("uDoubleSided", doubleSided ? 1 : 0);

            // Cull face
            if (doubleSided)
                gl.Disable(EnableCap.CullFace);
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(item.MR.InvertFrontFace ? TriangleFace.Front : TriangleFace.Back);
            }

            // Albedo texture
            bool hasAlbedo = false;
            if (mat?.Textures != null && mat.Textures.Count > 0)
            {
                // Find first albedo texture — handles both RuntimeTexSlot and MaterialTexture
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? tex = null;
                    var slot = mat.Textures[i];

                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("albedo") || usage == "" || usage.Contains("base") || usage.Contains("diff"))
                            tex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Albedo)
                            tex = mtex.Texture;
                    }

                    if (tex != null)
                    {
                        var gpuTex = cache.GetTexture(tex);
                        gpuTex.Bind(TextureUnit.Texture0);
                        hasAlbedo = true;
                        break;
                    }
                }
            }

            if (!hasAlbedo)
            {
                cache.GetWhiteTexture().Bind(TextureUnit.Texture0);
            }
            shader.SetTexture("uAlbedoTex", 0);
            shader.SetInt("uHasAlbedoTex", hasAlbedo ? 1 : 0);

            // Draw
            gpuMesh.Draw();
        }

        private static void SetLightUniforms(
            ShaderProgram shader,
            SN.Vector3 lightDir, float diffuseK, float ambient,
            bool lightIsPoint, SN.Vector3 lightPosW, float lightRange)
        {
            shader.SetVector3("uLightDir", lightDir);
            shader.SetFloat("uDiffuseK", diffuseK);
            shader.SetFloat("uAmbient", ambient);
            shader.SetInt("uLightIsPoint", lightIsPoint ? 1 : 0);
            shader.SetVector3("uLightPos", lightPosW);
            shader.SetFloat("uLightRange", lightRange);
        }

        private static void RenderShadowNode(
            GL gl,
            ShaderProgram depthShader,
            ResourceCache cache,
            GameObject go,
            in SN.Matrix4x4 parentWorld,
            in SN.Matrix4x4 lightVP,
            Plane[] planes)
        {
            var world = parentWorld * TransformUtil.WorldFromTransform(go.Transform);

            // Pair MeshFilters with MeshRenderers in order (no allocations).
            var behaviors = go.Behaviors;
            int nextMR = 0;
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is MeshFilter f && f.Enabled)
                {
                    MeshRenderer? mr = null;
                    for (int j = nextMR; j < behaviors.Count; j++)
                    {
                        if (behaviors[j] is MeshRenderer r && r.Enabled)
                        {
                            mr = r;
                            nextMR = j + 1;
                            break;
                        }
                    }
                    if (mr == null || !mr.CastShadows) continue;

                    var mesh = f.Mesh;
                    if (mesh == null) continue;

                    var sph = GetMeshSphere(mesh);
                    if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                    var gpuMesh = cache.GetMesh(mesh);
                    var mvp = world * lightVP;
                    depthShader.SetMatrix4("uMVP", mvp);

                    // Front-face culling: only render back faces into the shadow
                    // map. This prevents self-shadowing (shadow acne) on lit surfaces.
                    if (mr.DoubleSided)
                        gl.Disable(EnableCap.CullFace);
                    else
                    {
                        gl.Enable(EnableCap.CullFace);
                        gl.CullFace(TriangleFace.Front);
                    }
                    gpuMesh.Draw();
                }
            }

            var ch = go.Children;
            for (int i = 0; i < ch.Count; i++)
                RenderShadowNode(gl, depthShader, cache, ch[i], world, lightVP, planes);
        }
    }
}
