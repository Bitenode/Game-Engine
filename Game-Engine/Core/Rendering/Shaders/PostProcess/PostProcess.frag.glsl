#version 330 core

in vec2 vUV;

#include "Common/FrameGlobals.glsl"

uniform sampler2D uScene;       // unit 0
uniform sampler2D uDepthTex;    // unit 1
uniform int       uHasDepthTex;
uniform vec2 uTexelSize;        // 1.0 / resolution

// Bloom
uniform int   uBloomEnabled;
uniform float uBloomThreshold;
uniform float uBloomIntensity;

// Fog
uniform int   uFogEnabled;
uniform vec3  uFogColor;
uniform float uFogDensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform int   uFogHeightBased;
uniform float uFogHeightFalloff;

// Color Grading
uniform int   uColorGradingEnabled;
uniform float uBrightness;
uniform float uContrast;
uniform float uSaturation;
uniform float uExposure;
uniform int   uToneMap;        // 0=None, 1=Reinhard, 2=ACES
uniform int   uToneMapCompare; // 1=3-way split: Left=None, Mid=Reinhard, Right=ACES

// Vignette
uniform int   uVignetteEnabled;
uniform float uVignetteIntensity;
uniform float uVignetteSmoothness;

// FXAA
uniform int   uFXAAEnabled;

// Underwater
uniform int   uUnderwaterEnabled;
uniform vec3  uUnderwaterTint;
uniform float uUnderwaterFogDensity;
uniform float uUnderwaterCausticStr;
uniform float uUnderwaterDistortion;
uniform float uUnderwaterTime;
uniform float uUnderwaterDepth;    // how far below the surface (0 = at surface)

out vec4 FragColor;

vec3 ACESFilm(vec3 x)
{
    x *= 0.6;
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x*(a*x+b))/(x*(c*x+d)+e), 0.0, 1.0);
}

vec3 ReinhardTonemap(vec3 color)
{
    return color / (color + vec3(1.0));
}

vec3 ApplyToneMap(vec3 color)
{
    if (uToneMapCompare != 0)
    {
        if (vUV.x < (1.0 / 3.0))
            return clamp(color, 0.0, 1.0);
        else if (vUV.x < (2.0 / 3.0))
            return ReinhardTonemap(color);
        else
            return ACESFilm(color);
    }

    if (uToneMap == 1) return ReinhardTonemap(color);
    if (uToneMap == 2) return ACESFilm(color);
    return color;
}

void main()
{
    vec3 color = texture(uScene, vUV).rgb;

    // FXAA (simplified)
    if (uFXAAEnabled != 0)
    {
        vec3 rgbNW = texture(uScene, vUV + vec2(-1.0, -1.0) * uTexelSize).rgb;
        vec3 rgbNE = texture(uScene, vUV + vec2( 1.0, -1.0) * uTexelSize).rgb;
        vec3 rgbSW = texture(uScene, vUV + vec2(-1.0,  1.0) * uTexelSize).rgb;
        vec3 rgbSE = texture(uScene, vUV + vec2( 1.0,  1.0) * uTexelSize).rgb;

        vec3 luma = vec3(0.299, 0.587, 0.114);
        float lumaNW = dot(rgbNW, luma);
        float lumaNE = dot(rgbNE, luma);
        float lumaSW = dot(rgbSW, luma);
        float lumaSE = dot(rgbSE, luma);
        float lumaM  = dot(color, luma);

        float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
        float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
        float lumaRange = lumaMax - lumaMin;

        if (lumaRange > max(0.0312, lumaMax * 0.0625))
        {
            vec2 dir;
            dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
            dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

            float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * 0.25, 1.0/128.0);
            float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
            dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * uTexelSize;

            vec3 rgbA = 0.5 * (
                texture(uScene, vUV + dir * (1.0/3.0 - 0.5)).rgb +
                texture(uScene, vUV + dir * (2.0/3.0 - 0.5)).rgb);
            vec3 rgbB = rgbA * 0.5 + 0.25 * (
                texture(uScene, vUV + dir * -0.5).rgb +
                texture(uScene, vUV + dir *  0.5).rgb);

            float lumaB = dot(rgbB, luma);
            color = (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
        }
    }

    // Bloom (linear HDR bright-pass extraction + 9-tap gather BEFORE tone mapping)
    if (uBloomEnabled != 0)
    {
        vec3 thresh = vec3(max(uBloomThreshold, 0.0));
        vec3 bloom = max(color - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2( 2.0,  0.0) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(-2.0,  0.0) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2( 0.0,  2.0) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2( 0.0, -2.0) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2( 1.5,  1.5) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(-1.5,  1.5) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2( 1.5, -1.5) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(-1.5, -1.5) * uTexelSize).rgb - thresh, vec3(0.0));
        bloom *= (1.0 / 9.0);
        color += bloom * uBloomIntensity;
    }

    // Depth & Height Fog (real scene depth from uDepthTex + FrameGlobals)
    if (uFogEnabled != 0 && uHasDepthTex != 0)
    {
        float rawDepth = texture(uDepthTex, vUV).r;
        // Skip skybox pixels (depth >= 0.9999)
        if (rawDepth < 0.9999)
        {
            vec4 clipPos = vec4(vUV * 2.0 - 1.0, rawDepth * 2.0 - 1.0, 1.0);
            vec4 viewPos = uInvProj * clipPos;
            viewPos /= max(abs(viewPos.w), 1e-6) * (viewPos.w < 0.0 ? -1.0 : 1.0);
            vec3 worldPos = (uInvView * viewPos).xyz;

            float dist = length(worldPos - uCamPos);
            float dStart = max(dist - uFogStart, 0.0);

            float fogFactor;
            if (uFogDensity > 0.0001)
            {
                // Exponential distance fog starting at uFogStart
                fogFactor = 1.0 - exp(-uFogDensity * dStart);
                if (uFogEnd > uFogStart)
                {
                    float linFactor = clamp(dStart / max(uFogEnd - uFogStart, 0.001), 0.0, 1.0);
                    fogFactor = max(fogFactor, linFactor * clamp(uFogDensity * 25.0, 0.0, 1.0));
                }
            }
            else
            {
                // Pure linear fog when density is 0
                fogFactor = clamp(dStart / max(uFogEnd - uFogStart, 0.001), 0.0, 1.0);
            }

            if (uFogHeightBased != 0)
            {
                float heightTerm = exp(-max(worldPos.y, 0.0) * max(uFogHeightFalloff, 0.001));
                fogFactor *= clamp(heightTerm, 0.0, 1.0);
            }

            fogFactor = clamp(fogFactor, 0.0, 1.0);
            color = mix(color, uFogColor, fogFactor);
        }
    }

    // Exposure + Single-Pass Tone Mapping (HDR -> LDR) + Display-Space Color Grading
    if (uColorGradingEnabled != 0)
    {
        // Exposure in linear HDR space
        color = max(color, vec3(0.0)) * uExposure;

        // Single-pass tone mapping
        color = ApplyToneMap(color);

        // Brightness
        color += vec3(uBrightness);

        // Contrast
        color = ((color - 0.5) * uContrast) + 0.5;

        // Saturation
        float gray = dot(color, vec3(0.299, 0.587, 0.114));
        color = mix(vec3(gray), color, uSaturation);
    }

    // Vignette
    if (uVignetteEnabled != 0)
    {
        float dist = distance(vUV, vec2(0.5));
        float vig = smoothstep(0.5 - uVignetteSmoothness, 0.5, dist);
        color *= 1.0 - vig * uVignetteIntensity;
    }

    // Underwater effect
    if (uUnderwaterEnabled != 0)
    {
        // Screen-space distortion (wavy wobble)
        float wobbleX = sin(vUV.y * 25.0 + uUnderwaterTime * 2.3) * uUnderwaterDistortion;
        float wobbleY = cos(vUV.x * 20.0 + uUnderwaterTime * 1.7) * uUnderwaterDistortion;
        vec2 distortedUV = vUV + vec2(wobbleX, wobbleY);
        distortedUV = clamp(distortedUV, 0.001, 0.999);
        color = texture(uScene, distortedUV).rgb;

        // Re-apply exposure, tone mapping, and color grading on distorted sample
        if (uColorGradingEnabled != 0)
        {
            color = max(color, vec3(0.0)) * uExposure;
            color = ApplyToneMap(color);
            color += vec3(uBrightness);
            color = ((color - 0.5) * uContrast) + 0.5;
            float grayUW = dot(color, vec3(0.299, 0.587, 0.114));
            color = mix(vec3(grayUW), color, uSaturation);
        }

        // Underwater fog (depth-based tinting — stronger further below surface)
        float depthFactor = clamp(uUnderwaterDepth * uUnderwaterFogDensity, 0.0, 0.85);
        float screenFog = 0.0;
        if (uHasDepthTex != 0)
        {
            float uwRawDepth = texture(uDepthTex, distortedUV).r;
            float zNear = max(uNearPlane, 0.01);
            float zFar = max(uFarPlane, zNear + 1.0);
            float uwLin = (uwRawDepth >= 0.9999)
                ? zFar
                : (2.0 * zNear * zFar) / max(zFar + zNear - (uwRawDepth * 2.0 - 1.0) * (zFar - zNear), 0.001);
            screenFog = 1.0 - exp(-uUnderwaterFogDensity * 0.25 * uwLin);
        }
        else
        {
            float screenDepth = dot(color, vec3(0.299, 0.587, 0.114));
            screenFog = 1.0 - exp(-uUnderwaterFogDensity * 8.0 * screenDepth);
        }
        float totalFog = clamp(depthFactor + screenFog * 0.6, 0.0, 0.9);
        color = mix(color, uUnderwaterTint, totalFog);

        // Caustic light patterns (animated)
        vec2 cUV = vUV * 8.0;
        float c1 = sin(cUV.x * 3.0 + uUnderwaterTime * 1.1) * cos(cUV.y * 2.7 + uUnderwaterTime * 0.9);
        float c2 = sin(cUV.x * 2.1 - uUnderwaterTime * 0.7) * cos(cUV.y * 3.3 + uUnderwaterTime * 1.3);
        float caustic = (c1 + c2) * 0.5 + 0.5;
        caustic = caustic <= 0.0001 ? 0.0 : exp2(3.0 * log2(caustic));
        // Caustics are brighter near the surface
        float causticFade = clamp(1.0 - uUnderwaterDepth * 0.15, 0.1, 1.0);
        color += vec3(caustic) * uUnderwaterCausticStr * causticFade;

        // Color absorption (reds fade first, then greens, leaving blues)
        float absorption = clamp(uUnderwaterDepth * 0.08, 0.0, 0.7);
        color.r *= 1.0 - absorption;
        color.g *= 1.0 - absorption * 0.5;

        // Underwater vignette (stronger than normal)
        float uwDist = distance(vUV, vec2(0.5));
        float uwVig = smoothstep(0.3, 0.7, uwDist);
        color *= 1.0 - uwVig * 0.4;
    }

    color = clamp(color, 0.0, 1.0);
    // A NaN from an earlier pass (D3D pow/normalize) would survive clamp and present black.
    color *= step(color, color);

    // 1px dark vertical dividers at x = 1/3 and x = 2/3 in ToneMapCompare mode
    if (uToneMapCompare != 0)
    {
        float halfPx = max(uTexelSize.x * 0.55, 0.0005);
        if (abs(vUV.x - (1.0 / 3.0)) < halfPx || abs(vUV.x - (2.0 / 3.0)) < halfPx)
            color = vec3(0.05);
    }

    FragColor = vec4(color, 1.0);
}
