#version 330 core

in vec2 vUV;

uniform sampler2D uScene;
uniform vec2 uTexelSize;     // 1.0 / resolution

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

// Color Grading
uniform int   uColorGradingEnabled;
uniform float uBrightness;
uniform float uContrast;
uniform float uSaturation;
uniform float uExposure;
uniform int   uToneMap;     // 0=None, 1=Reinhard, 2=ACES

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

    // Bloom (simplified bright pass + blur approximation)
    if (uBloomEnabled != 0)
    {
        vec3 bright = max(color - vec3(uBloomThreshold), vec3(0.0));
        // Simple 5-tap blur for bloom approximation
        vec3 bloom = bright;
        bloom += max(texture(uScene, vUV + vec2( 2.0, 0.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(-2.0, 0.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(0.0,  2.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom += max(texture(uScene, vUV + vec2(0.0, -2.0) * uTexelSize).rgb - vec3(uBloomThreshold), vec3(0.0));
        bloom *= 0.2;
        color += bloom * uBloomIntensity;
    }

    // Fog
    if (uFogEnabled != 0)
    {
        // Distance-based fog using depth approximation from luminance (simplified)
        float depth = dot(color, vec3(0.299, 0.587, 0.114)); // rough depth proxy
        float fogFactor = 1.0 - exp(-uFogDensity * uFogDensity * depth * depth * 100.0);
        fogFactor = clamp(fogFactor, 0.0, 1.0);
        color = mix(color, uFogColor, fogFactor);
    }

    // Color Grading
    if (uColorGradingEnabled != 0)
    {
        // Exposure
        color *= uExposure;

        // Brightness
        color += vec3(uBrightness);

        // Contrast
        color = ((color - 0.5) * uContrast) + 0.5;

        // Saturation
        float gray = dot(color, vec3(0.299, 0.587, 0.114));
        color = mix(vec3(gray), color, uSaturation);

        // Tone mapping
        if (uToneMap == 1) color = ReinhardTonemap(color);
        else if (uToneMap == 2) color = ACESFilm(color);
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

        // Re-apply tone mapping on distorted sample if color grading is active
        if (uColorGradingEnabled != 0)
        {
            color *= uExposure;
            color += vec3(uBrightness);
            color = ((color - 0.5) * uContrast) + 0.5;
            float grayUW = dot(color, vec3(0.299, 0.587, 0.114));
            color = mix(vec3(grayUW), color, uSaturation);
            if (uToneMap == 1) color = ReinhardTonemap(color);
            else if (uToneMap == 2) color = ACESFilm(color);
        }

        // Underwater fog (depth-based tinting — stronger further below surface)
        float depthFactor = clamp(uUnderwaterDepth * uUnderwaterFogDensity, 0.0, 0.85);
        // Also apply screen-space depth fog (objects far from camera appear foggier)
        float screenDepth = dot(color, vec3(0.299, 0.587, 0.114));
        float screenFog = 1.0 - exp(-uUnderwaterFogDensity * 8.0 * screenDepth);
        float totalFog = clamp(depthFactor + screenFog * 0.5, 0.0, 0.9);
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
    FragColor = vec4(color, 1.0);
}
