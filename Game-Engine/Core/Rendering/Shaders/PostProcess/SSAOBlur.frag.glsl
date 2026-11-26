#version 330 core

in vec2 vUV;

uniform sampler2D uSSAOInput;
uniform sampler2D gDepth;
uniform vec2 uTexelSize;
uniform float uDepthSigma;

out vec4 FragColor;

void main()
{
    float dC = texture(gDepth, vUV).r;
    float sum = 0.0;
    float wsum = 0.0;
    for (int x = -2; x <= 2; x++)
    {
        for (int y = -2; y <= 2; y++)
        {
            vec2 o = vec2(float(x), float(y)) * uTexelSize;
            vec2 suv = vUV + o;
            float d = texture(gDepth, suv).r;
            float w = exp(-abs(d - dC) * uDepthSigma);
            sum += texture(uSSAOInput, suv).r * w;
            wsum += w;
        }
    }
    float result = wsum > 1e-5 ? (sum / wsum) : texture(uSSAOInput, vUV).r;
    FragColor = vec4(vec3(result), 1.0);
}
