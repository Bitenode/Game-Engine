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

// MRT outputs
layout(location = 0) out vec4 gAlbedoMetallic;    // RT0: Albedo.rgb + Metallic
layout(location = 1) out vec4 gNormalRoughness;   // RT1: Normal.xyz + Roughness
layout(location = 2) out vec4 gEmissiveAO;        // RT2: Emissive.rgb + AO

#include "Common/NormalMapping.glsl"

void main()
{
    vec3 N = normalize(vWorldNormal);
    if (uDoubleSided != 0 && !gl_FrontFacing) N = -N;

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
