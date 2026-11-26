#version 330 core

in vec2 vUV;
in vec4 vColor;

uniform sampler2D uTex;
uniform int       uHasTexture;   // 0 = solid color, 1 = textured

out vec4 FragColor;

void main()
{
    vec4 texColor = (uHasTexture != 0) ? texture(uTex, vUV) : vec4(1.0);
    vec4 final = texColor * vColor;
    if (final.a < 0.004) discard;   // ~1/255 alpha clip
    FragColor = final;
}
