#pragma once

#ifndef MAX_BONES
#define MAX_BONES 128
#endif

layout(location = 3) in vec4 aBoneIds;
layout(location = 4) in vec4 aBoneWeights;

uniform int uHasBones;                // 1 = apply bone skinning

// Binding 2 — bound automatically by ShaderProgram.BindStandardUniformBlocks()
layout(std140) uniform BoneGlobals
{
    mat4 uBones[MAX_BONES];           // bone final matrices
};

mat4 ComputeSkinMatrix()
{
    ivec4 ids = clamp(ivec4(aBoneIds), 0, MAX_BONES - 1);
    vec4  w   = aBoneWeights;
    return w.x * uBones[ids.x]
         + w.y * uBones[ids.y]
         + w.z * uBones[ids.z]
         + w.w * uBones[ids.w];
}

void ApplySkinning(inout vec3 pos, inout vec3 norm)
{
    if (uHasBones == 1)
    {
        mat4 skin = ComputeSkinMatrix();
        pos  = (skin * vec4(pos, 1.0)).xyz;
        norm = mat3(skin) * norm;
    }
}

void ApplySkinningPositionOnly(inout vec3 pos)
{
    if (uHasBones == 1)
    {
        mat4 skin = ComputeSkinMatrix();
        pos = (skin * vec4(pos, 1.0)).xyz;
    }
}
