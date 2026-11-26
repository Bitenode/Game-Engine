#version 330 core

in vec2 vUV;

uniform sampler2D gNormalRoughness;
uniform sampler2D gDepth;

uniform mat4  uProjection;
uniform mat4  uView;
uniform mat4  uInvViewProj;

uniform float uRadius;
uniform float uBias;
uniform int   uSampleCount;
uniform vec2  uNoiseScale;

out vec4 FragColor;

#define MAX_SAMPLES 32

float halton(int index, int base)
{
    float f = 1.0, r = 0.0;
    int i = index;
    for (int n = 0; n < 16; n++)
    {
        if (i <= 0) break;
        f /= float(base);
        r += f * float(i - (i / base) * base);
        i /= base;
    }
    return r;
}

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

vec3 randomVec(vec2 uv)
{
    return normalize(vec3(
        hash(uv) * 2.0 - 1.0,
        hash(uv + vec2(1.0, 0.0)) * 2.0 - 1.0,
        0.0
    ));
}

vec3 cosineHemisphere(vec2 xi)
{
    float phi = 6.28318530718 * xi.x;
    float cosTheta = sqrt(max(0.0, 1.0 - xi.y));
    float sinTheta = sqrt(clamp(xi.y, 0.0, 1.0));
    return vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

void main()
{
    float depth = texture(gDepth, vUV).r;
    if (depth >= 1.0)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 tnm = texture(gNormalRoughness, vUV).rgb;
    vec2 nxy = tnm.xy * 2.0 - 1.0;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    vec3 N = normalize(vec3(nxy, nz));

    vec3 fragPos = (uView * vec4(worldPos, 1.0)).xyz;
    vec3 normal  = normalize((uView * vec4(N, 0.0)).xyz);

    vec3 rvec = randomVec(vUV * uNoiseScale);
    vec3 tangent   = normalize(rvec - normal * dot(rvec, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN       = mat3(tangent, bitangent, normal);

    int count = clamp(uSampleCount, 4, MAX_SAMPLES);
    float occlusion = 0.0;
    for (int i = 0; i < MAX_SAMPLES; i++)
    {
        if (i >= count) break;
        vec2 xi = vec2(halton(i + 1, 2), halton(i + 1, 3));
        vec3 hem = cosineHemisphere(xi);
        vec3 samplePos = fragPos + TBN * hem * uRadius;

        vec4 offset = uProjection * vec4(samplePos, 1.0);
        offset.xy /= offset.w;
        offset.xy = offset.xy * 0.5 + 0.5;

        float sampleDepth = texture(gDepth, offset.xy).r;
        vec3 sampleWorld = WorldPosFromDepth(sampleDepth, offset.xy);
        float sampleViewZ = (uView * vec4(sampleWorld, 1.0)).z;

        float rangeCheck = smoothstep(0.0, 1.0, uRadius / max(0.001, abs(fragPos.z - sampleViewZ)));
        occlusion += (sampleViewZ >= samplePos.z + uBias ? 1.0 : 0.0) * rangeCheck;
    }

    occlusion = 1.0 - (occlusion / float(count));
    FragColor = vec4(vec3(occlusion), 1.0);
}
