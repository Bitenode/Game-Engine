#version 330 core

layout(location = 0) in vec2 aPosition;    // billboard quad corner (-0.5..0.5)

#include "Common/FrameGlobals.glsl"

// Per-instance data (passed as uniforms per batch)
uniform vec4 uParticlePos[128];   // xyz = world position, w = size / streak width
uniform vec4 uParticleCol[128];   // rgba color
uniform int  uAlignVelocity;
uniform int  uParticleLook;       // 0 soft circle, 1 rain streak, 2 flame
uniform float uStretchLength;
uniform vec3 uFallDir;

out vec4 vColor;
out vec2 vUV;
flat out int vInstanceID;

void main()
{
    int id = gl_InstanceID;
    vInstanceID = id;

    vec3 worldPos = uParticlePos[id].xyz;
    float size = uParticlePos[id].w;
    vColor = uParticleCol[id];
    vUV = aPosition + 0.5;

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
        corner = worldPos + (aPosition.x * camRight + aPosition.y * camUp) * size;
    }
    gl_Position = uProj * uView * vec4(corner, 1.0);
}
