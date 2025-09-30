using Avalonia.Media;

namespace Game_Engine.Core
{
    public enum LightType { Directional, Point }

    public sealed class Light : Behavior
    {
        // Directional uses the GameObject's Transform forward for direction
        [Persist] public LightType Type { get; set; } = LightType.Directional;

        // Multiplies the diffuse strength (1 = default)
        [Persist] public float Intensity { get; set; } = 1.0f;

        // For Point lights (simple falloff)
        [Persist] public float Range { get; set; } = 10f;

        // For future tinting  renderer treats this as a luminance now
        [Persist] public Color Color { get; set; } = Colors.White;

        
    }
}
