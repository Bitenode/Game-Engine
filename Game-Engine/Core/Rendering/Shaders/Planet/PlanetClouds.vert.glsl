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
    vec4 worldBase = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(worldBase.xyz - uPlanetCenter);
    float rTop = uPlanetRadius + max(uCloudTopHeight, uCloudBaseHeight + 1.0);
    // Slight 0.5% padding so the faceted bounding sphere encloses the analytical outer shell cleanly.
    vec3 shellPos = uPlanetCenter + radialDir * (rTop * 1.005);

    vWorldPos = shellPos;
    vWorldNormal = radialDir;
    gl_Position = uProj * uView * vec4(shellPos, 1.0);
}
