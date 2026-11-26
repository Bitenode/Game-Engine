#version 330 core

in vec2 vUV;
in vec3 vWorldPos;
in vec3 vWorldNormal;
in float vHeight;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform sampler2D uAlbedoTex;
uniform float uSunIntensity;
uniform float uAlphaCutoff;
uniform float uWetness;
uniform float uSnow;
uniform float uCloudiness;

out vec4 FragColor;

void main()
{
    vec4 albedo = texture(uAlbedoTex, vUV);
    if (albedo.a < uAlphaCutoff)
        discard;

    vec3 N = normalize(vWorldNormal);
    // Same convention as planet terrain: uLightDir is FROM the sun.
    vec3 L = normalize(-uLightDir);
    float nDotL = max(dot(N, L), 0.0);
    // Day: soft wrap. Night: collapse toward real N.L so carpet matches terrain.
    float day = clamp(uSunIntensity, 0.0, 1.0);
    float wrap = mix(nDotL, nDotL * 0.55 + 0.45, day);
    float back = max(dot(-N, L), 0.0) * 0.20 * day;
    float diffuse = min(wrap + back, 1.15);

    float clouds = clamp(uCloudiness, 0.0, 1.0);
    float sunI = max(0.02, uSunIntensity) * mix(1.0, 0.55, clouds);
    float amb = max(0.02, uAmbient) * mix(1.0, 0.72, clouds);

    vec3 color = albedo.rgb;
    float wet = clamp(uWetness, 0.0, 1.0);
    color *= mix(1.0, 0.84, wet);
    color = mix(color, color * vec3(0.88, 0.93, 0.97), wet * 0.30);
    float snowMask = clamp(uSnow, 0.0, 1.0) * smoothstep(0.28, 1.0, vHeight);
    color = mix(color, vec3(0.86, 0.89, 0.93), snowMask);

    vec3 lit = color * (amb + uDiffuseK * diffuse * sunI);
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 22.0) * wet * vHeight * 0.20;
    lit += uLightColor * sunI * spec;

    FragColor = vec4(lit, 1.0);
}
