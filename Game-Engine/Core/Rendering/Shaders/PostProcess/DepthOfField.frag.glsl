#version 330 core

in vec2 vUV;

#include "Common/FrameGlobals.glsl"

uniform sampler2D uSceneColor;  // unit 0
uniform sampler2D uDepthTex;    // unit 1

uniform float uFocusDistance;
uniform float uAperture;
uniform float uFocalLength;
uniform float uMaxBlurRadius;
uniform float uNearBlurScale;
uniform float uFarBlurScale;
uniform vec2  uTexelSize;
uniform int   uPass;            // 0 = horizontal, 1 = vertical
uniform float uNear;
uniform float uFar;

out vec4 FragColor;

float LinearizeDepth(float d)
{
    float n = uNear > 0.0001 ? uNear : max(uNearPlane, 0.01);
    float f = uFar > n ? uFar : max(uFarPlane, n + 1.0);
    if (d >= 0.9999) return f;
    float z = d * 2.0 - 1.0;
    return (2.0 * n * f) / max(f + n - z * (f - n), 0.0001);
}

// Compute signed-magnitude Circle of Confusion radius in pixels
float ComputeCoC(float depth)
{
    float focalLengthM = max(uFocalLength, 1.0) * 0.001; // mm to meters
    float s1 = max(uFocusDistance, focalLengthM + 0.05);
    float s2 = max(depth, 0.01);
    float aperture = max(uAperture, 0.5);

    // Thin-lens CoC formula
    float coc = abs(focalLengthM * focalLengthM * (s2 - s1)) /
                max(aperture * s2 * (s1 - focalLengthM), 0.0001);

    coc = clamp(coc * 1000.0, 0.0, max(uMaxBlurRadius, 1.0));

    if (s2 < s1)
        coc *= max(uNearBlurScale, 0.0);
    else
        coc *= max(uFarBlurScale, 0.0);

    return coc;
}

void main()
{
    float centerDepth = texture(uDepthTex, vUV).r;
    vec3 centerColor = texture(uSceneColor, vUV).rgb;

    float linearDepth = LinearizeDepth(centerDepth);
    float centerCoC = ComputeCoC(linearDepth);

    if (centerCoC < 0.4)
    {
        FragColor = vec4(centerColor, 1.0);
        return;
    }

    vec2 dir = (uPass == 0) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);

    vec3 colorSum = centerColor;
    float weightSum = 1.0;
    float maxRadius = clamp(min(centerCoC, uMaxBlurRadius), 1.0, 16.0);
    int samples = int(maxRadius);

    // Constant upper bound (16) guarantees OpenGL ES 3.0 / ANGLE compatibility
    for (int i = 1; i <= 16; i++)
    {
        if (i > samples) break;

        float offset = float(i);
        float t = offset / (float(samples) + 1.0);
        float weight = (1.0 - t) * (1.0 - t);

        vec2 uv1 = clamp(vUV + dir * uTexelSize * offset, vec2(0.001), vec2(0.999));
        vec2 uv2 = clamp(vUV - dir * uTexelSize * offset, vec2(0.001), vec2(0.999));

        float d1 = LinearizeDepth(texture(uDepthTex, uv1).r);
        float d2 = LinearizeDepth(texture(uDepthTex, uv2).r);
        float coc1 = ComputeCoC(d1);
        float coc2 = ComputeCoC(d2);

        // Bleeding protection: don't let sharp foreground bleed into blurred background,
        // while still allowing blurred near-field to softly spread over background.
        float b1 = (d1 < linearDepth) ? smoothstep(0.0, 1.5, coc1) : smoothstep(0.0, 1.5, min(coc1, centerCoC));
        float b2 = (d2 < linearDepth) ? smoothstep(0.0, 1.5, coc2) : smoothstep(0.0, 1.5, min(coc2, centerCoC));

        float w1 = weight * b1;
        float w2 = weight * b2;

        colorSum += texture(uSceneColor, uv1).rgb * w1;
        colorSum += texture(uSceneColor, uv2).rgb * w2;
        weightSum += w1 + w2;
    }

    FragColor = vec4(colorSum / max(weightSum, 0.0001), 1.0);
}
