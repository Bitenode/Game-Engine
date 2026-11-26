#version 330 core

in vec2 vUV;

uniform mat4  uInvVP;
uniform mat4  uVP;
uniform vec3  uCamPos;
uniform float uGridStep;
uniform int   uMajorEvery;

out vec4 FragColor;

void main()
{
    vec2 ndc = vUV * 2.0 - 1.0;
    vec4 near4 = uInvVP * vec4(ndc, 0.0, 1.0);
    vec4 far4  = uInvVP * vec4(ndc, 1.0, 1.0);
    vec3 nearW = near4.xyz / near4.w;
    vec3 farW  = far4.xyz / far4.w;
    vec3 dir   = normalize(farW - nearW);

    // Intersect Y=0 plane
    if (abs(dir.y) < 1e-6)
        discard;

    float t = -nearW.y / dir.y;
    if (t <= 0.0)
        discard;

    vec3 hit = nearW + dir * t;

    // Compute correct depth
    vec4 clip = uVP * vec4(hit, 1.0);
    float ndcZ = clip.z / clip.w;
    gl_FragDepth = ndcZ * 0.5 + 0.5;

    // Grid lines
    float gx = hit.x / uGridStep;
    float gz = hit.z / uGridStep;
    float wx = abs(gx - round(gx));
    float wz = abs(gz - round(gz));
    float distToLine = min(wx, wz);

    float lineW = 0.015 + 0.0025 * min(40.0, t);
    float alpha = clamp((lineW - distToLine) / lineW, 0.0, 1.0);

    // Distance fade
    float d = distance(uCamPos, hit);
    float fade = 1.0 / (1.0 + 0.12 * d);
    alpha *= fade;

    if (alpha <= 0.0)
        discard;

    // Color: axis, major, minor
    int ix = int(round(gx));
    int iz = int(round(gz));
    bool onAxis  = (ix == 0) || (iz == 0);
    bool onMajor = (ix % uMajorEvery == 0) || (iz % uMajorEvery == 0);

    vec3 col = onAxis ? vec3(0.375) : (onMajor ? vec3(0.282) : vec3(0.188));

    FragColor = vec4(col, alpha);
}
