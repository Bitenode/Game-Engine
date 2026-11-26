#nullable enable
using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Unified binder for custom <c>.shader</c> <see cref="MaterialPropertyBag"/> overrides,
/// schema defaults (<see cref="ParsedShaderFile.Properties"/>), and <see cref="ShaderRenderState"/>
/// (cull mode, ZWrite, and alpha blending) with deterministic GL state restoration.
/// </summary>
public static class MaterialBinder
{
    /// <summary>
    /// Dedicated safe texture units for custom <c>Texture2D</c> shader properties.
    /// Completely disjoint from:
    /// <list type="bullet">
    ///   <item>Units 0, 1, 2: <c>uTexture0</c> (Albedo), <c>uTexture1</c> (Normal), <c>uTexture2</c> (Specular)</item>
    ///   <item>Unit 4: <c>uShadowMap</c> (legacy custom forward pass)</item>
    ///   <item>Units 7, 8, 9, 10: <c>uShadowMap</c> and <c>uShadowMapC1..C3</c> (standard/Surface CSM)</item>
    ///   <item>Unit 15: <c>uEnvMap</c> (ReflectionProbe cubemap for Surface forward shaders)</item>
    /// </list>
    /// Stays strictly within OpenGL ES 3.0's guaranteed 16 texture image units (0..15).
    /// </summary>
    public static ReadOnlySpan<int> CustomTextureUnits => new[] { 3, 5, 6, 11, 12, 13, 14 };

    private static readonly int[] s_customTextureUnits = { 3, 5, 6, 11, 12, 13, 14 };

    /// <summary>
    /// Zero-allocation RAII scope that applies a <see cref="ShaderRenderState"/> before a draw
    /// and deterministically restores the caller's GL state on <see cref="Dispose"/>.
    /// </summary>
    public readonly struct RenderStateScope : IDisposable
    {
        private readonly GL _gl;
        private readonly bool _restoreCullEnabled;
        private readonly TriangleFace _restoreCullFace;
        private readonly bool _restoreDepthWrite;
        private readonly bool _restoreBlend;
        private readonly bool _active;

        internal RenderStateScope(
            GL gl,
            ShaderRenderState? state,
            bool doubleSidedOverride,
            bool invertFrontFace,
            bool restoreCullEnabled,
            TriangleFace restoreCullFace,
            bool restoreDepthWrite,
            bool restoreBlend)
        {
            _gl = gl;
            _restoreCullEnabled = restoreCullEnabled;
            _restoreCullFace = restoreCullFace;
            _restoreDepthWrite = restoreDepthWrite;
            _restoreBlend = restoreBlend;
            _active = true;

            // Cull mode
            if (doubleSidedOverride || state?.Cull == ShaderCullMode.Off)
            {
                gl.Disable(EnableCap.CullFace);
            }
            else if (state?.Cull == ShaderCullMode.Front)
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(invertFrontFace ? TriangleFace.Back : TriangleFace.Front);
            }
            else
            {
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(invertFrontFace ? TriangleFace.Front : TriangleFace.Back);
            }

            if (state != null)
            {
                // ZWrite
                if (!state.ZWrite)
                    gl.DepthMask(false);

                // Blend mode for Transparent / Overlay queues
                if (state.Queue == ShaderRenderQueue.Transparent || state.Queue == ShaderRenderQueue.Overlay)
                {
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
            }
        }

        public void Dispose()
        {
            if (!_active) return;

            if (_restoreCullEnabled)
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(_restoreCullFace);
            }
            else
            {
                _gl.Disable(EnableCap.CullFace);
            }

            _gl.DepthMask(_restoreDepthWrite);

            if (_restoreBlend)
                _gl.Enable(EnableCap.Blend);
            else
                _gl.Disable(EnableCap.Blend);
        }
    }

    /// <summary>
    /// Apply <see cref="ShaderRenderState"/> (cull, ZWrite, blend) and return a scope that
    /// restores the caller's GL state when disposed.
    /// </summary>
    public static RenderStateScope ApplyRenderState(
        GL gl,
        ShaderRenderState? state,
        bool doubleSidedOverride = false,
        bool invertFrontFace = false,
        bool restoreCullEnabled = true,
        TriangleFace restoreCullFace = TriangleFace.Back,
        bool restoreDepthWrite = true,
        bool restoreBlend = false)
    {
        return new RenderStateScope(
            gl, state, doubleSidedOverride, invertFrontFace,
            restoreCullEnabled, restoreCullFace, restoreDepthWrite, restoreBlend);
    }

    /// <summary>
    /// Bind all custom shader properties declared in <paramref name="schema"/> (using overrides from
    /// <paramref name="mat"/>.<see cref="Material.Properties"/> or schema defaults), plus any extra
    /// overrides present in <paramref name="mat"/>.<see cref="Material.Properties"/>.
    /// </summary>
    public static void BindProperties(
        ShaderProgram shader,
        Material? mat,
        ParsedShaderFile? schema,
        ResourceCache cache)
    {
        var bag = mat?.Properties;
        bool hasBag = bag != null && !bag.IsEmpty;
        bool hasSchemaProps = schema != null && schema.Properties.Count > 0;

        if (!hasBag && !hasSchemaProps)
            return;

        if (hasBag && bag!.TexturePaths.Count > 0)
            bag.ResolveTextures();

        int texSlotIdx = 0;

        // 1. Bind all schema-declared properties (material override -> schema default)
        if (hasSchemaProps)
        {
            var props = schema!.Properties;
            for (int i = 0; i < props.Count; i++)
            {
                var prop = props[i];
                switch (prop.Type)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float val = (hasBag && bag!.TryGetFloat(prop.Name, out float ov))
                            ? ov
                            : (prop.DefaultNumbers.Length > 0 ? prop.DefaultNumbers[0] : 0f);
                        shader.SetFloat(prop.Name, val);
                        break;
                    }

                    case ShaderPropertyType.Int:
                    {
                        int val = (hasBag && bag!.TryGetInt(prop.Name, out int ov))
                            ? ov
                            : (prop.DefaultNumbers.Length > 0 ? (int)MathF.Round(prop.DefaultNumbers[0]) : 0);
                        shader.SetInt(prop.Name, val);
                        break;
                    }

                    case ShaderPropertyType.Color:
                    {
                        if (hasBag && bag!.TryGetColor(prop.Name, out var col))
                        {
                            shader.SetVector4(prop.Name, col.R / 255f, col.G / 255f, col.B / 255f, col.A / 255f);
                        }
                        else if (hasBag && bag!.TryGetVector(prop.Name, out var cv))
                        {
                            shader.SetVector4(prop.Name, cv.X, cv.Y, cv.Z, cv.W);
                        }
                        else
                        {
                            var d = prop.DefaultNumbers;
                            float r = d.Length > 0 ? d[0] : 1f;
                            float g = d.Length > 1 ? d[1] : 1f;
                            float b = d.Length > 2 ? d[2] : 1f;
                            float a = d.Length > 3 ? d[3] : 1f;
                            shader.SetVector4(prop.Name, r, g, b, a);
                        }
                        break;
                    }

                    case ShaderPropertyType.Vector:
                    {
                        if (hasBag && bag!.TryGetVector(prop.Name, out var vec))
                        {
                            shader.SetVector4(prop.Name, vec.X, vec.Y, vec.Z, vec.W);
                        }
                        else
                        {
                            var d = prop.DefaultNumbers;
                            float x = d.Length > 0 ? d[0] : 0f;
                            float y = d.Length > 1 ? d[1] : 0f;
                            float z = d.Length > 2 ? d[2] : 0f;
                            float w = d.Length > 3 ? d[3] : 0f;
                            shader.SetVector4(prop.Name, x, y, z, w);
                        }
                        break;
                    }

                    case ShaderPropertyType.Texture2D:
                    {
                        if (texSlotIdx >= s_customTextureUnits.Length)
                            break;

                        int unit = s_customTextureUnits[texSlotIdx++];
                        Texture2D? tex = null;
                        if (hasBag)
                            bag!.TryGetTexture(prop.Name, out tex);

                        GPUTexture gpuTex = tex != null
                            ? cache.GetTexture(tex)
                            : GetDefaultTexture(cache, prop.DefaultTexture);

                        gpuTex.Bind((TextureUnit)((int)TextureUnit.Texture0 + unit));
                        shader.SetTexture(prop.Name, unit);
                        break;
                    }
                }
            }
        }

        // 2. Bind any extra overrides in mat.Properties not already declared in schema.Properties
        if (!hasBag) return;

        foreach (var kv in bag!.Floats)
        {
            if (!IsDeclaredInSchema(schema, kv.Key))
                shader.SetFloat(kv.Key, kv.Value);
        }

        foreach (var kv in bag.Ints)
        {
            if (!IsDeclaredInSchema(schema, kv.Key))
                shader.SetInt(kv.Key, kv.Value);
        }

        foreach (var kv in bag.Colors)
        {
            if (!IsDeclaredInSchema(schema, kv.Key))
                shader.SetVector4(kv.Key, kv.Value.R / 255f, kv.Value.G / 255f, kv.Value.B / 255f, kv.Value.A / 255f);
        }

        foreach (var kv in bag.Vectors)
        {
            if (!IsDeclaredInSchema(schema, kv.Key))
                shader.SetVector4(kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Z, kv.Value.W);
        }

        foreach (var kv in bag.Textures)
        {
            if (IsDeclaredInSchema(schema, kv.Key)) continue;
            if (texSlotIdx >= s_customTextureUnits.Length) break;

            int unit = s_customTextureUnits[texSlotIdx++];
            GPUTexture gpuTex = kv.Value != null
                ? cache.GetTexture(kv.Value)
                : cache.GetWhiteTexture();
            gpuTex.Bind((TextureUnit)((int)TextureUnit.Texture0 + unit));
            shader.SetTexture(kv.Key, unit);
        }
    }

    private static bool IsDeclaredInSchema(ParsedShaderFile? schema, string name)
    {
        if (schema == null) return false;
        var props = schema.Properties;
        for (int i = 0; i < props.Count; i++)
        {
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static GPUTexture GetDefaultTexture(ResourceCache cache, string? defaultName)
    {
        if (string.IsNullOrWhiteSpace(defaultName))
            return cache.GetWhiteTexture();

        if (defaultName.Equals("black", StringComparison.OrdinalIgnoreCase))
            return cache.GetBlackTexture();
        if (defaultName.Equals("bump", StringComparison.OrdinalIgnoreCase)
            || defaultName.Equals("normal", StringComparison.OrdinalIgnoreCase))
            return cache.GetFlatNormalTexture();

        return cache.GetWhiteTexture();
    }
}
