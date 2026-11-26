#version 330 core

in vec2 vUV;

uniform sampler2D uCurr;
uniform sampler2D uHistory;
uniform sampler2D uDepth;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;
uniform vec2 uTexel;
uniform float uAlpha;
uniform float uSharpen;

out vec4 FragColor;

vec3 RGBToYCoCg(vec3 c)
{
    float Y = dot(c, vec3(0.25, 0.5, 0.25));
    float Co = dot(c, vec3(0.5, 0.0, -0.5));
    float Cg = dot(c, vec3(-0.25, 0.5, -0.25));
    return vec3(Y, Co, Cg);
}

vec3 YCoCgToRGB(vec3 ycocg)
{
    float t = ycocg.x - ycocg.z * 0.5;
    float g = ycocg.x + ycocg.z * 0.5;
    float b = t - ycocg.y * 0.5;
    float r = t + ycocg.y * 0.5;
    return vec3(r, g, b);
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
    vec3 curr = texture(uCurr, vUV).rgb;
    float d = texture(uDepth, vUV).r;
    if (d >= 1.0)
    {
        FragColor = vec4(curr, 1.0);
        return;
    }

    vec3 wpos = WorldPosFromDepth(d, vUV);
    vec4 pclip = uPrevViewProj * vec4(wpos, 1.0);
    vec2 huv = pclip.xy / pclip.w * 0.5 + 0.5;
    vec3 hist = texture(uHistory, huv).rgb;

    vec3 minC = curr, maxC = curr;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            vec3 s = texture(uCurr, vUV + vec2(float(x), float(y)) * uTexel).rgb;
            minC = min(minC, s);
            maxC = max(maxC, s);
        }
    }

    vec3 yHist = RGBToYCoCg(hist);
    vec3 yMin = RGBToYCoCg(minC);
    vec3 yMax = RGBToYCoCg(maxC);
    yHist = clamp(yHist, yMin, yMax);
    hist = YCoCgToRGB(yHist);

    vec3 resolved = mix(hist, curr, uAlpha);
    vec3 blur = (texture(uCurr, vUV + vec2(uTexel.x, 0.0)).rgb
               + texture(uCurr, vUV - vec2(uTexel.x, 0.0)).rgb
               + texture(uCurr, vUV + vec2(0.0, uTexel.y)).rgb
               + texture(uCurr, vUV - vec2(0.0, uTexel.y)).rgb) * 0.25;
    resolved = resolved + (resolved - blur) * uSharpen;
    FragColor = vec4(max(resolved, vec3(0.0)), 1.0);
}
