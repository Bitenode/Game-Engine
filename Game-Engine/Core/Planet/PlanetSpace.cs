using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Planet-local unscaled space: world position minus planet center, then divided by
/// the planet transform's uniform scale. Density sampling, voxel edits, mesh
/// generation, and physics queries all use this space.
/// </summary>
public static class PlanetSpace
{
    public static float SanitizeScale(float worldScale)
        => MathF.Max(0.0001f, worldScale);

    /// <summary>World point → planet-local unscaled coordinates.</summary>
    public static SN.Vector3 WorldToLocal(SN.Vector3 worldPos, SN.Vector3 planetCenter, float worldScale)
        => (worldPos - planetCenter) / SanitizeScale(worldScale);

    /// <summary>Planet-local unscaled point → world coordinates.</summary>
    public static SN.Vector3 LocalToWorld(SN.Vector3 localPos, SN.Vector3 planetCenter, float worldScale)
        => planetCenter + localPos * SanitizeScale(worldScale);

    /// <summary>World length (brush radius, etc.) → local unscaled length.</summary>
    public static float WorldToLocalLength(float worldLength, float worldScale)
        => worldLength / SanitizeScale(worldScale);

    /// <summary>Local unscaled length → world length.</summary>
    public static float LocalToWorldLength(float localLength, float worldScale)
        => localLength * SanitizeScale(worldScale);
}
