#version 330 core

in vec2 vUV;

uniform sampler2D uSceneColor;
uniform sampler2D gDepth;

uniform mat4  uInvViewProj;
uniform vec3  uCamPos;
uniform float uFocusDistance;
uniform float uAperture;
uniform float uFocalLength;
uniform float uMaxBlurRadius;
uniform float uNearBlurScale;
uniform float uFarBlurScale;
uniform vec2  uTexelSize;
uniform int   uPass;       // 0 = horizontal, 1 = vertical

// Camera near/far for linearizing depth
uniform float uNear;
uniform float uFar;

out vec4 FragColor;

float LinearizeDepth(float d)
{
    float z = d * 2.0 - 1.0;
    return (2.0 * uNear * uFar) / (uFar + uNear - z * (uFar - uNear));
}

// Compute Circle of Confusion diameter
float ComputeCoC(float depth)
{
    float focalLengthM = uFocalLength * 0.001; // mm to meters
    float s1 = uFocusDistance;
    float s2 = depth;

    // Thin lens CoC formula
    float coc = abs(focalLengthM * focalLengthM * (s2 - s1)) /
                (uAperture * s2 * (s1 - focalLengthM));

    // Scale to pixel radius and clamp
    coc = coc * 1000.0; // scale to visible range
    coc = clamp(coc, 0.0, uMaxBlurRadius);

    // Apply near/far scaling
    if (s2 < s1)
        coc *= uNearBlurScale;
    else
        coc *= uFarBlurScale;

    return coc;
}

void main()
{
    float depth = texture(gDepth, vUV).r;
    vec3 centerColor = texture(uSceneColor, vUV).rgb;

    if (depth >= 1.0)
    {
        FragColor = vec4(centerColor, 1.0);
        return;
    }

    float linearDepth = LinearizeDepth(depth);
    float coc = ComputeCoC(linearDepth);

    if (coc < 0.5)
    {
        FragColor = vec4(centerColor, 1.0);
        return;
    }

    // Direction: horizontal (pass 0) or vertical (pass 1)
    vec2 dir = (uPass == 0) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);

    // Variable-width Gaussian blur based on CoC
    vec3 colorSum = centerColor;
    float weightSum = 1.0;
    int samples = int(min(coc, uMaxBlurRadius));
    samples = max(samples, 1);

    for (int i = 1; i <= samples; i++)
    {
        float offset = float(i);
        float weight = 1.0 - (offset / (float(samples) + 1.0));
        weight *= weight; // quadratic falloff

        vec2 uv1 = vUV + dir * uTexelSize * offset;
        vec2 uv2 = vUV - dir * uTexelSize * offset;

        // Sample neighbor CoC to prevent sharp objects bleeding into blurred areas
        float d1 = texture(gDepth, uv1).r;
        float d2 = texture(gDepth, uv2).r;
        float coc1 = ComputeCoC(LinearizeDepth(d1));
        float coc2 = ComputeCoC(LinearizeDepth(d2));

        // Only blur if the neighbor also wants to be blurred
        float w1 = weight * smoothstep(0.0, 2.0, coc1);
        float w2 = weight * smoothstep(0.0, 2.0, coc2);

        colorSum += texture(uSceneColor, uv1).rgb * w1;
        colorSum += texture(uSceneColor, uv2).rgb * w2;
        weightSum += w1 + w2;
    }

    FragColor = vec4(colorSum / weightSum, 1.0);
}
