#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 3) in vec4 aBlendIndices;
layout(location = 4) in vec4 aBlendWeights;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform mat4 uModel;
uniform mat4 uNormalMatrix;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;
flat out ivec4 vBlendIdx;
out vec4 vBlendWt;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(aNormal, 0.0)).xyz);
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    vBlendIdx = ivec4(aBlendIndices);
    vBlendWt = aBlendWeights;
    gl_Position = uProj * uView * worldPos;
}
