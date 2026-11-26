#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#include "Common/Skinning.glsl"

uniform mat4 uModel;
uniform mat4 uNormalMatrix;   // transpose(inverse(model))

// Wind / vegetation animation
uniform float uWindStrength;
uniform int   uIsVegetation;  // 1 = apply wind displacement
uniform float uRain;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vUV;
out vec4 vShadowCoord;

void main()
{
    vec3 localPos  = aPosition;
    vec3 localNorm = aNormal;

    ApplySkinning(localPos, localNorm);

    vec4 worldPos = uModel * vec4(localPos, 1.0);

    // Wind vertex animation for vegetation (slow canopy sway + light leaf flutter)
    if (uIsVegetation == 1 && uWindStrength > 0.0)
    {
        float localY = max(aPosition.y, 0.0);
        float h = clamp(localY / 5.0, 0.0, 1.0);
        float side = clamp(length(aPosition.xz) / 3.2, 0.0, 1.0);
        float leaf = max(h, side * 0.7);
        float tip = leaf * leaf;
        float weatherKick = 1.0 + clamp(uRain, 0.0, 1.0) * 0.25;
        vec3 wdir = normalize(uWindDir + vec3(0.001, 0.0, 0.0));

        float phase1 = uWindTime * 0.48 + worldPos.x * 0.07 + worldPos.z * 0.05;
        vec3 trunkSway = wdir * uWindStrength * tip * sin(phase1) * weatherKick;

        float phase2 = uWindTime * 1.15 + dot(worldPos.xyz, vec3(0.19, 0.11, 0.16));
        vec3 flutter = wdir.zxy * uWindStrength * 0.32 * leaf * sin(phase2) * weatherKick;

        float phase3 = uWindTime * 1.85 + worldPos.x * 0.37 - worldPos.z * 0.29;
        flutter += vec3(0.0, 1.0, 0.0) * uWindStrength * 0.12 * leaf * sin(phase3);

        worldPos.xyz += trunkSway + flutter;
    }

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize((uNormalMatrix * vec4(localNorm, 0.0)).xyz);
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    gl_Position = uProj * uView * worldPos;
}
