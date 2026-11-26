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

int SelectShadowCascade(float fragDist)
{
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
    return cascadeIdx;
}
#endif
