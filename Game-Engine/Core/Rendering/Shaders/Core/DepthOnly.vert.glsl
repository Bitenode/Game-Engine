#version 330 core

layout(location = 0) in vec3 aPosition;

#include "Common/Skinning.glsl"

uniform mat4 uMVP;

void main()
{
    vec3 pos = aPosition;
    ApplySkinningPositionOnly(pos);
    gl_Position = uMVP * vec4(pos, 1.0);
}
