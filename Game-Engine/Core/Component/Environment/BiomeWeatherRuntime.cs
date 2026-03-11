using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Shared runtime weather state consumed by render/vegetation systems.
/// Values are planet-local in producer components; this static snapshot
/// is used by shader-driven vegetation tinting.
/// </summary>
public static class BiomeWeatherRuntime
{
    public static float Wetness { get; set; } = 0f;
    public static float SnowCoverage { get; set; } = 0f;
    public static SN.Vector3 CloudTint { get; set; } = new(1f, 1f, 1f);
}
