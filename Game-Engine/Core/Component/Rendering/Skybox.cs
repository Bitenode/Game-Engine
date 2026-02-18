using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    [ComponentCategory("Rendering")]
    public sealed class Skybox : Behavior
    {
        [Persist] public Color Top { get; set; } = Color.Parse("#1f1f1f");
        [Persist] public Color Bottom { get; set; } = Color.Parse("#0a0a0a");
        [Persist] public float Ambient { get; set; } = 0.90f;

        [Persist] public Texture2D? Texture { get; set; } = null;   // optional sky texture
        [Persist] public string? TexturePath { get; set; } = null;  // project-relative path
        [Persist] public float TextureBlend { get; set; } = 1.0f;

        [Persist] public float Yaw { get; set; } = 0f;
        /// <summary>Sun elevation in degrees: 0 = horizon (deep shadows through windows),
        /// 90 = overhead (minimal window penetration). Default 45.</summary>
        [Persist] public float SunElevation { get; set; } = 45f;
      //  [Persist] public float SeamFeather { get; set; } = 0.01f;
      //  [Persist] public bool KeyOutNearBlack { get; set; } = false;
      //  [Persist] public float KeyLuma { get; set; } = 0.08f;
    }
}
