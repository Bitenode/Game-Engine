namespace Game_Engine.Core
{
    public sealed class MeshFilter : Behavior
    {
        // geometry snapshot persisted by the serializer
        [Persist] public Mesh? Mesh { get; set; }

       
    }
}
