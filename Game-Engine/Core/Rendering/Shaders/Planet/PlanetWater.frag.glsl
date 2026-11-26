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

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform vec3 uSkyColor;
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

float atmoPow(float x, float e)
{
    return x <= 0.0001 ? 0.0 : exp2(e * log2(x));
}

vec3 atmoSun(vec3 dir)
{
    float len = length(dir);
    return len > 0.0001 ? dir / len : vec3(0.0, 1.0, 0.0);
}

vec3 evalAtmosphere(vec3 worldPos, vec3 viewDir, vec3 radialDir)
{
    if (uAtmoEnabled == 0) return vec3(0.0);
    float altitude = max(length(worldPos - uPlanetCenter) - uPlanetRadius, 0.0);
    float atmoDepth = clamp(altitude / max(uAtmoHeight, 1.0), 0.0, 1.0);
    float density = exp(-atmoDepth * max(uAtmoDensityFalloff, 0.1));
    float horizon = atmoPow(clamp(1.0 - abs(dot(viewDir, radialDir)), 0.0, 1.0), 1.8) * max(uAtmoHorizonBlend, 0.0);
    vec3 sun = atmoSun(uAtmoSunDir);
    float sunForward = atmoPow(clamp(dot(viewDir, sun), 0.0, 1.0), 4.0);
    float sunset = atmoPow(1.0 - clamp(dot(radialDir, sun), 0.0, 1.0), 2.0) * max(uAtmoSunsetBoost, 0.0);
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
    // Keep a dusk floor so pond/lake/river tints do not crush to black at sundown.
    float lighting = max(uAmbient, 0.26) + NdotL * 0.55 * uDiffuseK;
    waterColor = max(waterColor, vec3(0.04, 0.14, 0.18));
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
