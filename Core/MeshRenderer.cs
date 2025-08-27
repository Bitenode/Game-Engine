using Avalonia.Media;

namespace Game_Engine.Core
{
    public sealed class MeshRenderer : Behavior
    {
        [Persist] public Color Color { get; set; } = Colors.White;
        [Persist] public bool Wireframe { get; set; } = false;
        [Persist] public double LineWidth { get; set; } = 1.0;

        //WIP Setup Later/////////////////////////////
        [Persist] public bool CastShadows { get; set; }
        [Persist] public bool ReceiveShadows { get; set; }
        
        /////////////////////////////////////////////////////////
        

        [Persist] public Material? Material { get; set; } = new Material();


    }
}
