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
        /// <summary>Bumped on every CPU edit so each GL context uploads its own copy.</summary>
        public int Revision;
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
    static readonly Dictionary<object, GrassGpu> s_gpus = new();
    static Texture2D? s_fallbackTex;
    static int s_bladeTotal;

    /// <summary>GL objects for one view. Scene and Game each have their own context.</summary>
    sealed class GrassGpu
    {
        public GL Gl = null!;
        public ShaderProgram? Shader;
        public uint Vao;
        public uint MeshVbo;
        public uint MeshEbo;
        public uint InstanceVbo;
        public int IndexCount;
        public bool Failed;
        public readonly Dictionary<Texture2D, GPUTexture> Textures = new();
        public readonly Dictionary<TexBatch, GrassBuf> Bufs = new();
    }

    sealed class GrassBuf
    {
        public uint Vbo;
        public int GpuFloats;
        public int Revision = -1;
    }

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

        string texKey = TextureKey(texturePath);
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

    static readonly Dictionary<string, string> s_textureKeys = new(StringComparer.Ordinal);
    static string? s_textureKeysRoot;

    /// <summary>
    /// Batch key for a texture path. Normalising is path arithmetic against the project
    /// root, and a walk plants hundreds of patches a frame from a handful of cards, so the
    /// answer is kept per spelling. Game thread only, like the rest of patch registration.
    /// </summary>
    static string TextureKey(string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath))
            return PlanetAssetIO.NormalizeAssetReference(texturePath ?? "");
        string? root = ProjectService.Current?.RootPath;
        if (!string.Equals(root, s_textureKeysRoot, StringComparison.Ordinal) || s_textureKeys.Count > 1024)
        {
            // Keys are relative to the project.
            s_textureKeys.Clear();
            s_textureKeysRoot = root;
        }
        if (!s_textureKeys.TryGetValue(texturePath, out var key))
        {
            key = PlanetAssetIO.NormalizeAssetReference(texturePath);
            s_textureKeys[texturePath] = key;
        }
        return key;
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
        batch.Revision++;
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
        batch.Revision++;
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
        if (!TryGetGpu(gl, cache, out var gpu) || gpu.Shader == null)
            return;
        if (s_batches.Count == 0)
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

        var shader = gpu.Shader;
        shader.Use();
        shader.SetMatrix4("uPlanetWorld", planetWorld);
        shader.SetMatrix4("uView", view);
        shader.SetMatrix4("uProj", proj);
        shader.SetVector3("uCamPos", camPos);
        shader.SetVector3("uLightDir", lightDir);
        shader.SetVector3("uLightColor", sunTint);
        shader.SetFloat("uAmbient", Math.Clamp(amb, 0.02f, 0.85f));
        shader.SetFloat("uDiffuseK", Math.Clamp(diffuseK, 0.20f, 1.35f));
        shader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.02f, 2f));
        shader.SetFloat("uAlphaCutoff", 0.32f);
        shader.SetFloat("uWetness", wetness);
        shader.SetFloat("uSnow", snow);
        shader.SetFloat("uRain", rain);
        shader.SetFloat("uStorm", storm);
        shader.SetFloat("uCloudiness", cloudiness);
        shader.SetFloat("uWindTime", WindSystem.Time);
        shader.SetFloat("uWindStrength", Math.Clamp(wind * 3.0f, 0.02f, 0.36f));
        shader.SetVector3("uWindDir", WindSystem.Direction.LengthSquared() > 1e-6f
            ? SN.Vector3.Normalize(WindSystem.Direction)
            : SN.Vector3.UnitX);
        shader.SetFloat("uCardWidth", Math.Clamp(owner.GrassWidthScale, 0.25f, 4f));
        shader.SetTexture("uAlbedoTex", 0);

        gl.BindVertexArray(gpu.Vao);
        // Enough unique cards per frame that the carpet stays textured once the mix is ready.
        int uploadsLeft = 16;

        for (int bi = 0; bi < s_batches.Count; bi++)
        {
            var batch = s_batches[bi];
            if (batch.Slots <= 0 || !ReferenceEquals(batch.Key.Owner, owner)) continue;
            if (!PlanetGrassTextureCache.TryGet(batch.Texture, out var cpuTex))
            {
                // Missing card: re-request and skip. Drawing CreateSharedCard here painted
                // the whole Game View as neon green/yellow static while the mix decoded.
                PlanetGrassTextureCache.Request(batch.Texture);
                continue;
            }
            if (!BindGrassTexture(gpu, cpuTex, ref uploadsLeft))
                continue;

            if (!gpu.Bufs.TryGetValue(batch, out var buf))
            {
                buf = new GrassBuf { Vbo = gl.GenBuffer() };
                gpu.Bufs[batch] = buf;
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf.Vbo);
            UploadBatch(gl, batch, buf);

            gl.EnableVertexAttribArray(2);
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribDivisor(2, 1);
            gl.VertexAttribDivisor(3, 1);

            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)0);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)(4 * sizeof(float)));
            uint indexCount = batch.SingleCard ? 6u : (uint)gpu.IndexCount;
            int instances = batch.Slots * batch.Blades;
#if XBOX_UWP
            // One draw of the whole carpet is what stops the GPU after you walk far enough
            // for it to fill. Same instances, several smaller draws.
            const int group = 64;
            int strideBytes = FloatsPerInstance * sizeof(float);
            for (int start = 0; start < instances; start += group)
            {
                int count = Math.Min(group, instances - start);
                int byteOff = start * strideBytes;
                gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)strideBytes, (void*)byteOff);
                gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, (uint)strideBytes, (void*)(byteOff + 4 * sizeof(float)));
                gl.DrawElementsInstanced(
                    PrimitiveType.Triangles,
                    indexCount,
                    DrawElementsType.UnsignedInt,
                    (void*)0,
                    (uint)count);
            }
#else
            gl.DrawElementsInstanced(
                PrimitiveType.Triangles,
                indexCount,
                DrawElementsType.UnsignedInt,
                (void*)0,
                (uint)instances);
#endif
        }

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
#if !XBOX_UWP
        // Leave the grass VAO instanced. Zeroing its divisors after the draw is what
        // made the carpet vanish, and a divisor left on is what warped the next models.
        // The default VAO is where UI and un-bound draws pick it up.
        gl.BindVertexArray(0);
        gl.VertexAttribDivisor(2, 0);
        gl.VertexAttribDivisor(3, 0);
#endif
        gl.Disable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(0f, 0f);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        if (prevCull) gl.Enable(EnableCap.CullFace);
        if (prevBlend) gl.Enable(EnableCap.Blend);
        gl.UseProgram(0);
    }

    /// <summary>
    /// Upload this context's copy when the CPU batch changed. Scene and Game do not
    /// share buffers, and the first view must not consume the update for the second.
    /// </summary>
    static unsafe void UploadBatch(GL gl, TexBatch batch, GrassBuf buf)
    {
        int floats = batch.Slots * batch.SlotFloats;
        if (floats <= 0 || floats > batch.Packed.Length)
            return;
        if (buf.Revision == batch.Revision && buf.GpuFloats >= floats)
            return;
        fixed (float* ptr = batch.Packed)
        {
            if (floats > buf.GpuFloats)
            {
                int cap = Math.Max(floats, buf.GpuFloats * 3 / 2);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), null, BufferUsageARB.DynamicDraw);
                buf.GpuFloats = cap;
            }
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(floats * sizeof(float)), ptr);
            buf.Revision = batch.Revision;
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

    static bool BindGrassTexture(GrassGpu gpu, Texture2D cpuTex, ref int uploadsLeft)
    {
        if (!gpu.Textures.TryGetValue(cpuTex, out var tex))
        {
            if (uploadsLeft <= 0)
            {
                // Never substitute CreateSharedCard here — that painted Game View as neon static.
                return false;
            }
            uploadsLeft--;
            tex = new GPUTexture(gpu.Gl);
            tex.UploadLinearClampNoMip(cpuTex);
            gpu.Textures[cpuTex] = tex;
        }
        tex.Bind(TextureUnit.Texture0);
        return true;
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

    static unsafe bool TryGetGpu(GL gl, object gpuKey, out GrassGpu gpu)
    {
        if (s_gpus.TryGetValue(gpuKey, out gpu!))
            return !gpu.Failed && gpu.Shader != null;

        gpu = new GrassGpu { Gl = gl };
        s_gpus[gpuKey] = gpu;
        try
        {
            bool es = true;
            try { es = gl.GetStringS(StringName.Version)?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) == true; }
            catch { es = true; }

            gpu.Shader = new ShaderProgram(gl,
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
                0,1,2, 0,2,3,
                4,5,6, 4,6,7
            };
            gpu.IndexCount = idx.Length;

            gpu.Vao = gl.GenVertexArray();
            gpu.MeshVbo = gl.GenBuffer();
            gpu.MeshEbo = gl.GenBuffer();
            gpu.InstanceVbo = gl.GenBuffer();

            gl.BindVertexArray(gpu.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.MeshVbo);
            fixed (float* vp = verts)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), vp, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gpu.MeshEbo);
            fixed (uint* ip = idx)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.InstanceVbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, 32, null, BufferUsageARB.StreamDraw);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)0);
            gl.VertexAttribDivisor(2, 1);
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, FloatsPerInstance * sizeof(float), (void*)(4 * sizeof(float)));
            gl.VertexAttribDivisor(3, 1);

#if !XBOX_UWP
            gl.BindVertexArray(0);
#endif
            return true;
        }
        catch (Exception ex)
        {
            gpu.Failed = true;
            Log.Error($"[PlanetGpuGrass] Failed to init instanced grass: {ex.Message}");
            return false;
        }
    }

    static void DisposeGpu(GrassGpu gpu)
    {
        var gl = gpu.Gl;
        try
        {
            if (gpu.Vao != 0) gl.DeleteVertexArray(gpu.Vao);
            if (gpu.MeshVbo != 0) gl.DeleteBuffer(gpu.MeshVbo);
            if (gpu.MeshEbo != 0) gl.DeleteBuffer(gpu.MeshEbo);
            if (gpu.InstanceVbo != 0) gl.DeleteBuffer(gpu.InstanceVbo);
            foreach (var buf in gpu.Bufs.Values)
            {
                if (buf.Vbo != 0) gl.DeleteBuffer(buf.Vbo);
            }
        }
        catch { }
        foreach (var tex in gpu.Textures.Values)
        {
            try { tex.Dispose(); } catch { }
        }
        gpu.Textures.Clear();
        gpu.Bufs.Clear();
        gpu.Shader?.Dispose();
        gpu.Shader = null;
        gpu.Vao = gpu.MeshVbo = gpu.MeshEbo = gpu.InstanceVbo = 0;
    }

    /// <summary>
    /// Release one view's grass GPU state. Must be called while that GL context is current.
    /// Scene and Game keep separate objects, so flushing one does not wipe the other.
    /// </summary>
    public static void ReleaseGpuFor(object gpuKey)
    {
        if (s_gpus.Remove(gpuKey, out var gpu))
            DisposeGpu(gpu);
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
