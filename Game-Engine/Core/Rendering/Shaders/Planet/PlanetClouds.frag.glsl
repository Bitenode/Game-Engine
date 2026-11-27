#version 330 core

in vec3 vWorldPos;
in vec3 vWorldNormal;

#include "Common/FrameGlobals.glsl"

uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform float uCloudBaseHeight;
uniform float uCloudTopHeight;
uniform float uCloudCoverage;
uniform float uCloudDensity;
uniform float uCloudScale;
uniform float uCloudDetail;
uniform float uCloudSpeed;
uniform float uCloudSoftness;
uniform float uCloudLightResponse;
uniform float uCloudSilverLining;
uniform vec3 uCloudColor;
uniform vec3 uCloudShadowColor;
uniform int uCloudStepCount;
uniform vec3 uSunDir;
uniform float uSunIntensity;
uniform vec3 uSkyTint;
uniform float uTime;

out vec4 FragColor;

// Bounded compile-time step limits for OpenGL ES 3.0 / ANGLE compatibility.
#if defined(TARGET_ES) || defined(TARGET_XBOX)
const int MAX_CLOUD_STEPS = 10;
const int SUN_SHADOW_STEPS = 1;
#else
const int MAX_CLOUD_STEPS = 24;
const int SUN_SHADOW_STEPS = 2;
#endif

float hash31(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.x + p.y) * p.z);
}

float noise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hash31(i + vec3(0.0, 0.0, 0.0));
    float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
    float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
    float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
    float n111 = hash31(i + vec3(1.0, 1.0, 1.0));

    float nx00 = mix(n000, n100, u.x);
    float nx10 = mix(n010, n110, u.x);
    float nx01 = mix(n001, n101, u.x);
    float nx11 = mix(n011, n111, u.x);
    float nxy0 = mix(nx00, nx10, u.y);
    float nxy1 = mix(nx01, nx11, u.y);
    return mix(nxy0, nxy1, u.z);
}

float fbm3(vec3 p)
{
    float value = 0.0;
    float amp = 0.52;
    for (int i = 0; i < 3; i++)
    {
        value += noise3(p) * amp;
        p = p * 2.03 + vec3(17.1, 11.3, 7.7);
        amp *= 0.5;
    }
    return value;
}

float fbm2(vec3 p)
{
    float value = noise3(p) * 0.65;
    p = p * 2.11 + vec3(5.3, 13.7, 9.1);
    value += noise3(p) * 0.35;
    return value;
}

vec2 intersectSphere(vec3 ro, vec3 rd, float r)
{
    float b = dot(ro, rd);
    float c = dot(ro, ro) - r * r;
    float h = b * b - c;
    if (h < 0.0) return vec2(1e9, -1e9);
    h = sqrt(h);
    return vec2(-b - h, -b + h);
}

float henyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = max(0.001, 1.0 + g2 - 2.0 * g * cosTheta);
    // Avoid pow(x, 1.5) for ANGLE/HLSL safety: denom * sqrt(denom).
    return (1.0 - g2) / (12.5663706 * denom * sqrt(denom));
}

float sampleCloudDensity(vec3 p, float rBase, float thickness, float cw, float sw, vec3 detailWind)
{
    float r = length(p);
    float h = (r - rBase) / thickness;
    if (h <= 0.0 || h >= 1.0) return 0.0;

    // Flat-ish cumulus base, rounded billowing top.
    float bottomFade = smoothstep(0.0, 0.18, h);
    float topFade = smoothstep(1.0, 0.34, h);
    float heightProfile = bottomFade * topFade;

    vec3 dir = p / max(r, 0.001);
    // True spherical longitude drift around the planet Y axis (seamless at all latitudes/scales).
    vec3 rotDir = vec3(dir.x * cw - dir.z * sw, dir.y, dir.x * sw + dir.z * cw);

    float scale = max(uCloudScale, 0.25);
    // Gentle latitude-band swirl + macro cumulus shape.
    float swirl = sin(rotDir.y * 4.5 + rotDir.x * 2.0) * 0.08;
    vec3 sampleCoord = rotDir * scale + vec3(swirl, h * 0.48, -swirl);
    float baseN = fbm3(sampleCoord);

    // Higher-frequency detail erosion (driven by uCloudDetail).
    float detailFreq = scale * max(uCloudDetail, 0.5) * 1.45;
    float detailN = fbm2(rotDir * detailFreq + vec3(0.0, h * 1.10, 0.0) + detailWind);

    float shape = baseN * heightProfile;
    float threshold = mix(0.66, 0.16, clamp(uCloudCoverage, 0.0, 1.0));
    float edge = max(0.025, uCloudSoftness * 0.30);
    float cov = smoothstep(threshold - edge, threshold + edge, shape);
    if (cov <= 0.001) return 0.0;

    // Erode edges & tops for fluffy volumetric silhouettes.
    float erosion = (1.0 - detailN) * 0.42 * (1.0 - cov * 0.55);
    float density = clamp(cov - erosion, 0.0, 1.0);
    return density * max(uCloudDensity, 0.0);
}

void main()
{
    if (uCloudDensity <= 0.001 || uCloudCoverage <= 0.001)
        discard;

    vec3 ro = uCamPos - uPlanetCenter;
    vec3 rayRaw = vWorldPos - uCamPos;
    float rayLen = length(rayRaw);
    if (rayLen < 0.0001) discard;
    vec3 rd = rayRaw / rayLen;

    float sunLen = length(uSunDir);
    vec3 L = sunLen > 0.0001 ? uSunDir / sunLen : vec3(0.20, 0.82, 0.53);

    float rPlanet = max(uPlanetRadius, 1.0);
    float rBase = rPlanet + max(uCloudBaseHeight, 1.0);
    float rTop = max(rBase + 1.0, rPlanet + uCloudTopHeight);
    float thickness = rTop - rBase;

    vec2 hitTop = intersectSphere(ro, rd, rTop);
    if (hitTop.y <= 0.0 || hitTop.x > hitTop.y)
        discard;

    vec2 hitBase = intersectSphere(ro, rd, rBase);
    vec2 hitPlanet = intersectSphere(ro, rd, rPlanet);

    float camR = length(ro);
    float tStart = 0.0;
    float tEnd = 0.0;
    float horizonFade = 1.0;

    if (camR < rBase)
    {
        // Ground / below-cloud view looking up into the cloud layer.
        tStart = max(0.0, hitBase.y);
        tEnd = hitTop.y;
        // If ray hits the planet surface before reaching the cloud base (when above nominal radius), clip.
        if (camR > rPlanet && hitPlanet.x > 0.0 && hitPlanet.x < tStart)
            discard;

        // Smooth horizon fade to avoid grazing-angle aliasing and blend into atmospheric haze.
        float upDot = dot(rd, ro / max(camR, 0.001));
        horizonFade = smoothstep(-0.02, 0.14, upDot);
        if (horizonFade <= 0.001) discard;

        // Cap extreme grazing march distance near horizon for sharp detail & consistent perf.
        tEnd = min(tEnd, tStart + thickness * 6.5);
    }
    else if (camR <= rTop)
    {
        // Camera is flying inside the volumetric cloud layer!
        tStart = 0.0;
        if (hitBase.x > 0.0)
            tEnd = min(hitTop.y, hitBase.x);
        else
            tEnd = hitTop.y;
        tEnd = min(tEnd, thickness * 6.5);
    }
    else
    {
        // Orbit / above-cloud view looking down at the spherical cloud shell.
        tStart = max(0.0, hitTop.x);
        if (hitPlanet.x > 0.0)
        {
            // Ray hits the solid planet: march only through the near-side cloud shell.
            tEnd = (hitBase.x > tStart) ? min(hitTop.y, hitBase.x) : min(hitTop.y, hitPlanet.x);
        }
        else
        {
            // Grazing the limb around the planet's horizon.
            tEnd = hitTop.y;
        }
    }

    float marchDist = tEnd - tStart;
    if (marchDist <= 0.01)
        discard;

    int steps = clamp(uCloudStepCount, 4, MAX_CLOUD_STEPS);
    float stepLen = marchDist / float(steps);

    // Interleaved Gradient Noise jitter eliminates volumetric slicing artifacts.
    float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));

    // Precompute spherical wind rotation once per fragment.
    float windAngle = uTime * uCloudSpeed * 0.25;
    float cw = cos(windAngle);
    float sw = sin(windAngle);
    vec3 detailWind = vec3(uTime * uCloudSpeed * 0.18, -uTime * uCloudSpeed * 0.05, uTime * uCloudSpeed * 0.13);

    // Forward/back Henyey-Greenstein phase (silver lining around the sun).
    float cosTheta = dot(rd, L);
    float hgForward = henyeyGreenstein(cosTheta, 0.62);
    float hgBack = henyeyGreenstein(cosTheta, -0.25);
    float phase = (hgForward * 0.72 + hgBack * 0.18) * 3.2;

    float extScale = 2.6 / max(thickness, 1.0);
    float sunStep1 = thickness * 0.28;
    float sunStep2 = thickness * 0.72;

    vec3 accumLight = vec3(0.0);
    float transmittance = 1.0;

    for (int i = 0; i < MAX_CLOUD_STEPS; i++)
    {
        if (i >= steps) break;

        float t = tStart + (float(i) + jitter) * stepLen;
        vec3 p = ro + rd * t;

        float dens = sampleCloudDensity(p, rBase, thickness, cw, sw, detailWind);
        if (dens > 0.002)
        {
            float r = length(p);
            float h = clamp((r - rBase) / thickness, 0.0, 1.0);
            vec3 radialDir = p / max(r, 0.001);

            // Sun-direction self-shadowing (1 step ES / 2 steps Desktop).
            float sunDens = sampleCloudDensity(p + L * sunStep1, rBase, thickness, cw, sw, detailWind);
            float sunOpticalDepth = sunDens * 0.55;
            if (SUN_SHADOW_STEPS > 1)
            {
                float sunDens2 = sampleCloudDensity(p + L * sunStep2, rBase, thickness, cw, sw, detailWind);
                sunOpticalDepth += sunDens2 * 0.45;
            }

            // Beer-Lambert direct transmittance + soft multiple-scattering floor.
            float directTrans = exp(-sunOpticalDepth * 1.85);
            float multiScatter = exp(-sunOpticalDepth * 0.42) * 0.45;
            float lightEnergy = max(directTrans, multiScatter);

            // Smooth day/night terminator & warm sunset/sunrise glow.
            float sunDot = dot(radialDir, L);
            float dayFactor = smoothstep(-0.18, 0.22, sunDot);
            float sunsetBand = exp(-abs(sunDot - 0.04) * 7.5) * smoothstep(-0.24, 0.06, sunDot);

            vec3 warmSunset = vec3(1.0, 0.54, 0.26);
            vec3 sunColor = mix(uCloudColor, warmSunset, sunsetBand * 0.78) * max(uSunIntensity, 0.05) * dayFactor;

            // Ambient sky fill (brighter near cloud tops, cooler blue-grey undersides, dim on night side).
            vec3 dayAmbient = mix(uCloudShadowColor, uSkyTint, 0.35) * (0.38 + 0.62 * h);
            vec3 nightAmbient = uCloudShadowColor * vec3(0.12, 0.16, 0.26) * (0.5 + 0.5 * h);
            vec3 ambientCol = mix(nightAmbient, dayAmbient, dayFactor);

            // Combine direct illumination, silver-lining forward scattering, and ambient fill.
            float silverTerm = phase * directTrans * uCloudSilverLining * 0.45;
            vec3 lit = ambientCol + sunColor * (lightEnergy * uCloudLightResponse + silverTerm);

            // Front-to-back Beer-Lambert integration.
            float stepExt = dens * stepLen * extScale;
            float stepTrans = exp(-stepExt);
            float stepAlpha = 1.0 - stepTrans;

            accumLight += transmittance * lit * stepAlpha;
            transmittance *= stepTrans;

            if (transmittance < 0.02)
                break;
        }
    }

    float alpha = (1.0 - transmittance) * horizonFade;
    if (alpha < 0.005)
        discard;

    // Convert premultiplied accumLight to straight alpha for SrcAlpha / OneMinusSrcAlpha blending.
    vec3 finalColor = accumLight / max(1.0 - transmittance, 0.0001);
    finalColor *= step(finalColor, finalColor);
    alpha *= step(alpha, alpha);

    FragColor = vec4(finalColor, clamp(alpha, 0.0, 0.96));
}
