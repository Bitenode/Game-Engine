#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

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
    private readonly Dictionary<Texture2D, GPUTexture> _textures = new(64);

    // Per-context terrain splatmap textures (avoids cross-context GL issues)
    // Also tracks the splatmap version last uploaded so each context re-uploads independently.
    private readonly Dictionary<Terrain, (GPUTexture Splat0, GPUTexture Splat1, int Version)> _terrainSplatTextures = new();

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
    }

    /// <summary>Call when the scene changes to invalidate cached state.</summary>
    public void InvalidateAll()
    {
        _globalVersion++;
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
        foreach (var kv in _meshes)
            kv.Value.GPU.Dispose();
        _meshes.Clear();

        foreach (var kv in _textures)
            kv.Value.Dispose();
        _textures.Clear();

        foreach (var kv in _terrainSplatTextures)
        {
            kv.Value.Splat0?.Dispose();
            kv.Value.Splat1?.Dispose();
        }
        _terrainSplatTextures.Clear();

        _globalVersion++;
        FlushRequested = false;
    }

    /// <summary>
    /// Get or create a GPUMesh for the given engine Mesh.
    /// Uploads data if the mesh is new or has changed.
    /// </summary>
    public GPUMesh GetMesh(Mesh mesh)
    {
        if (_meshes.TryGetValue(mesh, out var entry))
        {
            return entry.GPU;
        }

        var gpuMesh = new GPUMesh(_gl);
        gpuMesh.Upload(mesh);
        _meshes[mesh] = new GPUMeshEntry { GPU = gpuMesh, Version = _globalVersion };
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
            entry.Version = _globalVersion;
            _meshes[mesh] = entry;
        }
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

    /// <summary>
    /// Get or create a GPUTexture for the given engine Texture2D.
    /// </summary>
    public GPUTexture GetTexture(Texture2D tex)
    {
        if (_textures.TryGetValue(tex, out var gpuTex))
            return gpuTex;

        gpuTex = new GPUTexture(_gl);
        gpuTex.Upload(tex);
        _textures[tex] = gpuTex;
        return gpuTex;
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

    public void Dispose()
    {
        foreach (var kv in _meshes)
            kv.Value.GPU.Dispose();
        _meshes.Clear();

        foreach (var kv in _textures)
            kv.Value.Dispose();
        _textures.Clear();

        foreach (var kv in _terrainSplatTextures)
        {
            kv.Value.Splat0?.Dispose();
            kv.Value.Splat1?.Dispose();
        }
        _terrainSplatTextures.Clear();

        _whiteTex?.Dispose();
        _whiteTex = null;
    }
}
