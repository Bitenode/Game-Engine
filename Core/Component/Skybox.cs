using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    //  vertical gradient sky + ambient 
    public sealed class Skybox : Behavior
    {
        [Persist] public Color Top { get; set; } = Color.Parse("#1f1f1f");
        [Persist] public Color Bottom { get; set; } = Color.Parse("#0a0a0a");
        [Persist] public float Ambient { get; set; } = 0.90f;


        [Persist] public Texture2D? Texture { get; set; } = null; // optional sky texture
        [Persist] public float TextureBlend { get; set; } = 1.0f; // 0..1, over the gradient

        [Persist] public float Yaw { get; set; } = 0f;            // degrees
        [Persist] public float SeamFeather { get; set; } = 0.01f; // 0..~0.02
        [Persist] public bool KeyOutNearBlack { get; set; } = false;
        [Persist] public float KeyLuma { get; set; } = 0.08f;     // 0..1
    }
}
