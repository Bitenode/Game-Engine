#version 330 core

in vec4 vColor;
in vec2 vUV;
in vec2 vQuadUV;
flat in int vInstanceID;

uniform sampler2D uParticleTex;
uniform int uHasTexture;
uniform int uAlignVelocity;
uniform int uParticleLook;

out vec4 FragColor;

void main()
{
    if (uHasTexture != 0)
    {
        vec4 tex = texture(uParticleTex, vUV);
        vec4 col = tex * vColor;
        if (col.a < 0.01) discard;
        FragColor = col;
        return;
    }

    if (uParticleLook == 2)
    {
        float x = abs(vQuadUV.x - 0.5) * 2.0;
        float y = vQuadUV.y;
        float wobble = 0.84 + 0.16 * fract(sin(float(vInstanceID) * 12.9898 + y * 47.0) * 43758.5453);
        float halfW = mix(0.96, 0.16, y) * wobble;
        float radial = x / max(halfW, 0.05);
        float body = 1.0 - smoothstep(0.45, 1.0, radial);
        float tip = 1.0 - smoothstep(0.58, 1.0, y);
        float base = smoothstep(0.0, 0.06, y);
        float core = exp(-radial * radial * 1.7) * (1.0 - y * 0.8);
        vec3 hot = vec3(1.0, 0.97, 0.78);
        vec3 edge = vec3(0.72, 0.12, 0.01);
        vec3 col = mix(edge, vColor.rgb, clamp(body, 0.0, 1.0));
        col = mix(col, hot, clamp(core, 0.0, 1.0));
        float alpha = body * tip * base * vColor.a;
        if (alpha < 0.01) discard;
        FragColor = vec4(col, alpha);
        return;
    }

    float alpha;
    if (uAlignVelocity != 0)
    {
        float ax = abs(vQuadUV.x - 0.5) * 2.0;
        float ay = abs(vQuadUV.y - 0.5) * 2.0;
        alpha = (1.0 - smoothstep(0.25, 1.0, ax)) * (1.0 - smoothstep(0.82, 1.0, ay));
    }
    else
    {
        float dist = length(vQuadUV - vec2(0.5));
        alpha = 1.0 - smoothstep(0.3, 0.5, dist);
    }

    FragColor = vec4(vColor.rgb, vColor.a * alpha);
    if (FragColor.a < 0.01) discard;
}
