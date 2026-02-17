#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Parses compiled .shader files from the Visual Shader Editor and caches
/// the resulting ShaderProgram objects so they can be used at render time.
/// Each GL context gets its own cache to prevent cross-context shader sharing.
/// </summary>
public static class CustomShaderCache
{
    /// <summary>Per-GL-context shader caches, keyed by a context identifier.</summary>
    private static readonly Dictionary<nint, Dictionary<string, ShaderProgram?>> _perContextCache = new();

    /// <summary>Get or create the cache for a specific GL context.</summary>
    private static Dictionary<string, ShaderProgram?> GetContextCache(GL gl)
    {
        // Use the GL object's native context handle as a unique identifier.
        // Fallback: use the GL object's hash code if no handle is available.
        nint key = gl.GetHashCode();
        if (!_perContextCache.TryGetValue(key, out var cache))
        {
            cache = new Dictionary<string, ShaderProgram?>(StringComparer.OrdinalIgnoreCase);
            _perContextCache[key] = cache;
        }
        return cache;
    }

    /// <summary>
    /// Get or compile a custom shader from a .shader file.
    /// Returns null if the file doesn't exist, can't be parsed, or fails to compile.
    /// Results are cached per GL context so compilation only happens once per path per context.
    /// </summary>
    public static ShaderProgram? GetOrCompile(string absolutePath, GL gl, bool isES = true)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        var cache = GetContextCache(gl);

        if (cache.TryGetValue(absolutePath, out var cached))
            return cached;

        ShaderProgram? program = null;
        try
        {
            if (!File.Exists(absolutePath))
            {
                Log.Warning($"[CustomShaderCache] Shader file not found: {absolutePath}");
                cache[absolutePath] = null;
                return null;
            }

            string fileContent = File.ReadAllText(absolutePath);
            var (vertexSource, fragmentSource) = ParseShaderFile(fileContent);

            if (string.IsNullOrWhiteSpace(vertexSource) || string.IsNullOrWhiteSpace(fragmentSource))
            {
                Log.Warning($"[CustomShaderCache] Could not parse VERTEX/FRAGMENT blocks from: {Path.GetFileName(absolutePath)}");
                cache[absolutePath] = null;
                return null;
            }

            // Adapt GLSL for the current GL context (desktop vs ES/ANGLE)
            vertexSource = ShaderSources.Adapt(vertexSource, isES);
            fragmentSource = ShaderSources.Adapt(fragmentSource, isES);

            program = new ShaderProgram(gl, vertexSource, fragmentSource);
            Log.Success($"[CustomShaderCache] Compiled custom shader: {Path.GetFileName(absolutePath)}");
        }
        catch (Exception ex)
        {
            Log.Error($"[CustomShaderCache] Failed to compile {Path.GetFileName(absolutePath)}: {ex.Message}");
            program = null;
        }

        cache[absolutePath] = program;
        return program;
    }

    /// <summary>
    /// Parse a .shader file to extract the VERTEX and FRAGMENT GLSL source blocks.
    /// Expected format:
    /// <code>
    /// Shader "Name" {
    ///     VERTEX {
    ///         &lt;glsl code&gt;
    ///     }
    ///     FRAGMENT {
    ///         &lt;glsl code&gt;
    ///     }
    /// }
    /// </code>
    /// </summary>
    private static (string vertex, string fragment) ParseShaderFile(string content)
    {
        string vertex = ExtractBlock(content, "VERTEX");
        string fragment = ExtractBlock(content, "FRAGMENT");
        return (vertex, fragment);
    }

    /// <summary>
    /// Extract a named block from the shader file content.
    /// Finds "BLOCKNAME" followed by "{" and matches braces to find the closing "}".
    /// </summary>
    private static string ExtractBlock(string content, string blockName)
    {
        int searchStart = 0;
        while (true)
        {
            int nameIdx = content.IndexOf(blockName, searchStart, StringComparison.Ordinal);
            if (nameIdx < 0) return "";

            // Find the opening brace after the block name
            int braceStart = -1;
            for (int i = nameIdx + blockName.Length; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '{') { braceStart = i; break; }
                if (!char.IsWhiteSpace(c)) break; // non-whitespace before brace = not our block
            }

            if (braceStart < 0)
            {
                searchStart = nameIdx + blockName.Length;
                continue;
            }

            // Match braces to find the closing brace
            int depth = 1;
            int bodyStart = braceStart + 1;
            for (int i = bodyStart; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return content.Substring(bodyStart, i - bodyStart).Trim();
                    }
                }
            }

            // Unmatched braces
            return "";
        }
    }

    /// <summary>
    /// Invalidate a single cached shader across all contexts (e.g., after recompilation).
    /// </summary>
    public static void Invalidate(string absolutePath)
    {
        foreach (var ctxCache in _perContextCache.Values)
        {
            if (ctxCache.TryGetValue(absolutePath, out var old))
            {
                old?.Dispose();
                ctxCache.Remove(absolutePath);
            }
        }
    }

    /// <summary>
    /// Dispose and clear all cached shader programs across all contexts.
    /// Call on project close or GL context teardown.
    /// </summary>
    public static void Clear()
    {
        foreach (var ctxCache in _perContextCache.Values)
        {
            foreach (var kvp in ctxCache)
                kvp.Value?.Dispose();
            ctxCache.Clear();
        }
        _perContextCache.Clear();
    }

    /// <summary>
    /// Dispose and clear cached shader programs for a specific GL context.
    /// Call when a GL context is being torn down.
    /// </summary>
    public static void ClearContext(GL gl)
    {
        nint key = gl.GetHashCode();
        if (_perContextCache.TryGetValue(key, out var ctxCache))
        {
            foreach (var kvp in ctxCache)
                kvp.Value?.Dispose();
            ctxCache.Clear();
            _perContextCache.Remove(key);
        }
    }
}
