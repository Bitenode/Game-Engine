namespace Game_Engine.Core.Component
{
    public sealed class MeshFilter : Behavior
    {
        // geometry snapshot persisted by the serializer
        [Persist] public Mesh? Mesh { get; set; }
        
        [Persist] public string ModelPath { get; set; }
        [Persist] public string ModelPartIndex { get; set; }


    }
}
