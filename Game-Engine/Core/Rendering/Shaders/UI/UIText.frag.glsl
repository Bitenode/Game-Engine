#version 330 core

in vec2 vUV;
in vec4 vColor;

uniform sampler2D uTex;

out vec4 FragColor;

void main()
{
    float dist = texture(uTex, vUV).r;
    // SDF smoothstep parameters — adjust for sharpness
    float edgeWidth = fwidth(dist) * 0.75;
    float alpha = smoothstep(0.5 - edgeWidth, 0.5 + edgeWidth, dist);
    if (alpha < 0.004) discard;
    FragColor = vec4(vColor.rgb, vColor.a * alpha);
}
