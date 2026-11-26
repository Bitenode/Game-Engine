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

// Normals 0-4 always fit in a 16-unit budget. 5-7 and height maps are compiled in only when units remain.
#ifndef TERRAIN_NORMAL_LAYERS
#define TERRAIN_NORMAL_LAYERS 5
#endif
#ifndef TERRAIN_HEIGHT_LAYERS
#define TERRAIN_HEIGHT_LAYERS 0
#endif

uniform sampler2D uNormalLayer0;
uniform sampler2D uNormalLayer1;
uniform sampler2D uNormalLayer2;
uniform sampler2D uNormalLayer3;
uniform sampler2D uNormalLayer4;
#if TERRAIN_NORMAL_LAYERS > 5
uniform sampler2D uNormalLayer5;
#endif
#if TERRAIN_NORMAL_LAYERS > 6
uniform sampler2D uNormalLayer6;
#endif
#if TERRAIN_NORMAL_LAYERS > 7
uniform sampler2D uNormalLayer7;
#endif
uniform int uHasNormal0; uniform int uHasNormal1; uniform int uHasNormal2;
uniform int uHasNormal3; uniform int uHasNormal4;
uniform int uHasNormal5; uniform int uHasNormal6; uniform int uHasNormal7;

// Dedicated height maps. When a layer has none, or the context has no unit for it, parallax uses normal-map alpha.
#if TERRAIN_HEIGHT_LAYERS > 0
uniform sampler2D uHeightLayer0;
#endif
#if TERRAIN_HEIGHT_LAYERS > 1
uniform sampler2D uHeightLayer1;
#endif
#if TERRAIN_HEIGHT_LAYERS > 2
uniform sampler2D uHeightLayer2;
#endif
#if TERRAIN_HEIGHT_LAYERS > 3
uniform sampler2D uHeightLayer3;
#endif
#if TERRAIN_HEIGHT_LAYERS > 4
uniform sampler2D uHeightLayer4;
#endif
#if TERRAIN_HEIGHT_LAYERS > 5
uniform sampler2D uHeightLayer5;
#endif
#if TERRAIN_HEIGHT_LAYERS > 6
uniform sampler2D uHeightLayer6;
#endif
#if TERRAIN_HEIGHT_LAYERS > 7
uniform sampler2D uHeightLayer7;
#endif
uniform int uHasHeight0; uniform int uHasHeight1; uniform int uHasHeight2; uniform int uHasHeight3;
uniform int uHasHeight4; uniform int uHasHeight5; uniform int uHasHeight6; uniform int uHasHeight7;

uniform float uParallaxStrength;

// Global UBOs (uCamPos, uLightDir, uLightPos, uLightRange, uLightIsPoint, uDiffuseK, uAmbient, uHasShadow)
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

// Shadow
uniform sampler2D uShadowMap;
uniform float     uShadowBias;
uniform vec3      uSunDir;

// Fallback (no layers defined) — no extra albedo sampler (keeps fragment stage <= 16 texture units on GLES/ANGLE)
uniform vec4  uBaseColor;

out vec4 FragColor;

#include "Common/Shadows.glsl"

float ShadowCalc(vec4 sc, vec3 N)
{
    if (uHasShadow == 0) return 1.0;
    vec3 proj = sc.xyz / sc.w;
    proj = proj * 0.5 + 0.5;
    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    float cosTheta = max(dot(N, -uSunDir), 0.0);
    float bias = uShadowBias + uShadowBias * 3.0 * (1.0 - cosTheta);
    float currentDepth = proj.z - bias;

    float shadow = SampleShadowSingle(uShadowMap, proj.xy, currentDepth);

    float fade = ShadowEdgeFade(proj.xy, 0.08);
    return mix(1.0, shadow, fade);
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

float layerTiling(int i)
{
    if (i == 0) return uTiling0;
    if (i == 1) return uTiling1;
    if (i == 2) return uTiling2;
    if (i == 3) return uTiling3;
    if (i == 4) return uTiling4;
    if (i == 5) return uTiling5;
    if (i == 6) return uTiling6;
    return uTiling7;
}

bool layerHasNormal(int i)
{
    if (i >= TERRAIN_NORMAL_LAYERS) return false;
    if (i == 0) return uHasNormal0 != 0;
    if (i == 1) return uHasNormal1 != 0;
    if (i == 2) return uHasNormal2 != 0;
    if (i == 3) return uHasNormal3 != 0;
    if (i == 4) return uHasNormal4 != 0;
    if (i == 5) return uHasNormal5 != 0;
    if (i == 6) return uHasNormal6 != 0;
    return uHasNormal7 != 0;
}

vec4 sampleNormal(int i, vec2 uv)
{
    if (i == 0) return texture(uNormalLayer0, uv);
    if (i == 1) return texture(uNormalLayer1, uv);
    if (i == 2) return texture(uNormalLayer2, uv);
    if (i == 3) return texture(uNormalLayer3, uv);
    if (i == 4) return texture(uNormalLayer4, uv);
#if TERRAIN_NORMAL_LAYERS > 5
    if (i == 5) return texture(uNormalLayer5, uv);
#endif
#if TERRAIN_NORMAL_LAYERS > 6
    if (i == 6) return texture(uNormalLayer6, uv);
#endif
#if TERRAIN_NORMAL_LAYERS > 7
    if (i == 7) return texture(uNormalLayer7, uv);
#endif
    return vec4(0.5, 0.5, 1.0, 1.0);
}

bool layerHasHeight(int i)
{
#if TERRAIN_HEIGHT_LAYERS == 0
    return false;
#else
    if (i >= TERRAIN_HEIGHT_LAYERS) return false;
    if (i == 0) return uHasHeight0 != 0;
    if (i == 1) return uHasHeight1 != 0;
    if (i == 2) return uHasHeight2 != 0;
    if (i == 3) return uHasHeight3 != 0;
    if (i == 4) return uHasHeight4 != 0;
    if (i == 5) return uHasHeight5 != 0;
    if (i == 6) return uHasHeight6 != 0;
    return uHasHeight7 != 0;
#endif
}

float sampleHeight(int i, vec2 uv)
{
#if TERRAIN_HEIGHT_LAYERS > 0
    if (i == 0) return texture(uHeightLayer0, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 1
    if (i == 1) return texture(uHeightLayer1, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 2
    if (i == 2) return texture(uHeightLayer2, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 3
    if (i == 3) return texture(uHeightLayer3, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 4
    if (i == 4) return texture(uHeightLayer4, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 5
    if (i == 5) return texture(uHeightLayer5, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 6
    if (i == 6) return texture(uHeightLayer6, uv).r;
#endif
#if TERRAIN_HEIGHT_LAYERS > 7
    if (i == 7) return texture(uHeightLayer7, uv).r;
#endif
    return 0.0;
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

        // Parallax from a dedicated height map, or normal-map alpha when that map is empty.
        float hAcc = 0.0;
        float hW = 0.0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-5) continue;
            vec2 uvi = vUV * layerTiling(i);
            float hSample = 0.0;
            bool gotH = false;
            if (layerHasHeight(i))
            {
                hSample = sampleHeight(i, uvi);
                gotH = true;
            }
            else if (layerHasNormal(i))
            {
                hSample = sampleNormal(i, uvi).a;
                gotH = true;
            }
            if (!gotH) continue;
            hAcc += hSample * wi;
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

        // Blended tangent-space normal. Layers without a map stay flat.
        vec3 tSum = vec3(0.0);
        float nW = 0.0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-6) continue;
            vec3 tn = vec3(0.0, 0.0, 1.0);
            if (layerHasNormal(i))
            {
                vec2 uvi = uvDetail * layerTiling(i);
                tn = sampleNormal(i, uvi).rgb * 2.0 - 1.0;
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
