namespace Game_Engine.Core.Component
{
    [ComponentCategory("Rendering")]
    public sealed class MeshFilter : Behavior
    {
        // geometry snapshot persisted by the serializer
        [Persist] public Mesh? Mesh { get; set; }
        
        [Persist] public string ModelPath { get; set; }
        [Persist] public string ModelPartIndex { get; set; }

        /// <summary>FBX node name from import. Not saved. Used to tell the trunk mesh from the crown.</summary>
        public string SourceNodeName { get; set; } = "";

        [Persist] public List<string> TargetPaths { get; private set; } = new List<string>();



    }
}
