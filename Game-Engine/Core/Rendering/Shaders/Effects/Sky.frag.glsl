#version 330 core

in vec2 vUV;

uniform mat4  uInvVP;
uniform vec3  uTopColor;
uniform vec3  uBotColor;
uniform vec3  uSunDir;
uniform int   uUseSun;
uniform sampler2D uSkyTex;
uniform int   uHasSkyTex;
uniform float uSkyBlend;
uniform float uSkyYaw;
uniform int   uHasPlanetUp;
uniform vec3  uPlanetUp;

out vec4 FragColor;

#define PI 3.14159265359

void main()
{
    // Reconstruct world-space ray direction from screen UV
    vec2 ndc = vUV * 2.0 - 1.0;
    vec4 near4 = uInvVP * vec4(ndc, 0.0, 1.0);
    vec4 far4  = uInvVP * vec4(ndc, 1.0, 1.0);
    vec3 nearW = near4.xyz / near4.w;
    vec3 farW  = far4.xyz / far4.w;
    vec3 dir   = normalize(farW - nearW);

    // Apply sky yaw rotation around Y
    float s = sin(uSkyYaw);
    float c = cos(uSkyYaw);
    dir = vec3(c * dir.x + s * dir.z, dir.y, -s * dir.x + c * dir.z);

    // On a planet, up is away from the center. World +Y smears the zenith
    // across the horizon once you leave the pole.
    vec3 skyUp = (uHasPlanetUp != 0 && dot(uPlanetUp, uPlanetUp) > 0.25)
        ? normalize(uPlanetUp)
        : vec3(0.0, 1.0, 0.0);
    float t = clamp(0.5 + 0.5 * dot(dir, skyUp), 0.0, 1.0);

    // Optional sun glow
    if (uUseSun != 0)
    {
        float sunNd = max(dot(dir, uSunDir), 0.0);
        float sunGlow = sunNd <= 0.0001 ? 0.0 : exp2(64.0 * log2(sunNd));
        t = clamp(t + sunGlow * 0.08, 0.0, 1.0);
    }

    vec3 gradColor = mix(uBotColor, uTopColor, t);

    // Equirectangular texture sampling
    if (uHasSkyTex != 0 && uSkyBlend > 0.001)
    {
        float u = 0.5 + atan(dir.x, -dir.z) / (2.0 * PI);
        float v = 0.5 - asin(clamp(dir.y, -1.0, 1.0)) / PI;

        // Wrap longitude explicitly and clamp latitude to avoid pole bleed.
        vec2 uv = vec2(fract(u), clamp(v, 0.0, 1.0));
        vec4 texSamp = texture(uSkyTex, uv);

        // Feather the 0/1 longitude seam by blending to averaged edge texels.
        // This reduces visible vertical seams in non-perfectly seamless panoramas.
        float seamDist = min(uv.x, 1.0 - uv.x);
        float seamWidth = max(1.5 / float(textureSize(uSkyTex, 0).x), 0.002);
        if (seamDist < seamWidth)
        {
            vec4 edgeAvg = 0.5 * (texture(uSkyTex, vec2(0.0, uv.y)) + texture(uSkyTex, vec2(1.0, uv.y)));
            float keepOriginal = smoothstep(0.0, seamWidth, seamDist);
            texSamp = mix(edgeAvg, texSamp, keepOriginal);
        }

        gradColor = mix(gradColor, texSamp.rgb, uSkyBlend * texSamp.a);
    }

    FragColor = vec4(gradColor, 1.0);
}
