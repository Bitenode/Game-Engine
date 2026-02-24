#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
        // ---------- Render context for custom shader support ----------
        /// <summary>
        /// Bundles per-frame render state so DrawMeshItem can set uniforms on custom shaders.
        /// </summary>
        private struct RenderContext
        {
            public SN.Matrix4x4 View;
            public SN.Matrix4x4 Proj;
            public SN.Vector3 CamPos;
            public SN.Vector3 LightDir;
            public SN.Vector3 LightColor;
            public float DiffuseK;
            public float Ambient;
            public SN.Matrix4x4 ShadowVP;
            public ShaderProgram StandardShader;
            public ResourceCache Cache;
            public bool IsES;
            /// <summary>When true, skip custom shader detection and always use the standard shader.</summary>
            public bool ForceStandardShader;
        }

        // ---------- Frustum culling ----------
        private struct Sphere { public SN.Vector3 Center; public float Radius; }
        private static readonly Dictionary<Mesh, Sphere> s_meshSpheres = new(1024);

        /// <summary>Periodic cleanup counter to evict orphaned mesh sphere entries.</summary>
        private static int s_sphereCleanupCounter;

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

            // Periodically cap the cache to prevent unbounded growth
            if (++s_sphereCleanupCounter > 500 && s_meshSpheres.Count > 2048)
            {
                s_meshSpheres.Clear(); // nuclear option — entries rebuild lazily
                s_sphereCleanupCounter = 0;
            }
            return s;
        }

        private struct Plane { public float A, B, C, D; }

        // Reusable per-frame buffers to avoid GC pressure
        [ThreadStatic] private static Plane[]? s_planes;
        [ThreadStatic] private static List<DrawItem>? s_opaqueItems;
        [ThreadStatic] private static List<DrawItem>? s_transparentItems;
        [ThreadStatic] private static Plane[]? s_shadowPlanes;

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
            public Terrain? Terrain; // non-null when this mesh belongs to a terrain
            public Tree? Tree;       // non-null for vegetation (wind animation)
            public TreeLOD? TreeLOD;  // non-null for tree LOD management
            public Water? Water;      // non-null for water rendering
            public SkinnedMeshRenderer? Skinned; // non-null for GPU skinned meshes
        }

        // ---------- Particle rendering ----------
        // Reusable buffers for particle instance data
        [ThreadStatic] private static SN.Vector4[]? s_particlePositions;
        [ThreadStatic] private static SN.Vector4[]? s_particleColors;

        /// <summary>
        /// Render all active particle emitters as billboard quads.
        /// Call after main scene rendering, before post-processing.
        /// </summary>
        public static void RenderParticles(
            GL gl,
            ShaderProgram particleShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj)
        {
            var positions = s_particlePositions ??= new SN.Vector4[128];
            var colors = s_particleColors ??= new SN.Vector4[128];

            // Find all particle emitters
            foreach (var root in SceneService.Root)
                RenderParticlesRecursive(gl, particleShader, cache, root, view, proj, positions, colors);
        }

        private static void RenderParticlesRecursive(
            GL gl, ShaderProgram shader, ResourceCache cache,
            GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            SN.Vector4[] positions, SN.Vector4[] colors)
        {
            if (!go.Enabled) return;

            foreach (var b in go.Behaviors)
            {
                if (b is ParticleEmitter pe && pe.IsActiveAndEnabled && pe.ActiveParticleCount > 0)
                {
                    int count = pe.FillRenderData(positions, colors, 128);
                    if (count <= 0) continue;

                    shader.Use();
                    shader.SetMatrix4("uView", view);
                    shader.SetMatrix4("uProj", proj);

                    // Upload particle data as uniform arrays
                    for (int i = 0; i < count; i++)
                    {
                        shader.SetVector4($"uParticlePos[{i}]", positions[i].X, positions[i].Y, positions[i].Z, positions[i].W);
                        shader.SetVector4($"uParticleCol[{i}]", colors[i].X, colors[i].Y, colors[i].Z, colors[i].W);
                    }

                    // Draw instanced billboard quads
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    gl.DepthMask(false);

                    DrawBillboardQuads(gl, count);

                    gl.DepthMask(true);
                    gl.Disable(EnableCap.Blend);
                }
            }

            foreach (var child in go.Children)
                RenderParticlesRecursive(gl, shader, cache, child, view, proj, positions, colors);
        }

        // Billboard quad VAO (lazy init)
        [ThreadStatic] private static uint s_billboardVAO;
        [ThreadStatic] private static uint s_billboardVBO;
        [ThreadStatic] private static bool s_billboardInit;

        private static unsafe void DrawBillboardQuads(GL gl, int instanceCount)
        {
            if (!s_billboardInit)
            {
                float[] quadVerts = {
                    -0.5f, -0.5f,
                     0.5f, -0.5f,
                    -0.5f,  0.5f,
                     0.5f, -0.5f,
                     0.5f,  0.5f,
                    -0.5f,  0.5f
                };

                s_billboardVAO = gl.GenVertexArray();
                s_billboardVBO = gl.GenBuffer();

                gl.BindVertexArray(s_billboardVAO);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, s_billboardVBO);

                fixed (float* ptr = quadVerts)
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);

                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), null);

                gl.BindVertexArray(0);
                s_billboardInit = true;
            }

            gl.BindVertexArray(s_billboardVAO);
            gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)instanceCount);
            gl.BindVertexArray(0);
        }

        // ---------- Water rendering ----------

        /// <summary>
        /// Render all water components using the water shader.
        /// Call after opaque pass, before or during transparent pass.
        /// </summary>
        public static void RenderWater(
            GL gl,
            ShaderProgram waterShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 lightDir,
            float ambient,
            float diffuseK,
            SN.Vector3 camPos,
            SN.Vector3 skyColor)
        {
            foreach (var root in SceneService.Root)
                RenderWaterRecursive(gl, waterShader, cache, root, SN.Matrix4x4.Identity, view, proj,
                    lightDir, ambient, diffuseK, camPos, skyColor);
        }

        private static void RenderWaterRecursive(
            GL gl, ShaderProgram shader, ResourceCache cache,
            GameObject go, in SN.Matrix4x4 parentWorld,
            in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
            SN.Vector3 lightDir, float ambient, float diffuseK,
            SN.Vector3 camPos, SN.Vector3 skyColor)
        {
            if (!go.Enabled) return;

            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

            foreach (var b in go.Behaviors)
            {
                if (b is Water water && water.IsActiveAndEnabled)
                {
                    // Ensure the water mesh is built (editor doesn't call Awake)
                    water.EnsureMesh();

                    var mf = go.Behaviors.OfType<MeshFilter>().FirstOrDefault();
                    if (mf?.Mesh == null) continue;

                    shader.Use();
                    shader.SetMatrix4("uModel", world);
                    shader.SetMatrix4("uView", view);
                    shader.SetMatrix4("uProj", proj);

                    SN.Matrix4x4.Invert(world, out var invWorld);
                    shader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

                    // Wave uniforms
                    shader.SetFloat("uTime", water.AnimTime);
                    shader.SetFloat("uWaveAmp1", water.WaveAmplitude);
                    shader.SetFloat("uWaveFreq1", water.WaveFrequency);
                    shader.SetVector2("uWaveDir1", water.WaveDirection.X, water.WaveDirection.Y);
                    shader.SetFloat("uWaveSteep1", water.WaveSteepness);
                    shader.SetFloat("uWaveAmp2", water.Wave2Amplitude);
                    shader.SetFloat("uWaveFreq2", water.Wave2Frequency);
                    shader.SetVector2("uWaveDir2", water.Wave2Direction.X, water.Wave2Direction.Y);

                    // Water appearance
                    shader.SetVector4("uShallowColor", water.ShallowColor.X, water.ShallowColor.Y, water.ShallowColor.Z, water.ShallowColor.W);
                    shader.SetVector4("uDeepColor", water.DeepColor.X, water.DeepColor.Y, water.DeepColor.Z, water.DeepColor.W);
                    shader.SetFloat("uFresnelPower", water.FresnelPower);
                    shader.SetFloat("uReflectivity", water.Reflectivity);
                    shader.SetFloat("uTransparency", water.Transparency);

                    // Foam
                    shader.SetInt("uFoamEnabled", water.FoamEnabled ? 1 : 0);
                    shader.SetFloat("uFoamThreshold", water.FoamDepthThreshold);
                    shader.SetFloat("uFoamIntensity", water.FoamIntensity);
                    shader.SetVector3("uFoamColor", new SN.Vector3(water.FoamColor.X, water.FoamColor.Y, water.FoamColor.Z));

                    // Lighting
                    shader.SetVector3("uLightDir", lightDir);
                    shader.SetFloat("uAmbient", ambient);
                    shader.SetFloat("uDiffuseK", diffuseK);
                    shader.SetVector3("uCamPos", camPos);
                    shader.SetVector3("uSkyColor", skyColor);

                    // Draw
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    gl.Disable(EnableCap.CullFace);

                    var gpuMesh = cache.GetMesh(mf.Mesh);
                    gpuMesh.Draw();

                    gl.Enable(EnableCap.CullFace);
                    gl.Disable(EnableCap.Blend);
                }
            }

            foreach (var child in go.Children)
                RenderWaterRecursive(gl, shader, cache, child, world, view, proj,
                    lightDir, ambient, diffuseK, camPos, skyColor);
        }

        // ---------- Post-Processing ----------

        /// <summary>
        /// Apply post-processing effects to the scene texture.
        /// Renders a fullscreen quad with the post-processing shader.
        /// The caller MUST pass the volume obtained from GetActive() — this avoids
        /// a second lookup that could return a different result mid-frame.
        /// </summary>
        public static void ApplyPostProcessing(
            GL gl,
            ShaderProgram postShader,
            GPUTexture sceneTexture,
            int viewportWidth,
            int viewportHeight,
            PostProcessVolume? volume = null,
            UnderwaterState? underwater = null,
            float underwaterTime = 0f)
        {
            // Always bind the post-process shader, even for passthrough —
            // rendering without a shader causes a black screen.
            postShader.Use();

            sceneTexture.Bind(TextureUnit.Texture0);
            postShader.SetTexture("uScene", 0);
            postShader.SetVector2("uTexelSize", 1f / viewportWidth, 1f / viewportHeight);

            if (volume == null)
            {
                // Pass-through: disable all effects so the shader just copies the texture
                postShader.SetInt("uBloomEnabled", 0);
                postShader.SetInt("uFogEnabled", 0);
                postShader.SetInt("uColorGradingEnabled", 0);
                postShader.SetInt("uVignetteEnabled", 0);
                postShader.SetInt("uFXAAEnabled", 0);
                postShader.SetFloat("uExposure", 1f);
                postShader.SetFloat("uContrast", 1f);
                postShader.SetFloat("uSaturation", 1f);
                postShader.SetFloat("uBrightness", 0f);
                postShader.SetInt("uToneMap", 0);
            }
            else
            {
                // Bloom
                postShader.SetInt("uBloomEnabled", volume.BloomEnabled ? 1 : 0);
                postShader.SetFloat("uBloomThreshold", volume.BloomThreshold);
                postShader.SetFloat("uBloomIntensity", volume.BloomIntensity);

                // Fog
                postShader.SetInt("uFogEnabled", volume.FogEnabled ? 1 : 0);
                postShader.SetVector3("uFogColor", volume.FogColor);
                postShader.SetFloat("uFogDensity", volume.FogDensity);
                postShader.SetFloat("uFogStart", volume.FogStart);
                postShader.SetFloat("uFogEnd", volume.FogEnd);

                // Color Grading
                postShader.SetInt("uColorGradingEnabled", volume.ColorGradingEnabled ? 1 : 0);
                postShader.SetFloat("uBrightness", volume.Brightness);
                postShader.SetFloat("uContrast", volume.Contrast);
                postShader.SetFloat("uSaturation", volume.Saturation);
                postShader.SetFloat("uExposure", volume.Exposure);
                postShader.SetInt("uToneMap", (int)volume.ToneMap);

                // Vignette
                postShader.SetInt("uVignetteEnabled", volume.VignetteEnabled ? 1 : 0);
                postShader.SetFloat("uVignetteIntensity", volume.VignetteIntensity);
                postShader.SetFloat("uVignetteSmoothness", volume.VignetteSmoothness);

                // FXAA
                postShader.SetInt("uFXAAEnabled", volume.FXAAEnabled ? 1 : 0);
            }

            // Underwater
            if (underwater.HasValue)
            {
                var uw = underwater.Value;
                postShader.SetInt("uUnderwaterEnabled", 1);
                postShader.SetVector3("uUnderwaterTint", uw.Tint);
                postShader.SetFloat("uUnderwaterFogDensity", uw.FogDensity);
                postShader.SetFloat("uUnderwaterCausticStr", uw.CausticStrength);
                postShader.SetFloat("uUnderwaterDistortion", uw.Distortion);
                postShader.SetFloat("uUnderwaterTime", underwaterTime);
                postShader.SetFloat("uUnderwaterDepth", uw.Depth);
            }
            else
            {
                postShader.SetInt("uUnderwaterEnabled", 0);
            }

            // Draw fullscreen triangle (covers entire screen with a single triangle)
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Enable(EnableCap.DepthTest);
        }

        // ---------- GPU RENDER ENTRY POINT ----------

        /// <summary>
        /// Render the entire scene using GPU draw calls.
        /// Must be called within an active GL context.
        /// </summary>
        // NOTE: Splatmap textures are now managed per-context via ResourceCache.GetTerrainSplatTextures()
        // to avoid cross-GL-context issues between SceneView and GameView.

        // Static string arrays for terrain shader uniforms (avoid per-draw-call allocation)
        private static readonly string[] s_layerNames = { "uLayer0", "uLayer1", "uLayer2", "uLayer3", "uLayer4", "uLayer5", "uLayer6", "uLayer7" };
        private static readonly string[] s_tilingNames = { "uTiling0", "uTiling1", "uTiling2", "uTiling3", "uTiling4", "uTiling5", "uTiling6", "uTiling7" };

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
            SN.Vector3 sunShineDir = default,
            ShaderProgram? terrainShader = null,
            bool isES = true,
            SN.Vector3 lightColor = default)
        {
            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            // Reuse draw-item lists to avoid GC pressure
            var opaqueItems = s_opaqueItems ??= new List<DrawItem>(256);
            var transparentItems = s_transparentItems ??= new List<DrawItem>(64);
            opaqueItems.Clear();
            transparentItems.Clear();

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
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture7);
                standardShader.SetTexture("uShadowMap", 7);

                standardShader.SetInt("uCascadeCount", 1);
                standardShader.SetMatrix4("uShadowVPC[0]", shadowVP);
                standardShader.SetFloat("uCascadeSplits[0]", 1000f);
            }
            else
            {
                standardShader.SetInt("uHasShadow", 0);
            }

            // Build render context for custom shader support
            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = lightDir,
                LightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor,
                DiffuseK = diffuseK,
                Ambient = ambient,
                ShadowVP = shadowVP,
                StandardShader = standardShader,
                Cache = cache,
                IsES = isES
            };

            // --- BATCH terrain draw items by Terrain reference ---
            // This avoids rebinding splatmap + layer textures for every single chunk.
            // We first draw all non-terrain opaque items, then group terrain items by terrain.
            Terrain? boundTerrain = null;
            bool terrainShaderActive = false;

            foreach (var item in opaqueItems)
            {
                if (item.Terrain != null && terrainShader != null && item.Terrain.Layers.Count > 0)
                    continue; // terrain items are drawn in a second pass below
                if (terrainShaderActive)
                {
                    standardShader.Use();
                    if (shadowFBO?.DepthTexture != null)
                    {
                        shadowFBO.DepthTexture.Bind(TextureUnit.Texture7);
                        standardShader.SetTexture("uShadowMap", 7);
                    }
                    terrainShaderActive = false;
                }
                DrawMeshItem(gl, standardShader, cache, item, in renderCtx);
            }

            // Now draw ALL terrain items, grouped by terrain to minimize state changes
            if (terrainShader != null)
            {
                terrainShader.Use();
                SetLightUniforms(terrainShader, lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange);
                terrainShader.SetMatrix4("uView", view);
                terrainShader.SetMatrix4("uProj", proj);
                terrainShader.SetVector3("uCamPos", camPos);

                if (shadowFBO?.DepthTexture != null)
                {
                    terrainShader.SetInt("uHasShadow", 1);
                    terrainShader.SetMatrix4("uShadowVP", shadowVP);
                    terrainShader.SetFloat("uShadowBias", 0.008f);
                    terrainShader.SetVector3("uSunDir", sunShineDir);
                    shadowFBO.DepthTexture.Bind(TextureUnit.Texture2);
                    terrainShader.SetTexture("uShadowMap", 2);
                }
                else
                {
                    terrainShader.SetInt("uHasShadow", 0);
                }

                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Back);
                terrainShaderActive = true;

                foreach (var item in opaqueItems)
                {
                    if (item.Terrain == null || item.Terrain.Layers.Count <= 0) continue;

                    if (!ReferenceEquals(item.Terrain, boundTerrain))
                    {
                        boundTerrain = item.Terrain;
                        BindTerrainState(gl, terrainShader, cache, boundTerrain);

                gl.Enable(EnableCap.CullFace);
                    }
                    DrawTerrainChunk(gl, terrainShader, cache, item);
                }
            }

            // --- TRANSPARENT PASS (back-to-front) ---
            if (transparentItems.Count > 0)
            {
                transparentItems.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);

                // Ensure standard shader is active for transparent pass
                standardShader.Use();
                if (shadowFBO?.DepthTexture != null)
                {
                    shadowFBO.DepthTexture.Bind(TextureUnit.Texture7);
                    standardShader.SetTexture("uShadowMap", 7);
                }

                foreach (var item in transparentItems)
                {
                    DrawMeshItem(gl, standardShader, cache, item, in renderCtx);
                }

                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
            }

            // --- WORLD-SPACE UI CANVASES (rendered as transparent overlays in 3D) ---
            RenderWorldSpaceCanvases(gl, cache, in view, in proj);
        }

        /// <summary>
        /// Render world-space UI canvases as textured quads in 3D space.
        /// Called at the end of the forward pass after the transparent pass.
        /// </summary>
        public static void RenderWorldSpaceCanvases(
            GL gl,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj)
        {
            var worldCanvases = Component.UI.Canvas.All
                .Where(c => c.IsActiveAndEnabled && c.RenderMode == Component.UI.CanvasRenderMode.WorldSpace)
                .ToList();

            if (worldCanvases.Count == 0) return;

            // World-space canvases are rendered by the CanvasRenderer instance
            // owned by the view (GameView/SceneView). The view calls
            // CanvasRenderer.RenderWorldCanvas() for each world-space canvas
            // after calling RenderGPU(). This static method is a hook for
            // future use if we want to batch world-space canvases in the scene renderer.
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
            var planes = s_shadowPlanes ??= new Plane[6];
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

        /// <summary>
        /// Render cascaded shadow maps. Renders the shadow pass once per cascade.
        /// </summary>
        public static void RenderCascadedShadowPass(
            GL gl,
            ShaderProgram depthShader,
            ResourceCache cache,
            CascadedShadowMap csm,
            uint defaultFB = 0)
        {
            for (int i = 0; i < csm.CascadeCount; i++)
            {
                var cascade = csm.Cascades[i];
                cascade.Begin(gl);
                gl.Viewport(0, 0, (uint)cascade.Width, (uint)cascade.Height);
                RenderShadowPass(gl, depthShader, cache, csm.LightVPs[i]);
                cascade.End(gl, defaultFB);
            }
        }

        /// <summary>
        /// Bind cascaded shadow map textures and uniforms to a shader.
        /// </summary>
        public static void BindCascadeShadowUniforms(
            ShaderProgram shader,
            CascadedShadowMap csm,
            int baseTextureUnit = 7)
        {
            shader.SetInt("uCascadeCount", csm.CascadeCount);
            shader.SetInt("uHasShadow", 1);

            // Bind cascade shadow maps to texture units
            string[] samplerNames = { "uShadowMap", "uShadowMapC1", "uShadowMapC2", "uShadowMapC3" };
            for (int i = 0; i < csm.CascadeCount; i++)
            {
                int unit = baseTextureUnit + i;
                csm.Cascades[i].FBO.DepthTexture?.Bind(TextureUnit.Texture0 + unit);
                shader.SetTexture(samplerNames[i], unit);
            }

            // Upload per-cascade VP matrices and split distances
            for (int i = 0; i < csm.CascadeCount; i++)
            {
                shader.SetMatrix4($"uShadowVPC[{i}]", csm.LightVPs[i]);
                shader.SetFloat($"uCascadeSplits[{i}]", csm.SplitDistances[i]);
            }

            // Backward compat: set uShadowVP to cascade 0
            shader.SetMatrix4("uShadowVP", csm.LightVPs[0]);
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
            if (!go.Enabled) return;

            // Skip planet chunks and water — they're rendered by the dedicated planet pipeline.
            if (go.Name != null && (go.Name.StartsWith("PlanetChunk_") || go.Name == "PlanetWater"))
                return;

            // Fast skip for vegetation chunks whose MeshRenderer was disabled by distance culling.
            if (go.Name.StartsWith("chunk_"))
            {
                var bh = go.Behaviors;
                for (int k = 0; k < bh.Count; k++)
                    if (bh[k] is MeshRenderer cmr) { if (!cmr.Enabled) return; break; }
            }

            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

            // Detect terrain component on this GO or its parent (for splatmap shader path)
            // Chunks are children of the terrain GO but don't have a Terrain component themselves.
            Terrain? terrain = null;
            Tree? tree = null;
            TreeLOD? treeLod = null;
            foreach (var b in go.Behaviors)
            {
                if (terrain == null && b is Terrain tt && tt.Enabled) terrain = tt;
                if (tree == null && b is Tree tr && tr.Enabled) tree = tr;
                if (treeLod == null && b is TreeLOD tl && tl.Enabled) treeLod = tl;
            }
            if (terrain == null && go.Parent != null)
            {
                foreach (var b in go.Parent.Behaviors)
                    if (b is Terrain tt && tt.Enabled) { terrain = tt; break; }
            }

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

                    // Ensure bone matrices are computed for skinned meshes (editor doesn't run Start/LateUpdate)
                    var skinned = mr as SkinnedMeshRenderer;
                    if (skinned != null)
                        skinned.EnsureBoneMatrices();

                    var item = new DrawItem
                    {
                        SortZ = sortZ,
                        World = world,
                        MF = f,
                        MR = mr,
                        Mat = mat,
                        IsTransparent = isTransparent,
                        Terrain = terrain,
                        Tree = tree,
                        TreeLOD = treeLod,
                        Skinned = skinned
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
            in DrawItem item,
            in RenderContext ctx)
        {
            var mesh = item.MF.Mesh;
            if (mesh == null) return;

            var gpuMesh = cache.GetMesh(mesh);

            // ── Custom shader path ──
            // If the material references a .shader file, try to use it instead of the standard shader.
            // Skipped when ForceStandardShader is set (e.g., G-buffer pass in deferred pipeline).
            var mat = item.Mat;
            if (!ctx.ForceStandardShader && mat != null && !string.IsNullOrWhiteSpace(mat.ShaderAssetPath))
            {
                string shaderPath = mat.ShaderAssetPath;
                string absPath = shaderPath;
                var proj = ProjectService.Current;
                if (proj != null && !System.IO.Path.IsPathRooted(shaderPath))
                    absPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(proj.RootPath, shaderPath));

                var customShader = CustomShaderCache.GetOrCompile(absPath, gl, ctx.IsES);
                if (customShader != null)
                {
                    DrawWithCustomShader(gl, customShader, cache, item, gpuMesh, in ctx);
                    // Restore the standard shader for subsequent draws
                    ctx.StandardShader.Use();
                    SetLightUniforms(ctx.StandardShader, ctx.LightDir, ctx.DiffuseK, ctx.Ambient, false, SN.Vector3.Zero, 0f);
                    ctx.StandardShader.SetMatrix4("uView", ctx.View);
                    ctx.StandardShader.SetMatrix4("uProj", ctx.Proj);
                    ctx.StandardShader.SetVector3("uCamPos", ctx.CamPos);
                    return;
                }
            }

            // ── Standard shader path (unchanged) ──

            // Model matrix
            shader.SetMatrix4("uModel", item.World);

            // Normal matrix = transpose(inverse(model))
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            var normalMatrix = SN.Matrix4x4.Transpose(invWorld);
            shader.SetMatrix4("uNormalMatrix", normalMatrix);

            // Material properties
            float r = 1f, g2 = 1f, b = 1f, a = 1f;
            float roughness = 0.5f, metallic = 0f, alphaCutoff = 0f;
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

            // Emissive
            float emR = 0f, emG = 0f, emB = 0f, emIntensity = 0f;
            if (mat != null && mat.EmissiveIntensity > 0f)
            {
                emR = mat.EmissiveColor.R / 255f;
                emG = mat.EmissiveColor.G / 255f;
                emB = mat.EmissiveColor.B / 255f;
                emIntensity = mat.EmissiveIntensity;
            }
            shader.SetVector3("uEmissiveColor", new SN.Vector3(emR, emG, emB));
            shader.SetFloat("uEmissiveIntensity", emIntensity);

            // Cull face
            if (doubleSided)
                gl.Disable(EnableCap.CullFace);
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(item.MR.InvertFrontFace ? TriangleFace.Front : TriangleFace.Back);
            }

            // Wind / vegetation uniforms
            if (item.Tree != null && item.Tree.IsVegetation)
            {
                shader.SetInt("uIsVegetation", 1);
                shader.SetFloat("uWindTime", WindSystem.Time * item.Tree.WindSpeed);
                shader.SetVector3("uWindDir", WindSystem.Direction);
                shader.SetFloat("uWindStrength", WindSystem.GetCurrentStrength() * item.Tree.WindSway);
            }
            else
            {
                shader.SetInt("uIsVegetation", 0);
            }

            // Bone skinning matrices
            if (item.Skinned != null && item.Skinned.HasValidBoneMatrices)
            {
                shader.SetInt("uHasBones", 1);
                var bones = item.Skinned.BoneMatrices!;
                int count = System.Math.Min(bones.Length, SkeletonLimits.MaxBones);
                for (int bi = 0; bi < count; bi++)
                    shader.SetMatrix4($"uBones[{bi}]", bones[bi]);
            }
            else
            {
                shader.SetInt("uHasBones", 0);
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

            // Normal map texture
            bool hasNormal = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? nTex = null;
                    var slot = mat.Textures[i];

                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("normal") || usage.Contains("bump"))
                            nTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Normal)
                            nTex = mtex.Texture;
                    }

                    if (nTex != null)
                    {
                        cache.GetTexture(nTex).Bind(TextureUnit.Texture1);
                        hasNormal = true;
                        break;
                    }
                }
            }
            shader.SetTexture("uNormalMap", 1);
            shader.SetInt("uHasNormalMap", hasNormal ? 1 : 0);
            shader.SetFloat("uNormalStrength", mat?.NormalStrength ?? 1f);

            // Specular texture → Texture2
            bool hasSpec = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? sTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("specular")) sTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Specular) sTex = mtex.Texture;
                    }
                    if (sTex != null) { cache.GetTexture(sTex).Bind(TextureUnit.Texture2); hasSpec = true; break; }
                }
            }
            shader.SetTexture("uSpecularTex", 2);
            shader.SetInt("uHasSpecularTex", hasSpec ? 1 : 0);

            // Metallic texture → Texture3
            bool hasMetal = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? mTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("metallic") || usage.Contains("metalness")) mTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Metallic) mTex = mtex.Texture;
                    }
                    if (mTex != null) { cache.GetTexture(mTex).Bind(TextureUnit.Texture3); hasMetal = true; break; }
                }
            }
            shader.SetTexture("uMetallicTex", 3);
            shader.SetInt("uHasMetallicTex", hasMetal ? 1 : 0);

            // Roughness texture → Texture4
            bool hasRough = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? rTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("rough")) rTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Roughness) rTex = mtex.Texture;
                    }
                    if (rTex != null) { cache.GetTexture(rTex).Bind(TextureUnit.Texture4); hasRough = true; break; }
                }
            }
            shader.SetTexture("uRoughnessTex", 4);
            shader.SetInt("uHasRoughnessTex", hasRough ? 1 : 0);

            // Ambient occlusion texture → Texture5
            bool hasAO = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? aoTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("occlusion") || usage.Contains("ao")) aoTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.AmbientOcclusion) aoTex = mtex.Texture;
                    }
                    if (aoTex != null) { cache.GetTexture(aoTex).Bind(TextureUnit.Texture5); hasAO = true; break; }
                }
            }
            shader.SetTexture("uAOTex", 5);
            shader.SetInt("uHasAOTex", hasAO ? 1 : 0);

            // Emissive texture → Texture6
            bool hasEmissiveTex = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? eTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("emissive") || usage.Contains("emission")) eTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Emissive) eTex = mtex.Texture;
                    }
                    if (eTex != null) { cache.GetTexture(eTex).Bind(TextureUnit.Texture6); hasEmissiveTex = true; break; }
                }
            }
            shader.SetTexture("uEmissiveTex", 6);
            shader.SetInt("uHasEmissiveTex", hasEmissiveTex ? 1 : 0);

            // Emissive (update the values set earlier if we now have an emissive texture)
            if (mat != null && (mat.EmissiveIntensity > 0f || hasEmissiveTex))
            {
                shader.SetVector3("uEmissiveColor", new SN.Vector3(
                    mat.EmissiveColor.R / 255f,
                    mat.EmissiveColor.G / 255f,
                    mat.EmissiveColor.B / 255f));
                // Ensure at least 1.0 intensity when an emissive texture is present
                float emI = mat.EmissiveIntensity;
                if (hasEmissiveTex && emI < 1f) emI = 1f;
                shader.SetFloat("uEmissiveIntensity", emI);
            }
            else
            {
                shader.SetVector3("uEmissiveColor", SN.Vector3.Zero);
                shader.SetFloat("uEmissiveIntensity", 0f);
            }

            // Draw
            gpuMesh.Draw();
        }

        // ────────── CUSTOM SHADER DRAW ──────────

        /// <summary>
        /// Draw a mesh using a custom shader compiled from the Visual Shader Editor.
        /// Binds all available material textures (albedo, normal, specular) and sets
        /// lighting/shadow uniforms using the custom shader naming convention.
        /// </summary>
        private static void DrawWithCustomShader(
            GL gl,
            ShaderProgram customShader,
            ResourceCache cache,
            in DrawItem item,
            GPUMesh gpuMesh,
            in RenderContext ctx)
        {
            customShader.Use();

            // ── Vertex uniforms ──
            customShader.SetMatrix4("uModel", item.World);
            customShader.SetMatrix4("uView", ctx.View);
            customShader.SetMatrix4("uProjection", ctx.Proj);
            customShader.SetMatrix4("uLightSpaceMatrix", ctx.ShadowVP);

            // Normal matrix = transpose(inverse(model))
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            var normalMatrix = SN.Matrix4x4.Transpose(invWorld);
            customShader.SetMatrix4("uNormalMatrix", normalMatrix);

            // ── Fragment uniforms ──
            customShader.SetVector3("uCameraPos", ctx.CamPos);
            customShader.SetFloat("uTime", WindSystem.Time);

            // Both the standard shader and custom shaders use the same convention:
            // uLightDir = direction FROM the light (the "shine" direction).
            // Shaders negate internally: dot(N, -uLightDir) or vec3 L = normalize(-uLightDir).
            customShader.SetVector3("uLightDir", ctx.LightDir);
            customShader.SetVector3("uLightColor", ctx.LightColor);
            customShader.SetFloat("uLightIntensity", ctx.DiffuseK);
            customShader.SetFloat("uAmbient", ctx.Ambient);

            // Material properties
            var mat = item.Mat;
            float roughness = mat?.Roughness ?? 0.5f;
            float metallic = mat?.Metallic ?? 0f;
            customShader.SetFloat("uRoughness", roughness);
            customShader.SetFloat("uMetallic", metallic);

            // ── Bind albedo texture → uTexture0 (unit 0) ──
            bool hasAlbedo = false;
            if (mat?.Textures != null)
            {
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
                    if (tex != null) { cache.GetTexture(tex).Bind(TextureUnit.Texture0); hasAlbedo = true; break; }
                }
            }
            if (!hasAlbedo) cache.GetWhiteTexture().Bind(TextureUnit.Texture0);
            customShader.SetTexture("uTexture0", 0);

            // ── Bind normal map → uTexture1 (unit 1) ──
            bool hasNormal = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? nTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("normal") || usage.Contains("bump"))
                            nTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Normal)
                            nTex = mtex.Texture;
                    }
                    if (nTex != null) { cache.GetTexture(nTex).Bind(TextureUnit.Texture1); hasNormal = true; break; }
                }
            }
            if (!hasNormal)
            {
                // Bind a flat normal texture (0.5, 0.5, 1.0 = identity)
                cache.GetWhiteTexture().Bind(TextureUnit.Texture1);
            }
            customShader.SetTexture("uTexture1", 1);
            customShader.SetInt("uHasNormalMap", hasNormal ? 1 : 0);
            customShader.SetFloat("uNormalStrength", mat?.NormalStrength ?? 1f);

            // ── Bind specular map → uTexture2 (unit 2) ──
            bool hasSpec = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? sTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("specular") || usage.Contains("spec"))
                            sTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Specular)
                            sTex = mtex.Texture;
                    }
                    if (sTex != null) { cache.GetTexture(sTex).Bind(TextureUnit.Texture2); hasSpec = true; break; }
                }
            }
            if (!hasSpec) cache.GetWhiteTexture().Bind(TextureUnit.Texture2);
            customShader.SetTexture("uTexture2", 2);
            customShader.SetInt("uHasSpecularMap", hasSpec ? 1 : 0);

            // ── Bind shadow map → unit 4 ──
            // (matches deferred lighting convention so the shader can optionally use it)
            customShader.SetInt("uHasShadow", 0);

            // ── Cull face ──
            bool doubleSided = item.MR.DoubleSided;
            if (doubleSided)
                gl.Disable(EnableCap.CullFace);
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(item.MR.InvertFrontFace ? TriangleFace.Front : TriangleFace.Back);
            }

            gpuMesh.Draw();
        }

        // ────────── TERRAIN SPLATMAP DRAW (batched) ──────────

        /// <summary>
        /// Bind per-terrain state: splatmaps, layer textures, tiling uniforms.
        /// Call once per unique Terrain before drawing its chunks.
        /// </summary>
        private static void BindTerrainState(GL gl, ShaderProgram shader, ResourceCache cache, Terrain terrain)
        {
            // Ensure splatmaps
            terrain.EnsureSplatmaps();

            // Get per-context splatmap textures. NeedsUpload is true when this context's
            // cached version doesn't match the terrain's current SplatmapVersion.
            var (splat0, splat1, needsUpload) = cache.GetTerrainSplatTextures(terrain);

            // Upload if textures are new for this context OR if terrain data changed
            if (needsUpload)
            {
                splat0.UploadFloat(terrain.Splatmap0!, terrain.ResX, terrain.ResZ);
                splat1.UploadFloat(terrain.Splatmap1!, terrain.ResX, terrain.ResZ);
                cache.SetTerrainSplatVersion(terrain, terrain.SplatmapVersion);
            }

            // Bind splatmaps on units 0,1
            splat0.Bind(TextureUnit.Texture0);
            shader.SetTexture("uSplatmap0", 0);
            splat1.Bind(TextureUnit.Texture1);
            shader.SetTexture("uSplatmap1", 1);

            // Layer count + textures — bound ONCE for all chunks of this terrain
            int layerCount = Math.Min(terrain.Layers.Count, 8);
            shader.SetInt("uLayerCount", layerCount);

            for (int i = 0; i < layerCount; i++)
            {
                var layer = terrain.Layers[i];
                int texUnit = 4 + i;

                Texture2D? layerTex = null;
                if (!string.IsNullOrEmpty(layer.TexturePath))
                    layerTex = TryLoadLayerTexture(layer.TexturePath);

                if (layerTex != null)
                    cache.GetTexture(layerTex).Bind(TextureUnit.Texture0 + texUnit);
                else
                    cache.GetWhiteTexture().Bind(TextureUnit.Texture0 + texUnit);

                shader.SetTexture(s_layerNames[i], texUnit);
                shader.SetFloat(s_tilingNames[i], layer.Tiling);
            }
        }

        /// <summary>
        /// Draw a single terrain chunk. Only sets per-chunk state (model matrix).
        /// Assumes BindTerrainState was already called for this chunk's terrain.
        /// </summary>
        private static void DrawTerrainChunk(GL gl, ShaderProgram shader, ResourceCache cache, in DrawItem item)
        {
            var mesh = item.MF.Mesh;
            if (mesh == null) return;

            var gpuMesh = cache.GetMesh(mesh);

            // Per-chunk: model matrix + normal matrix
            shader.SetMatrix4("uModel", item.World);
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            shader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

            // Base color fallback
            var mat = item.Mat;
            float r = 1f, g2 = 1f, b = 1f, a = 1f;
            if (mat != null)
            {
                r = mat.BaseColor.R / 255f; g2 = mat.BaseColor.G / 255f;
                b = mat.BaseColor.B / 255f; a = mat.BaseColor.A / 255f;
            }
            var tint = item.MR.Color;
            r *= tint.R / 255f; g2 *= tint.G / 255f; b *= tint.B / 255f; a *= tint.A / 255f;
            shader.SetVector4("uBaseColor", r, g2, b, a);
            shader.SetInt("uHasAlbedoTex", 0);

            gpuMesh.Draw();
        }

        // Cache loaded layer textures by path
        private static readonly Dictionary<string, Texture2D?> s_layerTextureCache = new();

        private static string? ResolveTexturePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                // 1) Absolute path as-is
                if (System.IO.Path.IsPathRooted(path))
                {
                    string abs = System.IO.Path.GetFullPath(path);
                    if (System.IO.File.Exists(abs)) return abs;
                }

                // 2) Project-relative path (preferred)
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    string fromProj = System.IO.Path.GetFullPath(System.IO.Path.Combine(proj.RootPath, path));
                    if (System.IO.File.Exists(fromProj)) return fromProj;
                }

                // 3) Current working directory relative
                string fromCwd = System.IO.Path.GetFullPath(path);
                if (System.IO.File.Exists(fromCwd)) return fromCwd;

                // 4) App base directory relative (e.g. bin/Debug launch)
                string fromBase = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, path));
                if (System.IO.File.Exists(fromBase)) return fromBase;

                // 5) If stored as Assets/..., walk parent directories to find project root
                string norm = path.Replace('\\', '/');
                if (norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                    while (dir != null)
                    {
                        string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, path));
                        if (System.IO.File.Exists(candidate)) return candidate;
                        dir = dir.Parent;
                    }
                }
            }
            catch { }

            return null;
        }

        private static Texture2D? TryLoadLayerTexture(string path)
        {
            if (s_layerTextureCache.TryGetValue(path, out var cached))
                return cached;

            Texture2D? tex = null;
            try
            {
                var abs = ResolveTexturePath(path);
                if (abs != null)
                    tex = Texture2D.FromFile(abs);
            }
            catch { }

            s_layerTextureCache[path] = tex;
            return tex;
        }

        // ══════════════════════════════════════════════════════════
        //  DEFERRED RENDERING PIPELINE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// G-buffer geometry pass: draws opaque standard items to the G-buffer MRT.
        /// Populates thread-static draw item lists for use by RenderForwardOverlays.
        /// The G-buffer FBO must be bound before calling this method.
        /// </summary>
        public static void RenderGBufferPass(
            GL gl,
            ShaderProgram gbufferShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP,
            SN.Vector3 sunShineDir,
            bool isES = true)
        {
            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            var opaqueItems = s_opaqueItems ??= new List<DrawItem>(256);
            var transparentItems = s_transparentItems ??= new List<DrawItem>(64);
            opaqueItems.Clear();
            transparentItems.Clear();

            foreach (var root in SceneService.Root)
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            gbufferShader.Use();
            gbufferShader.SetMatrix4("uView", view);
            gbufferShader.SetMatrix4("uProj", proj);
            gbufferShader.SetVector3("uCamPos", camPos);
            gbufferShader.SetMatrix4("uShadowVP", shadowVP);

            // Build render context — ForceStandardShader ensures custom shader items
            // render with the G-buffer shader instead of their custom shaders.
            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = SN.Vector3.UnitY,
                LightColor = SN.Vector3.One,
                DiffuseK = 0f,
                Ambient = 0f,
                ShadowVP = shadowVP,
                StandardShader = gbufferShader,
                Cache = cache,
                IsES = isES,
                ForceStandardShader = true
            };

            foreach (var item in opaqueItems)
            {
                // Skip terrain (forward-rendered with splatmap shader)
                if (item.Terrain != null && item.Terrain.Layers.Count > 0) continue;

                // Custom shader items are rendered here with the G-buffer shader
                // (standard PBR). Custom shader effects only apply in Scene View
                // (forward renderer). This avoids cross-pipeline rendering issues.
                DrawMeshItem(gl, gbufferShader, cache, item, in renderCtx);
            }
        }

        /// <summary>
        /// Deferred lighting pass: fullscreen quad that reads G-buffer, computes PBR Cook-Torrance
        /// lighting with multi-light support, and outputs the lit scene.
        /// </summary>
        public static void RenderDeferredLighting(
            GL gl,
            ShaderProgram deferredShader,
            FullscreenQuad fsQuad,
            GPUFramebuffer gbufferFBO,
            GPUTexture? ssaoTexture,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            in SN.Matrix4x4 shadowVP,
            SN.Vector3 sunShineDir,
            float ambient,
            float shadowBias)
        {
            deferredShader.Use();

            // Bind G-buffer textures
            if (gbufferFBO.ColorTextures != null && gbufferFBO.ColorTextures.Length >= 3)
            {
                gbufferFBO.ColorTextures[0].Bind(TextureUnit.Texture0);
                deferredShader.SetTexture("gAlbedoMetallic", 0);

                gbufferFBO.ColorTextures[1].Bind(TextureUnit.Texture1);
                deferredShader.SetTexture("gNormalRoughness", 1);

                gbufferFBO.ColorTextures[2].Bind(TextureUnit.Texture2);
                deferredShader.SetTexture("gEmissiveAO", 2);
            }

            // Depth
            if (gbufferFBO.DepthTexture != null)
            {
                gbufferFBO.DepthTexture.Bind(TextureUnit.Texture3);
                deferredShader.SetTexture("gDepth", 3);
            }

            // Shadow map
            if (shadowFBO?.DepthTexture != null)
            {
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture4);
                deferredShader.SetTexture("uShadowMap", 4);
                deferredShader.SetInt("uHasShadow", 1);
                deferredShader.SetMatrix4("uShadowVP", shadowVP);
                deferredShader.SetFloat("uShadowBias", shadowBias);
                deferredShader.SetVector3("uSunDir", sunShineDir);
            }
            else
            {
                deferredShader.SetInt("uHasShadow", 0);
            }

            // SSAO
            if (ssaoTexture != null)
            {
                ssaoTexture.Bind(TextureUnit.Texture5);
                deferredShader.SetTexture("uSSAOTex", 5);
                deferredShader.SetInt("uHasSSAO", 1);
            }
            else
            {
                deferredShader.SetInt("uHasSSAO", 0);
            }

            // Camera + inverse VP
            deferredShader.SetVector3("uCamPos", camPos);
            var vp = view * proj;
            SN.Matrix4x4.Invert(vp, out var invVP);
            deferredShader.SetMatrix4("uInvViewProj", invVP);

            // Ambient
            deferredShader.SetFloat("uAmbient", ambient);

            // Multi-light upload
            var lights = Component.Light.AllLights;
            int lightCount = Math.Min(lights.Count, 16);
            deferredShader.SetInt("uLightCount", lightCount);

            for (int i = 0; i < lightCount; i++)
            {
                var light = lights[i];
                int type = light.Type == LightType.Directional ? 0 : 1;
                deferredShader.SetInt($"uLightTypes[{i}]", type);

                if (light.Type == LightType.Directional)
                {
                    var dir = light.GetWorldDirection();
                    deferredShader.SetVector3($"uLightDirs[{i}]", SN.Vector3.Normalize(-dir));
                }
                else
                {
                    deferredShader.SetVector3($"uLightPositions[{i}]", light.GetWorldPosition());
                    deferredShader.SetFloat($"uLightRanges[{i}]", Math.Max(0.001f, light.Range));
                }

                var col = light.GetColorRGB();
                deferredShader.SetVector3($"uLightColors[{i}]", new SN.Vector3(
                    light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f));
                deferredShader.SetFloat($"uLightIntensities[{i}]", light.Intensity);
            }

            // Draw fullscreen quad
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            fsQuad.Draw();
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// Forward overlay pass: renders terrain, custom shader items, and transparent objects.
        /// Must be called after RenderGBufferPass (which populates the draw item lists).
        /// </summary>
        public static void RenderForwardOverlays(
            GL gl,
            ShaderProgram standardShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            SN.Vector3 lightDir,
            float diffuseK,
            float ambient,
            bool lightIsPoint,
            SN.Vector3 lightPosW,
            float lightRange,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP,
            SN.Vector3 sunShineDir,
            ShaderProgram? terrainShader = null,
            bool isES = true,
            SN.Vector3 lightColor = default)
        {
            var opaqueItems = s_opaqueItems;
            var transparentItems = s_transparentItems;
            if (opaqueItems == null) return;

            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = lightDir,
                LightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor,
                DiffuseK = diffuseK,
                Ambient = ambient,
                ShadowVP = shadowVP,
                StandardShader = standardShader,
                Cache = cache,
                IsES = isES
            };

            // --- TERRAIN PASS ---
            if (terrainShader != null)
            {
                Terrain? boundTerrain = null;
                terrainShader.Use();
                SetLightUniforms(terrainShader, lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange);
                terrainShader.SetMatrix4("uView", view);
                terrainShader.SetMatrix4("uProj", proj);
                terrainShader.SetVector3("uCamPos", camPos);

                if (shadowFBO?.DepthTexture != null)
                {
                    terrainShader.SetInt("uHasShadow", 1);
                    terrainShader.SetMatrix4("uShadowVP", shadowVP);
                    terrainShader.SetFloat("uShadowBias", 0.008f);
                    terrainShader.SetVector3("uSunDir", sunShineDir);
                    shadowFBO.DepthTexture.Bind(TextureUnit.Texture2);
                    terrainShader.SetTexture("uShadowMap", 2);
                }
                else
                {
                    terrainShader.SetInt("uHasShadow", 0);
                }

                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Back);

                foreach (var item in opaqueItems)
                {
                    if (item.Terrain == null || item.Terrain.Layers.Count <= 0) continue;
                    if (!ReferenceEquals(item.Terrain, boundTerrain))
                    {
                        boundTerrain = item.Terrain;
                        BindTerrainState(gl, terrainShader, cache, boundTerrain);
                    }
                    DrawTerrainChunk(gl, terrainShader, cache, item);
                }
            }

            // CUSTOM SHADER FORWARD PASS — re-render items that have custom shaders
            // with their actual shaders. They were already written to the G-buffer (for
            // depth, SSAO, etc.) with the standard PBR shader, so we use GL_LEQUAL depth
            // to overdraw only where they already exist.
            {
                var customCtx = new RenderContext
                {
                    View = view,
                    Proj = proj,
                    CamPos = camPos,
                    LightDir = lightDir,
                    LightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor,
                    DiffuseK = diffuseK,
                    Ambient = ambient,
                    ShadowVP = shadowVP,
                    StandardShader = standardShader,
                    Cache = cache,
                    IsES = isES,
                    ForceStandardShader = false
                };

                gl.DepthFunc(DepthFunction.Lequal);
                foreach (var item in opaqueItems)
                {
                    if (item.Terrain != null) continue;
                    var mat = item.Mat;
                    if (mat == null || string.IsNullOrWhiteSpace(mat.ShaderAssetPath)) continue;
                    DrawMeshItem(gl, standardShader, cache, item, in customCtx);
                }
                gl.DepthFunc(DepthFunction.Less);
            }

            // Set up the standard shader for transparent items below.
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
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture7);
                standardShader.SetTexture("uShadowMap", 7);

                standardShader.SetInt("uCascadeCount", 1);
                standardShader.SetMatrix4("uShadowVPC[0]", shadowVP);
                standardShader.SetFloat("uCascadeSplits[0]", 1000f);
            }
            else
            {
                standardShader.SetInt("uHasShadow", 0);
            }

            // --- TRANSPARENT PASS (back-to-front) ---
            if (transparentItems != null && transparentItems.Count > 0)
            {
                transparentItems.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);

                standardShader.Use();
                if (shadowFBO?.DepthTexture != null)
                {
                    shadowFBO.DepthTexture.Bind(TextureUnit.Texture7);
                    standardShader.SetTexture("uShadowMap", 7);
                }

                foreach (var item in transparentItems)
                {
                    DrawMeshItem(gl, standardShader, cache, item, in renderCtx);
                }

                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
            }
        }

        /// <summary>
        /// SSAO pass: computes screen-space ambient occlusion from G-buffer depth + normals.
        /// The SSAO FBO must be bound before calling.
        /// </summary>
        public static void RenderSSAO(
            GL gl,
            ShaderProgram ssaoShader,
            FullscreenQuad fsQuad,
            GPUFramebuffer gbufferFBO,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3[] kernel,
            int screenWidth,
            int screenHeight,
            float radius = 0.5f,
            float bias = 0.025f)
        {
            ssaoShader.Use();

            // Bind G-buffer textures
            if (gbufferFBO.ColorTextures != null && gbufferFBO.ColorTextures.Length >= 2)
            {
                gbufferFBO.ColorTextures[1].Bind(TextureUnit.Texture0);
                ssaoShader.SetTexture("gNormalRoughness", 0);
            }
            if (gbufferFBO.DepthTexture != null)
            {
                gbufferFBO.DepthTexture.Bind(TextureUnit.Texture1);
                ssaoShader.SetTexture("gDepth", 1);
            }

            ssaoShader.SetMatrix4("uProjection", proj);
            ssaoShader.SetMatrix4("uView", view);
            var vp = view * proj;
            SN.Matrix4x4.Invert(vp, out var invVP);
            ssaoShader.SetMatrix4("uInvViewProj", invVP);

            // Upload kernel samples
            for (int i = 0; i < Math.Min(kernel.Length, 32); i++)
                ssaoShader.SetVector3($"uSamples[{i}]", kernel[i]);

            ssaoShader.SetFloat("uRadius", radius);
            ssaoShader.SetFloat("uBias", bias);
            ssaoShader.SetVector2("uNoiseScale", screenWidth / 4f, screenHeight / 4f);

            gl.Disable(EnableCap.DepthTest);
            fsQuad.Draw();
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>SSAO blur pass. The blur FBO must be bound before calling.</summary>
        public static void RenderSSAOBlur(
            GL gl,
            ShaderProgram blurShader,
            FullscreenQuad fsQuad,
            GPUTexture ssaoInput,
            int width,
            int height)
        {
            blurShader.Use();
            ssaoInput.Bind(TextureUnit.Texture0);
            blurShader.SetTexture("uSSAOInput", 0);
            blurShader.SetVector2("uTexelSize", 1f / width, 1f / height);

            gl.Disable(EnableCap.DepthTest);
            fsQuad.Draw();
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// SSR pass: screen-space reflections from the lit scene + G-buffer.
        /// The output FBO must be bound before calling.
        /// </summary>
        public static void RenderSSR(
            GL gl,
            ShaderProgram ssrShader,
            FullscreenQuad fsQuad,
            GPUTexture litScene,
            GPUFramebuffer gbufferFBO,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            int screenWidth,
            int screenHeight)
        {
            ssrShader.Use();

            litScene.Bind(TextureUnit.Texture0);
            ssrShader.SetTexture("uLitScene", 0);

            if (gbufferFBO.ColorTextures != null && gbufferFBO.ColorTextures.Length >= 2)
            {
                gbufferFBO.ColorTextures[1].Bind(TextureUnit.Texture1);
                ssrShader.SetTexture("gNormalRoughness", 1);

                gbufferFBO.ColorTextures[0].Bind(TextureUnit.Texture2);
                ssrShader.SetTexture("gAlbedoMetallic", 2);
            }
            if (gbufferFBO.DepthTexture != null)
            {
                gbufferFBO.DepthTexture.Bind(TextureUnit.Texture3);
                ssrShader.SetTexture("gDepth", 3);
            }

            ssrShader.SetMatrix4("uView", view);
            ssrShader.SetMatrix4("uProjection", proj);
            var vp = view * proj;
            SN.Matrix4x4.Invert(vp, out var invVP);
            ssrShader.SetMatrix4("uInvViewProj", invVP);
            ssrShader.SetVector3("uCamPos", camPos);
            ssrShader.SetVector2("uScreenSize", screenWidth, screenHeight);

            gl.Disable(EnableCap.DepthTest);
            fsQuad.Draw();
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// Volumetric fog pass: ray-marched fullscreen effect that reads the scene
        /// color + depth and composites fog with light scattering and shadow occlusion.
        /// The output FBO must be bound before calling.
        /// </summary>
        public static void RenderVolumetricFog(
            GL gl,
            ShaderProgram volFogShader,
            FullscreenQuad fsQuad,
            GPUTexture sceneColor,
            GPUTexture sceneDepth,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            SN.Vector3 lightDir,
            SN.Vector3 lightColor,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP,
            PostProcessVolume volume,
            float time)
        {
            volFogShader.Use();

            sceneColor.Bind(TextureUnit.Texture0);
            volFogShader.SetTexture("uSceneColor", 0);

            sceneDepth.Bind(TextureUnit.Texture1);
            volFogShader.SetTexture("gDepth", 1);

            var vp = view * proj;
            SN.Matrix4x4.Invert(vp, out var invVP);
            volFogShader.SetMatrix4("uInvViewProj", invVP);
            volFogShader.SetVector3("uCamPos", camPos);
            volFogShader.SetVector3("uLightDir", lightDir);
            volFogShader.SetVector3("uLightColor", lightColor);

            volFogShader.SetFloat("uFogDensity", volume.VolumetricFogDensity);
            volFogShader.SetFloat("uFogAnisotropy", volume.VolumetricFogAnisotropy);
            volFogShader.SetFloat("uFogScattering", volume.VolumetricFogScattering);
            volFogShader.SetFloat("uFogHeightFalloff", volume.VolumetricFogHeightFalloff);
            volFogShader.SetFloat("uFogBaseHeight", volume.VolumetricFogBaseHeight);
            volFogShader.SetFloat("uFogNoiseScale", volume.VolumetricFogNoiseScale);
            volFogShader.SetFloat("uFogNoiseSpeed", volume.VolumetricFogNoiseSpeed);
            volFogShader.SetFloat("uFogMaxDistance", volume.VolumetricFogMaxDistance);
            volFogShader.SetVector3("uFogColor", volume.VolumetricFogColor);
            volFogShader.SetInt("uFogSteps", Math.Clamp(volume.VolumetricFogSteps, 4, 128));
            volFogShader.SetFloat("uTime", time);

            if (shadowFBO?.DepthTexture != null)
            {
                volFogShader.SetInt("uHasShadow", 1);
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture2);
                volFogShader.SetTexture("uShadowMap", 2);
                volFogShader.SetMatrix4("uShadowVP", shadowVP);
            }
            else
            {
                volFogShader.SetInt("uHasShadow", 0);
            }

            gl.Disable(EnableCap.DepthTest);
            fsQuad.Draw();
            gl.Enable(EnableCap.DepthTest);
        }

        // ══════════════════════════════════════════════════════════

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
            if (!go.Enabled) return;

            // Planet chunks are rendered by the planet pipeline, not the standard shadow pass.
            if (go.Name != null && (go.Name.StartsWith("PlanetChunk_") || go.Name == "PlanetWater"))
                return;

            // Fast skip for culled vegetation chunks
            if (go.Name.StartsWith("chunk_"))
            {
                var bh = go.Behaviors;
                for (int k = 0; k < bh.Count; k++)
                    if (bh[k] is MeshRenderer cmr) { if (!cmr.Enabled) return; break; }
            }

            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

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

                    // Handle skinned mesh bone matrices in shadow pass
                    var skinned = mr as SkinnedMeshRenderer;
                    if (skinned != null)
                        skinned.EnsureBoneMatrices();

                    if (skinned != null && skinned.HasValidBoneMatrices)
                    {
                        depthShader.SetInt("uHasBones", 1);
                        var bones = skinned.BoneMatrices!;
                        int boneCount = System.Math.Min(bones.Length, SkeletonLimits.MaxBones);
                        for (int bi = 0; bi < boneCount; bi++)
                            depthShader.SetMatrix4($"uBones[{bi}]", bones[bi]);
                    }
                    else
                    {
                        depthShader.SetInt("uHasBones", 0);
                    }

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

        // ==================================================================
        // PLANET TERRAIN rendering
        // ==================================================================

        public readonly struct PlanetAtmosphereRenderParams
        {
            public readonly bool Enabled;
            public readonly bool CloudsEnabled;
            public readonly SN.Vector3 SunDir;
            public readonly float SunIntensity;
            public readonly float Ambient;
            public readonly float GroundRadius;
            public readonly float AtmosphereHeight;
            public readonly float AtmosphereBlend;
            public readonly float RayleighStrength;
            public readonly float MieStrength;
            public readonly float DensityFalloff;
            public readonly float HorizonBlend;
            public readonly float SunsetBoost;
            public readonly int SampleCount;
            public readonly SN.Vector3 ZenithTint;
            public readonly SN.Vector3 HorizonTint;
            public readonly SN.Vector3 SkyTint;
            public readonly float CloudBaseHeight;
            public readonly float CloudTopHeight;
            public readonly float CloudCoverage;
            public readonly float CloudDensity;
            public readonly float CloudDetail;
            public readonly float CloudSpeed;
            public readonly float CloudSoftness;
            public readonly float CloudLightResponse;
            public readonly float CloudSilverLining;
            public readonly int CloudStepCount;

            public PlanetAtmosphereRenderParams(
                bool enabled,
                bool cloudsEnabled,
                SN.Vector3 sunDir,
                float sunIntensity,
                float ambient,
                float groundRadius,
                float atmosphereHeight,
                float atmosphereBlend,
                float rayleighStrength,
                float mieStrength,
                float densityFalloff,
                float horizonBlend,
                float sunsetBoost,
                int sampleCount,
                SN.Vector3 zenithTint,
                SN.Vector3 horizonTint,
                SN.Vector3 skyTint,
                float cloudBaseHeight,
                float cloudTopHeight,
                float cloudCoverage,
                float cloudDensity,
                float cloudDetail,
                float cloudSpeed,
                float cloudSoftness,
                float cloudLightResponse,
                float cloudSilverLining,
                int cloudStepCount)
            {
                Enabled = enabled;
                CloudsEnabled = cloudsEnabled;
                SunDir = sunDir;
                SunIntensity = sunIntensity;
                Ambient = ambient;
                GroundRadius = groundRadius;
                AtmosphereHeight = atmosphereHeight;
                AtmosphereBlend = atmosphereBlend;
                RayleighStrength = rayleighStrength;
                MieStrength = mieStrength;
                DensityFalloff = densityFalloff;
                HorizonBlend = horizonBlend;
                SunsetBoost = sunsetBoost;
                SampleCount = sampleCount;
                ZenithTint = zenithTint;
                HorizonTint = horizonTint;
                SkyTint = skyTint;
                CloudBaseHeight = cloudBaseHeight;
                CloudTopHeight = cloudTopHeight;
                CloudCoverage = cloudCoverage;
                CloudDensity = cloudDensity;
                CloudDetail = cloudDetail;
                CloudSpeed = cloudSpeed;
                CloudSoftness = cloudSoftness;
                CloudLightResponse = cloudLightResponse;
                CloudSilverLining = cloudSilverLining;
                CloudStepCount = cloudStepCount;
            }
        }

        public static PlanetAtmosphereRenderParams ResolvePlanetAtmosphere(
            PlanetTerrain planet,
            Light? sceneLight,
            SN.Vector3 fallbackSunDir,
            float fallbackAmbient)
        {
            var atmo = planet.Atmosphere;
            float radius = planet.Config?.Radius ?? planet.Radius;
            if (atmo != null && atmo.GroundRadiusOverride > 0.01f)
                radius = atmo.GroundRadiusOverride;

            SN.Vector3 sunDir = fallbackSunDir.LengthSquared() > 1e-5f
                ? SN.Vector3.Normalize(fallbackSunDir)
                : SN.Vector3.Normalize(new SN.Vector3(0.20f, 0.82f, 0.53f));
            if (atmo != null)
            {
                if (atmo.UseDirectionalLight && sceneLight?.Type == LightType.Directional && sceneLight.gameObject != null)
                {
                    var world = TransformUtil.WorldFromTransform(sceneLight.gameObject.Transform);
                    var fwd = new SN.Vector3(world.M13, world.M23, world.M33);
                    if (fwd.LengthSquared() > 1e-6f) sunDir = SN.Vector3.Normalize(fwd);
                }
                else
                {
                    sunDir = atmo.SunDirectionOverride;
                }
            }

            float sunIntensity = atmo?.SunIntensity ?? 1f;
            var zenith = atmo?.ZenithTint ?? new SN.Vector3(0.26f, 0.40f, 0.92f);
            var horizon = atmo?.HorizonTint ?? new SN.Vector3(0.82f, 0.86f, 0.98f);
            float sunUp = Math.Clamp(sunDir.Y * 0.5f + 0.5f, 0f, 1f);
            var skyTint = SN.Vector3.Lerp(horizon, zenith, sunUp);
            skyTint *= 0.35f + sunIntensity * 0.65f;

            return new PlanetAtmosphereRenderParams(
                enabled: atmo?.Enabled ?? false,
                cloudsEnabled: atmo?.EnableClouds ?? false,
                sunDir: sunDir,
                sunIntensity: Math.Max(0.01f, sunIntensity),
                ambient: Math.Clamp(atmo?.Ambient ?? fallbackAmbient, 0f, 1f),
                groundRadius: Math.Max(1f, radius),
                atmosphereHeight: Math.Max(1f, atmo?.AtmosphereHeight ?? 120f),
                atmosphereBlend: Math.Clamp(atmo?.AtmosphereBlend ?? 0.45f, 0f, 1.25f),
                rayleighStrength: Math.Max(0f, atmo?.RayleighStrength ?? 1.0f),
                mieStrength: Math.Max(0f, atmo?.MieStrength ?? 0.30f),
                densityFalloff: Math.Max(0.1f, atmo?.DensityFalloff ?? 1.25f),
                horizonBlend: Math.Max(0f, atmo?.HorizonBlend ?? 1.0f),
                sunsetBoost: Math.Max(0f, atmo?.SunsetBoost ?? 1.0f),
                sampleCount: Math.Clamp(atmo?.SampleCount ?? 8, 2, 32),
                zenithTint: zenith,
                horizonTint: horizon,
                skyTint: skyTint,
                cloudBaseHeight: Math.Max(1f, atmo?.CloudBaseHeight ?? 32f),
                cloudTopHeight: Math.Max(2f, atmo?.CloudTopHeight ?? 88f),
                cloudCoverage: Math.Clamp(atmo?.CloudCoverage ?? 0.46f, 0f, 1f),
                cloudDensity: Math.Max(0f, atmo?.CloudDensity ?? 1.0f),
                cloudDetail: Math.Max(0.1f, atmo?.CloudDetail ?? 2.0f),
                cloudSpeed: Math.Max(0f, atmo?.CloudSpeed ?? 0.025f),
                cloudSoftness: Math.Clamp(atmo?.CloudSoftness ?? 0.30f, 0.01f, 1.0f),
                cloudLightResponse: Math.Max(0f, atmo?.CloudLightResponse ?? 0.9f),
                cloudSilverLining: Math.Max(0f, atmo?.CloudSilverLining ?? 0.65f),
                cloudStepCount: Math.Clamp(atmo?.CloudStepCount ?? 16, 4, 64));
        }

        public static void RenderPlanetTerrain(
            GL gl,
            ShaderProgram planetShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            PlanetTerrain planet,
            in PlanetAtmosphereRenderParams atmo,
            SN.Vector3 lightDir,
            float diffuseK,
            SN.Vector3 camPos,
            SN.Vector3 planetCenter,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP)
        {
            if (planet.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled)
                return;

            var vp = view * proj;
            ExtractFrustumPlanes(vp, out var frustumPlanes);

            planetShader.Use();
            planetShader.SetMatrix4("uView", view);
            planetShader.SetMatrix4("uProj", proj);
            planetShader.SetVector3("uLightDir", lightDir);
            planetShader.SetVector3("uCamPos", camPos);
            planetShader.SetFloat("uAmbient", atmo.Ambient);
            planetShader.SetFloat("uDiffuseK", diffuseK);
            planetShader.SetVector3("uPlanetCenter", planetCenter);
            planetShader.SetFloat("uPlanetRadius", atmo.GroundRadius);

            planetShader.SetInt("uAtmoEnabled", atmo.Enabled ? 1 : 0);
            planetShader.SetVector3("uAtmoSunDir", atmo.SunDir);
            planetShader.SetFloat("uAtmoSunIntensity", atmo.SunIntensity);
            planetShader.SetFloat("uAtmoBlend", atmo.AtmosphereBlend);
            planetShader.SetFloat("uAtmoRayleigh", atmo.RayleighStrength);
            planetShader.SetFloat("uAtmoMie", atmo.MieStrength);
            planetShader.SetFloat("uAtmoDensityFalloff", atmo.DensityFalloff);
            planetShader.SetFloat("uAtmoHorizonBlend", atmo.HorizonBlend);
            planetShader.SetFloat("uAtmoSunsetBoost", atmo.SunsetBoost);
            planetShader.SetFloat("uAtmoHeight", atmo.AtmosphereHeight);
            planetShader.SetInt("uAtmoSampleCount", atmo.SampleCount);
            planetShader.SetVector3("uAtmoZenithTint", atmo.ZenithTint);
            planetShader.SetVector3("uAtmoHorizonTint", atmo.HorizonTint);
            planetShader.SetVector3("uAtmoSkyTint", atmo.SkyTint);

            if (shadowFBO != null)
            {
                planetShader.SetMatrix4("uShadowVP", shadowVP);
                planetShader.SetInt("uHasShadow", 1);
                gl.ActiveTexture(TextureUnit.Texture15);
                gl.BindTexture(TextureTarget.Texture2D, shadowFBO.DepthTexture?.Handle ?? 0);
                planetShader.SetTexture("uShadowMap", 15);
            }
            else
            {
                planetShader.SetInt("uHasShadow", 0);
            }

            BindBiomeTextures(gl, planetShader, cache, planet.Config.Biomes);

            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            var go = planet.gameObject;
            var parentWorld = TransformUtil.WorldFromTransform(go.Transform);

            foreach (var child in go.Children)
            {
                if (!child.Enabled) continue;
                if (child.Name == null || !child.Name.StartsWith("PlanetChunk_")) continue;

                MeshFilter? mf = null;
                foreach (var b in child.Behaviors)
                    if (b is MeshFilter f && f.Enabled) { mf = f; break; }
                if (mf?.Mesh == null) continue;

                var world = TransformUtil.WorldFromTransform(child.Transform) * parentWorld;
                var chunkSphere = GetMeshSphere(mf.Mesh);
                // Frustum test in world space (center transformed by model matrix).
                var worldCenter = SN.Vector3.Transform(chunkSphere.Center, world);
                var sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                var sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                var sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                float worldRadius = chunkSphere.Radius * MathF.Max(sx, MathF.Max(sy, sz));
                if (!SphereInFrustum(frustumPlanes, worldCenter, worldRadius))
                    continue;

                planetShader.SetMatrix4("uModel", world);
                SN.Matrix4x4.Invert(world, out var invWorld);
                planetShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

                var gpuMesh = cache.GetMesh(mf.Mesh);
                gpuMesh.Draw();
            }
        }

        private static void ExtractFrustumPlanes(in SN.Matrix4x4 vp, out SN.Vector4[] planes)
        {
            planes = new SN.Vector4[6];
            planes[0] = new SN.Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41); // Left
            planes[1] = new SN.Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41); // Right
            planes[2] = new SN.Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42); // Bottom
            planes[3] = new SN.Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42); // Top
            planes[4] = new SN.Vector4(vp.M14 + vp.M13, vp.M24 + vp.M23, vp.M34 + vp.M33, vp.M44 + vp.M43); // Near
            planes[5] = new SN.Vector4(vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43); // Far
            for (int i = 0; i < 6; i++)
            {
                float len = new SN.Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
                if (len > 0.0001f) planes[i] /= len;
            }
        }

        private static bool SphereInFrustum(SN.Vector4[] planes, SN.Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
            {
                float dist = planes[i].X * center.X + planes[i].Y * center.Y + planes[i].Z * center.Z + planes[i].W;
                if (dist < -radius) return false;
            }
            return true;
        }

        // Keep CPU textures cached globally; convert to GPU per-context via ResourceCache.
        private static Texture2D?[]? _boundBiomeTex2D;
        private static string?[]? _boundBiomePaths;
        private static bool _biomeTexDirty = true;
        private static float[]? _cachedTiling;
        private static SN.Vector3[]? _cachedBaseColor;
        private static SN.Vector3[]? _cachedUnderColor;

        public static void ResetBiomeTexDebug() { _biomeTexDirty = true; }

        private static void BindBiomeTextures(GL gl, ShaderProgram shader, ResourceCache cache,
            Game_Engine.Core.Biome.BiomeDefinition[] biomes)
        {
            if (_boundBiomeTex2D == null)
            {
                _boundBiomeTex2D = new Texture2D?[8];
                _boundBiomePaths = new string[8];
                _cachedTiling = new float[8];
                _cachedBaseColor = new SN.Vector3[8];
                _cachedUnderColor = new SN.Vector3[8];
            }

            bool needRebuild = _biomeTexDirty;
            if (!needRebuild)
            {
                for (int i = 0; i < 8 && i < biomes.Length; i++)
                {
                    if (_boundBiomePaths![i] != biomes[i].TopTexturePath)
                    { needRebuild = true; break; }
                }
            }

            if (needRebuild)
            {
                Log.Info("[BiomeTex] Rebuilding biome texture bindings...");

                for (int i = 0; i < 8 && i < biomes.Length; i++)
                {
                    var b = biomes[i];
                    _boundBiomePaths![i] = b.TopTexturePath;
                    _boundBiomeTex2D[i] = null;

                    if (!string.IsNullOrWhiteSpace(b.TopTexturePath))
                    {
                        try
                        {
                            var absPath = ResolveTexturePath(b.TopTexturePath);
                            if (absPath != null && System.IO.File.Exists(absPath))
                            {
                                var tex2d = Texture2D.FromFile(absPath);
                                _boundBiomeTex2D[i] = tex2d;
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': {tex2d.Width}x{tex2d.Height} OK");
                            }
                            else
                            {
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': file not found '{b.TopTexturePath}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}' EXCEPTION: {ex.Message}");
                        }
                    }

                    // Keep null here; bind fallback white per-context below.

                    _cachedTiling![i] = b.TopTiling;
                    _cachedBaseColor![i] = new SN.Vector3(b.BaseColorR, b.BaseColorG, b.BaseColorB);

                    // Under-texture: compute average color if path is set, else darken base
                    var underCol = new SN.Vector3(
                        Math.Max(b.BaseColorR * 0.6f, 0.12f),
                        Math.Max(b.BaseColorG * 0.5f, 0.1f),
                        Math.Max(b.BaseColorB * 0.5f, 0.1f));
                    if (!string.IsNullOrWhiteSpace(b.UnderTexturePath))
                    {
                        try
                        {
                            var underAbs = ResolveTexturePath(b.UnderTexturePath);
                            if (underAbs != null && System.IO.File.Exists(underAbs))
                            {
                                var underTex = Texture2D.FromFile(underAbs);
                                var pixels = underTex.Rgba;
                                long rSum = 0, gSum = 0, bSum = 0;
                                int step = Math.Max(1, pixels.Length / 256);
                                int samples = 0;
                                for (int px = 0; px < pixels.Length; px += step * 4)
                                {
                                    if (px + 2 < pixels.Length)
                                    {
                                        rSum += pixels[px]; gSum += pixels[px + 1]; bSum += pixels[px + 2];
                                        samples++;
                                    }
                                }
                                if (samples > 0)
                                    underCol = new SN.Vector3(rSum / (samples * 255f), gSum / (samples * 255f), bSum / (samples * 255f));
                            }
                        }
                        catch { /* fall back to darkened base */ }
                    }
                    _cachedUnderColor![i] = underCol;
                }

                _biomeTexDirty = false;
            }

            for (int i = 0; i < 8 && i < biomes.Length; i++)
            {
                var tex2d = _boundBiomeTex2D![i];
                var gpu = tex2d != null ? cache.GetTexture(tex2d) : cache.GetWhiteTexture();
                gpu.Bind((TextureUnit)((int)TextureUnit.Texture0 + i));
                shader.SetTexture($"uBiomeTex{i}", i);
            }

            for (int i = 0; i < 8 && i < biomes.Length; i++)
            {
                shader.SetFloat($"uBiomeTiling[{i}]", _cachedTiling![i]);
                shader.SetVector3($"uBiomeBaseColor[{i}]", _cachedBaseColor![i]);
                shader.SetVector3($"uBiomeUnderColor[{i}]", _cachedUnderColor![i]);
            }
        }

        // ==================================================================
        // PLANET WATER rendering
        // ==================================================================

        public static void RenderPlanetWater(
            GL gl,
            ShaderProgram waterShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            PlanetTerrain planet,
            in PlanetAtmosphereRenderParams atmo,
            SN.Vector3 lightDir,
            float diffuseK,
            SN.Vector3 camPos,
            SN.Vector3 planetCenter,
            float seaLevel)
        {
            if (planet.gameObject == null || !planet.IsActiveAndEnabled || !planet.EnableWater)
                return;

            var waterObj = planet.WaterGO;
            if (waterObj == null) return;

            var mf = waterObj.Behaviors.OfType<MeshFilter>().FirstOrDefault();
            if (mf?.Mesh == null) return;

            var world = TransformUtil.WorldFromTransform(waterObj.Transform)
                      * TransformUtil.WorldFromTransform(planet.gameObject.Transform);

            waterShader.Use();
            waterShader.SetMatrix4("uModel", world);
            waterShader.SetMatrix4("uView", view);
            waterShader.SetMatrix4("uProj", proj);

            SN.Matrix4x4.Invert(world, out var invWorld);
            waterShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

            waterShader.SetVector3("uPlanetCenter", planetCenter);
            waterShader.SetFloat("uTime", planet.WaterAnimTime);
            waterShader.SetFloat("uWaveAmp1", 0.4f);
            waterShader.SetFloat("uWaveFreq1", 0.6f);
            waterShader.SetFloat("uWaveSteep1", 0.25f);
            waterShader.SetFloat("uWaveAmp2", 0.2f);
            waterShader.SetFloat("uWaveFreq2", 1.2f);

            var oceanBiome = planet.OceanBiome;
            waterShader.SetVector4("uShallowColor",
                oceanBiome.WaterShallowColorR, oceanBiome.WaterShallowColorG,
                oceanBiome.WaterShallowColorB, 1f);
            waterShader.SetVector4("uDeepColor",
                oceanBiome.WaterDeepColorR, oceanBiome.WaterDeepColorG,
                oceanBiome.WaterDeepColorB, 1f);
            waterShader.SetVector4("uDeepestColor",
                oceanBiome.WaterDeepestColorR, oceanBiome.WaterDeepestColorG,
                oceanBiome.WaterDeepestColorB, 1f);

            waterShader.SetVector3("uPlanetCenter", planetCenter);
            waterShader.SetFloat("uSeaLevel", seaLevel);
            waterShader.SetFloat("uDepthRange", oceanBiome.WaterDepthColorRange);

            waterShader.SetFloat("uFresnelPower", 4.0f);
            waterShader.SetFloat("uReflectivity", 0.65f);
            waterShader.SetFloat("uTransparency", 0.75f);

            waterShader.SetVector3("uLightDir", lightDir);
            waterShader.SetVector3("uCamPos", camPos);
            waterShader.SetVector3("uSkyColor", atmo.SkyTint);
            waterShader.SetFloat("uAmbient", atmo.Ambient);
            waterShader.SetFloat("uDiffuseK", diffuseK);
            waterShader.SetInt("uAtmoEnabled", atmo.Enabled ? 1 : 0);
            waterShader.SetVector3("uAtmoSunDir", atmo.SunDir);
            waterShader.SetFloat("uAtmoSunIntensity", atmo.SunIntensity);
            waterShader.SetFloat("uAtmoBlend", atmo.AtmosphereBlend);
            waterShader.SetFloat("uAtmoRayleigh", atmo.RayleighStrength);
            waterShader.SetFloat("uAtmoMie", atmo.MieStrength);
            waterShader.SetFloat("uAtmoDensityFalloff", atmo.DensityFalloff);
            waterShader.SetFloat("uAtmoHorizonBlend", atmo.HorizonBlend);
            waterShader.SetFloat("uAtmoSunsetBoost", atmo.SunsetBoost);
            waterShader.SetFloat("uAtmoHeight", atmo.AtmosphereHeight);
            waterShader.SetVector3("uAtmoZenithTint", atmo.ZenithTint);
            waterShader.SetVector3("uAtmoHorizonTint", atmo.HorizonTint);
            waterShader.SetFloat("uPlanetRadius", atmo.GroundRadius);

            waterShader.SetInt("uHasWaterNormalMap", 0);
            waterShader.SetInt("uHasWaterTexture", 0);
            waterShader.SetInt("uFoamEnabled", 1);
            waterShader.SetFloat("uFoamThreshold", 0.6f);
            waterShader.SetFloat("uFoamIntensity", 0.4f);
            waterShader.SetVector4("uFoamColor", 0.9f, 0.95f, 1.0f, 1.0f);

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            var gpuMesh = cache.GetMesh(mf.Mesh);
            gpuMesh.Draw();

            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }

        public static void RenderPlanetClouds(
            GL gl,
            ShaderProgram cloudShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            PlanetTerrain planet,
            in PlanetAtmosphereRenderParams atmo,
            SN.Vector3 camPos,
            SN.Vector3 planetCenter,
            float timeSec)
        {
            if (!atmo.Enabled || !atmo.CloudsEnabled || planet.gameObject == null || !planet.IsActiveAndEnabled || planet.Config == null)
                return;

            cloudShader.Use();
            cloudShader.SetMatrix4("uView", view);
            cloudShader.SetMatrix4("uProj", proj);
            cloudShader.SetVector3("uCamPos", camPos);
            cloudShader.SetVector3("uPlanetCenter", planetCenter);
            cloudShader.SetFloat("uPlanetRadius", atmo.GroundRadius);
            cloudShader.SetFloat("uCloudBaseHeight", atmo.CloudBaseHeight);
            cloudShader.SetFloat("uCloudTopHeight", Math.Max(atmo.CloudBaseHeight + 0.01f, atmo.CloudTopHeight));
            cloudShader.SetFloat("uCloudCoverage", atmo.CloudCoverage);
            cloudShader.SetFloat("uCloudDensity", atmo.CloudDensity);
            cloudShader.SetFloat("uCloudDetail", atmo.CloudDetail);
            cloudShader.SetFloat("uCloudSpeed", atmo.CloudSpeed);
            cloudShader.SetFloat("uCloudSoftness", atmo.CloudSoftness);
            cloudShader.SetFloat("uCloudLightResponse", atmo.CloudLightResponse);
            cloudShader.SetFloat("uCloudSilverLining", atmo.CloudSilverLining);
            cloudShader.SetInt("uCloudStepCount", atmo.CloudStepCount);
            cloudShader.SetVector3("uSunDir", atmo.SunDir);
            cloudShader.SetFloat("uSunIntensity", atmo.SunIntensity);
            cloudShader.SetVector3("uSkyTint", atmo.SkyTint);
            cloudShader.SetFloat("uTime", timeSec);

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(false);

            var go = planet.gameObject;
            var parentWorld = TransformUtil.WorldFromTransform(go.Transform);
            var shellMf = planet.WaterGO?.Behaviors.OfType<MeshFilter>().FirstOrDefault();
            if (shellMf?.Mesh != null && planet.WaterGO != null)
            {
                var world = TransformUtil.WorldFromTransform(planet.WaterGO.Transform) * parentWorld;
                cloudShader.SetMatrix4("uModel", world);
                var gpuMesh = cache.GetMesh(shellMf.Mesh);
                gpuMesh.Draw();
            }
            else
            {
                foreach (var child in go.Children)
                {
                    if (!child.Enabled) continue;
                    if (child.Name == null || !child.Name.StartsWith("PlanetChunk_")) continue;

                    MeshFilter? mf = null;
                    foreach (var b in child.Behaviors)
                        if (b is MeshFilter f && f.Enabled) { mf = f; break; }
                    if (mf?.Mesh == null) continue;

                    var world = TransformUtil.WorldFromTransform(child.Transform) * parentWorld;
                    cloudShader.SetMatrix4("uModel", world);
                    var gpuMesh = cache.GetMesh(mf.Mesh);
                    gpuMesh.Draw();
                }
            }

            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
        }

        public static void RenderPlanetAtmosphere(
            GL gl,
            ShaderProgram atmoShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            PlanetTerrain planet,
            in PlanetAtmosphereRenderParams atmo,
            SN.Vector3 camPos,
            SN.Vector3 planetCenter)
        {
            if (!atmo.Enabled || planet.gameObject == null || !planet.IsActiveAndEnabled || planet.Config == null)
                return;

            atmoShader.Use();
            atmoShader.SetMatrix4("uView", view);
            atmoShader.SetMatrix4("uProj", proj);
            atmoShader.SetVector3("uCamPos", camPos);
            atmoShader.SetVector3("uPlanetCenter", planetCenter);
            atmoShader.SetFloat("uPlanetRadius", atmo.GroundRadius);
            atmoShader.SetFloat("uAtmosphereHeight", atmo.AtmosphereHeight);
            atmoShader.SetVector3("uSunDir", atmo.SunDir);
            atmoShader.SetFloat("uSunIntensity", atmo.SunIntensity);
            atmoShader.SetFloat("uAtmoBlend", atmo.AtmosphereBlend);
            atmoShader.SetFloat("uRayleighStrength", atmo.RayleighStrength);
            atmoShader.SetFloat("uMieStrength", atmo.MieStrength);
            atmoShader.SetFloat("uDensityFalloff", atmo.DensityFalloff);
            atmoShader.SetFloat("uHorizonBlend", atmo.HorizonBlend);
            atmoShader.SetFloat("uSunsetBoost", atmo.SunsetBoost);
            atmoShader.SetVector3("uZenithTint", atmo.ZenithTint);
            atmoShader.SetVector3("uHorizonTint", atmo.HorizonTint);

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(false);

            var go = planet.gameObject;
            var parentWorld = TransformUtil.WorldFromTransform(go.Transform);
            var shellMf = planet.WaterGO?.Behaviors.OfType<MeshFilter>().FirstOrDefault();
            if (shellMf?.Mesh != null && planet.WaterGO != null)
            {
                var world = TransformUtil.WorldFromTransform(planet.WaterGO.Transform) * parentWorld;
                atmoShader.SetMatrix4("uModel", world);
                var gpuMesh = cache.GetMesh(shellMf.Mesh);
                gpuMesh.Draw();
            }
            else
            {
                foreach (var child in go.Children)
                {
                    if (!child.Enabled) continue;
                    if (child.Name == null || !child.Name.StartsWith("PlanetChunk_")) continue;

                    MeshFilter? mf = null;
                    foreach (var b in child.Behaviors)
                        if (b is MeshFilter f && f.Enabled) { mf = f; break; }
                    if (mf?.Mesh == null) continue;

                    var world = TransformUtil.WorldFromTransform(child.Transform) * parentWorld;
                    atmoShader.SetMatrix4("uModel", world);
                    var gpuMesh = cache.GetMesh(mf.Mesh);
                    gpuMesh.Draw();
                }
            }

            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
        }
    }
}
