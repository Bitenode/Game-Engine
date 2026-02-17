Shader "Steel PBR" {
    VERTEX {
#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightSpaceMatrix;

out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec2 vTexCoord;
out vec4 vShadowCoord;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos     = worldPos.xyz;
    vWorldNormal  = normalize(mat3(transpose(inverse(uModel))) * aNormal);
    vTexCoord     = aTexCoord;
    vShadowCoord  = uLightSpaceMatrix * worldPos;
    gl_Position   = uProjection * uView * worldPos;
}
    }
    FRAGMENT {
#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vTexCoord;
in vec4 vShadowCoord;

uniform sampler2D uTexture0;
uniform sampler2D uTexture1;
uniform sampler2D uTexture2;

uniform int   uHasNormalMap;
uniform float uNormalStrength;
uniform int   uHasSpecularMap;

uniform float uRoughness;
uniform float uMetallic;
uniform float uAmbient;

uniform vec3  uLightDir;
uniform vec3  uLightColor;
uniform float uLightIntensity;
uniform vec3  uCameraPos;
uniform float uTime;

out vec4 FragColor;

#define PI 3.14159265359

mat3 CotangentFrame(vec3 N, vec3 p, vec2 uv)
{
    vec3 dp1 = dFdx(p); vec3 dp2 = dFdy(p);
    vec2 duv1 = dFdx(uv); vec2 duv2 = dFdy(uv);
    vec3 dp2perp = cross(dp2, N); vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float mx = max(dot(T,T), dot(B,B));
    if (mx < 1e-6) return mat3(vec3(1,0,0), vec3(0,1,0), N);
    float inv = inversesqrt(mx);
    return mat3(T*inv, B*inv, N);
}

float DistGGX(vec3 N, vec3 H, float r)
{
    float a = r*r; float a2 = a*a;
    float d = max(dot(N,H),0.0); d = d*d*(a2-1.0)+1.0;
    return a2 / max(PI*d*d, 0.0001);
}

float GeoGGX(float NdV, float r)
{
    float k = ((r+1.0)*(r+1.0))/8.0;
    return NdV / (NdV*(1.0-k)+k);
}

float GeoSmith(vec3 N, vec3 V, vec3 L, float r)
{
    return GeoGGX(max(dot(N,V),0.0),r) * GeoGGX(max(dot(N,L),0.0),r);
}

vec3 FresnelSchlick(float cosT, vec3 F0)
{
    return F0 + (1.0-F0)*pow(clamp(1.0-cosT,0.0,1.0), 5.0);
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    vec3 geoN = N;

    // Normal mapping (if bound)
    if (uHasNormalMap == 1)
    {
        vec3 mapN = texture(uTexture1, vTexCoord).rgb * 2.0 - 1.0;
        float nStr = uNormalStrength;
        if (nStr < 0.01) nStr = 1.0;
        mapN.xy *= nStr;
        mapN = normalize(mapN);
        mat3 TBN = CotangentFrame(N, vWorldPos, vTexCoord);
        N = normalize(TBN * mapN);
        if (dot(N, geoN) < 0.1)
            N = normalize(mix(N, geoN, 0.5));
    }

    // Albedo
    vec3 albedo = texture(uTexture0, vTexCoord).rgb;

    // PBR params with safe defaults
    float roughness = uRoughness;
    if (roughness < 0.01) roughness = 0.5;
    roughness = clamp(roughness, 0.04, 1.0);
    float metallic = clamp(uMetallic, 0.0, 1.0);

    // Specular map
    float specMask = 1.0;
    if (uHasSpecularMap == 1)
    {
        specMask = texture(uTexture2, vTexCoord).r;
        metallic = max(metallic, specMask * 0.3);
        roughness = mix(roughness, 1.0 - specMask, 0.4);
    }

    // Light direction: uLightDir is FROM the light, negate to get TOWARD the light
    vec3 L = normalize(-uLightDir);
    vec3 V = normalize(uCameraPos - vWorldPos);
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);

    // Cook-Torrance BRDF
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    float NDF = DistGGX(N, H, roughness);
    float G   = GeoSmith(N, V, L, roughness);
    vec3  F   = FresnelSchlick(max(dot(H, V), 0.0), F0);
    vec3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL + 0.0001, 0.0001);
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);

    // Light intensity — respect engine toggle (0 = light off, ambient-only)
    float intensity = max(uLightIntensity, 0.0);
    vec3 lightCol = uLightColor;
    if (dot(lightCol, lightCol) < 0.001) lightCol = vec3(1.0);

    vec3 radiance = lightCol * intensity;
    vec3 Lo = (kD * albedo / PI + spec) * radiance * NdotL;

    // Ambient (fallback to 0.15 if not set)
    float amb = uAmbient;
    if (amb < 0.01) amb = 0.15;
    vec3 ambient = vec3(amb) * albedo;

    vec3 color = ambient + Lo;

    // Reinhard tone mapping
    color = color / (color + vec3(1.0));

    FragColor = vec4(color, 1.0);
}
    }
}
