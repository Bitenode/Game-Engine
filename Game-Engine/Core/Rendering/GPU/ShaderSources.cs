#nullable enable

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// All GLSL shader sources embedded as string constants.
/// Desktop shaders use #version 330 core; call <see cref="Adapt"/> to convert
/// for OpenGL ES 3.0 (ANGLE on Windows).
/// </summary>
public static class ShaderSources
{
    /// <summary>
    /// Adapt a GLSL source string for the current GL context.
    /// When <paramref name="isES"/> is true, replaces the version directive with
    /// #version 300 es and adds required precision qualifiers.
    /// </summary>
    public static string Adapt(string glsl, bool isES)
    {
        // Trim leading whitespace — ANGLE requires #version on the very first line.
        glsl = glsl.TrimStart('\r', '\n', ' ', '\t');

        if (!isES) return glsl;
        return glsl.Replace(
            "#version 330 core",
            "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\nprecision highp samplerCube;");
    }

    // =====================================================================
    // STANDARD (PBR-like: diffuse + Blinn-Phong specular)
    // =====================================================================
    public const string StandardVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 3) in vec4 aBoneIds;
layout(location = 4) in vec4 aBoneWeights;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uNormalMatrix;   // transpose(inverse(model))
uniform mat4 uShadowVP;      // light view-proj for shadow mapping

// Wind / vegetation animation
uniform float uWindTime;
uniform vec3  uWindDir;
uniform float uWindStrength;
uniform int   uIsVegetation;  // 1 = apply wind displacement

// Skeletal animation (GPU skinning)
uniform int  uHasBones;       // 1 = apply bone skinning
uniform mat4 uBones[128];     // bone final matrices

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;

void main()
{
    vec3 localPos  = aPosition;
    vec3 localNorm = aNormal;

    // GPU skinning
    if (uHasBones == 1)
    {
        ivec4 ids = ivec4(aBoneIds);
        vec4  w   = aBoneWeights;

        mat4 skin = w.x * uBones[ids.x]
                  + w.y * uBones[ids.y]
                  + w.z * uBones[ids.z]
                  + w.w * uBones[ids.w];

        localPos  = (skin * vec4(aPosition, 1.0)).xyz;
        localNorm = mat3(skin) * aNormal;
    }

    vec4 worldPos = uModel * vec4(localPos, 1.0);

    // Wind vertex animation for vegetation
    if (uIsVegetation == 1 && uWindStrength > 0.0)
    {
        // Height factor: vertices higher above model origin sway more
        float localY = aPosition.y;
        float h = clamp(localY / 6.0, 0.0, 1.0);
        float h2 = h * h;  // quadratic: tips move much more than base

        // Trunk sway: slow large movement
        float phase1 = uWindTime * 1.2 + worldPos.x * 0.5 + worldPos.z * 0.3;
        vec3 trunkSway = uWindDir * uWindStrength * h2 * sin(phase1);

        // Leaf flutter: fast small jitter perpendicular to wind
        float phase2 = uWindTime * 3.7 + dot(worldPos.xyz, vec3(1.3, 0.7, 2.1));
        vec3 flutter = uWindDir.zxy * uWindStrength * 0.3 * h * sin(phase2);

        // Secondary micro-flutter for realism
        float phase3 = uWindTime * 5.3 + worldPos.x * 2.7 - worldPos.z * 1.9;
        flutter += vec3(0.0, 1.0, 0.0) * uWindStrength * 0.15 * h * sin(phase3);

        worldPos.xyz += trunkSway + flutter;
    }

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(localNorm, 0.0)).xyz);
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    gl_Position = uProj * uView * worldPos;
}
";

    public const string StandardFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;

// Material
uniform vec4  uBaseColor;       // RGBA tint
uniform float uRoughness;
uniform float uMetallic;
uniform float uAlphaCutoff;
uniform bool  uTransparent;
uniform bool  uDoubleSided;

// Albedo texture
uniform sampler2D uAlbedoTex;
uniform bool      uHasAlbedoTex;

uniform sampler2D uOpacityTex;
uniform int       uHasOpacityTex;
uniform float     uLumaClip;

// Normal map
uniform sampler2D uNormalMap;
uniform int       uHasNormalMap;
uniform float     uNormalStrength;

// Specular map
uniform sampler2D uSpecularTex;
uniform int       uHasSpecularTex;

// Metallic map
uniform sampler2D uMetallicTex;
uniform int       uHasMetallicTex;

// Roughness map
uniform sampler2D uRoughnessTex;
uniform int       uHasRoughnessTex;

// Ambient occlusion map
uniform sampler2D uAOTex;
uniform int       uHasAOTex;

// Emissive
uniform sampler2D uEmissiveTex;
uniform int       uHasEmissiveTex;
uniform vec3      uEmissiveColor;
uniform float     uEmissiveIntensity;

// Shadow (cascaded shadow maps)
#define MAX_CASCADES 4
uniform sampler2D uShadowMap;          // cascade 0 (backward compat)
uniform sampler2D uShadowMapC1;        // cascade 1
uniform sampler2D uShadowMapC2;        // cascade 2
uniform sampler2D uShadowMapC3;        // cascade 3
uniform mat4      uShadowVPC[MAX_CASCADES];
uniform float     uCascadeSplits[MAX_CASCADES];
uniform int       uCascadeCount;
uniform bool      uHasShadow;

// Lighting
uniform vec3  uLightDir;        // world-space direction TO the light (normalized)
uniform vec3  uLightPos;
uniform float uLightRange;
uniform bool  uLightIsPoint;
uniform float uDiffuseK;
uniform float uAmbient;
uniform float uShadowBias;
uniform vec3  uSunDir;          // direction FROM sun (for slope bias)

// Camera
uniform vec3  uCamPos;

out vec4 FragColor;

// Construct a cotangent-frame TBN matrix from screen-space derivatives.
// This allows normal mapping without per-vertex tangent attributes.
mat3 CotangentFrame(vec3 N, vec3 p, vec2 uv)
{
    vec3 dp1  = dFdx(p);
    vec3 dp2  = dFdy(p);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);

    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    // Guard against degenerate derivatives (edge-on surfaces, UV discontinuities)
    float maxLen = max(dot(T, T), dot(B, B));
    if (maxLen < 1e-6) return mat3(vec3(1,0,0), vec3(0,1,0), N);
    float invmax = inversesqrt(maxLen);
    return mat3(T * invmax, B * invmax, N);
}

float SampleShadowMap(int cascade, vec2 uv, float compareDepth)
{
    // PCF 3x3 for the selected cascade
    vec2 texelSize;
    float result = 0.0;
    if (cascade == 0)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMap, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMap, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else if (cascade == 1)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC1, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC1, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else if (cascade == 2)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC2, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC2, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC3, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC3, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    return max(result / 9.0, 0.10);
}

float ShadowCalc(vec4 sc, vec3 N)
{
    if (!uHasShadow) return 1.0;

    // Determine cascade index from fragment distance to camera
    float fragDist = length(vWorldPos - uCamPos);
    int cascadeIdx = 0;
    for (int i = 0; i < uCascadeCount; i++)
    {
        if (fragDist < uCascadeSplits[i])
        {
            cascadeIdx = i;
            break;
        }
        cascadeIdx = i;
    }

    // Project into the selected cascade's light space
    vec4 shadowCoord = uShadowVPC[cascadeIdx] * vec4(vWorldPos, 1.0);
    vec3 proj = shadowCoord.xyz / shadowCoord.w;
    proj = proj * 0.5 + 0.5;

    // Outside shadow map bounds -> fully lit
    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    // Slope-scaled bias: surfaces nearly parallel to the light get more bias
    float cosTheta = max(dot(N, -uSunDir), 0.0);
    float bias = uShadowBias + uShadowBias * 3.0 * (1.0 - cosTheta);
    float currentDepth = proj.z - bias;

    float shadow = SampleShadowMap(cascadeIdx, proj.xy, currentDepth);

    // Smooth edge falloff near shadow map borders
    float fadeMargin = 0.08;
    float fadeX = smoothstep(0.0, fadeMargin, proj.x) * smoothstep(1.0, 1.0 - fadeMargin, proj.x);
    float fadeY = smoothstep(0.0, fadeMargin, proj.y) * smoothstep(1.0, 1.0 - fadeMargin, proj.y);
    float fade = fadeX * fadeY;
    return mix(1.0, shadow, fade);
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    if (uDoubleSided && !gl_FrontFacing) N = -N;

    // Save geometric normal for shadow bias (unaffected by normal map)
    vec3 geoN = N;

    // ── Normal mapping (screen-space derivative TBN) ──
    if (uHasNormalMap == 1)
    {
        vec3 tnm = texture(uNormalMap, vUV).rgb;
        vec2 nxy = (tnm.xy * 2.0 - 1.0) * uNormalStrength;
        float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
        vec3 mapN = normalize(vec3(nxy, nz));
        mat3 TBN = CotangentFrame(N, vWorldPos, vUV);
        N = normalize(TBN * mapN);

        // Prevent the normal map from flipping the surface away from the
        // geometric normal — clamp to at least 10% alignment to avoid
        // completely black patches on lit surfaces.
        if (dot(N, geoN) < 0.1)
            N = normalize(mix(N, geoN, 0.5));
    }

    // ── Albedo ──
    vec4 albedo = uBaseColor;
    if (uHasAlbedoTex)
        albedo *= texture(uAlbedoTex, vUV);

    float maskA = albedo.a;
    if (uHasOpacityTex != 0)
        maskA *= texture(uOpacityTex, vUV).r;

    float luma = dot(albedo.rgb, vec3(0.299, 0.587, 0.114));
    float mx = max(albedo.r, max(albedo.g, albedo.b));
    bool darkBackdrop = (uLumaClip > 0.0) && (luma < uLumaClip) && (mx < 0.23);

    // Alpha test + RGB-only sheet heuristic (many FBX leaf textures have no alpha, only dark backing RGB)
    bool clipAlpha = false;
    if (uTransparent)
        clipAlpha = maskA < uAlphaCutoff;
    else if (uHasAlbedoTex)
    {
        bool byMask = (uAlphaCutoff > 0.0005) && (maskA < uAlphaCutoff);
        clipAlpha = byMask || darkBackdrop;
    }
    if (clipAlpha)
        discard;

    // ── Per-pixel PBR parameters from texture maps ──
    float roughness = uRoughness;
    if (uHasRoughnessTex == 1)
        roughness = texture(uRoughnessTex, vUV).r;

    float metallic = uMetallic;
    if (uHasMetallicTex == 1)
        metallic = texture(uMetallicTex, vUV).r;

    float aoFactor = 1.0;
    if (uHasAOTex == 1)
        aoFactor = texture(uAOTex, vUV).r;

    float specMask = 1.0;
    if (uHasSpecularTex == 1)
        specMask = texture(uSpecularTex, vUV).r;

    // ── Light direction ──
    // uLightDir is FROM the light (shine direction). Negate for TOWARD the light.
    vec3 L;
    float atten = 1.0;
    if (uLightIsPoint)
    {
        vec3 toLight = uLightPos - vWorldPos;
        float dist = length(toLight);
        L = toLight / max(dist, 0.0001);
        if (uLightRange > 0.0)
        {
            float t = dist / uLightRange;
            atten = 1.0 / (1.0 + t * t);
        }
    }
    else
    {
        L = -uLightDir;
    }

    float NdotL = max(dot(N, L), 0.0);
    float diffuse = NdotL * atten;
    if (diffuse > 1.0) diffuse = 1.0;

    // Shadow — use geometric normal for bias to prevent normal-map shadow acne
    float shadow = ShadowCalc(vShadowCoord, geoN);

    // ── Specular (Blinn-Phong) ──
    float specular = 0.0;
    if (uDiffuseK > 0.0 && diffuse > 0.0)
    {
        float smoothness = 1.0 - roughness;
        float shininess = 8.0 + smoothness * smoothness * 248.0;
        vec3 V = normalize(uCamPos - vWorldPos);
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        specular = pow(NdotH, shininess) * (0.25 + 0.75 * metallic) * diffuse * specMask;
    }

    // ── Combine ──
    // Shadow attenuates both ambient (sky occlusion) and diffuse
    // In shadowed areas ambient drops to ~35%, simulating occlusion from the sun/sky
    float ambShadow = mix(0.35, 1.0, shadow);
    float shade = clamp(uAmbient * ambShadow + uDiffuseK * diffuse * shadow, 0.0, 1.0);
    vec3 color = albedo.rgb * shade + vec3(specular * shadow);

    // AO darkens the entire lit result (ambient + diffuse + specular)
    // Applied before emissive so self-illumination is unaffected by occlusion
    color *= aoFactor;

    // ── Emissive (bypasses lighting) ──
    vec3 emissive = uEmissiveColor * uEmissiveIntensity;
    if (uHasEmissiveTex == 1)
        emissive *= texture(uEmissiveTex, vUV).rgb;
    color += emissive;

    color = clamp(color, 0.0, 1.0);

    float alpha = uTransparent ? maskA : 1.0;
    FragColor = vec4(color, alpha);
}
";

    // =====================================================================
    // G-BUFFER (deferred geometry pass — outputs to MRT)
    // =====================================================================

    /// <summary>GBuffer vertex shader — identical to StandardVert (same transforms, skinning, wind).</summary>
    public const string GBufferVert = StandardVert;

    public const string GBufferFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;

// Material
uniform vec4  uBaseColor;
uniform float uRoughness;
uniform float uMetallic;
uniform float uAlphaCutoff;
uniform bool  uTransparent;
uniform bool  uDoubleSided;

// Albedo texture
uniform sampler2D uAlbedoTex;
uniform bool      uHasAlbedoTex;

uniform sampler2D uOpacityTex;
uniform int       uHasOpacityTex;
uniform float     uLumaClip;

// Normal map
uniform sampler2D uNormalMap;
uniform int       uHasNormalMap;
uniform float     uNormalStrength;

// Specular map
uniform sampler2D uSpecularTex;
uniform int       uHasSpecularTex;

// Metallic map
uniform sampler2D uMetallicTex;
uniform int       uHasMetallicTex;

// Roughness map
uniform sampler2D uRoughnessTex;
uniform int       uHasRoughnessTex;

// Ambient occlusion map
uniform sampler2D uAOTex;
uniform int       uHasAOTex;

// Emissive
uniform sampler2D uEmissiveTex;
uniform int       uHasEmissiveTex;
uniform vec3      uEmissiveColor;
uniform float     uEmissiveIntensity;

// MRT outputs
layout(location = 0) out vec4 gAlbedoMetallic;    // RT0: Albedo.rgb + Metallic
layout(location = 1) out vec4 gNormalRoughness;    // RT1: Normal.xyz + Roughness
layout(location = 2) out vec4 gEmissiveAO;         // RT2: Emissive.rgb + AO

// Construct cotangent-frame TBN from screen-space derivatives
mat3 CotangentFrame(vec3 N, vec3 p, vec2 uv)
{
    vec3 dp1  = dFdx(p);
    vec3 dp2  = dFdy(p);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);

    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    float maxLen = max(dot(T, T), dot(B, B));
    if (maxLen < 1e-6) return mat3(vec3(1,0,0), vec3(0,1,0), N);
    float invmax = inversesqrt(maxLen);
    return mat3(T * invmax, B * invmax, N);
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    if (uDoubleSided && !gl_FrontFacing) N = -N;

    vec3 geoN = N;

    // Normal mapping
    if (uHasNormalMap == 1)
    {
        vec3 tnm = texture(uNormalMap, vUV).rgb;
        vec2 nxy = (tnm.xy * 2.0 - 1.0) * uNormalStrength;
        float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
        vec3 mapN = normalize(vec3(nxy, nz));
        mat3 TBN = CotangentFrame(N, vWorldPos, vUV);
        N = normalize(TBN * mapN);

        // Prevent normal map from flipping surface away from geometric normal
        if (dot(N, geoN) < 0.1)
            N = normalize(mix(N, geoN, 0.5));
    }

    // Albedo
    vec4 albedo = uBaseColor;
    if (uHasAlbedoTex)
        albedo *= texture(uAlbedoTex, vUV);

    float maskA = albedo.a;
    if (uHasOpacityTex != 0)
        maskA *= texture(uOpacityTex, vUV).r;

    float luma = dot(albedo.rgb, vec3(0.299, 0.587, 0.114));
    float mx = max(albedo.r, max(albedo.g, albedo.b));
    bool darkBackdrop = (uLumaClip > 0.0) && (luma < uLumaClip) && (mx < 0.23);

    bool clipAlpha = false;
    if (uTransparent)
        clipAlpha = maskA < uAlphaCutoff;
    else if (uHasAlbedoTex)
    {
        bool byMask = (uAlphaCutoff > 0.0005) && (maskA < uAlphaCutoff);
        clipAlpha = byMask || darkBackdrop;
    }
    if (clipAlpha)
        discard;

    // PBR parameters
    float roughness = uRoughness;
    if (uHasRoughnessTex == 1)
        roughness = texture(uRoughnessTex, vUV).r;

    float metallic = uMetallic;
    if (uHasMetallicTex == 1)
        metallic = texture(uMetallicTex, vUV).r;

    float aoFactor = 1.0;
    if (uHasAOTex == 1)
        aoFactor = texture(uAOTex, vUV).r;

    // Emissive
    vec3 emissive = uEmissiveColor * uEmissiveIntensity;
    if (uHasEmissiveTex == 1)
        emissive *= texture(uEmissiveTex, vUV).rgb;

    // Write to G-buffer MRT
    gAlbedoMetallic  = vec4(albedo.rgb, metallic);
    gNormalRoughness = vec4(N * 0.5 + 0.5, roughness);  // encode normal to [0,1]
    gEmissiveAO      = vec4(emissive, aoFactor);
}
";

    // =====================================================================
    // DEFERRED LIGHTING (fullscreen pass reading G-buffer)
    // =====================================================================

    public const string DeferredLightingVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string DeferredLightingFrag = @"
#version 330 core
in vec2 vUV;

// G-buffer textures
uniform sampler2D gAlbedoMetallic;   // RT0
uniform sampler2D gNormalRoughness;  // RT1
uniform sampler2D gEmissiveAO;       // RT2
uniform sampler2D gDepth;            // Depth buffer

// Shadow (cascaded shadow maps)
#define MAX_CASCADES 4
uniform sampler2D uShadowMap;          // cascade 0
uniform sampler2D uShadowMapC1;        // cascade 1
uniform sampler2D uShadowMapC2;        // cascade 2
uniform sampler2D uShadowMapC3;        // cascade 3
uniform mat4      uShadowVPC[MAX_CASCADES];
uniform float     uCascadeSplits[MAX_CASCADES];
uniform int       uCascadeCount;
uniform bool      uHasShadow;
uniform mat4      uShadowVP;           // backward compat (cascade 0)
uniform float     uShadowBias;
uniform vec3      uSunDir;

// Camera
uniform vec3  uCamPos;
uniform mat4  uInvViewProj;  // inverse(proj * view)

// Directional lights (max 4; first receives cascaded shadow)
#define MAX_DIR_LIGHTS 4
uniform int   uDirLightCount;
uniform vec3  uDirLightDirs[MAX_DIR_LIGHTS];
uniform vec3  uDirLightColors[MAX_DIR_LIGHTS];
uniform float uDirLightIntensities[MAX_DIR_LIGHTS];

// Tiled point/spot lights (texture atlases)
uniform int   uLocalLightCount;
uniform int   uTilesX;
uniform int   uTilesY;
uniform sampler2D uTileMeta;
uniform sampler2D uTileLightIdx;
uniform sampler2D uLocalLightTex;

// Optional reflection probe (specular IBL)
uniform int   uHasProbe;
uniform samplerCube uProbeSpec;
uniform float uProbeMipCount;
uniform float uProbeBlend;

// Ambient
uniform float uAmbient;

// SSAO (optional)
uniform sampler2D uSSAOTex;
uniform bool      uHasSSAO;
uniform float     uSSAOIntensity;

out vec4 FragColor;

#define PI 3.14159265359

// ── PBR: Cook-Torrance BRDF ──

// GGX / Trowbridge-Reitz normal distribution
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a  = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

// Smith's Schlick-GGX geometry function
float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);
}

// Fresnel-Schlick approximation
vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Reconstruct world position from depth
vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

// Shadow calculation — deferred version with cascaded shadow maps.
float SampleDeferredShadow(int cascade, vec2 uv, float compareDepth)
{
    float result = 0.0;
    vec2 texelSize;
    if (cascade == 0)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMap, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMap, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else if (cascade == 1)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC1, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC1, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else if (cascade == 2)
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC2, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC2, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    else
    {
        texelSize = 1.0 / vec2(textureSize(uShadowMapC3, 0));
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
                result += (compareDepth > texture(uShadowMapC3, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    }
    return max(result / 9.0, 0.10);
}

float ShadowCalc(vec3 worldPos, vec3 N)
{
    if (!uHasShadow) return 1.0;

    // Normal offset for shadow acne reduction
    float cosTheta = max(dot(N, -uSunDir), 0.0);
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    float normalOff = 0.15 * sinTheta;
    float lightOff  = 0.08;
    vec3 offsetPos = worldPos + N * normalOff + (-uSunDir) * lightOff;

    // Select cascade based on distance to camera
    float fragDist = length(worldPos - uCamPos);
    int cascadeIdx = 0;
    for (int i = 0; i < uCascadeCount; i++)
    {
        if (fragDist < uCascadeSplits[i])
        {
            cascadeIdx = i;
            break;
        }
        cascadeIdx = i;
    }

    vec4 sc = uShadowVPC[cascadeIdx] * vec4(offsetPos, 1.0);
    vec3 proj = sc.xyz / sc.w;
    proj = proj * 0.5 + 0.5;

    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    // Aggressive slope-scaled depth bias for deferred
    float bias = uShadowBias * 3.0 + uShadowBias * 8.0 * (1.0 - cosTheta);
    bias = max(bias, 0.002);
    float currentDepth = proj.z - bias;

    float shadow = SampleDeferredShadow(cascadeIdx, proj.xy, currentDepth);

    // Edge fade
    float fadeMargin = 0.08;
    float fadeX = smoothstep(0.0, fadeMargin, proj.x) * smoothstep(1.0, 1.0 - fadeMargin, proj.x);
    float fadeY = smoothstep(0.0, fadeMargin, proj.y) * smoothstep(1.0, 1.0 - fadeMargin, proj.y);
    return mix(1.0, shadow, fadeX * fadeY);
}

void main()
{
    // Sample G-buffer
    vec4 albedoMetal = texture(gAlbedoMetallic, vUV);
    vec4 normalRough = texture(gNormalRoughness, vUV);
    vec4 emissiveAO  = texture(gEmissiveAO, vUV);
    float depth      = texture(gDepth, vUV).r;

    // Skip sky pixels (depth == 1.0)
    if (depth >= 1.0)
        discard;

    vec3  albedo    = albedoMetal.rgb;
    float metallic  = albedoMetal.a;
    vec3  N         = normalize(normalRough.rgb * 2.0 - 1.0);  // decode from [0,1]
    float roughness = normalRough.a;
    vec3  emissive  = emissiveAO.rgb;
    float ao        = emissiveAO.a;

    // SSAO (intensity scales how much occlusion affects ambient)
    if (uHasSSAO)
    {
        float ss = texture(uSSAOTex, vUV).r;
        ao *= mix(1.0, ss, clamp(uSSAOIntensity, 0.0, 1.0));
    }

    // Reconstruct world position
    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 V = normalize(uCamPos - worldPos);

    // PBR: base reflectivity
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Shadow for primary light
    float shadow = ShadowCalc(worldPos, N);

    vec3 Lo = vec3(0.0);

    for (int i = 0; i < uDirLightCount && i < MAX_DIR_LIGHTS; i++)
    {
        vec3 L = uDirLightDirs[i];
        vec3 H = normalize(V + L);
        float NdotL = max(dot(N, L), 0.0);
        float NDF = DistributionGGX(N, H, roughness);
        float G   = GeometrySmith(N, V, L, roughness);
        vec3  F   = FresnelSchlick(max(dot(H, V), 0.0), F0);
        vec3 numerator   = NDF * G * F;
        float denominator = 4.0 * max(dot(N, V), 0.0) * NdotL + 0.0001;
        vec3 specular    = numerator / denominator;
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
        vec3 radiance = uDirLightColors[i] * uDirLightIntensities[i];
        float lightShadow = (i == 0) ? shadow : 1.0;
        Lo += (kD * albedo / PI + specular) * radiance * NdotL * lightShadow;
    }

    if (uLocalLightCount > 0)
    {
        ivec2 tile = ivec2(
            min(uTilesX - 1, int(floor(vUV.x * float(uTilesX)))),
            min(uTilesY - 1, int(floor(vUV.y * float(uTilesY)))));
        int tid = tile.y * uTilesX + tile.x;
        int lc = int(texelFetch(uTileMeta, tile, 0).r * 255.0 + 0.001);
        for (int k = 0; k < lc && k < 32; k++)
        {
            int li = int(texelFetch(uTileLightIdx, ivec2(k, tid), 0).r * 255.0 + 0.001);
            if (li < 0 || li >= uLocalLightCount) continue;

            vec4 r0 = texelFetch(uLocalLightTex, ivec2(li, 0), 0);
            vec4 r1 = texelFetch(uLocalLightTex, ivec2(li, 1), 0);
            vec4 r2 = texelFetch(uLocalLightTex, ivec2(li, 2), 0);
            vec4 r3 = texelFetch(uLocalLightTex, ivec2(li, 3), 0);

            vec3 lightPos = r0.xyz;
            float range = max(r0.w, 0.001);
            vec3 radianceBase = r1.xyz * r1.w;
            vec3 spotDir = normalize(r2.xyz);
            float isSpot = r2.w;

            vec3 toLight = lightPos - worldPos;
            float dist = length(toLight);
            vec3 L = toLight / max(dist, 0.0001);
            float attenuation = 1.0 / (1.0 + pow(dist / range, 2.0));

            if (isSpot > 0.5)
            {
                float cosTheta = dot(-L, spotDir);
                float cosOuter = r3.y;
                float cosInner = r3.x;
                attenuation *= smoothstep(cosOuter, cosInner, cosTheta);
            }

            vec3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            if (NdotL <= 0.0) continue;

            float NDF = DistributionGGX(N, H, roughness);
            float G   = GeometrySmith(N, V, L, roughness);
            vec3  F   = FresnelSchlick(max(dot(H, V), 0.0), F0);
            vec3 numerator   = NDF * G * F;
            float denominator = 4.0 * max(dot(N, V), 0.0) * NdotL + 0.0001;
            vec3 specular    = numerator / denominator;
            vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);

            Lo += (kD * albedo / PI + specular) * radianceBase * attenuation * NdotL;
        }
    }

    if (uHasProbe != 0)
    {
        vec3 R = reflect(-V, N);
        float mip = roughness * max(uProbeMipCount - 1.0, 0.0);
        vec3 prefiltered = textureLod(uProbeSpec, R, mip).rgb;
        vec3 Fenv = FresnelSchlick(max(dot(N, V), 0.0), F0);
        Lo += prefiltered * Fenv * uProbeBlend * (1.0 - roughness) * 0.5;
    }

    // Ambient
    float ambShadow = mix(0.35, 1.0, shadow);
    vec3 ambient = vec3(uAmbient) * albedo * ao * ambShadow;

    vec3 color = ambient + Lo + emissive;

    // Reinhard tone mapping (compresses HDR from multiple lights into LDR range)
    color = color / (color + vec3(1.0));

    FragColor = vec4(color, 1.0);
}
";

    // =====================================================================
    // SSAO (Screen-Space Ambient Occlusion)
    // =====================================================================

    public const string SSAOVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string SSAOFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D gNormalRoughness;
uniform sampler2D gDepth;

uniform mat4  uProjection;
uniform mat4  uView;
uniform mat4  uInvViewProj;

uniform float uRadius;
uniform float uBias;
uniform int   uSampleCount;
uniform vec2  uNoiseScale;

out vec4 FragColor;

#define MAX_SAMPLES 32

float halton(int index, int base)
{
    float f = 1.0, r = 0.0;
    int i = index;
    while (i > 0)
    {
        f /= float(base);
        r += f * float(i % base);
        i /= base;
    }
    return r;
}

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

vec3 randomVec(vec2 uv)
{
    return normalize(vec3(
        hash(uv) * 2.0 - 1.0,
        hash(uv + vec2(1.0, 0.0)) * 2.0 - 1.0,
        0.0
    ));
}

vec3 cosineHemisphere(vec2 xi)
{
    float phi = 6.28318530718 * xi.x;
    float cosTheta = sqrt(max(0.0, 1.0 - xi.y));
    float sinTheta = sqrt(clamp(xi.y, 0.0, 1.0));
    return vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

void main()
{
    float depth = texture(gDepth, vUV).r;
    if (depth >= 1.0)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 tnm = texture(gNormalRoughness, vUV).rgb;
    vec2 nxy = tnm.xy * 2.0 - 1.0;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    vec3 N = normalize(vec3(nxy, nz));

    vec3 fragPos = (uView * vec4(worldPos, 1.0)).xyz;
    vec3 normal  = normalize((uView * vec4(N, 0.0)).xyz);

    vec3 rvec = randomVec(vUV * uNoiseScale);
    vec3 tangent   = normalize(rvec - normal * dot(rvec, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN       = mat3(tangent, bitangent, normal);

    int count = clamp(uSampleCount, 4, MAX_SAMPLES);
    float occlusion = 0.0;
    for (int i = 0; i < MAX_SAMPLES; i++)
    {
        if (i >= count) break;
        vec2 xi = vec2(halton(i + 1, 2), halton(i + 1, 3));
        vec3 hem = cosineHemisphere(xi);
        vec3 samplePos = fragPos + TBN * hem * uRadius;

        vec4 offset = uProjection * vec4(samplePos, 1.0);
        offset.xy /= offset.w;
        offset.xy = offset.xy * 0.5 + 0.5;

        float sampleDepth = texture(gDepth, offset.xy).r;
        vec3 sampleWorld = WorldPosFromDepth(sampleDepth, offset.xy);
        float sampleViewZ = (uView * vec4(sampleWorld, 1.0)).z;

        float rangeCheck = smoothstep(0.0, 1.0, uRadius / max(0.001, abs(fragPos.z - sampleViewZ)));
        occlusion += (sampleViewZ >= samplePos.z + uBias ? 1.0 : 0.0) * rangeCheck;
    }

    occlusion = 1.0 - (occlusion / float(count));
    FragColor = vec4(vec3(occlusion), 1.0);
}
";

    public const string SSAOBlurFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D uSSAOInput;
uniform sampler2D gDepth;
uniform vec2 uTexelSize;
uniform float uDepthSigma;

out vec4 FragColor;

void main()
{
    float dC = texture(gDepth, vUV).r;
    float sum = 0.0;
    float wsum = 0.0;
    for (int x = -2; x <= 2; x++)
    {
        for (int y = -2; y <= 2; y++)
        {
            vec2 o = vec2(float(x), float(y)) * uTexelSize;
            vec2 suv = vUV + o;
            float d = texture(gDepth, suv).r;
            float w = exp(-abs(d - dC) * uDepthSigma);
            sum += texture(uSSAOInput, suv).r * w;
            wsum += w;
        }
    }
    float result = wsum > 1e-5 ? (sum / wsum) : texture(uSSAOInput, vUV).r;
    FragColor = vec4(vec3(result), 1.0);
}
";

    // =====================================================================
    // SSR (Screen-Space Reflections)
    // =====================================================================

    public const string SSRVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string SSRFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D uLitScene;
uniform sampler2D gNormalRoughness;
uniform sampler2D gAlbedoMetallic;
uniform sampler2D gDepth;

uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uInvViewProj;

uniform vec3 uCamPos;
uniform vec2 uScreenSize;
uniform int   uMaxSteps;
uniform float uRoughnessCutoff;
uniform float uMaxRayLength;

out vec4 FragColor;

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

vec2 ToUv(vec4 clip)
{
    vec3 ndc = clip.xyz / clip.w;
    return ndc.xy * 0.5 + 0.5;
}

void main()
{
    vec4 normalRough = texture(gNormalRoughness, vUV);
    vec4 albedoMetal = texture(gAlbedoMetallic, vUV);
    float depth = texture(gDepth, vUV).r;
    vec3 litColor = texture(uLitScene, vUV).rgb;

    if (depth >= 1.0)
    {
        FragColor = vec4(litColor, 1.0);
        return;
    }

    float roughness = normalRough.a;
    if (roughness > uRoughnessCutoff)
    {
        FragColor = vec4(litColor, 1.0);
        return;
    }

    vec3 albedo = albedoMetal.rgb;
    float metallic = albedoMetal.a;
    vec3 tnm = normalRough.rgb;
    vec2 nxy = tnm.xy * 2.0 - 1.0;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    vec3 N = normalize(vec3(nxy, nz));

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 V = normalize(uCamPos - worldPos);
    vec3 R = reflect(-V, N);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    float NdotV = max(dot(N, V), 0.0);
    vec3 F = F0 + (1.0 - F0) * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);

    mat3 rotView = mat3(uView);
    vec3 rv = normalize(rotView * R);
    vec4 vw = uView * vec4(worldPos + R * 0.05, 1.0);
    vec3 vp = vw.xyz;
    float stepLen = uMaxRayLength / float(max(uMaxSteps, 1));

    vec3 reflColor = litColor;
    float hitMask = 0.0;

    for (int i = 0; i < 128; i++)
    {
        if (i >= uMaxSteps) break;
        vp += rv * stepLen;
        vec4 clip = uProjection * vec4(vp, 1.0);
        vec2 suv = ToUv(clip);
        if (suv.x < 0.001 || suv.x > 0.999 || suv.y < 0.001 || suv.y > 0.999)
            break;

        float sd = texture(gDepth, suv).r;
        vec3 sw = WorldPosFromDepth(sd, suv);
        vec3 sv = (uView * vec4(sw, 1.0)).xyz;

        if (vp.z > sv.z && vp.z < sv.z + 0.5)
        {
            reflColor = texture(uLitScene, suv).rgb;
            hitMask = 1.0;
            vec2 edgeFade = smoothstep(vec2(0.0), vec2(0.08), suv) * smoothstep(vec2(1.0), vec2(0.92), suv);
            hitMask *= edgeFade.x * edgeFade.y;
            hitMask *= (1.0 - roughness) * (0.2 + 0.8 * metallic);
            break;
        }
    }

    float blurW = clamp(roughness / max(uRoughnessCutoff, 0.01), 0.0, 1.0);
    if (blurW > 0.05 && hitMask > 0.01)
    {
        vec2 px = 1.0 / uScreenSize;
        vec3 acc = reflColor;
        acc += texture(uLitScene, vUV + vec2(px.x, 0.0) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV - vec2(px.x, 0.0) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV + vec2(0.0, px.y) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV - vec2(0.0, px.y) * blurW * 3.0).rgb;
        reflColor = acc * 0.2;
    }

    float fresnelW = hitMask * (F.x * 0.33 + F.y * 0.33 + F.z * 0.34);
    vec3 finalColor = mix(litColor, reflColor, clamp(fresnelW, 0.0, 1.0));
    FragColor = vec4(finalColor, 1.0);
}
";

    // =====================================================================
    // DEPTH ONLY (shadow map pass)
    // =====================================================================
    public const string DepthOnlyVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 3) in vec4 aBoneIds;
layout(location = 4) in vec4 aBoneWeights;

uniform mat4 uMVP;
uniform int  uHasBones;
uniform mat4 uBones[128];

void main()
{
    vec3 pos = aPosition;
    if (uHasBones == 1)
    {
        ivec4 ids = ivec4(aBoneIds);
        vec4  w   = aBoneWeights;
        mat4 skin = w.x * uBones[ids.x]
                  + w.y * uBones[ids.y]
                  + w.z * uBones[ids.z]
                  + w.w * uBones[ids.w];
        pos = (skin * vec4(aPosition, 1.0)).xyz;
    }
    gl_Position = uMVP * vec4(pos, 1.0);
}
";

    public const string DepthOnlyFrag = @"
#version 330 core
out vec4 FragColor;
void main()
{
    // Depth is written automatically; dummy color output for ES compatibility
    FragColor = vec4(0.0);
}
";

    // =====================================================================
    // SKY (fullscreen, gradient + equirectangular texture)
    // =====================================================================
    public const string SkyVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aUV;

out vec2 vUV;

void main()
{
    vUV = aUV;
    gl_Position = vec4(aPosition, 0.999, 1.0);
}
";

    public const string SkyFrag = @"
#version 330 core
in vec2 vUV;

uniform mat4  uInvVP;
uniform vec3  uTopColor;
uniform vec3  uBotColor;
uniform vec3  uSunDir;
uniform bool  uUseSun;
uniform sampler2D uSkyTex;
uniform bool  uHasSkyTex;
uniform float uSkyBlend;
uniform float uSkyYaw;

out vec4 FragColor;

#define PI 3.14159265359

void main()
{
    // Reconstruct world-space ray direction from screen UV
    vec2 ndc = vUV * 2.0 - 1.0;
    vec4 near4 = uInvVP * vec4(ndc, 0.0, 1.0);
    vec4 far4  = uInvVP * vec4(ndc, 1.0, 1.0);
    vec3 nearW = near4.xyz / near4.w;
    vec3 farW  = far4.xyz / far4.w;
    vec3 dir   = normalize(farW - nearW);

    // Apply sky yaw rotation around Y
    float s = sin(uSkyYaw);
    float c = cos(uSkyYaw);
    dir = vec3(c * dir.x + s * dir.z, dir.y, -s * dir.x + c * dir.z);

    // Gradient based on world up
    float t = clamp(0.5 + 0.5 * dir.y, 0.0, 1.0);

    // Optional sun glow
    if (uUseSun)
    {
        float sunGlow = pow(max(dot(dir, uSunDir), 0.0), 64.0);
        t = clamp(t + sunGlow * 0.08, 0.0, 1.0);
    }

    vec3 gradColor = mix(uBotColor, uTopColor, t);

    // Equirectangular texture sampling
    if (uHasSkyTex && uSkyBlend > 0.001)
    {
        float u = 0.5 + atan(dir.x, -dir.z) / (2.0 * PI);
        float v = 0.5 - asin(clamp(dir.y, -1.0, 1.0)) / PI;

        // Wrap longitude explicitly and clamp latitude to avoid pole bleed.
        vec2 uv = vec2(fract(u), clamp(v, 0.0, 1.0));
        vec4 texSamp = texture(uSkyTex, uv);

        // Feather the 0/1 longitude seam by blending to averaged edge texels.
        // This reduces visible vertical seams in non-perfectly seamless panoramas.
        float seamDist = min(uv.x, 1.0 - uv.x);
        float seamWidth = max(1.5 / float(textureSize(uSkyTex, 0).x), 0.002);
        if (seamDist < seamWidth)
        {
            vec4 edgeAvg = 0.5 * (texture(uSkyTex, vec2(0.0, uv.y)) + texture(uSkyTex, vec2(1.0, uv.y)));
            float keepOriginal = smoothstep(0.0, seamWidth, seamDist);
            texSamp = mix(edgeAvg, texSamp, keepOriginal);
        }

        gradColor = mix(gradColor, texSamp.rgb, uSkyBlend * texSamp.a);
    }

    FragColor = vec4(gradColor, 1.0);
}
";

    // =====================================================================
    // GRID (infinite ground plane, ray-cast per pixel)
    // =====================================================================
    public const string GridVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aUV;

out vec2 vUV;

void main()
{
    vUV = aUV;
    gl_Position = vec4(aPosition, 0.999, 1.0);
}
";

    public const string GridFrag = @"
#version 330 core
in vec2 vUV;

uniform mat4  uInvVP;
uniform mat4  uVP;
uniform vec3  uCamPos;
uniform float uGridStep;
uniform int   uMajorEvery;

out vec4 FragColor;

void main()
{
    vec2 ndc = vUV * 2.0 - 1.0;
    vec4 near4 = uInvVP * vec4(ndc, 0.0, 1.0);
    vec4 far4  = uInvVP * vec4(ndc, 1.0, 1.0);
    vec3 nearW = near4.xyz / near4.w;
    vec3 farW  = far4.xyz / far4.w;
    vec3 dir   = normalize(farW - nearW);

    // Intersect Y=0 plane
    if (abs(dir.y) < 1e-6)
        discard;

    float t = -nearW.y / dir.y;
    if (t <= 0.0)
        discard;

    vec3 hit = nearW + dir * t;

    // Compute correct depth
    vec4 clip = uVP * vec4(hit, 1.0);
    float ndcZ = clip.z / clip.w;
    gl_FragDepth = ndcZ * 0.5 + 0.5;

    // Grid lines
    float gx = hit.x / uGridStep;
    float gz = hit.z / uGridStep;
    float wx = abs(gx - round(gx));
    float wz = abs(gz - round(gz));
    float distToLine = min(wx, wz);

    float lineW = 0.015 + 0.0025 * min(40.0, t);
    float alpha = clamp((lineW - distToLine) / lineW, 0.0, 1.0);

    // Distance fade
    float d = distance(uCamPos, hit);
    float fade = 1.0 / (1.0 + 0.12 * d);
    alpha *= fade;

    if (alpha <= 0.0)
        discard;

    // Color: axis, major, minor
    int ix = int(round(gx));
    int iz = int(round(gz));
    bool onAxis  = (ix == 0) || (iz == 0);
    bool onMajor = (ix % uMajorEvery == 0) || (iz % uMajorEvery == 0);

    vec3 col = onAxis ? vec3(0.375) : (onMajor ? vec3(0.282) : vec3(0.188));

    FragColor = vec4(col, alpha);
}
";

    // =====================================================================
    // WIREFRAME (simple colored lines)
    // =====================================================================
    public const string WireframeVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;

uniform mat4 uMVP;

void main()
{
    gl_Position = uMVP * vec4(aPosition, 1.0);
}
";

    public const string WireframeFrag = @"
#version 330 core
uniform vec4 uColor;

out vec4 FragColor;

void main()
{
    FragColor = uColor;
}
";

    // ════════════════ TERRAIN SPLATMAP SHADER ════════════════

    public const string TerrainVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uNormalMatrix;
uniform mat4 uShadowVP;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(aNormal, 0.0)).xyz);
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    gl_Position = uProj * uView * worldPos;
}
";

    public const string TerrainFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;

// Splatmaps (RGBA float textures, channels = layer weights)
uniform sampler2D uSplatmap0;    // layers 0-3
uniform sampler2D uSplatmap1;    // layers 4-7
uniform int       uLayerCount;   // how many layers are active (0-8)

// Layer albedo textures (up to 8)
uniform sampler2D uLayer0; uniform float uTiling0;
uniform sampler2D uLayer1; uniform float uTiling1;
uniform sampler2D uLayer2; uniform float uTiling2;
uniform sampler2D uLayer3; uniform float uTiling3;
uniform sampler2D uLayer4; uniform float uTiling4;
uniform sampler2D uLayer5; uniform float uTiling5;
uniform sampler2D uLayer6; uniform float uTiling6;
uniform sampler2D uLayer7; uniform float uTiling7;

// Per-layer PBR scalars (blended by splat weights)
uniform float uRough0; uniform float uRough1; uniform float uRough2; uniform float uRough3;
uniform float uRough4; uniform float uRough5; uniform float uRough6; uniform float uRough7;
uniform float uMetal0; uniform float uMetal1; uniform float uMetal2; uniform float uMetal3;
uniform float uMetal4; uniform float uMetal5; uniform float uMetal6; uniform float uMetal7;

// Normal maps: layers 0–4 only (texture unit budget); uHasNormal5–7 reserved / unused samplers
uniform sampler2D uNormalLayer0;
uniform sampler2D uNormalLayer1;
uniform sampler2D uNormalLayer2;
uniform sampler2D uNormalLayer3;
uniform sampler2D uNormalLayer4;
uniform int uHasNormal0; uniform int uHasNormal1; uniform int uHasNormal2;
uniform int uHasNormal3; uniform int uHasNormal4;
uniform int uHasNormal5; uniform int uHasNormal6; uniform int uHasNormal7;

uniform float uParallaxStrength;

// Shadow
uniform sampler2D uShadowMap;
uniform bool      uHasShadow;
uniform float     uShadowBias;
uniform vec3      uSunDir;

// Lighting
uniform vec3  uLightDir;
uniform vec3  uLightPos;
uniform float uLightRange;
uniform bool  uLightIsPoint;
uniform float uDiffuseK;
uniform float uAmbient;

// Camera
uniform vec3  uCamPos;

// Fallback (no layers defined) — no extra albedo sampler (keeps fragment stage ≤16 texture units on GLES/ANGLE)
uniform vec4  uBaseColor;

out vec4 FragColor;

float ShadowCalc(vec4 sc, vec3 N)
{
    if (!uHasShadow) return 1.0;
    vec3 proj = sc.xyz / sc.w;
    proj = proj * 0.5 + 0.5;
    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    float cosTheta = max(dot(N, -uSunDir), 0.0);
    float bias = uShadowBias + uShadowBias * 3.0 * (1.0 - cosTheta);
    float currentDepth = proj.z - bias;

    float pcfDepth = texture(uShadowMap, proj.xy).r;
    float shadow = (currentDepth > pcfDepth) ? 0.10 : 1.0;

    float fadeMargin = 0.08;
    float fadeX = smoothstep(0.0, fadeMargin, proj.x) * smoothstep(1.0, 1.0 - fadeMargin, proj.x);
    float fadeY = smoothstep(0.0, fadeMargin, proj.y) * smoothstep(1.0, 1.0 - fadeMargin, proj.y);
    return mix(1.0, shadow, fadeX * fadeY);
}

float layerW(int i, vec4 s0, vec4 s1)
{
    if (i == 0) return s0.r;
    if (i == 1) return s0.g;
    if (i == 2) return s0.b;
    if (i == 3) return s0.a;
    if (i == 4) return s1.r;
    if (i == 5) return s1.g;
    if (i == 6) return s1.b;
    return s1.a;
}

void main()
{
    vec3 Ngeom = normalize(vWorldNormal);
    vec2 uvDetail = vUV;

    vec4 albedo = vec4(1.0);
    float rough = 0.8;
    float metal = 0.0;
    vec3 N = Ngeom;

    if (uLayerCount > 0)
    {
        vec4 s0 = texture(uSplatmap0, vUV);
        vec4 s1 = (uLayerCount > 4) ? texture(uSplatmap1, vUV) : vec4(0.0);

        // Tangent frame for normal maps + parallax
        vec3 up = abs(Ngeom.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        vec3 T = normalize(cross(up, Ngeom));
        vec3 B = normalize(cross(Ngeom, T));
        mat3 TBN = mat3(T, B, Ngeom);

        vec3 Vw = normalize(uCamPos - vWorldPos);
        vec3 Vts = vec3(dot(Vw, T), dot(Vw, B), dot(Vw, Ngeom));

        // Parallax from normal-map alpha (layers 0–4), weighted by splat
        float hAcc = 0.0;
        float hW = 0.0;
        for (int i = 0; i < 5; i++)
        {
            if (i >= uLayerCount) break;
            int hasN = (i == 0) ? uHasNormal0 : (i == 1) ? uHasNormal1 : (i == 2) ? uHasNormal2 : (i == 3) ? uHasNormal3 : uHasNormal4;
            if (hasN == 0) continue;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-5) continue;
            vec2 uvi = vUV * ((i == 0) ? uTiling0 : (i == 1) ? uTiling1 : (i == 2) ? uTiling2 : (i == 3) ? uTiling3 : uTiling4);
            vec4 nsmpl = (i == 0) ? texture(uNormalLayer0, uvi)
                : (i == 1) ? texture(uNormalLayer1, uvi)
                : (i == 2) ? texture(uNormalLayer2, uvi)
                : (i == 3) ? texture(uNormalLayer3, uvi)
                : texture(uNormalLayer4, uvi);
            hAcc += nsmpl.a * wi;
            hW += wi;
        }
        if (hW > 1e-4 && uParallaxStrength > 0.0)
        {
            float h = hAcc / hW;
            vec2 pdir = Vts.xy / max(abs(Vts.z), 0.15);
            uvDetail = vUV - pdir * (h * uParallaxStrength);
        }

        // Blended albedo (detail UV)
        albedo = vec4(0.0);
        albedo += texture(uLayer0, uvDetail * uTiling0) * s0.r;
        if (uLayerCount > 1) albedo += texture(uLayer1, uvDetail * uTiling1) * s0.g;
        if (uLayerCount > 2) albedo += texture(uLayer2, uvDetail * uTiling2) * s0.b;
        if (uLayerCount > 3) albedo += texture(uLayer3, uvDetail * uTiling3) * s0.a;
        if (uLayerCount > 4)
        {
            albedo += texture(uLayer4, uvDetail * uTiling4) * s1.r;
            if (uLayerCount > 5) albedo += texture(uLayer5, uvDetail * uTiling5) * s1.g;
            if (uLayerCount > 6) albedo += texture(uLayer6, uvDetail * uTiling6) * s1.b;
            if (uLayerCount > 7) albedo += texture(uLayer7, uvDetail * uTiling7) * s1.a;
        }
        albedo.a = 1.0;

        // Roughness / metallic
        float rw = 0.0;
        rough = 0.0;
        metal = 0.0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-6) continue;
            float r = (i == 0) ? uRough0 : (i == 1) ? uRough1 : (i == 2) ? uRough2 : (i == 3) ? uRough3
                : (i == 4) ? uRough4 : (i == 5) ? uRough5 : (i == 6) ? uRough6 : uRough7;
            float m = (i == 0) ? uMetal0 : (i == 1) ? uMetal1 : (i == 2) ? uMetal2 : (i == 3) ? uMetal3
                : (i == 4) ? uMetal4 : (i == 5) ? uMetal5 : (i == 6) ? uMetal6 : uMetal7;
            rough += r * wi;
            metal += m * wi;
            rw += wi;
        }
        if (rw > 1e-5) { rough /= rw; metal /= rw; }

        // Blended tangent-space normal (layers 0–4 have maps; 5–7 default to flat)
        vec3 tSum = vec3(0.0);
        float nW = 0.0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-6) continue;
            vec3 tn = vec3(0.0, 0.0, 1.0);
            if (i < 5)
            {
                int hasN = (i == 0) ? uHasNormal0 : (i == 1) ? uHasNormal1 : (i == 2) ? uHasNormal2 : (i == 3) ? uHasNormal3 : uHasNormal4;
                if (hasN != 0)
                {
                    vec2 uvi = uvDetail * ((i == 0) ? uTiling0 : (i == 1) ? uTiling1 : (i == 2) ? uTiling2 : (i == 3) ? uTiling3 : uTiling4);
                    vec3 samp = (i == 0) ? texture(uNormalLayer0, uvi).rgb
                        : (i == 1) ? texture(uNormalLayer1, uvi).rgb
                        : (i == 2) ? texture(uNormalLayer2, uvi).rgb
                        : (i == 3) ? texture(uNormalLayer3, uvi).rgb
                        : texture(uNormalLayer4, uvi).rgb;
                    tn = samp * 2.0 - 1.0;
                }
            }
            tSum += tn * wi;
            nW += wi;
        }
        if (nW > 1e-5)
        {
            vec3 tN = normalize(tSum / nW);
            N = normalize(TBN * tN);
        }
        else
            N = Ngeom;
    }
    else
    {
        albedo = uBaseColor;
    }

    vec3 L;
    float atten = 1.0;
    if (uLightIsPoint)
    {
        vec3 toLight = uLightPos - vWorldPos;
        float dist = length(toLight);
        L = toLight / max(dist, 0.0001);
        if (uLightRange > 0.0)
        {
            float t = dist / uLightRange;
            atten = 1.0 / (1.0 + t * t);
        }
    }
    else
    {
        L = -uLightDir;
    }

    float NdotL = max(dot(N, L), 0.0);
    float diffuse = min(NdotL * atten, 1.0);

    float shadow = ShadowCalc(vShadowCoord, N);

    float specular = 0.0;
    if (uDiffuseK > 0.0 && diffuse > 0.0)
    {
        float shininess = mix(4.0, 96.0, 1.0 - rough);
        vec3 V = normalize(uCamPos - vWorldPos);
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        vec3 F0 = mix(vec3(0.04), albedo.rgb, metal);
        float specAmt = pow(NdotH, shininess) * (0.1 + 0.35 * (1.0 - rough)) * (1.0 - metal * 0.85);
        specular = specAmt * diffuse * (F0.r + F0.g + F0.b) / 3.0;
    }

    float ambShadow = mix(0.35, 1.0, shadow);
    float shade = clamp(uAmbient * ambShadow + uDiffuseK * diffuse * shadow, 0.0, 1.0);
    vec3 color = clamp(albedo.rgb * shade + vec3(specular * shadow), 0.0, 1.0);

    FragColor = vec4(color, 1.0);
}
";

    // ════════════════ BLIT (fullscreen texture copy / upscale) ════════════════

    public const string BlitVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string BlitFrag = @"
#version 330 core
in vec2 vUV;
uniform sampler2D uTex;
out vec4 FragColor;
void main()
{
    FragColor = texture(uTex, vUV);
}
";

    /// <summary>
    /// Copies a depth texture into the current framebuffer's depth buffer (writes gl_FragDepth only).
    /// Used instead of glBlitFramebuffer for gbuffer→scene depth on drivers where blit fails or mismatches formats (e.g. ANGLE).
    /// </summary>
    public const string DepthCopyFrag = @"
#version 330 core
in vec2 vUV;
uniform sampler2D uDepth;
out vec4 FragColor;
void main()
{
    // texelFetch avoids filtering / edge issues; clamp to valid range for packed D24S8 textures.
    ivec2 dims = textureSize(uDepth, 0);
    ivec2 tc = clamp(ivec2(gl_FragCoord.xy), ivec2(0), dims - ivec2(1));
    float d = texelFetch(uDepth, tc, 0).r;
    gl_FragDepth = d;
    FragColor = vec4(0.0);
}
";

    // ════════════════ PARTICLE BILLBOARD SHADER ════════════════

    public const string ParticleVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;    // billboard quad corner (-0.5..0.5)

// Per-instance data (passed as uniforms per batch)
uniform vec4 uParticlePos[128];   // xyz = world position, w = size / streak width
uniform vec4 uParticleCol[128];   // rgba color
uniform int uAlignVelocity;
uniform float uStretchLength;
uniform vec3 uFallDir;

uniform mat4 uView;
uniform mat4 uProj;

out vec4 vColor;
out vec2 vUV;
flat out int vInstanceID;

void main()
{
    int id = gl_InstanceID;
    vInstanceID = id;

    vec3 worldPos = uParticlePos[id].xyz;
    float size = uParticlePos[id].w;
    vColor = uParticleCol[id];
    vUV = aPosition + 0.5;

    vec3 camRight = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 camUp    = vec3(uView[0][1], uView[1][1], uView[2][1]);
    vec3 corner;
    if (uAlignVelocity != 0)
    {
        vec3 fall = uFallDir;
        float fallLen = length(fall);
        if (fallLen > 1e-5)
            fall /= fallLen;
        else
            fall = vec3(0.0, -1.0, 0.0);
        vec3 side = cross(fall, camRight);
        if (dot(side, side) < 1e-6)
            side = camRight;
        else
            side = normalize(side);
        float len = max(0.08, uStretchLength);
        corner = worldPos + (aPosition.x * side * size + aPosition.y * fall * len);
    }
    else
    {
        corner = worldPos + (aPosition.x * camRight + aPosition.y * camUp) * size;
    }
    gl_Position = uProj * uView * vec4(corner, 1.0);
}
";

    public const string ParticleFrag = @"
#version 330 core
in vec4 vColor;
in vec2 vUV;
uniform int uAlignVelocity;

out vec4 FragColor;

void main()
{
    float alpha;
    if (uAlignVelocity != 0)
    {
        float ax = abs(vUV.x - 0.5) * 2.0;
        float ay = abs(vUV.y - 0.5) * 2.0;
        alpha = (1.0 - smoothstep(0.25, 1.0, ax)) * (1.0 - smoothstep(0.82, 1.0, ay));
    }
    else
    {
        float dist = length(vUV - vec2(0.5));
        alpha = 1.0 - smoothstep(0.3, 0.5, dist);
    }

    FragColor = vec4(vColor.rgb, vColor.a * alpha);
    if (FragColor.a < 0.01) discard;
}
";

    // ════════════════ TAA (temporal anti-aliasing) ════════════════

    public const string TaaResolveFrag = @"
#version 330 core
in vec2 vUV;
uniform sampler2D uCurr;
uniform sampler2D uHistory;
uniform sampler2D uDepth;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec2 uTexel;
uniform float uAlpha;
uniform float uSharpen;
out vec4 FragColor;

vec3 RGBToYCoCg(vec3 c)
{
    float Y = dot(c, vec3(0.25, 0.5, 0.25));
    float Co = dot(c, vec3(0.5, 0.0, -0.5));
    float Cg = dot(c, vec3(-0.25, 0.5, -0.25));
    return vec3(Y, Co, Cg);
}

vec3 YCoCgToRGB(vec3 ycocg)
{
    float t = ycocg.x - ycocg.z * 0.5;
    float g = ycocg.x + ycocg.z * 0.5;
    float b = t - ycocg.y * 0.5;
    float r = t + ycocg.y * 0.5;
    return vec3(r, g, b);
}

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

void main()
{
    vec3 curr = texture(uCurr, vUV).rgb;
    float d = texture(uDepth, vUV).r;
    if (d >= 1.0)
    {
        FragColor = vec4(curr, 1.0);
        return;
    }

    vec3 wpos = WorldPosFromDepth(d, vUV);
    vec4 pclip = uPrevViewProj * vec4(wpos, 1.0);
    vec2 huv = pclip.xy / pclip.w * 0.5 + 0.5;
    vec3 hist = texture(uHistory, huv).rgb;

    vec3 minC = curr, maxC = curr;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            vec3 s = texture(uCurr, vUV + vec2(float(x), float(y)) * uTexel).rgb;
            minC = min(minC, s);
            maxC = max(maxC, s);
        }
    }

    vec3 yHist = RGBToYCoCg(hist);
    vec3 yMin = RGBToYCoCg(minC);
    vec3 yMax = RGBToYCoCg(maxC);
    yHist = clamp(yHist, yMin, yMax);
    hist = YCoCgToRGB(yHist);

    vec3 resolved = mix(hist, curr, uAlpha);
    vec3 blur = (texture(uCurr, vUV + vec2(uTexel.x, 0.0)).rgb
               + texture(uCurr, vUV - vec2(uTexel.x, 0.0)).rgb
               + texture(uCurr, vUV + vec2(0.0, uTexel.y)).rgb
               + texture(uCurr, vUV - vec2(0.0, uTexel.y)).rgb) * 0.25;
    resolved = resolved + (resolved - blur) * uSharpen;
    FragColor = vec4(max(resolved, vec3(0.0)), 1.0);
}
";

    // ════════════════ POST-PROCESSING SHADER ════════════════

    public const string PostProcessVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string PostProcessFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D uScene;
uniform vec2 uTexelSize;     // 1.0 / resolution

// Bloom
uniform bool  uBloomEnabled;
uniform float uBloomThreshold;
uniform float uBloomIntensity;

// Fog
uniform bool  uFogEnabled;
uniform vec3  uFogColor;
uniform float uFogDensity;
uniform float uFogStart;
uniform float uFogEnd;

// Color Grading
uniform bool  uColorGradingEnabled;
uniform float uBrightness;
uniform float uContrast;
uniform float uSaturation;
uniform float uExposure;
uniform int   uToneMap;     // 0=None, 1=Reinhard, 2=ACES

// Vignette
uniform bool  uVignetteEnabled;
uniform float uVignetteIntensity;
uniform float uVignetteSmoothness;

// FXAA
uniform bool  uFXAAEnabled;

// Underwater
uniform bool  uUnderwaterEnabled;
uniform vec3  uUnderwaterTint;
uniform float uUnderwaterFogDensity;
uniform float uUnderwaterCausticStr;
uniform float uUnderwaterDistortion;
uniform float uUnderwaterTime;
uniform float uUnderwaterDepth;    // how far below the surface (0 = at surface)

out vec4 FragColor;

vec3 ACESFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x*(a*x+b))/(x*(c*x+d)+e), 0.0, 1.0);
}

vec3 ReinhardTonemap(vec3 color)
{
    return color / (color + vec3(1.0));
}

void main()
{
    vec3 color = texture(uScene, vUV).rgb;

    // ── FXAA (simplified) ──
    if (uFXAAEnabled)
    {
        vec3 rgbNW = texture(uScene, vUV + vec2(-1.0, -1.0) * uTexelSize).rgb;
        vec3 rgbNE = texture(uScene, vUV + vec2( 1.0, -1.0) * uTexelSize).rgb;
        vec3 rgbSW = texture(uScene, vUV + vec2(-1.0,  1.0) * uTexelSize).rgb;
        vec3 rgbSE = texture(uScene, vUV + vec2( 1.0,  1.0) * uTexelSize).rgb;

        vec3 luma = vec3(0.299, 0.587, 0.114);
        float lumaNW = dot(rgbNW, luma);
        float lumaNE = dot(rgbNE, luma);
        float lumaSW = dot(rgbSW, luma);
        float lumaSE = dot(rgbSE, luma);
        float lumaM  = dot(color, luma);

        float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
        float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
        float lumaRange = lumaMax - lumaMin;

        if (lumaRange > max(0.0312, lumaMax * 0.0625))
        {
            vec2 dir;
            dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
            dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

            float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * 0.25, 1.0/128.0);
            float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
            dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * uTexelSize;

            vec3 rgbA = 0.5 * (
                texture(uScene, vUV + dir * (1.0/3.0 - 0.5)).rgb +
                texture(uScene, vUV + dir * (2.0/3.0 - 0.5)).rgb);
            vec3 rgbB = rgbA * 0.5 + 0.25 * (
                texture(uScene, vUV + dir * -0.5).rgb +
                texture(uScene, vUV + dir *  0.5).rgb);

            float lumaB = dot(rgbB, luma);
            color = (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
        }
    }

    // ── Bloom (simplified bright pass + blur approximation) ──
    if (uBloomEnabled)
    {
        vec3 bright = max(color - vec3(uBloomThreshold), vec3(0.0));
        // Simple 5-tap blur for bloom approximation
        vec3 bloom = bright;
        bloom += max(texture(uScene, vUV + vec2( 2.0, 0.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(-2.0, 0.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(0.0,  2.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(0.0, -2.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom *= 0.2;
        color += bloom * uBloomIntensity;
    }

    // ── Fog ──
    if (uFogEnabled)
    {
        // Distance-based fog using depth approximation from luminance (simplified)
        float depth = dot(color, vec3(0.299, 0.587, 0.114)); // rough depth proxy
        float fogFactor = 1.0 - exp(-uFogDensity * uFogDensity * depth * depth * 100.0);
        fogFactor = clamp(fogFactor, 0.0, 1.0);
        color = mix(color, uFogColor, fogFactor);
    }

    // ── Color Grading ──
    if (uColorGradingEnabled)
    {
        // Exposure
        color *= uExposure;

        // Brightness
        color += vec3(uBrightness);

        // Contrast
        color = ((color - 0.5) * uContrast) + 0.5;

        // Saturation
        float gray = dot(color, vec3(0.299, 0.587, 0.114));
        color = mix(vec3(gray), color, uSaturation);

        // Tone mapping
        if (uToneMap == 1) color = ReinhardTonemap(color);
        else if (uToneMap == 2) color = ACESFilm(color);
    }

    // ── Vignette ──
    if (uVignetteEnabled)
    {
        float dist = distance(vUV, vec2(0.5));
        float vig = smoothstep(0.5 - uVignetteSmoothness, 0.5, dist);
        color *= 1.0 - vig * uVignetteIntensity;
    }

    // ── Underwater effect ──
    if (uUnderwaterEnabled)
    {
        // Screen-space distortion (wavy wobble)
        float wobbleX = sin(vUV.y * 25.0 + uUnderwaterTime * 2.3) * uUnderwaterDistortion;
        float wobbleY = cos(vUV.x * 20.0 + uUnderwaterTime * 1.7) * uUnderwaterDistortion;
        vec2 distortedUV = vUV + vec2(wobbleX, wobbleY);
        distortedUV = clamp(distortedUV, 0.001, 0.999);
        color = texture(uScene, distortedUV).rgb;

        // Re-apply tone mapping on distorted sample if color grading is active
        if (uColorGradingEnabled)
        {
            color *= uExposure;
            color += vec3(uBrightness);
            color = ((color - 0.5) * uContrast) + 0.5;
            float grayUW = dot(color, vec3(0.299, 0.587, 0.114));
            color = mix(vec3(grayUW), color, uSaturation);
            if (uToneMap == 1) color = ReinhardTonemap(color);
            else if (uToneMap == 2) color = ACESFilm(color);
        }

        // Underwater fog (depth-based tinting — stronger further below surface)
        float depthFactor = clamp(uUnderwaterDepth * uUnderwaterFogDensity, 0.0, 0.85);
        // Also apply screen-space depth fog (objects far from camera appear foggier)
        float screenDepth = dot(color, vec3(0.299, 0.587, 0.114));
        float screenFog = 1.0 - exp(-uUnderwaterFogDensity * 8.0 * screenDepth);
        float totalFog = clamp(depthFactor + screenFog * 0.5, 0.0, 0.9);
        color = mix(color, uUnderwaterTint, totalFog);

        // Caustic light patterns (animated)
        vec2 cUV = vUV * 8.0;
        float c1 = sin(cUV.x * 3.0 + uUnderwaterTime * 1.1) * cos(cUV.y * 2.7 + uUnderwaterTime * 0.9);
        float c2 = sin(cUV.x * 2.1 - uUnderwaterTime * 0.7) * cos(cUV.y * 3.3 + uUnderwaterTime * 1.3);
        float caustic = (c1 + c2) * 0.5 + 0.5;
        caustic = pow(caustic, 3.0);
        // Caustics are brighter near the surface
        float causticFade = clamp(1.0 - uUnderwaterDepth * 0.15, 0.1, 1.0);
        color += vec3(caustic) * uUnderwaterCausticStr * causticFade;

        // Color absorption (reds fade first, then greens, leaving blues)
        float absorption = clamp(uUnderwaterDepth * 0.08, 0.0, 0.7);
        color.r *= 1.0 - absorption;
        color.g *= 1.0 - absorption * 0.5;

        // Underwater vignette (stronger than normal)
        float uwDist = distance(vUV, vec2(0.5));
        float uwVig = smoothstep(0.3, 0.7, uwDist);
        color *= 1.0 - uwVig * 0.4;
    }

    color = clamp(color, 0.0, 1.0);
    FragColor = vec4(color, 1.0);
}
";

    // ════════════════ WATER SHADER ════════════════

    public const string WaterVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uNormalMatrix;

// Wave parameters
uniform float uTime;
uniform float uWaveAmp1;
uniform float uWaveFreq1;
uniform vec2  uWaveDir1;
uniform float uWaveSteep1;
uniform float uWaveAmp2;
uniform float uWaveFreq2;
uniform vec2  uWaveDir2;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);

    // Gerstner wave 1
    vec2 d1 = normalize(uWaveDir1);
    float dot1 = d1.x * worldPos.x + d1.y * worldPos.z;
    float phase1 = dot1 * uWaveFreq1 + uTime;
    worldPos.y += uWaveAmp1 * sin(phase1);
    worldPos.x += uWaveSteep1 * uWaveAmp1 * d1.x * cos(phase1);
    worldPos.z += uWaveSteep1 * uWaveAmp1 * d1.y * cos(phase1);

    // Gerstner wave 2
    vec2 d2 = normalize(uWaveDir2);
    float dot2 = d2.x * worldPos.x + d2.y * worldPos.z;
    float phase2 = dot2 * uWaveFreq2 + uTime * 0.7;
    worldPos.y += uWaveAmp2 * sin(phase2);

    // Compute normal from wave derivatives
    float nx = -(uWaveAmp1 * uWaveFreq1 * d1.x * cos(phase1) + uWaveAmp2 * uWaveFreq2 * d2.x * cos(phase2));
    float nz = -(uWaveAmp1 * uWaveFreq1 * d1.y * cos(phase1) + uWaveAmp2 * uWaveFreq2 * d2.y * cos(phase2));
    vec3 waveNormal = normalize(vec3(nx, 1.0, nz));

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(waveNormal, 0.0)).xyz);
    vUV = aUV;

    gl_Position = uProj * uView * worldPos;
}
";

    public const string WaterFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;

// Water colors
uniform vec4  uShallowColor;
uniform vec4  uDeepColor;
uniform float uFresnelPower;
uniform float uReflectivity;
uniform float uTransparency;

// Foam
uniform bool  uFoamEnabled;
uniform float uFoamThreshold;
uniform float uFoamIntensity;
uniform vec3  uFoamColor;

// Lighting
uniform vec3  uLightDir;
uniform float uAmbient;
uniform float uDiffuseK;
uniform vec3  uCamPos;

// Sky reflection color (from skybox)
uniform vec3 uSkyColor;

out vec4 FragColor;

void main()
{
    vec3 N = normalize(vWorldNormal);
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 L = -uLightDir;

    // Fresnel
    float fresnel = pow(1.0 - max(dot(N, V), 0.0), uFresnelPower);
    fresnel = clamp(fresnel, 0.0, 1.0);

    // Base water color (shallow/deep blend using fresnel)
    vec4 waterColor = mix(uShallowColor, uDeepColor, fresnel);

    // Reflection (sky color as simple reflection)
    vec3 R = reflect(-V, N);
    float upness = max(R.y, 0.0);
    vec3 reflColor = mix(uSkyColor * 0.5, uSkyColor, upness) * uReflectivity;

    // Specular highlight (sun reflection on water)
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 128.0) * 0.8;

    // Lighting — water is primarily reflective; diffuse contribution is soft
    float NdotL = max(dot(N, L), 0.0);
    float diffuse = min(uDiffuseK, 1.0) * NdotL * 0.3;
    float lighting = uAmbient + diffuse;

    vec3 color = waterColor.rgb * lighting + reflColor * fresnel + vec3(spec) * min(uDiffuseK, 1.0);

    // Foam (based on wave height, simplified)
    if (uFoamEnabled)
    {
        float foam = smoothstep(uFoamThreshold, uFoamThreshold + 0.3, N.y);
        float foamPattern = fract(sin(dot(vWorldPos.xz * 3.0, vec2(12.9898, 78.233))) * 43758.5453);
        foam *= foamPattern * uFoamIntensity;
        color = mix(color, uFoamColor, foam);
    }

    // Reinhard tone mapping to prevent whiteout
    color = color / (color + vec3(1.0));

    float alpha = mix(uTransparency, 1.0, fresnel);
    FragColor = vec4(clamp(color, 0.0, 1.0), alpha);
}
";

    // =====================================================================
    // VOLUMETRIC FOG (ray-marched fullscreen pass)
    // =====================================================================

    public const string VolumetricFogVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string VolumetricFogFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D uSceneColor;
uniform sampler2D gDepth;

uniform mat4  uInvViewProj;
uniform vec3  uCamPos;
uniform vec3  uLightDir;       // direction FROM light (negated for L)
uniform vec3  uLightColor;

// Volumetric fog parameters
uniform float uFogDensity;
uniform float uFogAnisotropy;
uniform float uFogScattering;
uniform float uFogHeightFalloff;
uniform float uFogBaseHeight;
uniform float uFogNoiseScale;
uniform float uFogNoiseSpeed;
uniform float uFogMaxDistance;
uniform vec3  uFogColor;
uniform int   uFogSteps;
uniform float uTime;

// Shadow map for light occlusion during ray march
uniform sampler2D uShadowMap;
uniform mat4      uShadowVP;
uniform bool      uHasShadow;

out vec4 FragColor;

// Simple 3D hash noise
float hash3D(vec3 p)
{
    p = fract(p * vec3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float noise3D(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = hash3D(i);
    float b = hash3D(i + vec3(1, 0, 0));
    float c = hash3D(i + vec3(0, 1, 0));
    float d = hash3D(i + vec3(1, 1, 0));
    float e = hash3D(i + vec3(0, 0, 1));
    float f1 = hash3D(i + vec3(1, 0, 1));
    float g = hash3D(i + vec3(0, 1, 1));
    float h = hash3D(i + vec3(1, 1, 1));

    return mix(mix(mix(a, b, f.x), mix(c, d, f.x), f.y),
               mix(mix(e, f1, f.x), mix(g, h, f.x), f.y), f.z);
}

// Henyey-Greenstein phase function
float phaseHG(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * 3.14159265 * pow(denom, 1.5));
}

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

float ShadowCheck(vec3 worldPos)
{
    if (!uHasShadow) return 1.0;
    vec4 sc = uShadowVP * vec4(worldPos, 1.0);
    vec3 proj = sc.xyz / sc.w * 0.5 + 0.5;
    if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0 || proj.z > 1.0)
        return 1.0;
    float shadowDepth = texture(uShadowMap, proj.xy).r;
    return (proj.z - 0.005 > shadowDepth) ? 0.0 : 1.0;
}

void main()
{
    vec3 sceneColor = texture(uSceneColor, vUV).rgb;
    float depth = texture(gDepth, vUV).r;

    // No volumetric fog for sky pixels
    if (depth >= 1.0)
    {
        FragColor = vec4(sceneColor, 1.0);
        return;
    }

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 rayDir = normalize(worldPos - uCamPos);
    float maxDist = min(length(worldPos - uCamPos), uFogMaxDistance);
    float stepSize = maxDist / float(uFogSteps);

    // Phase function for directional light scattering
    float cosAngle = dot(rayDir, -uLightDir);
    float phase = phaseHG(cosAngle, uFogAnisotropy);

    vec3 fogAccum = vec3(0.0);
    float transmittance = 1.0;

    for (int i = 0; i < uFogSteps; i++)
    {
        float t = (float(i) + 0.5) * stepSize;
        vec3 samplePos = uCamPos + rayDir * t;

        // Height-based density
        float heightAtten = exp(-max(samplePos.y - uFogBaseHeight, 0.0) * uFogHeightFalloff);

        // Noise-based density variation
        vec3 noiseCoord = samplePos * uFogNoiseScale + vec3(uTime * uFogNoiseSpeed, 0.0, uTime * uFogNoiseSpeed * 0.7);
        float noiseFactor = noise3D(noiseCoord) * 0.5 + 0.5;

        float localDensity = uFogDensity * heightAtten * noiseFactor;
        if (localDensity < 0.0001) continue;

        // Light contribution (check shadow)
        float shadowFactor = ShadowCheck(samplePos);
        vec3 lightContrib = uLightColor * phase * uFogScattering * shadowFactor;
        vec3 ambient = uFogColor * 0.15;

        // Beer-Lambert extinction
        float extinction = exp(-localDensity * stepSize);

        // Accumulate in-scattered light
        fogAccum += transmittance * (lightContrib + ambient) * uFogColor * localDensity * stepSize;
        transmittance *= extinction;

        if (transmittance < 0.01) break;
    }

    vec3 finalColor = sceneColor * transmittance + fogAccum;
    FragColor = vec4(finalColor, 1.0);
}
";

    // =====================================================================
    // DEPTH OF FIELD (separable bokeh blur)
    // =====================================================================

    public const string DepthOfFieldVert = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUV;
void main()
{
    vUV = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

    public const string DepthOfFieldFrag = @"
#version 330 core
in vec2 vUV;

uniform sampler2D uSceneColor;
uniform sampler2D gDepth;

uniform mat4  uInvViewProj;
uniform vec3  uCamPos;
uniform float uFocusDistance;
uniform float uAperture;
uniform float uFocalLength;
uniform float uMaxBlurRadius;
uniform float uNearBlurScale;
uniform float uFarBlurScale;
uniform vec2  uTexelSize;
uniform int   uPass;       // 0 = horizontal, 1 = vertical

// Camera near/far for linearizing depth
uniform float uNear;
uniform float uFar;

out vec4 FragColor;

float LinearizeDepth(float d)
{
    float z = d * 2.0 - 1.0;
    return (2.0 * uNear * uFar) / (uFar + uNear - z * (uFar - uNear));
}

// Compute Circle of Confusion diameter
float ComputeCoC(float depth)
{
    float focalLengthM = uFocalLength * 0.001; // mm to meters
    float s1 = uFocusDistance;
    float s2 = depth;

    // Thin lens CoC formula
    float coc = abs(focalLengthM * focalLengthM * (s2 - s1)) /
                (uAperture * s2 * (s1 - focalLengthM));

    // Scale to pixel radius and clamp
    coc = coc * 1000.0; // scale to visible range
    coc = clamp(coc, 0.0, uMaxBlurRadius);

    // Apply near/far scaling
    if (s2 < s1)
        coc *= uNearBlurScale;
    else
        coc *= uFarBlurScale;

    return coc;
}

void main()
{
    float depth = texture(gDepth, vUV).r;
    vec3 centerColor = texture(uSceneColor, vUV).rgb;

    if (depth >= 1.0)
    {
        FragColor = vec4(centerColor, 1.0);
        return;
    }

    float linearDepth = LinearizeDepth(depth);
    float coc = ComputeCoC(linearDepth);

    if (coc < 0.5)
    {
        FragColor = vec4(centerColor, 1.0);
        return;
    }

    // Direction: horizontal (pass 0) or vertical (pass 1)
    vec2 dir = (uPass == 0) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);

    // Variable-width Gaussian blur based on CoC
    vec3 colorSum = centerColor;
    float weightSum = 1.0;
    int samples = int(min(coc, uMaxBlurRadius));
    samples = max(samples, 1);

    for (int i = 1; i <= samples; i++)
    {
        float offset = float(i);
        float weight = 1.0 - (offset / (float(samples) + 1.0));
        weight *= weight; // quadratic falloff

        vec2 uv1 = vUV + dir * uTexelSize * offset;
        vec2 uv2 = vUV - dir * uTexelSize * offset;

        // Sample neighbor CoC to prevent sharp objects bleeding into blurred areas
        float d1 = texture(gDepth, uv1).r;
        float d2 = texture(gDepth, uv2).r;
        float coc1 = ComputeCoC(LinearizeDepth(d1));
        float coc2 = ComputeCoC(LinearizeDepth(d2));

        // Only blur if the neighbor also wants to be blurred
        float w1 = weight * smoothstep(0.0, 2.0, coc1);
        float w2 = weight * smoothstep(0.0, 2.0, coc2);

        colorSum += texture(uSceneColor, uv1).rgb * w1;
        colorSum += texture(uSceneColor, uv2).rgb * w2;
        weightSum += w1 + w2;
    }

    FragColor = vec4(colorSum / weightSum, 1.0);
}
";

    // =====================================================================
    // UI — Flat textured/colored quads for Canvas UI elements
    // =====================================================================
    public const string UIVert = @"
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;

uniform mat4 uMVP;

out vec2 vUV;
out vec4 vColor;

void main()
{
    vUV    = aUV;
    vColor = aColor;
    gl_Position = uMVP * vec4(aPos, 0.0, 1.0);
}
";

    public const string UIFrag = @"
#version 330 core
in vec2 vUV;
in vec4 vColor;

uniform sampler2D uTex;
uniform int       uHasTexture;   // 0 = solid color, 1 = textured

out vec4 FragColor;

void main()
{
    vec4 texColor = (uHasTexture != 0) ? texture(uTex, vUV) : vec4(1.0);
    vec4 final = texColor * vColor;
    if (final.a < 0.004) discard;   // ~1/255 alpha clip
    FragColor = final;
}
";

    // =====================================================================
    // UI Text — SDF (signed-distance-field) font rendering
    // Uses the same vertex format as UI quads.
    // =====================================================================
    public const string UITextFrag = @"
#version 330 core
in vec2 vUV;
in vec4 vColor;

uniform sampler2D uTex;

out vec4 FragColor;

void main()
{
    float dist = texture(uTex, vUV).r;
    // SDF smoothstep parameters — adjust for sharpness
    float edgeWidth = fwidth(dist) * 0.75;
    float alpha = smoothstep(0.5 - edgeWidth, 0.5 + edgeWidth, dist);
    if (alpha < 0.004) discard;
    FragColor = vec4(vColor.rgb, vColor.a * alpha);
}
";

    // =====================================================================
    // PLANET TERRAIN — triplanar, multi-biome, slope-based top/under blend
    // =====================================================================
    public const string PlanetTerrainVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 3) in vec4 aBlendIndices;
layout(location = 4) in vec4 aBlendWeights;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uNormalMatrix;
uniform mat4 uShadowVP;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;
flat out ivec4 vBlendIdx;
out vec4 vBlendWt;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(aNormal, 0.0)).xyz);
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    vBlendIdx = ivec4(aBlendIndices);
    vBlendWt = aBlendWeights;
    gl_Position = uProj * uView * worldPos;
}
";

    public const string PlanetTerrainFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;
flat in ivec4 vBlendIdx;
in vec4 vBlendWt;

uniform vec3 uPlanetCenter;
uniform vec3 uLightDir;
uniform vec3 uCamPos;
uniform float uAmbient;
uniform float uDiffuseK;
uniform int uAtmoEnabled;
uniform vec3 uAtmoSunDir;
uniform float uAtmoSunIntensity;
uniform float uAtmoBlend;
uniform float uAtmoRayleigh;
uniform float uAtmoMie;
uniform float uAtmoDensityFalloff;
uniform float uAtmoHorizonBlend;
uniform float uAtmoSunsetBoost;
uniform float uAtmoHeight;
uniform int uAtmoSampleCount;
uniform vec3 uAtmoZenithTint;
uniform vec3 uAtmoHorizonTint;
uniform vec3 uAtmoSkyTint;

// One dedicated sampler per biome (units 0-7), 8 + 1 shadow = 9 total
uniform sampler2D uBiomeTex0;
uniform sampler2D uBiomeTex1;
uniform sampler2D uBiomeTex2;
uniform sampler2D uBiomeTex3;
uniform sampler2D uBiomeTex4;
uniform sampler2D uBiomeTex5;
uniform sampler2D uBiomeTex6;
uniform sampler2D uBiomeTex7;

uniform float uBiomeTiling[8];
uniform vec3  uBiomeBaseColor[8];
uniform vec3  uBiomeUnderColor[8];

uniform sampler2D uShadowMap;
uniform int uHasShadow;

out vec4 FragColor;

uniform float uPlanetRadius;
uniform float uWetness;
uniform float uSnowCoverage;
uniform float uWeatherEnabled;

vec3 triplanar(sampler2D tex, vec3 worldPos, vec3 ba, float t)
{
    // Tile in meters: tiling 12 on a radius-1000 planet => ~83m repeats, not UV*worldPos sparkle.
    float meters = max(uPlanetRadius, 1.0) / max(t, 0.25);
    vec3 local = worldPos - uPlanetCenter;
    vec3 p = local / meters;
    vec3 cyz = texture(tex, p.yz).rgb;
    vec3 cxz = texture(tex, p.xz).rgb;
    vec3 cxy = texture(tex, p.xy).rgb;

    // abs(radial) weights zero the only valid projection on the XYZ planes, which
    // draws a plus of stretched texels through each cube-face pole. Weight by UV
    // area (|y|*|z| for the YZ plane, etc.) and fall back to facing at the poles.
    vec3 mag = abs(local);
    vec3 q = vec3(mag.y * mag.z, mag.x * mag.z, mag.x * mag.y);
    float qsum = q.x + q.y + q.z;
    vec3 w = qsum < 1e-4 ? abs(ba) : q / qsum;
    w = pow(max(w, vec3(0.0)), vec3(1.6));
    w /= (w.x + w.y + w.z + 0.001);
    return cyz * w.x + cxz * w.y + cxy * w.z;
}

vec3 sampleBiome(int idx, vec3 wp, vec3 ba, float t)
{
    if (idx == 0) return triplanar(uBiomeTex0, wp, ba, t);
    if (idx == 1) return triplanar(uBiomeTex1, wp, ba, t);
    if (idx == 2) return triplanar(uBiomeTex2, wp, ba, t);
    if (idx == 3) return triplanar(uBiomeTex3, wp, ba, t);
    if (idx == 4) return triplanar(uBiomeTex4, wp, ba, t);
    if (idx == 5) return triplanar(uBiomeTex5, wp, ba, t);
    if (idx == 6) return triplanar(uBiomeTex6, wp, ba, t);
    return triplanar(uBiomeTex7, wp, ba, t);
}

float shadowFactor(vec4 sc)
{
    if (uHasShadow == 0) return 1.0;
    vec3 projCoords = sc.xyz / sc.w;
    projCoords = projCoords * 0.5 + 0.5;
    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
        return 1.0;
    float closestDepth = texture(uShadowMap, projCoords.xy).r;
    float bias = 0.004 + (1.0 - max(dot(normalize(vWorldNormal), normalize(-uLightDir)), 0.0)) * 0.02;
    return (projCoords.z - bias > closestDepth) ? 0.55 : 1.0;
}

vec3 evalBiome(int idx, vec3 worldPos, vec3 ba, float slopeBlend, float nDotRadial)
{
    float t = uBiomeTiling[idx];
    vec3 texCol = sampleBiome(idx, worldPos, ba, t);
    vec3 baseCol = uBiomeBaseColor[idx];
    vec3 underCol = uBiomeUnderColor[idx];

    float lum = dot(texCol, vec3(0.299, 0.587, 0.114));
    vec3 topCol = (lum > 0.98) ? baseCol : texCol;

    // Grey floor is for outward cliffs only; inward cave rock keeps biome under-color.
    underCol = mix(underCol, max(underCol, vec3(0.12)), clamp(nDotRadial, 0.0, 1.0));
    vec3 cliffCol = mix(underCol, topCol * 0.7, 0.4);

    return mix(cliffCol, topCol, slopeBlend);
}

vec3 evalAtmosphere(vec3 worldPos, vec3 viewDir, vec3 radialDir)
{
    if (uAtmoEnabled == 0) return vec3(0.0);

    float distFromCenter = length(worldPos - uPlanetCenter);
    // Below the crust: skip sky tint so cave walls are not shaded like outdoor rock.
    if (distFromCenter < uPlanetRadius) return vec3(0.0);

    float altitude = max(distFromCenter - uPlanetRadius, 0.0);
    float atmoDepth = clamp(altitude / max(uAtmoHeight, 1.0), 0.0, 1.0);
    float density = exp(-atmoDepth * max(uAtmoDensityFalloff, 0.1));

    float horizon = pow(clamp(1.0 - abs(dot(viewDir, radialDir)), 0.0, 1.0), 1.8);
    horizon *= max(uAtmoHorizonBlend, 0.0);

    float sunForward = pow(clamp(dot(viewDir, normalize(uAtmoSunDir)), 0.0, 1.0), 4.0);
    float sunset = pow(1.0 - clamp(dot(radialDir, normalize(uAtmoSunDir)), 0.0, 1.0), 2.0) * max(uAtmoSunsetBoost, 0.0);

    vec3 grad = mix(uAtmoHorizonTint, uAtmoZenithTint, clamp(radialDir.y * 0.5 + 0.5, 0.0, 1.0));
    vec3 rayleigh = grad * (0.3 + horizon * 0.9) * max(uAtmoRayleigh, 0.0);
    vec3 mie = vec3(1.0, 0.96, 0.90) * (sunForward * 0.7 + sunset * 0.35) * max(uAtmoMie, 0.0);
    vec3 color = (rayleigh + mie + uAtmoSkyTint * 0.2) * density * max(uAtmoSunIntensity, 0.01);

    return color * clamp(uAtmoBlend, 0.0, 1.5);
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    float nDotRadial = dot(N, radialDir);

    float slope = abs(nDotRadial);
    float slopeBlend = smoothstep(0.15, 0.55, slope);

    vec3 radialAxes = abs(radialDir);
    radialAxes = pow(radialAxes, vec3(3.0));
    radialAxes = radialAxes / (radialAxes.x + radialAxes.y + radialAxes.z + 0.001);

    vec3 normalAxes = abs(N);
    normalAxes = pow(normalAxes, vec3(1.8));
    normalAxes = normalAxes / (normalAxes.x + normalAxes.y + normalAxes.z + 0.001);

    // Flat ground: radial projection (stable at cube-face poles). Steep slopes: surface normal
    // so triplanar does not smear the albedo along cliff faces.
    vec3 blendAxes = mix(normalAxes, radialAxes, slopeBlend);

    vec3 finalColor = vec3(0.0);
    float totalWeight = 0.0;

    float weights[4] = float[4](vBlendWt.x, vBlendWt.y, vBlendWt.z, vBlendWt.w);
    int   indices[4] = int[4](vBlendIdx.x, vBlendIdx.y, vBlendIdx.z, vBlendIdx.w);

    for (int i = 0; i < 4; i++)
    {
        float w = weights[i];
        if (w < 0.01) continue;
        int idx = clamp(indices[i], 0, 7);
        finalColor += evalBiome(idx, vWorldPos, blendAxes, slopeBlend, nDotRadial) * w;
        totalWeight += w;
    }

    if (totalWeight > 0.0) finalColor /= totalWeight;
    else finalColor = vec3(0.5);

    float distFromCenter = length(vWorldPos - uPlanetCenter);
    float interior = 1.0 - smoothstep(uPlanetRadius - 2.0, uPlanetRadius, distFromCenter);
    float outdoor = 1.0 - interior;
    float weatherOn = clamp(uWeatherEnabled, 0.0, 1.0) * outdoor;

    // Stable hash in meters. Never replace albedo with a constant color.
    vec3 q = (vWorldPos - uPlanetCenter) * 0.028;
    vec2 wuv = fract(vec2(dot(q, vec3(0.17, 0.08, 0.13)), dot(q, vec3(0.11, 0.19, 0.06))));
    float nA = fract(sin(dot(wuv, vec2(12.9898, 78.233))) * 43758.5453);
    float nB = fract(sin(dot(wuv + vec2(0.17, 0.31), vec2(39.346, 11.135))) * 23421.631);
    float slopeOk = smoothstep(0.18, 0.58, abs(nDotRadial));
    float wetAmt = clamp(uWetness, 0.0, 1.0) * weatherOn;
    float snowAmt = clamp(uSnowCoverage, 0.0, 1.0) * weatherOn;
    float puddleMask = smoothstep(0.36, 0.66, nA * 0.7 + nB * 0.3);
    float puddle = wetAmt * slopeOk * puddleMask * (1.0 - snowAmt);
    finalColor *= mix(vec3(1.0), vec3(0.80, 0.88, 0.90), wetAmt * slopeOk * 0.4);
    finalColor *= mix(vec3(1.0), vec3(0.58, 0.70, 0.76), puddle * 0.5);

    vec3 L = normalize(-uLightDir);
    float NdotL = max(dot(N, L), 0.0);
    float diffuse = NdotL * uDiffuseK;

    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 32.0) * 0.06 * slopeBlend;
    spec += pow(max(dot(N, H), 0.0), 18.0) * puddle * 0.55;
    float fres = pow(1.0 - max(dot(N, V), 0.0), 2.8) * puddle * 0.35;

    float shadow = shadowFactor(vShadowCoord);
    float ao = mix(1.0, 0.35, clamp(-nDotRadial, 0.0, 1.0));
    float ambient = uAmbient * mix(1.0, 0.7, interior);
    vec3 lit = finalColor * (ambient + diffuse * shadow) * ao + vec3((spec + fres) * shadow * ao);
    lit += evalAtmosphere(vWorldPos, V, radialDir);
    lit = lit / (lit + vec3(1.0));

    lit += vec3(0.14, 0.17, 0.20) * pow(max(dot(N, H), 0.0), 26.0) * puddle * shadow;
    lit = mix(lit, lit * 0.45 + vec3(0.86, 0.90, 0.94) * 0.55, snowAmt * slopeOk * mix(0.2, 0.5, nB));

    FragColor = vec4(lit, 1.0);
}
";

    // =====================================================================
    // PLANET WATER — spherical waves, depth-dependent color, Fresnel
    // =====================================================================
    public const string PlanetWaterVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uNormalMatrix;

uniform vec3 uPlanetCenter;
uniform float uTime;
uniform float uWaveAmp1;
uniform float uWaveFreq1;
uniform float uWaveSteep1;
uniform float uWaveAmp2;
uniform float uWaveFreq2;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(worldPos.xyz - uPlanetCenter);

    vec3 up = abs(radialDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent   = normalize(cross(up, radialDir));
    vec3 bitangent = cross(radialDir, tangent);

    float tCoord = dot(worldPos.xyz, tangent);
    float bCoord = dot(worldPos.xyz, bitangent);

    float phase1 = (tCoord * 0.7 + bCoord * 0.7) * uWaveFreq1 + uTime;
    float s1 = sin(phase1);
    float c1 = cos(phase1);

    float phase2 = (tCoord * -0.5 + bCoord * 0.86) * uWaveFreq2 + uTime * 0.8;
    float s2 = sin(phase2);
    float c2 = cos(phase2);

    worldPos.xyz += radialDir * (uWaveAmp1 * s1 + uWaveAmp2 * s2);
    worldPos.xyz += tangent * uWaveSteep1 * uWaveAmp1 * c1;

    vec3 waveNormal = normalize(
        radialDir
        - tangent   * (uWaveFreq1 * uWaveAmp1 * c1 * 0.7 + uWaveFreq2 * uWaveAmp2 * c2 * -0.5)
        - bitangent * (uWaveFreq1 * uWaveAmp1 * c1 * 0.7 + uWaveFreq2 * uWaveAmp2 * c2 * 0.86)
    );

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(waveNormal, 0.0)).xyz);
    vUV = aUV;
    gl_Position = uProj * uView * worldPos;
}
";

    public const string PlanetWaterFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;

uniform vec3 uPlanetCenter;
uniform float uSeaLevel;
uniform float uDepthRange;

uniform vec4 uShallowColor;
uniform vec4 uDeepColor;
uniform vec4 uDeepestColor;
uniform vec4 uBodyShallow[8];
uniform vec4 uBodyDeep[8];
uniform vec4 uBodyDeepest[8];
uniform int uWaterBodyCount;
uniform vec3 uBiomeBaseColor[8];
uniform int uBiomeColorCount;
uniform float uShorelineThreshold;
uniform float uShorelineSoftness;
uniform float uShoreBiomeBlend;

uniform float uFresnelPower;
uniform float uReflectivity;
uniform float uTransparency;

uniform vec3 uLightDir;
uniform vec3 uCamPos;
uniform vec3 uSkyColor;
uniform float uAmbient;
uniform float uDiffuseK;
uniform float uTime;
uniform int uAtmoEnabled;
uniform vec3 uAtmoSunDir;
uniform float uAtmoSunIntensity;
uniform float uAtmoBlend;
uniform float uAtmoRayleigh;
uniform float uAtmoMie;
uniform float uAtmoDensityFalloff;
uniform float uAtmoHorizonBlend;
uniform float uAtmoSunsetBoost;
uniform float uAtmoHeight;
uniform vec3 uAtmoZenithTint;
uniform vec3 uAtmoHorizonTint;
uniform float uPlanetRadius;

uniform sampler2D uWaterNormalMap;
uniform sampler2D uWaterTexture;
uniform int uHasWaterNormalMap;
uniform int uHasWaterTexture;

uniform int uFoamEnabled;
uniform float uFoamThreshold;
uniform float uFoamIntensity;
uniform vec4 uFoamColor;

out vec4 FragColor;

vec3 sampleBiomeColor(int idx)
{
    if (uBiomeColorCount <= 0) return uShallowColor.rgb;
    int clampedIdx = clamp(idx, 0, min(7, uBiomeColorCount - 1));
    if (clampedIdx == 0) return uBiomeBaseColor[0];
    if (clampedIdx == 1) return uBiomeBaseColor[1];
    if (clampedIdx == 2) return uBiomeBaseColor[2];
    if (clampedIdx == 3) return uBiomeBaseColor[3];
    if (clampedIdx == 4) return uBiomeBaseColor[4];
    if (clampedIdx == 5) return uBiomeBaseColor[5];
    if (clampedIdx == 6) return uBiomeBaseColor[6];
    return uBiomeBaseColor[7];
}

vec3 evalAtmosphere(vec3 worldPos, vec3 viewDir, vec3 radialDir)
{
    if (uAtmoEnabled == 0) return vec3(0.0);
    float altitude = max(length(worldPos - uPlanetCenter) - uPlanetRadius, 0.0);
    float atmoDepth = clamp(altitude / max(uAtmoHeight, 1.0), 0.0, 1.0);
    float density = exp(-atmoDepth * max(uAtmoDensityFalloff, 0.1));
    float horizon = pow(clamp(1.0 - abs(dot(viewDir, radialDir)), 0.0, 1.0), 1.8) * max(uAtmoHorizonBlend, 0.0);
    float sunForward = pow(clamp(dot(viewDir, normalize(uAtmoSunDir)), 0.0, 1.0), 4.0);
    float sunset = pow(1.0 - clamp(dot(radialDir, normalize(uAtmoSunDir)), 0.0, 1.0), 2.0) * max(uAtmoSunsetBoost, 0.0);
    vec3 grad = mix(uAtmoHorizonTint, uAtmoZenithTint, clamp(radialDir.y * 0.5 + 0.5, 0.0, 1.0));
    vec3 rayleigh = grad * (0.3 + horizon * 0.9) * max(uAtmoRayleigh, 0.0);
    vec3 mie = vec3(1.0, 0.96, 0.90) * (sunForward * 0.7 + sunset * 0.35) * max(uAtmoMie, 0.0);
    return (rayleigh + mie) * density * max(uAtmoSunIntensity, 0.01) * clamp(uAtmoBlend, 0.0, 1.5);
}

void main()
{
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    vec3 N = normalize(vWorldNormal);

    vec3 up = abs(radialDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 T = normalize(cross(up, radialDir));
    vec3 B = cross(radialDir, T);

    float tCoord = dot(vWorldPos, T);
    float bCoord = dot(vWorldPos, B);

    if (uHasWaterNormalMap == 1)
    {
        vec2 uv1 = vec2(tCoord, bCoord) * 0.02 + vec2(uTime * 0.01, uTime * 0.008);
        vec2 uv2 = vec2(tCoord, bCoord) * 0.035 + vec2(-uTime * 0.007, uTime * 0.012);
        vec3 n1 = texture(uWaterNormalMap, uv1).rgb * 2.0 - 1.0;
        vec3 n2 = texture(uWaterNormalMap, uv2).rgb * 2.0 - 1.0;
        vec3 detailN = normalize(n1 + n2);
        N = normalize(N + (T * detailN.x + B * detailN.y + radialDir * detailN.z) * 0.3);
    }

    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 L = normalize(-uLightDir);

    // View-angle based apparent depth: looking straight down = shallow, grazing = deep
    float viewAngle = max(dot(V, radialDir), 0.0);
    float apparentDepth = 1.0 - viewAngle;

    // Subtle noise-based depth variation for visual interest
    float noiseVal = fract(sin(dot(vec2(tCoord, bCoord) * 0.1, vec2(12.9898, 78.233))) * 43758.5453);
    apparentDepth = clamp(apparentDepth + noiseVal * 0.1 - 0.05, 0.0, 1.0);

    int shorelineBiomeIdx = int(mod(vUV.x, 8.0));
    int bodyIdx = int(vUV.x / 8.0);
    vec4 shallowCol = uShallowColor;
    vec4 deepCol = uDeepColor;
    vec4 deepestCol = uDeepestColor;
    float waterMask = clamp(vUV.y, 0.0, 1.0);
    if (waterMask < 0.02) discard;
    bool isLava = bodyIdx == 6;
    if (uWaterBodyCount > 0 && !isLava)
    {
        int clampedBody = clamp(bodyIdx, 0, min(7, uWaterBodyCount - 1));
        if (clampedBody == 0) { shallowCol = uBodyShallow[0]; deepCol = uBodyDeep[0]; deepestCol = uBodyDeepest[0]; }
        else if (clampedBody == 1) { shallowCol = uBodyShallow[1]; deepCol = uBodyDeep[1]; deepestCol = uBodyDeepest[1]; }
        else if (clampedBody == 2) { shallowCol = uBodyShallow[2]; deepCol = uBodyDeep[2]; deepestCol = uBodyDeepest[2]; }
        else if (clampedBody == 3) { shallowCol = uBodyShallow[3]; deepCol = uBodyDeep[3]; deepestCol = uBodyDeepest[3]; }
        else if (clampedBody == 4) { shallowCol = uBodyShallow[4]; deepCol = uBodyDeep[4]; deepestCol = uBodyDeepest[4]; }
        else if (clampedBody == 5) { shallowCol = uBodyShallow[5]; deepCol = uBodyDeep[5]; deepestCol = uBodyDeepest[5]; }
        else if (clampedBody == 6) { shallowCol = uBodyShallow[6]; deepCol = uBodyDeep[6]; deepestCol = uBodyDeepest[6]; }
        else { shallowCol = uBodyShallow[7]; deepCol = uBodyDeep[7]; deepestCol = uBodyDeepest[7]; }
    }
    if (isLava)
    {
        shallowCol = vec4(1.00, 0.62, 0.12, 1.0);
        deepCol    = vec4(0.85, 0.18, 0.03, 1.0);
        deepestCol = vec4(0.22, 0.03, 0.01, 1.0);
    }

    vec3 waterColor;
    if (apparentDepth < 0.4)
        waterColor = mix(shallowCol.rgb, deepCol.rgb, apparentDepth * 2.5);
    else
        waterColor = mix(deepCol.rgb, deepestCol.rgb, (apparentDepth - 0.4) * 1.67);

    vec3 shorelineBiomeColor = sampleBiomeColor(shorelineBiomeIdx);
    float shoreWetness = smoothstep(
        max(0.0, uShorelineThreshold - uShorelineSoftness),
        min(1.0, uShorelineThreshold + uShorelineSoftness),
        waterMask);
    vec3 shoreTint = mix(shallowCol.rgb, shorelineBiomeColor, clamp(uShoreBiomeBlend, 0.0, 1.0));
    waterColor = mix(shoreTint, waterColor, shoreWetness);

    if (uHasWaterTexture == 1)
    {
        vec2 texUV = vec2(tCoord, bCoord) * 0.01 + vec2(uTime * 0.005);
        vec3 texCol = texture(uWaterTexture, texUV).rgb;
        waterColor = mix(waterColor, texCol, 0.15);
    }

    float lavaPulse = 0.55 + 0.45 * sin(uTime * 1.8 + tCoord * 0.07 + bCoord * 0.05);
    if (isLava)
    {
        vec3 lavaHot = vec3(1.0, 0.72, 0.16);
        waterColor = mix(deepestCol.rgb, mix(deepCol.rgb, lavaHot, lavaPulse), 0.55 + apparentDepth * 0.35);
        waterColor += lavaHot * (0.18 + 0.14 * lavaPulse);
    }

    // Fresnel: strong reflection at grazing angles
    float fresnel = pow(1.0 - max(dot(N, V), 0.0), uFresnelPower);
    if (isLava)
        fresnel *= 0.18;

    vec3 R = reflect(-V, N);
    float skyFactor = clamp(dot(R, radialDir) * 0.5 + 0.5, 0.0, 1.0);
    vec3 reflColor = uSkyColor * (0.4 + skyFactor * 0.6);
    reflColor += evalAtmosphere(vWorldPos, V, radialDir);
    waterColor = mix(waterColor, reflColor, fresnel * uReflectivity);

    // Sun specular
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 256.0) * 1.2;

    // Scattered sub-surface glow
    float scatter = pow(max(dot(V, -L), 0.0), 4.0) * 0.15;
    vec3 scatterColor = uShallowColor.rgb * scatter;

    float NdotL = max(dot(N, L), 0.0);
    float lighting = uAmbient + NdotL * 0.4 * uDiffuseK;
    vec3 color = waterColor * lighting + vec3(spec) + scatterColor;
    color += evalAtmosphere(vWorldPos, V, radialDir) * 0.45;
    float viewExtinction = exp(-max(0.0, 1.0 - dot(V, radialDir)) * 2.2);
    color *= mix(0.82, 1.0, viewExtinction);

    if (uFoamEnabled == 1)
    {
        float edgeFoam = smoothstep(0.02, 0.0, apparentDepth * 0.3);
        float hash = fract(sin(dot(floor(vec2(tCoord, bCoord) * 5.0 + uTime * 0.3), vec2(12.9898, 78.233))) * 43758.5453);
        float foam = edgeFoam * hash * uFoamIntensity;
        color = mix(color, uFoamColor.rgb, foam);
    }

    color = color / (color + vec3(1.0));

    float alpha = mix(uTransparency, 0.98, fresnel * 0.6);
    alpha *= mix(0.75, 1.0, shoreWetness);
    if (isLava)
        alpha = 0.96;
    if (alpha < 0.02) discard;
    FragColor = vec4(color, alpha);
}
";

    public const string PlanetCloudsVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCloudBaseHeight;
uniform float uCloudTopHeight;

out vec3 vWorldPos;
out vec3 vWorldNormal;

void main()
{
    vec4 terrainWorldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(terrainWorldPos.xyz - uPlanetCenter);
    float shellHeight = max(0.0, 0.5 * (uCloudBaseHeight + uCloudTopHeight));
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + shellHeight);

    vWorldPos = shellPos;
    vWorldNormal = radialDir;
    gl_Position = uProj * uView * vec4(shellPos, 1.0);
}
";

    public const string PlanetAtmosphereVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uAtmosphereHeight;

out vec3 vWorldPos;
out vec3 vRadialDir;

void main()
{
    vec4 terrainWorldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(terrainWorldPos.xyz - uPlanetCenter);
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + max(uAtmosphereHeight, 1.0));
    vWorldPos = shellPos;
    vRadialDir = radialDir;
    gl_Position = uProj * uView * vec4(shellPos, 1.0);
}
";

    public const string PlanetAtmosphereFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vRadialDir;

uniform vec3 uCamPos;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uAtmosphereHeight;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform float uAtmoBlend;
uniform float uRayleighStrength;
uniform float uMieStrength;
uniform float uDensityFalloff;
uniform float uHorizonBlend;
uniform float uSunsetBoost;
uniform vec3 uZenithTint;
uniform vec3 uHorizonTint;

out vec4 FragColor;

void main()
{
    vec3 V = normalize(vWorldPos - uCamPos);
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    vec3 sunDir = normalize(uSunDir);

    float horizonBase = clamp(1.0 - abs(dot(V, radialDir)), 0.0, 1.0);
    float horizon = pow(horizonBase, 2.1) * max(uHorizonBlend, 0.0);
    float sunForward = pow(clamp(dot(V, sunDir), 0.0, 1.0), 10.0);
    float sunset = pow(1.0 - clamp(dot(radialDir, sunDir), 0.0, 1.0), 2.4) * max(uSunsetBoost, 0.0);
    float heightFade = exp(-max(uDensityFalloff, 0.05));
    float shellFade = clamp(pow(horizonBase, 1.6), 0.0, 1.0) * heightFade;

    vec3 grad = mix(uHorizonTint, uZenithTint, clamp(radialDir.y * 0.5 + 0.5, 0.0, 1.0));
    vec3 rayleigh = grad * (0.08 + horizon * 0.70) * max(uRayleighStrength, 0.0);
    vec3 mie = vec3(1.0, 0.96, 0.90) * (sunForward * 0.28 + sunset * 0.18) * max(uMieStrength, 0.0);
    vec3 col = (rayleigh + mie) * max(uSunIntensity, 0.01);
    float camDist = length(uCamPos - uPlanetCenter);
    float inside01 = clamp(((uPlanetRadius + uAtmosphereHeight) - camDist) / max(uAtmosphereHeight, 1.0), 0.0, 1.0);
    float insideHaze = inside01 * (0.025 + (1.0 - horizonBase) * 0.05);
    float alpha = clamp((horizon * 0.22) * max(uAtmoBlend, 0.0) + insideHaze * max(uAtmoBlend, 0.0), 0.0, 0.38) * shellFade;
    FragColor = vec4(col, alpha);
}
";

    public const string PlanetCloudsFrag = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vWorldNormal;

uniform vec3 uCamPos;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCloudBaseHeight;
uniform float uCloudTopHeight;
uniform float uCloudCoverage;
uniform float uCloudDensity;
uniform float uCloudDetail;
uniform float uCloudSpeed;
uniform float uCloudSoftness;
uniform float uCloudLightResponse;
uniform float uCloudSilverLining;
uniform int uCloudStepCount;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform vec3 uSkyTint;
uniform float uTime;

out vec4 FragColor;

float hash31(vec3 p)
{
    return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453123);
}

float noise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hash31(i + vec3(0.0, 0.0, 0.0));
    float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
    float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
    float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
    float n111 = hash31(i + vec3(1.0, 1.0, 1.0));

    float nx00 = mix(n000, n100, u.x);
    float nx10 = mix(n010, n110, u.x);
    float nx01 = mix(n001, n101, u.x);
    float nx11 = mix(n011, n111, u.x);
    float nxy0 = mix(nx00, nx10, u.y);
    float nxy1 = mix(nx01, nx11, u.y);
    return mix(nxy0, nxy1, u.z);
}

float fbm(vec3 p)
{
    float value = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        value += noise3(p) * amp;
        p = p * 2.0 + vec3(17.0, 11.0, 7.0);
        amp *= 0.5;
    }
    return value;
}

void main()
{
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 L = normalize(uSunDir);

    float midHeight = max(1.0, 0.5 * (uCloudBaseHeight + uCloudTopHeight));
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + midHeight);

    vec3 wind = vec3(uTime * uCloudSpeed, 0.0, uTime * uCloudSpeed * 0.73);
    float baseN = fbm(shellPos * (0.0025 * uCloudDetail) + wind);
    float detailN = fbm(shellPos * (0.0080 * uCloudDetail) - wind * 2.1);
    float density = mix(baseN, detailN, 0.55);
    density = density * density;

    float threshold = mix(0.80, 0.25, clamp(uCloudCoverage, 0.0, 1.0));
    float edge = max(0.02, uCloudSoftness) * 0.45;
    float coverage = smoothstep(threshold - edge, threshold + edge, density);
    float cloudAlpha = coverage * clamp((density - threshold + edge) / max(edge * 2.0, 0.001), 0.0, 1.0);
    cloudAlpha *= clamp(uCloudDensity * 0.45, 0.0, 1.0);

    if (cloudAlpha < 0.01) discard;

    float sunFacing = clamp(dot(radialDir, L) * 0.5 + 0.5, 0.0, 1.0);
    float silver = pow(clamp(1.0 - max(dot(V, L), 0.0), 0.0, 1.0), 6.0) * uCloudSilverLining;
    float lightTerm = (0.25 + sunFacing * 0.75) * uCloudLightResponse;
    vec3 cloudColor = mix(uSkyTint * 0.75, vec3(0.92, 0.94, 0.98), lightTerm);
    cloudColor += vec3(1.0, 0.97, 0.92) * silver * 0.35 * uSunIntensity;

    FragColor = vec4(cloudColor, clamp(cloudAlpha, 0.0, 0.55));
}
";
}
