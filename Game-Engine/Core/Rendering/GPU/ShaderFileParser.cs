#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>Shader property data types parsed from a <c>Properties { ... }</c> block.</summary>
public enum ShaderPropertyType
{
    Float,
    Range,
    Int,
    Color,
    Vector,
    Texture2D,
    TextureCube
}

/// <summary>Structured metadata for one declared property inside a <c>.shader</c> file.</summary>
public sealed class ShaderPropertyDesc
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required ShaderPropertyType Type { get; init; }
    public float RangeMin { get; init; }
    public float RangeMax { get; init; } = 1f;
    public float[] DefaultNumbers { get; init; } = Array.Empty<float>();
    public string? DefaultTexture { get; init; }
}

public enum ShaderRenderQueue
{
    Opaque = 0,
    AlphaTest = 1,
    Transparent = 2,
    Overlay = 3
}

public enum ShaderCullMode
{
    Back = 0,
    Front = 1,
    Off = 2
}

public enum ShaderLightingModel
{
    PBR = 0,
    Unlit = 1,
    Custom = 2
}

/// <summary>Render pipeline pass variant for compiling a <c>.shader</c> file.</summary>
public enum ShaderPassType
{
    Forward = 0,
    GBuffer = 1,
    ShadowCaster = 2
}

/// <summary>Render state parsed from an optional <c>RenderState { ... }</c> block.</summary>
public sealed class ShaderRenderState
{
    public ShaderRenderQueue Queue { get; init; } = ShaderRenderQueue.Opaque;
    public ShaderCullMode Cull { get; init; } = ShaderCullMode.Back;
    public ShaderLightingModel Lighting { get; init; } = ShaderLightingModel.PBR;
    public bool ZWrite { get; init; } = true;
    public float AlphaClipThreshold { get; init; } = 0.5f;
}

/// <summary>Result of parsing a <c>.shader</c> file.</summary>
public sealed class ParsedShaderFile
{
    public required string Name { get; init; }
    public IReadOnlyList<ShaderPropertyDesc> Properties { get; init; } = Array.Empty<ShaderPropertyDesc>();
    public ShaderRenderState RenderState { get; init; } = new();

    public string? VertexSource { get; init; }
    public int VertexStartLine { get; init; } = 1;

    public string? FragmentSource { get; init; }
    public int FragmentStartLine { get; init; } = 1;

    public string? SurfaceSource { get; init; }
    public int SurfaceStartLine { get; init; } = 1;

    public bool HasExplicitStages =>
        !string.IsNullOrWhiteSpace(VertexSource) && !string.IsNullOrWhiteSpace(FragmentSource);

    public bool HasSurfaceBlock => !string.IsNullOrWhiteSpace(SurfaceSource);

    public bool IsSurfaceShader =>
        !string.IsNullOrWhiteSpace(SurfaceSource) && string.IsNullOrWhiteSpace(FragmentSource);
}

/// <summary>
/// Comment- and string-aware unified <c>.shader</c> file parser.
/// Supports:
/// <list type="bullet">
///   <item>Explicit <c>VERTEX { ... }</c> / <c>FRAGMENT { ... }</c> blocks (100% backward compatible).</item>
///   <item>High-level <c>Surface { void surf(in SurfaceInput IN, inout SurfaceOutput OUT) { ... } }</c> blocks, expanded into Forward, GBuffer, and ShadowCaster passes.</item>
///   <item><c>Properties { ... }</c> metadata declarations for Inspector reflection.</item>
///   <item>Optional <c>RenderState { Queue ...; Cull ...; Lighting ...; ZWrite ...; }</c> directives.</item>
/// </list>
/// </summary>
public static class ShaderFileParser
{
    private static readonly object s_schemaCacheLock = new();
    private static readonly Dictionary<string, (DateTime LastWriteUtc, ParsedShaderFile Parsed)> s_schemaCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thread-safe, CPU-only cached schema lookup for a <c>.shader</c> file.
    /// Does not require an active OpenGL context; automatically re-parses when the file's
    /// write timestamp changes or when <see cref="InvalidateSchemaCache"/> is called.
    /// </summary>
    public static ParsedShaderFile? GetCachedSchema(string? absPath)
    {
        if (string.IsNullOrWhiteSpace(absPath)) return null;

        string fullPath;
        try { fullPath = Path.GetFullPath(absPath); }
        catch { return null; }

        if (!File.Exists(fullPath))
            return null;

        DateTime lastWrite;
        try { lastWrite = File.GetLastWriteTimeUtc(fullPath); }
        catch { return null; }

        lock (s_schemaCacheLock)
        {
            if (s_schemaCache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWrite)
                return cached.Parsed;
        }

        try
        {
            string content = File.ReadAllText(fullPath);
            var parsed = Parse(content, Path.GetFileNameWithoutExtension(fullPath));
            lock (s_schemaCacheLock)
            {
                s_schemaCache[fullPath] = (lastWrite, parsed);
            }
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Invalidate one cached <c>.shader</c> schema (or all cached schemas when <paramref name="absPath"/> is null/empty).
    /// </summary>
    public static void InvalidateSchemaCache(string? absPath = null)
    {
        lock (s_schemaCacheLock)
        {
            if (string.IsNullOrWhiteSpace(absPath))
            {
                s_schemaCache.Clear();
                return;
            }

            try { s_schemaCache.Remove(Path.GetFullPath(absPath)); }
            catch { s_schemaCache.Remove(absPath); }
        }
    }

    /// <summary>Parse the full contents of a <c>.shader</c> file.</summary>
    public static ParsedShaderFile Parse(string content, string fallbackName = "CustomShader")
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ParsedShaderFile { Name = fallbackName };
        }

        string text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        string shaderName = fallbackName;
        int bodyStart = 0;
        int bodyEnd = text.Length;

        // Locate optional outer: Shader "Name" { ... }
        int idx = 0;
        if (TryFindTopLevelShaderHeader(text, ref idx, out string? parsedName, out int outerOpen, out int outerClose))
        {
            if (!string.IsNullOrWhiteSpace(parsedName))
                shaderName = parsedName;
            bodyStart = outerOpen + 1;
            bodyEnd = outerClose;
        }

        string? vertexSrc = null;
        int vertexLine = 1;
        string? fragmentSrc = null;
        int fragmentLine = 1;
        string? surfaceSrc = null;
        int surfaceLine = 1;
        List<ShaderPropertyDesc> properties = new();
        ShaderRenderState renderState = new();

        int pos = bodyStart;
        while (pos < bodyEnd)
        {
            SkipWhitespaceAndComments(text, ref pos, bodyEnd);
            if (pos >= bodyEnd) break;

            if (!TryReadIdentifier(text, ref pos, bodyEnd, out string ident))
            {
                pos++;
                continue;
            }

            SkipWhitespaceAndComments(text, ref pos, bodyEnd);
            if (pos >= bodyEnd || text[pos] != '{')
            {
                // Skip non-block tokens
                continue;
            }

            int blockOpen = pos;
            int blockClose = FindMatchingBrace(text, blockOpen, bodyEnd);
            if (blockClose < 0)
                break;

            int contentStart = blockOpen + 1;
            string blockBody = text.Substring(contentStart, blockClose - contentStart);
            int blockStartLine = CountLineNumber(text, contentStart);

            if (ident.Equals("VERTEX", StringComparison.OrdinalIgnoreCase))
            {
                (vertexSrc, vertexLine) = TrimLeadingBlankLines(blockBody, blockStartLine);
            }
            else if (ident.Equals("FRAGMENT", StringComparison.OrdinalIgnoreCase))
            {
                (fragmentSrc, fragmentLine) = TrimLeadingBlankLines(blockBody, blockStartLine);
            }
            else if (ident.Equals("Surface", StringComparison.OrdinalIgnoreCase))
            {
                (surfaceSrc, surfaceLine) = TrimLeadingBlankLines(blockBody, blockStartLine);
            }
            else if (ident.Equals("Properties", StringComparison.OrdinalIgnoreCase))
            {
                properties = ParsePropertiesBlock(blockBody);
            }
            else if (ident.Equals("RenderState", StringComparison.OrdinalIgnoreCase))
            {
                renderState = ParseRenderStateBlock(blockBody);
            }

            pos = blockClose + 1;
        }

        return new ParsedShaderFile
        {
            Name = shaderName,
            Properties = properties,
            RenderState = renderState,
            VertexSource = vertexSrc,
            VertexStartLine = vertexLine,
            FragmentSource = fragmentSrc,
            FragmentStartLine = fragmentLine,
            SurfaceSource = surfaceSrc,
            SurfaceStartLine = surfaceLine
        };
    }

    /// <summary>
    /// Generate GLSL vertex and fragment source code for a given pass.
    /// For explicit <c>VERTEX</c>/<c>FRAGMENT</c> shaders, returns them directly (for <see cref="ShaderPassType.Forward"/>).
    /// For <c>Surface</c> shaders, wraps <c>surf(in SurfaceInput IN, inout SurfaceOutput OUT)</c> into a complete
    /// Forward, GBuffer, or ShadowCaster GLSL pair using <c>#include "Common/..."</c>.
    /// </summary>
    public static (string VertexSource, int VertexStartLine, string FragmentSource, int FragmentStartLine)
        GeneratePassSources(ParsedShaderFile parsed, ShaderPassType pass = ShaderPassType.Forward)
    {
        if (parsed.HasExplicitStages)
        {
            if (pass == ShaderPassType.ShadowCaster)
            {
                return (GenerateSurfaceShadowVert(), 1, GenerateSurfaceShadowFrag(), 1);
            }
            return (parsed.VertexSource!, parsed.VertexStartLine, parsed.FragmentSource!, parsed.FragmentStartLine);
        }

        if (parsed.IsSurfaceShader)
        {
            string propUniforms = BuildPropertyUniformDeclarations(parsed.Properties);
            return pass switch
            {
                ShaderPassType.Forward => (
                    parsed.VertexSource ?? GenerateSurfaceVert(),
                    parsed.VertexSource != null ? parsed.VertexStartLine : 1,
                    GenerateSurfaceForwardFrag(parsed, propUniforms),
                    1),
                ShaderPassType.GBuffer => (
                    parsed.VertexSource ?? GenerateSurfaceVert(),
                    parsed.VertexSource != null ? parsed.VertexStartLine : 1,
                    GenerateSurfaceGBufferFrag(parsed, propUniforms),
                    1),
                ShaderPassType.ShadowCaster => parsed.RenderState.Queue == ShaderRenderQueue.AlphaTest
                    ? (GenerateSurfaceShadowAlphaVert(), 1, GenerateSurfaceShadowAlphaFrag(parsed, propUniforms), 1)
                    : (GenerateSurfaceShadowVert(), 1, GenerateSurfaceShadowFrag(), 1),
                _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, null)
            };
        }

        return (string.Empty, 1, string.Empty, 1);
    }

    // ── Surface Shader Code Generation ────────────────────────────────────────

    private static string BuildPropertyUniformDeclarations(IReadOnlyList<ShaderPropertyDesc> props)
    {
        if (props.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var p in props)
        {
            string glslType = p.Type switch
            {
                ShaderPropertyType.Float or ShaderPropertyType.Range => "float",
                ShaderPropertyType.Int => "int",
                ShaderPropertyType.Color or ShaderPropertyType.Vector => "vec4",
                ShaderPropertyType.Texture2D => "sampler2D",
                ShaderPropertyType.TextureCube => "samplerCube",
                _ => "float"
            };
            sb.Append("uniform ").Append(glslType).Append(' ').Append(p.Name).AppendLine(";");
        }
        return sb.ToString();
    }

    private static string GenerateSurfaceVert() =>
@"layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

#include ""Common/FrameGlobals.glsl""
#include ""Common/LightingGlobals.glsl""
#include ""Common/Skinning.glsl""

uniform mat4 uModel;
uniform mat4 uNormalMatrix;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vTexCoord;
out vec4 vShadowCoord;

void main()
{
    vec3 localPos  = aPosition;
    vec3 localNorm = aNormal;
    ApplySkinning(localPos, localNorm);

    vec4 worldPos = uModel * vec4(localPos, 1.0);
    vWorldPos     = worldPos.xyz;
    vWorldNormal  = normalize((uNormalMatrix * vec4(localNorm, 0.0)).xyz);
    vTexCoord     = aTexCoord;
    vShadowCoord  = uShadowVP * worldPos;
    gl_Position   = uProj * uView * worldPos;
}
";

    private static string GenerateSurfaceHeader(string propUniforms) =>
$@"in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vTexCoord;
in vec4 vShadowCoord;

#include ""Common/FrameGlobals.glsl""
#include ""Common/LightingGlobals.glsl""

#define uCameraPos      uCamPos
#define uTime           uFrameTime
#define uLightIntensity uDiffuseK

uniform sampler2D uTexture0;
uniform sampler2D uTexture1;
uniform sampler2D uTexture2;

uniform int   uHasNormalMap;
uniform float uNormalStrength;
uniform int   uHasSpecularMap;
uniform float uShadowBias;
uniform vec3  uSunDir;

uniform float uRoughness;
uniform float uMetallic;

// ReflectionProbe cubemap IBL (unit 15)
uniform samplerCube uEnvMap;
uniform int         uHasEnvMap;
uniform float       uEnvIntensity;
uniform float       uEnvMaxMip;

// HDR output toggle (0 = Reinhard in-shader for LDR targets, 1 = linear HDR for PostProcess)
uniform int         uHdrOutput;

{propUniforms}

#include ""Common/NormalMapping.glsl""
#include ""Common/PBR.glsl""
#define SHADOW_CASCADES
#include ""Common/Shadows.glsl""

struct SurfaceInput
{{
    vec3 worldPos;
    vec3 worldNormal;
    vec2 uv;
    vec3 viewDir;
    float time;
}};

struct SurfaceOutput
{{
    vec3 Albedo;
    vec3 Normal;
    bool NormalIsWorldSpace;
    float Metallic;
    float Roughness;
    float AO;
    vec3 Emission;
    float Alpha;
}};
";

    private static string GenerateSurfaceForwardFrag(ParsedShaderFile parsed, string propUniforms)
    {
        bool alphaTest = parsed.RenderState.Queue == ShaderRenderQueue.AlphaTest;
        bool unlit = parsed.RenderState.Lighting == ShaderLightingModel.Unlit;
        string clipThreshold = Math.Clamp(parsed.RenderState.AlphaClipThreshold, 0f, 1f)
            .ToString("0.0###", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append(GenerateSurfaceHeader(propUniforms));
        sb.AppendLine("out vec4 FragColor;");
        sb.AppendLine();
        sb.AppendLine(parsed.SurfaceSource);
        sb.AppendLine();
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        sb.AppendLine("    vec3 geoN = normalize(vWorldNormal);");
        sb.AppendLine("    vec3 V = normalize(uCameraPos - vWorldPos);");
        sb.AppendLine("    SurfaceInput IN;");
        sb.AppendLine("    IN.worldPos    = vWorldPos;");
        sb.AppendLine("    IN.worldNormal = geoN;");
        sb.AppendLine("    IN.uv          = vTexCoord;");
        sb.AppendLine("    IN.viewDir     = V;");
        sb.AppendLine("    IN.time        = uTime;");
        sb.AppendLine();
        sb.AppendLine("    SurfaceOutput OUT;");
        sb.AppendLine("    OUT.Albedo    = vec3(1.0);");
        sb.AppendLine("    OUT.Normal    = vec3(0.0, 0.0, 1.0);");
        sb.AppendLine("    OUT.NormalIsWorldSpace = false;");
        sb.AppendLine("    OUT.Metallic  = clamp(uMetallic, 0.0, 1.0);");
        sb.AppendLine("    OUT.Roughness = clamp(uRoughness < 0.01 ? 0.5 : uRoughness, 0.04, 1.0);");
        sb.AppendLine("    OUT.AO        = 1.0;");
        sb.AppendLine("    OUT.Emission  = vec3(0.0);");
        sb.AppendLine("    OUT.Alpha     = 1.0;");
        sb.AppendLine();
        sb.AppendLine("    surf(IN, OUT);");
        if (alphaTest)
            sb.AppendLine($"    if (OUT.Alpha < {clipThreshold}) discard;");
        sb.AppendLine();
        sb.AppendLine("    vec3 N = geoN;");
        sb.AppendLine("    if (OUT.NormalIsWorldSpace)");
        sb.AppendLine("    {");
        sb.AppendLine("        float nLen = length(OUT.Normal);");
        sb.AppendLine("        if (nLen > 0.0001) N = OUT.Normal / nLen;");
        sb.AppendLine("    }");
        sb.AppendLine("    else if (length(OUT.Normal - vec3(0.0, 0.0, 1.0)) > 0.001)");
        sb.AppendLine("    {");
        sb.AppendLine("        mat3 TBN = CotangentFrame(geoN, vWorldPos, vTexCoord);");
        sb.AppendLine("        N = normalize(TBN * normalize(OUT.Normal));");
        sb.AppendLine("        if (dot(N, geoN) < 0.1) N = normalize(mix(N, geoN, 0.5));");
        sb.AppendLine("    }");
        if (unlit)
        {
            sb.AppendLine("    vec3 color = OUT.Albedo + OUT.Emission;");
        }
        else
        {
            sb.AppendLine("    float roughness = clamp(OUT.Roughness, 0.04, 1.0);");
            sb.AppendLine("    float metallic  = clamp(OUT.Metallic, 0.0, 1.0);");
            sb.AppendLine("    vec3 F0 = mix(vec3(0.04), OUT.Albedo, metallic);");
            sb.AppendLine("    float intensity = max(uLightIntensity, 0.0);");
            sb.AppendLine("    vec3 lightCol = dot(uLightColor, uLightColor) < 0.001 ? vec3(1.0) : uLightColor;");
            sb.AppendLine("    vec3 L;");
            sb.AppendLine("    float atten = 1.0;");
            sb.AppendLine("    if (uLightIsPoint != 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        vec3 toL = uLightPos - vWorldPos;");
            sb.AppendLine("        float dist = length(toL);");
            sb.AppendLine("        L = dist > 0.0001 ? toL / dist : vec3(0.0, 1.0, 0.0);");
            sb.AppendLine("        float range = max(uLightRange, 0.001);");
            sb.AppendLine("        float r = clamp(1.0 - dist / range, 0.0, 1.0);");
            sb.AppendLine("        atten = r * r;");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine("        L = normalize(-uLightDir);");
            sb.AppendLine("    }");
            sb.AppendLine("    float shadow = 1.0;");
            sb.AppendLine("    if (uHasShadow != 0 && uLightIsPoint == 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        float baseBias = uShadowBias > 0.0 ? uShadowBias : 0.006;");
            sb.AppendLine("        shadow = SampleShadowCascaded(vWorldPos, geoN, uSunDir, baseBias, 0.04);");
            sb.AppendLine("    }");
            sb.AppendLine("    vec3 Lo = EvaluateCookTorrance(N, V, L, OUT.Albedo, metallic, roughness, F0, lightCol * intensity * atten) * shadow;");
            sb.AppendLine("    for (int pi = 0; pi < 4; ++pi)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (pi >= uPointCount) break;");
            sb.AppendLine("        vec3 toP = uPointPosRange[pi].xyz - vWorldPos;");
            sb.AppendLine("        float pDist = length(toP);");
            sb.AppendLine("        float pRange = max(uPointPosRange[pi].w, 0.001);");
            sb.AppendLine("        if (pDist < pRange)");
            sb.AppendLine("        {");
            sb.AppendLine("            vec3 pL = pDist > 0.0001 ? toP / pDist : vec3(0.0, 1.0, 0.0);");
            sb.AppendLine("            float pr = clamp(1.0 - pDist / pRange, 0.0, 1.0);");
            sb.AppendLine("            float pAtten = pr * pr;");
            sb.AppendLine("            vec3 pRad = uPointColor[pi].rgb * max(uPointColor[pi].a, 1.0) * pAtten;");
            sb.AppendLine("            Lo += EvaluateCookTorrance(N, V, pL, OUT.Albedo, metallic, roughness, F0, pRad);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    vec3 R = reflect(-V, N);");
            sb.AppendLine("    vec3 Fenv = FresnelSchlickRoughness(max(dot(N, V), 0.0), F0, roughness);");
            sb.AppendLine("    vec3 envSpec = (uHasEnvMap != 0)");
            sb.AppendLine("        ? textureLod(uEnvMap, R, roughness * max(uEnvMaxMip, 0.0)).rgb * Fenv * uEnvIntensity");
            sb.AppendLine("        : ProceduralSkyIBL(R, roughness, uAmbient) * Fenv * (0.35 + 0.65 * metallic);");
            sb.AppendLine("    float amb = uAmbient < 0.01 ? 0.15 : uAmbient;");
            sb.AppendLine("    vec3 kD = (vec3(1.0) - Fenv) * (1.0 - metallic);");
            sb.AppendLine("    vec3 color = (kD * vec3(amb) * OUT.Albedo + envSpec) * OUT.AO + Lo + OUT.Emission;");
            sb.AppendLine("    if (uHdrOutput == 0) color = color / (color + vec3(1.0));");
        }
        sb.AppendLine("    FragColor = vec4(color, OUT.Alpha);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateSurfaceGBufferFrag(ParsedShaderFile parsed, string propUniforms)
    {
        bool alphaTest = parsed.RenderState.Queue == ShaderRenderQueue.AlphaTest;
        string clipThreshold = Math.Clamp(parsed.RenderState.AlphaClipThreshold, 0f, 1f)
            .ToString("0.0###", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append(GenerateSurfaceHeader(propUniforms));
        sb.AppendLine("layout(location = 0) out vec4 gAlbedoMetallic;");
        sb.AppendLine("layout(location = 1) out vec4 gNormalRoughness;");
        sb.AppendLine("layout(location = 2) out vec4 gEmissiveAO;");
        sb.AppendLine();
        sb.AppendLine(parsed.SurfaceSource);
        sb.AppendLine();
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        sb.AppendLine("    vec3 geoN = normalize(vWorldNormal);");
        sb.AppendLine("    vec3 V = normalize(uCameraPos - vWorldPos);");
        sb.AppendLine("    SurfaceInput IN;");
        sb.AppendLine("    IN.worldPos    = vWorldPos;");
        sb.AppendLine("    IN.worldNormal = geoN;");
        sb.AppendLine("    IN.uv          = vTexCoord;");
        sb.AppendLine("    IN.viewDir     = V;");
        sb.AppendLine("    IN.time        = uTime;");
        sb.AppendLine();
        sb.AppendLine("    SurfaceOutput OUT;");
        sb.AppendLine("    OUT.Albedo    = vec3(1.0);");
        sb.AppendLine("    OUT.Normal    = vec3(0.0, 0.0, 1.0);");
        sb.AppendLine("    OUT.NormalIsWorldSpace = false;");
        sb.AppendLine("    OUT.Metallic  = clamp(uMetallic, 0.0, 1.0);");
        sb.AppendLine("    OUT.Roughness = clamp(uRoughness < 0.01 ? 0.5 : uRoughness, 0.04, 1.0);");
        sb.AppendLine("    OUT.AO        = 1.0;");
        sb.AppendLine("    OUT.Emission  = vec3(0.0);");
        sb.AppendLine("    OUT.Alpha     = 1.0;");
        sb.AppendLine();
        sb.AppendLine("    surf(IN, OUT);");
        if (alphaTest)
            sb.AppendLine($"    if (OUT.Alpha < {clipThreshold}) discard;");
        sb.AppendLine();
        sb.AppendLine("    vec3 N = geoN;");
        sb.AppendLine("    if (OUT.NormalIsWorldSpace)");
        sb.AppendLine("    {");
        sb.AppendLine("        float nLen = length(OUT.Normal);");
        sb.AppendLine("        if (nLen > 0.0001) N = OUT.Normal / nLen;");
        sb.AppendLine("    }");
        sb.AppendLine("    else if (length(OUT.Normal - vec3(0.0, 0.0, 1.0)) > 0.001)");
        sb.AppendLine("    {");
        sb.AppendLine("        mat3 TBN = CotangentFrame(geoN, vWorldPos, vTexCoord);");
        sb.AppendLine("        N = normalize(TBN * normalize(OUT.Normal));");
        sb.AppendLine("        if (dot(N, geoN) < 0.1) N = normalize(mix(N, geoN, 0.5));");
        sb.AppendLine("    }");
        sb.AppendLine("    gAlbedoMetallic  = vec4(OUT.Albedo, clamp(OUT.Metallic, 0.0, 1.0));");
        sb.AppendLine("    gNormalRoughness = vec4(N * 0.5 + 0.5, clamp(OUT.Roughness, 0.04, 1.0));");
        sb.AppendLine("    gEmissiveAO      = vec4(OUT.Emission, clamp(OUT.AO, 0.0, 1.0));");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateSurfaceShadowVert() =>
@"layout(location = 0) in vec3 aPosition;

#include ""Common/Skinning.glsl""

uniform mat4 uMVP;

void main()
{
    vec3 pos = aPosition;
    ApplySkinningPositionOnly(pos);
    gl_Position = uMVP * vec4(pos, 1.0);
}
";

    private static string GenerateSurfaceShadowFrag() =>
@"out vec4 FragColor;

void main()
{
    FragColor = vec4(0.0);
}
";

    private static string GenerateSurfaceShadowAlphaVert() =>
@"layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

#include ""Common/FrameGlobals.glsl""
#include ""Common/LightingGlobals.glsl""
#include ""Common/Skinning.glsl""

uniform mat4 uMVP;
uniform mat4 uModel;
uniform mat4 uNormalMatrix;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vTexCoord;
out vec4 vShadowCoord;

void main()
{
    vec3 localPos  = aPosition;
    vec3 localNorm = aNormal;
    ApplySkinning(localPos, localNorm);

    vec4 worldPos = uModel * vec4(localPos, 1.0);
    vWorldPos     = worldPos.xyz;
    vWorldNormal  = normalize((uNormalMatrix * vec4(localNorm, 0.0)).xyz);
    vTexCoord     = aTexCoord;
    vShadowCoord  = uShadowVP * worldPos;
    gl_Position   = uMVP * vec4(localPos, 1.0);
}
";

    private static string GenerateSurfaceShadowAlphaFrag(ParsedShaderFile parsed, string propUniforms)
    {
        string clipThreshold = Math.Clamp(parsed.RenderState.AlphaClipThreshold, 0f, 1f)
            .ToString("0.0###", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append(GenerateSurfaceHeader(propUniforms));
        sb.AppendLine("out vec4 FragColor;");
        sb.AppendLine();
        sb.AppendLine(parsed.SurfaceSource);
        sb.AppendLine();
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        sb.AppendLine("    vec3 geoN = normalize(vWorldNormal);");
        sb.AppendLine("    vec3 V = normalize(uCameraPos - vWorldPos);");
        sb.AppendLine("    SurfaceInput IN;");
        sb.AppendLine("    IN.worldPos    = vWorldPos;");
        sb.AppendLine("    IN.worldNormal = geoN;");
        sb.AppendLine("    IN.uv          = vTexCoord;");
        sb.AppendLine("    IN.viewDir     = V;");
        sb.AppendLine("    IN.time        = uTime;");
        sb.AppendLine();
        sb.AppendLine("    SurfaceOutput OUT;");
        sb.AppendLine("    OUT.Albedo    = vec3(1.0);");
        sb.AppendLine("    OUT.Normal    = vec3(0.0, 0.0, 1.0);");
        sb.AppendLine("    OUT.NormalIsWorldSpace = false;");
        sb.AppendLine("    OUT.Metallic  = clamp(uMetallic, 0.0, 1.0);");
        sb.AppendLine("    OUT.Roughness = clamp(uRoughness < 0.01 ? 0.5 : uRoughness, 0.04, 1.0);");
        sb.AppendLine("    OUT.AO        = 1.0;");
        sb.AppendLine("    OUT.Emission  = vec3(0.0);");
        sb.AppendLine("    OUT.Alpha     = 1.0;");
        sb.AppendLine();
        sb.AppendLine("    surf(IN, OUT);");
        sb.AppendLine($"    if (OUT.Alpha < {clipThreshold}) discard;");
        sb.AppendLine("    FragColor = vec4(0.0);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Properties Block Parser ───────────────────────────────────────────────

    private static List<ShaderPropertyDesc> ParsePropertiesBlock(string block)
    {
        var result = new List<ShaderPropertyDesc>();
        int pos = 0;
        int end = block.Length;

        while (pos < end)
        {
            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos >= end) break;

            if (!TryReadIdentifier(block, ref pos, end, out string propName))
            {
                pos++;
                continue;
            }

            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos >= end || block[pos] != '(')
                continue;

            int parenEnd = FindMatchingParen(block, pos, end);
            if (parenEnd < 0) break;

            string meta = block.Substring(pos + 1, parenEnd - pos - 1);
            pos = parenEnd + 1;

            // Parse ("Display Name", Type)
            int mPos = 0;
            SkipWhitespaceAndComments(meta, ref mPos, meta.Length);
            string displayName = TryReadStringLiteral(meta, ref mPos, meta.Length, out string? dn) && dn != null
                ? dn
                : propName;
            SkipWhitespaceAndComments(meta, ref mPos, meta.Length);
            if (mPos < meta.Length && meta[mPos] == ',') mPos++;
            SkipWhitespaceAndComments(meta, ref mPos, meta.Length);

            string typeStr = meta.Substring(mPos).Trim();
            var (propType, rangeMin, rangeMax) = ParsePropertyType(typeStr);

            // Optional: = DefaultValue (and optional trailing {} for Unity-style texture syntax)
            float[] defaultNums = Array.Empty<float>();
            string? defaultTex = null;

            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos < end && block[pos] == '=')
            {
                pos++;
                SkipWhitespaceAndComments(block, ref pos, end);
                if (pos < end)
                {
                    if (block[pos] == '"')
                    {
                        if (TryReadStringLiteral(block, ref pos, end, out string? texDef))
                            defaultTex = texDef;
                        SkipWhitespaceAndComments(block, ref pos, end);
                        if (pos < end && block[pos] == '{')
                        {
                            int closeB = FindMatchingBrace(block, pos, end);
                            if (closeB >= 0) pos = closeB + 1;
                        }
                    }
                    else if (block[pos] == '(')
                    {
                        int closeP = FindMatchingParen(block, pos, end);
                        if (closeP >= 0)
                        {
                            defaultNums = ParseNumberTuple(block.Substring(pos + 1, closeP - pos - 1));
                            pos = closeP + 1;
                        }
                    }
                    else
                    {
                        int startNum = pos;
                        while (pos < end && !char.IsWhiteSpace(block[pos]) && block[pos] != ';' && block[pos] != ',')
                            pos++;
                        string numToken = block.Substring(startNum, pos - startNum);
                        if (float.TryParse(numToken, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                            defaultNums = new[] { v };
                    }
                }
            }

            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos < end && block[pos] == ';') pos++;

            result.Add(new ShaderPropertyDesc
            {
                Name = propName,
                DisplayName = displayName,
                Type = propType,
                RangeMin = rangeMin,
                RangeMax = rangeMax,
                DefaultNumbers = defaultNums,
                DefaultTexture = defaultTex
            });
        }

        return result;
    }

    private static (ShaderPropertyType Type, float Min, float Max) ParsePropertyType(string typeStr)
    {
        if (typeStr.StartsWith("Range", StringComparison.OrdinalIgnoreCase))
        {
            int open = typeStr.IndexOf('(');
            int close = typeStr.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                var nums = ParseNumberTuple(typeStr.Substring(open + 1, close - open - 1));
                float min = nums.Length > 0 ? nums[0] : 0f;
                float max = nums.Length > 1 ? nums[1] : 1f;
                return (ShaderPropertyType.Range, min, max);
            }
            return (ShaderPropertyType.Range, 0f, 1f);
        }

        if (typeStr.Equals("Color", StringComparison.OrdinalIgnoreCase))
            return (ShaderPropertyType.Color, 0f, 1f);
        if (typeStr.Equals("Vector", StringComparison.OrdinalIgnoreCase))
            return (ShaderPropertyType.Vector, 0f, 1f);
        if (typeStr.Equals("Int", StringComparison.OrdinalIgnoreCase) || typeStr.Equals("Integer", StringComparison.OrdinalIgnoreCase))
            return (ShaderPropertyType.Int, 0f, 1f);
        if (typeStr.Equals("2D", StringComparison.OrdinalIgnoreCase) || typeStr.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            return (ShaderPropertyType.Texture2D, 0f, 1f);
        if (typeStr.Equals("Cube", StringComparison.OrdinalIgnoreCase) || typeStr.Equals("TextureCube", StringComparison.OrdinalIgnoreCase))
            return (ShaderPropertyType.TextureCube, 0f, 1f);

        return (ShaderPropertyType.Float, 0f, 1f);
    }

    private static float[] ParseNumberTuple(string tuple)
    {
        var parts = tuple.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<float>(parts.Length);
        foreach (var part in parts)
        {
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                list.Add(f);
        }
        return list.ToArray();
    }

    // ── RenderState Block Parser ──────────────────────────────────────────────

    private static ShaderRenderState ParseRenderStateBlock(string block)
    {
        var queue = ShaderRenderQueue.Opaque;
        var cull = ShaderCullMode.Back;
        var lighting = ShaderLightingModel.PBR;
        bool zwrite = true;
        float alphaClip = 0.5f;

        int pos = 0;
        int end = block.Length;
        while (pos < end)
        {
            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos >= end) break;

            if (!TryReadIdentifier(block, ref pos, end, out string key))
            {
                pos++;
                continue;
            }

            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos < end && (block[pos] == '=' || block[pos] == ':'))
            {
                pos++;
                SkipWhitespaceAndComments(block, ref pos, end);
            }

            if (key.Equals("AlphaClipThreshold", StringComparison.OrdinalIgnoreCase)
                || key.Equals("AlphaClip", StringComparison.OrdinalIgnoreCase))
            {
                int startNum = pos;
                while (pos < end && !char.IsWhiteSpace(block[pos]) && block[pos] != ';')
                    pos++;
                string numTok = block.Substring(startNum, pos - startNum);
                if (float.TryParse(numTok, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedClip))
                    alphaClip = Math.Clamp(parsedClip, 0f, 1f);
            }
            else
            {
                if (!TryReadIdentifier(block, ref pos, end, out string val))
                    continue;

                if (key.Equals("Queue", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<ShaderRenderQueue>(val, true, out var q)) queue = q;
                }
                else if (key.Equals("Cull", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<ShaderCullMode>(val, true, out var c)) cull = c;
                }
                else if (key.Equals("Lighting", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<ShaderLightingModel>(val, true, out var l)) lighting = l;
                }
                else if (key.Equals("ZWrite", StringComparison.OrdinalIgnoreCase))
                {
                    zwrite = !val.Equals("Off", StringComparison.OrdinalIgnoreCase)
                          && !val.Equals("False", StringComparison.OrdinalIgnoreCase)
                          && !val.Equals("0", StringComparison.OrdinalIgnoreCase);
                }
            }

            SkipWhitespaceAndComments(block, ref pos, end);
            if (pos < end && block[pos] == ';') pos++;
        }

        return new ShaderRenderState
        {
            Queue = queue,
            Cull = cull,
            Lighting = lighting,
            ZWrite = zwrite,
            AlphaClipThreshold = alphaClip
        };
    }

    // ── Comment- & String-Aware Lexing Helpers ────────────────────────────────

    private static bool TryFindTopLevelShaderHeader(
        string text, ref int pos, out string? name, out int openBrace, out int closeBrace)
    {
        name = null;
        openBrace = -1;
        closeBrace = -1;
        int end = text.Length;

        SkipWhitespaceAndComments(text, ref pos, end);
        int saved = pos;
        if (!TryReadIdentifier(text, ref pos, end, out string kw) || !kw.Equals("Shader", StringComparison.OrdinalIgnoreCase))
        {
            pos = saved;
            return false;
        }

        SkipWhitespaceAndComments(text, ref pos, end);
        if (pos < end && text[pos] == '"')
            TryReadStringLiteral(text, ref pos, end, out name);

        SkipWhitespaceAndComments(text, ref pos, end);
        if (pos >= end || text[pos] != '{')
        {
            pos = saved;
            return false;
        }

        openBrace = pos;
        closeBrace = FindMatchingBrace(text, openBrace, end);
        if (closeBrace < 0)
        {
            pos = saved;
            return false;
        }

        return true;
    }

    private static void SkipWhitespaceAndComments(string text, ref int pos, int end)
    {
        while (pos < end)
        {
            char c = text[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }
            if (c == '/' && pos + 1 < end)
            {
                char next = text[pos + 1];
                if (next == '/')
                {
                    pos += 2;
                    while (pos < end && text[pos] != '\n') pos++;
                    continue;
                }
                if (next == '*')
                {
                    pos += 2;
                    while (pos + 1 < end && !(text[pos] == '*' && text[pos + 1] == '/')) pos++;
                    pos = Math.Min(end, pos + 2);
                    continue;
                }
            }
            break;
        }
    }

    private static bool TryReadIdentifier(string text, ref int pos, int end, out string ident)
    {
        ident = string.Empty;
        if (pos >= end || !(char.IsLetter(text[pos]) || text[pos] == '_'))
            return false;
        int start = pos;
        pos++;
        while (pos < end && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
            pos++;
        ident = text.Substring(start, pos - start);
        return true;
    }

    private static bool TryReadStringLiteral(string text, ref int pos, int end, out string? value)
    {
        value = null;
        if (pos >= end || text[pos] != '"')
            return false;

        pos++; // skip opening quote
        var sb = new StringBuilder();
        while (pos < end)
        {
            char c = text[pos++];
            if (c == '\\' && pos < end)
            {
                sb.Append(text[pos++]);
                continue;
            }
            if (c == '"')
            {
                value = sb.ToString();
                return true;
            }
            sb.Append(c);
        }
        return false;
    }

    private static int FindMatchingBrace(string text, int openIndex, int end)
        => FindMatchingDelimiter(text, openIndex, end, '{', '}');

    private static int FindMatchingParen(string text, int openIndex, int end)
        => FindMatchingDelimiter(text, openIndex, end, '(', ')');

    private static int FindMatchingDelimiter(string text, int openIndex, int end, char openChar, char closeChar)
    {
        int depth = 1;
        int pos = openIndex + 1;
        while (pos < end)
        {
            char c = text[pos];

            // Skip line comments
            if (c == '/' && pos + 1 < end && text[pos + 1] == '/')
            {
                pos += 2;
                while (pos < end && text[pos] != '\n') pos++;
                continue;
            }

            // Skip block comments
            if (c == '/' && pos + 1 < end && text[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < end && !(text[pos] == '*' && text[pos + 1] == '/')) pos++;
                pos = Math.Min(end, pos + 2);
                continue;
            }

            // Skip string literals
            if (c == '"')
            {
                pos++;
                while (pos < end)
                {
                    if (text[pos] == '\\' && pos + 1 < end) { pos += 2; continue; }
                    if (text[pos] == '"') { pos++; break; }
                    pos++;
                }
                continue;
            }

            if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) return pos;
            }

            pos++;
        }
        return -1;
    }

    private static int CountLineNumber(string text, int index)
    {
        int line = 1;
        int limit = Math.Min(index, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static (string Trimmed, int StartLine) TrimLeadingBlankLines(string body, int startLine)
    {
        int line = startLine;
        int i = 0;
        while (i < body.Length)
        {
            int nl = body.IndexOf('\n', i);
            if (nl < 0)
                break;
            bool allSpace = true;
            for (int k = i; k < nl; k++)
            {
                if (!char.IsWhiteSpace(body[k])) { allSpace = false; break; }
            }
            if (!allSpace) break;
            i = nl + 1;
            line++;
        }
        return (body.Substring(i).TrimEnd(), line);
    }
}
