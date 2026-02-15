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
            Component.Water? underwaterWater = null,
            float underwaterDepth = 0f,
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
            if (underwaterWater != null)
            {
                postShader.SetInt("uUnderwaterEnabled", 1);
                postShader.SetVector3("uUnderwaterTint", underwaterWater.UnderwaterTint);
                postShader.SetFloat("uUnderwaterFogDensity", underwaterWater.UnderwaterFogDensity);
                postShader.SetFloat("uUnderwaterCausticStr", underwaterWater.UnderwaterCausticStrength);
                postShader.SetFloat("uUnderwaterDistortion", underwaterWater.UnderwaterDistortion);
                postShader.SetFloat("uUnderwaterTime", underwaterTime);
                postShader.SetFloat("uUnderwaterDepth", underwaterDepth);
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
            ShaderProgram? terrainShader = null)
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
            }
            else
            {
                standardShader.SetInt("uHasShadow", 0);
            }

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
                DrawMeshItem(gl, standardShader, cache, item);
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

                    // Bind per-terrain state only when the terrain reference changes
                    if (!ReferenceEquals(item.Terrain, boundTerrain))
                    {
                        boundTerrain = item.Terrain;
                        BindTerrainState(gl, terrainShader, cache, boundTerrain);
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
            // Fast skip for vegetation chunks whose MeshRenderer was disabled by distance culling.
            // Avoids expensive matrix computation and behavior iteration for distant grass.
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

        private static Texture2D? TryLoadLayerTexture(string path)
        {
            if (s_layerTextureCache.TryGetValue(path, out var cached))
                return cached;

            Texture2D? tex = null;
            try
            {
                string abs = path;
                var proj = ProjectService.Current;
                if (proj != null && !System.IO.Path.IsPathRooted(path))
                    abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(proj.RootPath, path));

                if (System.IO.File.Exists(abs))
                    tex = Texture2D.FromFile(abs);
            }
            catch { }

            s_layerTextureCache[path] = tex;
            return tex;
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
    }
}
