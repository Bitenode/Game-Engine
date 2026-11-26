#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aInstPosScale;
layout(location = 3) in vec4 aInstUpYaw;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform mat4 uPlanetWorld;
uniform float uWindStrength;
uniform float uRain;
uniform float uStorm;
uniform float uCardWidth;

out vec2 vUV;
out vec3 vWorldPos;
out vec3 vWorldNormal;
out float vHeight;

void main()
{
    vec3 up = normalize(aInstUpYaw.xyz);
    vec3 seed = abs(up.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 side = normalize(cross(seed, up));
    vec3 fwd = normalize(cross(up, side));
    float cy = cos(aInstUpYaw.w);
    float sy = sin(aInstUpYaw.w);
    vec3 xAxis = side * cy + fwd * sy;
    vec3 zAxis = -side * sy + fwd * cy;

    float h = max(0.05, aInstPosScale.w);
    float w = h * max(0.25, uCardWidth);
    vec3 local = aInstPosScale.xyz
        + xAxis * (aPosition.x * w)
        + up * (aPosition.y * h)
        + zAxis * (aPosition.z * w);

    float tip = clamp(aPosition.y, 0.0, 1.0);
    vec4 world = uPlanetWorld * vec4(local, 1.0);
    vec3 wdir = normalize(uWindDir + vec3(0.001, 0.0, 0.0));
    float phase = uWindTime * 1.15 + dot(aInstPosScale.xyz, vec3(0.19, 0.11, 0.17));
    float gust = sin(phase) * 0.72 + sin(phase * 1.73 + tip * 2.4) * 0.28;
    // Soft breeze — rain used to add ~2.4x weatherKick and look like a gale.
    float weatherKick = 1.0 + uRain * 0.28 + uStorm * 0.18;
    world.xyz += wdir * (tip * tip * uWindStrength * weatherKick * gust);
    world.xyz += wdir.zxy * (tip * uRain * 0.03 * sin(phase * 4.1));

    vWorldPos = world.xyz;
    vWorldNormal = normalize(mat3(uPlanetWorld) * up);
    vUV = aUV;
    vHeight = tip;
    gl_Position = uProj * uView * world;
}
