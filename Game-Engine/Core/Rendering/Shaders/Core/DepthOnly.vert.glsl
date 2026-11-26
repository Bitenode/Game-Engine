#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 5) in vec4 aInstPosScale;
layout(location = 6) in vec4 aInstUpYaw;

#include "Common/Skinning.glsl"

uniform mat4 uMVP;
uniform int  uInstanced;

void main()
{
    vec3 pos = aPosition;
    ApplySkinningPositionOnly(pos);
    if (uInstanced == 1)
    {
        float instScale = aInstPosScale.w;
        if (instScale <= 0.0001)
        {
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            return;
        }
        vec3 up = normalize(aInstUpYaw.xyz);
        vec3 seed = abs(up.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
        vec3 side = normalize(cross(seed, up));
        vec3 fwd = normalize(cross(up, side));
        float cy = cos(aInstUpYaw.w);
        float sy = sin(aInstUpYaw.w);
        vec3 xAxis = side * cy + fwd * sy;
        vec3 zAxis = -side * sy + fwd * cy;
        pos = aInstPosScale.xyz
            + xAxis * (pos.x * instScale)
            + up    * (pos.y * instScale)
            + zAxis * (pos.z * instScale);
    }
    gl_Position = uMVP * vec4(pos, 1.0);
}
