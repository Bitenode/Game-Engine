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
uniform int   uTransparent;
uniform int   uDoubleSided;

// Albedo texture
uniform sampler2D uAlbedoTex;
uniform int       uHasAlbedoTex;

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

// Global UBOs + Shadow (cascaded shadow maps)
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#define SHADOW_CASCADES
#include "Common/Shadows.glsl"
#include "Common/NormalMapping.glsl"

// Lighting (per-pass / planet overrides)
uniform int   uHasPlanetFill;
uniform vec3  uPlanetCenter;
uniform float uShadowBias;
uniform vec3  uSunDir;          // direction FROM sun (for slope bias)
uniform int   uIsVegetation;
uniform float uSunIntensity;
uniform float uWetness;
uniform float uSnow;
uniform float uCloudiness;

out vec4 FragColor;

float ShadowCalc(vec4 sc, vec3 N)
{
    if (uHasShadow == 0) return 1.0;

    // Determine cascade index from fragment distance to camera
    float fragDist = length(vWorldPos - uCamPos);
    int cascadeIdx = SelectShadowCascade(fragDist);

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
    float fade = ShadowEdgeFade(proj.xy, 0.08);
    return mix(1.0, shadow, fade);
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    if (uDoubleSided != 0 && !gl_FrontFacing) N = -N;

    // Save geometric normal for shadow bias (unaffected by normal map)
    vec3 geoN = N;

    // Normal mapping (screen-space derivative TBN)
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

    // Albedo
    vec4 albedo = uBaseColor;
    if (uHasAlbedoTex != 0)
        albedo *= texture(uAlbedoTex, vUV);

    float maskA = albedo.a;
    if (uHasOpacityTex != 0)
    {
        // RGB block compression (BC1) has no alpha and reports it as 0. The opacity map is the
        // alpha for that material; a zero albedo alpha must not erase it.
        float albedoA = albedo.a < 0.001 ? 1.0 : albedo.a;
        maskA = albedoA * texture(uOpacityTex, vUV).r;
    }

    float luma = dot(albedo.rgb, vec3(0.299, 0.587, 0.114));
    float mx = max(albedo.r, max(albedo.g, albedo.b));
    bool darkBackdrop = (uLumaClip > 0.0) && (luma < uLumaClip) && (mx < 0.23);

    // Alpha test + RGB-only sheet heuristic (many FBX leaf textures have no alpha, only dark backing RGB)
    bool clipAlpha = false;
    if (uTransparent != 0)
        clipAlpha = maskA < uAlphaCutoff;
    else if (uHasAlbedoTex != 0)
    {
        bool byMask = (uAlphaCutoff > 0.0005) && (maskA < uAlphaCutoff);
        clipAlpha = byMask || darkBackdrop;
    }
    if (clipAlpha)
        discard;

    // Per-pixel PBR parameters from texture maps
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

    // Light direction
    // uLightDir is FROM the light (shine direction). Negate for TOWARD the light.
    vec3 L;
    float atten = 1.0;
    if (uLightIsPoint != 0)
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
    float card = uIsVegetation == 1 && uAlphaCutoff > 0.05 ? 1.0 : 0.0;
    float wrap = mix(NdotL, NdotL * 0.5 + 0.5, card);
    float back = max(dot(-N, L), 0.0) * 0.28 * card;
    float diffuse = min((wrap + back) * atten, 1.15);

    // Shadow — use geometric normal for bias to prevent normal-map shadow acne
    float shadow = ShadowCalc(vShadowCoord, geoN);

    // Specular (Blinn-Phong)
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

    vec3 litAlbedo = albedo.rgb;
    float shade;
    float aoLit = aoFactor;
    if (uIsVegetation == 1)
    {
        float clouds = clamp(uCloudiness, 0.0, 1.0);
        float wet = clamp(uWetness, 0.0, 1.0);
        float snow = clamp(uSnow, 0.0, 1.0);
        float sunI = max(0.02, uSunIntensity) * mix(1.0, 0.55, clouds);
        float amb = max(0.02, uAmbient) * mix(1.0, 0.72, clouds);
        litAlbedo *= mix(1.0, 0.82, wet);
        litAlbedo = mix(litAlbedo, litAlbedo * vec3(0.86, 0.92, 0.96), wet * 0.35);
        litAlbedo = mix(litAlbedo, vec3(0.86, 0.89, 0.93), snow * mix(0.18, 0.42, card));
        aoLit = mix(0.62, 1.0, aoFactor);
        float ambShadow = mix(0.42, 1.0, shadow);
        // Scale wrap with sun so night does not keep half-Lambert fill lighting.
        float night = clamp(uSunIntensity, 0.0, 1.0);
        float wrapDiff = mix(diffuse, diffuse * 0.35 + 0.08, 1.0 - night);
        shade = amb * ambShadow + uDiffuseK * wrapDiff * sunI * mix(0.40, 1.0, shadow);
        specular *= wet * 0.45 * sunI;
    }
    else
    {
        // Shadow attenuates both ambient (sky occlusion) and diffuse
        float ambShadow = mix(0.35, 1.0, shadow);
        shade = clamp(uAmbient * ambShadow + uDiffuseK * diffuse * shadow, 0.0, 1.0);
        // Planet terrain is Lambert. Props were left on a dim PBR-style response plus a
        // crushed AO bake, so campfires and workbenches read nearly black on the ground.
        if (uHasPlanetFill == 1)
        {
            vec3 radial = normalize(vWorldPos - uPlanetCenter);
            float sky = clamp(dot(N, radial), 0.0, 1.0);
            float aoLift = mix(0.62, 1.0, clamp(aoFactor, 0.0, 1.0));
            float hemi = uAmbient * aoLift * (0.90 + 0.55 * sky);
            float sun = uDiffuseK * diffuse * mix(0.55, 1.0, shadow);
            shade = clamp(hemi + sun, 0.0, 1.35);
            aoLit = 1.0;
        }
    }

    vec3 color = litAlbedo * shade + vec3(specular * shadow);

    // AO darkens the entire lit result (ambient + diffuse + specular)
    // Applied before emissive so self-illumination is unaffected by occlusion
    color *= aoLit;

    // Emissive (bypasses lighting)
    vec3 emissive = uEmissiveColor * uEmissiveIntensity;
    if (uHasEmissiveTex == 1)
        emissive *= texture(uEmissiveTex, vUV).rgb;
    color += emissive;

    color = clamp(color, 0.0, 1.0);

    float alpha = (uTransparent != 0) ? maskA : 1.0;
    FragColor = vec4(color, alpha);
}
