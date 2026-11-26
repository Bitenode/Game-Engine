#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Tier 1 GPU-instanced near-ring 3D trees.
/// Batches all trees sharing the same <c>(PlanetVegetationSystem, Mesh, Material, DoubleSided)</c>
/// into a single instanced draw call using <see cref="GPUMesh.DrawInstanced"/> and the
/// <c>Standard</c> shader (<c>uInstanced = 1</c>). Uses O(1) swap-remove slots and per-GL-context
/// dynamic instance VBOs keyed by <see cref="ResourceCache"/>.
/// </summary>
public static class PlanetGpuTrees
{
    public const int FloatsPerInstance = 12; // vec4 PosScale + vec4 UpYaw + vec4 Tint
    public const int StrideBytes = FloatsPerInstance * sizeof(float); // 48 bytes

    public readonly record struct SubmeshPart(Mesh Mesh, Material? Material, bool DoubleSided);

    readonly record struct TreeKey(PlanetVegetationSystem Owner, int Token);
    readonly record struct BatchKey(PlanetVegetationSystem Owner, Mesh Mesh, Material? Material, bool DoubleSided);

    sealed class SlotRef
    {
        public TreeBatch Batch = null!;
        public int Slot;
    }

    sealed class TreeBatch
    {
        public BatchKey Key;
        public float[] Packed = Array.Empty<float>();
        public readonly List<(TreeKey Tree, int PartIndex)> SlotOwners = new();
        public int Revision;
        public float MeshBoundRadius = -1f;
        public int Count => SlotOwners.Count;
    }

    sealed class BatchBuf
    {
        public uint Vbo;
        public int GpuCapacityFloats;
        public int UploadedRev = -1;
    }

    sealed class TreesGpu
    {
        public GL Gl = null!;
        public readonly Dictionary<TreeBatch, BatchBuf> Bufs = new();
    }

    struct FrustumPlane { public float A, B, C, D; }
    [ThreadStatic] static FrustumPlane[]? s_frustumPlanes;
    [ThreadStatic] static float[]? s_visibleScratch;

    static readonly Dictionary<TreeKey, List<SlotRef>> s_trees = new();
    static readonly List<TreeBatch> s_batches = new();
    static readonly Dictionary<BatchKey, TreeBatch> s_batchByKey = new();
    static readonly Dictionary<object, TreesGpu> s_gpus = new();

    public static int TotalTreeCount => s_trees.Count;

    public static int TreeCount(PlanetVegetationSystem? owner)
    {
        if (owner == null || s_trees.Count == 0) return 0;
        int n = 0;
        foreach (var k in s_trees.Keys)
        {
            if (ReferenceEquals(k.Owner, owner))
                n++;
        }
        return n;
    }

    public static bool HasTree(PlanetVegetationSystem owner, int token)
        => s_trees.ContainsKey(new TreeKey(owner, token));

    public static void AddOrUpdateTree(
        PlanetVegetationSystem owner,
        int token,
        IReadOnlyList<SubmeshPart> parts,
        SN.Vector3 localPos,
        SN.Vector3 localUp,
        float scale,
        float yawRad,
        SN.Vector3 tint = default)
    {
        if (owner == null || parts == null || parts.Count == 0)
            return;

        var key = new TreeKey(owner, token);
        if (s_trees.ContainsKey(key))
            RemoveTree(owner, token);

        var up = localUp.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(localUp) : SN.Vector3.UnitY;
        var col = tint.LengthSquared() > 1e-6f ? tint : SN.Vector3.One;
        var refs = new List<SlotRef>(parts.Count);

        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            if (part.Mesh == null) continue;
            var batch = GetOrCreateBatch(owner, part.Mesh, part.Material, part.DoubleSided);
            int slot = batch.Count;
            int need = (slot + 1) * FloatsPerInstance;
            if (batch.Packed.Length < need)
                Array.Resize(ref batch.Packed, Math.Max(need, Math.Max(FloatsPerInstance * 32, batch.Packed.Length * 2)));

            batch.SlotOwners.Add((key, refs.Count));
            WriteSlot(batch, slot, localPos, up, scale, yawRad, col);
            refs.Add(new SlotRef { Batch = batch, Slot = slot });
        }

        if (refs.Count > 0)
            s_trees[key] = refs;
    }

    /// <summary>Update an existing tree's seat/orientation in-place without reallocating slots.</summary>
    public static bool UpdateTreeTransform(
        PlanetVegetationSystem owner,
        int token,
        SN.Vector3 localPos,
        SN.Vector3 localUp,
        float scale,
        float yawRad)
    {
        if (owner == null) return false;
        if (!s_trees.TryGetValue(new TreeKey(owner, token), out var refs))
            return false;

        var up = localUp.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(localUp) : SN.Vector3.UnitY;
        for (int i = 0; i < refs.Count; i++)
        {
            var sr = refs[i];
            int o = sr.Slot * FloatsPerInstance;
            var col = new SN.Vector3(sr.Batch.Packed[o + 8], sr.Batch.Packed[o + 9], sr.Batch.Packed[o + 10]);
            WriteSlot(sr.Batch, sr.Slot, localPos, up, scale, yawRad, col);
        }
        return true;
    }

    public static void RemoveTree(PlanetVegetationSystem owner, int token)
    {
        if (owner == null) return;
        var key = new TreeKey(owner, token);
        if (!s_trees.Remove(key, out var refs))
            return;
        for (int i = 0; i < refs.Count; i++)
            ReleaseSlot(refs[i].Batch, refs[i].Slot);
    }

    public static void ClearOwner(PlanetVegetationSystem owner)
    {
        if (owner == null) return;
        for (int i = 0; i < s_batches.Count; i++)
        {
            var b = s_batches[i];
            if (!ReferenceEquals(b.Key.Owner, owner) || b.Count == 0)
                continue;
            for (int s = 0; s < b.SlotOwners.Count; s++)
                s_trees.Remove(b.SlotOwners[s].Tree);
            b.SlotOwners.Clear();
            b.Revision++;
        }
    }

    static TreeBatch GetOrCreateBatch(PlanetVegetationSystem owner, Mesh mesh, Material? mat, bool doubleSided)
    {
        var bk = new BatchKey(owner, mesh, mat, doubleSided);
        if (s_batchByKey.TryGetValue(bk, out var batch))
            return batch;
        batch = new TreeBatch { Key = bk };
        s_batchByKey[bk] = batch;
        s_batches.Add(batch);
        return batch;
    }

    static void WriteSlot(TreeBatch batch, int slot, SN.Vector3 pos, SN.Vector3 up, float scale, float yawRad, SN.Vector3 col)
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
        batch.Revision++;
    }

    static void ReleaseSlot(TreeBatch batch, int slot)
    {
        int last = batch.Count - 1;
        if (last < 0 || slot < 0 || slot > last)
            return;
        if (slot != last)
        {
            Array.Copy(batch.Packed, last * FloatsPerInstance, batch.Packed, slot * FloatsPerInstance, FloatsPerInstance);
            var moved = batch.SlotOwners[last];
            batch.SlotOwners[slot] = moved;
            if (s_trees.TryGetValue(moved.Tree, out var refs) && moved.PartIndex >= 0 && moved.PartIndex < refs.Count)
                refs[moved.PartIndex].Slot = slot;
        }
        batch.SlotOwners.RemoveAt(last);
        batch.Revision++;
    }

    public static unsafe void Render(
        GL gl,
        ShaderProgram standardShader,
        ResourceCache cache,
        PlanetVegetationSystem owner,
        in SN.Matrix4x4 planetWorld,
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
        if (owner == null || standardShader == null || s_batches.Count == 0)
            return;

        // Quick check whether this owner has any active batches.
        bool any = false;
        for (int i = 0; i < s_batches.Count; i++)
        {
            var b = s_batches[i];
            if (b.Count > 0 && ReferenceEquals(b.Key.Owner, owner))
            {
                any = true;
                break;
            }
        }
        if (!any) return;

        if (!s_gpus.TryGetValue(cache, out var gpu))
        {
            gpu = new TreesGpu { Gl = gl };
            s_gpus[cache] = gpu;
        }

        owner.GetGpuGrassEnvironment(
            out float wetness, out float snow, out float rain, out float cloudiness,
            out float windMul, out float sunIntensity, out float atmoAmbient);

        float wind = WindSystem.GetCurrentStrength() * 0.35f * Math.Clamp(windMul, 0.35f, 1.15f);
        wind *= 1f + Math.Clamp(rain, 0f, 1f) * 0.2f;
        var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
            ? SN.Vector3.Normalize(WindSystem.Direction)
            : SN.Vector3.UnitX;
        float windTime = WindSystem.Time * 0.55f * 0.45f;
        var sunTint = lightColor.LengthSquared() > 1e-6f ? lightColor : SN.Vector3.One;
        bool hasShadow = cascadedShadow != null || shadowFBO?.DepthTexture != null;

        GameRenderPipeline.ExtractNearFar(in proj, out float nearP, out float farP);
        var globals = GlobalUniforms.ForContext(gl);
        globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time, nearPlane: nearP, farPlane: farP);
        globals.UpdateLighting(
            lightDir, diffuseK, sunTint, Math.Clamp(atmoAmbient, 0.02f, 0.85f),
            lightIsPoint, lightPosW, lightRange,
            hasShadow, in shadowVP,
            cascadeCount: hasShadow ? 1 : 0,
            cascadeSplits: new SN.Vector4(1000f, 1000f, 1000f, 1000f),
            windDir: windDir,
            windTime: windTime);

        var prevCull = gl.IsEnabled(EnableCap.CullFace);
        var prevBlend = gl.IsEnabled(EnableCap.Blend);

        SN.Matrix4x4.Invert(planetWorld, out var invWorld);
        var normalMatrix = SN.Matrix4x4.Transpose(invWorld);

        standardShader.Use();
        standardShader.SetInt("uInstanced", 1);
        standardShader.SetInt("uHasBones", 0);
        standardShader.SetInt("uHdrOutput", hdrOutput ? 1 : 0);
        standardShader.SetInt("uDebugViewMode", debugViewMode);
        standardShader.SetMatrix4("uModel", planetWorld);
        standardShader.SetMatrix4("uNormalMatrix", normalMatrix);

        standardShader.SetInt("uIsVegetation", 1);
        standardShader.SetFloat("uWindStrength", Math.Clamp(wind * 4.2f, 0.04f, 0.38f));
        standardShader.SetFloat("uRain", rain);
        standardShader.SetFloat("uWetness", wetness);
        standardShader.SetFloat("uSnow", snow);
        standardShader.SetFloat("uCloudiness", cloudiness);
        standardShader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.02f, 2f));
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

        var localViewProj = planetWorld * view * proj;
        var planes = s_frustumPlanes ??= new FrustumPlane[6];
        ExtractFrustumPlanes(in localViewProj, planes);

        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);

        try
        {
            for (int bi = 0; bi < s_batches.Count; bi++)
            {
                var batch = s_batches[bi];
                if (batch.Count <= 0 || !ReferenceEquals(batch.Key.Owner, owner))
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

                BindMaterialForInstancedBatch(gl, standardShader, cache, batch.Key.Material, batch.Key.DoubleSided);
                gpuMesh.DrawInstanced(buf.Vbo, visibleCount, StrideBytes);
            }
        }
        finally
        {
            standardShader.SetInt("uInstanced", 0);
            standardShader.SetInt("uIsVegetation", 0);
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

    static float GetMeshBoundRadius(TreeBatch batch)
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

    static int CullAndPackVisible(TreeBatch batch, FrustumPlane[] planes, out float[] scratch)
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

            float halfH = scale * meshR * 0.5f;
            float cx = src[o + 0] + src[o + 4] * halfH;
            float cy = src[o + 1] + src[o + 5] * halfH;
            float cz = src[o + 2] + src[o + 6] * halfH;
            float r = MathF.Max(scale * meshR * 1.15f, 4f);

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

    static void BindMaterialForInstancedBatch(
        GL gl,
        ShaderProgram shader,
        ResourceCache cache,
        Material? mat,
        bool doubleSided)
    {
        float r = 1f, g = 1f, b = 1f, a = 1f;
        float roughness = 0.65f, metallic = 0f, alphaCutoff = 0.35f;
        if (mat != null)
        {
            r = mat.BaseColor.R / 255f;
            g = mat.BaseColor.G / 255f;
            b = mat.BaseColor.B / 255f;
            a = mat.BaseColor.A / 255f;
            roughness = mat.Roughness;
            metallic = mat.Metallic;
            alphaCutoff = mat.AlphaCutoff > 0f ? mat.AlphaCutoff : (doubleSided ? 0.35f : 0f);
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
        bool hasAlbedo = false, hasNormal = false, hasSpec = false, hasMetal = false, hasRough = false, hasAO = false, hasOpacity = false;
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

        if (mat != null && alphaCutoff <= 0f && hasOpacity)
        {
            alphaCutoff = 0.35f;
            shader.SetFloat("uAlphaCutoff", alphaCutoff);
        }

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
        shader.SetInt("uHasEmissiveTex", 0);
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
}
