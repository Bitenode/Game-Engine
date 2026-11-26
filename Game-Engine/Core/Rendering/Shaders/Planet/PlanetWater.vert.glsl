#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

#include "Common/FrameGlobals.glsl"

uniform mat4 uModel;
uniform mat4 uNormalMatrix;

uniform vec3 uPlanetCenter;
uniform float uTime;
uniform float uWaveAmp1;
uniform float uWaveFreq1;
uniform float uWaveSteep1;
uniform float uWaveAmp2;
uniform float uWaveFreq2;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vec3 radialDir = normalize(worldPos.xyz - uPlanetCenter);

    vec3 up = abs(radialDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent   = normalize(cross(up, radialDir));
    vec3 bitangent = cross(radialDir, tangent);

    float tCoord = dot(worldPos.xyz, tangent);
    float bCoord = dot(worldPos.xyz, bitangent);

    float phase1 = (tCoord * 0.7 + bCoord * 0.7) * uWaveFreq1 + uTime;
    float s1 = sin(phase1);
    float c1 = cos(phase1);

    float phase2 = (tCoord * -0.5 + bCoord * 0.86) * uWaveFreq2 + uTime * 0.8;
    float s2 = sin(phase2);
    float c2 = cos(phase2);

    worldPos.xyz += radialDir * (uWaveAmp1 * s1 + uWaveAmp2 * s2);
    worldPos.xyz += tangent * uWaveSteep1 * uWaveAmp1 * c1;

    vec3 waveNormal = normalize(
        radialDir
        - tangent   * (uWaveFreq1 * uWaveAmp1 * c1 * 0.7 + uWaveFreq2 * uWaveAmp2 * c2 * -0.5)
        - bitangent * (uWaveFreq1 * uWaveAmp1 * c1 * 0.7 + uWaveFreq2 * uWaveAmp2 * c2 * 0.86)
    );

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(waveNormal, 0.0)).xyz);
    vUV = aUV;
    gl_Position = uProj * uView * worldPos;
}
