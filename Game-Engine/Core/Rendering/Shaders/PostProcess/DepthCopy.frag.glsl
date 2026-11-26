#version 330 core

in vec2 vUV;

uniform sampler2D uDepth;

out vec4 FragColor;

void main()
{
    // texelFetch avoids filtering / edge issues; clamp to valid range for packed D24S8 textures.
    ivec2 dims = textureSize(uDepth, 0);
    ivec2 tc = clamp(ivec2(gl_FragCoord.xy), ivec2(0), dims - ivec2(1));
    float d = texelFetch(uDepth, tc, 0).r;
    gl_FragDepth = d;
    FragColor = vec4(0.0);
}
