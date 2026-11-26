#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aMeta;          // x = 0 trunk / 1 canopy, y = vertical crown factor (0..1)

layout(location = 5) in vec4 aInstPosScale;  // xyz = planet-local root pos, w = uniform scale (<=0.001 = culled)
layout(location = 6) in vec4 aInstUpYaw;     // xyz = planet-local radial up, w = yaw radians
layout(location = 7) in vec4 aInstTint;      // rgb = biome foliage tint, a = fade seed [0..1]

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform mat4 uPlanetWorld;
uniform vec3 uNearAnchorLocal;
uniform vec2 uCrossfadeRange;                // x = inner fade radius (local), y = outer fade radius (local); y <= 0 disables crossfade
uniform float uWindStrength;
uniform float uRain;
uniform float uStorm;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec3 vTint;
out vec2 vMeta;

void main()
{
    float scale = aInstPosScale.w;
    if (scale <= 0.001)
    {
        vWorldPos = vec3(0.0);
        vWorldNormal = vec3(0.0, 1.0, 0.0);
        vTint = vec3(0.0);
        vMeta = vec2(0.0);
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    vec3 up = normalize(aInstUpYaw.xyz);
    mat3 planetRot = mat3(uPlanetWorld);
    vec3 worldRoot = (uPlanetWorld * vec4(aInstPosScale.xyz, 1.0)).xyz;
    vec3 worldUp = normalize(planetRot * up);
    vec3 toCam = normalize(uCamPos - worldRoot);

    // Horizon cull: skip trees on the far hemisphere of the planet.
    if (dot(worldUp, toCam) < -0.18)
    {
        vWorldPos = vec3(0.0);
        vWorldNormal = worldUp;
        vTint = vec3(0.0);
        vMeta = vec2(0.0);
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    // Coarse frustum cull on canopy center so off-screen trees never rasterize.
    vec3 worldCenter = worldRoot + worldUp * (scale * 0.5);
    vec4 clipCenter = uProj * uView * vec4(worldCenter, 1.0);
    float margin = max(scale * 1.6, 12.0);
    if (clipCenter.w < -margin
        || abs(clipCenter.x) > clipCenter.w + margin
        || abs(clipCenter.y) > clipCenter.w + margin)
    {
        vWorldPos = vec3(0.0);
        vWorldNormal = worldUp;
        vTint = vec3(0.0);
        vMeta = vec2(0.0);
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    // Tier 1 <-> Tier 2 smooth crossfade around the near-ring anchor.
    if (uCrossfadeRange.y > 0.0)
    {
        float distLocal = length(aInstPosScale.xyz - uNearAnchorLocal);
        float bandSpan = max(1.0, uCrossfadeRange.y - uCrossfadeRange.x);
        float jitter = (aInstTint.a - 0.5) * bandSpan * 0.28;
        float keep = smoothstep(uCrossfadeRange.x, uCrossfadeRange.y, distLocal + jitter);
        if (keep <= 0.01)
        {
            vWorldPos = vec3(0.0);
            vWorldNormal = worldUp;
            vTint = vec3(0.0);
            vMeta = vec2(0.0);
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            return;
        }
        scale *= keep;
    }

    // Planet-up tangent frame rotated by instance yaw.
    vec3 seed = abs(up.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 side = normalize(cross(seed, up));
    vec3 fwd = normalize(cross(up, side));
    float cy = cos(aInstUpYaw.w);
    float sy = sin(aInstUpYaw.w);
    vec3 xAxis = side * cy + fwd * sy;
    vec3 zAxis = -side * sy + fwd * cy;

    vec3 localPos = aInstPosScale.xyz
        + xAxis * (aPosition.x * scale)
        + up    * (aPosition.y * scale)
        + zAxis * (aPosition.z * scale);

    vec3 localNorm = normalize(
        xAxis * aNormal.x
        + up  * aNormal.y
        + zAxis * aNormal.z);

    vec4 worldPos = uPlanetWorld * vec4(localPos, 1.0);

    // Subtle wind sway on the upper canopy.
    float canopyFactor = aMeta.x * aMeta.y;
    if (canopyFactor > 0.01 && uWindStrength > 0.0001)
    {
        vec3 wdir = normalize(uWindDir + vec3(0.001, 0.0, 0.0));
        float phase = uWindTime * 0.75 + dot(aInstPosScale.xyz, vec3(0.09, 0.07, 0.11));
        float gust = sin(phase) * 0.7 + sin(phase * 1.63) * 0.3;
        float weatherKick = 1.0 + clamp(uRain, 0.0, 1.0) * 0.22 + clamp(uStorm, 0.0, 1.0) * 0.18;
        worldPos.xyz += wdir * (canopyFactor * canopyFactor * uWindStrength * weatherKick * gust * max(0.5, scale * 0.18));
    }

    vWorldPos = worldPos.xyz;
    vWorldNormal = normalize(planetRot * localNorm);
    vTint = aInstTint.rgb;
    vMeta = aMeta;
    gl_Position = uProj * uView * worldPos;
}
