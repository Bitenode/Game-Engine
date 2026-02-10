#nullable enable
using System;
using System.Collections.Generic;
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

    public void Dispose()
    {
        foreach (var kv in _meshes)
            kv.Value.GPU.Dispose();
        _meshes.Clear();

        foreach (var kv in _textures)
            kv.Value.Dispose();
        _textures.Clear();

        _whiteTex?.Dispose();
        _whiteTex = null;
    }
}
