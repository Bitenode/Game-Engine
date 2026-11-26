#version 330 core

layout(location = 0) in vec3 aPosition;

#include "Common/FrameGlobals.glsl"

uniform mat4 uModel;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uAtmosphereHeight;

out vec3 vWorldPos;
out vec3 vRadialDir;

void main()
{
    vec4 terrainWorldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(terrainWorldPos.xyz - uPlanetCenter);
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + max(uAtmosphereHeight, 1.0));
    vWorldPos = shellPos;
    vRadialDir = radialDir;
    gl_Position = uProj * uView * vec4(shellPos, 1.0);
}
