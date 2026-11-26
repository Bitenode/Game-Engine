#pragma once

// Construct a cotangent-frame TBN matrix from screen-space derivatives.
// Allows normal mapping without per-vertex tangent attributes.
mat3 CotangentFrame(vec3 N, vec3 p, vec2 uv)
{
    vec3 dp1  = dFdx(p);
    vec3 dp2  = dFdy(p);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);

    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    // Guard against degenerate derivatives (edge-on surfaces, UV discontinuities)
    float maxLen = max(dot(T, T), dot(B, B));
    if (maxLen < 1e-6) return mat3(vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), N);
    float invmax = inversesqrt(maxLen);
    return mat3(T * invmax, B * invmax, N);
}
