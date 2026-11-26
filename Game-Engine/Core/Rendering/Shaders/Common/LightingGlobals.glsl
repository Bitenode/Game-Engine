#pragma once

// Binding 1 — bound automatically by ShaderProgram.BindStandardUniformBlocks()
// (GL 3.3 Core & GLES 3.0 compatible: no layout(binding = N) qualifier)
layout(std140) uniform LightingGlobals
{
    vec4 uLightDirDiffuse;     // xyz = directional light dir, w = diffuseK
    vec4 uLightColorAmbient;   // xyz = light color, w = ambient
    vec4 uLightPosRange;       // xyz = primary point light pos, w = range
    vec4 uLightFlags;          // x = lightIsPoint, y = hasShadow, z = cascadeCount, w = pointCount
    mat4 uShadowVP;
    mat4 uShadowVPC[4];
    vec4 uCascadeSplits;
    vec4 uPointPosRange[4];    // xyz = pos, w = range
    vec4 uPointColor[4];       // rgb = color, a = intensity/unused
    vec4 uWindDirTime;         // xyz = wind dir, w = wind time
};

#define uLightDir     (uLightDirDiffuse.xyz)
#define uDiffuseK     (uLightDirDiffuse.w)
#define uLightColor   (uLightColorAmbient.xyz)
#define uAmbient      (uLightColorAmbient.w)
#define uLightPos     (uLightPosRange.xyz)
#define uLightRange   (uLightPosRange.w)
#define uLightIsPoint (int(uLightFlags.x + 0.5))
#define uHasShadow    (int(uLightFlags.y + 0.5))
#define uCascadeCount (int(uLightFlags.z + 0.5))
#define uPointCount   (int(uLightFlags.w + 0.5))
#define uWindDir      (uWindDirTime.xyz)
#define uWindTime     (uWindDirTime.w)
