#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Biome.Graph;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Tracks scatter recipes and renders planet scatter rocks via GPU instancing
/// (<see cref="GPUMesh.DrawInstanced"/> + <c>Standard</c> shader with <c>uInstanced = 1</c>),
/// avoiding per-rock <see cref="GameObject"/> overhead while retaining backwards-compatible
/// <see cref="Instances"/> / <see cref="TryAddInstance"/> bookkeeping for prefab fallbacks.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetScatterRenderer : Behavior
{
    public const int FloatsPerInstance = 12; // vec4 PosScale + vec4 UpYaw + vec4 Tint
    public const int StrideBytes = FloatsPerInstance * sizeof(float); // 48 bytes

    [Persist] public bool Enabled { get; set; } = true;
    [Persist] public int MaxInstances { get; set; } = 1024;

    readonly record struct BatchKey(Mesh Mesh, Material? Material, bool DoubleSided);

    sealed class SlotRef
    {
        public RockBatch Batch = null!;
        public int Slot;
    }

    sealed class RockBatch
    {
        public BatchKey Key;
        public float[] Packed = Array.Empty<float>();
        public readonly List<(int Token, int PartIndex)> SlotOwners = new();
        public float MeshBoundRadius = -1f;
        public int Count => SlotOwners.Count;
    }

    sealed class BatchBuf
    {
        public uint Vbo;
        public int GpuCapacityFloats;
    }

    sealed class ScatterGpu
    {
        public GL Gl = null!;
        public readonly Dictionary<RockBatch, BatchBuf> Bufs = new();
    }

    struct FrustumPlane { public float A, B, C, D; }
    [ThreadStatic] static FrustumPlane[]? s_frustumPlanes;
    [ThreadStatic] static float[]? s_visibleScratch;

    static readonly List<PlanetScatterRenderer> s_activeRenderers = new();
    static readonly Dictionary<object, ScatterGpu> s_gpus = new();

    public static IReadOnlyList<PlanetScatterRenderer> ActiveRenderers => s_activeRenderers;

    readonly List<ScatterInstance> _instances = new();
    readonly Dictionary<int, List<SlotRef>> _gpuRocks = new();
    readonly List<RockBatch> _batches = new();
    readonly Dictionary<BatchKey, RockBatch> _batchByKey = new();
    ScatterLayerRecipe[] _recipes = Array.Empty<ScatterLayerRecipe>();

    public int InstanceCount => _instances.Count + _gpuRocks.Count;
    public int GpuInstanceCount => _gpuRocks.Count;
    public bool HasRoom => Enabled && InstanceCount < InstanceLimit;

    int InstanceLimit
    {
        get
        {
            int limit = Math.Max(1, MaxInstances);
#if XBOX_UWP
            // GPU-instanced rocks draw in 1 instanced call per mesh/material (64-instance ANGLE chunks),
            // lifting the old 96-GameObject cap cleanly to 512 on Xbox.
            limit = Math.Min(limit, 512);
#else
            limit = Math.Min(limit, 2048);
#endif
            return limit;
        }
    }

    public IReadOnlyList<ScatterInstance> Instances => _instances;
    public ScatterLayerRecipe[] Recipes => _recipes;

    public override void OnEnable()
    {
        base.OnEnable();
        DropIfDuplicate();
        if (gameObject != null && !s_activeRenderers.Contains(this))
            s_activeRenderers.Add(this);
    }

    public override void OnDisable()
    {
        s_activeRenderers.Remove(this);
        base.OnDisable();
    }

    public override void OnDestroy()
    {
        s_activeRenderers.Remove(this);
        ClearInstances();
        base.OnDestroy();
    }

    void DropIfDuplicate()
    {
        if (gameObject == null) return;
        var behaviors = gameObject.Behaviors;
        for (int i = 0; i < behaviors.Count; i++)
        {
            if (behaviors[i] is not PlanetScatterRenderer existing) continue;
            if (ReferenceEquals(existing, this)) return;
            gameObject.RemoveBehavior(this);
            return;
        }
    }

    public void ApplyRecipes(ScatterLayerRecipe[]? recipes)
    {
        _recipes = recipes ?? Array.Empty<ScatterLayerRecipe>();
    }

    public void BindLayers(ScatterLayerRecipe[]? recipes) => ApplyRecipes(recipes);

    public void ClearInstances()
    {
        _instances.Clear();
        _gpuRocks.Clear();
        for (int i = 0; i < _batches.Count; i++)
            _batches[i].SlotOwners.Clear();
    }

    public bool TryAddInstance(in ScatterInstance instance)
    {
        if (!Enabled) return false;
        if (InstanceCount >= InstanceLimit) return false;
        _instances.Add(instance);
        return true;
    }

    /// <summary>
    /// Register a GPU-instanced scatter rock in planet-local space using the same 48-byte
    /// <c>(posScale, upYaw, tint)</c> layout as <see cref="PlanetGpuTrees"/>.
    /// </summary>
    public bool TryAddGpuInstance(
        int token,
        IReadOnlyList<PlanetGpuTrees.SubmeshPart> parts,
        SN.Vector3 localPos,
        SN.Vector3 localUp,
        float localScale,
        float yawRad,
        SN.Vector3 tint = default)
    {
        if (!Enabled || parts == null || parts.Count == 0 || localScale <= 0.0001f)
            return false;

        if (_gpuRocks.ContainsKey(token))
            RemoveGpuInstance(token);

        if (InstanceCount >= InstanceLimit)
            return false;

        var up = localUp.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(localUp) : SN.Vector3.UnitY;
        var col = tint.LengthSquared() > 1e-6f ? tint : SN.Vector3.One;
        var refs = new List<SlotRef>(parts.Count);

        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            if (part.Mesh == null) continue;
            var batch = GetOrCreateBatch(part.Mesh, part.Material, part.DoubleSided);
            int slot = batch.Count;
            int need = (slot + 1) * FloatsPerInstance;
            if (batch.Packed.Length < need)
                Array.Resize(ref batch.Packed, Math.Max(need, Math.Max(FloatsPerInstance * 32, batch.Packed.Length * 2)));

            batch.SlotOwners.Add((token, refs.Count));
            WriteSlot(batch, slot, localPos, up, localScale, yawRad, col);
            refs.Add(new SlotRef { Batch = batch, Slot = slot });
        }

        if (refs.Count == 0)
            return false;

        _gpuRocks[token] = refs;
        return true;
    }

    /// <summary>O(1) swap-remove of a GPU-instanced scatter rock by token.</summary>
    public bool RemoveGpuInstance(int token)
    {
        if (!_gpuRocks.Remove(token, out var refs))
            return false;
        for (int i = 0; i < refs.Count; i++)
            ReleaseSlot(refs[i].Batch, refs[i].Slot);
        return true;
    }

    RockBatch GetOrCreateBatch(Mesh mesh, Material? mat, bool doubleSided)
    {
        var bk = new BatchKey(mesh, mat, doubleSided);
        if (_batchByKey.TryGetValue(bk, out var batch))
            return batch;
        batch = new RockBatch { Key = bk };
        _batchByKey[bk] = batch;
        _batches.Add(batch);
        return batch;
    }

    static void WriteSlot(RockBatch batch, int slot, SN.Vector3 pos, SN.Vector3 up, float scale, float yawRad, SN.Vector3 col)
    {
        int o = slot * FloatsPerInstance;
        batch.Packed[o + 0] = pos.X;
        batch.Packed[o + 1] = pos.Y;
        batch.Packed[o + 2] = pos.Z;
        batch.Packed[o + 3] = scale;
        batch.Packed[o + 4] = up.X;
        batch.Packed[o + 5] = up.Y;
        batch.Packed[o + 6] = up.Z;
        batch.Packed[o + 7] = yawRad;
        batch.Packed[o + 8] = col.X;
        batch.Packed[o + 9] = col.Y;
        batch.Packed[o + 10] = col.Z;
        batch.Packed[o + 11] = 1f;
    }

    void ReleaseSlot(RockBatch batch, int slot)
    {
        int last = batch.Count - 1;
        if (last < 0 || slot < 0 || slot > last)
            return;
        if (slot != last)
        {
            Array.Copy(batch.Packed, last * FloatsPerInstance, batch.Packed, slot * FloatsPerInstance, FloatsPerInstance);
            var moved = batch.SlotOwners[last];
            batch.SlotOwners[slot] = moved;
            if (_gpuRocks.TryGetValue(moved.Token, out var refs) && moved.PartIndex >= 0 && moved.PartIndex < refs.Count)
                refs[moved.PartIndex].Slot = slot;
        }
        batch.SlotOwners.RemoveAt(last);
    }

    /// <summary>Drop the instance nearest <paramref name="position"/> when it is within <paramref name="maxDistance"/>.</summary>
    public bool RemoveNearest(SN.Vector3 position, float maxDistance = 1.5f)
    {
        float maxSq = maxDistance * maxDistance;
        int best = -1;
        float bestSq = maxSq;
        for (int i = 0; i < _instances.Count; i++)
        {
            float d = SN.Vector3.DistanceSquared(_instances[i].Position, position);
            if (d <= bestSq)
            {
                bestSq = d;
                best = i;
            }
        }
        if (best < 0) return false;
        _instances.RemoveAt(best);
        return true;
    }

    /// <summary>
    /// Pack billboard-style instance data (pos.xyz, scale) for DrawArraysInstanced consumers.
    /// Returns count written.
    /// </summary>
    public int FillBillboardBuffer(Span<float> dst)
    {
        int written = 0;
        int max = Math.Min(_instances.Count, dst.Length / 4);
        for (int i = 0; i < max; i++)
        {
            var inst = _instances[i];
            int o = i * 4;
            dst[o] = inst.Position.X;
            dst[o + 1] = inst.Position.Y;
            dst[o + 2] = inst.Position.Z;
            dst[o + 3] = inst.Scale;
            written++;
        }
        return written;
    }

    /// <summary>
    /// Render all active <see cref="PlanetScatterRenderer"/> GPU-instanced rock batches
    /// using the <c>Standard</c> shader (<c>uInstanced = 1</c>) and <see cref="GPUMesh.DrawInstanced"/>.
    /// </summary>
    public static unsafe void RenderAll(
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
        CascadedShadowMap? cascadedShadow,
        bool hdrOutput,
        int debugViewMode)
    {
        if (standardShader == null || s_activeRenderers.Count == 0)
            return;

        bool any = false;
        for (int i = 0; i < s_activeRenderers.Count; i++)
        {
            var r = s_activeRenderers[i];
            if (r != null && r.Enabled && r.IsActiveAndEnabled && r.gameObject != null && r._gpuRocks.Count > 0)
            {
                any = true;
                break;
            }
        }
        if (!any) return;

        if (!s_gpus.TryGetValue(cache, out var gpu))
        {
            gpu = new ScatterGpu { Gl = gl };
            s_gpus[cache] = gpu;
        }

        var sunTint = lightColor.LengthSquared() > 1e-6f ? lightColor : SN.Vector3.One;
        bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;

        GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
        var globals = GlobalUniforms.ForContext(gl);
        globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
        globals.UpdateLighting(
            lightDir, diffuseK, sunTint, Math.Clamp(ambient, 0.02f, 0.85f),
            lightIsPoint, lightPosW, lightRange,
            hasShadow, in shadowVP,
            cascadeCount: hasShadow ? 1 : 0,
            cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
            windDir: SN.Vector3.UnitX,
            windTime: 0f);

        var prevCull = gl.IsEnabled(EnableCap.CullFace);
        var prevBlend = gl.IsEnabled(EnableCap.Blend);

        standardShader.Use();
        standardShader.SetInt("uInstanced", 1);
        standardShader.SetInt("uHasBones", 0);
        standardShader.SetInt("uHdrOutput", hdrOutput ? 1 : 0);
        standardShader.SetInt("uDebugViewMode", debugViewMode);
        standardShader.SetInt("uIsVegetation", 0);
        standardShader.SetFloat("uWindStrength", 0f);
        standardShader.SetFloat("uRain", 0f);
        standardShader.SetFloat("uWetness", 0f);
        standardShader.SetFloat("uSnow", 0f);
        standardShader.SetFloat("uCloudiness", 0f);
        standardShader.SetFloat("uSunIntensity", 1f);
        standardShader.SetInt("uTransparent", 0);
        standardShader.SetVector3("uEmissiveColor", SN.Vector3.Zero);
        standardShader.SetFloat("uEmissiveIntensity", 0f);

        if (hasShadow)
        {
            standardShader.SetFloat("uShadowBias", 0.008f);
            standardShader.SetVector3("uSunDir", sunShineDir);
            SceneRenderer.BindShadowMapsToShader(standardShader, shadowFBO, cascadedShadow, 7);
        }
        SceneRenderer.BindEnvMapToShader(gl, standardShader, camPos, 12);

        var planes = s_frustumPlanes ??= new FrustumPlane[6];

        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);

        try
        {
            for (int ri = 0; ri < s_activeRenderers.Count; ri++)
            {
                var renderer = s_activeRenderers[ri];
                if (renderer == null || !renderer.Enabled || !renderer.IsActiveAndEnabled
                    || renderer.gameObject == null || renderer._gpuRocks.Count == 0)
                    continue;

                var planetWorld = TransformUtil.WorldFromTransform(renderer.gameObject.Transform);
                SN.Matrix4x4.Invert(planetWorld, out var invWorld);
                var normalMatrix = SN.Matrix4x4.Transpose(invWorld);

                standardShader.SetMatrix4("uModel", planetWorld);
                standardShader.SetMatrix4("uNormalMatrix", normalMatrix);

                var localViewProj = planetWorld * view * proj;
                ExtractFrustumPlanes(in localViewProj, planes);

                for (int bi = 0; bi < renderer._batches.Count; bi++)
                {
                    var batch = renderer._batches[bi];
                    if (batch.Count <= 0)
                        continue;

                    var gpuMesh = cache.GetMesh(batch.Key.Mesh);
                    if (gpuMesh.IndexCount <= 0)
                        continue;

                    int visibleCount = CullAndPackVisible(batch, planes, out var visibleData);
                    if (visibleCount <= 0)
                        continue;

                    if (!gpu.Bufs.TryGetValue(batch, out var buf))
                    {
                        buf = new BatchBuf { Vbo = gl.GenBuffer() };
                        gpu.Bufs[batch] = buf;
                    }
                    UploadVisible(gl, visibleData, visibleCount, buf);

                    BindMaterialForRockBatch(gl, standardShader, cache, batch.Key.Material, batch.Key.DoubleSided);
                    gpuMesh.DrawInstanced(buf.Vbo, visibleCount, StrideBytes);
                    Profiler.CountBatch();
                }
            }
        }
        finally
        {
            standardShader.SetInt("uInstanced", 0);
            if (prevCull) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
            if (prevBlend) gl.Enable(EnableCap.Blend); else gl.Disable(EnableCap.Blend);
        }
    }

    static void ExtractFrustumPlanes(in SN.Matrix4x4 vp, FrustumPlane[] planes)
    {
        planes[0] = new FrustumPlane { A = vp.M14 + vp.M11, B = vp.M24 + vp.M21, C = vp.M34 + vp.M31, D = vp.M44 + vp.M41 };
        planes[1] = new FrustumPlane { A = vp.M14 - vp.M11, B = vp.M24 - vp.M21, C = vp.M34 - vp.M31, D = vp.M44 - vp.M41 };
        planes[2] = new FrustumPlane { A = vp.M14 + vp.M12, B = vp.M24 + vp.M22, C = vp.M34 + vp.M32, D = vp.M44 + vp.M42 };
        planes[3] = new FrustumPlane { A = vp.M14 - vp.M12, B = vp.M24 - vp.M22, C = vp.M34 - vp.M32, D = vp.M44 - vp.M42 };
        planes[4] = new FrustumPlane { A = vp.M13,          B = vp.M23,          C = vp.M33,          D = vp.M43 };
        planes[5] = new FrustumPlane { A = vp.M14 - vp.M13, B = vp.M24 - vp.M23, C = vp.M34 - vp.M33, D = vp.M44 - vp.M43 };
        for (int i = 0; i < 6; i++)
        {
            float lenSq = planes[i].A * planes[i].A + planes[i].B * planes[i].B + planes[i].C * planes[i].C;
            if (lenSq > 1e-12f)
            {
                float inv = 1f / MathF.Sqrt(lenSq);
                planes[i].A *= inv; planes[i].B *= inv; planes[i].C *= inv; planes[i].D *= inv;
            }
        }
    }

    static float GetMeshBoundRadius(RockBatch batch)
    {
        if (batch.MeshBoundRadius > 0f)
            return batch.MeshBoundRadius;
        float maxSq = 1f;
        var verts = batch.Key.Mesh?.Vertices;
        if (verts != null)
        {
            for (int i = 0; i < verts.Length; i++)
            {
                float dSq = verts[i].LengthSquared();
                if (dSq > maxSq) maxSq = dSq;
            }
        }
        batch.MeshBoundRadius = MathF.Sqrt(maxSq);
        return batch.MeshBoundRadius;
    }

    static int CullAndPackVisible(RockBatch batch, FrustumPlane[] planes, out float[] scratch)
    {
        int total = batch.Count;
        int needFloats = total * FloatsPerInstance;
        scratch = s_visibleScratch ??= new float[Math.Max(needFloats, FloatsPerInstance * 64)];
        if (scratch.Length < needFloats)
        {
            scratch = new float[Math.Max(needFloats, scratch.Length * 2)];
            s_visibleScratch = scratch;
        }

        float meshR = GetMeshBoundRadius(batch);
        var src = batch.Packed;
        int vis = 0;
        for (int i = 0; i < total; i++)
        {
            int o = i * FloatsPerInstance;
            float scale = src[o + 3];
            if (scale <= 0.0001f)
                continue;

            float cx = src[o + 0];
            float cy = src[o + 1];
            float cz = src[o + 2];
            float r = MathF.Max(scale * meshR * 1.15f, 2f);

            bool inside = true;
            for (int p = 0; p < 6; p++)
            {
                float d = planes[p].A * cx + planes[p].B * cy + planes[p].C * cz + planes[p].D;
                if (d < -r) { inside = false; break; }
            }
            if (!inside)
                continue;

            Array.Copy(src, o, scratch, vis * FloatsPerInstance, FloatsPerInstance);
            vis++;
        }
        return vis;
    }

    static unsafe void UploadVisible(GL gl, float[] data, int instanceCount, BatchBuf buf)
    {
        int floats = instanceCount * FloatsPerInstance;
        if (floats <= 0 || floats > data.Length)
            return;

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf.Vbo);
        fixed (float* ptr = data)
        {
            if (floats > buf.GpuCapacityFloats)
            {
                int cap = Math.Max(floats, Math.Max(FloatsPerInstance * 64, buf.GpuCapacityFloats * 3 / 2));
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), null, BufferUsageARB.DynamicDraw);
                buf.GpuCapacityFloats = cap;
            }
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(floats * sizeof(float)), ptr);
        }
    }

    static void BindMaterialForRockBatch(
        GL gl,
        ShaderProgram shader,
        ResourceCache cache,
        Material? mat,
        bool doubleSided)
    {
        float r = 1f, g = 1f, b = 1f, a = 1f;
        float roughness = 0.78f, metallic = 0f, alphaCutoff = 0f;
        if (mat != null)
        {
            r = mat.BaseColor.R / 255f;
            g = mat.BaseColor.G / 255f;
            b = mat.BaseColor.B / 255f;
            a = mat.BaseColor.A / 255f;
            roughness = mat.Roughness;
            metallic = mat.Metallic;
            alphaCutoff = mat.AlphaCutoff;
        }

        shader.SetVector4("uBaseColor", r, g, b, a);
        shader.SetFloat("uRoughness", roughness);
        shader.SetFloat("uMetallic", metallic);
        shader.SetFloat("uAlphaCutoff", alphaCutoff);
        shader.SetInt("uDoubleSided", doubleSided ? 1 : 0);

        if (doubleSided)
            gl.Disable(EnableCap.CullFace);
        else
        {
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
        }

        const int opacityTexUnit = 11;
        bool hasAlbedo = false, hasNormal = false, hasSpec = false, hasMetal = false, hasRough = false, hasAO = false, hasEmissive = false, hasOpacity = false;
        if (mat?.Textures != null && mat.Textures.Count > 0)
        {
            for (int i = 0; i < mat.Textures.Count; i++)
            {
                var slot = mat.Textures[i];
                if (slot is RuntimeTexSlot rts && rts.Texture != null)
                {
                    var u = rts.Usage?.ToLowerInvariant() ?? "";
                    if (!hasAlbedo && (u.Contains("albedo") || u == "" || u.Contains("base") || u.Contains("diff")))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture0);
                        hasAlbedo = true;
                    }
                    else if (!hasNormal && (u.Contains("normal") || u.Contains("bump")))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture1);
                        hasNormal = true;
                    }
                    else if (!hasSpec && u.Contains("specular"))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture2);
                        hasSpec = true;
                    }
                    else if (!hasMetal && (u.Contains("metallic") || u.Contains("metalness")))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture3);
                        hasMetal = true;
                    }
                    else if (!hasRough && u.Contains("rough"))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture4);
                        hasRough = true;
                    }
                    else if (!hasAO && (u.Contains("occlusion") || u.Contains("ao")))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture5);
                        hasAO = true;
                    }
                    else if (!hasEmissive && (u.Contains("emissive") || u.Contains("emission")))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture6);
                        hasEmissive = true;
                    }
                    else if (!hasOpacity && (u.Contains("opacity") || u.Contains("transparency") || u == "mask"))
                    {
                        cache.GetTexture(rts.Texture).Bind(TextureUnit.Texture0 + opacityTexUnit);
                        hasOpacity = true;
                    }
                }
                else if (slot is MaterialTexture mtex && mtex.Texture != null)
                {
                    switch (mtex.Usage)
                    {
                        case MaterialTexture.TexUsage.Albedo when !hasAlbedo:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture0);
                            hasAlbedo = true;
                            break;
                        case MaterialTexture.TexUsage.Normal when !hasNormal:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture1);
                            hasNormal = true;
                            break;
                        case MaterialTexture.TexUsage.Specular when !hasSpec:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture2);
                            hasSpec = true;
                            break;
                        case MaterialTexture.TexUsage.Metallic when !hasMetal:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture3);
                            hasMetal = true;
                            break;
                        case MaterialTexture.TexUsage.Roughness when !hasRough:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture4);
                            hasRough = true;
                            break;
                        case MaterialTexture.TexUsage.AmbientOcclusion when !hasAO:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture5);
                            hasAO = true;
                            break;
                        case MaterialTexture.TexUsage.Emissive when !hasEmissive:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture6);
                            hasEmissive = true;
                            break;
                        case MaterialTexture.TexUsage.Opacity when !hasOpacity:
                            cache.GetTexture(mtex.Texture, mtex).Bind(TextureUnit.Texture0 + opacityTexUnit);
                            hasOpacity = true;
                            break;
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
        shader.SetInt("uHasEmissiveTex", hasEmissive ? 1 : 0);
        shader.SetTexture("uOpacityTex", opacityTexUnit);
        shader.SetInt("uHasOpacityTex", hasOpacity ? 1 : 0);
        shader.SetFloat("uLumaClip", mat?.LumaClip ?? 0f);
    }

    public static void ReleaseGpuFor(object gpuKey)
    {
        if (!s_gpus.Remove(gpuKey, out var gpu))
            return;
        var gl = gpu.Gl;
        foreach (var buf in gpu.Bufs.Values)
        {
            try
            {
                if (buf.Vbo != 0)
                    gl.DeleteBuffer(buf.Vbo);
            }
            catch { }
        }
        gpu.Bufs.Clear();
    }

    public readonly struct ScatterInstance
    {
        public SN.Vector3 Position { get; init; }
        public float Scale { get; init; }
        public float YawDeg { get; init; }
        public string ScatterType { get; init; }
        public string ModelPath { get; init; }

        public ScatterInstance(SN.Vector3 position, float scale, float yawDeg, string scatterType, string modelPath)
        {
            Position = position;
            Scale = scale;
            YawDeg = yawDeg;
            ScatterType = scatterType ?? "Rock";
            ModelPath = modelPath ?? "";
        }
    }
}
