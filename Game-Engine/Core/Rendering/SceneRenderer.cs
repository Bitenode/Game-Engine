#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Silk.NET.OpenGL;
using Game_Engine.Core.Component;
using Game_Engine.Core.Planet;
using Game_Engine.Core.Rendering;
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
        private static int s_viewRenderActive;

        static SceneRenderer()
        {
            ProjectService.ProjectClosed += ClearProjectManagedCaches;
        }

        static void ClearProjectManagedCaches()
        {
            s_layerTextureCache.Clear();
            _boundBiomeTex2D = null;
            _boundBiomeUnderTex2D = null;
            _boundBiomePaths = null;
            _boundBiomeUnderPaths = null;
            _cachedTiling = null;
            _cachedUnderTiling = null;
            _cachedBaseColor = null;
            _cachedUnderColor = null;
            _cachedHasUnder = null;
            _biomeTexDirty = true;
            ClearMeshMetadata();
        }

        /// <summary>
        /// Scene View sets this while Play is running so weather particles are not drawn twice.
        /// Game View leaves it false. Trees/grass stay visible in both views.
        /// </summary>
        [ThreadStatic] public static bool SkipPlanetVegetationDraws;
        /// <summary>Scene-view play/orbit: skip the extra atmosphere + cloud planet passes.</summary>
        [ThreadStatic] public static bool SkipPlanetAtmosphereClouds;
        /// <summary>Skip drawing every planet leaf into the shadow map (orbit / play Scene view).</summary>
        [ThreadStatic] public static bool SkipPlanetLeafShadows;
        [ThreadStatic] private static bool s_suppressEnvMap;

        /// <summary>
        /// When true, forward/deferred lighting shaders output linear HDR radiance
        /// (skipping in-shader Reinhard) for single-pass Bloom + Tone Mapping in PostProcess.
        /// Defaults to false so LDR targets (cubemaps, previews, RGBA8 fallback) tone-map safely.
        /// </summary>
        public static bool HdrOutputActive { get; set; }

        /// <summary>Active rendering debug view mode (Lit, Wireframe, Albedo, Normals, etc.).</summary>
        public static RenderDebugMode DebugViewMode { get; set; } = RenderDebugMode.Lit;

        /// <summary>True when DebugViewMode outputs raw [0,1] diagnostic data that should bypass HDR tone mapping.</summary>
        public static bool IsDiagnosticDebugView =>
            DebugViewMode is RenderDebugMode.Albedo
                or RenderDebugMode.Normals
                or RenderDebugMode.Roughness
                or RenderDebugMode.Metallic
                or RenderDebugMode.Depth
                or RenderDebugMode.SSAO;

        public static bool TryBeginViewRender()
            => Interlocked.CompareExchange(ref s_viewRenderActive, 1, 0) == 0;

        public static void EndViewRender()
            => Volatile.Write(ref s_viewRenderActive, 0);

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
            public GPUFramebuffer? ShadowFBO;
            public CascadedShadowMap? CascadedShadow;
            public SN.Vector3 SunShineDir;
            public ShaderProgram StandardShader;
            public ResourceCache Cache;
            public GlobalUniforms Globals;
            public bool IsES;
            /// <summary>When true, skip custom shader detection and always use the standard shader.</summary>
            public bool ForceStandardShader;
            /// <summary>Planet vegetation forward pass: imported trees get sun/weather/wind.</summary>
            public bool PlanetFoliagePass;
        }

        static int BoneUniformLimit(bool isES) => isES ? 48 : SkeletonLimits.MaxBones;

        // ---------- Frustum culling ----------
        private struct Sphere { public SN.Vector3 Center; public float Radius; }
        private static readonly Dictionary<Mesh, Sphere> s_meshSpheres = new(1024);
        private static readonly object s_meshSphereGate = new();

        /// <summary>Periodic cleanup counter to evict orphaned mesh sphere entries.</summary>
        private static int s_sphereCleanupCounter;

        private static Sphere GetMeshSphere(Mesh m)
        {
            lock (s_meshSphereGate)
                if (s_meshSpheres.TryGetValue(m, out var cached))
                    return cached;

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

            var s = new Sphere { Center = c, Radius = (float)Math.Sqrt(r2) };
            lock (s_meshSphereGate)
            {
                s_meshSpheres[m] = s;

                // Emergency cap; normal mesh retirement removes entries immediately.
                if (++s_sphereCleanupCounter > 500 && s_meshSpheres.Count > 2048)
                {
                    s_meshSpheres.Clear();
                    s_sphereCleanupCounter = 0;
                }
            }
            return s;
        }

        public static void ReleaseMeshMetadata(Mesh mesh)
        {
            lock (s_meshSphereGate)
                s_meshSpheres.Remove(mesh);
        }

        public static void ClearMeshMetadata()
        {
            lock (s_meshSphereGate)
            {
                s_meshSpheres.Clear();
                s_sphereCleanupCounter = 0;
            }
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

        /// <summary>
        /// Pairs a <see cref="MeshFilter"/> with the next <see cref="MeshRenderer"/> on the same GameObject.
        /// Skinned meshes require <see cref="SkinnedMeshRenderer"/> so <c>uBones</c> are set; a plain
        /// <see cref="MeshRenderer"/> listed earlier (e.g. user-added or paste order) must not win.
        /// </summary>
        private static bool TryPairMeshRendererForFilter(
            IList<Behavior> behaviors,
            ref int nextMR,
            Mesh? mesh,
            out MeshRenderer? mr)
        {
            mr = null;
            int start = nextMR;

            if (mesh?.HasBones == true)
            {
                for (int j = start; j < behaviors.Count; j++)
                {
                    if (behaviors[j] is SkinnedMeshRenderer smr && smr.Enabled)
                    {
                        mr = smr;
                        nextMR = j + 1;
                        return true;
                    }
                }
            }

            for (int j = start; j < behaviors.Count; j++)
            {
                if (behaviors[j] is not MeshRenderer r || !r.Enabled)
                    continue;
                // Do not bind a SkinnedMeshRenderer to a non-skinned mesh — save it for a later MF with bones.
                if (mesh?.HasBones != true && r is SkinnedMeshRenderer)
                    continue;
                mr = r;
                nextMR = j + 1;
                return true;
            }

            return false;
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

        // ---------- Automatic Static-Mesh GPU Instancing ----------
        /// <summary>
        /// When true, repeated opaque static meshes sharing the same (Mesh, Material) with count &gt;=
        /// <see cref="AutoInstancingMinBatchSize"/> are automatically batched into a single GPU-instanced
        /// draw call using <c>Standard.vert.glsl</c> (<c>uInstanced = 2</c>, full per-instance mat4).
        /// </summary>
        public static bool EnableAutoInstancing { get; set; } = true;

        /// <summary>Minimum number of identical opaque static draw items required to form a GPU-instanced batch.</summary>
        public static int AutoInstancingMinBatchSize { get; set; } = 4;

        private readonly record struct AutoInstBatchKey(
            Mesh Mesh,
            Material? Mat,
            byte ColorR,
            byte ColorG,
            byte ColorB,
            byte ColorA,
            bool DoubleSided,
            bool InvertFrontFace);

        private sealed class AutoInstGpu
        {
            public GL Gl = null!;
            public uint Vbo;
            public int GpuCapacityFloats;
        }

        private static readonly Dictionary<object, AutoInstGpu> s_autoInstGpus = new();
        [ThreadStatic] private static Dictionary<AutoInstBatchKey, List<DrawItem>>? s_autoInstBuckets;
        [ThreadStatic] private static List<List<DrawItem>>? s_autoInstListPool;
        [ThreadStatic] private static List<AutoInstBatchKey>? s_autoInstBucketKeys;
        [ThreadStatic] private static float[]? s_autoInstMatrixScratch;

        public static void ReleaseAutoInstancingGpuFor(object gpuKey)
        {
            if (!s_autoInstGpus.Remove(gpuKey, out var gpu))
                return;
            try
            {
                if (gpu.Vbo != 0)
                    gpu.Gl.DeleteBuffer(gpu.Vbo);
            }
            catch { }
            gpu.Vbo = 0;
            gpu.GpuCapacityFloats = 0;
        }

        private static bool CanAutoInstance(in DrawItem item)
        {
            var mesh = item.MF?.Mesh;
            if (mesh == null || mesh.HasBones) return false;
            if (item.IsTransparent || item.Terrain != null || item.Skinned != null || item.Tree != null || item.Water != null)
                return false;
            var mat = item.Mat;
            if (mat != null)
            {
                if (mat.Transparent) return false;
                if (!string.IsNullOrWhiteSpace(mat.ShaderAssetPath)) return false;
            }
            return true;
        }

        private static void DrawOpaqueItemsWithAutoInstancing(
            GL gl,
            ShaderProgram shader,
            ResourceCache cache,
            List<DrawItem> opaqueItems,
            in RenderContext renderCtx,
            bool skipTerrainWithLayers,
            bool skipAllTerrain,
            bool isGBufferPass)
        {
            int minBatch = Math.Max(2, AutoInstancingMinBatchSize);
            bool useInstancing = EnableAutoInstancing && opaqueItems.Count >= minBatch;

            if (!useInstancing)
            {
                for (int i = 0; i < opaqueItems.Count; i++)
                {
                    var item = opaqueItems[i];
                    if (skipAllTerrain && item.Terrain != null) continue;
                    if (skipTerrainWithLayers && item.Terrain != null && item.Terrain.Layers.Count > 0) continue;

                    if (isGBufferPass && IsDeferredSurfaceShader(item.Mat, out var absShaderPath, out var surfaceSchema))
                    {
                        var gbufSurface = CustomShaderCache.GetOrCompile(absShaderPath!, gl, ShaderPassType.GBuffer, renderCtx.IsES);
                        if (gbufSurface != null)
                        {
                            DrawSurfaceToGBuffer(gl, gbufSurface, surfaceSchema!, cache, item, in renderCtx);
                            shader.Use();
                            continue;
                        }
                    }

                    DrawMeshItem(gl, shader, cache, item, in renderCtx);
                }
                return;
            }

            var buckets = s_autoInstBuckets ??= new Dictionary<AutoInstBatchKey, List<DrawItem>>(64);
            var pool = s_autoInstListPool ??= new List<List<DrawItem>>(64);
            var keys = s_autoInstBucketKeys ??= new List<AutoInstBatchKey>(64);
            buckets.Clear();
            keys.Clear();

            try
            {
                for (int i = 0; i < opaqueItems.Count; i++)
                {
                    var item = opaqueItems[i];
                    if (skipAllTerrain && item.Terrain != null) continue;
                    if (skipTerrainWithLayers && item.Terrain != null && item.Terrain.Layers.Count > 0) continue;

                    if (isGBufferPass && IsDeferredSurfaceShader(item.Mat, out var absShaderPath, out var surfaceSchema))
                    {
                        var gbufSurface = CustomShaderCache.GetOrCompile(absShaderPath!, gl, ShaderPassType.GBuffer, renderCtx.IsES);
                        if (gbufSurface != null)
                        {
                            DrawSurfaceToGBuffer(gl, gbufSurface, surfaceSchema!, cache, item, in renderCtx);
                            shader.Use();
                            continue;
                        }
                    }

                    if (!CanAutoInstance(in item))
                    {
                        DrawMeshItem(gl, shader, cache, item, in renderCtx);
                        continue;
                    }

                    var c = item.MR.Color;
                    var key = new AutoInstBatchKey(
                        item.MF.Mesh!,
                        item.Mat,
                        c.R, c.G, c.B, c.A,
                        item.MR.DoubleSided,
                        item.MR.InvertFrontFace);

                    if (!buckets.TryGetValue(key, out var list))
                    {
                        if (pool.Count > 0)
                        {
                            list = pool[pool.Count - 1];
                            pool.RemoveAt(pool.Count - 1);
                            list.Clear();
                        }
                        else
                        {
                            list = new List<DrawItem>(16);
                        }
                        buckets[key] = list;
                        keys.Add(key);
                    }
                    list.Add(item);
                }

                for (int k = 0; k < keys.Count; k++)
                {
                    var key = keys[k];
                    var list = buckets[key];
                    if (list.Count < minBatch)
                    {
                        for (int j = 0; j < list.Count; j++)
                            DrawMeshItem(gl, shader, cache, list[j], in renderCtx);
                    }
                    else
                    {
                        DrawAutoInstancedBatch(gl, shader, cache, key, list, in renderCtx, isGBufferPass);
                    }
                }
            }
            finally
            {
                for (int k = 0; k < keys.Count; k++)
                {
                    if (buckets.TryGetValue(keys[k], out var list))
                    {
                        list.Clear();
                        pool.Add(list);
                    }
                }
                buckets.Clear();
                keys.Clear();
            }
        }

        private static unsafe void DrawAutoInstancedBatch(
            GL gl,
            ShaderProgram shader,
            ResourceCache cache,
            in AutoInstBatchKey key,
            List<DrawItem> items,
            in RenderContext ctx,
            bool isGBufferPass)
        {
            int count = items.Count;
            if (count <= 0) return;

            var gpuMesh = cache.GetMesh(key.Mesh);
            if (gpuMesh.IndexCount <= 0) return;

            int needFloats = count * 16;
            var scratch = s_autoInstMatrixScratch ??= new float[Math.Max(needFloats, 16 * 64)];
            if (scratch.Length < needFloats)
            {
                scratch = new float[Math.Max(needFloats, scratch.Length * 2)];
                s_autoInstMatrixScratch = scratch;
            }

            for (int i = 0; i < count; i++)
            {
                var w = items[i].World;
                int o = i * 16;
                scratch[o + 0]  = w.M11; scratch[o + 1]  = w.M12; scratch[o + 2]  = w.M13; scratch[o + 3]  = w.M14;
                scratch[o + 4]  = w.M21; scratch[o + 5]  = w.M22; scratch[o + 6]  = w.M23; scratch[o + 7]  = w.M24;
                scratch[o + 8]  = w.M31; scratch[o + 9]  = w.M32; scratch[o + 10] = w.M33; scratch[o + 11] = w.M34;
                scratch[o + 12] = w.M41; scratch[o + 13] = w.M42; scratch[o + 14] = w.M43; scratch[o + 15] = w.M44;
            }

            if (!s_autoInstGpus.TryGetValue(cache, out var gpu))
            {
                gpu = new AutoInstGpu { Gl = gl, Vbo = gl.GenBuffer() };
                s_autoInstGpus[cache] = gpu;
            }

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.Vbo);
            fixed (float* ptr = scratch)
            {
                if (needFloats > gpu.GpuCapacityFloats)
                {
                    int cap = Math.Max(needFloats, Math.Max(16 * 64, gpu.GpuCapacityFloats * 3 / 2));
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), null, BufferUsageARB.DynamicDraw);
                    gpu.GpuCapacityFloats = cap;
                }
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(needFloats * sizeof(float)), ptr);
            }

            shader.Use();
            if (!isGBufferPass)
            {
                if (ctx.CascadedShadow != null || ctx.ShadowFBO?.DepthTexture != null)
                    BindShadowMapsToShader(shader, ctx.ShadowFBO, ctx.CascadedShadow, 7);
                BindEnvMapToShader(gl, shader, ctx.CamPos, 12);
            }

            var mat = key.Mat;
            float r = 1f, g2 = 1f, b = 1f, a = 1f;
            float roughness = 0.5f, metallic = 0f, alphaCutoff = 0f;
            if (mat != null)
            {
                r = mat.BaseColor.R / 255f;
                g2 = mat.BaseColor.G / 255f;
                b = mat.BaseColor.B / 255f;
                a = mat.BaseColor.A / 255f;
                roughness = mat.Roughness;
                metallic = mat.Metallic;
                alphaCutoff = mat.AlphaCutoff;
            }

            r *= key.ColorR / 255f;
            g2 *= key.ColorG / 255f;
            b *= key.ColorB / 255f;
            a *= key.ColorA / 255f;

            shader.SetInt("uInstanced", 2);
            shader.SetInt("uHasBones", 0);
            shader.SetVector4("uBaseColor", r, g2, b, a);
            shader.SetFloat("uRoughness", roughness);
            shader.SetFloat("uMetallic", metallic);
            shader.SetFloat("uAlphaCutoff", alphaCutoff);
            shader.SetInt("uTransparent", 0);
            shader.SetInt("uDoubleSided", key.DoubleSided ? 1 : 0);

            if (key.DoubleSided)
                gl.Disable(EnableCap.CullFace);
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(key.InvertFrontFace ? TriangleFace.Front : TriangleFace.Back);
            }

            ctx.Globals.UpdateAmbientIfChanged(ctx.Ambient);
            shader.SetInt("uIsVegetation", 0);
            shader.SetFloat("uRain", 0f);
            shader.SetFloat("uWetness", 0f);
            shader.SetFloat("uSnow", 0f);
            shader.SetFloat("uCloudiness", 0f);
            shader.SetFloat("uSunIntensity", 1f);
            if (!isGBufferPass)
                SetPlanetPropFill(shader, ctx.CamPos);

            const int opacityTexUnit = 11;
            bool hasAlbedo = false, hasNormal = false, hasSpec = false, hasMetal = false;
            bool hasRough = false, hasAO = false, hasEmissiveTex = false, hasOpacity = false;

            if (mat?.Textures != null && mat.Textures.Count > 0)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts && rts.Texture != null)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (!hasAlbedo && (usage.Contains("albedo") || usage == "" || usage.Contains("base") || usage.Contains("diff")))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture0); hasAlbedo = true; }
                        else if (!hasNormal && (usage.Contains("normal") || usage.Contains("bump")))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture1); hasNormal = true; }
                        else if (!hasSpec && usage.Contains("specular"))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture2); hasSpec = true; }
                        else if (!hasMetal && (usage.Contains("metallic") || usage.Contains("metalness")))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture3); hasMetal = true; }
                        else if (!hasRough && usage.Contains("rough"))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture4); hasRough = true; }
                        else if (!hasAO && (usage.Contains("occlusion") || usage.Contains("ao")))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture5); hasAO = true; }
                        else if (!hasEmissiveTex && (usage.Contains("emissive") || usage.Contains("emission")))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture6); hasEmissiveTex = true; }
                        else if (!hasOpacity && (usage.Contains("opacity") || usage.Contains("transparency") || usage == "mask"))
                        { cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture0 + opacityTexUnit); hasOpacity = true; }
                    }
                    else if (slot is MaterialTexture mtex && mtex.Texture != null)
                    {
                        switch (mtex.Usage)
                        {
                            case MaterialTexture.TexUsage.Albedo when !hasAlbedo:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture0); hasAlbedo = true; break;
                            case MaterialTexture.TexUsage.Normal when !hasNormal:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture1); hasNormal = true; break;
                            case MaterialTexture.TexUsage.Specular when !hasSpec:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture2); hasSpec = true; break;
                            case MaterialTexture.TexUsage.Metallic when !hasMetal:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture3); hasMetal = true; break;
                            case MaterialTexture.TexUsage.Roughness when !hasRough:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture4); hasRough = true; break;
                            case MaterialTexture.TexUsage.AmbientOcclusion when !hasAO:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture5); hasAO = true; break;
                            case MaterialTexture.TexUsage.Emissive when !hasEmissiveTex:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture6); hasEmissiveTex = true; break;
                            case MaterialTexture.TexUsage.Opacity when !hasOpacity:
                                cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture0 + opacityTexUnit); hasOpacity = true; break;
                        }
                    }
                }
            }

            if (!hasAlbedo)
                cache.GetWhiteTexture().Bind(TextureUnit.Texture0);
            if (!hasOpacity)
                cache.GetWhiteTexture().Bind(TextureUnit.Texture0 + opacityTexUnit);

            shader.SetTexture("uAlbedoTex", 0);
            shader.SetInt("uHasAlbedoTex", hasAlbedo ? 1 : 0);
            shader.SetTexture("uNormalMap", 1);
            shader.SetInt("uHasNormalMap", hasNormal ? 1 : 0);
            shader.SetFloat("uNormalStrength", mat?.NormalStrength ?? 1f);
            shader.SetTexture("uSpecularTex", 2);
            shader.SetInt("uHasSpecularTex", hasSpec ? 1 : 0);
            shader.SetTexture("uMetallicTex", 3);
            shader.SetInt("uHasMetallicTex", hasMetal ? 1 : 0);
            shader.SetTexture("uRoughnessTex", 4);
            shader.SetInt("uHasRoughnessTex", hasRough ? 1 : 0);
            shader.SetTexture("uAOTex", 5);
            shader.SetInt("uHasAOTex", hasAO ? 1 : 0);
            shader.SetTexture("uEmissiveTex", 6);
            shader.SetInt("uHasEmissiveTex", hasEmissiveTex ? 1 : 0);
            shader.SetTexture("uOpacityTex", opacityTexUnit);
            shader.SetInt("uHasOpacityTex", hasOpacity ? 1 : 0);
            shader.SetFloat("uLumaClip", mat?.LumaClip ?? 0f);

            if (mat != null && (mat.EmissiveIntensity > 0f || hasEmissiveTex))
            {
                shader.SetVector3("uEmissiveColor", new SN.Vector3(
                    mat.EmissiveColor.R / 255f,
                    mat.EmissiveColor.G / 255f,
                    mat.EmissiveColor.B / 255f));
                shader.SetFloat("uEmissiveIntensity", mat.EmissiveIntensity > 0f ? mat.EmissiveIntensity : 1f);
            }
            else
            {
                shader.SetVector3("uEmissiveColor", SN.Vector3.Zero);
                shader.SetFloat("uEmissiveIntensity", 0f);
            }

            try
            {
                gpuMesh.DrawInstancedMatrices(gpu.Vbo, count, 64);
                Profiler.CountBatch();
            }
            finally
            {
                shader.SetInt("uInstanced", 0);
            }
        }

        // ---------- Particle rendering ----------

        /// <summary>
        /// Render all active particle emitters as GPU-instanced billboard quads.
        /// Call after main scene rendering, before post-processing.
        /// </summary>
        public static void RenderParticles(
            GL gl,
            ShaderProgram particleShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            int viewportW = 0,
            int viewportH = 0,
            bool overlayPass = false)
        {
            if (viewportW > 0 && viewportH > 0)
                gl.Viewport(0, 0, (uint)viewportW, (uint)viewportH);
            gl.Disable(EnableCap.ScissorTest);
            if (overlayPass)
                gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            var batcher = cache.GetParticleBatcher();

            var globals = GlobalUniforms.ForContext(gl);
            var camPos = SN.Matrix4x4.Invert(view, out var invV) ? invV.Translation : SN.Vector3.Zero;
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, viewportW, viewportH);

            // Find all particle emitters
            foreach (var root in SceneService.Root)
                RenderParticlesRecursive(gl, particleShader, cache, batcher, root, overlayPass);

            if (overlayPass)
                gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
        }

        private static bool TryBindParticleTexture(GameObject go, ResourceCache cache)
        {
            for (int bi = 0; bi < go.Behaviors.Count; bi++)
            {
                if (go.Behaviors[bi] is MeshRenderer mr && mr.Material?.Textures != null)
                {
                    var textures = mr.Material.Textures;
                    for (int i = 0; i < textures.Count; i++)
                    {
                        var slot = textures[i];
                        if (slot is RuntimeTexSlot rts && rts.Texture != null)
                        {
                            var usage = rts.Usage?.ToLowerInvariant() ?? "";
                            if (usage.Contains("albedo") || usage == "" || usage.Contains("base") || usage.Contains("diff"))
                            {
                                cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture0);
                                return true;
                            }
                        }
                        else if (slot is MaterialTexture mtex && mtex.Texture != null && mtex.Usage == MaterialTexture.TexUsage.Albedo)
                        {
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture0);
                            return true;
                        }
                    }
                    break;
                }
            }
            cache.GetWhiteTexture().Bind(TextureUnit.Texture0);
            return false;
        }

        private static void RenderParticlesRecursive(
            GL gl, ShaderProgram shader, ResourceCache cache, GPUParticleBatcher batcher,
            GameObject go, bool overlayPass)
        {
            if (!go.Enabled) return;

            if (SkipPlanetVegetationDraws && IsPlanetVegetationDrawName(go.Name))
                return;

            foreach (var b in go.Behaviors)
            {
                if (b is ParticleEmitter pe && pe.IsActiveAndEnabled && pe.ActiveParticleCount > 0)
                {
                    int activeCount = pe.ActiveParticleCount;
                    float[] staging = batcher.EnsureStagingCapacity(activeCount);
                    int count = pe.FillInstancedData(staging, activeCount);
                    if (count <= 0) continue;

                    shader.Use();
                    int particleLook = pe.Look == ParticleLook.Flame ? 2
                        : pe.Look == ParticleLook.Streak || pe.StretchAlongVelocity ? 1
                        : 0;
                    shader.SetInt("uParticleLook", particleLook);
                    shader.SetInt("uAlignVelocity", particleLook == 1 ? 1 : 0);
                    shader.SetFloat("uStretchLength", Math.Max(0.08f, pe.StretchLength));
                    var fall = particleLook == 2 && pe.EmissionDirection.LengthSquared() > 1e-6f
                        ? pe.EmissionDirection
                        : pe.GetRenderFallDirection();
                    shader.SetVector3("uFallDir", fall);

                    bool hasTex = TryBindParticleTexture(go, cache);
                    shader.SetTexture("uParticleTex", 0);
                    shader.SetInt("uHasTexture", hasTex ? 1 : 0);

                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(
                        BlendingFactor.SrcAlpha,
                        particleLook == 2 ? BlendingFactor.One : BlendingFactor.OneMinusSrcAlpha);
                    gl.DepthMask(false);
                    bool weatherPrecip = go.Name?.StartsWith("BiomeWeatherPrecipitation", StringComparison.Ordinal) == true;
                    if (weatherPrecip || overlayPass)
                        gl.Disable(EnableCap.DepthTest);

                    batcher.UploadAndDraw(count);

                    if (weatherPrecip || overlayPass)
                        gl.Enable(EnableCap.DepthTest);
                    gl.DepthMask(true);
                    gl.Disable(EnableCap.Blend);
                }
            }

            foreach (var c in go.Children)
                RenderParticlesRecursive(gl, shader, cache, batcher, c, overlayPass);
        }

        static bool IsPlanetVegetationDrawName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("BiomeWeatherPrecipitation", StringComparison.Ordinal)
                || name.StartsWith("BiomeGrass", StringComparison.Ordinal)
                || name.StartsWith("BiomeTree", StringComparison.Ordinal)
                || name.StartsWith("AssetGrass", StringComparison.Ordinal)
                || name.StartsWith("AssetTree", StringComparison.Ordinal)
                || name.StartsWith("LocalTree_", StringComparison.Ordinal)
                || name.StartsWith("planet_grass_", StringComparison.Ordinal)
                || name == PlanetVegetationSystem.RuntimeRootName;
        }

        /// <summary>Release thread-local renderer objects before a GL context is destroyed.</summary>
        public static void DisposeStaticGlResources(GL gl)
        {
            // Particle billboard VAO/VBOs are now owned per-context by ResourceCache (GPUParticleBatcher)
            // and disposed via ResourceCache.Dispose() / ResourceCache.FlushAll().
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
            var globals = GlobalUniforms.ForContext(gl);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time);
            globals.UpdateLighting(
                lightDir, diffuseK, SN.Vector3.One, ambient,
                lightIsPoint: false, SN.Vector3.Zero, 0f,
                hasShadow: false, SN.Matrix4x4.Identity);

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

                    // Sky reflection color (lighting + camera come from FrameGlobals/LightingGlobals UBOs)
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
        /// <summary>
        /// Renders a fullscreen quad with the post-processing shader (blended <see cref="PostProcessSettings"/> + real scene depth).
        /// </summary>
        public static void ApplyPostProcessing(
            GL gl,
            ShaderProgram postShader,
            GPUTexture sceneTexture,
            int viewportWidth,
            int viewportHeight,
            PostProcessSettings? settings = null,
            UnderwaterState? underwater = null,
            float underwaterTime = 0f,
            GPUTexture? depthTexture = null,
            bool hdrInput = false)
        {
            // Always bind the post-process shader, even for passthrough —
            // rendering without a shader causes a black screen.
            postShader.Use();
            gl.GetInteger(GetPName.DrawFramebufferBinding, out int presentFb);
            GPUFramebuffer.DrawColorTo(gl, (uint)presentFb);
            gl.ColorMask(true, true, true, true);

            sceneTexture.Bind(TextureUnit.Texture0);
            postShader.SetTexture("uScene", 0);
            postShader.SetVector2("uTexelSize", 1f / viewportWidth, 1f / viewportHeight);

            if (depthTexture != null)
            {
                depthTexture.Bind(TextureUnit.Texture1);
                postShader.SetTexture("uDepthTex", 1);
                postShader.SetInt("uHasDepthTex", 1);
            }
            else
            {
                postShader.SetInt("uHasDepthTex", 0);
            }

            bool toneMapCompare = DebugViewMode == RenderDebugMode.ToneMapCompare;
            postShader.SetInt("uToneMapCompare", toneMapCompare ? 1 : 0);

            bool needDefaultHdrToneMap = (hdrInput || toneMapCompare) && !IsDiagnosticDebugView;

            if (settings == null)
            {
                // Pass-through for LDR, or single-pass ACES tone map for linear HDR SceneFbo
                postShader.SetInt("uBloomEnabled", 0);
                postShader.SetInt("uFogEnabled", 0);
                postShader.SetInt("uFogHeightBased", 0);
                postShader.SetFloat("uFogHeightFalloff", 0.1f);
                postShader.SetInt("uColorGradingEnabled", needDefaultHdrToneMap ? 1 : 0);
                postShader.SetInt("uVignetteEnabled", 0);
                postShader.SetInt("uFXAAEnabled", 0);
                postShader.SetFloat("uExposure", 1f);
                postShader.SetFloat("uContrast", 1f);
                postShader.SetFloat("uSaturation", 1f);
                postShader.SetFloat("uBrightness", 0f);
                postShader.SetInt("uToneMap", needDefaultHdrToneMap ? (int)ToneMapping.ACES : 0);
                postShader.SetFloat("uBloomThreshold", 1f);
                postShader.SetFloat("uBloomIntensity", 0f);
            }
            else
            {
                // Bloom
                postShader.SetInt("uBloomEnabled", settings.BloomEnabled ? 1 : 0);
                postShader.SetFloat("uBloomThreshold", settings.BloomThreshold);
                postShader.SetFloat("uBloomIntensity", settings.BloomIntensity);

                // Fog (real depth & height fog)
                postShader.SetInt("uFogEnabled", settings.FogEnabled ? 1 : 0);
                postShader.SetVector3("uFogColor", settings.FogColor);
                postShader.SetFloat("uFogDensity", settings.FogDensity);
                postShader.SetFloat("uFogStart", settings.FogStart);
                postShader.SetFloat("uFogEnd", settings.FogEnd);
                postShader.SetInt("uFogHeightBased", settings.FogHeightBased ? 1 : 0);
                postShader.SetFloat("uFogHeightFalloff", settings.FogHeightFalloff);

                // Color Grading (guarantee single-pass tone mapping when presenting linear HDR)
                if (settings.ColorGradingEnabled)
                {
                    postShader.SetInt("uColorGradingEnabled", 1);
                    postShader.SetFloat("uBrightness", settings.Brightness);
                    postShader.SetFloat("uContrast", settings.Contrast);
                    postShader.SetFloat("uSaturation", settings.Saturation);
                    postShader.SetFloat("uExposure", settings.Exposure);
                    postShader.SetInt("uToneMap", (int)settings.ToneMap);
                }
                else if (needDefaultHdrToneMap)
                {
                    postShader.SetInt("uColorGradingEnabled", 1);
                    postShader.SetFloat("uBrightness", 0f);
                    postShader.SetFloat("uContrast", 1f);
                    postShader.SetFloat("uSaturation", 1f);
                    postShader.SetFloat("uExposure", 1f);
                    postShader.SetInt("uToneMap", (int)ToneMapping.ACES);
                }
                else
                {
                    postShader.SetInt("uColorGradingEnabled", 0);
                    postShader.SetFloat("uBrightness", 0f);
                    postShader.SetFloat("uContrast", 1f);
                    postShader.SetFloat("uSaturation", 1f);
                    postShader.SetFloat("uExposure", 1f);
                    postShader.SetInt("uToneMap", 0);
                }

                // Vignette
                postShader.SetInt("uVignetteEnabled", settings.VignetteEnabled ? 1 : 0);
                postShader.SetFloat("uVignetteIntensity", settings.VignetteIntensity);
                postShader.SetFloat("uVignetteSmoothness", settings.VignetteSmoothness);

                // FXAA
                postShader.SetInt("uFXAAEnabled", settings.FXAAEnabled ? 1 : 0);
            }

            // Underwater. Land weather fog must not run in this pass — it tints
            // and wobbles like the underwater shader when rain is on.
            if (underwater.HasValue)
            {
                var uw = underwater.Value;
                postShader.SetInt("uFogEnabled", 0);
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

            // Draw fullscreen triangle (covers entire screen with a single triangle).
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>Backward-compatible overload accepting a <see cref="PostProcessVolume"/> directly.</summary>
        public static void ApplyPostProcessing(
            GL gl,
            ShaderProgram postShader,
            GPUTexture sceneTexture,
            int viewportWidth,
            int viewportHeight,
            PostProcessVolume? volume,
            UnderwaterState? underwater = null,
            float underwaterTime = 0f,
            GPUTexture? depthTexture = null)
        {
            ApplyPostProcessing(gl, postShader, sceneTexture, viewportWidth, viewportHeight,
                volume != null ? PostProcessSettings.FromVolume(volume) : null,
                underwater, underwaterTime, depthTexture);
        }

        /// <summary>
        /// 2-pass separable Depth-of-Field post-processing pass (horizontal -> vertical).
        /// Uses unit 0 (<c>uSceneColor</c>) and unit 1 (<c>uDepthTex</c>).
        /// </summary>
        public static GPUTexture RenderDepthOfField(
            GL gl,
            ShaderProgram dofShader,
            FullscreenQuad fsQuad,
            GPUTexture sceneColor,
            GPUTexture depthTex,
            GPUFramebuffer tempFbo,
            GPUFramebuffer outFbo,
            int width,
            int height,
            PostProcessSettings settings,
            float nearPlane,
            float farPlane)
        {
            dofShader.Use();
            dofShader.SetFloat("uFocusDistance", MathF.Max(settings.DoFFocusDistance, 0.1f));
            dofShader.SetFloat("uAperture", MathF.Max(settings.DoFAperture, 0.5f));
            dofShader.SetFloat("uFocalLength", MathF.Max(settings.DoFFocalLength, 1f));
            dofShader.SetFloat("uMaxBlurRadius", Math.Clamp(settings.DoFMaxBlurRadius, 1f, 16f));
            dofShader.SetFloat("uNearBlurScale", MathF.Max(settings.DoFNearBlurScale, 0f));
            dofShader.SetFloat("uFarBlurScale", MathF.Max(settings.DoFFarBlurScale, 0f));
            dofShader.SetVector2("uTexelSize", 1f / Math.Max(1, width), 1f / Math.Max(1, height));
            dofShader.SetFloat("uNear", MathF.Max(nearPlane, 0.01f));
            dofShader.SetFloat("uFar", MathF.Max(farPlane, nearPlane + 1f));

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.CullFace);
            gl.ColorMask(true, true, true, true);

            // Pass 0: horizontal blur -> tempFbo
            tempFbo.Bind();
            sceneColor.Bind(TextureUnit.Texture0);
            dofShader.SetTexture("uSceneColor", 0);
            depthTex.Bind(TextureUnit.Texture1);
            dofShader.SetTexture("uDepthTex", 1);
            dofShader.SetInt("uPass", 0);
            fsQuad.Draw();

            // Pass 1: vertical blur -> outFbo
            outFbo.Bind();
            tempFbo.ColorTexture!.Bind(TextureUnit.Texture0);
            dofShader.SetTexture("uSceneColor", 0);
            depthTex.Bind(TextureUnit.Texture1);
            dofShader.SetTexture("uDepthTex", 1);
            dofShader.SetInt("uPass", 1);
            fsQuad.Draw();

            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
            return outFbo.ColorTexture!;
        }

        // ---------- GPU RENDER ENTRY POINT ----------

        /// <summary>
        /// Render the entire scene using GPU draw calls.
        /// Must be called within an active GL context.
        /// </summary>
        // NOTE: Splatmap textures are now managed per-context via ResourceCache.GetTerrainSplatTextures()
        // to avoid cross-GL-context issues between SceneView and GameView.

        // Static string arrays for terrain shader uniforms (avoid per-draw-call allocation)
        private static readonly string[] s_tilingNames = { "uTiling0", "uTiling1", "uTiling2", "uTiling3", "uTiling4", "uTiling5", "uTiling6", "uTiling7" };
        private static readonly string[] s_roughNames = { "uRough0", "uRough1", "uRough2", "uRough3", "uRough4", "uRough5", "uRough6", "uRough7" };
        private static readonly string[] s_metalNames = { "uMetal0", "uMetal1", "uMetal2", "uMetal3", "uMetal4", "uMetal5", "uMetal6", "uMetal7" };
        private static readonly string[] s_hasNormalNames = { "uHasNormal0", "uHasNormal1", "uHasNormal2", "uHasNormal3", "uHasNormal4", "uHasNormal5", "uHasNormal6", "uHasNormal7" };

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
            SN.Vector3 lightColor = default,
            CascadedShadowMap? cascadedShadow = null)
        {
            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            // Reuse draw-item lists to avoid GC pressure
            var opaqueItems = s_opaqueItems ??= new List<DrawItem>(256);
            var transparentItems = s_transparentItems ??= new List<DrawItem>(64);
            opaqueItems.Clear();
            transparentItems.Clear();

            bool skipPlanetVegInSceneGraph = PlanetVegetationSystem.AnyUseDedicatedRenderPass;
            var prevSkip = SkipPlanetVegetationDraws;
            SkipPlanetVegetationDraws = skipPlanetVegInSceneGraph || prevSkip;

            foreach (var root in SceneService.Root)
            {
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);
            }

            SkipPlanetVegetationDraws = prevSkip;

            // --- OPAQUE PASS ---
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            // Upload FrameGlobals & LightingGlobals UBOs once for this pass
            var globals = GlobalUniforms.ForContext(gl);
            var resolvedLightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor;
            bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;
            var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                ? SN.Vector3.Normalize(WindSystem.Direction)
                : SN.Vector3.UnitX;

            GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
            globals.UpdateLighting(
                lightDir, diffuseK, resolvedLightColor, ambient,
                lightIsPoint, lightPosW, lightRange,
                hasShadow, in shadowVP,
                cascadeCount: hasShadow ? 1 : 0,
                cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
                windDir: windDir,
                windTime: WindSystem.Time);
            BindNearestPointLights(globals, camPos);

            standardShader.Use();
            standardShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
            standardShader.SetInt("uDebugViewMode", (int)DebugViewMode);

            if (hasShadow)
            {
                standardShader.SetFloat("uShadowBias", 0.008f);
                standardShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
            }
            BindEnvMapToShader(gl, standardShader, camPos, 12);

            bool desktopWireframe = DebugViewMode == RenderDebugMode.Wireframe && !isES;
            if (desktopWireframe)
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            try
            {

            // Build render context for custom shader support
            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = lightDir,
                LightColor = resolvedLightColor,
                DiffuseK = diffuseK,
                Ambient = ambient,
                ShadowVP = shadowVP,
                ShadowFBO = shadowFBO,
                CascadedShadow = cascadedShadow,
                SunShineDir = sunShineDir,
                StandardShader = standardShader,
                Cache = cache,
                Globals = globals,
                IsES = isES
            };

            // --- BATCH terrain draw items by Terrain reference ---
            // This avoids rebinding splatmap + layer textures for every single chunk.
            // We first draw all non-terrain opaque items (with automatic static-mesh GPU instancing),
            // then group terrain items by terrain.
            Terrain? boundTerrain = null;

            DrawOpaqueItemsWithAutoInstancing(
                gl, standardShader, cache, opaqueItems, in renderCtx,
                skipTerrainWithLayers: terrainShader != null,
                skipAllTerrain: false,
                isGBufferPass: false);

            // Now draw ALL terrain items, grouped by terrain to minimize state changes
            if (terrainShader != null)
            {
                // Restore scene ambient in case last non-terrain item was foliage
                globals.UpdateAmbientIfChanged(ambient);
                terrainShader.Use();
                terrainShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
                terrainShader.SetFloat("uParallaxStrength", 0.035f);
                terrainShader.SetFloat("uShadowBias", 0.008f);
                terrainShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(
                    terrainShader,
                    hasShadow ? shadowFBO : null,
                    hasShadow ? cascadedShadow : null,
                    7,
                    cache);

                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Back);

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

            // Planet vegetation is drawn after planet terrain (see RenderPlanetVegetationAfterTerrain).

            // --- TRANSPARENT PASS (back-to-front) ---
            if (transparentItems.Count > 0)
            {
                transparentItems.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);
                // Glass is a second surface in the wall's plane. ANGLE's depth buffer is coarser than
                // desktop GL, and Less rejects that layer so the windows stay empty.
                gl.DepthFunc(DepthFunction.Lequal);
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(-2f, -2f);

                // Ensure standard shader is active for transparent pass
                standardShader.Use();
                if (hasShadow)
                    BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
                BindEnvMapToShader(gl, standardShader, camPos, 12);

                foreach (var item in transparentItems)
                {
                    DrawMeshItem(gl, standardShader, cache, item, in renderCtx);
                }

                gl.DepthFunc(DepthFunction.Less);
                gl.Disable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(0f, 0f);
                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
            }
            }
            finally
            {
                if (desktopWireframe)
                    gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
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
            Profiler.ShadowPassDepth++;
            try
            {
            RenderShadowPassCore(gl, depthShader, cache, in lightVP);
            }
            finally
            {
                Profiler.ShadowPassDepth = Math.Max(0, Profiler.ShadowPassDepth - 1);
            }
        }

        static void RenderShadowPassCore(
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

            if (PlanetVegetationSystem.AnyUseDedicatedRenderPass)
            {
                var systems = PlanetVegetationSystem.ActiveSystems;
                bool prevSkip = SkipPlanetVegetationDraws;
                SkipPlanetVegetationDraws = false;
                try
                {
                    for (int s = 0; s < systems.Count; s++)
                    {
                        var sys = systems[s];
                        if (sys == null || !sys.IsActiveAndEnabled || !sys.UseDedicatedRenderPass)
                            continue;
                        var root = sys.RuntimeDrawRoot;
                        if (root == null || sys.TerrainGameObject == null)
                            continue;
                        var planetWorld = TransformUtil.WorldFromTransform(sys.TerrainGameObject.Transform);
                        RenderShadowNode(gl, depthShader, cache, root, planetWorld, lightVP, planes);
                    }
                }
                finally
                {
                    SkipPlanetVegetationDraws = prevSkip;
                }
            }

            RenderPlanetLeafShadows(gl, depthShader, cache, lightVP, planes);

            // Restore default back-face culling after shadow pass
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
        }

        /// <summary>
        /// Draw GPU-cached planet leaves into the existing camera-centered shadow map.
        /// Leaves are not scene nodes, so they would otherwise never occlude cave mouths or crater rims.
        /// </summary>
        private static void RenderPlanetLeafShadows(
            GL gl,
            ShaderProgram depthShader,
            ResourceCache cache,
            in SN.Matrix4x4 lightVP,
            Plane[] planes)
        {
            var planets = PlanetTerrain.ActivePlanets;
            int planetCount = planets.Count;
            if (planetCount == 0 || SkipPlanetLeafShadows) return;

            depthShader.SetInt("uHasBones", 0);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            for (int p = 0; p < planetCount; p++)
            {
                var planet = planets[p];
                if (planet == null || !planet.IsActiveAndEnabled || planet.gameObject == null)
                    continue;

                var leaves = planet.ChunkManager?.GetRenderableLeaves();
                if (leaves == null || leaves.Count == 0)
                    continue;

                var planetWorld = TransformUtil.WorldFromTransform(planet.gameObject.Transform);

                for (int i = 0; i < leaves.Count; i++)
                {
                    var mesh = leaves[i].GeneratedMesh;
                    if (mesh == null) continue;

                    var sph = GetMeshSphere(mesh);
                    if (!SphereInsideFrustum(ref sph, planetWorld, planes)) continue;

                    var gpuMesh = cache.GetMesh(mesh);
                    depthShader.SetMatrix4("uMVP", planetWorld * lightVP);
                    gpuMesh.Draw();

                    var cave = leaves[i].GeneratedCaveMesh;
                    if (cave != null && planet.Config is { CameraBelowCrust: true })
                        cache.GetMesh(cave).Draw();
                }
            }
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

        private static readonly string[] s_cascadeSamplerNames = { "uShadowMap", "uShadowMapC1", "uShadowMapC2", "uShadowMapC3" };

        /// <summary>
        /// Bind cascaded shadow map textures and upload cascade matrices/splits to <c>LightingGlobals</c>.
        /// </summary>
        public static void BindCascadeShadowUniforms(
            ShaderProgram shader,
            CascadedShadowMap csm,
            int baseTextureUnit = 7)
        {
            int count = Math.Clamp(csm.CascadeCount, 1, 4);

            // Bind cascade shadow maps to texture units
            for (int i = 0; i < count; i++)
            {
                int unit = baseTextureUnit + i;
                csm.Cascades[i].FBO.DepthTexture?.Bind(TextureUnit.Texture0 + unit);
                shader.SetTexture(s_cascadeSamplerNames[i], unit);
            }
            // Assign any unused cascade samplers to the last valid cascade unit so none stay on unit 0
            // (prevents sampler2D vs sampler2DArray collisions on unit 0 under OpenGL ES / ANGLE).
            int fallbackUnit = baseTextureUnit + count - 1;
            for (int i = count; i < 4; i++)
                shader.SetTexture(s_cascadeSamplerNames[i], fallbackUnit);

            // Upload per-cascade VP matrices and split distances into LightingGlobals UBO
            var vp0 = csm.LightVPs[0];
            var vp1 = count > 1 ? csm.LightVPs[1] : vp0;
            var vp2 = count > 2 ? csm.LightVPs[2] : vp1;
            var vp3 = count > 3 ? csm.LightVPs[3] : vp2;
            var splits = new SN.Vector4(
                csm.SplitDistances[0],
                count > 1 ? csm.SplitDistances[1] : 1000f,
                count > 2 ? csm.SplitDistances[2] : 1000f,
                count > 3 ? csm.SplitDistances[3] : 1000f);

            GlobalUniforms.ForContext(shader.GL).UpdateCascadedShadows(
                count, in vp0, in vp1, in vp2, in vp3, splits);
        }

        /// <summary>
        /// Bind either all 4 CSM cascades (units 7..10) or a single shadow map (unit 7) to <paramref name="shader"/>.
        /// Always assigns <c>uShadowMap</c> and <c>uShadowMapC1..C3</c> to <paramref name="baseUnit"/>..+3 so no
        /// <c>sampler2D</c> shadow uniform can remain at default unit 0 (which ANGLE rejects if unit 0 holds a <c>sampler2DArray</c>).
        /// </summary>
        internal static void BindShadowMapsToShader(
            ShaderProgram shader,
            GPUFramebuffer? shadowFBO,
            CascadedShadowMap? csm,
            int baseUnit = 7,
            ResourceCache? fallbackCache = null)
        {
            if (csm != null)
            {
                BindCascadeShadowUniforms(shader, csm, baseUnit);
            }
            else if (shadowFBO?.DepthTexture != null)
            {
                shadowFBO.DepthTexture.Bind(TextureUnit.Texture0 + baseUnit);
                shader.SetTexture("uShadowMap", baseUnit);
                shader.SetTexture("uShadowMapC1", baseUnit);
                shader.SetTexture("uShadowMapC2", baseUnit);
                shader.SetTexture("uShadowMapC3", baseUnit);
            }
            else
            {
                fallbackCache?.GetWhiteTexture().Bind(TextureUnit.Texture0 + baseUnit);
                shader.SetTexture("uShadowMap", baseUnit);
                shader.SetTexture("uShadowMapC1", baseUnit);
                shader.SetTexture("uShadowMapC2", baseUnit);
                shader.SetTexture("uShadowMapC3", baseUnit);
            }
        }

        /// <summary>
        /// Bind the nearest captured <see cref="ReflectionProbe"/> cubemap (<c>uEnvMap</c>) to
        /// <paramref name="textureUnit"/>, or disable env-map sampling if none is captured or capture is in progress.
        /// </summary>
        public static void BindEnvMapToShader(
            GL gl,
            ShaderProgram shader,
            SN.Vector3 worldPos,
            int textureUnit,
            GPUTexture? explicitCubemap = null,
            float explicitIntensity = 0f)
        {
            // Always assign uEnvMap to its dedicated texture unit (even when uHasEnvMap == 0).
            // OpenGL / GLES 3.0 (strictly enforced by ANGLE) rejects draw calls with GL_INVALID_OPERATION
            // if a samplerCube (defaulting to unit 0) shares a texture unit with a sampler2D (uAlbedoTex on unit 0).
            shader.SetTexture("uEnvMap", textureUnit);

            if (s_suppressEnvMap)
            {
                shader.SetInt("uHasEnvMap", 0);
                shader.SetFloat("uEnvIntensity", 0f);
                shader.SetFloat("uEnvMaxMip", 0f);
                return;
            }

            GPUTexture? cube = explicitCubemap;
            float intensity = explicitIntensity;
            if (cube == null)
            {
                var probe = ReflectionProbe.GetBestForPosition(worldPos, gl);
                var probeCube = probe?.GetCubemap(gl);
                if (probe != null && probeCube != null && probe.HasCapturedFor(gl) && probe.Intensity > 0.001f)
                {
                    cube = probeCube;
                    intensity = probe.Intensity;
                }
            }

            if (cube != null && intensity > 0.001f)
            {
                cube.BindCubemap(TextureUnit.Texture0 + textureUnit);
                shader.SetInt("uHasEnvMap", 1);
                shader.SetFloat("uEnvIntensity", Math.Clamp(intensity, 0f, 3f));
                int dim = Math.Max(1, cube.Width);
                float maxMip = MathF.Max(0f, MathF.Floor(MathF.Log2(dim)));
                shader.SetFloat("uEnvMaxMip", maxMip);
            }
            else
            {
                shader.SetInt("uHasEnvMap", 0);
                shader.SetFloat("uEnvIntensity", 0f);
                shader.SetFloat("uEnvMaxMip", 0f);
            }
        }

        // OpenGL cubemap face directions and exact up vectors (+X, -X, +Y, -Y, +Z, -Z)
        private static readonly SN.Vector3[] s_cubeFaceDirs =
        {
            new SN.Vector3( 1f,  0f,  0f),
            new SN.Vector3(-1f,  0f,  0f),
            new SN.Vector3( 0f,  1f,  0f),
            new SN.Vector3( 0f, -1f,  0f),
            new SN.Vector3( 0f,  0f,  1f),
            new SN.Vector3( 0f,  0f, -1f),
        };

        private static readonly SN.Vector3[] s_cubeFaceUps =
        {
            new SN.Vector3(0f, -1f,  0f),
            new SN.Vector3(0f, -1f,  0f),
            new SN.Vector3(0f,  0f,  1f),
            new SN.Vector3(0f,  0f, -1f),
            new SN.Vector3(0f, -1f,  0f),
            new SN.Vector3(0f, -1f,  0f),
        };

        /// <summary>
        /// Capture up to 1 pending <see cref="ReflectionProbe"/> cubemap per frame (6 faces, 90° FOV,
        /// Sky + forward opaque scene + terrain + planets) with <c>uHasEnvMap = 0</c> to avoid feedback,
        /// then generate mipmaps and cleanly restore previous FBO, viewport, and <c>FrameGlobals</c> UBO.
        /// </summary>
        public static unsafe void CaptureReflectionProbes(
            GL gl,
            ViewRenderResources res,
            SN.Vector3 lightDir,
            float diffuseK,
            float ambient,
            bool lightIsPoint,
            SN.Vector3 lightPosW,
            float lightRange,
            GPUFramebuffer? shadowFBO,
            in SN.Matrix4x4 shadowVP,
            SN.Vector3 sunSD,
            SN.Vector3 lightColor,
            Light? light,
            Avalonia.Media.Color skyTop,
            Avalonia.Media.Color skyBot,
            SN.Vector3? sunDir,
            Texture2D? skyTex,
            float skyMix,
            float skyYaw,
            SN.Vector3 fallbackSun,
            SN.Vector3? planetSkyUp = null)
        {
            if (res.Standard == null || res.Depth == null || res.Sky == null || res.FsQuad == null || res.Cache == null)
                return;

            var probes = ReflectionProbe.ActiveProbes;
            if (probes.Count == 0) return;

            ReflectionProbe? target = null;
            ReflectionProbe.ProbeGpuState? targetState = null;
            for (int i = 0; i < probes.Count; i++)
            {
                var p = probes[i];
                if (p is not { IsActiveAndEnabled: true, gameObject: not null }) continue;
                var state = p.EnsureGpuResources(gl);
                if (state.NeedsCapture || p.ShouldRefreshRealtime(gl))
                {
                    target = p;
                    targetState = state;
                    break; // Limit to 1 probe capture per frame to avoid hitches
                }
            }

            if (target == null || targetState?.Cubemap == null || target.gameObject == null) return;

            var targetCubemap = targetState.Cubemap;
            int size = Math.Clamp(targetCubemap.Width, 16, 512);

            // Save caller GL state & FrameGlobals UBO
            gl.GetInteger(GetPName.DrawFramebufferBinding, out int prevFb);
            Span<int> prevViewport = stackalloc int[4];
            gl.GetInteger(GetPName.Viewport, prevViewport);

            var globals = GlobalUniforms.ForContext(gl);
            var savedFrame = globals.FrameData;

            // Ensure reusable capture FBO + depth renderbuffer matching probe size
            if (res.ProbeCaptureFbo == 0)
                res.ProbeCaptureFbo = gl.GenFramebuffer();
            if (res.ProbeCaptureDepthRbo == 0)
                res.ProbeCaptureDepthRbo = gl.GenRenderbuffer();

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, res.ProbeCaptureFbo);
            if (res.ProbeCaptureSize != size)
            {
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, res.ProbeCaptureDepthRbo);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)size, (uint)size);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer, res.ProbeCaptureDepthRbo);
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
                res.ProbeCaptureSize = size;
            }

            DrawBufferMode color0 = DrawBufferMode.ColorAttachment0;
            gl.DrawBuffers(1, &color0);

            var worldMat = SceneGraphUtil.AccumulateWorld(target.gameObject);
            var probePos = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
            var faceProj = SN.Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1f, 0.1f, 500f);

            bool prevSuppress = s_suppressEnvMap;
            bool prevHdr = HdrOutputActive;
            var prevDebug = DebugViewMode;
            s_suppressEnvMap = true;
            HdrOutputActive = false;
            DebugViewMode = RenderDebugMode.Lit;

            try
            {
                gl.Viewport(0, 0, (uint)size, (uint)size);
                gl.Enable(EnableCap.DepthTest);
                gl.DepthFunc(DepthFunction.Less);
                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
                gl.ColorMask(true, true, true, true);

                for (int face = 0; face < 6; face++)
                {
                    gl.FramebufferTexture2D(
                        FramebufferTarget.Framebuffer,
                        FramebufferAttachment.ColorAttachment0,
                        TextureTarget.TextureCubeMapPositiveX + face,
                        targetCubemap.Handle,
                        0);

                    gl.ClearColor(0.12f, 0.12f, 0.15f, 1f);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                    var faceView = SN.Matrix4x4.CreateLookAt(
                        probePos,
                        probePos + s_cubeFaceDirs[face],
                        s_cubeFaceUps[face]);

                    globals.UpdateFrame(in faceView, in faceProj, probePos, WindSystem.Time, size, size, 0.1f, 500f);

                    // 1. Skybox
                    Sky.RenderGPU(gl, res.Sky, res.FsQuad, res.Cache, faceView, faceProj,
                        skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw, planetSkyUp);

                    // 2. Opaque scene + terrain (forward pass, env map suppressed)
                    RenderGPU(gl, res.Standard, res.Depth, res.Cache,
                        faceView, faceProj, lightDir, diffuseK, ambient,
                        lightIsPoint, lightPosW, lightRange,
                        shadowFBO, shadowVP, probePos, sunSD,
                        terrainShader: res.Terrain, isES: res.IsES, lightColor: lightColor,
                        cascadedShadow: null);

                    // 3. Planet terrain (if any)
                    if (res.PlanetTerrain != null && PlanetTerrain.ActivePlanets.Count > 0)
                    {
                        foreach (var planet in PlanetTerrain.ActivePlanets)
                        {
                            if (planet?.Config == null) continue;
                            var tp = planet.gameObject?.Transform?.Position;
                            var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                            var atmo = ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                            RenderPlanetTerrain(gl, res.PlanetTerrain, res.Cache, faceView, faceProj, planet, atmo,
                                lightDir, diffuseK, probePos, pc, shadowFBO, shadowVP);
                        }
                    }
                }

                // Generate prefiltered mip chain on GPU
                gl.BindTexture(TextureTarget.TextureCubeMap, targetCubemap.Handle);
                gl.GenerateMipmap(TextureTarget.TextureCubeMap);
                gl.BindTexture(TextureTarget.TextureCubeMap, 0);

                target.MarkCaptured(gl);
            }
            finally
            {
                s_suppressEnvMap = prevSuppress;
                HdrOutputActive = prevHdr;
                DebugViewMode = prevDebug;

                // Restore caller framebuffer, draw buffers, viewport, and FrameGlobals UBO
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFb);
                GPUFramebuffer.DrawColorTo(gl, (uint)prevFb);
                gl.Viewport(prevViewport[0], prevViewport[1], (uint)Math.Max(1, prevViewport[2]), (uint)Math.Max(1, prevViewport[3]));
                globals.RestoreFrame(in savedFrame);
            }
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

            if (SkipPlanetVegetationDraws && IsPlanetVegetationDrawName(go.Name))
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
            if (tree == null)
            {
                var ancestor = go.Parent;
                int hops = 0;
                while (ancestor != null && tree == null && hops < 6)
                {
                    foreach (var b in ancestor.Behaviors)
                    {
                        if (b is Tree tr && tr.Enabled)
                        {
                            tree = tr;
                            break;
                        }
                    }
                    ancestor = ancestor.Parent;
                    hops++;
                }
            }

            // Iterate behaviors once, pairing MeshFilters with MeshRenderers in order.
            // Avoids per-call list allocations — use index tracking instead.
            var behaviors = go.Behaviors;
            int nextMR = 0; // tracks which MeshRenderer index to pair with next MeshFilter
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is MeshFilter f && f.Enabled)
                {
                    var mesh = f.Mesh;
                    if (mesh == null) continue;
                    if (!TryPairMeshRendererForFilter(behaviors, ref nextMR, mesh, out var mr) || mr == null || mr.Wireframe)
                        continue;

                    // Chunked terrain: parent GO keeps MeshFilter + MeshRenderer enabled (serialized). The actual
                    // surface is drawn per Chunk_* child; skip the full-res parent mesh to avoid double draws.
                    if (terrain != null && ReferenceEquals(go, terrain.gameObject) && terrain.IsChunkedRenderingActive)
                        continue;

                    var sph = GetMeshSphere(mesh);
                    if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                    // LOD
                    var surface = new Size(1920, 1080);
                    var lodMesh = MeshLod.EnsureProceduralLod(f, world, view, proj, surface);
                    if (lodMesh == null) continue;

                    // Direct property access — no reflection, no pixel scanning.
                    var mat = mr.Material;
                    bool isTransparent = mat?.Transparent == true || mr.Color.A < 255;
                    if (!isTransparent && mat != null && !string.IsNullOrWhiteSpace(mat.ShaderAssetPath))
                    {
                        var absShader = ResolveShaderAbsPath(mat.ShaderAssetPath);
                        var schema = ShaderFileParser.GetCachedSchema(absShader);
                        if (schema != null && (schema.RenderState.Queue == ShaderRenderQueue.Transparent
                            || schema.RenderState.Queue == ShaderRenderQueue.Overlay))
                        {
                            isTransparent = true;
                        }
                    }

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
                string? absPath = ResolveShaderAbsPath(mat.ShaderAssetPath);
                if (!string.IsNullOrWhiteSpace(absPath))
                {
                    var schema = ShaderFileParser.GetCachedSchema(absPath);
                    var customShader = CustomShaderCache.GetOrCompile(absPath, gl, ShaderPassType.Forward, ctx.IsES);
                    if (customShader != null)
                    {
                        DrawWithCustomShader(gl, customShader, schema, cache, item, gpuMesh, in ctx);
                        // Restore the standard shader for subsequent draws.
                        // FrameGlobals & LightingGlobals live in UBOs so point lights, camera, and
                        // shadow state remain intact without clobbering them!
                        ctx.StandardShader.Use();
                        if (ctx.CascadedShadow != null || ctx.ShadowFBO?.DepthTexture != null)
                            BindShadowMapsToShader(ctx.StandardShader, ctx.ShadowFBO, ctx.CascadedShadow, 7);
                        BindEnvMapToShader(gl, ctx.StandardShader, ctx.CamPos, 12);
                        return;
                    }
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

            // Blended transparency: discard nearly invisible pixels; keep material cutout
            // threshold for the opaque pass (shader uses mat.AlphaCutoff when not transparent).
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
            bool foliage = ctx.PlanetFoliagePass
                || (item.Tree != null && item.Tree.IsVegetation);
            if (foliage)
            {
                PlanetVegetationSystem.TryGetActiveFoliageEnvironment(
                    out float wetness, out float snow, out float rain, out float cloudiness,
                    out float windMul, out float sunIntensity, out float atmoAmbient);
                float sway = item.Tree != null ? item.Tree.WindSway : 0.35f;
                float speed = item.Tree != null ? item.Tree.WindSpeed : 0.55f;
                float wind = WindSystem.GetCurrentStrength()
                    * Math.Clamp(sway, 0.08f, 0.55f)
                    * Math.Clamp(windMul, 0.35f, 1.15f);
                // Light rain stiffens a bit — do not multiply into storm displacement.
                wind *= 1f + Math.Clamp(rain, 0f, 1f) * 0.2f;
                var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                    ? SN.Vector3.Normalize(WindSystem.Direction)
                    : SN.Vector3.UnitX;
                float windTime = WindSystem.Time * Math.Clamp(speed, 0.25f, 1.05f) * 0.45f;
                float foliageAmbient = Math.Clamp(atmoAmbient, 0.02f, 0.85f);
                ctx.Globals.UpdateWindAndAmbient(windDir, windTime, foliageAmbient);

                shader.SetInt("uIsVegetation", 1);
                shader.SetFloat("uWindStrength", Math.Clamp(wind * 4.2f, 0.04f, 0.38f));
                shader.SetFloat("uRain", rain);
                shader.SetFloat("uWetness", wetness);
                shader.SetFloat("uSnow", snow);
                shader.SetFloat("uCloudiness", cloudiness);
                // Match planet day/night — no bright floors, don't pick Max(ctx, atmo).
                shader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.02f, 2f));
            }
            else
            {
                ctx.Globals.UpdateAmbientIfChanged(ctx.Ambient);
                shader.SetInt("uIsVegetation", 0);
                shader.SetFloat("uRain", 0f);
                shader.SetFloat("uWetness", 0f);
                shader.SetFloat("uSnow", 0f);
                shader.SetFloat("uCloudiness", 0f);
                shader.SetFloat("uSunIntensity", 1f);
                SetPlanetPropFill(shader, ctx.CamPos);
            }
            // Bone skinning matrices (zero-allocation BoneGlobals UBO upload)
            if (item.Skinned != null && item.Skinned.HasValidBoneMatrices)
            {
                shader.SetInt("uHasBones", 1);
                var bones = item.Skinned.BoneMatrices!;
                int count = System.Math.Min(bones.Length, BoneUniformLimit(ctx.IsES));
                ctx.Globals.UpdateBones(bones.AsSpan(0, count));
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
                        var gpuTex = slot is MaterialTexture ma ? cache.GetTexture(tex, ma) : cache.GetTexture(tex);
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
                        var gpuN = slot is MaterialTexture mn ? cache.GetTexture(nTex, mn) : cache.GetTexture(nTex);
                        gpuN.Bind(TextureUnit.Texture1);
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
                    if (sTex != null)
                    {
                        (slot is MaterialTexture ms ? cache.GetTexture(sTex, ms) : cache.GetTexture(sTex)).Bind(TextureUnit.Texture2);
                        hasSpec = true;
                        break;
                    }
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
                    if (mTex != null)
                    {
                        (slot is MaterialTexture mm ? cache.GetTexture(mTex, mm) : cache.GetTexture(mTex)).Bind(TextureUnit.Texture3);
                        hasMetal = true;
                        break;
                    }
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
                    if (rTex != null)
                    {
                        (slot is MaterialTexture mr ? cache.GetTexture(rTex, mr) : cache.GetTexture(rTex)).Bind(TextureUnit.Texture4);
                        hasRough = true;
                        break;
                    }
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
                    if (aoTex != null)
                    {
                        (slot is MaterialTexture mao ? cache.GetTexture(aoTex, mao) : cache.GetTexture(aoTex)).Bind(TextureUnit.Texture5);
                        hasAO = true;
                        break;
                    }
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
                    if (eTex != null)
                    {
                        (slot is MaterialTexture me ? cache.GetTexture(eTex, me) : cache.GetTexture(eTex)).Bind(TextureUnit.Texture6);
                        hasEmissiveTex = true;
                        break;
                    }
                }
            }
            shader.SetTexture("uEmissiveTex", 6);
            shader.SetInt("uHasEmissiveTex", hasEmissiveTex ? 1 : 0);

            // Opacity mask → unit 11 (material uses 0–6; cascaded shadows use 7–10)
            const int opacityTexUnit = 11;
            bool hasOpacity = false;
            if (mat?.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    Texture2D? oTex = null;
                    var slot = mat.Textures[i];
                    if (slot is RuntimeTexSlot rts)
                    {
                        var usage = rts.Usage?.ToLowerInvariant() ?? "";
                        if (usage.Contains("opacity") || usage.Contains("transparency") || usage == "mask")
                            oTex = rts.Texture;
                    }
                    else if (slot is MaterialTexture mtex)
                    {
                        if (mtex.Usage == MaterialTexture.TexUsage.Opacity)
                            oTex = mtex.Texture;
                    }
                    if (oTex != null)
                    {
                        (slot is MaterialTexture mo ? cache.GetTexture(oTex, mo) : cache.GetTexture(oTex))
                            .Bind(TextureUnit.Texture0 + opacityTexUnit);
                        hasOpacity = true;
                        break;
                    }
                }
            }
            shader.SetTexture("uOpacityTex", opacityTexUnit);
            shader.SetInt("uHasOpacityTex", hasOpacity ? 1 : 0);
            shader.SetFloat("uLumaClip", mat?.LumaClip ?? 0f);

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
        private static string? ResolveShaderAbsPath(string? shaderPath)
        {
            if (string.IsNullOrWhiteSpace(shaderPath)) return null;
            try
            {
                if (System.IO.Path.IsPathRooted(shaderPath))
                {
                    var abs = System.IO.Path.GetFullPath(shaderPath);
                    return System.IO.File.Exists(abs) ? abs : (AssetPathRemap.FindExisting(shaderPath) ?? abs);
                }
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(proj.RootPath, shaderPath));
                    if (System.IO.File.Exists(abs)) return abs;
                }
                return AssetPathRemap.FindExisting(shaderPath) ?? shaderPath;
            }
            catch { return shaderPath; }
        }

        private static bool IsDeferredSurfaceShader(Material? mat, out string? absPath, out ParsedShaderFile? schema)
        {
            absPath = null;
            schema = null;
            if (mat == null || string.IsNullOrWhiteSpace(mat.ShaderAssetPath)) return false;

            absPath = ResolveShaderAbsPath(mat.ShaderAssetPath);
            if (string.IsNullOrWhiteSpace(absPath)) return false;

            schema = ShaderFileParser.GetCachedSchema(absPath);
            return schema != null
                && schema.IsSurfaceShader
                && schema.RenderState.Queue != ShaderRenderQueue.Transparent
                && schema.RenderState.Queue != ShaderRenderQueue.Overlay
                && schema.RenderState.Lighting != ShaderLightingModel.Unlit;
        }

        private static void BindCustomStandardTextures(
            ShaderProgram customShader,
            Material? mat,
            ResourceCache cache)
        {
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
                    if (tex != null)
                    {
                        (slot is MaterialTexture ma0 ? cache.GetTexture(tex, ma0) : cache.GetTexture(tex)).Bind(TextureUnit.Texture0);
                        hasAlbedo = true;
                        break;
                    }
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
                    if (nTex != null)
                    {
                        (slot is MaterialTexture mn0 ? cache.GetTexture(nTex, mn0) : cache.GetTexture(nTex)).Bind(TextureUnit.Texture1);
                        hasNormal = true;
                        break;
                    }
                }
            }
            if (!hasNormal)
                cache.GetFlatNormalTexture().Bind(TextureUnit.Texture1);
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
                    if (sTex != null)
                    {
                        (slot is MaterialTexture ms0 ? cache.GetTexture(sTex, ms0) : cache.GetTexture(sTex)).Bind(TextureUnit.Texture2);
                        hasSpec = true;
                        break;
                    }
                }
            }
            if (!hasSpec) cache.GetWhiteTexture().Bind(TextureUnit.Texture2);
            customShader.SetTexture("uTexture2", 2);
            customShader.SetInt("uHasSpecularMap", hasSpec ? 1 : 0);
        }

        private static void DrawSurfaceToGBuffer(
            GL gl,
            ShaderProgram gbufSurfaceShader,
            ParsedShaderFile schema,
            ResourceCache cache,
            in DrawItem item,
            in RenderContext ctx)
        {
            var mesh = item.MF.Mesh;
            if (mesh == null) return;
            var gpuMesh = cache.GetMesh(mesh);

            gbufSurfaceShader.Use();

            gbufSurfaceShader.SetMatrix4("uModel", item.World);
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            var normalMatrix = SN.Matrix4x4.Transpose(invWorld);
            gbufSurfaceShader.SetMatrix4("uNormalMatrix", normalMatrix);

            var mat = item.Mat;
            gbufSurfaceShader.SetFloat("uRoughness", mat?.Roughness ?? 0.5f);
            gbufSurfaceShader.SetFloat("uMetallic", mat?.Metallic ?? 0f);

            BindCustomStandardTextures(gbufSurfaceShader, mat, cache);
            // Surface GBuffer header declares uniform samplerCube uEnvMap; bind it to unit 15
            // (disjoint from MaterialBinder custom units 3, 5, 6, 11..14) so it never collides with unit 0.
            BindEnvMapToShader(gl, gbufSurfaceShader, ctx.CamPos, 15);
            MaterialBinder.BindProperties(gbufSurfaceShader, mat, schema, cache);

            if (item.Skinned != null && item.Skinned.HasValidBoneMatrices)
            {
                gbufSurfaceShader.SetInt("uHasBones", 1);
                var bones = item.Skinned.BoneMatrices!;
                int boneCount = System.Math.Min(bones.Length, BoneUniformLimit(ctx.IsES));
                var boneSpan = bones.AsSpan(0, boneCount);
                if (gbufSurfaceShader.HasBoneUniformBlock)
                    ctx.Globals.UpdateBones(boneSpan);
                else
                    gbufSurfaceShader.SetMatrix4Array("uBones[0]", boneSpan);
            }
            else
            {
                gbufSurfaceShader.SetInt("uHasBones", 0);
            }

            using var stateScope = MaterialBinder.ApplyRenderState(
                gl,
                schema.RenderState,
                doubleSidedOverride: item.MR.DoubleSided,
                invertFrontFace: item.MR.InvertFrontFace,
                restoreCullEnabled: true,
                restoreCullFace: TriangleFace.Back,
                restoreDepthWrite: true,
                restoreBlend: false);

            gpuMesh.Draw();
        }

        /// <summary>
        /// Draws a mesh item using a custom .shader program, binding all standard
        /// lighting/shadow uniforms, custom Material.Properties, and ShaderRenderState.
        /// </summary>
        private static void DrawWithCustomShader(
            GL gl,
            ShaderProgram customShader,
            ParsedShaderFile? schema,
            ResourceCache cache,
            in DrawItem item,
            GPUMesh gpuMesh,
            in RenderContext ctx)
        {
            customShader.Use();

            // Ensure LightingGlobals has the scene ambient (in case previous draw was foliage)
            ctx.Globals.UpdateAmbientIfChanged(ctx.Ambient);

            // ── Vertex uniforms ──
            customShader.SetMatrix4("uModel", item.World);

            // Normal matrix = transpose(inverse(model))
            SN.Matrix4x4.Invert(item.World, out var invWorld);
            var normalMatrix = SN.Matrix4x4.Transpose(invWorld);
            customShader.SetMatrix4("uNormalMatrix", normalMatrix);

            // Legacy custom shaders that don't declare FrameGlobals / LightingGlobals UBO blocks
            // still receive the classic uniform names for 100% backward compatibility.
            if (!customShader.HasFrameUniformBlock)
            {
                customShader.SetMatrix4("uView", ctx.View);
                customShader.SetMatrix4("uProjection", ctx.Proj);
                customShader.SetMatrix4("uProj", ctx.Proj);
                customShader.SetVector3("uCameraPos", ctx.CamPos);
                customShader.SetVector3("uCamPos", ctx.CamPos);
                customShader.SetFloat("uTime", WindSystem.Time);
            }

            if (!customShader.HasLightingUniformBlock)
            {
                customShader.SetMatrix4("uLightSpaceMatrix", ctx.ShadowVP);
                customShader.SetMatrix4("uShadowVP", ctx.ShadowVP);
                // Both the standard shader and custom shaders use the same convention:
                // uLightDir = direction FROM the light (the "shine" direction).
                // Shaders negate internally: dot(N, -uLightDir) or vec3 L = normalize(-uLightDir).
                customShader.SetVector3("uLightDir", ctx.LightDir);
                customShader.SetVector3("uLightColor", ctx.LightColor);
                customShader.SetFloat("uLightIntensity", ctx.DiffuseK);
                customShader.SetFloat("uDiffuseK", ctx.DiffuseK);
                customShader.SetFloat("uAmbient", ctx.Ambient);
            }

            // Material properties
            var mat = item.Mat;
            float roughness = mat?.Roughness ?? 0.5f;
            float metallic = mat?.Metallic ?? 0f;
            customShader.SetFloat("uRoughness", roughness);
            customShader.SetFloat("uMetallic", metallic);

            // Standard material texture slots (units 0, 1, 2)
            BindCustomStandardTextures(customShader, mat, cache);

            // ── Bind shadow map(s) → units 7..10 (CSM) or unit 7 (single) + unit 4 (legacy custom) ──
            if (ctx.CascadedShadow != null)
            {
                BindCascadeShadowUniforms(customShader, ctx.CascadedShadow, 7);
                if (!customShader.HasLightingUniformBlock)
                    customShader.SetInt("uHasShadow", 1);
                customShader.SetFloat("uShadowBias", 0.008f);
                customShader.SetVector3("uSunDir", ctx.SunShineDir);
            }
            else if (ctx.ShadowFBO?.DepthTexture != null)
            {
                int unit = customShader.HasLightingUniformBlock ? 7 : 4;
                ctx.ShadowFBO.DepthTexture.Bind(TextureUnit.Texture0 + unit);
                customShader.SetTexture("uShadowMap", unit);
                customShader.SetTexture("uShadowMapC1", unit);
                customShader.SetTexture("uShadowMapC2", unit);
                customShader.SetTexture("uShadowMapC3", unit);
                if (!customShader.HasLightingUniformBlock)
                    customShader.SetInt("uHasShadow", 1);
                customShader.SetFloat("uShadowBias", 0.008f);
                customShader.SetVector3("uSunDir", ctx.SunShineDir);
            }
            else if (!customShader.HasLightingUniformBlock)
            {
                customShader.SetInt("uHasShadow", 0);
            }

            // ReflectionProbe cubemap IBL → unit 15 (disjoint from MaterialBinder units 3,5,6,11..14)
            BindEnvMapToShader(gl, customShader, ctx.CamPos, 15);
            customShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);

            // Dynamic Material.Properties overrides + schema defaults (safe units 3, 5, 6, 11..14)
            MaterialBinder.BindProperties(customShader, mat, schema, cache);

            // GPU skinning: upload to BoneGlobals UBO when declared, or fall back to zero-alloc
            // SetMatrix4Array("uBones[0]", ...) for legacy custom shaders.
            if (item.Skinned != null && item.Skinned.HasValidBoneMatrices)
            {
                customShader.SetInt("uHasBones", 1);
                var bones = item.Skinned.BoneMatrices!;
                int boneCount = System.Math.Min(bones.Length, BoneUniformLimit(ctx.IsES));
                var boneSpan = bones.AsSpan(0, boneCount);
                if (customShader.HasBoneUniformBlock)
                    ctx.Globals.UpdateBones(boneSpan);
                else
                    customShader.SetMatrix4Array("uBones[0]", boneSpan);
            }
            else
                customShader.SetInt("uHasBones", 0);

            using var stateScope = MaterialBinder.ApplyRenderState(
                gl,
                schema?.RenderState,
                doubleSidedOverride: item.MR.DoubleSided,
                invertFrontFace: item.MR.InvertFrontFace,
                restoreCullEnabled: true,
                restoreCullFace: TriangleFace.Back,
                restoreDepthWrite: !item.IsTransparent,
                restoreBlend: item.IsTransparent);

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

            // Bind splatmaps on units 0, 1
            splat0.Bind(TextureUnit.Texture0);
            shader.SetTexture("uSplatmap0", 0);
            splat1.Bind(TextureUnit.Texture1);
            shader.SetTexture("uSplatmap1", 1);

            // Layer count + per-layer scalars + GL_TEXTURE_2D_ARRAY bindings (units 2 & 3)
            int layerCount = Math.Min(terrain.Layers.Count, 8);
            shader.SetInt("uLayerCount", layerCount);

            var albedoTextures = new Texture2D?[8];
            var normalTextures = new Texture2D?[8];

            for (int i = 0; i < 8; i++)
            {
                if (i < layerCount)
                {
                    var layer = terrain.Layers[i];
                    if (!string.IsNullOrEmpty(layer.TexturePath))
                        albedoTextures[i] = TryLoadLayerTexture(layer.TexturePath);
                    if (!string.IsNullOrEmpty(layer.NormalMapPath))
                        normalTextures[i] = TryLoadLayerTexture(layer.NormalMapPath);

                    shader.SetFloat(s_tilingNames[i], layer.Tiling);
                    shader.SetFloat(s_roughNames[i], layer.Roughness);
                    shader.SetFloat(s_metalNames[i], layer.Metallic);
                    shader.SetInt(s_hasNormalNames[i], normalTextures[i] != null ? 1 : 0);
                }
                else
                {
                    shader.SetInt(s_hasNormalNames[i], 0);
                }
            }

            // Units: 0..1 splatmaps, 2 albedo array (8 layers), 3 normal array (8 layers), 7..10 CSM shadows = 8 units total.
            int targetSize = cache.IsESContext ? 512 : 1024;
            string keyPrefix = $"terrain:{RuntimeHelpers.GetHashCode(terrain)}";

            var albedoArray = cache.GetOrCreateTexture2DArray(
                keyPrefix + ":albedo", albedoTextures, targetSize, 255, 255, 255, 255);
            albedoArray.Bind(TextureUnit.Texture2);
            shader.SetTexture("uLayerAlbedos", 2);

            var normalArray = cache.GetOrCreateTexture2DArray(
                keyPrefix + ":normal", normalTextures, targetSize, 128, 128, 255, 255);
            normalArray.Bind(TextureUnit.Texture3);
            shader.SetTexture("uLayerNormals", 3);
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

                // 6) Absolute path from another machine: the same asset under this project's Assets
                return AssetPathRemap.FindExisting(path);
            }
            catch { }

            return null;
        }

        private static readonly ConcurrentDictionary<string, Texture2D?> s_asyncBiomeTex = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> s_asyncBiomeBusy = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Decode one ground texture off the render thread. The frame only picks up the pixels when they are ready.</summary>
        static void RequestBiomeDecode(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (s_asyncBiomeTex.ContainsKey(path) || !s_asyncBiomeBusy.TryAdd(path, 1))
                return;
            Task.Run(() =>
            {
                Texture2D? tex = null;
                try
                {
                    var abs = ResolveTexturePath(path);
                    if (abs != null)
                    {
                        tex = Texture2D.FromFile(abs);
                        if (tex != null && (tex.Width > BiomeCpuMaxEdge || tex.Height > BiomeCpuMaxEdge))
                            tex = tex.DownscaleMaxEdge(BiomeCpuMaxEdge);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("[BiomeTex] decode failed: " + ex.Message);
                }
                s_asyncBiomeTex[path] = tex;
            });
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
                {
                    tex = Texture2D.FromFile(abs);
                    if (tex != null && (tex.Width > BiomeCpuMaxEdge || tex.Height > BiomeCpuMaxEdge))
                        tex = tex.DownscaleMaxEdge(BiomeCpuMaxEdge);
                }
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

            bool skipPlanetVegInSceneGraph = PlanetVegetationSystem.AnyUseDedicatedRenderPass;
            var prevSkipG = SkipPlanetVegetationDraws;
            SkipPlanetVegetationDraws = skipPlanetVegInSceneGraph || prevSkipG;
            foreach (var root in SceneService.Root)
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);
            SkipPlanetVegetationDraws = prevSkipG;

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            var globals = GlobalUniforms.ForContext(gl);
            var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                ? SN.Vector3.Normalize(WindSystem.Direction)
                : SN.Vector3.UnitX;
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time);
            globals.UpdateLighting(
                SN.Vector3.UnitY, 0f, SN.Vector3.One, 0f,
                lightIsPoint: false, SN.Vector3.Zero, 0f,
                hasShadow: shadowFBO?.DepthTexture != null, in shadowVP,
                windDir: windDir, windTime: WindSystem.Time);

            gbufferShader.Use();

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
                ShadowFBO = shadowFBO,
                SunShineDir = sunShineDir,
                StandardShader = gbufferShader,
                Cache = cache,
                Globals = globals,
                IsES = isES,
                ForceStandardShader = true
            };

            bool desktopWireframe = DebugViewMode == RenderDebugMode.Wireframe && !isES;
            if (desktopWireframe)
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            try
            {
                DrawOpaqueItemsWithAutoInstancing(
                    gl, gbufferShader, cache, opaqueItems, in renderCtx,
                    skipTerrainWithLayers: true,
                    skipAllTerrain: false,
                    isGBufferPass: true);
            }
            finally
            {
                if (desktopWireframe)
                    gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
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
            float shadowBias,
            float ssaoIntensity = 1f,
            TiledLightTextureSystem? tiledLights = null,
            int screenWidth = 0,
            int screenHeight = 0,
            GPUTexture? reflectionProbeCubemap = null,
            float reflectionProbeBlend = 0f,
            CascadedShadowMap? cascadedShadow = null)
        {
            // Update FrameGlobals & LightingGlobals UBOs for deferred lighting pass
            var globals = GlobalUniforms.ForContext(gl);
            bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;
            GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, screenWidth, screenHeight, nearP, farP);
            globals.UpdateLighting(
                sunShineDir, 1f, SN.Vector3.One, ambient,
                lightIsPoint: false, SN.Vector3.Zero, 0f,
                hasShadow: hasShadow, in shadowVP,
                cascadeCount: hasShadow ? 1 : 0,
                cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f));

            deferredShader.Use();
            deferredShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
            deferredShader.SetInt("uDebugViewMode", (int)DebugViewMode);

            // Texture unit map (0..14 — zero collisions, GLES 3.0 safe):
            // 0..2: G-buffer MRT | 3: gDepth | 4: uSSAOTex | 7..10: CSM shadows | 11..13: Tiled lights | 14: uEnvMap
            if (gbufferFBO.ColorTextures != null && gbufferFBO.ColorTextures.Length >= 3)
            {
                gbufferFBO.ColorTextures[0].Bind(TextureUnit.Texture0);
                deferredShader.SetTexture("gAlbedoMetallic", 0);

                gbufferFBO.ColorTextures[1].Bind(TextureUnit.Texture1);
                deferredShader.SetTexture("gNormalRoughness", 1);

                gbufferFBO.ColorTextures[2].Bind(TextureUnit.Texture2);
                deferredShader.SetTexture("gEmissiveAO", 2);
            }

            // Depth (unit 3)
            if (gbufferFBO.DepthTexture != null)
            {
                gbufferFBO.DepthTexture.Bind(TextureUnit.Texture3);
                deferredShader.SetTexture("gDepth", 3);
            }

            // SSAO (unit 4)
            if (ssaoTexture != null)
            {
                ssaoTexture.Bind(TextureUnit.Texture4);
                deferredShader.SetTexture("uSSAOTex", 4);
                deferredShader.SetInt("uHasSSAO", 1);
                deferredShader.SetFloat("uSSAOIntensity", Math.Clamp(ssaoIntensity, 0f, 2f));
            }
            else
            {
                deferredShader.SetInt("uHasSSAO", 0);
                deferredShader.SetFloat("uSSAOIntensity", 0f);
            }

            // Shadow map(s) (units 7..10)
            if (hasShadow)
            {
                BindShadowMapsToShader(deferredShader, shadowFBO, cascadedShadow, 7);
                deferredShader.SetFloat("uShadowBias", shadowBias);
                deferredShader.SetVector3("uSunDir", sunShineDir);
            }

            // Inverse VP (uCamPos and uAmbient come from FrameGlobals / LightingGlobals UBOs)
            var vp = view * proj;
            SN.Matrix4x4.Invert(vp, out var invVP);
            deferredShader.SetMatrix4("uInvViewProj", invVP);

            SetPlanetPropFill(deferredShader, camPos);

            var dirScratch = new List<Light>(8);
            var localScratch = new List<Light>(128);
            if (tiledLights != null && screenWidth > 0 && screenHeight > 0)
                tiledLights.BuildAndUpload(screenWidth, screenHeight, in view, in proj, dirScratch, localScratch);
            else
            {
                foreach (var light in Light.AllLights)
                {
                    if (light is not { Enabled: true, gameObject: not null }) continue;
                    if (light.Type == LightType.Directional)
                    {
                        if (dirScratch.Count < TiledLightTextureSystem.MaxDirLights)
                            dirScratch.Add(light);
                    }
                    else if (localScratch.Count < TiledLightTextureSystem.MaxLocalLights)
                        localScratch.Add(light);
                }
            }

            int dirCount = Math.Min(dirScratch.Count, TiledLightTextureSystem.MaxDirLights);
            deferredShader.SetInt("uDirLightCount", dirCount);
            for (int i = 0; i < TiledLightTextureSystem.MaxDirLights; i++)
            {
                if (i < dirCount)
                {
                    var light = dirScratch[i];
                    var d = light.GetWorldDirection();
                    deferredShader.SetVector3($"uDirLightDirs[{i}]", SN.Vector3.Normalize(-d));
                    deferredShader.SetVector3($"uDirLightColors[{i}]",
                        new SN.Vector3(light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f));
                    deferredShader.SetFloat($"uDirLightIntensities[{i}]", light.Intensity);
                }
                else
                {
                    deferredShader.SetVector3($"uDirLightDirs[{i}]", SN.Vector3.UnitY);
                    deferredShader.SetVector3($"uDirLightColors[{i}]", SN.Vector3.Zero);
                    deferredShader.SetFloat($"uDirLightIntensities[{i}]", 0f);
                }
            }

            // Tiled local lights (units 11, 12, 13)
            int localCount = localScratch.Count;
            if (tiledLights != null && screenWidth > 0 && screenHeight > 0)
            {
                deferredShader.SetInt("uLocalLightCount", localCount);
                deferredShader.SetInt("uTilesX", tiledLights.TilesX);
                deferredShader.SetInt("uTilesY", tiledLights.TilesY);
                if (localCount > 0)
                    tiledLights.BindToShader(deferredShader, 11);
            }
            else
            {
                deferredShader.SetInt("uLocalLightCount", 0);
                deferredShader.SetInt("uTilesX", 1);
                deferredShader.SetInt("uTilesY", 1);
            }

            // ReflectionProbe specular IBL (unit 14)
            BindEnvMapToShader(gl, deferredShader, camPos, 14, reflectionProbeCubemap, reflectionProbeBlend);

            // Draw fullscreen quad — do not write depth here; scene FBO depth is filled by the
            // gbuffer→scene blit in GameView so forward overlays (terrain) depth-test correctly.
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.DepthMask(false);
            fsQuad.Draw();
            gl.DepthMask(true);
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// Fills the current framebuffer's depth buffer from a depth texture (fullscreen pass).
        /// Color writes are masked off; call with the scene FBO bound after deferred lighting.
        /// </summary>
        public static void RenderDepthTextureToFramebufferDepth(
            GL gl,
            ShaderProgram depthCopyShader,
            FullscreenQuad fsQuad,
            GPUTexture gbufferDepth)
        {
            // Sample depth as a float (same as deferred). Shadow maps use compare mode; gbuffer depth must not.
            const TextureParameterName TextureCompareMode = (TextureParameterName)0x884C;
            gl.ColorMask(false, false, false, false);
            gl.DepthMask(true);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.Disable(EnableCap.StencilTest);
            depthCopyShader.Use();
            gbufferDepth.Bind(TextureUnit.Texture0);
            gl.TexParameter(TextureTarget.Texture2D, TextureCompareMode, 0);
            depthCopyShader.SetTexture("uDepth", 0);
            fsQuad.Draw();
            gl.ColorMask(true, true, true, true);
            gl.DepthMask(true);
            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// Forward overlay pass: renders terrain, custom shader items, and transparent objects.
        /// Re-gathers draw items for this view/proj (do not rely on lists left from RenderGBufferPass alone).
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
            SN.Vector3 lightColor = default,
            CascadedShadowMap? cascadedShadow = null)
        {
            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            var opaqueItems = s_opaqueItems ??= new List<DrawItem>(256);
            var transparentItems = s_transparentItems ??= new List<DrawItem>(64);
            opaqueItems.Clear();
            transparentItems.Clear();
            bool skipPlanetVegInSceneGraph = PlanetVegetationSystem.AnyUseDedicatedRenderPass;
            var prevSkipF = SkipPlanetVegetationDraws;
            SkipPlanetVegetationDraws = skipPlanetVegInSceneGraph || prevSkipF;
            foreach (var root in SceneService.Root)
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);
            SkipPlanetVegetationDraws = prevSkipF;

            var globals = GlobalUniforms.ForContext(gl);
            var resolvedLightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor;
            bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;
            var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                ? SN.Vector3.Normalize(WindSystem.Direction)
                : SN.Vector3.UnitX;

            GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
            globals.UpdateLighting(
                lightDir, diffuseK, resolvedLightColor, ambient,
                lightIsPoint, lightPosW, lightRange,
                hasShadow, in shadowVP,
                cascadeCount: hasShadow ? 1 : 0,
                cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
                windDir: windDir,
                windTime: WindSystem.Time);
            BindNearestPointLights(globals, camPos);

            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = lightDir,
                LightColor = resolvedLightColor,
                DiffuseK = diffuseK,
                Ambient = ambient,
                ShadowVP = shadowVP,
                ShadowFBO = shadowFBO,
                CascadedShadow = cascadedShadow,
                SunShineDir = sunShineDir,
                StandardShader = standardShader,
                Cache = cache,
                Globals = globals,
                IsES = isES
            };

            // --- TERRAIN PASS ---
            if (terrainShader != null)
            {
                Terrain? boundTerrain = null;
                gl.Disable(EnableCap.StencilTest);
                gl.ColorMask(true, true, true, true);
                gl.DepthMask(true);
                gl.Enable(EnableCap.DepthTest);
                // Deferred-only: scene depth was copied from the G-buffer (terrain was never drawn there).
                // LEQUAL + slight polygon offset tolerates quantization / copy vs rasterization mismatch; strict
                // Less can reject every terrain fragment if stored depth is even one ULP closer than drawn depth.
                gl.DepthFunc(DepthFunction.Lequal);
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(-1f, -1f);
                terrainShader.Use();
                terrainShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
                terrainShader.SetFloat("uParallaxStrength", 0.035f);
                terrainShader.SetFloat("uShadowBias", 0.008f);
                terrainShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(
                    terrainShader,
                    hasShadow ? shadowFBO : null,
                    hasShadow ? cascadedShadow : null,
                    7,
                    cache);

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
                gl.Disable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(0f, 0f);
            }

            // Planet vegetation is drawn after planet terrain (see RenderPlanetVegetationAfterTerrain).

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
                    LightColor = resolvedLightColor,
                    DiffuseK = diffuseK,
                    Ambient = ambient,
                    ShadowVP = shadowVP,
                    ShadowFBO = shadowFBO,
                    CascadedShadow = cascadedShadow,
                    SunShineDir = sunShineDir,
                    StandardShader = standardShader,
                    Cache = cache,
                    Globals = globals,
                    IsES = isES,
                    ForceStandardShader = false
                };

                gl.DepthFunc(DepthFunction.Lequal);
                foreach (var item in opaqueItems)
                {
                    if (item.Terrain != null) continue;
                    var mat = item.Mat;
                    if (mat == null || string.IsNullOrWhiteSpace(mat.ShaderAssetPath)) continue;

                    // Opaque lit Surface shaders were already rendered natively into the G-Buffer
                    // (avoid double-drawing / double-tonemapping).
                    if (IsDeferredSurfaceShader(mat, out _, out _)) continue;

                    DrawMeshItem(gl, standardShader, cache, item, in customCtx);
                }
                gl.DepthFunc(DepthFunction.Less);
            }

            // Set up the standard shader for transparent items below.
            standardShader.Use();

            if (hasShadow)
            {
                standardShader.SetFloat("uShadowBias", 0.008f);
                standardShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
            }
            BindEnvMapToShader(gl, standardShader, camPos, 12);

            // --- TRANSPARENT PASS (back-to-front) ---
            if (transparentItems != null && transparentItems.Count > 0)
            {
                transparentItems.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);
                // Glass is a second surface in the wall's plane. ANGLE's depth buffer is coarser than
                // desktop GL, and Less rejects that layer so the windows stay empty.
                gl.DepthFunc(DepthFunction.Lequal);
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(-2f, -2f);

                standardShader.Use();
                standardShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
                standardShader.SetInt("uDebugViewMode", (int)DebugViewMode);
                if (hasShadow)
                    BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
                BindEnvMapToShader(gl, standardShader, camPos, 12);

                foreach (var item in transparentItems)
                {
                    DrawMeshItem(gl, standardShader, cache, item, in renderCtx);
                }

                gl.DepthFunc(DepthFunction.Less);
                gl.Disable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(0f, 0f);
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
            int screenWidth,
            int screenHeight,
            float radius = 0.5f,
            float bias = 0.025f,
            int sampleCount = 24)
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

            ssaoShader.SetFloat("uRadius", radius);
            ssaoShader.SetFloat("uBias", bias);
            ssaoShader.SetInt("uSampleCount", Math.Clamp(sampleCount, 4, 32));
            ssaoShader.SetVector2("uNoiseScale", screenWidth / 4f, screenHeight / 4f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            fsQuad.Draw();
            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
        }

        /// <summary>SSAO bilateral blur pass. The blur FBO must be bound before calling.</summary>
        public static void RenderSSAOBlur(
            GL gl,
            ShaderProgram blurShader,
            FullscreenQuad fsQuad,
            GPUTexture ssaoInput,
            GPUFramebuffer gbufferFBO,
            int width,
            int height,
            float depthSigma = 80f)
        {
            blurShader.Use();
            ssaoInput.Bind(TextureUnit.Texture0);
            blurShader.SetTexture("uSSAOInput", 0);
            if (gbufferFBO.DepthTexture != null)
            {
                gbufferFBO.DepthTexture.Bind(TextureUnit.Texture1);
                blurShader.SetTexture("gDepth", 1);
            }
            blurShader.SetVector2("uTexelSize", 1f / width, 1f / height);
            blurShader.SetFloat("uDepthSigma", depthSigma);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            fsQuad.Draw();
            gl.Enable(EnableCap.CullFace);
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
            int screenHeight,
            int maxRaySteps = 64,
            float roughnessCutoff = 0.6f,
            float maxRayLength = 50f)
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
            ssrShader.SetInt("uMaxSteps", Math.Clamp(maxRaySteps, 4, 128));
            ssrShader.SetFloat("uRoughnessCutoff", roughnessCutoff);
            ssrShader.SetFloat("uMaxRayLength", Math.Max(1f, maxRayLength));

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            fsQuad.Draw();
            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
        }

        public static void RenderTemporalAA(
            GL gl,
            ShaderProgram taaShader,
            FullscreenQuad fsQuad,
            GPUTexture currScene,
            GPUTexture? history,
            GPUTexture depthTexture,
            in SN.Matrix4x4 invViewProj,
            in SN.Matrix4x4 prevViewProj,
            int width,
            int height,
            float blendNew,
            float sharpen,
            bool resetHistory)
        {
            taaShader.Use();
            currScene.Bind(TextureUnit.Texture0);
            taaShader.SetTexture("uCurr", 0);
            var histTex = history ?? currScene;
            histTex.Bind(TextureUnit.Texture1);
            taaShader.SetTexture("uHistory", 1);
            depthTexture.Bind(TextureUnit.Texture2);
            taaShader.SetTexture("uDepth", 2);
            taaShader.SetMatrix4("uInvViewProj", invViewProj);
            taaShader.SetMatrix4("uPrevViewProj", prevViewProj);
            taaShader.SetVector2("uTexel", 1f / width, 1f / height);
            taaShader.SetFloat("uAlpha", resetHistory ? 1f : Math.Clamp(blendNew, 0.02f, 0.5f));
            taaShader.SetFloat("uSharpen", Math.Clamp(sharpen, 0f, 1f));

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            fsQuad.Draw();
            gl.Enable(EnableCap.CullFace);
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
            => RenderVolumetricFog(gl, volFogShader, fsQuad, sceneColor, sceneDepth,
                in view, in proj, camPos, lightDir, lightColor, shadowFBO, in shadowVP,
                PostProcessSettings.FromVolume(volume), time);

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
            PostProcessSettings settings,
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

            volFogShader.SetFloat("uFogDensity", settings.VolumetricFogDensity);
            volFogShader.SetFloat("uFogAnisotropy", settings.VolumetricFogAnisotropy);
            volFogShader.SetFloat("uFogScattering", settings.VolumetricFogScattering);
            volFogShader.SetFloat("uFogHeightFalloff", settings.VolumetricFogHeightFalloff);
            volFogShader.SetFloat("uFogBaseHeight", settings.VolumetricFogBaseHeight);
            volFogShader.SetFloat("uFogNoiseScale", settings.VolumetricFogNoiseScale);
            volFogShader.SetFloat("uFogNoiseSpeed", settings.VolumetricFogNoiseSpeed);
            volFogShader.SetFloat("uFogMaxDistance", settings.VolumetricFogMaxDistance);
            volFogShader.SetVector3("uFogColor", settings.VolumetricFogColor);
            volFogShader.SetInt("uFogSteps", Math.Clamp(settings.VolumetricFogSteps, 4, 16));
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
            gl.Disable(EnableCap.CullFace);
            fsQuad.Draw();
            gl.Enable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest);
        }

        // ══════════════════════════════════════════════════════════

        // Reusable scratch buffers for zero-allocation nearest point-light gathering
        [ThreadStatic] private static Light?[]? s_nearestPointSlots;
        [ThreadStatic] private static float[]? s_nearestPointDists;

        static void BindNearestPointLights(GlobalUniforms globals, SN.Vector3 camPos)
        {
            const int Max = 4;
            var slot = s_nearestPointSlots ??= new Light?[Max];
            var dist = s_nearestPointDists ??= new float[Max];
            for (int i = 0; i < Max; i++)
            {
                slot[i] = null;
                dist[i] = float.MaxValue;
            }

            var lights = Light.AllLights;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                if (light is not { Enabled: true, gameObject: not null }) continue;
                if (light.Type != LightType.Point || light.Intensity <= 0.01f || light.Range <= 0.05f)
                    continue;
                float d2 = SN.Vector3.DistanceSquared(light.GetWorldPosition(), camPos);
                float reach = light.Range + 6f;
                if (d2 > reach * reach) continue;
                for (int s = 0; s < Max; s++)
                {
                    if (d2 >= dist[s]) continue;
                    for (int k = Max - 1; k > s; k--)
                    {
                        slot[k] = slot[k - 1];
                        dist[k] = dist[k - 1];
                    }
                    slot[s] = light;
                    dist[s] = d2;
                    break;
                }
            }

            static void PackPoint(Light? light, out SN.Vector4 posRange, out SN.Vector4 color)
            {
                if (light == null)
                {
                    posRange = SN.Vector4.Zero;
                    color = SN.Vector4.Zero;
                    return;
                }
                var p = light.GetWorldPosition();
                posRange = new SN.Vector4(p.X, p.Y, p.Z, light.Range);
                color = new SN.Vector4(
                    light.Color.R / 255f * light.Intensity,
                    light.Color.G / 255f * light.Intensity,
                    light.Color.B / 255f * light.Intensity,
                    light.Intensity);
            }

            int count = 0;
            for (int i = 0; i < Max; i++)
                if (slot[i] != null) count = i + 1;

            PackPoint(slot[0], out var pr0, out var c0);
            PackPoint(slot[1], out var pr1, out var c1);
            PackPoint(slot[2], out var pr2, out var c2);
            PackPoint(slot[3], out var pr3, out var c3);

            // Clear references so Light instances aren't retained across scene unloads
            for (int i = 0; i < Max; i++)
                slot[i] = null;

            globals.UpdatePointLights(count, pr0, c0, pr1, c1, pr2, c2, pr3, c3);
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

            if (PlanetVegetationSystem.AnyUseDedicatedRenderPass && IsPlanetVegetationDrawName(go.Name))
                return;

            // Fast skip for culled vegetation chunks
            if (go.Name.StartsWith("chunk_"))
            {
                var bh = go.Behaviors;
                for (int k = 0; k < bh.Count; k++)
                    if (bh[k] is MeshRenderer cmr) { if (!cmr.Enabled) return; break; }
            }

            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

            Terrain? shadowTerrain = null;
            foreach (var b in go.Behaviors)
            {
                if (shadowTerrain == null && b is Terrain st && st.Enabled) shadowTerrain = st;
            }
            if (shadowTerrain == null && go.Parent != null)
            {
                foreach (var b in go.Parent.Behaviors)
                    if (b is Terrain st && st.Enabled) { shadowTerrain = st; break; }
            }

            // Pair MeshFilters with MeshRenderers in order (no allocations).
            var behaviors = go.Behaviors;
            int nextMR = 0;
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is MeshFilter f && f.Enabled)
                {
                    var mesh = f.Mesh;
                    if (mesh == null) continue;
                    if (!TryPairMeshRendererForFilter(behaviors, ref nextMR, mesh, out var mr) || mr == null || !mr.CastShadows)
                        continue;

                    if (shadowTerrain != null && ReferenceEquals(go, shadowTerrain.gameObject) && shadowTerrain.IsChunkedRenderingActive)
                        continue;

                    var sph = GetMeshSphere(mesh);
                    if (!SphereInsideFrustum(ref sph, world, planes)) continue;

                    var gpuMesh = cache.GetMesh(mesh);
                    var mvp = world * lightVP;

                    // Handle skinned mesh bone matrices in shadow pass
                    var skinned = mr as SkinnedMeshRenderer;
                    if (skinned != null)
                        skinned.EnsureBoneMatrices();

                    // First-class Surface ShadowCaster pass (for AlphaTest cutout surface shaders)
                    var shadowMat = mr.Material;
                    if (shadowMat != null && !string.IsNullOrWhiteSpace(shadowMat.ShaderAssetPath))
                    {
                        var absShader = ResolveShaderAbsPath(shadowMat.ShaderAssetPath);
                        var schema = ShaderFileParser.GetCachedSchema(absShader);
                        if (schema != null && schema.IsSurfaceShader && schema.RenderState.Queue == ShaderRenderQueue.AlphaTest)
                        {
                            var lib = ShaderLibrary.GetOrCreate(gl);
                            var surfShadow = lib.GetCustom(absShader!, ShaderPassType.ShadowCaster);
                            if (surfShadow != null)
                            {
                                surfShadow.Use();
                                surfShadow.SetMatrix4("uMVP", mvp);
                                surfShadow.SetMatrix4("uModel", world);
                                SN.Matrix4x4.Invert(world, out var invW);
                                surfShadow.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invW));
                                surfShadow.SetFloat("uRoughness", shadowMat.Roughness);
                                surfShadow.SetFloat("uMetallic", shadowMat.Metallic);

                                if (skinned != null && skinned.HasValidBoneMatrices)
                                {
                                    surfShadow.SetInt("uHasBones", 1);
                                    var bones = skinned.BoneMatrices!;
                                    var globals = GlobalUniforms.ForContext(gl);
                                    int boneCount = System.Math.Min(bones.Length, globals.MaxBones);
                                    globals.UpdateBones(bones.AsSpan(0, boneCount));
                                }
                                else
                                {
                                    surfShadow.SetInt("uHasBones", 0);
                                }

                                BindCustomStandardTextures(surfShadow, shadowMat, cache);
                                MaterialBinder.BindProperties(surfShadow, shadowMat, schema, cache);

                                if (mr.DoubleSided || schema.RenderState.Cull == ShaderCullMode.Off)
                                    gl.Disable(EnableCap.CullFace);
                                else
                                {
                                    gl.Enable(EnableCap.CullFace);
                                    gl.CullFace(TriangleFace.Front);
                                }

                                gpuMesh.Draw();
                                depthShader.Use();
                                continue;
                            }
                        }
                    }

                    depthShader.SetMatrix4("uMVP", mvp);

                    if (skinned != null && skinned.HasValidBoneMatrices)
                    {
                        depthShader.SetInt("uHasBones", 1);
                        var bones = skinned.BoneMatrices!;
                        var globals = GlobalUniforms.ForContext(gl);
                        int boneCount = System.Math.Min(bones.Length, globals.MaxBones);
                        globals.UpdateBones(bones.AsSpan(0, boneCount));
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
            public readonly float CloudScale;
            public readonly float CloudDetail;
            public readonly float CloudSpeed;
            public readonly float CloudSoftness;
            public readonly float CloudLightResponse;
            public readonly float CloudSilverLining;
            public readonly SN.Vector3 CloudColor;
            public readonly SN.Vector3 CloudShadowColor;
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
                float cloudScale,
                float cloudDetail,
                float cloudSpeed,
                float cloudSoftness,
                float cloudLightResponse,
                float cloudSilverLining,
                SN.Vector3 cloudColor,
                SN.Vector3 cloudShadowColor,
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
                CloudScale = cloudScale;
                CloudDetail = cloudDetail;
                CloudSpeed = cloudSpeed;
                CloudSoftness = cloudSoftness;
                CloudLightResponse = cloudLightResponse;
                CloudSilverLining = cloudSilverLining;
                CloudColor = cloudColor;
                CloudShadowColor = cloudShadowColor;
                CloudStepCount = cloudStepCount;
            }
        }

        /// <summary>
        /// Props on a planet were lit with the flat PBR sun while terrain uses a radial
        /// Lambert term, so placed meshes went nearly black. When the camera is inside
        /// an atmosphere shell, shaders add a sky fill along the surface normal.
        /// </summary>
        static void SetPlanetPropFill(ShaderProgram shader, SN.Vector3 camPosWorld)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled)
                    continue;
                var atmo = planet.Atmosphere;
                if (atmo == null || !atmo.Enabled)
                    continue;

                var p = planet.gameObject.Transform.Position;
                var center = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
                float groundR = atmo.GroundRadiusOverride > 0.01f ? atmo.GroundRadiusOverride : planet.Config.Radius;
                float worldR = planet.Config.EffectiveWorldRadius;
                float atmoR = Math.Max(groundR, worldR) + Math.Max(1f, atmo.AtmosphereHeight);
                if ((camPosWorld - center).LengthSquared() > atmoR * atmoR)
                    continue;

                shader.SetInt("uHasPlanetFill", 1);
                shader.SetVector3("uPlanetCenter", center);
                return;
            }

            shader.SetInt("uHasPlanetFill", 0);
        }

        /// <summary>
        /// When the camera lies inside an active planet's atmosphere shell, replaces
        /// <paramref name="ambient"/> with the same ambient term used by
        /// <see cref="RenderPlanetTerrain"/> / <see cref="ResolvePlanetAtmosphere"/> so
        /// standard forward materials (imported trees, props) match planet terrain lighting.
        /// </summary>
        /// <returns><c>true</c> if a matching planet atmosphere was found and applied.</returns>
        public static bool TryApplyPlanetAtmosphereAmbient(
            SN.Vector3 camPosWorld,
            Light? sceneLight,
            ref float ambient)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled)
                    continue;
                var atmo = planet.Atmosphere;
                if (atmo == null || !atmo.Enabled)
                    continue;

                var p = planet.gameObject.Transform.Position;
                var center = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
                float groundR = atmo.GroundRadiusOverride > 0.01f ? atmo.GroundRadiusOverride : planet.Config.Radius;
                float atmoR = groundR + Math.Max(1f, atmo.AtmosphereHeight);
                if ((camPosWorld - center).LengthSquared() > atmoR * atmoR)
                    continue;

                // Sun fallback only affects sky-tint fields inside Resolve; ambient comes from atmosphere.
                var prm = ResolvePlanetAtmosphere(planet, sceneLight, SN.Vector3.UnitY, ambient);
                ambient = prm.Ambient;
                return true;
            }

            return false;
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
                cloudBaseHeight: Math.Max(1f, atmo?.CloudBaseHeight ?? 120f),
                cloudTopHeight: Math.Max(2f, atmo?.CloudTopHeight ?? 220f),
                cloudCoverage: Math.Clamp(atmo?.CloudCoverage ?? 0.46f, 0f, 1f),
                cloudDensity: Math.Max(0f, atmo?.CloudDensity ?? 1.0f),
                cloudScale: Math.Clamp(atmo?.CloudScale ?? 3.5f, 0.25f, 24f),
                cloudDetail: Math.Max(0.1f, atmo?.CloudDetail ?? 2.0f),
                cloudSpeed: Math.Max(0f, atmo?.CloudSpeed ?? 0.025f),
                cloudSoftness: Math.Clamp(atmo?.CloudSoftness ?? 0.30f, 0.01f, 1.0f),
                cloudLightResponse: Math.Max(0f, atmo?.CloudLightResponse ?? 0.9f),
                cloudSilverLining: Math.Max(0f, atmo?.CloudSilverLining ?? 0.65f),
                cloudColor: atmo?.CloudColor ?? new SN.Vector3(1.0f, 0.99f, 0.97f),
                cloudShadowColor: atmo?.CloudShadowColor ?? new SN.Vector3(0.36f, 0.44f, 0.58f),
#if XBOX_UWP
                cloudStepCount: Math.Clamp(Math.Min(atmo?.CloudStepCount ?? 16, 10), 4, 10));
#else
                cloudStepCount: Math.Clamp(atmo?.CloudStepCount ?? 16, 4, 24));
#endif
        }

        /// <summary>
        /// Draw streamed planet grass/trees on top of the planet crust mesh.
        /// Must run after <see cref="RenderPlanetTerrain"/> so terrain does not overpaint vegetation.
        /// </summary>
        public static void RenderPlanetVegetationAfterTerrain(
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
            bool isES = true,
            SN.Vector3 lightColor = default,
            CascadedShadowMap? cascadedShadow = null,
            bool allowWholePlanetForestCanopies = false)
        {
            // Instanced grass, near-ring GPU trees, and whole-planet canopies are not scene meshes.
            // The dedicated pass also draws GameObject tree meshes; with that pass off, GameObject
            // trees still come from the main scene walk, but instanced layers must draw here at all altitudes.
            if (!PlanetVegetationSystem.AnyUseDedicatedRenderPass)
            {
                var effLightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor;
                if (allowWholePlanetForestCanopies)
                    RenderPlanetWholePlanetForest(gl, cache, view, proj, camPos, lightDir, ambient, diffuseK, effLightColor);
                RenderPlanetGpuTreeBatches(
                    gl, standardShader, cache, view, proj, camPos,
                    lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                    shadowFBO, shadowVP, sunShineDir, effLightColor, cascadedShadow);
                RenderPlanetGpuGrassBatches(gl, cache, view, proj, camPos, lightDir, ambient, diffuseK, effLightColor);
                return;
            }

            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);
            var globals = GlobalUniforms.ForContext(gl);
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
                ShadowFBO = shadowFBO,
                CascadedShadow = cascadedShadow,
                SunShineDir = sunShineDir,
                StandardShader = standardShader,
                Cache = cache,
                Globals = globals,
                IsES = isES,
                PlanetFoliagePass = true
            };
            RenderPlanetVegetation(
                gl, standardShader, cache, view, proj, planes, camPos,
                lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunShineDir, in renderCtx, allowWholePlanetForestCanopies);
        }

        static void RenderPlanetVegetation(
            GL gl,
            ShaderProgram standardShader,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            Plane[] planes,
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
            in RenderContext renderCtx,
            bool allowWholePlanetForestCanopies)
        {
            if (!PlanetVegetationSystem.AnyUseDedicatedRenderPass)
                return;

            var vegOpaque = s_planetVegOpaque ??= new List<DrawItem>(512);
            var vegTransparent = s_planetVegTransparent ??= new List<DrawItem>(64);
            vegOpaque.Clear();
            vegTransparent.Clear();

            var systems = PlanetVegetationSystem.ActiveSystems;
            bool prevSkip = SkipPlanetVegetationDraws;
            SkipPlanetVegetationDraws = false;
            try
            {
                for (int s = 0; s < systems.Count; s++)
                {
                    var sys = systems[s];
                    if (sys == null || !sys.IsActiveAndEnabled || !sys.UseDedicatedRenderPass)
                        continue;

                    var root = sys.RuntimeDrawRoot;
                    if (root == null || sys.TerrainGameObject == null)
                        continue;

                    var planetWorld = TransformUtil.WorldFromTransform(sys.TerrainGameObject.Transform);
                    GatherDrawItems(root, planetWorld, view, proj, planes, vegOpaque, vegTransparent);
                }
            }
            finally
            {
                SkipPlanetVegetationDraws = prevSkip;
            }

            if (vegOpaque.Count > 0 || vegTransparent.Count > 0)
            {
            bool hasShadow = renderCtx.CascadedShadow != null || shadowFBO?.DepthTexture != null;
            var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                ? SN.Vector3.Normalize(WindSystem.Direction)
                : SN.Vector3.UnitX;
            GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
            renderCtx.Globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
            renderCtx.Globals.UpdateLighting(
                lightDir, diffuseK, renderCtx.LightColor, ambient,
                lightIsPoint, lightPosW, lightRange,
                hasShadow, in shadowVP,
                cascadeCount: hasShadow ? 1 : 0,
                cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
                windDir: windDir,
                windTime: WindSystem.Time);
            BindNearestPointLights(renderCtx.Globals, camPos);

            standardShader.Use();
            standardShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
            standardShader.SetInt("uDebugViewMode", (int)DebugViewMode);
            if (hasShadow)
            {
                standardShader.SetFloat("uShadowBias", 0.008f);
                standardShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(standardShader, shadowFBO, renderCtx.CascadedShadow, 7);
            }
            BindEnvMapToShader(gl, standardShader, camPos, 12);

            gl.Enable(EnableCap.DepthTest);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
            gl.Enable(EnableCap.PolygonOffsetFill);
            gl.PolygonOffset(-1f, -1f);

            for (int i = 0; i < vegOpaque.Count; i++)
                DrawMeshItem(gl, standardShader, cache, vegOpaque[i], in renderCtx);

            gl.Disable(EnableCap.PolygonOffsetFill);

            if (vegTransparent.Count > 0)
            {
                vegTransparent.Sort((a, b) => b.SortZ.CompareTo(a.SortZ));
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);
                // Glass is a second surface in the wall's plane. ANGLE's depth buffer is coarser than
                // desktop GL, and Less rejects that layer so the windows stay empty.
                gl.DepthFunc(DepthFunction.Lequal);
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(-2f, -2f);
                for (int i = 0; i < vegTransparent.Count; i++)
                    DrawMeshItem(gl, standardShader, cache, vegTransparent[i], in renderCtx);
                gl.DepthFunc(DepthFunction.Less);
                gl.Disable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(0f, 0f);
                gl.DepthMask(true);
                gl.Disable(EnableCap.Blend);
            }
            }

            if (allowWholePlanetForestCanopies)
                RenderPlanetWholePlanetForest(gl, cache, view, proj, camPos, lightDir, ambient, diffuseK, renderCtx.LightColor);
            RenderPlanetGpuTreeBatches(
                gl, standardShader, cache, view, proj, camPos,
                lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunShineDir, renderCtx.LightColor, renderCtx.CascadedShadow);
            PlanetScatterRenderer.RenderAll(
                gl, standardShader, cache, view, proj, camPos,
                lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFBO, shadowVP, sunShineDir, renderCtx.LightColor, renderCtx.CascadedShadow,
                HdrOutputActive, (int)DebugViewMode);
            RenderPlanetGpuGrassBatches(gl, cache, view, proj, camPos, lightDir, ambient, diffuseK, renderCtx.LightColor);
        }

        static void RenderPlanetWholePlanetForest(
            GL gl,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            SN.Vector3 lightDir,
            float ambient,
            float diffuseK,
            SN.Vector3 lightColor)
        {
            var planets = PlanetTerrain.ActivePlanets;
            if (planets.Count == 0) return;

            var systems = PlanetVegetationSystem.ActiveSystems;
            for (int p = 0; p < planets.Count; p++)
            {
                var terrain = planets[p];
                if (terrain?.gameObject == null || terrain.Config == null || !terrain.IsActiveAndEnabled || !terrain.HasBakedStandSurface)
                    continue;

                // Tier 2 low-poly canopies are only for very long-range / orbital SceneView inspection.
                float planetWorldRadius = Math.Max(1f, terrain.Config.EffectiveWorldRadius) * Math.Max(0.0001f, terrain.GetWorldRadiusScale());
                float camAltitude = (camPos - terrain.GetWorldCenter()).Length() - planetWorldRadius;
                float minOrbitalAltitude = MathF.Min(450f, planetWorldRadius * 0.45f);
                if (camAltitude < minOrbitalAltitude)
                    continue;

                PlanetVegetationSystem? veg = null;
                for (int s = 0; s < systems.Count; s++)
                {
                    if (ReferenceEquals(systems[s]?.TerrainGameObject, terrain.gameObject))
                    {
                        veg = systems[s];
                        break;
                    }
                }

                var planetWorld = TransformUtil.WorldFromTransform(terrain.gameObject.Transform);
                SN.Vector3 nearAnchorLocal = SN.Vector3.Zero;
                float crossInner = 0f, crossOuter = 0f;
                veg?.GetNearForestCrossfade(camPos, out nearAnchorLocal, out crossInner, out crossOuter);

                PlanetWholePlanetForest.Render(
                    gl, cache, terrain, veg, planetWorld, view, proj, camPos,
                    lightDir, ambient, diffuseK, lightColor,
                    nearAnchorLocal, crossInner, crossOuter, HdrOutputActive);
            }
        }

        static void RenderPlanetGpuTreeBatches(
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
            SN.Vector3 lightColor,
            CascadedShadowMap? cascadedShadow)
        {
            var systems = PlanetVegetationSystem.ActiveSystems;
            for (int s = 0; s < systems.Count; s++)
            {
                var sys = systems[s];
                if (sys?.TerrainGameObject == null || !sys.IsActiveAndEnabled)
                    continue;
                sys.EnsurePrebuiltWholePlanetTrees();
                var planetWorld = TransformUtil.WorldFromTransform(sys.TerrainGameObject.Transform);
                PlanetGpuTrees.Render(
                    gl, standardShader, cache, sys, planetWorld, view, proj, camPos,
                    lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                    shadowFBO, shadowVP, sunShineDir, lightColor, cascadedShadow,
                    HdrOutputActive, (int)DebugViewMode);
            }
        }

        static void RenderPlanetGpuGrassBatches(
            GL gl,
            ResourceCache cache,
            in SN.Matrix4x4 view,
            in SN.Matrix4x4 proj,
            SN.Vector3 camPos,
            SN.Vector3 lightDir,
            float ambient,
            float diffuseK,
            SN.Vector3 lightColor)
        {
            var systems = PlanetVegetationSystem.ActiveSystems;
            for (int s = 0; s < systems.Count; s++)
            {
                var sys = systems[s];
                if (sys?.TerrainGameObject == null || !sys.IsActiveAndEnabled)
                    continue;
                var planetWorld = TransformUtil.WorldFromTransform(sys.TerrainGameObject.Transform);
                PlanetGpuGrass.Render(gl, cache, sys, planetWorld, view, proj, camPos, lightDir, ambient, diffuseK, lightColor);
            }
        }

        [ThreadStatic] private static List<DrawItem>? s_planetVegOpaque;
        [ThreadStatic] private static List<DrawItem>? s_planetVegTransparent;

        /// <summary>
        /// Forward-draw opaque scene meshes after planet terrain so cubes/pickups
        /// are not buried by the planet pass (deferred color is overwritten, then
        /// planet rasterizes without those objects in its depth).
        /// </summary>
        public static void RenderForwardSceneOpaques(
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
            bool isES = true,
            SN.Vector3 lightColor = default,
            CascadedShadowMap? cascadedShadow = null)
        {
            var viewProj = view * proj;
            var planes = s_planes ??= new Plane[6];
            ExtractFrustumPlanes(viewProj, planes);

            var opaqueItems = s_opaqueItems ??= new List<DrawItem>(256);
            var transparentItems = s_transparentItems ??= new List<DrawItem>(64);
            opaqueItems.Clear();
            transparentItems.Clear();

            bool skipPlanetVegInSceneGraph = PlanetVegetationSystem.AnyUseDedicatedRenderPass;
            var prevSkip = SkipPlanetVegetationDraws;
            SkipPlanetVegetationDraws = skipPlanetVegInSceneGraph || prevSkip;
            foreach (var root in SceneService.Root)
                GatherDrawItems(root, SN.Matrix4x4.Identity, view, proj, planes, opaqueItems, transparentItems);
            SkipPlanetVegetationDraws = prevSkip;

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            var globals = GlobalUniforms.ForContext(gl);
            var resolvedLightColor = lightColor == default ? new SN.Vector3(1f, 1f, 1f) : lightColor;
            bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;
            var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
                ? SN.Vector3.Normalize(WindSystem.Direction)
                : SN.Vector3.UnitX;

            GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
            globals.UpdateLighting(
                lightDir, diffuseK, resolvedLightColor, ambient,
                lightIsPoint, lightPosW, lightRange,
                hasShadow, in shadowVP,
                cascadeCount: hasShadow ? 1 : 0,
                cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
                windDir: windDir,
                windTime: WindSystem.Time);
            BindNearestPointLights(globals, camPos);

            standardShader.Use();
            standardShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
            standardShader.SetInt("uDebugViewMode", (int)DebugViewMode);

            if (hasShadow)
            {
                standardShader.SetFloat("uShadowBias", 0.008f);
                standardShader.SetVector3("uSunDir", sunShineDir);
                BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
            }
            BindEnvMapToShader(gl, standardShader, camPos, 12);

            var renderCtx = new RenderContext
            {
                View = view,
                Proj = proj,
                CamPos = camPos,
                LightDir = lightDir,
                LightColor = resolvedLightColor,
                DiffuseK = diffuseK,
                Ambient = ambient,
                ShadowVP = shadowVP,
                ShadowFBO = shadowFBO,
                CascadedShadow = cascadedShadow,
                SunShineDir = sunShineDir,
                StandardShader = standardShader,
                Cache = cache,
                Globals = globals,
                IsES = isES
            };

            DrawOpaqueItemsWithAutoInstancing(
                gl, standardShader, cache, opaqueItems, in renderCtx,
                skipTerrainWithLayers: false,
                skipAllTerrain: true,
                isGBufferPass: false);

            gl.DepthFunc(DepthFunction.Less);
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
            in SN.Matrix4x4 shadowVP,
            CascadedShadowMap? cascadedShadow = null)
        {
            if (planet.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled)
                return;

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.DepthMask(true);

            var vp = view * proj;
            ExtractFrustumPlanes(vp, out var frustumPlanes);

            var globals = GlobalUniforms.ForContext(gl);
            bool hasShadow = cascadedShadow != null || shadowFBO != null;
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time);
            globals.UpdateLighting(
                lightDir, diffuseK, SN.Vector3.One, atmo.Ambient,
                lightIsPoint: false, SN.Vector3.Zero, 0f,
                hasShadow: hasShadow, in shadowVP);
            BindNearestPointLights(globals, camPos);

            planetShader.Use();
            planetShader.SetInt("uHdrOutput", HdrOutputActive ? 1 : 0);
            planetShader.SetVector3("uPlanetCenter", planetCenter);
            float radiusWorld = MathF.Max(1f, planet.Config.EffectiveWorldRadius);
            if (radiusWorld < 2f)
                radiusWorld = MathF.Max(1f, atmo.GroundRadius);
            planetShader.SetFloat("uPlanetRadius", radiusWorld);
            ResolveWeatherOverlays(planet, out float wetness, out float snowCoverage, out float weatherOn);
            planetShader.SetFloat("uWetness", wetness);
            planetShader.SetFloat("uSnowCoverage", snowCoverage);
            planetShader.SetFloat("uWeatherEnabled", weatherOn);

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

            // Units 7..10: 4-cascade CSM shadows (or single shadowFBO / safe 2D fallback so
            // no sampler2D shadow uniform remains on unit 0 where uBiomeTexArray is sampler2DArray).
            BindShadowMapsToShader(
                planetShader,
                hasShadow ? shadowFBO : null,
                hasShadow ? cascadedShadow : null,
                7,
                cache);

            BindBiomeTextures(gl, planetShader, cache, planet.Config.Biomes);
            BindPlanetSplatAtlases(planetShader, cache, planet);

            var go = planet.gameObject;
            var parentWorld = TransformUtil.WorldFromTransform(go.Transform);
            var renderableLeaves = planet.ChunkManager?.GetRenderableLeaves();
            if (renderableLeaves == null)
            {
                gl.DepthFunc(DepthFunction.Less);
                return;
            }

            float camRadial = (camPos - planetCenter).Length();
            bool belowCrust = planet.Config.CameraBelowCrust || camRadial < radiusWorld * 0.97f;
            if (belowCrust)
                gl.Disable(EnableCap.CullFace);
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Back);
            }

            for (int i = 0; i < renderableLeaves.Count; i++)
            {
                var leaf = renderableLeaves[i];
                var mesh = leaf.GeneratedMesh;
                if (mesh == null) continue;

                var world = parentWorld;
                mesh.GetLocalBounds(out var bMin, out var bMax);
                var localCenter = (bMin + bMax) * 0.5f;
                var worldCenter = SN.Vector3.Transform(localCenter, world);
                var sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
                var sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
                var sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
                float leafRadius = (bMax - bMin).Length() * 0.5f * MathF.Max(sx, MathF.Max(sy, sz));
                if (!SphereInFrustum(frustumPlanes, worldCenter, leafRadius + 2f))
                    continue;

                planetShader.SetMatrix4("uModel", world);
                SN.Matrix4x4.Invert(world, out var invWorld);
                planetShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

                var gpuMesh = cache.GetMesh(mesh);
                gpuMesh.Draw();

                var cave = leaf.GeneratedCaveMesh;
                if (cave != null && planet.Config.CameraBelowCrust)
                    cache.GetMesh(cave).Draw();
            }

            gl.DepthFunc(DepthFunction.Less);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
        }

        static void BindPlanetSplatAtlases(ShaderProgram shader, ResourceCache cache, PlanetTerrain planet)
        {
            // Units 2, 3, 4: uPlanetSplat0, uPlanetSplat1, uPlanetDigAtlas.
            // Always assign these sampler2D uniforms away from unit 0 (which holds sampler2DArray uBiomeTexArray)
            // even on early-return paths so OpenGL ES 3.0 / ANGLE never sees a sampler type collision on unit 0.
            var surface = planet.SurfaceCubemap;
            if (surface == null)
            {
                var white = cache.GetWhiteTexture();
                white.Bind(TextureUnit.Texture2);
                shader.SetTexture("uPlanetSplat0", 2);
                white.Bind(TextureUnit.Texture3);
                shader.SetTexture("uPlanetSplat1", 3);
#if !XBOX_UWP
                cache.GetBlackTexture().Bind(TextureUnit.Texture4);
                shader.SetTexture("uPlanetDigAtlas", 4);
#endif
                shader.SetInt("uUsePlanetSplat", 0);
                shader.SetInt("uUsePlanetDig", 0);
                return;
            }

#if XBOX_UWP
            // Dig stays off (R32F atlas). Splat paints rock versus dirt. One cubemap face
            // per frame (12 frames total) so SwapBuffers survives Dev Mode App budgets.
            shader.SetInt("uUsePlanetDig", 0);
            if (!surface.HasBaseHeights || surface.Splat0 == null || surface.Splat1 == null)
            {
                var white = cache.GetWhiteTexture();
                white.Bind(TextureUnit.Texture2);
                shader.SetTexture("uPlanetSplat0", 2);
                white.Bind(TextureUnit.Texture3);
                shader.SetTexture("uPlanetSplat1", 3);
                shader.SetInt("uUsePlanetSplat", 0);
                return;
            }

            int xboxSplatVersion = surface.SplatVersion;
            var (xboxSplat0, xboxSplat1, xboxNeedsUpload) = cache.GetPlanetSplatTextures(planet, xboxSplatVersion);
            if (xboxNeedsUpload)
            {
                if (_planetSplatUploadVersion != xboxSplatVersion)
                {
                    _planetSplatUploadVersion = xboxSplatVersion;
                    _planetSplatUploadFace = 0;
                    xboxSplat0.EnsureRgba8AtlasStorage(surface.Resolution);
                    xboxSplat1.EnsureRgba8AtlasStorage(surface.Resolution);
                }

                int res = surface.Resolution;
                int face = _planetSplatUploadFace;
                if (face < 6)
                    xboxSplat0.UploadRgba8AtlasFace(surface.Splat0[face], res, face);
                else if (face < 12)
                    xboxSplat1.UploadRgba8AtlasFace(surface.Splat1[face - 6], res, face - 6);

                _planetSplatUploadFace = face + 1;
                if (_planetSplatUploadFace >= 12)
                {
                    cache.SetPlanetSplatVersion(planet, xboxSplatVersion);
                    _planetSplatUploadFace = 0;
                    _planetSplatUploadPhase = 0;
                    Log.Info($"[Planet] Xbox splat atlases uploaded {res}px (12 faces)");
                }
                else
                {
                    var pendingWhite = cache.GetWhiteTexture();
                    pendingWhite.Bind(TextureUnit.Texture2);
                    shader.SetTexture("uPlanetSplat0", 2);
                    pendingWhite.Bind(TextureUnit.Texture3);
                    shader.SetTexture("uPlanetSplat1", 3);
                    shader.SetInt("uUsePlanetSplat", 0);
                    return;
                }
            }

            xboxSplat0.Bind(TextureUnit.Texture2);
            shader.SetTexture("uPlanetSplat0", 2);
            xboxSplat1.Bind(TextureUnit.Texture3);
            shader.SetTexture("uPlanetSplat1", 3);
            shader.SetInt("uUsePlanetSplat", 1);
            return;
#else
            // Scratch cubemap during an in-flight graph bake has empty splat —
            // keep vertex biome blends so the first dig cannot paint the planet dirt.
            if (!surface.HasBaseHeights)
            {
                var pendingWhite = cache.GetWhiteTexture();
                pendingWhite.Bind(TextureUnit.Texture2);
                shader.SetTexture("uPlanetSplat0", 2);
                pendingWhite.Bind(TextureUnit.Texture3);
                shader.SetTexture("uPlanetSplat1", 3);
                shader.SetInt("uUsePlanetSplat", 0);
                int pendingHeightVersion = surface.HeightVersion;
                var (pendingDig, pendingDigUpload) = cache.GetPlanetHeightTexture(planet, pendingHeightVersion);
                if (pendingDigUpload)
                {
                    pendingDig.UploadRedFloatFaces(surface.HeightDelta, surface.Resolution);
                    cache.SetPlanetHeightVersion(planet, pendingHeightVersion);
                }
                pendingDig.Bind(TextureUnit.Texture4);
                shader.SetTexture("uPlanetDigAtlas", 4);
                shader.SetInt("uUsePlanetDig", 1);
                return;
            }

            int splatVersion = surface.SplatVersion;
            var (splat0, splat1, needsUpload) = cache.GetPlanetSplatTextures(planet, splatVersion);
            if (needsUpload)
            {
                int res = surface.Resolution;
                splat0.UploadRgba16FloatFaces(surface.Splat0, res);
                splat1.UploadRgba16FloatFaces(surface.Splat1, res);
                cache.SetPlanetSplatVersion(planet, splatVersion);
            }

            splat0.Bind(TextureUnit.Texture2);
            shader.SetTexture("uPlanetSplat0", 2);
            splat1.Bind(TextureUnit.Texture3);
            shader.SetTexture("uPlanetSplat1", 3);
            shader.SetInt("uUsePlanetSplat", 1);

            int heightVersion = surface.HeightVersion;
            var (digAtlas, digNeedsUpload) = cache.GetPlanetHeightTexture(planet, heightVersion);
            if (digNeedsUpload)
            {
                int res = surface.Resolution;
                digAtlas.UploadRedFloatFaces(surface.HeightDelta, res);
                cache.SetPlanetHeightVersion(planet, heightVersion);
            }
            digAtlas.Bind(TextureUnit.Texture4);
            shader.SetTexture("uPlanetDigAtlas", 4);
            shader.SetInt("uUsePlanetDig", 1);
#endif
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
        private static Texture2D?[]? _boundBiomeUnderTex2D;
        private static string?[]? _boundBiomePaths;
        private static string?[]? _boundBiomeUnderPaths;
        private static bool _biomeTexDirty = true;
        private static bool _biomeMetaReady;
        private static int _biomeLoadCursor;
        private static int _biomeLoadFrame = -1;
        private static int _planetSplatUploadPhase; // Xbox legacy; face index uses _planetSplatUploadFace
        private static int _planetSplatUploadFace; // Xbox: 0..11 (splat0 faces, then splat1)
        private static int _planetSplatUploadVersion = int.MinValue;

        static int _xboxVegUnlockFrame;

        /// <summary>
        /// Xbox: true while biome decode, height bake, or splat atlas faces are still landing.
        /// </summary>
        public static bool XboxHoldHeavyWork
        {
            get
            {
#if !XBOX_UWP
                return false;
#else
                if (BiomeTexturesStreaming)
                    return true;
                if (_planetSplatUploadFace > 0)
                    return true;
                foreach (var planet in PlanetTerrain.ActivePlanets)
                {
                    if (planet?.Config == null) continue;
                    if (planet.SurfaceBakePending)
                        return true;
                    var surface = planet.SurfaceCubemap;
                    if (surface == null || !surface.HasBaseHeights)
                        return true;
                }
                return false;
#endif
            }
        }

        /// <summary>
        /// Xbox: hold plant spawn/draw through settle, then ~3s more so the first
        /// vegetation wave cannot dump dozens of mesh/texture uploads in one SwapBuffers.
        /// </summary>
        public static bool XboxHoldVegetation
        {
            get
            {
#if !XBOX_UWP
                return false;
#else
                if (XboxHoldHeavyWork)
                {
                    _xboxVegUnlockFrame = 0;
                    return true;
                }
                if (_xboxVegUnlockFrame == 0)
                    _xboxVegUnlockFrame = Time.frameCount + 90;
                return Time.frameCount < _xboxVegUnlockFrame;
#endif
            }
        }
        private static float[]? _cachedTiling;
        private static float[]? _cachedUnderTiling;
        private static SN.Vector3[]? _cachedBaseColor;
        private static SN.Vector3[]? _cachedUnderColor;
        private static int[]? _cachedHasUnder;
        // Under maps share the 16-unit GLES budget with top/splat/dig/shadow: biomes 0-3.
        private const int MaxBoundUnderBiomes = 4;
#if XBOX_UWP
        private const int BiomeCpuMaxEdge = 1024;
#else
        private const int BiomeCpuMaxEdge = 2048;
#endif

        public static void ResetBiomeTexDebug()
        {
            _biomeTexDirty = true;
            _biomeMetaReady = false;
            _biomeLoadCursor = 0;
            _biomeLoadFrame = -1;
        }

        /// <summary>True while planet biome CPU textures are still trickling in one-per-frame.</summary>
        public static bool BiomeTexturesStreaming => _biomeTexDirty;

        static void ResolveWeatherOverlays(PlanetTerrain planet, out float wetness, out float snowCoverage, out float weatherOn)
        {
            wetness = 0f;
            snowCoverage = 0f;
            weatherOn = 0f;
            if (UnderwaterQuery.AnyPlayerPlanetSubmerged())
                return;

            var behaviors = planet.gameObject?.Behaviors;
            if (behaviors == null)
                return;

            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is not PlanetWeatherController wx || !wx.EnableWeather || !wx.IsActiveAndEnabled)
                    continue;
                wetness = Math.Max(wx.Wetness, wx.RainIntensity * 0.92f);
                snowCoverage = wx.SnowCoverage;
                weatherOn = (wetness > 0.02f || snowCoverage > 0.02f || wx.RainIntensity > 0.05f) ? 1f : 0f;
                return;
            }
        }

        private static void BindBiomeTextures(GL gl, ShaderProgram shader, ResourceCache cache,
            Game_Engine.Core.Biome.BiomeDefinition[] biomes)
        {
            if (_boundBiomeTex2D == null)
            {
                _boundBiomeTex2D = new Texture2D?[8];
                _boundBiomeUnderTex2D = new Texture2D?[8];
                _boundBiomePaths = new string[8];
                _boundBiomeUnderPaths = new string[8];
                _cachedTiling = new float[8];
                _cachedUnderTiling = new float[8];
                _cachedBaseColor = new SN.Vector3[8];
                _cachedUnderColor = new SN.Vector3[8];
                _cachedHasUnder = new int[8];
            }

#if XBOX_UWP
            // Console: one biome top+under decode per frame so the first SwapBuffers survives.
            bool pathsChanged = false;
            for (int i = 0; i < 8 && i < biomes.Length; i++)
            {
                if (_boundBiomePaths![i] != biomes[i].TopTexturePath
                    || _boundBiomeUnderPaths![i] != biomes[i].UnderTexturePath)
                { pathsChanged = true; break; }
            }
            if (_biomeTexDirty || pathsChanged)
            {
                if (!_biomeMetaReady || pathsChanged)
                {
                    Log.Info("[BiomeTex] Rebuilding biome texture bindings (staggered)...");
                    for (int i = 0; i < 8; i++)
                    {
                        if (i < biomes.Length)
                        {
                            var b = biomes[i];
                            _boundBiomePaths![i] = b.TopTexturePath;
                            _boundBiomeUnderPaths![i] = b.UnderTexturePath;
                            _cachedTiling![i] = b.TopTiling;
                            _cachedUnderTiling![i] = Math.Max(0.25f, b.UnderTiling);
                            _cachedBaseColor![i] = new SN.Vector3(b.BaseColorR, b.BaseColorG, b.BaseColorB);
                            _cachedUnderColor![i] = new SN.Vector3(
                                Math.Max(b.BaseColorR * 0.6f, 0.12f),
                                Math.Max(b.BaseColorG * 0.5f, 0.1f),
                                Math.Max(b.BaseColorB * 0.5f, 0.1f));
                        }
                        else
                        {
                            _boundBiomePaths![i] = "";
                            _boundBiomeUnderPaths![i] = "";
                            _cachedTiling![i] = 24f;
                            _cachedUnderTiling![i] = 10f;
                            _cachedBaseColor![i] = new SN.Vector3(0.5f);
                            _cachedUnderColor![i] = new SN.Vector3(0.22f);
                        }
                        _boundBiomeTex2D[i] = null;
                        _boundBiomeUnderTex2D![i] = null;
                        _cachedHasUnder![i] = 0;
                    }
                    _biomeLoadCursor = 0;
                    _biomeLoadFrame = -1;
                    _biomeMetaReady = true;
                    _biomeTexDirty = true;
                }

                int biomeCount = Math.Min(8, biomes.Length);
                if (_biomeLoadCursor < biomeCount && _biomeLoadFrame != Time.frameCount)
                {
                    _biomeLoadFrame = Time.frameCount;
                    int i = _biomeLoadCursor;
                    var b = biomes[i];
                    string top = b.TopTexturePath ?? "";
                    string under = i < MaxBoundUnderBiomes ? (b.UnderTexturePath ?? "") : "";
                    bool topReady = top.Length == 0 || s_asyncBiomeTex.ContainsKey(top);
                    bool underReady = under.Length == 0 || s_asyncBiomeTex.ContainsKey(under);
                    if (!topReady) RequestBiomeDecode(top);
                    else if (!underReady) RequestBiomeDecode(under);
                    // Decoding a 1024 image on this thread is the 2 second planet-ground frame.
                    if (topReady && underReady)
                    {
                        _biomeLoadCursor++;
                        if (top.Length > 0 && s_asyncBiomeTex.TryGetValue(top, out var tex2d))
                        {
                            _boundBiomeTex2D[i] = tex2d;
                            if (tex2d != null)
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': {tex2d.Width}x{tex2d.Height} OK");
                            else
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': file not found '{top}'");
                        }
                        if (under.Length > 0 && s_asyncBiomeTex.TryGetValue(under, out var underTex) && underTex != null)
                        {
                            _boundBiomeUnderTex2D![i] = underTex;
                            _cachedHasUnder![i] = 1;
                        }
                    }
                }

                if (_biomeLoadCursor >= biomeCount)
                    _biomeTexDirty = false;
            }
#else
            // Desktop editor Game View: load every biome in one rebuild (pre-Xbox behavior).
            bool needRebuild = _biomeTexDirty;
            if (!needRebuild)
            {
                for (int i = 0; i < 8 && i < biomes.Length; i++)
                {
                    if (_boundBiomePaths![i] != biomes[i].TopTexturePath
                        || _boundBiomeUnderPaths![i] != biomes[i].UnderTexturePath)
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
                    _boundBiomeUnderPaths![i] = b.UnderTexturePath;
                    _boundBiomeTex2D[i] = null;
                    _boundBiomeUnderTex2D![i] = null;
                    _cachedHasUnder![i] = 0;

                    if (!string.IsNullOrWhiteSpace(b.TopTexturePath))
                    {
                        try
                        {
                            var tex2d = TryLoadLayerTexture(b.TopTexturePath);
                            if (tex2d != null)
                            {
                                _boundBiomeTex2D[i] = tex2d;
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': {tex2d.Width}x{tex2d.Height} OK");
                            }
                            else
                                Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}': file not found '{b.TopTexturePath}'");
                        }
                        catch (Exception ex)
                        {
                            Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}' EXCEPTION: {ex.Message}");
                        }
                    }

                    _cachedTiling![i] = b.TopTiling;
                    _cachedUnderTiling![i] = Math.Max(0.25f, b.UnderTiling);
                    _cachedBaseColor![i] = new SN.Vector3(b.BaseColorR, b.BaseColorG, b.BaseColorB);

                    var underCol = new SN.Vector3(
                        Math.Max(b.BaseColorR * 0.6f, 0.12f),
                        Math.Max(b.BaseColorG * 0.5f, 0.1f),
                        Math.Max(b.BaseColorB * 0.5f, 0.1f));
                    if (!string.IsNullOrWhiteSpace(b.UnderTexturePath))
                    {
                        try
                        {
                            var underTex = TryLoadLayerTexture(b.UnderTexturePath);
                            if (underTex != null)
                            {
                                if (i < MaxBoundUnderBiomes)
                                {
                                    _boundBiomeUnderTex2D![i] = underTex;
                                    _cachedHasUnder![i] = 1;
                                    Log.Info($"[BiomeTex] Biome[{i}] '{b.Name}' under: {underTex.Width}x{underTex.Height} OK");
                                }
                                else
                                {
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
                                        underCol = new SN.Vector3(
                                            rSum / (samples * 255f), gSum / (samples * 255f), bSum / (samples * 255f));
                                }
                            }
                        }
                        catch { /* fall back to darkened base */ }
                    }
                    _cachedUnderColor![i] = underCol;
                }

                for (int i = biomes.Length; i < 8; i++)
                {
                    _boundBiomePaths![i] = "";
                    _boundBiomeUnderPaths![i] = "";
                    _boundBiomeTex2D[i] = null;
                    _boundBiomeUnderTex2D![i] = null;
                    _cachedHasUnder![i] = 0;
                    _cachedTiling![i] = 24f;
                    _cachedUnderTiling![i] = 10f;
                    _cachedBaseColor![i] = new SN.Vector3(0.5f);
                    _cachedUnderColor![i] = new SN.Vector3(0.22f);
                }

                _biomeMetaReady = true;
                _biomeLoadCursor = Math.Min(8, biomes.Length);
                _biomeTexDirty = false;
            }
#endif

            // Unit 0: 8-layer biome top albedo texture array (GL_TEXTURE_2D_ARRAY)
            // Unit 1: 4-layer biome under/cliff albedo texture array (GL_TEXTURE_2D_ARRAY)
            int targetSize = cache.IsESContext ? 512 : 1024;
            var topTextures = new Texture2D?[8];
            for (int i = 0; i < 8; i++)
                topTextures[i] = i < biomes.Length ? _boundBiomeTex2D![i] : null;

            var topArray = cache.GetOrCreateTexture2DArray(
                "planet:biome_top", topTextures, targetSize, 255, 255, 255, 255);
            topArray.Bind(TextureUnit.Texture0);
            shader.SetTexture("uBiomeTexArray", 0);

            var underTextures = new Texture2D?[MaxBoundUnderBiomes];
            for (int i = 0; i < MaxBoundUnderBiomes; i++)
                underTextures[i] = _boundBiomeUnderTex2D![i];

            var underArray = cache.GetOrCreateTexture2DArray(
                "planet:biome_under", underTextures, targetSize, 255, 255, 255, 255);
            underArray.Bind(TextureUnit.Texture1);
            shader.SetTexture("uUnderTexArray", 1);

            for (int i = 0; i < 8; i++)
            {
                float tiling = i < biomes.Length ? Math.Max(0.25f, _cachedTiling![i]) : 24f;
                float underTiling = i < biomes.Length ? Math.Max(0.25f, _cachedUnderTiling![i]) : 10f;
                var baseCol = i < biomes.Length ? _cachedBaseColor![i] : new SN.Vector3(0.5f);
                var underCol = i < biomes.Length ? _cachedUnderColor![i] : new SN.Vector3(0.22f);
                shader.SetFloat($"uBiomeTiling[{i}]", tiling);
                shader.SetFloat($"uBiomeUnderTiling[{i}]", underTiling);
                shader.SetVector3($"uBiomeBaseColor[{i}]", baseCol);
                shader.SetVector3($"uBiomeUnderColor[{i}]", underCol);
                shader.SetFloat($"uBiomeHasUnder[{i}]", _cachedHasUnder![i]);
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

            bool camInsideCrust = planet.TrySampleWorldDensity(camPos, out float camDensity) && camDensity <= 0f;

            var waterObj = planet.WaterGO;
            var parentWorld = TransformUtil.WorldFromTransform(planet.gameObject.Transform);
            var world = waterObj != null
                ? TransformUtil.WorldFromTransform(waterObj.Transform) * parentWorld
                : parentWorld;
            var resolvedPlanetCenter = new SN.Vector3(world.M41, world.M42, world.M43);
            float sx = new SN.Vector3(world.M11, world.M12, world.M13).Length();
            float sy = new SN.Vector3(world.M21, world.M22, world.M23).Length();
            float sz = new SN.Vector3(world.M31, world.M32, world.M33).Length();
            float radiusScale = MathF.Max(0.0001f, (sx + sy + sz) / 3f);
            float seaLevelWorld = seaLevel * radiusScale;
            float planetRadiusWorld = MathF.Max(1f, atmo.GroundRadius * radiusScale);

            var globals = GlobalUniforms.ForContext(gl);
            globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time);
            globals.UpdateLighting(
                lightDir, diffuseK, SN.Vector3.One, atmo.Ambient,
                lightIsPoint: false, SN.Vector3.Zero, 0f,
                hasShadow: false, SN.Matrix4x4.Identity);

            waterShader.Use();
            waterShader.SetMatrix4("uModel", world);

            SN.Matrix4x4.Invert(world, out var invWorld);
            waterShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invWorld));

            waterShader.SetVector3("uPlanetCenter", resolvedPlanetCenter);
            float camDist = (camPos - resolvedPlanetCenter).Length();
            bool nearSurface = camDist < planetRadiusWorld * 1.15f;
            float waveAmp = nearSurface ? 0.08f : 0.4f;

            waterShader.SetFloat("uTime", planet.WaterAnimTime);
            waterShader.SetFloat("uWaveAmp1", waveAmp);
            waterShader.SetFloat("uWaveFreq1", 0.6f);
            waterShader.SetFloat("uWaveSteep1", nearSurface ? 0.08f : 0.25f);
            waterShader.SetFloat("uWaveAmp2", waveAmp * 0.45f);
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

            int waterBodyCount = 0;
            var waterBodies = planet.Config?.WaterBodies;
            void SetBodyColor(int i, float sr, float sg, float sb, float dr, float dg, float db, float er, float eg, float eb)
            {
                waterShader.SetVector4($"uBodyShallow[{i}]", sr, sg, sb, 1f);
                waterShader.SetVector4($"uBodyDeep[{i}]", dr, dg, db, 1f);
                waterShader.SetVector4($"uBodyDeepest[{i}]", er, eg, eb, 1f);
            }

            for (int i = 0; i < 8; i++)
            {
                SetBodyColor(i,
                    oceanBiome.WaterShallowColorR, oceanBiome.WaterShallowColorG, oceanBiome.WaterShallowColorB,
                    oceanBiome.WaterDeepColorR, oceanBiome.WaterDeepColorG, oceanBiome.WaterDeepColorB,
                    oceanBiome.WaterDeepestColorR, oceanBiome.WaterDeepestColorG, oceanBiome.WaterDeepestColorB);
            }

            if (waterBodies is { Length: > 0 })
            {
                waterBodyCount = 8;
                int n = Math.Min(7, waterBodies.Length);
                for (int i = 0; i < n; i++)
                {
                    var wb = waterBodies[i];
                    SetBodyColor(i,
                        wb.ShallowR, wb.ShallowG, wb.ShallowB,
                        wb.DeepR, wb.DeepG, wb.DeepB,
                        wb.DeepestR, wb.DeepestG, wb.DeepestB);
                }
                SetBodyColor(6, 1.00f, 0.55f, 0.10f, 0.82f, 0.16f, 0.03f, 0.20f, 0.03f, 0.01f);
                SetBodyColor(7, 0.16f, 0.48f, 0.50f, 0.05f, 0.22f, 0.30f, 0.03f, 0.12f, 0.20f);
            }
            waterShader.SetInt("uWaterBodyCount", waterBodyCount);

            waterShader.SetVector3("uPlanetCenter", resolvedPlanetCenter);
            waterShader.SetFloat("uSeaLevel", seaLevelWorld);
            waterShader.SetFloat("uDepthRange", oceanBiome.WaterDepthColorRange);
            waterShader.SetFloat("uShorelineThreshold", 0.35f);
            waterShader.SetFloat("uShorelineSoftness", 0.18f);
            waterShader.SetFloat("uShoreBiomeBlend", 0.35f);

            int biomeColorCount = 0;
            var biomes = planet.Config?.Biomes;
            if (biomes != null)
            {
                biomeColorCount = Math.Min(8, biomes.Length);
                for (int i = 0; i < biomeColorCount; i++)
                {
                    var b = biomes[i];
                    waterShader.SetVector3($"uBiomeBaseColor[{i}]",
                        new SN.Vector3(b.BaseColorR, b.BaseColorG, b.BaseColorB));
                }
            }
            waterShader.SetInt("uBiomeColorCount", biomeColorCount);

            waterShader.SetFloat("uFresnelPower", 4.0f);
            waterShader.SetFloat("uReflectivity", 0.65f);
            waterShader.SetFloat("uTransparency", 0.75f);

            waterShader.SetVector3("uSkyColor", atmo.SkyTint);
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
            waterShader.SetFloat("uPlanetRadius", planetRadiusWorld);

            waterShader.SetInt("uHasWaterNormalMap", 0);
            waterShader.SetInt("uHasWaterTexture", 0);
            waterShader.SetInt("uFoamEnabled", 1);
            waterShader.SetFloat("uFoamThreshold", 0.6f);
            waterShader.SetFloat("uFoamIntensity", 0.4f);
            waterShader.SetVector4("uFoamColor", 0.9f, 0.95f, 1.0f, 1.0f);

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.DepthMask(false);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            var leaves = planet.ChunkManager?.GetRenderableLeaves();
            int chunkWaterDrawn = 0;
            if (leaves != null)
            {
                // Always prefer chunk patches when they exist. The PlanetWater
                // GameObject is a full sea-level sphere — Scene view draws it as
                // water over grassland/forest the moment the camera is "orbit".
                waterShader.SetMatrix4("uModel", parentWorld);
                SN.Matrix4x4.Invert(parentWorld, out var invParent);
                waterShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invParent));

                for (int i = 0; i < leaves.Count; i++)
                {
                    var leaf = leaves[i];
                    var waterMesh = leaf.GeneratedWaterMesh;
                    if (waterMesh == null) continue;

                    cache.GetMesh(waterMesh).Draw();
                    chunkWaterDrawn++;
                }
            }

            // Far-orbit silhouette only. Never a fallback for missing shoreline.
            if (!camInsideCrust && !nearSurface && chunkWaterDrawn == 0
                && camDist > planetRadiusWorld * 1.6f)
            {
                var mf = waterObj?.Behaviors.OfType<MeshFilter>().FirstOrDefault();
                if (mf?.Mesh != null)
                {
                    waterShader.SetMatrix4("uModel", world);
                    SN.Matrix4x4.Invert(world, out var invOrbit);
                    waterShader.SetMatrix4("uNormalMatrix", SN.Matrix4x4.Transpose(invOrbit));
                    cache.GetMesh(mf.Mesh).Draw();
                }
            }

            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }

        private static Mesh? s_planetCloudShellMesh;

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
            if (SkipPlanetAtmosphereClouds) return;
            if (!atmo.Enabled || !atmo.CloudsEnabled || atmo.CloudDensity <= 0.001f || atmo.CloudCoverage <= 0.001f
                || planet.gameObject == null || !planet.IsActiveAndEnabled || planet.Config == null)
                return;

            GlobalUniforms.ForContext(gl).UpdateFrame(in view, in proj, camPos, timeSec);

            float cloudTopHeight = Math.Max(atmo.CloudBaseHeight + 1.0f, atmo.CloudTopHeight);
            float outerRadius = atmo.GroundRadius + cloudTopHeight;
            float camDist = (camPos - planetCenter).Length();
            bool cameraInsideShell = camDist <= outerRadius * 1.015f;

            cloudShader.Use();
            cloudShader.SetVector3("uPlanetCenter", planetCenter);
            cloudShader.SetFloat("uPlanetRadius", atmo.GroundRadius);
            cloudShader.SetFloat("uCloudBaseHeight", atmo.CloudBaseHeight);
            cloudShader.SetFloat("uCloudTopHeight", cloudTopHeight);
            cloudShader.SetFloat("uCloudCoverage", atmo.CloudCoverage);
            cloudShader.SetFloat("uCloudDensity", atmo.CloudDensity);
            cloudShader.SetFloat("uCloudScale", atmo.CloudScale);
            cloudShader.SetFloat("uCloudDetail", atmo.CloudDetail);
            cloudShader.SetFloat("uCloudSpeed", atmo.CloudSpeed);
            cloudShader.SetFloat("uCloudSoftness", atmo.CloudSoftness);
            cloudShader.SetFloat("uCloudLightResponse", atmo.CloudLightResponse);
            cloudShader.SetFloat("uCloudSilverLining", atmo.CloudSilverLining);
            cloudShader.SetVector3("uCloudColor", atmo.CloudColor);
            cloudShader.SetVector3("uCloudShadowColor", atmo.CloudShadowColor);
            cloudShader.SetInt("uCloudStepCount", atmo.CloudStepCount);
            cloudShader.SetVector3("uSunDir", atmo.SunDir);
            cloudShader.SetFloat("uSunIntensity", atmo.SunIntensity);
            cloudShader.SetVector3("uSkyTint", atmo.SkyTint);
            cloudShader.SetFloat("uTime", timeSec);

            s_planetCloudShellMesh ??= Mesh.CreateUvSphere(48, 28, 1.0f);
            cloudShader.SetMatrix4("uModel", SN.Matrix4x4.CreateTranslation(planetCenter));

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            gl.Enable(EnableCap.CullFace);
            // Inside or below the cloud shell: draw back faces (sky dome).
            // Outside in orbit: draw front faces (so depth test passes in front of the planet).
            gl.CullFace(cameraInsideShell ? TriangleFace.Front : TriangleFace.Back);

            var gpuMesh = cache.GetMesh(s_planetCloudShellMesh);
            gpuMesh.Draw();

            gl.CullFace(TriangleFace.Back);
            gl.Enable(EnableCap.CullFace);
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
            if (SkipPlanetAtmosphereClouds) return;
            if (!atmo.Enabled || planet.gameObject == null || !planet.IsActiveAndEnabled || planet.Config == null)
                return;

            GlobalUniforms.ForContext(gl).UpdateFrame(in view, in proj, camPos, WindSystem.Time);

            atmoShader.Use();
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
                gl.DepthMask(true);
                gl.Enable(EnableCap.CullFace);
                gl.Disable(EnableCap.Blend);
                return;
            }

            gl.DepthMask(true);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }
    }
}
