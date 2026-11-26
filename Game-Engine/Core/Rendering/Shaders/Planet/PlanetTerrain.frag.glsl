#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec2 vUV;
in vec4 vShadowCoord;
flat in ivec4 vBlendIdx;
in vec4 vBlendWt;

#include "Common/FrameGlobals.glsl"
#include "Common/LightingGlobals.glsl"

uniform vec3 uPlanetCenter;
uniform int uAtmoEnabled;
uniform vec3 uAtmoSunDir;
uniform float uAtmoSunIntensity;
uniform float uAtmoBlend;
uniform float uAtmoRayleigh;
uniform float uAtmoMie;
uniform float uAtmoDensityFalloff;
uniform float uAtmoHorizonBlend;
uniform float uAtmoSunsetBoost;
uniform float uAtmoHeight;
#ifndef TARGET_XBOX
uniform int uAtmoSampleCount;
#endif
uniform vec3 uAtmoZenithTint;
uniform vec3 uAtmoHorizonTint;
uniform vec3 uAtmoSkyTint;

// One dedicated sampler per biome/splat layer (units 0-7), splat atlases on 8-9, shadow on 15
uniform sampler2D uBiomeTex0;
uniform sampler2D uBiomeTex1;
uniform sampler2D uBiomeTex2;
uniform sampler2D uBiomeTex3;
uniform sampler2D uBiomeTex4;
uniform sampler2D uBiomeTex5;
uniform sampler2D uBiomeTex6;
uniform sampler2D uBiomeTex7;

// Under / cliff albedo (biome graph UnderTexture). Units 10-13 for biomes 0-3.
uniform sampler2D uBiomeUnderTex0;
uniform sampler2D uBiomeUnderTex1;
uniform sampler2D uBiomeUnderTex2;
uniform sampler2D uBiomeUnderTex3;
uniform float uBiomeHasUnder[8];

uniform sampler2D uPlanetSplat0;
uniform sampler2D uPlanetSplat1;
uniform int uUsePlanetSplat;

#ifndef TARGET_XBOX
// Dig depth atlas (same 3x2 layout as splat). R = height delta (negative when dug). Unit 14.
uniform sampler2D uPlanetDigAtlas;
uniform int uUsePlanetDig;
#endif

uniform float uBiomeTiling[8];
uniform float uBiomeUnderTiling[8];
uniform vec3  uBiomeBaseColor[8];
uniform vec3  uBiomeUnderColor[8];

uniform sampler2D uShadowMap;

uniform float uPlanetRadius;
uniform float uWetness;
uniform float uSnowCoverage;
uniform float uWeatherEnabled;
uniform int   uHdrOutput;

out vec4 FragColor;

// Cube-sphere dir -> face index + UV (matches CubeSphereMath.SphereToCube).
void sphereToCube(vec3 dir, out int face, out vec2 uv)
{
    vec3 a = abs(dir);
    float ax = a.x, ay = a.y, az = a.z;
    float aa, bb;
    if (ax >= ay && ax >= az)
    {
        float inv = 1.0 / max(ax, 1e-8);
        if (dir.x > 0.0) { face = 0; aa = -dir.z * inv; bb = dir.y * inv; }
        else { face = 1; aa = dir.z * inv; bb = dir.y * inv; }
    }
    else if (ay >= ax && ay >= az)
    {
        float inv = 1.0 / max(ay, 1e-8);
        if (dir.y > 0.0) { face = 2; aa = dir.x * inv; bb = -dir.z * inv; }
        else { face = 3; aa = dir.x * inv; bb = dir.z * inv; }
    }
    else
    {
        float inv = 1.0 / max(az, 1e-8);
        if (dir.z > 0.0) { face = 4; aa = dir.x * inv; bb = dir.y * inv; }
        else { face = 5; aa = -dir.x * inv; bb = dir.y * inv; }
    }
    uv = vec2((aa + 1.0) * 0.5, (bb + 1.0) * 0.5);
}

vec2 faceUvToAtlas(int face, vec2 uv)
{
    int col = face % 3;
    int row = face / 3;
    vec2 atlasPixels = vec2(textureSize(uPlanetSplat0, 0));
    vec2 facePixels = max(atlasPixels / vec2(3.0, 2.0), vec2(1.0));
    // Stay half a texel inside each tile; linear filtering must not blend a
    // cubemap edge with the unrelated neighboring tile in the packed atlas.
    vec2 inset = 0.5 / facePixels;
    vec2 safeUv = clamp(uv, inset, vec2(1.0) - inset);
    return vec2((float(col) + safeUv.x) / 3.0,
                (float(row) + safeUv.y) / 2.0);
}

float atmoPow(float x, float e)
{
    // D3D pow(0, non-integer) is NaN. ANGLE on Xbox then blends that NaN over the sky.
    return x <= 0.0001 ? 0.0 : exp2(e * log2(x));
}

vec3 atmoSun(vec3 dir)
{
    float len = length(dir);
    return len > 0.0001 ? dir / len : vec3(0.0, 1.0, 0.0);
}

vec3 evalAtmosphere(vec3 worldPos, vec3 viewDir, vec3 radialDir)
{
    if (uAtmoEnabled == 0) return vec3(0.0);

    float distFromCenter = length(worldPos - uPlanetCenter);
    // Below the crust: skip sky tint so cave walls are not shaded like outdoor rock.
    if (distFromCenter < uPlanetRadius) return vec3(0.0);

    float altitude = max(distFromCenter - uPlanetRadius, 0.0);
    float atmoDepth = clamp(altitude / max(uAtmoHeight, 1.0), 0.0, 1.0);
    float density = exp(-atmoDepth * max(uAtmoDensityFalloff, 0.1));

    float horizon = atmoPow(clamp(1.0 - abs(dot(viewDir, radialDir)), 0.0, 1.0), 1.8);
    horizon *= max(uAtmoHorizonBlend, 0.0);

    vec3 sun = atmoSun(uAtmoSunDir);
    float sunForward = atmoPow(clamp(dot(viewDir, sun), 0.0, 1.0), 4.0);
    float sunset = atmoPow(1.0 - clamp(dot(radialDir, sun), 0.0, 1.0), 2.0) * max(uAtmoSunsetBoost, 0.0);

    vec3 grad = mix(uAtmoHorizonTint, uAtmoZenithTint, clamp(radialDir.y * 0.5 + 0.5, 0.0, 1.0));
    vec3 rayleigh = grad * (0.3 + horizon * 0.9) * max(uAtmoRayleigh, 0.0);
    vec3 mie = vec3(1.0, 0.96, 0.90) * (sunForward * 0.7 + sunset * 0.35) * max(uAtmoMie, 0.0);
    vec3 color = (rayleigh + mie + uAtmoSkyTint * 0.2) * density * max(uAtmoSunIntensity, 0.01);

    return color * clamp(uAtmoBlend, 0.0, 1.5);
}

#ifdef TARGET_XBOX

// ANGLE-friendly unrolled biome sampling path for Xbox UWP so HLSL link returns quickly.
vec3 faceProject(sampler2D tex, vec3 p, vec3 dir)
{
    vec3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) return texture(tex, p.yz).rgb;
    if (a.z >= a.y) return texture(tex, p.xy).rgb;
    return texture(tex, p.xz).rgb;
}

vec3 triplanar(sampler2D tex, vec3 worldPos, vec3 radial, vec3 vertexN, float t)
{
    float meters = max(uPlanetRadius, 1.0) / max(t, 0.25);
    vec3 p = (worldPos - uPlanetCenter) / meters;
    // Derivative normals stay on the light. They wobble the projection axis
    // across a coarse triangle and that is the remaining smear.
    float align = abs(dot(normalize(vertexN), normalize(radial)));
    vec3 dir = align > 0.55 ? radial : vertexN;
    return faceProject(tex, p, dir);
}

vec3 painted(vec3 texCol, vec3 baseCol)
{
    float lum = dot(texCol, vec3(0.299, 0.587, 0.114));
    return lum > 0.98 ? baseCol : texCol;
}

void splatWeights(vec3 radialDir, out float w0, out float w1, out float w2, out float w3, out float w4, out float w5, out float w6, out float w7)
{
    int face;
    vec2 uv;
    sphereToCube(normalize(radialDir), face, uv);
    vec2 atlasUv = faceUvToAtlas(face, uv);
    vec4 s0 = texture(uPlanetSplat0, atlasUv);
    vec4 s1 = texture(uPlanetSplat1, atlasUv);
    float sum = max(s0.r, 0.0) + max(s0.g, 0.0) + max(s0.b, 0.0) + max(s0.a, 0.0)
              + max(s1.r, 0.0) + max(s1.g, 0.0) + max(s1.b, 0.0) + max(s1.a, 0.0);
    if (sum < 1e-4) { w0 = 1.0; w1 = 0.0; w2 = 0.0; w3 = 0.0; w4 = 0.0; w5 = 0.0; w6 = 0.0; w7 = 0.0; return; }
    float invSum = 1.0 / sum;
    w0 = max(s0.r, 0.0) * invSum; w1 = max(s0.g, 0.0) * invSum; w2 = max(s0.b, 0.0) * invSum; w3 = max(s0.a, 0.0) * invSum;
    w4 = max(s1.r, 0.0) * invSum; w5 = max(s1.g, 0.0) * invSum; w6 = max(s1.b, 0.0) * invSum; w7 = max(s1.a, 0.0) * invSum;
}

float addSlot(int slot, int idx, float wt)
{
    // Mountains / Boreal / Ice sit above slot 7 in the graph. The editor clamps
    // them into the last albedo; without that those hills paint empty.
    int c = clamp(idx, 0, 7);
    return c == slot ? max(wt, 0.0) : 0.0;
}

float shadowFactor(vec4 sc)
{
    if (uHasShadow == 0) return 1.0;
    vec3 p = sc.xyz / sc.w;
    p = p * 0.5 + 0.5;
    if (p.z > 1.0 || p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;
    return p.z - 0.004 > texture(uShadowMap, p.xy).r ? 0.55 : 1.0;
}

void main()
{
    vec3 vertexN = normalize(vWorldNormal);
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    vec3 dpdx = dFdx(vWorldPos);
    vec3 dpdy = dFdy(vWorldPos);
    vec3 geoCross = cross(dpdx, dpdy);
    float geoLen = length(geoCross);
    vec3 N = vertexN;
    if (geoLen > 1e-8)
    {
        vec3 geoN = geoCross / geoLen;
        if (dot(geoN, radialDir) < 0.0) geoN = -geoN;
        // Soft mix: pure derivative normals facet every coarse triangle.
        N = normalize(mix(vertexN, geoN, 0.72));
    }
    float slope = abs(dot(N, radialDir));
    float slopeBlend = smoothstep(0.18, 0.62, slope);

    float w0, w1, w2, w3, w4, w5, w6, w7;
    if (uUsePlanetSplat != 0)
    {
        splatWeights(radialDir, w0, w1, w2, w3, w4, w5, w6, w7);
    }
    else
    {
        w0 = addSlot(0, vBlendIdx.x, vBlendWt.x) + addSlot(0, vBlendIdx.y, vBlendWt.y) + addSlot(0, vBlendIdx.z, vBlendWt.z) + addSlot(0, vBlendIdx.w, vBlendWt.w);
        w1 = addSlot(1, vBlendIdx.x, vBlendWt.x) + addSlot(1, vBlendIdx.y, vBlendWt.y) + addSlot(1, vBlendIdx.z, vBlendWt.z) + addSlot(1, vBlendIdx.w, vBlendWt.w);
        w2 = addSlot(2, vBlendIdx.x, vBlendWt.x) + addSlot(2, vBlendIdx.y, vBlendWt.y) + addSlot(2, vBlendIdx.z, vBlendWt.z) + addSlot(2, vBlendIdx.w, vBlendWt.w);
        w3 = addSlot(3, vBlendIdx.x, vBlendWt.x) + addSlot(3, vBlendIdx.y, vBlendWt.y) + addSlot(3, vBlendIdx.z, vBlendWt.z) + addSlot(3, vBlendIdx.w, vBlendWt.w);
        w4 = addSlot(4, vBlendIdx.x, vBlendWt.x) + addSlot(4, vBlendIdx.y, vBlendWt.y) + addSlot(4, vBlendIdx.z, vBlendWt.z) + addSlot(4, vBlendIdx.w, vBlendWt.w);
        w5 = addSlot(5, vBlendIdx.x, vBlendWt.x) + addSlot(5, vBlendIdx.y, vBlendWt.y) + addSlot(5, vBlendIdx.z, vBlendWt.z) + addSlot(5, vBlendIdx.w, vBlendWt.w);
        w6 = addSlot(6, vBlendIdx.x, vBlendWt.x) + addSlot(6, vBlendIdx.y, vBlendWt.y) + addSlot(6, vBlendIdx.z, vBlendWt.z) + addSlot(6, vBlendIdx.w, vBlendWt.w);
        w7 = addSlot(7, vBlendIdx.x, vBlendWt.x) + addSlot(7, vBlendIdx.y, vBlendWt.y) + addSlot(7, vBlendIdx.z, vBlendWt.z) + addSlot(7, vBlendIdx.w, vBlendWt.w);
    }

    vec3 t0 = painted(triplanar(uBiomeTex0, vWorldPos, radialDir, vertexN, uBiomeTiling[0]), uBiomeBaseColor[0]);
    vec3 t1 = painted(triplanar(uBiomeTex1, vWorldPos, radialDir, vertexN, uBiomeTiling[1]), uBiomeBaseColor[1]);
    vec3 t2 = painted(triplanar(uBiomeTex2, vWorldPos, radialDir, vertexN, uBiomeTiling[2]), uBiomeBaseColor[2]);
    vec3 t3 = painted(triplanar(uBiomeTex3, vWorldPos, radialDir, vertexN, uBiomeTiling[3]), uBiomeBaseColor[3]);
    vec3 t4 = painted(triplanar(uBiomeTex4, vWorldPos, radialDir, vertexN, uBiomeTiling[4]), uBiomeBaseColor[4]);
    vec3 t5 = painted(triplanar(uBiomeTex5, vWorldPos, radialDir, vertexN, uBiomeTiling[5]), uBiomeBaseColor[5]);
    vec3 t6 = painted(triplanar(uBiomeTex6, vWorldPos, radialDir, vertexN, uBiomeTiling[6]), uBiomeBaseColor[6]);
    vec3 t7 = painted(triplanar(uBiomeTex7, vWorldPos, radialDir, vertexN, uBiomeTiling[7]), uBiomeBaseColor[7]);
    vec3 topCol = t0 * w0 + t1 * w1 + t2 * w2 + t3 * w3 + t4 * w4 + t5 * w5 + t6 * w6 + t7 * w7;

    vec3 u0 = painted(triplanar(uBiomeUnderTex0, vWorldPos, radialDir, vertexN, uBiomeUnderTiling[0]), uBiomeUnderColor[0]);
    vec3 u1 = painted(triplanar(uBiomeUnderTex1, vWorldPos, radialDir, vertexN, uBiomeUnderTiling[1]), uBiomeUnderColor[1]);
    vec3 u2 = painted(triplanar(uBiomeUnderTex2, vWorldPos, radialDir, vertexN, uBiomeUnderTiling[2]), uBiomeUnderColor[2]);
    vec3 u3 = painted(triplanar(uBiomeUnderTex3, vWorldPos, radialDir, vertexN, uBiomeUnderTiling[3]), uBiomeUnderColor[3]);
    vec3 underCol = u0 * w0 + u1 * w1 + u2 * w2 + u3 * w3
        + uBiomeUnderColor[4] * w4 + uBiomeUnderColor[5] * w5
        + uBiomeUnderColor[6] * w6 + uBiomeUnderColor[7] * w7;
    float wSum = w0 + w1 + w2 + w3 + w4 + w5 + w6 + w7;
    if (wSum > 0.001) { topCol /= wSum; underCol /= wSum; }
    else topCol = painted(triplanar(uBiomeTex0, vWorldPos, radialDir, vertexN, uBiomeTiling[0]), uBiomeBaseColor[0]);

    vec3 cliffCol = mix(underCol, topCol * 0.62, 0.22);
    vec3 finalColor = mix(cliffCol, topCol, slopeBlend);

    float distFromCenter = length(vWorldPos - uPlanetCenter);
    float outdoor = smoothstep(uPlanetRadius - 2.0, uPlanetRadius, distFromCenter);
    float weatherOn = clamp(uWeatherEnabled, 0.0, 1.0) * outdoor;
    float groundFlat = smoothstep(0.48, 0.82, slope);
    float wetAmt = clamp(uWetness, 0.0, 1.0) * weatherOn;
    float snowAmt = clamp(uSnowCoverage, 0.0, 1.0) * weatherOn;
    finalColor *= mix(vec3(1.0), vec3(0.86, 0.90, 0.93), wetAmt * groundFlat * 0.28);

    vec3 L = normalize(-uLightDir);
    float diffuse = max(dot(N, L), 0.0) * uDiffuseK;
    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 32.0) * 0.06 * slopeBlend;
    spec += pow(max(dot(N, H), 0.0), 28.0) * wetAmt * groundFlat * 0.18;
    float shadow = shadowFactor(vShadowCoord);
    float ao = mix(1.0, 0.35, clamp(-dot(N, radialDir), 0.0, 1.0));
    vec3 lit = finalColor * (uAmbient + diffuse * shadow) * ao + vec3(spec) * shadow;

    lit += evalAtmosphere(vWorldPos, V, radialDir);

    lit = mix(lit, lit * 0.45 + vec3(0.86, 0.90, 0.94) * 0.55, snowAmt * groundFlat * 0.35);
    if (uHdrOutput == 0)
        lit = lit / (lit + vec3(1.0));
    FragColor = vec4(lit, 1.0);
}

#else

vec3 triplanar(sampler2D tex, vec3 worldPos, vec3 dir, float t)
{
    // One cube face. Blending the other two stretched a second copy of the
    // albedo across hillsides where no axis was dominant.
    float meters = max(uPlanetRadius, 1.0) / max(t, 0.25);
    vec3 p = (worldPos - uPlanetCenter) / meters;
    vec3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) return texture(tex, p.yz).rgb;
    if (a.z >= a.y) return texture(tex, p.xy).rgb;
    return texture(tex, p.xz).rgb;
}

vec3 sampleBiome(int idx, vec3 wp, vec3 ba, float t)
{
    if (idx == 0) return triplanar(uBiomeTex0, wp, ba, t);
    if (idx == 1) return triplanar(uBiomeTex1, wp, ba, t);
    if (idx == 2) return triplanar(uBiomeTex2, wp, ba, t);
    if (idx == 3) return triplanar(uBiomeTex3, wp, ba, t);
    if (idx == 4) return triplanar(uBiomeTex4, wp, ba, t);
    if (idx == 5) return triplanar(uBiomeTex5, wp, ba, t);
    if (idx == 6) return triplanar(uBiomeTex6, wp, ba, t);
    return triplanar(uBiomeTex7, wp, ba, t);
}

vec3 sampleBiomeUnder(int idx, vec3 wp, vec3 ba, float t)
{
    // Hardware sampler budget: under maps for biomes 0-3 (unit 14 = dig atlas).
    if (idx == 0) return triplanar(uBiomeUnderTex0, wp, ba, t);
    if (idx == 1) return triplanar(uBiomeUnderTex1, wp, ba, t);
    if (idx == 2) return triplanar(uBiomeUnderTex2, wp, ba, t);
    if (idx == 3) return triplanar(uBiomeUnderTex3, wp, ba, t);
    return uBiomeUnderColor[idx];
}

float sampleDigDepthMeters(vec3 radialDir)
{
    if (uUsePlanetDig == 0) return 0.0;
    int face;
    vec2 uv;
    sphereToCube(normalize(radialDir), face, uv);
    vec2 atlasUv = faceUvToAtlas(face, uv);
    // R stores dig delta; digging subtracts height so delta is negative.
    float dig = -texture(uPlanetDigAtlas, atlasUv).r;
    return max(dig, 0.0);
}

void samplePlanetSplat(vec3 radialDir, out float w[8])
{
    int face;
    vec2 uv;
    sphereToCube(normalize(radialDir), face, uv);
    vec2 atlasUv = faceUvToAtlas(face, uv);
    vec4 s0 = texture(uPlanetSplat0, atlasUv);
    vec4 s1 = texture(uPlanetSplat1, atlasUv);
    w[0] = s0.r; w[1] = s0.g; w[2] = s0.b; w[3] = s0.a;
    w[4] = s1.r; w[5] = s1.g; w[6] = s1.b; w[7] = s1.a;
    float sum = 0.0;
    for (int i = 0; i < 8; i++) sum += max(w[i], 0.0);
    if (sum < 1e-4)
    {
        w[0] = 1.0;
        for (int i = 1; i < 8; i++) w[i] = 0.0;
        return;
    }
    float inv = 1.0 / sum;
    for (int i = 0; i < 8; i++) w[i] = max(w[i], 0.0) * inv;
}

float shadowFactor(vec4 sc)
{
    if (uHasShadow == 0) return 1.0;
    vec3 projCoords = sc.xyz / sc.w;
    projCoords = projCoords * 0.5 + 0.5;
    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
        return 1.0;
    float bias = 0.004 + (1.0 - max(dot(normalize(vWorldNormal), normalize(-uLightDir)), 0.0)) * 0.02;
    vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
    float visibility = 0.0;
    visibility += (projCoords.z - bias > texture(uShadowMap, projCoords.xy + vec2(-0.6, -0.6) * texel).r) ? 0.55 : 1.0;
    visibility += (projCoords.z - bias > texture(uShadowMap, projCoords.xy + vec2( 0.6, -0.6) * texel).r) ? 0.55 : 1.0;
    visibility += (projCoords.z - bias > texture(uShadowMap, projCoords.xy + vec2(-0.6,  0.6) * texel).r) ? 0.55 : 1.0;
    visibility += (projCoords.z - bias > texture(uShadowMap, projCoords.xy + vec2( 0.6,  0.6) * texel).r) ? 0.55 : 1.0;
    return visibility * 0.25;
}

vec3 evalBiome(int idx, vec3 worldPos, vec3 ba, float slopeBlend, float nDotRadial, float digBlend)
{
    float t = uBiomeTiling[idx];
    vec3 texCol = sampleBiome(idx, worldPos, ba, t);
    vec3 baseCol = uBiomeBaseColor[idx];
    vec3 underCol = uBiomeUnderColor[idx];

    float lum = dot(texCol, vec3(0.299, 0.587, 0.114));
    vec3 topCol = (lum > 0.98) ? baseCol : texCol;

    // Open ground is the top albedo. The second texture is only for cliffs and digs.
    float topW = slopeBlend * (1.0 - digBlend);
    if (topW > 0.92)
        return topCol;

    {
        float ut = max(uBiomeUnderTiling[idx], 0.25);
        vec3 underTex = (uBiomeHasUnder[idx] > 0.5)
            ? sampleBiomeUnder(idx, worldPos, ba, ut)
            : sampleBiome(idx, worldPos, ba, ut);
        float ulum = dot(underTex, vec3(0.299, 0.587, 0.114));
        underCol = (ulum > 0.98) ? underCol : underTex;
    }

    // Grey floor is for outward cliffs only; inward cave rock keeps biome under-color.
    underCol = mix(underCol, max(underCol, vec3(0.12)), clamp(nDotRadial, 0.0, 1.0));
    vec3 cliffCol = mix(underCol, topCol * 0.62, 0.22);

    return mix(cliffCol, topCol, topW);
}

float wetHash3(vec3 p)
{
    return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

float wetValueNoise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = wetHash3(i + vec3(0.0, 0.0, 0.0));
    float n100 = wetHash3(i + vec3(1.0, 0.0, 0.0));
    float n010 = wetHash3(i + vec3(0.0, 1.0, 0.0));
    float n110 = wetHash3(i + vec3(1.0, 1.0, 0.0));
    float n001 = wetHash3(i + vec3(0.0, 0.0, 1.0));
    float n101 = wetHash3(i + vec3(1.0, 0.0, 1.0));
    float n011 = wetHash3(i + vec3(0.0, 1.0, 1.0));
    float n111 = wetHash3(i + vec3(1.0, 1.0, 1.0));
    float z0 = mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y);
    float z1 = mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y);
    return mix(z0, z1, f.z);
}

float wetFbm3(vec3 p)
{
    float v = 0.0;
    float a = 0.55;
    for (int i = 0; i < 3; i++)
    {
        v += a * wetValueNoise3(p);
        p = p * 2.03 + vec3(17.3, 9.1, 13.7);
        a *= 0.5;
    }
    return v;
}

void main()
{
    vec3 N = normalize(vWorldNormal);
    vec3 radialDir = normalize(vWorldPos - uPlanetCenter);
    float nDotRadial = dot(N, radialDir);

    float slope = abs(nDotRadial);
    float slopeBlend = smoothstep(0.18, 0.62, slope);
    float digDepth = sampleDigDepthMeters(radialDir);
    float digBlend = smoothstep(0.25, 2.0, digDepth);

    // Flat ground and hills keep the radial cube face. Mixing that with the
    // surface normal picked a grazing axis and smeared the albedo on the slope.
    // Cliffs and digs use the surface normal so the texture runs with the face.
    vec3 projDir = (slope > 0.55 && digBlend < 0.5) ? radialDir : N;

    vec3 finalColor = vec3(0.0);
    float totalWeight = 0.0;

    if (uUsePlanetSplat != 0)
    {
        float sw[8];
        samplePlanetSplat(radialDir, sw);
        for (int i = 0; i < 8; i++)
        {
            float w = sw[i];
            if (w < 0.01) continue;
            finalColor += evalBiome(i, vWorldPos, projDir, slopeBlend, nDotRadial, digBlend) * w;
            totalWeight += w;
        }
    }
    else
    {
        float weights[4] = float[4](vBlendWt.x, vBlendWt.y, vBlendWt.z, vBlendWt.w);
        int   indices[4] = int[4](vBlendIdx.x, vBlendIdx.y, vBlendIdx.z, vBlendIdx.w);

        for (int i = 0; i < 4; i++)
        {
            float w = weights[i];
            if (w < 0.01) continue;
            int idx = clamp(indices[i], 0, 7);
            finalColor += evalBiome(idx, vWorldPos, projDir, slopeBlend, nDotRadial, digBlend) * w;
            totalWeight += w;
        }
    }

    if (totalWeight > 0.0) finalColor /= totalWeight;
    else finalColor = vec3(0.5);

    float distFromCenter = length(vWorldPos - uPlanetCenter);
    float interior = 1.0 - smoothstep(uPlanetRadius - 2.0, uPlanetRadius, distFromCenter);
    float outdoor = 1.0 - interior;
    float weatherOn = clamp(uWeatherEnabled, 0.0, 1.0) * outdoor;

    // Seamless reference-sphere coordinates: stable across camera motion, LOD swaps,
    // terrain height, and cubemap face boundaries.
    vec3 puddleCoord = radialDir * max(uPlanetRadius, 1.0) * 0.045;
    float nLow = wetFbm3(puddleCoord);
    float nDetail = wetValueNoise3(puddleCoord * 1.7 + vec3(4.7, 11.2, 2.9));
    // Keep puddles off steep faces where LOD-normal changes make highlights shimmer.
    float groundFlat = smoothstep(0.48, 0.82, slope);
    float wetAmt = clamp(uWetness, 0.0, 1.0) * weatherOn;
    float snowAmt = clamp(uSnowCoverage, 0.0, 1.0) * weatherOn;
    float puddleMask = smoothstep(0.48, 0.66, nLow * 0.82 + nDetail * 0.18) * groundFlat;
    float wetSheen = wetAmt * groundFlat * (1.0 - snowAmt);
    float puddle = wetSheen * puddleMask;

    finalColor *= mix(vec3(1.0), vec3(0.86, 0.90, 0.93), wetSheen * 0.28);
    finalColor *= mix(vec3(1.0), vec3(0.72, 0.80, 0.85), puddle * 0.30);

    vec3 L = normalize(-uLightDir);
    float NdotL = max(dot(N, L), 0.0);
    float diffuse = NdotL * uDiffuseK;

    vec3 V = normalize(uCamPos - vWorldPos);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 32.0) * 0.06 * slopeBlend;
    spec += pow(max(dot(N, H), 0.0), mix(20.0, 52.0, puddle)) * (wetSheen * 0.14 + puddle * 0.38);
    float fres = pow(1.0 - max(dot(N, V), 0.0), 4.0) * (wetSheen * 0.08 + puddle * 0.18);

    vec3 R = reflect(-V, N);
    vec3 skyRefCol = mix(uAtmoHorizonTint, uAtmoZenithTint, clamp(R.y * 0.5 + 0.5, 0.0, 1.0));
    float skyRef = pow(max(dot(normalize(R), radialDir), 0.0), 1.4) * puddle * 0.42;

    float shadow = shadowFactor(vShadowCoord);
    float ao = mix(1.0, 0.35, clamp(-nDotRadial, 0.0, 1.0));
    float ambient = uAmbient * mix(1.0, 0.7, interior);
    vec3 lit = finalColor * (ambient + diffuse * shadow) * ao;
    for (int i = 0; i < 4; i++)
    {
        if (i >= uPointCount) break;
        vec3 toL = uPointPosRange[i].xyz - vWorldPos;
        float dist = length(toL);
        float range = max(uPointPosRange[i].w, 0.001);
        if (dist >= range) continue;
        vec3 Lp = toL / max(dist, 0.0001);
        float nd = max(dot(N, Lp), 0.0);
        float wrap = nd * 0.9 + 0.1;
        float t = dist / range;
        float atten = (1.0 - t * t) / (1.0 + t * t);
        lit += finalColor * uPointColor[i].rgb * wrap * atten;
    }
    lit += vec3(spec + fres) * shadow * ao;
    lit += skyRefCol * skyRef * shadow * ao;
    lit += evalAtmosphere(vWorldPos, V, radialDir);
    lit = mix(lit, lit * 0.45 + vec3(0.86, 0.90, 0.94) * 0.55, snowAmt * groundFlat * mix(0.2, 0.5, nDetail));
    if (uHdrOutput == 0)
        lit = lit / (lit + vec3(1.0));

    FragColor = vec4(lit, 1.0);
}

#endif
