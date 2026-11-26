#version 330 core

in vec2 vUV;

uniform sampler2D uSceneColor;
uniform sampler2D gDepth;

uniform mat4  uInvViewProj;
uniform vec3  uCamPos;
uniform vec3  uLightDir;       // direction FROM light (negated for L)
uniform vec3  uLightColor;

// Volumetric fog parameters
uniform float uFogDensity;
uniform float uFogAnisotropy;
uniform float uFogScattering;
uniform float uFogHeightFalloff;
uniform float uFogBaseHeight;
uniform float uFogNoiseScale;
uniform float uFogNoiseSpeed;
uniform float uFogMaxDistance;
uniform vec3  uFogColor;
uniform int   uFogSteps;
uniform float uTime;

// Shadow map for light occlusion during ray march
uniform sampler2D uShadowMap;
uniform mat4      uShadowVP;
uniform int       uHasShadow;

out vec4 FragColor;

// Simple 3D hash noise
float hash3D(vec3 p)
{
    p = fract(p * vec3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float noise3D(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = hash3D(i);
    float b = hash3D(i + vec3(1, 0, 0));
    float c = hash3D(i + vec3(0, 1, 0));
    float d = hash3D(i + vec3(1, 1, 0));
    float e = hash3D(i + vec3(0, 0, 1));
    float f1 = hash3D(i + vec3(1, 0, 1));
    float g = hash3D(i + vec3(0, 1, 1));
    float h = hash3D(i + vec3(1, 1, 1));

    return mix(mix(mix(a, b, f.x), mix(c, d, f.x), f.y),
               mix(mix(e, f1, f.x), mix(g, h, f.x), f.y), f.z);
}

// Henyey-Greenstein phase function
float phaseHG(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 0.0001);
    return (1.0 - g2) / (4.0 * 3.14159265 * exp2(1.5 * log2(denom)));
}

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

float ShadowCheck(vec3 worldPos)
{
    if (uHasShadow == 0) return 1.0;
    vec4 sc = uShadowVP * vec4(worldPos, 1.0);
    vec3 proj = sc.xyz / sc.w * 0.5 + 0.5;
    if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0 || proj.z > 1.0)
        return 1.0;
    float shadowDepth = texture(uShadowMap, proj.xy).r;
    return (proj.z - 0.005 > shadowDepth) ? 0.0 : 1.0;
}

void main()
{
    vec3 sceneColor = texture(uSceneColor, vUV).rgb;
    float depth = texture(gDepth, vUV).r;

    // No volumetric fog for sky pixels
    if (depth >= 1.0)
    {
        FragColor = vec4(sceneColor, 1.0);
        return;
    }

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 rayDir = normalize(worldPos - uCamPos);
    float maxDist = min(length(worldPos - uCamPos), uFogMaxDistance);
    float stepSize = maxDist / float(uFogSteps);

    // Phase function for directional light scattering
    float cosAngle = dot(rayDir, -uLightDir);
    float phase = phaseHG(cosAngle, uFogAnisotropy);

    vec3 fogAccum = vec3(0.0);
    float transmittance = 1.0;

    for (int i = 0; i < 16; i++)
    {
        if (i >= uFogSteps) break;
        float t = (float(i) + 0.5) * stepSize;
        vec3 samplePos = uCamPos + rayDir * t;

        // Height-based density
        float heightAtten = exp(-max(samplePos.y - uFogBaseHeight, 0.0) * uFogHeightFalloff);

        // Noise-based density variation
        vec3 noiseCoord = samplePos * uFogNoiseScale + vec3(uTime * uFogNoiseSpeed, 0.0, uTime * uFogNoiseSpeed * 0.7);
        float noiseFactor = noise3D(noiseCoord) * 0.5 + 0.5;

        float localDensity = uFogDensity * heightAtten * noiseFactor;
        if (localDensity < 0.0001) continue;

        // Light contribution (check shadow)
        float shadowFactor = ShadowCheck(samplePos);
        vec3 lightContrib = uLightColor * phase * uFogScattering * shadowFactor;
        vec3 ambient = uFogColor * 0.15;

        // Beer-Lambert extinction
        float extinction = exp(-localDensity * stepSize);

        // Accumulate in-scattered light
        fogAccum += transmittance * (lightContrib + ambient) * uFogColor * localDensity * stepSize;
        transmittance *= extinction;

        if (transmittance < 0.01) break;
    }

    vec3 finalColor = sceneColor * transmittance + fogAccum;
    FragColor = vec4(finalColor, 1.0);
}
