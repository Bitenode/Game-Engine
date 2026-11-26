#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;

#include "Common/FrameGlobals.glsl"

uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCloudBaseHeight;
uniform float uCloudTopHeight;
uniform float uCloudCoverage;
uniform float uCloudDensity;
uniform float uCloudDetail;
uniform float uCloudSpeed;
uniform float uCloudSoftness;
uniform float uCloudLightResponse;
uniform float uCloudSilverLining;
uniform int uCloudStepCount;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform vec3 uSkyTint;
uniform float uTime;

out vec4 FragColor;

float hash31(vec3 p)
{
    return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453123);
}

float noise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hash31(i + vec3(0.0, 0.0, 0.0));
    float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
    float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
    float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
    float n111 = hash31(i + vec3(1.0, 1.0, 1.0));

    float nx00 = mix(n000, n100, u.x);
    float nx10 = mix(n010, n110, u.x);
    float nx01 = mix(n001, n101, u.x);
    float nx11 = mix(n011, n111, u.x);
    float nxy0 = mix(nx00, nx10, u.y);
    float nxy1 = mix(nx01, nx11, u.y);
    return mix(nxy0, nxy1, u.z);
}

float fbm(vec3 p)
{
    float value = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        value += noise3(p) * amp;
        p = p * 2.0 + vec3(17.0, 11.0, 7.0);
        amp *= 0.5;
    }
    return value;
}

void main()
{
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 L = normalize(uSunDir);

    float midHeight = max(1.0, 0.5 * (uCloudBaseHeight + uCloudTopHeight));
    vec3 shellPos = uPlanetCenter + radialDir * (uPlanetRadius + midHeight);

    vec3 wind = vec3(uTime * uCloudSpeed, 0.0, uTime * uCloudSpeed * 0.73);
    float baseN = fbm(shellPos * (0.0025 * uCloudDetail) + wind);
    float detailN = fbm(shellPos * (0.0080 * uCloudDetail) - wind * 2.1);
    float density = mix(baseN, detailN, 0.55);
    density = density * density;

    float threshold = mix(0.80, 0.25, clamp(uCloudCoverage, 0.0, 1.0));
    float edge = max(0.02, uCloudSoftness) * 0.45;
    float coverage = smoothstep(threshold - edge, threshold + edge, density);
    float cloudAlpha = coverage * clamp((density - threshold + edge) / max(edge * 2.0, 0.001), 0.0, 1.0);
    cloudAlpha *= clamp(uCloudDensity * 0.45, 0.0, 1.0);

    if (cloudAlpha < 0.01) discard;

    float sunFacing = clamp(dot(radialDir, L) * 0.5 + 0.5, 0.0, 1.0);
    float silverBase = clamp(1.0 - max(dot(V, L), 0.0), 0.0, 1.0);
    float silver = (silverBase <= 0.0001 ? 0.0 : exp2(6.0 * log2(silverBase))) * uCloudSilverLining;
    float lightTerm = (0.25 + sunFacing * 0.75) * uCloudLightResponse;
    vec3 cloudColor = mix(uSkyTint * 0.75, vec3(0.92, 0.94, 0.98), lightTerm);
    cloudColor += vec3(1.0, 0.97, 0.92) * silver * 0.35 * uSunIntensity;

    cloudColor *= step(cloudColor, cloudColor);
    FragColor = vec4(cloudColor, clamp(cloudAlpha, 0.0, 0.55));
}
