#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

#include "Common/FrameGlobals.glsl"

uniform mat4 uModel;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCloudBaseHeight;
uniform float uCloudTopHeight;

out vec3 vWorldPos;
out vec3 vWorldNormal;

void main()
{
    vec4 terrainWorldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(terrainWorldPos.xyz - uPlanetCenter);
    float shellHeight = max(0.0, 0.5 * (uCloudBaseHeight + uCloudTopHeight));
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + shellHeight);

    vWorldPos = shellPos;
    vWorldNormal = radialDir;
    gl_Position = uProj * uView * vec4(shellPos, 1.0);
}
