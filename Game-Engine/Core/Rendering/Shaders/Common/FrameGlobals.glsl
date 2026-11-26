#pragma once

// Binding 0 — bound automatically by ShaderProgram.BindStandardUniformBlocks()
// (GL 3.3 Core & GLES 3.0 compatible: no layout(binding = N) qualifier)
layout(std140) uniform FrameGlobals
{
    mat4 uView;
    mat4 uProj;
    mat4 uViewProj;
    mat4 uInvView;
    mat4 uInvProj;
    vec4 uCamPosTime;      // xyz = camera world pos, w = elapsed time
    vec4 uScreenNearFar;   // xy = viewport width/height, z = near, w = far
};

#define uCamPos     (uCamPosTime.xyz)
#define uFrameTime  (uCamPosTime.w)
#define uScreenSize (uScreenNearFar.xy)
#define uNearPlane  (uScreenNearFar.z)
#define uFarPlane   (uScreenNearFar.w)
