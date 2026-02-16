#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Parses compiled .shader files from the Visual Shader Editor and caches
/// the resulting ShaderProgram objects so they can be used at render time.
/// </summary>
public static class CustomShaderCache
{
    private static readonly Dictionary<string, ShaderProgram?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get or compile a custom shader from a .shader file.
    /// Returns null if the file doesn't exist, can't be parsed, or fails to compile.
    /// Results are cached so compilation only happens once per path.
    /// </summary>
    public static ShaderProgram? GetOrCompile(string absolutePath, GL gl, bool isES = true)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        if (_cache.TryGetValue(absolutePath, out var cached))
            return cached;

        ShaderProgram? program = null;
        try
        {
            if (!File.Exists(absolutePath))
            {
                Log.Warning($"[CustomShaderCache] Shader file not found: {absolutePath}");
                _cache[absolutePath] = null;
                return null;
            }

            string fileContent = File.ReadAllText(absolutePath);
            var (vertexSource, fragmentSource) = ParseShaderFile(fileContent);

            if (string.IsNullOrWhiteSpace(vertexSource) || string.IsNullOrWhiteSpace(fragmentSource))
            {
                Log.Warning($"[CustomShaderCache] Could not parse VERTEX/FRAGMENT blocks from: {Path.GetFileName(absolutePath)}");
                _cache[absolutePath] = null;
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

        _cache[absolutePath] = program;
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
    /// Invalidate a single cached shader (e.g., after recompilation).
    /// </summary>
    public static void Invalidate(string absolutePath)
    {
        if (_cache.TryGetValue(absolutePath, out var old))
        {
            old?.Dispose();
            _cache.Remove(absolutePath);
        }
    }

    /// <summary>
    /// Dispose and clear all cached shader programs.
    /// Call on project close or GL context teardown.
    /// </summary>
    public static void Clear()
    {
        foreach (var kvp in _cache)
            kvp.Value?.Dispose();
        _cache.Clear();
    }
}
