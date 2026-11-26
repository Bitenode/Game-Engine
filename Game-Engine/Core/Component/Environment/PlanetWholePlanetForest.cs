#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Planet;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Tier 2 whole-planet GPU-instanced forest canopy layer.
/// Bakes 30k–50k lightweight low-poly tree canopies across all 6 cube faces of a
/// <see cref="PlanetTerrain"/> on a background worker using <see cref="PlanetSurfaceCubemap"/>,
/// uploads them once to a static per-GL-context VBO (0 B/frame upload once baked), and renders
/// them in 1 draw call at every camera altitude with smooth Tier 1 <-> Tier 2 crossfade.
/// </summary>
public static class PlanetWholePlanetForest
{
    public const int FloatsPerInstance = 12; // vec4 PosScale + vec4 UpYaw + vec4 TintSeed
    public const int StrideBytes = FloatsPerInstance * sizeof(float); // 48 bytes

#if XBOX_UWP
    const int MaxInstances = 16000;
    const int CellsPerFace = 68;
#else
    const int MaxInstances = 48000;
    const int CellsPerFace = 116;
#endif

    sealed class PlanetForestState
    {
        public readonly object Lock = new();
        public float[] Instances = Array.Empty<float>();
        public int InstanceCount;
        public int BakedHeightVersion = -1;
        public int BakedSplatVersion = -1;
        public ulong BakedRecipeHash;
        public int FullRevision;
        public int EditRevision;
        public int DirtyMin = int.MaxValue;
        public int DirtyMax = -1;
        public volatile bool Baking;
        public volatile bool RebakeQueued;
    }

    sealed class ForestBuf
    {
        public uint Vbo;
        public int GpuCapacityFloats;
        public int UploadedFullRev = -1;
        public int UploadedEditRev = -1;
    }

    sealed class ForestGpu
    {
        public GL Gl = null!;
        public ShaderProgram? Shader;
        public long SeenReloadGen;
        public uint Vao;
        public uint MeshVbo;
        public uint MeshEbo;
        public int IndexCount;
        public bool Failed;
        public readonly Dictionary<PlanetTerrain, ForestBuf> Bufs = new();
    }

    static readonly object s_statesLock = new();
    static readonly Dictionary<PlanetTerrain, PlanetForestState> s_states = new();
    static readonly Dictionary<object, ForestGpu> s_gpus = new();

    /// <summary>Total baked canopy instances currently resident for <paramref name="terrain"/>.</summary>
    public static int GetInstanceCount(PlanetTerrain? terrain)
    {
        if (terrain == null) return 0;
        PlanetForestState? state;
        lock (s_statesLock)
            s_states.TryGetValue(terrain, out state);
        if (state == null) return 0;
        lock (state.Lock)
            return state.InstanceCount;
    }

    /// <summary>
    /// Ensure a background whole-planet canopy bake has been scheduled for <paramref name="terrain"/>
    /// once <see cref="PlanetTerrain.HasBakedStandSurface"/> is true, and rebake if cubemap versions changed.
    /// </summary>
    public static void EnsureBaked(PlanetTerrain? terrain, PlanetVegetationSystem? veg = null)
    {
        if (terrain == null || !terrain.HasBakedStandSurface)
            return;
        var cubemap = terrain.SurfaceCubemap;
        var config = terrain.Config;
        if (cubemap == null || config == null)
            return;

        PlanetForestState state;
        lock (s_statesLock)
        {
            if (!s_states.TryGetValue(terrain, out state!))
            {
                state = new PlanetForestState();
                s_states[terrain] = state;
            }
        }

        int hv = cubemap.HeightVersion;
        int sv = cubemap.SplatVersion;
        ulong rh = cubemap.RecipeHash;

        lock (state.Lock)
        {
            if (state.BakedHeightVersion == hv && state.BakedSplatVersion == sv && state.BakedRecipeHash == rh)
                return;
            if (state.Baking)
            {
                state.RebakeQueued = true;
                return;
            }
            state.Baking = true;
            state.RebakeQueued = false;
        }

        float treeBaseHeight = veg != null ? Math.Clamp(veg.TreeBaseHeight, 2f, 24f) : 7f;
        bool enableWater = terrain.EnableWater;
        float planetRadius = MathF.Max(1f, config.Radius);
        float seaLevel = config.SeaLevel;
        var biomes = config.Biomes ?? Array.Empty<BiomeDefinition>();
        // Snapshot lightweight biome specs so background worker never races with Inspector edits.
        var biomeSpecs = new BiomeTreeSpec[Math.Max(PlanetSurfaceCubemap.SplatLayerCount, biomes.Length)];
        for (int i = 0; i < biomes.Length && i < biomeSpecs.Length; i++)
        {
            var b = biomes[i];
            biomeSpecs[i] = BuildBiomeSpec(b);
        }

        // Snapshot cubemap arrays references (replaced wholesale on rebake; safe to read).
        int res = cubemap.Resolution;
        var heightFaces = cubemap.Height;
        var deltaFaces = cubemap.HeightDelta;
        var splat0Faces = cubemap.Splat0;
        var splat1Faces = cubemap.Splat1;

        Task.Run(() =>
        {
            try
            {
                BakeOnWorker(
                    state,
                    res,
                    heightFaces,
                    deltaFaces,
                    splat0Faces,
                    splat1Faces,
                    planetRadius,
                    seaLevel,
                    enableWater,
                    treeBaseHeight,
                    biomeSpecs,
                    hv,
                    sv,
                    rh);
            }
            catch (Exception ex)
            {
                Log.Warning($"[PlanetWholePlanetForest] Bake failed: {ex.Message}");
                lock (state.Lock)
                    state.Baking = false;
            }
        });
    }

    readonly struct BiomeTreeSpec
    {
        public readonly float TreeDensity;
        public readonly float MinScale;
        public readonly float MaxScale;
        public readonly float MaxSlopeTan;
        public readonly SN.Vector3 FoliageTint;

        public BiomeTreeSpec(float density, float minScale, float maxScale, float maxSlopeTan, SN.Vector3 tint)
        {
            TreeDensity = density;
            MinScale = minScale;
            MaxScale = maxScale;
            MaxSlopeTan = maxSlopeTan;
            FoliageTint = tint;
        }
    }

    static BiomeTreeSpec BuildBiomeSpec(BiomeDefinition? b)
    {
        if (b == null)
            return new BiomeTreeSpec(0f, 0.85f, 1.25f, 0.58f, new SN.Vector3(0.20f, 0.42f, 0.17f));

        float density = Math.Clamp(b.TreeDensity, 0f, 2f);
        float minS = Math.Clamp(b.TreeMinScale > 0.05f ? b.TreeMinScale : 0.85f, 0.35f, 3f);
        float maxS = Math.Clamp(b.TreeMaxScale >= minS ? b.TreeMaxScale : minS + 0.35f, minS, 4f);
        float maxSlopeDeg = Math.Clamp(b.TreeMaxSlope > 1f ? b.TreeMaxSlope : 34f, 8f, 60f);
        float maxSlopeTan = MathF.Tan(maxSlopeDeg * (MathF.PI / 180f));

        // Derive a rich biome-appropriate foliage tint from biome name + base color.
        string name = b.Name ?? "";
        SN.Vector3 tint;
        if (name.Contains("Taiga", StringComparison.OrdinalIgnoreCase) || name.Contains("Pine", StringComparison.OrdinalIgnoreCase) || name.Contains("Snow", StringComparison.OrdinalIgnoreCase) || name.Contains("Tundra", StringComparison.OrdinalIgnoreCase))
            tint = new SN.Vector3(0.14f, 0.31f, 0.19f);
        else if (name.Contains("Jungle", StringComparison.OrdinalIgnoreCase) || name.Contains("Rain", StringComparison.OrdinalIgnoreCase) || name.Contains("Swamp", StringComparison.OrdinalIgnoreCase))
            tint = new SN.Vector3(0.16f, 0.44f, 0.15f);
        else if (name.Contains("Savanna", StringComparison.OrdinalIgnoreCase) || name.Contains("Autumn", StringComparison.OrdinalIgnoreCase) || name.Contains("Mesa", StringComparison.OrdinalIgnoreCase))
            tint = new SN.Vector3(0.42f, 0.44f, 0.17f);
        else if (name.Contains("Desert", StringComparison.OrdinalIgnoreCase))
            tint = new SN.Vector3(0.34f, 0.41f, 0.21f);
        else
        {
            // Blend biome base color with natural forest canopy green so custom biomes tint nicely.
            var bc = new SN.Vector3(
                Math.Clamp(b.BaseColorR, 0.05f, 0.9f),
                Math.Clamp(b.BaseColorG, 0.05f, 0.9f),
                Math.Clamp(b.BaseColorB, 0.05f, 0.9f));
            var forestGreen = new SN.Vector3(0.19f, 0.42f, 0.16f);
            tint = SN.Vector3.Lerp(forestGreen, bc * new SN.Vector3(0.75f, 0.92f, 0.65f), 0.32f);
        }

        return new BiomeTreeSpec(density, minS, maxS, maxSlopeTan, tint);
    }

    static void BakeOnWorker(
        PlanetForestState state,
        int res,
        float[][] heightFaces,
        float[][] deltaFaces,
        float[][] splat0Faces,
        float[][] splat1Faces,
        float planetRadius,
        float seaLevel,
        bool enableWater,
        float treeBaseHeight,
        BiomeTreeSpec[] biomeSpecs,
        int heightVersion,
        int splatVersion,
        ulong recipeHash)
    {
        var packed = new float[MaxInstances * FloatsPerInstance];
        int count = 0;

        float texelArc = MathF.Max(0.25f, (MathF.PI * 0.5f * planetRadius) / MathF.Max(1, res - 1));
        float invTwoTexel = 1f / (2f * texelArc);
        float shoreMargin = MathF.Max(1.2f, planetRadius * 0.0012f);
        // Match Tier 1 tree visual height (~14-18m) while staying proportional on tiny moons.
        float worldTreeH = Math.Clamp(Math.Max(12f, treeBaseHeight * 2.0f), 6f, MathF.Max(10f, planetRadius * 0.045f));

        // Check if any biome has positive TreeDensity; if a custom planet left all at 0,
        // still allow light temperate canopy on dry non-steep land only when biomes exist.
        bool anyBiomeHasTrees = false;
        for (int i = 0; i < biomeSpecs.Length; i++)
        {
            if (biomeSpecs[i].TreeDensity > 0.001f)
            {
                anyBiomeHasTrees = true;
                break;
            }
        }

        int grid = CellsPerFace;
        float invGrid = 1f / grid;

        for (int face = 0; face < 6 && count < MaxInstances; face++)
        {
            var hArr = heightFaces[face];
            var dArr = deltaFaces[face];
            var s0Arr = splat0Faces[face];
            var s1Arr = splat1Faces[face];
            if (hArr == null || dArr == null || s0Arr == null || s1Arr == null)
                continue;

            for (int gy = 0; gy < grid && count < MaxInstances; gy++)
            {
                for (int gx = 0; gx < grid && count < MaxInstances; gx++)
                {
                    uint seed = Hash32((uint)(face * 73856093 ^ gx * 19349663 ^ gy * 83492791) ^ 0x51F3A9C1u);
                    float jx = (seed & 0xFFFF) * (1f / 65536f);
                    seed = Hash32(seed ^ 0x9E3779B9u);
                    float jy = (seed & 0xFFFF) * (1f / 65536f);

                    float u = Math.Clamp((gx + 0.1f + jx * 0.8f) * invGrid, 0f, 1f);
                    float v = Math.Clamp((gy + 0.1f + jy * 0.8f) * invGrid, 0f, 1f);

                    int tx = Math.Clamp((int)(u * (res - 1)), 0, res - 1);
                    int ty = Math.Clamp((int)(v * (res - 1)), 0, res - 1);
                    int idx = ty * res + tx;

                    float h = hArr[idx] + dArr[idx];
                    float surfR = planetRadius + h;
                    if (enableWater && surfR <= seaLevel + shoreMargin)
                        continue;

                    // Finite-difference slope check on the cubemap face.
                    int txL = Math.Max(0, tx - 1);
                    int txR = Math.Min(res - 1, tx + 1);
                    int tyD = Math.Max(0, ty - 1);
                    int tyU = Math.Min(res - 1, ty + 1);
                    float dhdx = ((hArr[ty * res + txR] + dArr[ty * res + txR]) - (hArr[ty * res + txL] + dArr[ty * res + txL])) * invTwoTexel;
                    float dhdy = ((hArr[tyU * res + tx] + dArr[tyU * res + tx]) - (hArr[tyD * res + tx] + dArr[tyD * res + tx])) * invTwoTexel;
                    float slopeTan = MathF.Sqrt(dhdx * dhdx + dhdy * dhdy);

                    // Weighted biome tree density, scale, slope limit, and tint from splat weights.
                    int sIdx = idx * 4;
                    float w0 = s0Arr[sIdx], w1 = s0Arr[sIdx + 1], w2 = s0Arr[sIdx + 2], w3 = s0Arr[sIdx + 3];
                    float w4 = s1Arr[sIdx], w5 = s1Arr[sIdx + 1], w6 = s1Arr[sIdx + 2], w7 = s1Arr[sIdx + 3];

                    float density = 0f;
                    float minScale = 0f;
                    float maxScale = 0f;
                    float maxSlopeTan = 0f;
                    float wSum = 0f;
                    SN.Vector3 tint = SN.Vector3.Zero;

                    AccumBiome(biomeSpecs, 0, w0, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 1, w1, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 2, w2, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 3, w3, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 4, w4, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 5, w5, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 6, w6, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);
                    AccumBiome(biomeSpecs, 7, w7, ref density, ref minScale, ref maxScale, ref maxSlopeTan, ref tint, ref wSum);

                    if (wSum > 1e-4f)
                    {
                        float invW = 1f / wSum;
                        density *= invW;
                        minScale *= invW;
                        maxScale *= invW;
                        maxSlopeTan *= invW;
                        tint *= invW;
                    }
                    else
                    {
                        density = anyBiomeHasTrees ? 0f : 0.15f;
                        minScale = 0.85f;
                        maxScale = 1.25f;
                        maxSlopeTan = 0.55f;
                        tint = new SN.Vector3(0.20f, 0.42f, 0.17f);
                    }

                    if (density <= 0.01f || slopeTan > MathF.Min(0.68f, maxSlopeTan))
                        continue;

                    seed = Hash32(seed ^ 0x85EBCA6Bu);
                    float probRoll = (seed & 0xFFFF) * (1f / 65536f);
                    // Slight boost so forested biomes form cohesive continuous canopies from orbit.
                    float prob = Math.Clamp(density * 1.25f, 0f, 0.94f);
                    if (probRoll > prob)
                        continue;

                    var dir = CubeSphereMath.CubemapTexelDirection(face, u, v);
                    var pos = dir * surfR;

                    seed = Hash32(seed ^ 0xC2B2AE35u);
                    float rScale = (seed & 0xFFFF) * (1f / 65536f);
                    seed = Hash32(seed ^ 0x27D4EB2Fu);
                    float rYaw = (seed & 0xFFFF) * (1f / 65536f);
                    seed = Hash32(seed ^ 0x165667B1u);
                    float rTint = (seed & 0xFFFF) * (1f / 65536f);
                    float fadeSeed = ((seed >> 16) & 0xFFFF) * (1f / 65536f);

                    float scale = worldTreeH * (minScale + (maxScale - minScale) * rScale);
                    float yaw = rYaw * MathF.Tau;
                    float shadeVar = 0.84f + rTint * 0.32f;

                    int o = count * FloatsPerInstance;
                    packed[o + 0] = pos.X;
                    packed[o + 1] = pos.Y;
                    packed[o + 2] = pos.Z;
                    packed[o + 3] = scale;
                    packed[o + 4] = dir.X;
                    packed[o + 5] = dir.Y;
                    packed[o + 6] = dir.Z;
                    packed[o + 7] = yaw;
                    packed[o + 8] = Math.Clamp(tint.X * shadeVar, 0.04f, 0.95f);
                    packed[o + 9] = Math.Clamp(tint.Y * shadeVar, 0.06f, 0.95f);
                    packed[o + 10] = Math.Clamp(tint.Z * shadeVar, 0.03f, 0.95f);
                    packed[o + 11] = fadeSeed;
                    count++;
                }
            }
        }

        if (count < MaxInstances)
            Array.Resize(ref packed, count * FloatsPerInstance);

        lock (state.Lock)
        {
            state.Instances = packed;
            state.InstanceCount = count;
            state.BakedHeightVersion = heightVersion;
            state.BakedSplatVersion = splatVersion;
            state.BakedRecipeHash = recipeHash;
            state.FullRevision++;
            state.EditRevision = state.FullRevision;
            state.DirtyMin = int.MaxValue;
            state.DirtyMax = -1;
            state.Baking = false;
        }
    }

    static void AccumBiome(
        BiomeTreeSpec[] specs, int idx, float w,
        ref float density, ref float minScale, ref float maxScale, ref float maxSlopeTan,
        ref SN.Vector3 tint, ref float wSum)
    {
        if (w <= 0.001f || idx >= specs.Length) return;
        ref readonly var s = ref specs[idx];
        density += s.TreeDensity * w;
        minScale += s.MinScale * w;
        maxScale += s.MaxScale * w;
        maxSlopeTan += s.MaxSlopeTan * w;
        tint += s.FoliageTint * w;
        wSum += w;
    }

    /// <summary>
    /// Zero the scale of all whole-planet canopy instances within a cone around <paramref name="localDir"/>.
    /// Marks a dirty slice so each GL context updates its static VBO via <c>BufferSubData</c>.
    /// </summary>
    public static int RemoveInCone(PlanetTerrain? terrain, SN.Vector3 localDir, float minDot)
    {
        if (terrain == null || localDir.LengthSquared() < 1e-8f)
            return 0;
        PlanetForestState? state;
        lock (s_statesLock)
            s_states.TryGetValue(terrain, out state);
        if (state == null)
            return 0;

        var dir = SN.Vector3.Normalize(localDir);
        int removed = 0;

        lock (state.Lock)
        {
            int count = state.InstanceCount;
            var data = state.Instances;
            if (count <= 0 || data.Length < count * FloatsPerInstance)
                return 0;

            int minIdx = int.MaxValue;
            int maxIdx = -1;
            for (int i = 0; i < count; i++)
            {
                int o = i * FloatsPerInstance;
                if (data[o + 3] <= 0.001f)
                    continue;
                float dot = data[o + 4] * dir.X + data[o + 5] * dir.Y + data[o + 6] * dir.Z;
                if (dot >= minDot)
                {
                    data[o + 3] = 0f;
                    if (i < minIdx) minIdx = i;
                    if (i > maxIdx) maxIdx = i;
                    removed++;
                }
            }

            if (removed > 0)
            {
                if (minIdx < state.DirtyMin) state.DirtyMin = minIdx;
                if (maxIdx > state.DirtyMax) state.DirtyMax = maxIdx;
                state.EditRevision++;
            }
        }

        return removed;
    }

    /// <summary>Remove CPU state for a destroyed planet.</summary>
    public static void ClearPlanet(PlanetTerrain? terrain)
    {
        if (terrain == null) return;
        lock (s_statesLock)
            s_states.Remove(terrain);
    }

    /// <summary>
    /// Render the whole-planet canopy layer for <paramref name="terrain"/> at any camera altitude.
    /// Pass <paramref name="crossfadeOuterLocal"/> &lt;= 0 to disable the near-ring cutout (e.g. in orbit).
    /// </summary>
    public static unsafe void Render(
        GL gl,
        ResourceCache cache,
        PlanetTerrain terrain,
        PlanetVegetationSystem? veg,
        in SN.Matrix4x4 planetWorld,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        SN.Vector3 camPos,
        SN.Vector3 lightDir,
        float ambient,
        float diffuseK,
        SN.Vector3 lightColor,
        SN.Vector3 nearAnchorLocal,
        float crossfadeInnerLocal,
        float crossfadeOuterLocal,
        bool hdrOutput)
    {
        if (terrain == null || !terrain.HasBakedStandSurface)
            return;

        EnsureBaked(terrain, veg);

        PlanetForestState? state;
        lock (s_statesLock)
            s_states.TryGetValue(terrain, out state);
        if (state == null)
            return;

        if (!TryGetGpu(gl, cache, out var gpu) || gpu.Shader == null)
            return;

        int instanceCount;
        if (!gpu.Bufs.TryGetValue(terrain, out var buf))
        {
            buf = new ForestBuf { Vbo = gl.GenBuffer() };
            gpu.Bufs[terrain] = buf;
        }

        lock (state.Lock)
        {
            instanceCount = state.InstanceCount;
            if (instanceCount <= 0 || state.Instances.Length < instanceCount * FloatsPerInstance)
                return;

            int totalFloats = instanceCount * FloatsPerInstance;
            if (buf.UploadedFullRev != state.FullRevision)
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf.Vbo);
                fixed (float* ptr = state.Instances)
                {
                    gl.BufferData(
                        BufferTargetARB.ArrayBuffer,
                        (nuint)(totalFloats * sizeof(float)),
                        ptr,
                        BufferUsageARB.StaticDraw);
                }
                buf.GpuCapacityFloats = totalFloats;
                buf.UploadedFullRev = state.FullRevision;
                buf.UploadedEditRev = state.EditRevision;
            }
            else if (buf.UploadedEditRev != state.EditRevision)
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf.Vbo);
                int lo = Math.Clamp(state.DirtyMin, 0, instanceCount - 1);
                int hi = Math.Clamp(state.DirtyMax, lo, instanceCount - 1);
                // If dirty range is invalid (e.g. second GL context catching up after a reset), upload full.
                if (state.DirtyMax < state.DirtyMin || lo > hi)
                {
                    lo = 0;
                    hi = instanceCount - 1;
                }
                int sliceFloats = (hi - lo + 1) * FloatsPerInstance;
                fixed (float* ptr = &state.Instances[lo * FloatsPerInstance])
                {
                    gl.BufferSubData(
                        BufferTargetARB.ArrayBuffer,
                        (nint)(lo * StrideBytes),
                        (nuint)(sliceFloats * sizeof(float)),
                        ptr);
                }
                buf.UploadedEditRev = state.EditRevision;
            }
        }

        var prevCull = gl.IsEnabled(EnableCap.CullFace);
        var prevBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);

        float wetness = 0f, snow = 0f, rain = 0f, cloudiness = 0f, windMul = 1f, sunIntensity = 1f, atmoAmbient = 0.18f;
        if (veg != null)
            veg.GetGpuGrassEnvironment(out wetness, out snow, out rain, out cloudiness, out windMul, out sunIntensity, out atmoAmbient);
        else
        {
            var atmo = terrain.Atmosphere;
            sunIntensity = Math.Clamp(atmo?.SunIntensity ?? 1f, 0.02f, 2f);
            atmoAmbient = Math.Clamp(atmo?.Ambient ?? 0.18f, 0.02f, 1f);
        }

        float wind = WindSystem.GetCurrentStrength() * Math.Clamp(windMul, 0.2f, 1.15f);
        float storm = rain >= 0.85f ? 1f : 0f;
        var sunTint = lightColor.LengthSquared() > 1e-6f ? lightColor : new SN.Vector3(1f, 0.96f, 0.88f);
        float amb = Math.Min(Math.Max(0.02f, ambient), Math.Max(0.02f, atmoAmbient));
        var windDir = WindSystem.Direction.LengthSquared() > 1e-6f
            ? SN.Vector3.Normalize(WindSystem.Direction)
            : SN.Vector3.UnitX;

        var globals = GlobalUniforms.ForContext(gl);
        globals.UpdateFrame(in view, in proj, camPos, WindSystem.Time);
        globals.UpdateLighting(
            lightDir, Math.Clamp(diffuseK, 0.20f, 1.35f), sunTint, Math.Clamp(amb, 0.02f, 0.85f),
            lightIsPoint: false, SN.Vector3.Zero, 0f,
            hasShadow: false, SN.Matrix4x4.Identity,
            windDir: windDir, windTime: WindSystem.Time);

        var shader = gpu.Shader;
        shader.Use();
        shader.SetMatrix4("uPlanetWorld", planetWorld);
        shader.SetVector3("uNearAnchorLocal", nearAnchorLocal);
        shader.SetVector2("uCrossfadeRange", crossfadeInnerLocal, crossfadeOuterLocal);
        shader.SetFloat("uSunIntensity", Math.Clamp(sunIntensity, 0.02f, 2f));
        shader.SetFloat("uWetness", wetness);
        shader.SetFloat("uSnow", snow);
        shader.SetFloat("uRain", rain);
        shader.SetFloat("uStorm", storm);
        shader.SetFloat("uCloudiness", cloudiness);
        shader.SetFloat("uWindStrength", Math.Clamp(wind * 2.0f, 0.01f, 0.26f));
        shader.SetInt("uHdrOutput", hdrOutput ? 1 : 0);

        gl.BindVertexArray(gpu.Vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf.Vbo);
        gl.EnableVertexAttribArray(5);
        gl.EnableVertexAttribArray(6);
        gl.EnableVertexAttribArray(7);
        gl.VertexAttribDivisor(5, 1);
        gl.VertexAttribDivisor(6, 1);
        gl.VertexAttribDivisor(7, 1);

        try
        {
#if XBOX_UWP
            const int group = 256;
            for (int start = 0; start < instanceCount; start += group)
            {
                int batchCount = Math.Min(group, instanceCount - start);
                int byteOff = start * StrideBytes;
                gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)byteOff);
                gl.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)(byteOff + 16));
                gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)(byteOff + 32));
                gl.DrawElementsInstanced(
                    PrimitiveType.Triangles,
                    (uint)gpu.IndexCount,
                    DrawElementsType.UnsignedInt,
                    (void*)0,
                    (uint)batchCount);
            }
#else
            gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)0);
            gl.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)16);
            gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)32);
            gl.DrawElementsInstanced(
                PrimitiveType.Triangles,
                (uint)gpu.IndexCount,
                DrawElementsType.UnsignedInt,
                (void*)0,
                (uint)instanceCount);
#endif
            Profiler.CountDrawCall();
            Profiler.CountTriangles((gpu.IndexCount / 3) * instanceCount);
            FrameProfile.CountDraw();
        }
        finally
        {
            gl.VertexAttribDivisor(5, 0);
            gl.VertexAttribDivisor(6, 0);
            gl.VertexAttribDivisor(7, 0);
            gl.DisableVertexAttribArray(5);
            gl.DisableVertexAttribArray(6);
            gl.DisableVertexAttribArray(7);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
#if !XBOX_UWP
            gl.BindVertexArray(0);
#endif
            if (!prevCull) gl.Disable(EnableCap.CullFace);
            if (prevBlend) gl.Enable(EnableCap.Blend);
            gl.UseProgram(0);
            ShaderProgram.NotifyProgramUnbound(gl);
        }
    }

    static unsafe bool TryGetGpu(GL gl, object gpuKey, out ForestGpu gpu)
    {
        if (s_gpus.TryGetValue(gpuKey, out gpu!))
        {
            if (!gpu.Failed && ShaderLibrary.TryGet(gl, out var lib) && lib != null)
            {
                long gen = lib.ReloadGeneration;
                if (gen != gpu.SeenReloadGen)
                {
                    gpu.SeenReloadGen = gen;
                    gpu.Shader = lib.GetBuiltin(
                        ShaderSources.Paths.PlanetForestCanopyVert,
                        ShaderSources.Paths.PlanetForestCanopyFrag,
                        throwOnInitialError: false);
                }
            }
            return !gpu.Failed && gpu.Shader != null;
        }

        gpu = new ForestGpu { Gl = gl };
        s_gpus[gpuKey] = gpu;
        try
        {
            var shaders = ShaderLibrary.GetOrCreate(gl);
            gpu.Shader = shaders.GetBuiltin(
                ShaderSources.Paths.PlanetForestCanopyVert,
                ShaderSources.Paths.PlanetForestCanopyFrag);
            gpu.SeenReloadGen = shaders.ReloadGeneration;

            BuildProceduralCanopyMesh(out var verts, out var indices);
            gpu.IndexCount = indices.Length;

            gpu.Vao = gl.GenVertexArray();
            gpu.MeshVbo = gl.GenBuffer();
            gpu.MeshEbo = gl.GenBuffer();

            gl.BindVertexArray(gpu.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.MeshVbo);
            fixed (float* vp = verts)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), vp, BufferUsageARB.StaticDraw);

            const uint meshStride = 8 * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, meshStride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, meshStride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, meshStride, (void*)(6 * sizeof(float)));

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gpu.MeshEbo);
            fixed (uint* ip = indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);

#if !XBOX_UWP
            gl.BindVertexArray(0);
#endif
            return true;
        }
        catch (Exception ex)
        {
            gpu.Failed = true;
            Log.Error($"[PlanetWholePlanetForest] Failed to init canopy GPU resources: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Procedural stylized low-poly tree mesh: 3-sided tapered trunk (6 tris) +
    /// 2-tier 6-sided layered canopy cones (12 tris) + underside cap (4 tris) = 22 triangles.
    /// Interleaved format: [Pos3, Normal3, Meta2 (isCanopy, crownHeight)].
    /// </summary>
    static void BuildProceduralCanopyMesh(out float[] verts, out uint[] indices)
    {
        var vList = new List<float>(32 * 8);
        var iList = new List<uint>(22 * 3);

        uint AddVert(SN.Vector3 p, SN.Vector3 n, float isCanopy, float crownY)
        {
            uint idx = (uint)(vList.Count / 8);
            var nn = n.LengthSquared() > 1e-6f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
            vList.Add(p.X); vList.Add(p.Y); vList.Add(p.Z);
            vList.Add(nn.X); vList.Add(nn.Y); vList.Add(nn.Z);
            vList.Add(isCanopy); vList.Add(crownY);
            return idx;
        }

        // 1) Trunk: 3-sided tapered prism (6 triangles) from y = -0.12 to y = 0.42
        const int trunkSides = 3;
        uint trunkBase = (uint)(vList.Count / 8);
        for (int i = 0; i < trunkSides; i++)
        {
            float ang = i * (MathF.Tau / trunkSides);
            float ca = MathF.Cos(ang);
            float sa = MathF.Sin(ang);
            var n = new SN.Vector3(ca, 0.1f, sa);
            AddVert(new SN.Vector3(ca * 0.095f, -0.12f, sa * 0.095f), n, 0f, 0f);
            AddVert(new SN.Vector3(ca * 0.055f,  0.42f, sa * 0.055f), n, 0f, 0.35f);
        }
        for (int i = 0; i < trunkSides; i++)
        {
            int next = (i + 1) % trunkSides;
            uint b0 = trunkBase + (uint)(i * 2);
            uint t0 = b0 + 1;
            uint b1 = trunkBase + (uint)(next * 2);
            uint t1 = b1 + 1;
            iList.Add(b0); iList.Add(t1); iList.Add(b1);
            iList.Add(b0); iList.Add(t0); iList.Add(t1);
        }

        // 2) Lower canopy cone: 6 sides (6 side tris + 4 cap tris)
        AddCanopyCone(vList, iList, sides: 6, baseY: 0.26f, apexY: 0.78f, radius: 0.46f, rotOffset: 0f, baseCrownY: 0.18f, apexCrownY: 0.76f, addBottomCap: true);

        // 3) Upper canopy cone: 6 sides (6 side tris), rotated 30 deg for layered organic silhouette
        AddCanopyCone(vList, iList, sides: 6, baseY: 0.52f, apexY: 1.00f, radius: 0.34f, rotOffset: MathF.PI / 6f, baseCrownY: 0.55f, apexCrownY: 1.00f, addBottomCap: false);

        verts = vList.ToArray();
        indices = iList.ToArray();
    }

    static void AddCanopyCone(
        List<float> vList,
        List<uint> iList,
        int sides,
        float baseY,
        float apexY,
        float radius,
        float rotOffset,
        float baseCrownY,
        float apexCrownY,
        bool addBottomCap)
    {
        uint AddV(SN.Vector3 p, SN.Vector3 n, float crownY)
        {
            uint idx = (uint)(vList.Count / 8);
            var nn = n.LengthSquared() > 1e-6f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
            vList.Add(p.X); vList.Add(p.Y); vList.Add(p.Z);
            vList.Add(nn.X); vList.Add(nn.Y); vList.Add(nn.Z);
            vList.Add(1f); vList.Add(crownY);
            return idx;
        }

        uint apexIdx = AddV(new SN.Vector3(0f, apexY, 0f), SN.Vector3.UnitY, apexCrownY);
        uint ringStart = (uint)(vList.Count / 8);
        float slopeUp = radius / MathF.Max(0.1f, apexY - baseY);

        for (int i = 0; i < sides; i++)
        {
            float ang = rotOffset + i * (MathF.Tau / sides);
            float ca = MathF.Cos(ang);
            float sa = MathF.Sin(ang);
            var p = new SN.Vector3(ca * radius, baseY, sa * radius);
            var n = new SN.Vector3(ca, slopeUp, sa);
            AddV(p, n, baseCrownY);
        }

        for (int i = 0; i < sides; i++)
        {
            uint c0 = ringStart + (uint)i;
            uint c1 = ringStart + (uint)((i + 1) % sides);
            // Counter-clockwise when viewed from outside
            iList.Add(c0);
            iList.Add(apexIdx);
            iList.Add(c1);
        }

        if (addBottomCap)
        {
            uint capStart = (uint)(vList.Count / 8);
            for (int i = 0; i < sides; i++)
            {
                float ang = rotOffset + i * (MathF.Tau / sides);
                var p = new SN.Vector3(MathF.Cos(ang) * radius, baseY, MathF.Sin(ang) * radius);
                AddV(p, -SN.Vector3.UnitY, baseCrownY * 0.65f);
            }
            for (int i = 1; i < sides - 1; i++)
            {
                iList.Add(capStart);
                iList.Add(capStart + (uint)i);
                iList.Add(capStart + (uint)(i + 1));
            }
        }
    }

    static void DisposeGpu(ForestGpu gpu)
    {
        var gl = gpu.Gl;
        try
        {
            if (gpu.Vao != 0) gl.DeleteVertexArray(gpu.Vao);
            if (gpu.MeshVbo != 0) gl.DeleteBuffer(gpu.MeshVbo);
            if (gpu.MeshEbo != 0) gl.DeleteBuffer(gpu.MeshEbo);
            foreach (var buf in gpu.Bufs.Values)
            {
                if (buf.Vbo != 0) gl.DeleteBuffer(buf.Vbo);
            }
        }
        catch { }
        gpu.Bufs.Clear();
        gpu.Shader = null;
        gpu.Vao = gpu.MeshVbo = gpu.MeshEbo = 0;
    }

    /// <summary>Release one GL context's canopy GPU state. Must be called while that GL context is current.</summary>
    public static void ReleaseGpuFor(object gpuKey)
    {
        if (s_gpus.Remove(gpuKey, out var gpu))
            DisposeGpu(gpu);
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
