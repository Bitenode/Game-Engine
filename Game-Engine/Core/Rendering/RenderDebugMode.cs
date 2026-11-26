#nullable enable

namespace Game_Engine.Core.Rendering;

/// <summary>
/// Rendering diagnostic / debug view modes supported by forward and deferred shaders
/// and exposed in the Scene View toolbar.
/// </summary>
public enum RenderDebugMode
{
    Lit = 0,
    Wireframe = 1,
    Albedo = 2,
    Normals = 3,
    Roughness = 4,
    Metallic = 5,
    Depth = 6,
    SSAO = 7,
    CsmCascades = 8,
    ToneMapCompare = 9
}
