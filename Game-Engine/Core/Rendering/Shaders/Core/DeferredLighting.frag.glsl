#version 330 core

in vec2 vUV;

// G-buffer textures
uniform sampler2D gAlbedoMetallic;   // RT0
uniform sampler2D gNormalRoughness;  // RT1
uniform sampler2D gEmissiveAO;       // RT2
uniform sampler2D gDepth;            // Depth buffer

// Global UBOs + Shadow (cascaded shadow maps)
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#define SHADOW_CASCADES
#include "Common/Shadows.glsl"
#include "Common/PBR.glsl"

uniform float uShadowBias;
uniform vec3  uSunDir;

// Inverse view-projection for world position reconstruction
uniform mat4  uInvViewProj;          // inverse(proj * view)

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

// Optional reflection probe (specular IBL, unit 14)
uniform samplerCube uEnvMap;
uniform int         uHasEnvMap;
uniform float       uEnvIntensity;
uniform float       uEnvMaxMip;

// Ambient (uAmbient provided by LightingGlobals)
uniform int   uHasPlanetFill;
uniform vec3  uPlanetCenter;

// SSAO (optional, unit 4)
uniform sampler2D uSSAOTex;
uniform int       uHasSSAO;
uniform float     uSSAOIntensity;

// HDR output & rendering debug view
uniform int       uHdrOutput;     // 0 = apply Reinhard in-shader (LDR target), 1 = linear HDR output
uniform int       uDebugViewMode; // 0=Lit, 1=Wireframe, 2=Albedo, 3=Normals, 4=Roughness, 5=Metallic, 6=Depth, 7=SSAO, 8=CsmCascades

out vec4 FragColor;

// Reconstruct world position from depth
vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

float ShadowCalc(vec3 worldPos, vec3 N)
{
    return SampleShadowCascaded(worldPos, N, uSunDir, max(uShadowBias * 2.0, 0.002), 0.12);
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
    float ssaoVal = 1.0;
    if (uHasSSAO != 0)
    {
        ssaoVal = texture(uSSAOTex, vUV).r;
        ao *= mix(1.0, ssaoVal, clamp(uSSAOIntensity, 0.0, 1.0));
    }

    // Reconstruct world position
    vec3 worldPos = WorldPosFromDepth(depth, vUV);

    // Non-lit G-buffer / diagnostic debug views (early out before lighting)
    if (uDebugViewMode == 2) { FragColor = vec4(albedo, 1.0); return; }
    if (uDebugViewMode == 3) { FragColor = vec4(N * 0.5 + 0.5, 1.0); return; }
    if (uDebugViewMode == 4) { FragColor = vec4(vec3(roughness), 1.0); return; }
    if (uDebugViewMode == 5) { FragColor = vec4(vec3(metallic), 1.0); return; }
    if (uDebugViewMode == 6)
    {
        float viewDist = length(uCamPos - worldPos);
        float dVis = 1.0 - clamp(viewDist / 100.0, 0.0, 1.0);
        FragColor = vec4(vec3(dVis), 1.0);
        return;
    }
    if (uDebugViewMode == 7) { FragColor = vec4(vec3(ssaoVal), 1.0); return; }

    vec3 V = normalize(uCamPos - worldPos);

    // PBR: base reflectivity
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Shadow for primary light
    float shadow = ShadowCalc(worldPos, N);

    vec3 Lo = vec3(0.0);

    for (int i = 0; i < uDirLightCount && i < MAX_DIR_LIGHTS; i++)
    {
        vec3 L = uDirLightDirs[i];
        vec3 radiance = uDirLightColors[i] * uDirLightIntensities[i];
        float lightShadow = (i == 0) ? shadow : 1.0;
        Lo += EvaluateCookTorrance(N, V, L, albedo, metallic, roughness, F0, radiance) * lightShadow;
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

            float NdotL = max(dot(N, L), 0.0);
            if (NdotL <= 0.0) continue;

            Lo += EvaluateCookTorrance(N, V, L, albedo, metallic, roughness, F0, radianceBase * attenuation);
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
        envSpec = textureLod(uEnvMap, R, mip).rgb * Fenv * uEnvIntensity;
    }
    else
    {
        envSpec = ProceduralSkyIBL(R, roughness, uAmbient) * Fenv * (0.35 + 0.65 * metallic);
    }

    // Ambient. On a planet, match the terrain Lambert term: the cook-torrance
    // albedo/PI sun leaves props black beside the ground, and pack AO bakes
    // zero out the sky fill on wood and stone.
    vec3 ambient;
    if (uHasPlanetFill == 1)
    {
        vec3 radial = normalize(worldPos - uPlanetCenter);
        float sky = clamp(dot(N, radial), 0.0, 1.0);
        float aoLift = mix(0.62, 1.0, clamp(ao, 0.0, 1.0));
        Lo *= PI;
        ambient = albedo * uAmbient * aoLift * (0.90 + 0.55 * sky) + envSpec * aoLift;
    }
    else
    {
        float ambShadow = mix(0.35, 1.0, shadow);
        vec3 kD = (vec3(1.0) - Fenv) * (1.0 - metallic);
        ambient = (kD * vec3(uAmbient) * albedo * ambShadow + envSpec) * ao;
    }

    vec3 color = ambient + Lo + emissive;

    // CSM cascade boundary visualization
    if (uDebugViewMode == 8)
    {
        float viewDepth = max(-(uView * vec4(worldPos, 1.0)).z, 0.0);
        int cIdx = SelectShadowCascade(viewDepth);
        vec3 cCol = (cIdx == 0) ? vec3(0.2, 0.9, 0.2)
                  : (cIdx == 1) ? vec3(0.95, 0.9, 0.2)
                  : (cIdx == 2) ? vec3(1.0, 0.5, 0.15)
                                : vec3(0.95, 0.2, 0.2);
        color = mix(color, cCol, 0.45);
    }

    // Apply in-shader Reinhard only for LDR targets; HDR SceneFbo outputs linear radiance
    // for single-pass tone mapping in PostProcess.
    if (uHdrOutput == 0)
        color = color / (color + vec3(1.0));

    FragColor = vec4(color, 1.0);
}
