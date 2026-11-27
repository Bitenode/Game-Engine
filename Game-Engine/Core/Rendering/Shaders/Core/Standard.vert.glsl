#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 5) in vec4 aInstPosScale;
layout(location = 6) in vec4 aInstUpYaw;
layout(location = 7) in vec4 aInstTint;
layout(location = 8) in vec4 aInstMat3;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"
#include "Common/Skinning.glsl"

uniform mat4 uModel;
uniform mat4 uNormalMatrix;   // transpose(inverse(model))
uniform int  uInstanced;      // 0 = single, 1 = planet-aligned (posScale/upYaw), 2 = full mat4 (loc 5..8)

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

    vec4 worldPos;
    vec3 worldNorm;

    if (uInstanced == 2)
    {
        // Full 4x4 per-instance world matrix across locations 5..8 (column-major).
        mat4 instModel = mat4(aInstPosScale, aInstUpYaw, aInstTint, aInstMat3);
        worldPos = instModel * vec4(localPos, 1.0);
        worldNorm = normalize(transpose(inverse(mat3(instModel))) * localNorm);
    }
    else
    {
        if (uInstanced == 1)
        {
            float instScale = aInstPosScale.w;
            if (instScale <= 0.0001)
            {
                vWorldPos = vec3(0.0);
                vWorldNormal = vec3(0.0, 1.0, 0.0);
                vUV = aUV;
                vShadowCoord = vec4(0.0);
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

            localPos = aInstPosScale.xyz
                + xAxis * (localPos.x * instScale)
                + up    * (localPos.y * instScale)
                + zAxis * (localPos.z * instScale);
            localNorm = normalize(
                xAxis * localNorm.x
                + up  * localNorm.y
                + zAxis * localNorm.z);
        }

        worldPos = uModel * vec4(localPos, 1.0);
        worldNorm = normalize((uNormalMatrix * vec4(localNorm, 0.0)).xyz);
    }

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
    vWorldNormal = worldNorm;
    vUV = aUV;
    vShadowCoord = uShadowVP * worldPos;
    gl_Position = uProj * uView * worldPos;
}
