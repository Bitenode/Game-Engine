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

// Global UBOs + Shadow (cascaded shadow maps) + Cook-Torrance PBR
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#define SHADOW_CASCADES
#include "Common/Shadows.glsl"
#include "Common/NormalMapping.glsl"
#include "Common/PBR.glsl"

// Environment / ReflectionProbe IBL (unit 12)
uniform samplerCube uEnvMap;
uniform int         uHasEnvMap;
uniform float       uEnvIntensity;
uniform float       uEnvMaxMip;

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
    return SampleShadowCascaded(vWorldPos, N, uSunDir, max(uShadowBias, 0.002), 0.04);
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
    float roughness = clamp(uRoughness, 0.04, 1.0);
    if (uHasRoughnessTex == 1)
        roughness = clamp(texture(uRoughnessTex, vUV).r, 0.04, 1.0);

    float metallic = clamp(uMetallic, 0.0, 1.0);
    if (uHasMetallicTex == 1)
        metallic = clamp(texture(uMetallicTex, vUV).r, 0.0, 1.0);

    float aoFactor = 1.0;
    if (uHasAOTex == 1)
        aoFactor = texture(uAOTex, vUV).r;

    float specMask = 1.0;
    if (uHasSpecularTex == 1)
        specMask = texture(uSpecularTex, vUV).r;

    // Primary light direction (uLightDir is FROM the light; negate for TOWARD the light)
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
        L = normalize(-uLightDir);
    }

    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 lightCol = dot(uLightColor, uLightColor) < 0.001 ? vec3(1.0) : uLightColor;

    // Shadow — use geometric normal for bias to prevent normal-map shadow acne
    float shadow = ShadowCalc(vShadowCoord, geoN);

    vec3 litAlbedo = albedo.rgb;
    vec3 color;
    if (uIsVegetation == 1)
    {
        float NdotL = max(dot(N, L), 0.0);
        float card = uAlphaCutoff > 0.05 ? 1.0 : 0.0;
        float wrap = mix(NdotL, NdotL * 0.5 + 0.5, card);
        float back = max(dot(-N, L), 0.0) * 0.28 * card;
        float diffuse = min((wrap + back) * atten, 1.15);

        float clouds = clamp(uCloudiness, 0.0, 1.0);
        float wet = clamp(uWetness, 0.0, 1.0);
        float snow = clamp(uSnow, 0.0, 1.0);
        float sunI = max(0.02, uSunIntensity) * mix(1.0, 0.55, clouds);
        float amb = max(0.02, uAmbient) * mix(1.0, 0.72, clouds);
        litAlbedo *= mix(1.0, 0.82, wet);
        litAlbedo = mix(litAlbedo, litAlbedo * vec3(0.86, 0.92, 0.96), wet * 0.35);
        litAlbedo = mix(litAlbedo, vec3(0.86, 0.89, 0.93), snow * mix(0.18, 0.42, card));
        float aoLit = mix(0.62, 1.0, aoFactor);
        float ambShadow = mix(0.42, 1.0, shadow);
        float night = clamp(uSunIntensity, 0.0, 1.0);
        float wrapDiff = mix(diffuse, diffuse * 0.35 + 0.08, 1.0 - night);

        vec3 F0 = mix(vec3(0.04), litAlbedo, metallic);
        vec3 direct = litAlbedo * (uDiffuseK * wrapDiff * sunI * mix(0.40, 1.0, shadow)) * lightCol;
        if (wet > 0.01)
        {
            vec3 specOnly = EvaluateCookTorrance(N, V, L, vec3(0.0), metallic, mix(roughness, 0.15, wet), F0, lightCol * uDiffuseK * sunI * atten);
            direct += specOnly * wet * 0.65 * shadow * specMask;
        }
        color = (litAlbedo * (amb * ambShadow) + direct) * aoLit;
    }
    else
    {
        // Cook-Torrance GGX PBR direct lighting (matches DeferredLighting.frag.glsl)
        vec3 F0 = mix(vec3(0.04) * specMask, litAlbedo, metallic);
        vec3 radiance = lightCol * max(uDiffuseK, 0.0) * atten;
        vec3 Lo = EvaluateCookTorrance(N, V, L, litAlbedo, metallic, roughness, F0, radiance) * shadow;

        // Additional point lights from LightingGlobals UBO (up to 4)
        for (int i = 0; i < 4; ++i)
        {
            if (i >= uPointCount) break;
            vec3 toP = uPointPosRange[i].xyz - vWorldPos;
            float pDist = length(toP);
            float pRange = max(uPointPosRange[i].w, 0.001);
            if (pDist < pRange)
            {
                vec3 pL = toP / max(pDist, 0.0001);
                float pt = pDist / pRange;
                float pAtten = 1.0 / (1.0 + pt * pt);
                vec3 pRad = uPointColor[i].rgb * max(uPointColor[i].a, 1.0) * pAtten;
                Lo += EvaluateCookTorrance(N, V, pL, litAlbedo, metallic, roughness, F0, pRad);
            }
        }

        // Specular IBL (ReflectionProbe cubemap or procedural sky-hemisphere fallback)
        vec3 R = reflect(-V, N);
        float NdotV = max(dot(N, V), 0.0);
        vec3 Fenv = FresnelSchlickRoughness(NdotV, F0, roughness);
        vec3 envSpec;
        if (uHasEnvMap != 0)
        {
            float mip = roughness * max(uEnvMaxMip, 0.0);
            envSpec = textureLod(uEnvMap, R, mip).rgb * Fenv * uEnvIntensity * specMask;
        }
        else
        {
            envSpec = ProceduralSkyIBL(R, roughness, uAmbient) * Fenv * (0.35 + 0.65 * metallic) * specMask;
        }

        vec3 ambientTerm;
        if (uHasPlanetFill == 1)
        {
            vec3 radial = normalize(vWorldPos - uPlanetCenter);
            float sky = clamp(dot(N, radial), 0.0, 1.0);
            float aoLift = mix(0.62, 1.0, clamp(aoFactor, 0.0, 1.0));
            Lo *= PI;
            ambientTerm = litAlbedo * uAmbient * aoLift * (0.90 + 0.55 * sky);
            color = ambientTerm + Lo + envSpec * aoLift;
        }
        else
        {
            float ambShadow = mix(0.35, 1.0, shadow);
            vec3 kD = (vec3(1.0) - Fenv) * (1.0 - metallic);
            ambientTerm = kD * litAlbedo * uAmbient * ambShadow;
            color = (ambientTerm + envSpec) * aoFactor + Lo;
        }
    }

    // Emissive (bypasses lighting & AO)
    vec3 emissive = uEmissiveColor * uEmissiveIntensity;
    if (uHasEmissiveTex == 1)
        emissive *= texture(uEmissiveTex, vUV).rgb;
    color += emissive;

    // Reinhard tone mapping (matches DeferredLighting.frag.glsl)
    color = color / (color + vec3(1.0));

    float alpha = (uTransparent != 0) ? maskA : 1.0;
    FragColor = vec4(color, alpha);
}
