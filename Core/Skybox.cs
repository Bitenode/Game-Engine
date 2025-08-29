using Avalonia.Media;

namespace Game_Engine.Core
{
    /// Simple vertical gradient sky + ambient source.
    public sealed class Skybox : Behavior
    {
        [Persist] public Color Top { get; set; } = Color.Parse("#1f1f1f");
        [Persist] public Color Bottom { get; set; } = Color.Parse("#0a0a0a");

        // Ambient term the renderer adds to all shading (0..1)
        [Persist] public float Ambient { get; set; } = 0.90f;
    }
}
