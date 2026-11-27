#version 330 core
#ifdef GL_ES
precision highp sampler2DArray;
#endif

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;

// Texture-unit layout (8 total units: 0, 1, 2, 3, 7, 8, 9, 10):
//   unit 0  : uSplatmap0     (sampler2D, layers 0-3)
//   unit 1  : uSplatmap1     (sampler2D, layers 4-7)
//   unit 2  : uLayerAlbedos  (sampler2DArray, 8 layers)
//   unit 3  : uLayerNormals  (sampler2DArray, 8 layers)
//   unit 7..10 : uShadowMap, uShadowMapC1..C3 (sampler2D, 4-cascade CSM)
uniform sampler2D      uSplatmap0;    // layers 0-3
uniform sampler2D      uSplatmap1;    // layers 4-7
uniform int            uLayerCount;   // how many layers are active (0-8)

uniform sampler2DArray uLayerAlbedos; // 8 albedo layers
uniform sampler2DArray uLayerNormals; // 8 tangent-space normal layers (alpha = optional parallax height)

uniform float uTiling0; uniform float uTiling1; uniform float uTiling2; uniform float uTiling3;
uniform float uTiling4; uniform float uTiling5; uniform float uTiling6; uniform float uTiling7;

// Per-layer PBR scalars (blended by splat weights)
uniform float uRough0; uniform float uRough1; uniform float uRough2; uniform float uRough3;
uniform float uRough4; uniform float uRough5; uniform float uRough6; uniform float uRough7;
uniform float uMetal0; uniform float uMetal1; uniform float uMetal2; uniform float uMetal3;
uniform float uMetal4; uniform float uMetal5; uniform float uMetal6; uniform float uMetal7;

uniform int uHasNormal0; uniform int uHasNormal1; uniform int uHasNormal2; uniform int uHasNormal3;
uniform int uHasNormal4; uniform int uHasNormal5; uniform int uHasNormal6; uniform int uHasNormal7;

uniform float uParallaxStrength;

// Global UBOs + 4-cascade CSM shadows (units 7..10)
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#define SHADOW_CASCADES
#include "Common/Shadows.glsl"

uniform float uShadowBias;
uniform vec3  uSunDir;

// Fallback (no layers defined) & HDR output flag
uniform vec4  uBaseColor;
uniform int   uHdrOutput;

out vec4 FragColor;

float ShadowCalc(vec3 geoN)
{
    return SampleShadowCascaded(vWorldPos, geoN, uSunDir, max(uShadowBias, 0.002), 0.04);
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
    if (i == 0) return uHasNormal0 != 0;
    if (i == 1) return uHasNormal1 != 0;
    if (i == 2) return uHasNormal2 != 0;
    if (i == 3) return uHasNormal3 != 0;
    if (i == 4) return uHasNormal4 != 0;
    if (i == 5) return uHasNormal5 != 0;
    if (i == 6) return uHasNormal6 != 0;
    return uHasNormal7 != 0;
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

        // Parallax from normal-map alpha for any active normal-mapped layer
        float hAcc = 0.0;
        float hW = 0.0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-5 || !layerHasNormal(i)) continue;
            vec2 uvi = vUV * layerTiling(i);
            float hSample = texture(uLayerNormals, vec3(uvi, float(i))).a;
            hAcc += hSample * wi;
            hW += wi;
        }
        if (hW > 1e-4 && uParallaxStrength > 0.0)
        {
            float h = hAcc / hW;
            vec2 pdir = Vts.xy / max(abs(Vts.z), 0.15);
            uvDetail = vUV - pdir * (h * uParallaxStrength);
        }

        // Blended albedo from 2D texture array (detail UV)
        albedo = vec4(0.0);
        for (int i = 0; i < 8; i++)
        {
            if (i >= uLayerCount) break;
            float wi = layerW(i, s0, s1);
            if (wi < 1e-6) continue;
            albedo += texture(uLayerAlbedos, vec3(uvDetail * layerTiling(i), float(i))) * wi;
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

        // Blended tangent-space normal across all 8 layers. Layers without a map stay flat.
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
                tn = texture(uLayerNormals, vec3(uvi, float(i))).rgb * 2.0 - 1.0;
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

    float shadow = ShadowCalc(Ngeom);

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
    vec3 color = albedo.rgb * shade + vec3(specular * shadow);
    if (uHdrOutput == 0)
        color = clamp(color, 0.0, 1.0);

    FragColor = vec4(color, 1.0);
}
