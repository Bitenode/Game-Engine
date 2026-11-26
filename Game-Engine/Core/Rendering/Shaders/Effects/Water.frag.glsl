#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;

// Water colors
uniform vec4  uShallowColor;
uniform vec4  uDeepColor;
uniform float uFresnelPower;
uniform float uReflectivity;
uniform float uTransparency;

// Foam
uniform int   uFoamEnabled;
uniform float uFoamThreshold;
uniform float uFoamIntensity;
uniform vec3  uFoamColor;

// Global UBOs (uCamPos, uLightDir, uAmbient, uDiffuseK)
#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

// Sky reflection color (from skybox)
uniform vec3 uSkyColor;

out vec4 FragColor;

void main()
{
    vec3 N = normalize(vWorldNormal);
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 L = -uLightDir;

    // Fresnel
    float fresnel = pow(1.0 - max(dot(N, V), 0.0), uFresnelPower);
    fresnel = clamp(fresnel, 0.0, 1.0);

    // Base water color (shallow/deep blend using fresnel)
    vec4 waterColor = mix(uShallowColor, uDeepColor, fresnel);

    // Reflection (sky color as simple reflection)
    vec3 R = reflect(-V, N);
    float upness = max(R.y, 0.0);
    vec3 reflColor = mix(uSkyColor * 0.5, uSkyColor, upness) * uReflectivity;

    // Specular highlight (sun reflection on water)
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 128.0) * 0.8;

    // Lighting — water is primarily reflective; diffuse contribution is soft
    float NdotL = max(dot(N, L), 0.0);
    float diffuse = min(uDiffuseK, 1.0) * NdotL * 0.3;
    float lighting = uAmbient + diffuse;

    vec3 color = waterColor.rgb * lighting + reflColor * fresnel + vec3(spec) * min(uDiffuseK, 1.0);

    // Foam (based on wave height, simplified)
    if (uFoamEnabled != 0)
    {
        float foam = smoothstep(uFoamThreshold, uFoamThreshold + 0.3, N.y);
        float foamPattern = fract(sin(dot(vWorldPos.xz * 3.0, vec2(12.9898, 78.233))) * 43758.5453);
        foam *= foamPattern * uFoamIntensity;
        color = mix(color, uFoamColor, foam);
    }

    // Reinhard tone mapping to prevent whiteout
    color = color / (color + vec3(1.0));

    float alpha = mix(uTransparency, 1.0, fresnel);
    FragColor = vec4(clamp(color, 0.0, 1.0), alpha);
}
