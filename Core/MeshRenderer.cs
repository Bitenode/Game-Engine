using Avalonia.Media;

namespace Game_Engine.Core
{
    public sealed class MeshRenderer : Behavior
    {
        public Color Color { get; set; } = Colors.White;
        public bool Wireframe { get; set; } = false;
        public double LineWidth { get; set; } = 1.0;

        public Material? Material { get; set; } = new Material();
    }
}
