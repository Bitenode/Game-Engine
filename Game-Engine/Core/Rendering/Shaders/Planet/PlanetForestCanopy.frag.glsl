#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec3 vTint;
in vec2 vMeta; // x = 0 trunk / 1 canopy, y = vertical crown factor (0..1)

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform float uSunIntensity;
uniform float uWetness;
uniform float uSnow;
uniform float uCloudiness;
uniform int   uHdrOutput;

out vec4 FragColor;

void main()
{
    vec3 N = normalize(vWorldNormal);
    // Same convention as planet terrain & grass: uLightDir is FROM the sun.
    vec3 L = normalize(-uLightDir);
    float nDotL = max(dot(N, L), 0.0);

    // Day: soft wrap diffuse for lush foliage volume. Night: collapse to true N.L.
    float day = clamp(uSunIntensity, 0.0, 1.0);
    float wrap = mix(nDotL, nDotL * 0.62 + 0.38, day);
    float back = max(dot(-N, L), 0.0) * 0.16 * day * vMeta.x;
    float diffuse = min(wrap + back, 1.15);

    float clouds = clamp(uCloudiness, 0.0, 1.0);
    float sunI = max(0.02, uSunIntensity) * mix(1.0, 0.55, clouds);
    float amb = max(0.02, uAmbient) * mix(1.0, 0.72, clouds);

    // Stylized layered foliage shading: warm dark bark on trunk, sunlit gradient on crown.
    vec3 trunkColor = vec3(0.24, 0.17, 0.11);
    float crownShade = mix(0.62, 1.16, clamp(vMeta.y, 0.0, 1.0));
    vec3 canopyColor = vTint * crownShade;
    vec3 baseColor = mix(trunkColor, canopyColor, clamp(vMeta.x, 0.0, 1.0));

    float wet = clamp(uWetness, 0.0, 1.0);
    baseColor *= mix(1.0, 0.84, wet);
    baseColor = mix(baseColor, baseColor * vec3(0.88, 0.93, 0.97), wet * 0.25);

    float snowMask = clamp(uSnow, 0.0, 1.0) * vMeta.x * smoothstep(0.35, 0.92, vMeta.y);
    baseColor = mix(baseColor, vec3(0.86, 0.89, 0.94), snowMask);

    vec3 sunTint = (dot(uLightColor, uLightColor) > 0.0001) ? uLightColor : vec3(1.0, 0.96, 0.88);
    vec3 lit = baseColor * (amb + sunTint * (uDiffuseK * diffuse * sunI));

    // Subtle specular sheen when wet on upper canopy.
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 18.0) * wet * vMeta.x * vMeta.y * 0.14;
    lit += sunTint * sunI * spec;

    if (uHdrOutput == 0)
        lit = lit / (lit + vec3(1.0));

    FragColor = vec4(lit, 1.0);
}
