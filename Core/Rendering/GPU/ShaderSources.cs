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
            "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;");
    }

    // =====================================================================
    // STANDARD (PBR-like: diffuse + Blinn-Phong specular)
    // =====================================================================
    public const string StandardVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

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

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);

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
    vWorldNormal = normalize((uNormalMatrix * vec4(aNormal, 0.0)).xyz);
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

// Textures
uniform sampler2D uAlbedoTex;
uniform bool      uHasAlbedoTex;
uniform sampler2D uShadowMap;
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

float ShadowCalc(vec4 sc, vec3 N)
{
    if (!uHasShadow) return 1.0;
    vec3 proj = sc.xyz / sc.w;
    proj = proj * 0.5 + 0.5;
    // Outside shadow map bounds → fully lit
    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    // Slope-scaled bias: surfaces nearly parallel to the light get more bias
    float cosTheta = max(dot(N, -uSunDir), 0.0);
    float bias = uShadowBias + uShadowBias * 3.0 * (1.0 - cosTheta);

    float currentDepth = proj.z - bias;

    // 3x3 PCF kernel for softer shadow edges
    vec2 texelSize = 1.0 / vec2(textureSize(uShadowMap, 0));
    float result = 0.0;
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            float pcfDepth = texture(uShadowMap, proj.xy + vec2(x, y) * texelSize).r;
            result += (currentDepth > pcfDepth) ? 0.0 : 1.0;
        }
    }
    float shadow = max(result / 9.0, 0.10);

    // Smooth edge falloff: fade shadow to 1.0 near shadow map borders
    // so there's no visible hard boundary as the player moves
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

    // Albedo
    vec4 albedo = uBaseColor;
    if (uHasAlbedoTex)
        albedo *= texture(uAlbedoTex, vUV);

    // Alpha test
    if (uTransparent && albedo.a < uAlphaCutoff)
        discard;

    // Light direction
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
        L = uLightDir;
    }

    float NdotL = max(dot(N, L), 0.0);
    float diffuse = NdotL * atten;
    if (diffuse > 1.0) diffuse = 1.0;

    // Shadow (slope-biased)
    float shadow = ShadowCalc(vShadowCoord, N);

    // Specular (Blinn-Phong)
    float specular = 0.0;
    if (uDiffuseK > 0.0 && diffuse > 0.0)
    {
        float smoothness = 1.0 - uRoughness;
        float shininess = 8.0 + smoothness * smoothness * 248.0;
        vec3 V = normalize(uCamPos - vWorldPos);
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        specular = pow(NdotH, shininess) * (0.25 + 0.75 * uMetallic) * diffuse;
    }

    // Combine — shadow attenuates both ambient (sky occlusion) and diffuse
    // In shadowed areas ambient drops to ~35%, simulating occlusion from the sun/sky
    float ambShadow = mix(0.35, 1.0, shadow);
    float shade = clamp(uAmbient * ambShadow + uDiffuseK * diffuse * shadow, 0.0, 1.0);
    vec3 color = albedo.rgb * shade + vec3(specular * shadow);
    color = clamp(color, 0.0, 1.0);

    float alpha = uTransparent ? albedo.a : 1.0;
    FragColor = vec4(color, alpha);
}
";

    // =====================================================================
    // DEPTH ONLY (shadow map pass)
    // =====================================================================
    public const string DepthOnlyVert = @"
#version 330 core
layout(location = 0) in vec3 aPosition;

uniform mat4 uMVP;

void main()
{
    gl_Position = uMVP * vec4(aPosition, 1.0);
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
        vec4 texSamp = texture(uSkyTex, vec2(u, v));
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

// Fallback (no layers defined)
uniform vec4  uBaseColor;
uniform sampler2D uAlbedoTex;
uniform bool      uHasAlbedoTex;

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

void main()
{
    vec3 N = normalize(vWorldNormal);

    // ── Splatmap blended albedo ──
    // Uses simple UV-based sampling (no triplanar) for maximum performance.
    vec4 albedo;
    if (uLayerCount > 0)
    {
        vec4 s0 = texture(uSplatmap0, vUV);
        albedo = vec4(0.0);

        // Layer 0
        albedo += texture(uLayer0, vUV * uTiling0) * s0.r;

        // Layers 1-3 (still in splatmap0)
        if (uLayerCount > 1) albedo += texture(uLayer1, vUV * uTiling1) * s0.g;
        if (uLayerCount > 2) albedo += texture(uLayer2, vUV * uTiling2) * s0.b;
        if (uLayerCount > 3) albedo += texture(uLayer3, vUV * uTiling3) * s0.a;

        // Layers 4-7 only sampled if needed (avoids splatmap1 texture fetch when <= 4 layers)
        if (uLayerCount > 4)
        {
            vec4 s1 = texture(uSplatmap1, vUV);
            albedo += texture(uLayer4, vUV * uTiling4) * s1.r;
            if (uLayerCount > 5) albedo += texture(uLayer5, vUV * uTiling5) * s1.g;
            if (uLayerCount > 6) albedo += texture(uLayer6, vUV * uTiling6) * s1.b;
            if (uLayerCount > 7) albedo += texture(uLayer7, vUV * uTiling7) * s1.a;
        }

        albedo.a = 1.0;
    }
    else
    {
        albedo = uBaseColor;
        if (uHasAlbedoTex) albedo *= texture(uAlbedoTex, vUV);
    }

    // ── Lighting ──
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
        L = uLightDir;
    }

    float NdotL = max(dot(N, L), 0.0);
    float diffuse = min(NdotL * atten, 1.0);

    float shadow = ShadowCalc(vShadowCoord, N);

    // Specular (Blinn-Phong)
    float specular = 0.0;
    if (uDiffuseK > 0.0 && diffuse > 0.0)
    {
        float shininess = 16.0;
        vec3 V = normalize(uCamPos - vWorldPos);
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        specular = pow(NdotH, shininess) * 0.15 * diffuse;
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
}
