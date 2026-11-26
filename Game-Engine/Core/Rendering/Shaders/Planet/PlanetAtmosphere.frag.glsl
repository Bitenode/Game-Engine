#version 330 core

in vec3 vWorldPos;
in vec3 vRadialDir;

#include "Common/FrameGlobals.glsl"

uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uAtmosphereHeight;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform float uAtmoBlend;
uniform float uRayleighStrength;
uniform float uMieStrength;
uniform float uDensityFalloff;
uniform float uHorizonBlend;
uniform float uSunsetBoost;
uniform vec3 uZenithTint;
uniform vec3 uHorizonTint;

out vec4 FragColor;

float atmoPow(float x, float e)
{
    // Xbox ANGLE compiles pow() to HLSL pow. pow(0, 2.1) is NaN there and blanks the sky.
    return x <= 0.0001 ? 0.0 : exp2(e * log2(x));
}

void main()
{
    vec3 fromCam = vWorldPos - uCamPos;
    vec3 fromCenter = vWorldPos - uPlanetCenter;
    vec3 V = length(fromCam) > 0.0001 ? normalize(fromCam) : vec3(0.0, 1.0, 0.0);
    vec3 radialDir = length(fromCenter) > 0.0001 ? normalize(fromCenter) : vec3(0.0, 1.0, 0.0);
    float sunLen = length(uSunDir);
    vec3 sunDir = sunLen > 0.0001 ? uSunDir / sunLen : vec3(0.0, 1.0, 0.0);

    float horizonBase = clamp(1.0 - abs(dot(V, radialDir)), 0.0, 1.0);
    float horizon = atmoPow(horizonBase, 2.1) * max(uHorizonBlend, 0.0);
    float sunForward = atmoPow(clamp(dot(V, sunDir), 0.0, 1.0), 10.0);
    float sunset = atmoPow(1.0 - clamp(dot(radialDir, sunDir), 0.0, 1.0), 2.4) * max(uSunsetBoost, 0.0);
    float heightFade = exp(-max(uDensityFalloff, 0.05));
    float shellFade = clamp(atmoPow(horizonBase, 1.6), 0.0, 1.0) * heightFade;

    vec3 grad = mix(uHorizonTint, uZenithTint, clamp(radialDir.y * 0.5 + 0.5, 0.0, 1.0));
    vec3 rayleigh = grad * (0.08 + horizon * 0.70) * max(uRayleighStrength, 0.0);
    vec3 mie = vec3(1.0, 0.96, 0.90) * (sunForward * 0.28 + sunset * 0.18) * max(uMieStrength, 0.0);
    vec3 col = (rayleigh + mie) * max(uSunIntensity, 0.01);
    float camDist = length(uCamPos - uPlanetCenter);
    float inside01 = clamp(((uPlanetRadius + uAtmosphereHeight) - camDist) / max(uAtmosphereHeight, 1.0), 0.0, 1.0);
    float insideHaze = inside01 * (0.025 + (1.0 - horizonBase) * 0.05);
    float alpha = clamp((horizon * 0.22) * max(uAtmoBlend, 0.0) + insideHaze * max(uAtmoBlend, 0.0), 0.0, 0.38) * shellFade;
    col *= step(col, col);
    alpha *= step(alpha, alpha);
    FragColor = vec4(col, alpha);
}
