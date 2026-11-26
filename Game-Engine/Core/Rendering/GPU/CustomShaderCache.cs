#nullable enable
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Public facade for custom <c>.shader</c> compilation and invalidation.
/// Delegates to the per-context <see cref="ShaderLibrary"/> and <see cref="ShaderFileParser"/>
/// so custom shaders share line-mapped diagnostics, safe hot-reload, and leak-free context cleanup.
/// </summary>
public static class CustomShaderCache
{
    /// <summary>
    /// Get or compile a custom shader from a <c>.shader</c> file for the given GL context.
    /// Returns null if the file does not exist or has never compiled successfully; on hot-reload
    /// failure the previous valid <see cref="ShaderProgram"/> stays active.
    /// </summary>
    public static ShaderProgram? GetOrCompile(string absolutePath, GL gl, bool isES = true)
        => GetOrCompile(absolutePath, gl, ShaderPassType.Forward, isES);

    /// <summary>
    /// Get or compile a specific pipeline pass (<see cref="ShaderPassType.Forward"/>,
    /// <see cref="ShaderPassType.GBuffer"/>, or <see cref="ShaderPassType.ShadowCaster"/>)
    /// for a custom <c>.shader</c> file on the given GL context.
    /// </summary>
    public static ShaderProgram? GetOrCompile(string absolutePath, GL gl, ShaderPassType pass, bool isES = true)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        var lib = ShaderLibrary.GetOrCreate(gl, isES);
        return lib.GetCustom(absolutePath, pass);
    }

    /// <summary>Returns true if the <c>.shader</c> file defines a <c>Surface</c> shader (CPU-only).</summary>
    public static bool IsSurfaceShader(string? absolutePath)
        => ShaderFileParser.GetCachedSchema(absolutePath)?.IsSurfaceShader == true;

    /// <summary>Retrieve the CPU-cached <see cref="ParsedShaderFile"/> schema without requiring an active GL context.</summary>
    public static bool TryGetSchema(string? absolutePath, out ParsedShaderFile? schema)
    {
        schema = ShaderFileParser.GetCachedSchema(absolutePath);
        return schema != null;
    }

    /// <summary>
    /// Mark a custom shader dirty across all active GL contexts so it safely hot-reloads on next draw.
    /// </summary>
    public static void Invalidate(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return;

        ShaderLibrary.NotifyFileChanged(absolutePath);
    }

    /// <summary>
    /// Dispose and clear all cached custom shader programs across all active GL contexts.
    /// </summary>
    public static void Clear()
    {
        ShaderLibrary.ClearAllCustomShaders();
    }

    /// <summary>
    /// Dispose and clear cached shaders for a specific GL context (call on GL context teardown).
    /// </summary>
    public static void ClearContext(GL gl)
    {
        ShaderLibrary.RemoveContext(gl);
    }
}
