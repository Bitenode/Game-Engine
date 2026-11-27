#version 330 core

layout(location = 0) in vec2 aPosition;     // billboard quad corner (-0.5..0.5)
layout(location = 1) in vec4 aInstPosSize;  // xyz = world position, w = size / streak width
layout(location = 2) in vec4 aInstColor;    // rgba color
layout(location = 3) in vec4 aInstParams;   // x = rotation (rad), y = normalized age (0..1), z = flipbook cols, w = flipbook rows

#include "Common/FrameGlobals.glsl"

uniform int   uAlignVelocity;
uniform int   uParticleLook;       // 0 soft circle / textured billboard, 1 rain streak, 2 flame
uniform float uStretchLength;
uniform vec3  uFallDir;

out vec4 vColor;
out vec2 vUV;
out vec2 vQuadUV;
flat out int vInstanceID;

void main()
{
    vInstanceID = gl_InstanceID;

    vec3 worldPos = aInstPosSize.xyz;
    float size    = aInstPosSize.w;
    vColor        = aInstColor;

    vec2 baseUV = aPosition + 0.5;
    vQuadUV = baseUV;

    // Optional sprite-sheet flipbook UV remapping (cols x rows)
    float cols = max(1.0, aInstParams.z);
    float rows = max(1.0, aInstParams.w);
    float totalFrames = cols * rows;
    if (totalFrames > 1.5)
    {
        float age01 = clamp(aInstParams.y, 0.0, 0.9999);
        float frameIdx = floor(age01 * totalFrames);
        float col = mod(frameIdx, cols);
        float row = floor(frameIdx / cols);
        vUV = vec2((col + baseUV.x) / cols, (row + baseUV.y) / rows);
    }
    else
    {
        vUV = baseUV;
    }

    vec3 camRight = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 camUp    = vec3(uView[0][1], uView[1][1], uView[2][1]);
    vec3 corner;
    if (uParticleLook == 2)
    {
        // Base sits on the particle. The tip rises along the flame axis.
        vec3 axis = uFallDir;
        float axisLen = length(axis);
        if (axisLen > 1e-5)
            axis /= axisLen;
        else
            axis = vec3(0.0, 1.0, 0.0);
        vec3 viewFwd = vec3(uView[0][2], uView[1][2], uView[2][2]);
        vec3 side = cross(axis, viewFwd);
        if (dot(side, side) < 1e-6)
            side = camRight;
        else
            side = normalize(side);
        float y01 = aPosition.y + 0.5;
        float taper = mix(1.0, 0.14, y01 * y01);
        float height = max(size * 1.15, 0.28);
        corner = worldPos + side * (aPosition.x * size * 1.45 * taper) + axis * (y01 * height);
    }
    else if (uAlignVelocity != 0)
    {
        vec3 fall = uFallDir;
        float fallLen = length(fall);
        if (fallLen > 1e-5)
            fall /= fallLen;
        else
            fall = vec3(0.0, -1.0, 0.0);
        vec3 side = cross(fall, camRight);
        if (dot(side, side) < 1e-6)
            side = camRight;
        else
            side = normalize(side);
        float len = max(0.08, uStretchLength);
        corner = worldPos + (aPosition.x * side * size + aPosition.y * fall * len);
    }
    else
    {
        float rot = aInstParams.x;
        float c = cos(rot);
        float s = sin(rot);
        vec2 localPos = vec2(
            aPosition.x * c - aPosition.y * s,
            aPosition.x * s + aPosition.y * c);
        corner = worldPos + (localPos.x * camRight + localPos.y * camUp) * size;
    }
    gl_Position = uProj * uView * vec4(corner, 1.0);
}
