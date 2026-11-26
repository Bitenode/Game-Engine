#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

#include "Common/FrameGlobals.glsl"

uniform mat4 uModel;
uniform mat4 uNormalMatrix;

// Wave parameters
uniform float uTime;
uniform float uWaveAmp1;
uniform float uWaveFreq1;
uniform vec2  uWaveDir1;
uniform float uWaveSteep1;
uniform float uWaveAmp2;
uniform float uWaveFreq2;
uniform vec2  uWaveDir2;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);

    // Gerstner wave 1
    vec2 d1 = normalize(uWaveDir1);
    float dot1 = d1.x * worldPos.x + d1.y * worldPos.z;
    float phase1 = dot1 * uWaveFreq1 + uTime;
    worldPos.y += uWaveAmp1 * sin(phase1);
    worldPos.x += uWaveSteep1 * uWaveAmp1 * d1.x * cos(phase1);
    worldPos.z += uWaveSteep1 * uWaveAmp1 * d1.y * cos(phase1);

    // Gerstner wave 2
    vec2 d2 = normalize(uWaveDir2);
    float dot2 = d2.x * worldPos.x + d2.y * worldPos.z;
    float phase2 = dot2 * uWaveFreq2 + uTime * 0.7;
    worldPos.y += uWaveAmp2 * sin(phase2);

    // Compute normal from wave derivatives
    float nx = -(uWaveAmp1 * uWaveFreq1 * d1.x * cos(phase1) + uWaveAmp2 * uWaveFreq2 * d2.x * cos(phase2));
    float nz = -(uWaveAmp1 * uWaveFreq1 * d1.y * cos(phase1) + uWaveAmp2 * uWaveFreq2 * d2.y * cos(phase2));
    vec3 waveNormal = normalize(vec3(nx, 1.0, nz));

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(waveNormal, 0.0)).xyz);
    vUV = aUV;

    gl_Position = uProj * uView * worldPos;
}
