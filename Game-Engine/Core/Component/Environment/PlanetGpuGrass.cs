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
/// Planet grass as GPU instances: one shared cross-card mesh, one draw per
/// (texture, blades-per-patch) batch. Each patch owns a fixed slot in its batch,
/// so adding, re-seating or removing a patch rewrites and uploads only that slot.
/// </summary>
public static class PlanetGpuGrass
{
    const int FloatsPerInstance = 8;
    const int MaxBladesPerPatch = 16;
    /// <summary>Above this many changed slots in a frame, upload one covering range instead.</summary>
    const int MaxSparseUploads = 32;

    readonly record struct Key(PlanetVegetationSystem Owner, int Token);
    readonly record struct BatchKey(PlanetVegetationSystem Owner, string Texture, int Blades, bool SingleCard);

    sealed class Patch
    {
        public TexBatch Batch = null!;
        public int Slot;
    }

    sealed class TexBatch
    {
        public BatchKey Key;
        public string Texture = "";
        public int Blades;
        /// <summary>Draw only the first quad of the cross card (distant grass).</summary>
        public bool SingleCard;
        public float[] Packed = Array.Empty<float>();
        public readonly List<Key> SlotOwners = new();
        public uint Vbo;
        public int GpuFloats;
        public bool FullUpload = true;
        public int DirtyMin = int.MaxValue;
        public int DirtyMax = -1;
        public readonly List<int> DirtySlots = new();

        public int Slots => SlotOwners.Count;
        public int SlotFloats => Blades * FloatsPerInstance;

        public void MarkDirty(int slot)
        {
            if (slot < DirtyMin) DirtyMin = slot;
            if (slot > DirtyMax) DirtyMax = slot;
            if (DirtySlots.Count <= MaxSparseUploads)
                DirtySlots.Add(slot);
        }

        public void ClearDirty()
        {
            DirtyMin = int.MaxValue;
            DirtyMax = -1;
            DirtySlots.Clear();
        }
    }

    static readonly Dictionary<Key, Patch> s_patches = new();
    static readonly List<TexBatch> s_batches = new();
    static readonly Dictionary<BatchKey, TexBatch> s_batchByKey = new();
    static readonly float[] s_bladeScratch = new float[MaxBladesPerPatch * FloatsPerInstance];
    static readonly Dictionary<Texture2D, GPUTexture> s_gpuTex = new();
    static Texture2D? s_fallbackTex;
    static int s_bladeTotal;

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

    public static int BladeCount => s_bladeTotal;

    public static bool HasPatch(PlanetVegetationSystem owner, int token)
        => s_patches.ContainsKey(new Key(owner, token));

    public static int PatchCount(PlanetVegetationSystem owner)
    {
        int n = 0;
        for (int i = 0; i < s_batches.Count; i++)
        {
            var b = s_batches[i];
            if (ReferenceEquals(b.Key.Owner, owner))
                n += b.Slots;
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
        string? texturePath = null,
        SN.Vector3? diskNormalLocal = null,
        bool singleCard = false)
    {
        if (owner == null)
            return 0;

        var up = SafeNormalize(upLocal, SN.Vector3.UnitY);
        // Blade roots scatter on the ground plane (true surface normal), while the
        // blades themselves lean with the softer `up`. Using `up` for the disk left
        // edge blades hanging off hillsides.
        var disk = diskNormalLocal.HasValue ? SafeNormalize(diskNormalLocal.Value, up) : up;
        var t = SN.Vector3.Cross(MathF.Abs(disk.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, disk);
        if (t.LengthSquared() < 1e-8f) t = SN.Vector3.UnitX;
        t = SN.Vector3.Normalize(t);
        var b = SN.Vector3.Normalize(SN.Vector3.Cross(disk, t));

        float localR = Math.Max(0.04f, patchRadiusLocal);
        float localH = Math.Max(0.06f, localHeight);
        int blades = Math.Clamp(bladeCount, 4, MaxBladesPerPatch);
        var packed = s_bladeScratch;
        // Token-only seed: a re-seat (same token, new height) must not reshuffle blades.
        uint seed = unchecked((uint)token * 0x9E3779B1u);
        for (int i = 0; i < blades; i++)
        {
            // Integer hash → [0,1). `Fract(seed * 0.1031f)` on a ~1e9 seed is a float
            // with no fractional bits, so every blade landed at the disk centre with the
            // same yaw — each clump was one card drawn 12 times.
            uint h = Hash32(seed + (uint)i * 0x85EBCA6Bu);
            float u1 = (h & 0xFFFFFF) * (1f / 16777216f);
            h = Hash32(h ^ 0x27D4EB2Fu);
            float u2 = (h & 0xFFFFFF) * (1f / 16777216f);
            h = Hash32(h ^ 0x165667B1u);
            float u3 = (h & 0xFFFFFF) * (1f / 16777216f);
            float ang = u1 * MathF.Tau;
            float rad = MathF.Sqrt(u2) * localR;
            var pos = centerLocal + t * (MathF.Cos(ang) * rad) + b * (MathF.Sin(ang) * rad);
            float yaw = yawDeg * (MathF.PI / 180f) + u3 * MathF.Tau;
            float scale = localH * (0.82f + u2 * 0.45f);
            int o = i * FloatsPerInstance;
            packed[o] = pos.X;
            packed[o + 1] = pos.Y;
            packed[o + 2] = pos.Z;
            packed[o + 3] = scale;
            packed[o + 4] = up.X;
            packed[o + 5] = up.Y;
            packed[o + 6] = up.Z;
            packed[o + 7] = yaw;
        }

        string texKey = PlanetAssetIO.NormalizeAssetReference(texturePath ?? "");
        if (!string.IsNullOrWhiteSpace(texKey))
            PlanetGrassTextureCache.Request(texKey);

        var key = new Key(owner, token);
        var batch = GetBatch(owner, texKey, blades, singleCard);
        if (s_patches.TryGetValue(key, out var patch))
        {
            if (ReferenceEquals(patch.Batch, batch))
            {
                WriteSlot(batch, patch.Slot, packed);
                return blades;
            }
            ReleaseSlot(patch.Batch, patch.Slot);
        }
        else
        {
            patch = new Patch();
            s_patches[key] = patch;
        }

        int slot = batch.Slots;
        int need = (slot + 1) * batch.SlotFloats;
        if (batch.Packed.Length < need)
            Array.Resize(ref batch.Packed, Math.Max(need, Math.Max(batch.SlotFloats * 64, batch.Packed.Length * 2)));
        batch.SlotOwners.Add(key);
        patch.Batch = batch;
        patch.Slot = slot;
        WriteSlot(batch, slot, packed);
        s_bladeTotal += blades;
        return blades;
    }

    public static void RemovePatch(PlanetVegetationSystem owner, int token)
    {
        if (s_patches.Remove(new Key(owner, token), out var patch))
            ReleaseSlot(patch.Batch, patch.Slot);
    }

    public static void ClearOwner(PlanetVegetationSystem owner)
    {
        if (owner == null) return;
        for (int i = 0; i < s_batches.Count; i++)
        {
            var b = s_batches[i];
            if (!ReferenceEquals(b.Key.Owner, owner) || b.Slots == 0) continue;
            for (int s = 0; s < b.SlotOwners.Count; s++)
                s_patches.Remove(b.SlotOwners[s]);
            s_bladeTotal -= b.Slots * b.Blades;
            b.SlotOwners.Clear();
            b.ClearDirty();
        }
    }

    static TexBatch GetBatch(PlanetVegetationSystem owner, string texKey, int blades, bool singleCard)
    {
        var bk = new BatchKey(owner, texKey, blades, singleCard);
        if (s_batchByKey.TryGetValue(bk, out var batch))
            return batch;
        batch = new TexBatch { Key = bk, Texture = texKey, Blades = blades, SingleCard = singleCard };
        s_batchByKey[bk] = batch;
        s_batches.Add(batch);
        return batch;
    }

    static void WriteSlot(TexBatch batch, int slot, float[] src)
    {
        Array.Copy(src, 0, batch.Packed, slot * batch.SlotFloats, batch.SlotFloats);
        batch.MarkDirty(slot);
    }

    /// <summary>Swap the last slot into the freed one so the batch stays packed.</summary>
    static void ReleaseSlot(TexBatch batch, int slot)
    {
        int last = batch.Slots - 1;
        if (last < 0 || slot < 0 || slot > last)
            return;
        if (slot != last)
        {
            int sf = batch.SlotFloats;
            Array.Copy(batch.Packed, last * sf, batch.Packed, slot * sf, sf);
            var moved = batch.SlotOwners[last];
            batch.SlotOwners[slot] = moved;
            if (s_patches.TryGetValue(moved, out var movedPatch))
                movedPatch.Slot = slot;
            batch.MarkDirty(slot);
        }
        batch.SlotOwners.RemoveAt(last);
        s_bladeTotal -= batch.Blades;
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
        // Breeze sway — was wind*10 (often clamped to storm max) + heavy rain kick.
        float wind = WindSystem.GetCurrentStrength() * Math.Clamp(windMul, 0.2f, 1.15f);
        float storm = rain >= 0.85f ? 1f : 0f;
        var sunTint = lightColor.LengthSquared() > 1e-6f
            ? lightColor
            : new SN.Vector3(1f, 0.96f, 0.88f);
        // Prefer atmosphere night values over a brighter scene ambient floor.
        float amb = Math.Min(Math.Max(0.02f, ambient), Math.Max(0.02f, atmoAmbient));

        s_shader.Use();
        s_shader.SetMatrix4("uPlanetWorld", planetWorld);
        s_shader.SetMatrix4("uView", view);
        s_shader.SetMatrix4("uProj", proj);
        s_shader.SetVector3("uCamPos", camPos);
        s_shader.SetVector3("uLightDir", lightDir);
        s_shader.SetVector3("uLightColor", sunTint);
        s_shader.SetFloat("uAmbient", Math.Clamp(amb, 0.02f, 0.85f));
        s_shader.SetFloat("uDiffuseK", Math.Clamp(diffuseK, 0.20f, 1.35f));
        s_shader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.02f, 2f));
        s_shader.SetFloat("uAlphaCutoff", 0.32f);
        s_shader.SetFloat("uWetness", wetness);
        s_shader.SetFloat("uSnow", snow);
        s_shader.SetFloat("uRain", rain);
        s_shader.SetFloat("uStorm", storm);
        s_shader.SetFloat("uCloudiness", cloudiness);
        s_shader.SetFloat("uWindTime", WindSystem.Time);
        s_shader.SetFloat("uWindStrength", Math.Clamp(wind * 3.0f, 0.02f, 0.36f));
        s_shader.SetVector3("uWindDir", WindSystem.Direction.LengthSquared() > 1e-6f
            ? SN.Vector3.Normalize(WindSystem.Direction)
            : SN.Vector3.UnitX);
        s_shader.SetFloat("uCardWidth", Math.Clamp(owner.GrassWidthScale, 0.25f, 4f));
        s_shader.SetTexture("uAlbedoTex", 0);

        gl.BindVertexArray(s_vao);
        int uploadsLeft = 4;

        for (int bi = 0; bi < s_batches.Count; bi++)
        {
            var batch = s_batches[bi];
            if (batch.Slots <= 0 || !ReferenceEquals(batch.Key.Owner, owner)) continue;
            if (!PlanetGrassTextureCache.TryGet(batch.Texture, out var cpuTex))
            {
                // Card not loaded (or evicted): re-request and skip rather than draw
                // the flat green stand-in — that is the "untextured mesh" look.
                PlanetGrassTextureCache.Request(batch.Texture);
                continue;
            }
            BindGrassTexture(gl, cpuTex, ref uploadsLeft);

            if (batch.Vbo == 0)
            {
                batch.Vbo = gl.GenBuffer();
                batch.GpuFloats = 0;
                batch.FullUpload = true;
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.Vbo);
            UploadBatch(gl, batch);

            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)0);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)(4 * sizeof(float)));
            gl.DrawElementsInstanced(
                PrimitiveType.Triangles,
                batch.SingleCard ? 6u : (uint)s_indexCount,
                DrawElementsType.UnsignedInt,
                null,
                (uint)(batch.Slots * batch.Blades));
        }

        gl.BindVertexArray(0);
        gl.Disable(EnableCap.PolygonOffsetFill);
        if (prevCull) gl.Enable(EnableCap.CullFace);
        if (prevBlend) gl.Enable(EnableCap.Blend);
        gl.UseProgram(0);
    }

    /// <summary>
    /// Upload only what changed since last draw. The old path repacked and re-uploaded
    /// every blade of every texture whenever one patch moved, every frame while walking.
    /// </summary>
    static unsafe void UploadBatch(GL gl, TexBatch batch)
    {
        int floats = batch.Slots * batch.SlotFloats;
        int sf = batch.SlotFloats;
        fixed (float* ptr = batch.Packed)
        {
            if (floats > batch.GpuFloats || batch.FullUpload)
            {
                if (floats > batch.GpuFloats)
                {
                    int cap = Math.Max(floats, batch.GpuFloats * 3 / 2);
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), null, BufferUsageARB.DynamicDraw);
                    batch.GpuFloats = cap;
                }
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(floats * sizeof(float)), ptr);
                batch.FullUpload = false;
                batch.ClearDirty();
                return;
            }

            if (batch.DirtyMax < 0)
                return;
            int lastSlot = batch.Slots - 1;
            if (batch.DirtySlots.Count <= MaxSparseUploads)
            {
                for (int i = 0; i < batch.DirtySlots.Count; i++)
                {
                    int s = batch.DirtySlots[i];
                    if (s > lastSlot) continue;
                    gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)(s * sf * sizeof(float)), (nuint)(sf * sizeof(float)), ptr + s * sf);
                }
            }
            else
            {
                int lo = Math.Max(0, batch.DirtyMin);
                int hi = Math.Min(lastSlot, batch.DirtyMax);
                if (hi >= lo)
                    gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)(lo * sf * sizeof(float)), (nuint)((hi - lo + 1) * sf * sizeof(float)), ptr + lo * sf);
            }
            batch.ClearDirty();
        }
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
            // Render() disables face culling, so a single winding per quad is enough.
            // The old set drew every card twice (both windings) = 2x fragment work.
            uint[] idx =
            {
                0,1,2, 0,2,3,
                4,5,6, 4,6,7
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
            for (int i = 0; i < s_batches.Count; i++)
            {
                var b = s_batches[i];
                if (b.Vbo != 0) s_gl.DeleteBuffer(b.Vbo);
            }
        }
        catch { }
        for (int i = 0; i < s_batches.Count; i++)
        {
            s_batches[i].Vbo = 0;
            s_batches[i].GpuFloats = 0;
            s_batches[i].FullUpload = true;
            s_batches[i].ClearDirty();
        }
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

    /// <summary>
    /// Release the static grass GPU state when its owning render-context cache is
    /// flushed or disposed. Must be called while that GL context is current.
    /// </summary>
    public static void ReleaseGpuFor(object gpuKey)
    {
        if (ReferenceEquals(s_gpuKey, gpuKey))
            DisposeGpu();
    }

    static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
    {
        float len = v.Length();
        return len > 1e-8f ? v / len : fallback;
    }

    static uint Hash32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }
}
