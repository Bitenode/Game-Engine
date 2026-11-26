#version 330 core

in vec2 vUV;

uniform sampler2D uLitScene;
uniform sampler2D gNormalRoughness;
uniform sampler2D gAlbedoMetallic;
uniform sampler2D gDepth;

uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uInvViewProj;

uniform vec3  uCamPos;
uniform vec2  uScreenSize;
uniform int   uMaxSteps;
uniform float uRoughnessCutoff;
uniform float uMaxRayLength;

out vec4 FragColor;

vec3 WorldPosFromDepth(float depth, vec2 uv)
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, depth * 2.0 - 1.0, 1.0);
    vec4 world = uInvViewProj * clip;
    return world.xyz / world.w;
}

vec2 ToUv(vec4 clip)
{
    vec3 ndc = clip.xyz / clip.w;
    return ndc.xy * 0.5 + 0.5;
}

void main()
{
    vec4 normalRough = texture(gNormalRoughness, vUV);
    vec4 albedoMetal = texture(gAlbedoMetallic, vUV);
    float depth = texture(gDepth, vUV).r;
    vec3 litColor = texture(uLitScene, vUV).rgb;

    if (depth >= 1.0)
    {
        FragColor = vec4(litColor, 1.0);
        return;
    }

    float roughness = normalRough.a;
    if (roughness > uRoughnessCutoff)
    {
        FragColor = vec4(litColor, 1.0);
        return;
    }

    vec3 albedo = albedoMetal.rgb;
    float metallic = albedoMetal.a;
    vec3 tnm = normalRough.rgb;
    vec2 nxy = tnm.xy * 2.0 - 1.0;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    vec3 N = normalize(vec3(nxy, nz));

    vec3 worldPos = WorldPosFromDepth(depth, vUV);
    vec3 V = normalize(uCamPos - worldPos);
    vec3 R = reflect(-V, N);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    float NdotV = max(dot(N, V), 0.0);
    vec3 F = F0 + (1.0 - F0) * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);

    mat3 rotView = mat3(uView);
    vec3 rv = normalize(rotView * R);
    vec4 vw = uView * vec4(worldPos + R * 0.05, 1.0);
    vec3 vp = vw.xyz;
    float stepLen = uMaxRayLength / float(max(uMaxSteps, 1));

    vec3 reflColor = litColor;
    float hitMask = 0.0;

    for (int i = 0; i < 128; i++)
    {
        if (i >= uMaxSteps) break;
        vp += rv * stepLen;
        vec4 clip = uProjection * vec4(vp, 1.0);
        vec2 suv = ToUv(clip);
        if (suv.x < 0.001 || suv.x > 0.999 || suv.y < 0.001 || suv.y > 0.999)
            break;

        float sd = texture(gDepth, suv).r;
        vec3 sw = WorldPosFromDepth(sd, suv);
        vec3 sv = (uView * vec4(sw, 1.0)).xyz;

        if (vp.z > sv.z && vp.z < sv.z + 0.5)
        {
            reflColor = texture(uLitScene, suv).rgb;
            hitMask = 1.0;
            vec2 edgeFade = smoothstep(vec2(0.0), vec2(0.08), suv) * smoothstep(vec2(1.0), vec2(0.92), suv);
            hitMask *= edgeFade.x * edgeFade.y;
            hitMask *= (1.0 - roughness) * (0.2 + 0.8 * metallic);
            break;
        }
    }

    float blurW = clamp(roughness / max(uRoughnessCutoff, 0.01), 0.0, 1.0);
    if (blurW > 0.05 && hitMask > 0.01)
    {
        vec2 px = 1.0 / uScreenSize;
        vec3 acc = reflColor;
        acc += texture(uLitScene, vUV + vec2(px.x, 0.0) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV - vec2(px.x, 0.0) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV + vec2(0.0, px.y) * blurW * 3.0).rgb;
        acc += texture(uLitScene, vUV - vec2(0.0, px.y) * blurW * 3.0).rgb;
        reflColor = acc * 0.2;
    }

    float fresnelW = hitMask * (F.x * 0.33 + F.y * 0.33 + F.z * 0.34);
    vec3 finalColor = mix(litColor, reflColor, clamp(fresnelW, 0.0, 1.0));
    FragColor = vec4(finalColor, 1.0);
}
