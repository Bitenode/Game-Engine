#nullable enable
using System.Numerics;

namespace Game_Engine.Core;

public struct ShadowMap
{
    public Matrix4x4 VP;   // light view-projection
    public float[] Depth;  // size = W*H, 0..1 depth
    public int W, H;
    public float Bias;     // depth bias (in NDC space)
}
