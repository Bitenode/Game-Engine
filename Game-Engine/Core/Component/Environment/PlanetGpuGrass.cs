#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Planet;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Planet grass as GPU instances: one shared cross-card mesh, one instance buffer,
/// one draw per unique texture. Replaces per-patch CPU mesh merges.
/// </summary>
public static class PlanetGpuGrass
{
    const int FloatsPerInstance = 8;
    const int MaxBladesPerPatch = 16;

    readonly record struct Key(PlanetVegetationSystem Owner, int Token);

    sealed class Patch
    {
        public float[] Blades = Array.Empty<float>();
        public int BladeCount;
        public string TextureKey = "";
    }

    sealed class TexBatch
    {
        public string Key = "";
        public float[] Packed = Array.Empty<float>();
        public int Count;
    }

    static readonly Dictionary<Key, Patch> s_patches = new();
    static readonly List<TexBatch> s_batches = new();
    static readonly Dictionary<Texture2D, GPUTexture> s_gpuTex = new();
    static Texture2D? s_fallbackTex;
    static bool s_batchDirty = true;

    static GL? s_gl;
    static object? s_gpuKey;
    static ShaderProgram? s_shader;
    static uint s_vao;
    static uint s_meshVbo;
    static uint s_meshEbo;
    static uint s_instanceVbo;
    static int s_indexCount;
    static bool s_gpuReady;
    static bool s_gpuFailed;

    public static int BladeCount
    {
        get
        {
            int n = 0;
            foreach (var p in s_patches.Values)
                n += p.BladeCount;
            return n;
        }
    }

    public static bool HasPatch(PlanetVegetationSystem owner, int token)
        => s_patches.ContainsKey(new Key(owner, token));

    public static int PatchCount(PlanetVegetationSystem owner)
    {
        int n = 0;
        foreach (var k in s_patches.Keys)
        {
            if (ReferenceEquals(k.Owner, owner))
                n++;
        }
        return n;
    }

    public static int RegisterPatch(
        PlanetVegetationSystem owner,
        int token,
        SN.Vector3 centerLocal,
        SN.Vector3 upLocal,
        float localHeight,
        float yawDeg,
        float patchRadiusLocal,
        int bladeCount,
        string? texturePath = null)
    {
        if (owner == null)
            return 0;

        var up = SafeNormalize(upLocal, SN.Vector3.UnitY);
        var t = SN.Vector3.Cross(MathF.Abs(up.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, up);
        if (t.LengthSquared() < 1e-8f) t = SN.Vector3.UnitX;
        t = SN.Vector3.Normalize(t);
        var b = SN.Vector3.Normalize(SN.Vector3.Cross(up, t));

        float localR = Math.Max(0.04f, patchRadiusLocal);
        float localH = Math.Max(0.06f, localHeight);
        int blades = Math.Clamp(bladeCount, 4, MaxBladesPerPatch);
        var packed = new float[blades * FloatsPerInstance];
        int seed = Hash(centerLocal) ^ (token * 397);
        int written = 0;
        for (int i = 0; i < blades; i++)
        {
            float u1 = Fract(seed * 0.1031f + i * 0.17f);
            float u2 = Fract(seed * 0.2101f + i * 0.31f);
            float u3 = Fract(seed * 0.3771f + i * 0.53f);
            float ang = u1 * MathF.Tau;
            float rad = MathF.Sqrt(u2) * localR;
            var pos = centerLocal + t * (MathF.Cos(ang) * rad) + b * (MathF.Sin(ang) * rad);
            float yaw = yawDeg * (MathF.PI / 180f) + u3 * MathF.Tau;
            float scale = localH * (0.82f + u2 * 0.45f);
            int o = written * FloatsPerInstance;
            packed[o] = pos.X;
            packed[o + 1] = pos.Y;
            packed[o + 2] = pos.Z;
            packed[o + 3] = scale;
            packed[o + 4] = up.X;
            packed[o + 5] = up.Y;
            packed[o + 6] = up.Z;
            packed[o + 7] = yaw;
            written++;
        }

        string texKey = PlanetAssetIO.NormalizeAssetReference(texturePath ?? "");
        if (!string.IsNullOrWhiteSpace(texKey))
            PlanetGrassTextureCache.Request(texKey);
        s_patches[new Key(owner, token)] = new Patch
        {
            Blades = packed,
            BladeCount = written,
            TextureKey = texKey
        };
        s_batchDirty = true;
        return written;
    }

    public static void RemovePatch(PlanetVegetationSystem owner, int token)
    {
        if (s_patches.Remove(new Key(owner, token)))
            s_batchDirty = true;
    }

    public static void ClearOwner(PlanetVegetationSystem owner)
    {
        if (owner == null) return;
        var dead = new List<Key>();
        foreach (var k in s_patches.Keys)
        {
            if (ReferenceEquals(k.Owner, owner))
                dead.Add(k);
        }
        if (dead.Count == 0) return;
        for (int i = 0; i < dead.Count; i++)
            s_patches.Remove(dead[i]);
        s_batchDirty = true;
    }

    public static unsafe void Render(
        GL gl,
        ResourceCache cache,
        PlanetVegetationSystem owner,
        in SN.Matrix4x4 planetWorld,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        SN.Vector3 camPos,
        SN.Vector3 lightDir,
        float ambient,
        float diffuseK,
        SN.Vector3 lightColor = default)
    {
        if (owner == null || s_patches.Count == 0)
            return;
        if (!owner.ShouldDrawGpuGrass(camPos))
            return;
        if (!EnsureGpu(gl, cache))
            return;

        RebuildPackedIfNeeded(owner);
        if (s_batches.Count == 0 || s_shader == null)
            return;

        var prevCull = gl.IsEnabled(EnableCap.CullFace);
        var prevBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(-1.5f, -1.5f);

        owner.GetGpuGrassEnvironment(out float wetness, out float snow, out float rain, out float cloudiness, out float windMul, out float sunIntensity, out float atmoAmbient);
        float wind = WindSystem.GetCurrentStrength() * Math.Max(0.2f, windMul);
        float storm = rain >= 0.85f ? 1f : 0f;
        var sunTint = lightColor.LengthSquared() > 1e-6f
            ? lightColor
            : new SN.Vector3(1f, 0.96f, 0.88f);
        float amb = Math.Max(ambient, atmoAmbient);

        s_shader.Use();
        s_shader.SetMatrix4("uPlanetWorld", planetWorld);
        s_shader.SetMatrix4("uView", view);
        s_shader.SetMatrix4("uProj", proj);
        s_shader.SetVector3("uCamPos", camPos);
        s_shader.SetVector3("uLightDir", lightDir);
        s_shader.SetVector3("uLightColor", sunTint);
        s_shader.SetFloat("uAmbient", Math.Clamp(amb, 0.10f, 0.85f));
        s_shader.SetFloat("uDiffuseK", Math.Clamp(diffuseK, 0.20f, 1.35f));
        s_shader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.12f, 2f));
        s_shader.SetFloat("uAlphaCutoff", 0.32f);
        s_shader.SetFloat("uWetness", wetness);
        s_shader.SetFloat("uSnow", snow);
        s_shader.SetFloat("uRain", rain);
        s_shader.SetFloat("uStorm", storm);
        s_shader.SetFloat("uCloudiness", cloudiness);
        s_shader.SetFloat("uWindTime", WindSystem.Time);
        s_shader.SetFloat("uWindStrength", Math.Clamp(wind * 10f, 0.03f, 1.45f));
        s_shader.SetVector3("uWindDir", WindSystem.Direction.LengthSquared() > 1e-6f
            ? SN.Vector3.Normalize(WindSystem.Direction)
            : SN.Vector3.UnitX);
        s_shader.SetTexture("uAlbedoTex", 0);

        gl.BindVertexArray(s_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, s_instanceVbo);
        int uploadsLeft = 4;

        for (int b = 0; b < s_batches.Count; b++)
        {
            var batch = s_batches[b];
            if (batch.Count <= 0) continue;
            var cpuTex = ResolveCpuTexture(batch.Key);
            BindGrassTexture(gl, cpuTex, ref uploadsLeft);
            fixed (float* ptr = batch.Packed)
            {
                gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    (nuint)(batch.Count * FloatsPerInstance * sizeof(float)),
                    ptr,
                    BufferUsageARB.DynamicDraw);
            }
            gl.DrawElementsInstanced(
                PrimitiveType.Triangles,
                (uint)s_indexCount,
                DrawElementsType.UnsignedInt,
                null,
                (uint)batch.Count);
        }

        gl.BindVertexArray(0);
        gl.Disable(EnableCap.PolygonOffsetFill);
        if (prevCull) gl.Enable(EnableCap.CullFace);
        if (prevBlend) gl.Enable(EnableCap.Blend);
        gl.UseProgram(0);
    }

    static Texture2D ResolveCpuTexture(string key)
    {
        if (PlanetGrassTextureCache.TryGet(key, out var tex))
            return tex;
        // Do not substitute another PSD — that is what made planted grass
        // morph through the catalog as files finished loading.
        s_fallbackTex ??= CreateSharedCard();
        return s_fallbackTex;
    }

    static void BindGrassTexture(GL gl, Texture2D cpuTex, ref int uploadsLeft)
    {
        if (!s_gpuTex.TryGetValue(cpuTex, out var gpu))
        {
            if (uploadsLeft <= 0)
            {
                s_fallbackTex ??= CreateSharedCard();
                if (!s_gpuTex.TryGetValue(s_fallbackTex, out gpu))
                {
                    gpu = new GPUTexture(gl);
                    gpu.UploadLinearClampNoMip(s_fallbackTex);
                    s_gpuTex[s_fallbackTex] = gpu;
                }
                gpu.Bind(TextureUnit.Texture0);
                return;
            }
            uploadsLeft--;
            gpu = new GPUTexture(gl);
            gpu.UploadLinearClampNoMip(cpuTex);
            s_gpuTex[cpuTex] = gpu;
        }
        gpu.Bind(TextureUnit.Texture0);
    }

    static void RebuildPackedIfNeeded(PlanetVegetationSystem owner)
    {
        if (!s_batchDirty)
            return;
        s_batchDirty = false;
        s_batches.Clear();
        var map = new Dictionary<string, TexBatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in s_patches)
        {
            if (!ReferenceEquals(kv.Key.Owner, owner)) continue;
            var patch = kv.Value;
            if (patch.BladeCount <= 0) continue;
            string key = patch.TextureKey ?? "";
            if (!map.TryGetValue(key, out var batch))
            {
                batch = new TexBatch { Key = key };
                map[key] = batch;
                s_batches.Add(batch);
            }
            int n = patch.BladeCount * FloatsPerInstance;
            int start = batch.Count * FloatsPerInstance;
            if (batch.Packed.Length < start + n)
                Array.Resize(ref batch.Packed, Math.Max(start + n, 64));
            Array.Copy(patch.Blades, 0, batch.Packed, start, n);
            batch.Count += patch.BladeCount;
        }
    }

    static Texture2D CreateSharedCard()
    {
        const int w = 32;
        const int h = 48;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1);
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                float dx = (u - 0.5f) * 2f;
                float blade = MathF.Max(0f, 1f - MathF.Abs(dx) / (0.22f + (1f - v) * 0.55f));
                float tip = 1f - v;
                float a = blade * MathF.Min(1f, tip * 3.2f);
                int o = (y * w + x) * 4;
                rgba[o] = 48;
                rgba[o + 1] = (byte)(110 + tip * 50);
                rgba[o + 2] = 36;
                rgba[o + 3] = (byte)(a * 255f);
            }
        }
        return new Texture2D(w, h, rgba);
    }

    static unsafe bool EnsureGpu(GL gl, object gpuKey)
    {
        if (s_gpuReady && ReferenceEquals(s_gl, gl) && ReferenceEquals(s_gpuKey, gpuKey) && s_shader != null)
            return true;
        s_gpuFailed = false;

        try
        {
            DisposeGpu();
            s_gl = gl;
            s_gpuKey = gpuKey;
            bool es = true;
            try { es = gl.GetStringS(StringName.Version)?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) == true; }
            catch { es = true; }

            s_shader = new ShaderProgram(gl,
                ShaderSources.Adapt(ShaderSources.PlanetGpuGrassVert, es),
                ShaderSources.Adapt(ShaderSources.PlanetGpuGrassFrag, es));

            float halfW = 0.32f;
            float[] verts =
            {
                -halfW, 0f, 0f,  0f, 1f,
                 halfW, 0f, 0f,  1f, 1f,
                 halfW, 1f, 0f,  1f, 0f,
                -halfW, 1f, 0f,  0f, 0f,
                 0f, 0f, -halfW, 0f, 1f,
                 0f, 0f,  halfW, 1f, 1f,
                 0f, 1f,  halfW, 1f, 0f,
                 0f, 1f, -halfW, 0f, 0f,
            };
            uint[] idx =
            {
                0,1,2, 0,2,3, 0,2,1, 0,3,2,
                4,5,6, 4,6,7, 4,6,5, 4,7,6
            };
            s_indexCount = idx.Length;

            s_vao = gl.GenVertexArray();
            s_meshVbo = gl.GenBuffer();
            s_meshEbo = gl.GenBuffer();
            s_instanceVbo = gl.GenBuffer();

            gl.BindVertexArray(s_vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, s_meshVbo);
            fixed (float* vp = verts)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), vp, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, s_meshEbo);
            fixed (uint* ip = idx)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, s_instanceVbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, 32, null, BufferUsageARB.StreamDraw);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)0);
            gl.VertexAttribDivisor(2, 1);
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)(4 * sizeof(float)));
            gl.VertexAttribDivisor(3, 1);

            gl.BindVertexArray(0);
            s_gpuReady = true;
            return true;
        }
        catch (Exception ex)
        {
            s_gpuFailed = true;
            Log.Error($"[PlanetGpuGrass] Failed to init instanced grass: {ex.Message}");
            return false;
        }
    }

    static void DisposeGpu()
    {
        if (s_gl == null) return;
        try
        {
            if (s_vao != 0) s_gl.DeleteVertexArray(s_vao);
            if (s_meshVbo != 0) s_gl.DeleteBuffer(s_meshVbo);
            if (s_meshEbo != 0) s_gl.DeleteBuffer(s_meshEbo);
            if (s_instanceVbo != 0) s_gl.DeleteBuffer(s_instanceVbo);
        }
        catch { }
        foreach (var tex in s_gpuTex.Values)
        {
            try { tex.Dispose(); } catch { }
        }
        s_gpuTex.Clear();
        s_shader?.Dispose();
        s_shader = null;
        s_vao = s_meshVbo = s_meshEbo = s_instanceVbo = 0;
        s_gpuReady = false;
        s_gl = null;
        s_gpuKey = null;
    }

    static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
    {
        float len = v.Length();
        return len > 1e-8f ? v / len : fallback;
    }

    static int Hash(SN.Vector3 v)
        => HashCode.Combine(BitConverter.SingleToInt32Bits(v.X),
            BitConverter.SingleToInt32Bits(v.Y),
            BitConverter.SingleToInt32Bits(v.Z));

    static float Fract(float x) => x - MathF.Floor(x);
}
