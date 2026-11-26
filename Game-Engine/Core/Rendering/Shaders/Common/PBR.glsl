#pragma once

#ifndef PI
#define PI 3.14159265359
#endif

// GGX / Trowbridge-Reitz normal distribution
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a  = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

// Smith's Schlick-GGX geometry function
float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);
}

// Fresnel-Schlick approximation
vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Roughness-attenuated Fresnel-Schlick for environment / IBL reflections
vec3 FresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness)
{
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Smooth procedural sky-hemisphere reflection fallback when no ReflectionProbe is active
vec3 ProceduralSkyIBL(vec3 R, float roughness, float ambient)
{
    float up = clamp(R.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 groundCol = vec3(0.14, 0.13, 0.12);
    vec3 horizonCol = vec3(0.45, 0.52, 0.62);
    vec3 zenithCol = vec3(0.24, 0.42, 0.72);
    vec3 sky = mix(horizonCol, zenithCol, smoothstep(0.5, 1.0, up));
    vec3 env = mix(groundCol, sky, smoothstep(0.35, 0.65, up));
    // Blur toward average hemisphere irradiance at high roughness
    vec3 avg = vec3(0.30, 0.34, 0.40);
    env = mix(env, avg, clamp(roughness * roughness, 0.0, 1.0));
    return env * max(ambient, 0.15);
}

// Evaluate direct Cook-Torrance BRDF contribution for one light
vec3 EvaluateCookTorrance(vec3 N, vec3 V, vec3 L, vec3 albedo, float metallic, float roughness, vec3 F0, vec3 radiance)
{
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    float NDF = DistributionGGX(N, H, roughness);
    float G   = GeometrySmith(N, V, L, roughness);
    vec3  F   = FresnelSchlick(max(dot(H, V), 0.0), F0);
    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * NdotL + 0.0001;
    vec3 specular = numerator / denominator;
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}
