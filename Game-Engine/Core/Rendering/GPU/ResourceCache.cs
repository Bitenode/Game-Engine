#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>Thread-safe queue of CPU meshes whose GPU entries should be dropped on the render thread.</summary>
public static class GpuMeshReleaseQueue
{
    static readonly object Gate = new();
    static readonly List<WeakReference<ResourceCache>> Caches = new();

    internal static void Register(ResourceCache cache)
    {
        lock (Gate)
        {
            for (int i = Caches.Count - 1; i >= 0; i--)
            {
                if (!Caches[i].TryGetTarget(out var existing))
                    Caches.RemoveAt(i);
                else if (ReferenceEquals(existing, cache))
                    return;
            }
            Caches.Add(new WeakReference<ResourceCache>(cache));
        }
    }

    internal static void Unregister(ResourceCache cache)
    {
        lock (Gate)
        {
            for (int i = Caches.Count - 1; i >= 0; i--)
            {
                if (!Caches[i].TryGetTarget(out var existing) || ReferenceEquals(existing, cache))
                    Caches.RemoveAt(i);
            }
        }
    }

    public static void Enqueue(Mesh? mesh)
    {
        if (mesh == null)
            return;

        SceneRenderer.ReleaseMeshMetadata(mesh);

        // Every GL view owns a separate cache. Broadcasting prevents one view from
        // consuming a global release while another view keeps the same mesh forever.
        lock (Gate)
        {
            for (int i = Caches.Count - 1; i >= 0; i--)
            {
                if (Caches[i].TryGetTarget(out var cache))
                    cache.QueueMeshRelease(mesh);
                else
                    Caches.RemoveAt(i);
            }
        }
    }

    public static int Drain(ResourceCache cache, int maxPerFrame = 64)
        => cache.DrainMeshReleases(maxPerFrame);
}

/// <summary>
/// Maps engine Mesh/Texture2D objects to their GPU counterparts.
/// Handles lazy upload, versioning, and disposal.
/// Must only be accessed within a GL context (OnOpenGlRender).
/// </summary>
public sealed class ResourceCache : IDisposable
{
    private readonly GL _gl;

    // Mesh → GPU mesh, keyed by reference identity
    private readonly Dictionary<Mesh, GPUMeshEntry> _meshes = new(64);
    private readonly Dictionary<TexBindKey, GPUTexture> _textures = new(64);
    // Weak: every view's cache is told about every released mesh, and a view that is not
    // drawing (a hidden editor tab) does not drain. Holding the meshes themselves kept
    // every discarded chunk mesh alive for as long as that view stayed hidden. A mesh this
    // cache has uploaded stays reachable through _meshes, so its entry still resolves.
    private readonly ConcurrentQueue<WeakReference<Mesh>> _pendingMeshReleases = new();

    /// <summary>After BCn GPU upload fails, skip re-attempting compression every frame (use RGBA path).</summary>
    private readonly HashSet<(Texture2D Tex, MaterialTexture.TexUsage Usage)> _skipBcnCompress = new(64);

    private readonly struct TexBindKey : IEquatable<TexBindKey>
    {
        public Texture2D Tex { get; }
        public MaterialTexture.TexUsage Usage { get; }
        public bool GpuCompressed { get; }

        public TexBindKey(Texture2D tex, MaterialTexture.TexUsage usage, bool gpuCompressed)
        {
            Tex = tex;
            Usage = usage;
            GpuCompressed = gpuCompressed;
        }

        public bool Equals(TexBindKey o) =>
            ReferenceEquals(Tex, o.Tex) && Usage == o.Usage && GpuCompressed == o.GpuCompressed;

        public override bool Equals(object? obj) => obj is TexBindKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Tex, Usage, GpuCompressed);
    }

    // Per-context terrain splatmap textures (avoids cross-context GL issues)
    // Also tracks the splatmap version last uploaded so each context re-uploads independently.
    private readonly Dictionary<Terrain, (GPUTexture Splat0, GPUTexture Splat1, int Version)> _terrainSplatTextures = new();
    private readonly Dictionary<object, (GPUTexture Splat0, GPUTexture Splat1, int Version)> _planetSplatTextures = new();
    private readonly Dictionary<object, (GPUTexture Height, int Version)> _planetHeightTextures = new();
    private readonly Dictionary<string, Texture2DArrayEntry> _textureArrays = new(StringComparer.Ordinal);

    private sealed class Texture2DArrayEntry
    {
        public GPUTexture Texture = null!;
        public Texture2D?[] LayerRefs = Array.Empty<Texture2D?>();
        public byte[]?[] LayerBuffers = Array.Empty<byte[]?>();
        public int[] LayerWidths = Array.Empty<int>();
        public int[] LayerHeights = Array.Empty<int>();
        public int TargetSize;
        public uint DefaultRgba;
    }

    private struct GPUMeshEntry
    {
        public GPUMesh GPU;
        public int Version; // bumped when mesh data changes
    }

    // Global version counter; incremented on scene changes
    private int _globalVersion;

    public ResourceCache(GL gl)
    {
        _gl = gl;
        GpuMeshReleaseQueue.Register(this);
    }

    /// <summary>Call when the scene changes to invalidate cached state.</summary>
    public void InvalidateAll()
    {
        _globalVersion++;
        // Do NOT clear _skipBcnCompress here. Transform/animal NotifyChanged fires every
        // frame in play mode; wiping the skip set forced File.Exists on every texture bind
        // and froze Xbox Dev Mode after fauna started moving.
    }

    /// <summary>
    /// Whether a full GPU cache flush has been requested.
    /// Checked at the start of the next GL render pass so disposal runs inside
    /// the correct GL context.
    /// </summary>
    public bool FlushRequested { get; set; }

    /// <summary>
    /// Dispose and clear ALL cached GPU resources (meshes, textures, terrain splatmaps).
    /// Must be called from within an active GL context (e.g. OnOpenGlRender).
    /// Use this when the entire scene has been replaced.
    /// </summary>
    public void FlushAll()
    {
        PlanetGpuGrass.ReleaseGpuFor(this);
        PlanetWholePlanetForest.ReleaseGpuFor(this);
        PlanetGpuTrees.ReleaseGpuFor(this);
        SceneRenderer.ClearMeshMetadata();

        foreach (var kv in _meshes)
            kv.Value.GPU.Dispose();
        _meshes.Clear();
        while (_pendingMeshReleases.TryDequeue(out _)) { }

        foreach (var kv in _textures)
            kv.Value.Dispose();
        _textures.Clear();

        foreach (var kv in _terrainSplatTextures)
        {
            kv.Value.Splat0?.Dispose();
            kv.Value.Splat1?.Dispose();
        }
        _terrainSplatTextures.Clear();

        foreach (var kv in _planetSplatTextures)
        {
            kv.Value.Splat0.Dispose();
            kv.Value.Splat1.Dispose();
        }
        _planetSplatTextures.Clear();

        foreach (var kv in _planetHeightTextures)
            kv.Value.Height.Dispose();
        _planetHeightTextures.Clear();

        foreach (var kv in _textureArrays)
            kv.Value.Texture.Dispose();
        _textureArrays.Clear();

        _whiteTex?.Dispose();
        _whiteTex = null;
        _blackTex?.Dispose();
        _blackTex = null;
        _flatNormalTex?.Dispose();
        _flatNormalTex = null;

        _globalVersion++;
        FlushRequested = false;
        _skipBcnCompress.Clear();
    }

    /// <summary>
    /// Get or create a GPUMesh for the given engine Mesh.
    /// Uploads data if the mesh is new or has changed.
    /// </summary>
    public GPUMesh GetMesh(Mesh mesh)
    {
        if (_meshes.TryGetValue(mesh, out var entry))
        {
            if (entry.Version != mesh.GeometryVersion)
            {
                long changedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                entry.GPU.Upload(mesh);
                entry.Version = mesh.GeometryVersion;
                _meshes[mesh] = entry;
                FrameProfile.CountMeshUpload(System.Diagnostics.Stopwatch.GetTimestamp() - changedAt);
            }
            return entry.GPU;
        }

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var gpuMesh = new GPUMesh(_gl);
        gpuMesh.Upload(mesh);
        _meshes[mesh] = new GPUMeshEntry { GPU = gpuMesh, Version = mesh.GeometryVersion };
        FrameProfile.CountMeshUpload(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
        return gpuMesh;
    }

    /// <summary>
    /// Force re-upload of a specific mesh (e.g. after terrain edit or LOD change).
    /// </summary>
    public void MarkMeshDirty(Mesh mesh)
    {
        if (_meshes.TryGetValue(mesh, out var entry))
        {
            entry.GPU.Upload(mesh);
            entry.Version = mesh.GeometryVersion;
            _meshes[mesh] = entry;
        }
    }

    /// <summary>Drop a single mesh from the GPU cache (render thread only).</summary>
    public void ReleaseMesh(Mesh mesh)
    {
        if (_meshes.TryGetValue(mesh, out var entry))
        {
            entry.GPU.Dispose();
            _meshes.Remove(mesh);
        }
    }

    internal void QueueMeshRelease(Mesh mesh) => _pendingMeshReleases.Enqueue(new WeakReference<Mesh>(mesh));

    internal int DrainMeshReleases(int maxPerFrame)
    {
        int budget = Math.Max(1, maxPerFrame);
        // Entries whose mesh is already gone cost nothing to skip, but a view that was
        // hidden for a long time can have a great many of them.
        int looked = 0, lookLimit = budget * 256;
        int released = 0;
        while (released < budget
               && looked++ < lookLimit
               && _pendingMeshReleases.TryDequeue(out var weak))
        {
            // Collected means this cache never held it: _meshes keeps its meshes alive.
            if (!weak.TryGetTarget(out var mesh))
                continue;
            ReleaseMesh(mesh);
            released++;
        }
        return released;
    }

    /// <summary>
    /// Remove entries that are no longer referenced by any scene object.
    /// Call periodically (e.g., every few seconds) to prevent unbounded growth.
    /// </summary>
    public void EvictOrphans(int maxEntries = 4096)
    {
        if (_meshes.Count <= maxEntries) return;
        // Nuclear eviction: clear everything; entries rebuild lazily on next GetMesh call.
        foreach (var kv in _meshes) kv.Value.GPU.Dispose();
        _meshes.Clear();
    }

    /// <summary>Process queued mesh releases then evict if the cache is still large.</summary>
    public void Maintain(int maxEntries = 512, int maxReleasesPerFrame = 64)
    {
        GpuMeshReleaseQueue.Drain(this, maxReleasesPerFrame);
        PruneDestroyedPlanetTextures();
        EvictOrphans(maxEntries);
    }

    void PruneDestroyedPlanetTextures()
    {
        List<object>? dead = null;
        foreach (var key in _planetSplatTextures.Keys)
        {
            if (key is PlanetTerrain planet && !PlanetTerrain.ActivePlanets.Contains(planet))
                (dead ??= new List<object>()).Add(key);
        }
        foreach (var key in _planetHeightTextures.Keys)
        {
            if (key is not PlanetTerrain planet || PlanetTerrain.ActivePlanets.Contains(planet))
                continue;
            if (dead == null || !dead.Contains(key))
                (dead ??= new List<object>()).Add(key);
        }
        if (dead == null)
            return;

        for (int i = 0; i < dead.Count; i++)
        {
            var key = dead[i];
            if (_planetSplatTextures.Remove(key, out var splat))
            {
                splat.Splat0.Dispose();
                splat.Splat1.Dispose();
            }
            if (_planetHeightTextures.Remove(key, out var height))
                height.Height.Dispose();
        }
    }

    /// <summary>
    /// Get or create a GPUTexture for the given engine Texture2D (RGBA8 path).
    /// </summary>
    public GPUTexture GetTexture(Texture2D tex) => GetTexture(tex, null);

    /// <summary>
    /// Get or create a GPUTexture; when <paramref name="materialSlot"/> is set, may upload BCn-compressed mips from a sidecar .dds.
    /// </summary>
    public GPUTexture GetTexture(Texture2D tex, MaterialTexture? materialSlot)
    {
        GpuCompressionCaps.Initialize(_gl);
        var usage = materialSlot?.Usage ?? MaterialTexture.TexUsage.Albedo;

        if (materialSlot != null && TextureBcCompression.ShouldCompressMaterialSlot(materialSlot, tex)
            && !_skipBcnCompress.Contains((tex, usage)))
        {
            var ckey = new TexBindKey(tex, usage, true);
            if (_textures.TryGetValue(ckey, out var cachedC))
                return cachedC;

            long compressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (TryCreateCompressedTexture(tex, materialSlot, out var gpuC, out bool skipBcn) && gpuC != null)
            {
                _textures[ckey] = gpuC;
                FrameProfile.CountTextureUpload(System.Diagnostics.Stopwatch.GetTimestamp() - compressedAt);
                return gpuC;
            }
            if (skipBcn)
                _skipBcnCompress.Add((tex, usage));
        }

        var key = new TexBindKey(tex, usage, false);
        if (_textures.TryGetValue(key, out var gpuTex))
            return gpuTex;

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        gpuTex = new GPUTexture(_gl);
#if XBOX_UWP
        // Mip chains average punch-through alpha toward grey. Leaf and grass cards then
        // keep a solid rectangle instead of discarding the backdrop.
        if (MaterialUtil.TextureHasMeaningfulAlpha(tex))
            gpuTex.UploadLinearClampNoMip(tex);
        else
            gpuTex.Upload(tex);
#else
        gpuTex.Upload(tex);
#endif
        _textures[key] = gpuTex;
        FrameProfile.CountTextureUpload(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
        return gpuTex;
    }

    bool TryCreateCompressedTexture(Texture2D tex, MaterialTexture slot, out GPUTexture? gpu, out bool skipFutureCompressionAttempts)
    {
        gpu = null;
        skipFutureCompressionAttempts = false;
        string? abs = TextureBcCompression.ResolveAbsoluteImagePath(slot.SourcePath);
        if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
            abs = tex.SourcePath;
        if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
            return false;

        bool hasAlpha = TextureBcCompression.HasMeaningfulAlpha(tex.Rgba, tex.Width, tex.Height);
        var wantFmt = TextureBcCompression.ChooseFormat(slot.Usage, hasAlpha, GpuCompressionCaps.Bptc);
        if (!TextureBcCompression.IsFormatUploadSupported(wantFmt))
        {
            skipFutureCompressionAttempts = true;
            return false;
        }

        string ddsPath = TextureBcCompression.GetSidecarDdsPath(abs);
        // An opacity map is the surface alpha. BC1 has none, and a compressed albedo then reports
        // alpha 0, which discards every pixel of glass and other transparent layers.
        if (slot.Usage == MaterialTexture.TexUsage.Opacity)
        {
            skipFutureCompressionAttempts = true;
            return false;
        }
#if XBOX_UWP
        // Do not encode BC on the console. The package is read-only and a 2k PNG encode stalls the frame.
        if (!File.Exists(ddsPath))
        {
            skipFutureCompressionAttempts = true;
            return false;
        }
#endif
        bool ddsStale = !File.Exists(ddsPath)
            || (File.Exists(abs) && File.GetLastWriteTimeUtc(ddsPath) < File.GetLastWriteTimeUtc(abs));
        if (ddsStale)
        {
            if (!TextureBcCompression.TryEnsureSidecarDds(abs, tex.Rgba, tex.Width, tex.Height, wantFmt))
                return false;
        }

        if (!TextureBcCompression.TryLoadDdsForGpu(ddsPath, out var dds, out var bcFmt) || dds == null)
            return false;
        // BC1 has no alpha. A leaf or grass card compressed that way draws the whole rectangle.
        if (hasAlpha && TextureBcCompression.DropsAlpha(bcFmt))
            return false;
        if (!TextureBcCompression.IsFormatUploadSupported(bcFmt))
        {
            skipFutureCompressionAttempts = true;
            return false;
        }

        gpu = new GPUTexture(_gl);
        if (!gpu.TryUploadCompressedFromDds(dds, bcFmt, out bool stripSidecar))
        {
            gpu.Dispose();
            gpu = null;
            skipFutureCompressionAttempts = true;
            if (stripSidecar)
            {
                try { File.Delete(ddsPath); } catch { /* ignore */ }
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Create a 1x1 white texture (used when no texture is assigned).
    /// </summary>
    public GPUTexture GetWhiteTexture()
    {
        if (_whiteTex != null) return _whiteTex;

        _whiteTex = new GPUTexture(_gl);
        var white = new Texture2D(1, 1, new byte[] { 255, 255, 255, 255 });
        _whiteTex.Upload(white);
        return _whiteTex;
    }
    private GPUTexture? _whiteTex;

    /// <summary>Create a 1x1 opaque black texture (used for "black" shader property defaults).</summary>
    public GPUTexture GetBlackTexture()
    {
        if (_blackTex != null) return _blackTex;

        _blackTex = new GPUTexture(_gl);
        var black = new Texture2D(1, 1, new byte[] { 0, 0, 0, 255 });
        _blackTex.Upload(black);
        return _blackTex;
    }
    private GPUTexture? _blackTex;

    /// <summary>Create a 1x1 flat tangent-space normal texture (0.5, 0.5, 1.0) for "bump"/"normal" defaults.</summary>
    public GPUTexture GetFlatNormalTexture()
    {
        if (_flatNormalTex != null) return _flatNormalTex;

        _flatNormalTex = new GPUTexture(_gl);
        var flatN = new Texture2D(1, 1, new byte[] { 128, 128, 255, 255 });
        _flatNormalTex.Upload(flatN);
        return _flatNormalTex;
    }
    private GPUTexture? _flatNormalTex;

    /// <summary>Create a 1x1 opaque black cubemap (used as a safe fallback for custom <c>samplerCube</c> properties).</summary>
    public GPUTexture GetBlackCubemap()
    {
        if (_blackCubemap != null) return _blackCubemap;

        _blackCubemap = new GPUTexture(_gl);
        _blackCubemap.CreateCubemapSolidRgba8(0, 0, 0, 255);
        return _blackCubemap;
    }
    private GPUTexture? _blackCubemap;

    /// <summary>
    /// Get or create splatmap GPU textures for a Terrain. Per-context, so
    /// SceneView and GameView each get their own GL textures.
    /// Returns (Splat0, Splat1, needsUpload) — needsUpload is true when the
    /// textures are brand new for this context and require an initial upload.
    /// </summary>
    /// <summary>
    /// Get or create splatmap GPU textures for a Terrain. Returns whether this context
    /// needs to (re-)upload the splatmap data, based on the terrain's SplatmapVersion.
    /// </summary>
    public (GPUTexture Splat0, GPUTexture Splat1, bool NeedsUpload) GetTerrainSplatTextures(Terrain terrain)
    {
        if (_terrainSplatTextures.TryGetValue(terrain, out var entry))
        {
            // Re-upload if the terrain's version is ahead of what we last uploaded
            bool stale = entry.Version != terrain.SplatmapVersion;
            return (entry.Splat0, entry.Splat1, stale);
        }

        var pair = (new GPUTexture(_gl), new GPUTexture(_gl), -1); // version -1 = never uploaded
        _terrainSplatTextures[terrain] = pair;
        return (pair.Item1, pair.Item2, true); // always needs upload on first use
    }

    /// <summary>Mark that this context has uploaded the terrain's splatmap at the given version.</summary>
    public void SetTerrainSplatVersion(Terrain terrain, int version)
    {
        if (_terrainSplatTextures.TryGetValue(terrain, out var entry))
            _terrainSplatTextures[terrain] = (entry.Splat0, entry.Splat1, version);
    }

    /// <summary>
    /// Per-context planet splat atlases (3×2 face layout). Keyed by the planet component instance.
    /// </summary>
    public (GPUTexture Splat0, GPUTexture Splat1, bool NeedsUpload) GetPlanetSplatTextures(object planetKey, int version)
    {
        if (_planetSplatTextures.TryGetValue(planetKey, out var entry))
        {
            bool stale = entry.Version != version;
            return (entry.Splat0, entry.Splat1, stale);
        }

        var pair = (new GPUTexture(_gl), new GPUTexture(_gl), -1);
        _planetSplatTextures[planetKey] = pair;
        return (pair.Item1, pair.Item2, true);
    }

    public void SetPlanetSplatVersion(object planetKey, int version)
    {
        if (_planetSplatTextures.TryGetValue(planetKey, out var entry))
            _planetSplatTextures[planetKey] = (entry.Splat0, entry.Splat1, version);
    }

    public (GPUTexture Height, bool NeedsUpload) GetPlanetHeightTexture(object planetKey, int version)
    {
        if (_planetHeightTextures.TryGetValue(planetKey, out var entry))
            return (entry.Height, entry.Version != version);

        var tex = new GPUTexture(_gl);
        _planetHeightTextures[planetKey] = (tex, -1);
        return (tex, true);
    }

    public void SetPlanetHeightVersion(object planetKey, int version)
    {
        if (_planetHeightTextures.TryGetValue(planetKey, out var entry))
            _planetHeightTextures[planetKey] = (entry.Height, version);
    }

    /// <summary>True when this cache's GL context is OpenGL ES (ANGLE / mobile / Xbox).</summary>
    public bool IsESContext
    {
        get
        {
#if XBOX_UWP
            return true;
#else
            if (ShaderLibrary.TryGet(_gl, out var lib) && lib != null)
                return lib.IsES;
            var ver = _gl.GetStringS(StringName.Version);
            return ver != null && ver.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }

    /// <summary>
    /// Get or create a cached <c>GL_TEXTURE_2D_ARRAY</c> built from <paramref name="textures"/>.
    /// Automatically invalidates and re-uploads if any layer's <see cref="Texture2D"/> reference,
    /// backing pixel buffer, or dimensions change. Layers whose dimensions differ from the array's
    /// size are resampled on the CPU; null layers are filled with the given default RGBA color.
    /// Pass <paramref name="targetSize"/> &lt;= 0 to auto-cap at 512 on ES/Xbox or 1024 on desktop.
    /// </summary>
    public GPUTexture GetOrCreateTexture2DArray(
        string cacheKey,
        IReadOnlyList<Texture2D?> textures,
        int targetSize,
        byte defaultR,
        byte defaultG,
        byte defaultB,
        byte defaultA)
    {
        int layers = Math.Max(1, textures?.Count ?? 0);
        int resolvedCap = targetSize > 0 ? targetSize : (IsESContext ? 512 : 1024);
        uint packedDefault = ((uint)defaultR << 24) | ((uint)defaultG << 16) | ((uint)defaultB << 8) | defaultA;

        if (_textureArrays.TryGetValue(cacheKey, out var existing)
            && existing.LayerRefs.Length == layers
            && existing.TargetSize == resolvedCap
            && existing.DefaultRgba == packedDefault)
        {
            bool match = true;
            for (int i = 0; i < layers; i++)
            {
                var t = (textures != null && i < textures.Count) ? textures[i] : null;
                if (!ReferenceEquals(existing.LayerRefs[i], t))
                {
                    match = false;
                    break;
                }
                if (t != null && (!ReferenceEquals(existing.LayerBuffers[i], t.Rgba)
                    || existing.LayerWidths[i] != t.Width
                    || existing.LayerHeights[i] != t.Height))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return existing.Texture;
        }

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        int maxW = 0, maxH = 0;
        for (int i = 0; i < layers; i++)
        {
            var t = (textures != null && i < textures.Count) ? textures[i] : null;
            if (t != null && t.Rgba != null && t.Width > 0 && t.Height > 0)
            {
                if (t.Width > maxW) maxW = t.Width;
                if (t.Height > maxH) maxH = t.Height;
            }
        }

        int arrayW = maxW > 0 ? Math.Clamp(maxW, 1, resolvedCap) : 1;
        int arrayH = maxH > 0 ? Math.Clamp(maxH, 1, resolvedCap) : 1;

        var layerPixels = new byte[]?[layers];
        var layerRefs = new Texture2D?[layers];
        var layerBuffers = new byte[]?[layers];
        var layerWidths = new int[layers];
        var layerHeights = new int[layers];

        for (int i = 0; i < layers; i++)
        {
            var t = (textures != null && i < textures.Count) ? textures[i] : null;
            layerRefs[i] = t;
            if (t != null && t.Rgba != null && t.Width > 0 && t.Height > 0)
            {
                layerBuffers[i] = t.Rgba;
                layerWidths[i] = t.Width;
                layerHeights[i] = t.Height;
                layerPixels[i] = ResampleRgba(t.Rgba, t.Width, t.Height, arrayW, arrayH);
            }
        }

        var gpuTex = existing?.Texture ?? new GPUTexture(_gl);
        gpuTex.CreateTexture2DArrayRgba8(
            arrayW,
            arrayH,
            layers,
            layerPixels,
            generateMipmaps: arrayW > 1 || arrayH > 1,
            defaultR: defaultR,
            defaultG: defaultG,
            defaultB: defaultB,
            defaultA: defaultA);

        _textureArrays[cacheKey] = new Texture2DArrayEntry
        {
            Texture = gpuTex,
            LayerRefs = layerRefs,
            LayerBuffers = layerBuffers,
            LayerWidths = layerWidths,
            LayerHeights = layerHeights,
            TargetSize = resolvedCap,
            DefaultRgba = packedDefault
        };

        FrameProfile.CountTextureUpload(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
        return gpuTex;
    }

    private static byte[] ResampleRgba(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH && src.Length >= dstW * dstH * 4)
            return src;

        var dst = new byte[dstW * dstH * 4];

        // Box-average downscale when shrinking on either axis to avoid aliasing.
        if (srcW > dstW || srcH > dstH)
        {
            for (int y = 0; y < dstH; y++)
            {
                int sy0 = y * srcH / dstH;
                int sy1 = Math.Max(sy0 + 1, (y + 1) * srcH / dstH);
                if (sy1 > srcH) sy1 = srcH;

                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = x * srcW / dstW;
                    int sx1 = Math.Max(sx0 + 1, (x + 1) * srcW / dstW);
                    if (sx1 > srcW) sx1 = srcW;

                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int row = sy * srcW * 4;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int idx = row + sx * 4;
                            if (idx + 3 < src.Length)
                            {
                                r += src[idx];
                                g += src[idx + 1];
                                b += src[idx + 2];
                                a += src[idx + 3];
                                n++;
                            }
                        }
                    }
                    if (n == 0) n = 1;
                    int o = (y * dstW + x) * 4;
                    dst[o]     = (byte)(r / n);
                    dst[o + 1] = (byte)(g / n);
                    dst[o + 2] = (byte)(b / n);
                    dst[o + 3] = (byte)(a / n);
                }
            }
            return dst;
        }

        // Bilinear upscale when expanding.
        for (int y = 0; y < dstH; y++)
        {
            float v = (y + 0.5f) * srcH / dstH - 0.5f;
            int y0 = (int)MathF.Floor(v);
            float fy = v - y0;
            int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
            y0 = Math.Clamp(y0, 0, srcH - 1);

            int row0 = y0 * srcW * 4;
            int row1 = y1 * srcW * 4;

            for (int x = 0; x < dstW; x++)
            {
                float u = (x + 0.5f) * srcW / dstW - 0.5f;
                int x0 = (int)MathF.Floor(u);
                float fx = u - x0;
                int x1 = Math.Clamp(x0 + 1, 0, srcW - 1);
                x0 = Math.Clamp(x0, 0, srcW - 1);

                int i00 = row0 + x0 * 4;
                int i10 = row0 + x1 * 4;
                int i01 = row1 + x0 * 4;
                int i11 = row1 + x1 * 4;

                float w00 = (1f - fx) * (1f - fy);
                float w10 = fx * (1f - fy);
                float w01 = (1f - fx) * fy;
                float w11 = fx * fy;

                int o = (y * dstW + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float val = src[i00 + c] * w00 + src[i10 + c] * w10 + src[i01 + c] * w01 + src[i11 + c] * w11;
                    dst[o + c] = (byte)Math.Clamp((int)(val + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }

    public void Dispose()
    {
        GpuMeshReleaseQueue.Unregister(this);
        PlanetGpuGrass.ReleaseGpuFor(this);
        PlanetWholePlanetForest.ReleaseGpuFor(this);
        PlanetGpuTrees.ReleaseGpuFor(this);
        foreach (var kv in _meshes)
            kv.Value.GPU.Dispose();
        _meshes.Clear();
        while (_pendingMeshReleases.TryDequeue(out _)) { }

        foreach (var kv in _textures)
            kv.Value.Dispose();
        _textures.Clear();

        foreach (var kv in _terrainSplatTextures)
        {
            kv.Value.Splat0?.Dispose();
            kv.Value.Splat1?.Dispose();
        }
        _terrainSplatTextures.Clear();

        foreach (var kv in _planetSplatTextures)
        {
            kv.Value.Splat0?.Dispose();
            kv.Value.Splat1?.Dispose();
        }
        _planetSplatTextures.Clear();

        foreach (var kv in _planetHeightTextures)
            kv.Value.Height?.Dispose();
        _planetHeightTextures.Clear();

        foreach (var kv in _textureArrays)
            kv.Value.Texture?.Dispose();
        _textureArrays.Clear();

        _whiteTex?.Dispose();
        _whiteTex = null;
        _blackTex?.Dispose();
        _blackTex = null;
        _flatNormalTex?.Dispose();
        _flatNormalTex = null;
        _blackCubemap?.Dispose();
        _blackCubemap = null;
        _skipBcnCompress.Clear();
    }
}
