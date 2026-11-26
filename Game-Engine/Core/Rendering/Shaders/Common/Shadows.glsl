#pragma once

// 3x3 PCF shadow sampling on a single shadow map (minimum visibility 0.10)
float SampleShadowSingle(sampler2D shadowMap, vec2 uv, float compareDepth)
{
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float result = 0.0;
    for (int x = -1; x <= 1; ++x)
        for (int y = -1; y <= 1; ++y)
            result += (compareDepth > texture(shadowMap, uv + vec2(x, y) * texelSize).r) ? 0.0 : 1.0;
    return max(result / 9.0, 0.10);
}

// Smooth fade near shadow map borders
float ShadowEdgeFade(vec2 uv, float fadeMargin)
{
    float fadeX = smoothstep(0.0, fadeMargin, uv.x) * smoothstep(1.0, 1.0 - fadeMargin, uv.x);
    float fadeY = smoothstep(0.0, fadeMargin, uv.y) * smoothstep(1.0, 1.0 - fadeMargin, uv.y);
    return fadeX * fadeY;
}

#ifdef SHADOW_CASCADES
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#define MAX_CASCADES 4
uniform sampler2D uShadowMap;          // cascade 0 (backward compat)
uniform sampler2D uShadowMapC1;        // cascade 1
uniform sampler2D uShadowMapC2;        // cascade 2
uniform sampler2D uShadowMapC3;        // cascade 3

float SampleShadowMap(int cascade, vec2 uv, float compareDepth)
{
    if (cascade == 0) return SampleShadowSingle(uShadowMap, uv, compareDepth);
    if (cascade == 1) return SampleShadowSingle(uShadowMapC1, uv, compareDepth);
    if (cascade == 2) return SampleShadowSingle(uShadowMapC2, uv, compareDepth);
    return SampleShadowSingle(uShadowMapC3, uv, compareDepth);
}

int SelectShadowCascade(float viewDepth)
{
    int count = clamp(uCascadeCount, 1, MAX_CASCADES);
    int cascadeIdx = count - 1;
    for (int i = 0; i < MAX_CASCADES; i++)
    {
        if (i >= count) break;
        if (viewDepth < uCascadeSplits[i])
        {
            cascadeIdx = i;
            break;
        }
    }
    return cascadeIdx;
}

float SampleShadowCascadeLevel(int cascadeIdx, vec3 worldPos, vec3 geoN, vec3 sunDir, float baseBias, float normalOffsetScale)
{
    float cosTheta = max(dot(geoN, -sunDir), 0.0);
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float cascadeScale = 1.0 + float(cascadeIdx) * 0.65;
    vec3 offsetPos = worldPos + geoN * (normalOffsetScale * sinTheta * cascadeScale);

    vec4 sc = uShadowVPC[cascadeIdx] * vec4(offsetPos, 1.0);
    vec3 proj = sc.xyz / max(sc.w, 0.0001);
    proj = proj * 0.5 + 0.5;

    if (proj.z > 1.0 || proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;

    float bias = (baseBias + baseBias * 3.0 * (1.0 - cosTheta)) * (1.0 + float(cascadeIdx) * 0.35);
    float s = SampleShadowMap(cascadeIdx, proj.xy, proj.z - bias);
    float fade = ShadowEdgeFade(proj.xy, 0.06);
    return mix(1.0, s, fade);
}

// Unified cascaded shadow evaluation with view-space depth selection and smooth border blend
float SampleShadowCascaded(vec3 worldPos, vec3 geoN, vec3 sunDir, float baseBias, float normalOffsetScale)
{
    if (uHasShadow == 0) return 1.0;

    float viewDepth = max(-(uView * vec4(worldPos, 1.0)).z, 0.0);
    int count = clamp(uCascadeCount, 1, MAX_CASCADES);
    int cascadeIdx = SelectShadowCascade(viewDepth);

    float shadow = SampleShadowCascadeLevel(cascadeIdx, worldPos, geoN, sunDir, baseBias, normalOffsetScale);

    // Smooth cascade-border transition in the last 10% of the cascade slice
    if (cascadeIdx + 1 < count)
    {
        float splitFar = uCascadeSplits[cascadeIdx];
        float splitNear = (cascadeIdx == 0) ? 0.0 : uCascadeSplits[cascadeIdx - 1];
        float blendZone = max((splitFar - splitNear) * 0.10, 0.5);
        float blendStart = splitFar - blendZone;
        if (viewDepth > blendStart)
        {
            float t = clamp((viewDepth - blendStart) / blendZone, 0.0, 1.0);
            float nextShadow = SampleShadowCascadeLevel(cascadeIdx + 1, worldPos, geoN, sunDir, baseBias, normalOffsetScale);
            shadow = mix(shadow, nextShadow, t);
        }
    }

    return shadow;
}
#endif
