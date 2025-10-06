using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    public sealed class MeshRenderer : Behavior
    {
        [Persist] public Color Color { get; set; } = Colors.White;
        [Persist] public bool Wireframe { get; set; } = false;
        [Persist] public double LineWidth { get; set; } = 1.0;
        [Persist] public bool CastShadows { get; set; } = true;
        [Persist] public bool ReceiveShadows { get; set; } = true;
        [Persist] public bool DoubleSided { get; set; } = false;
        [Persist] public bool InvertFrontFace { get; set; } = false;

        [Persist] public Material? Material { get; set; } = new Material();


    }
}
