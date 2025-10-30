using Avalonia.Media;
using Game_Engine.Core.Rendering;

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

        // Persisted: one per submesh
        [Persist] public List<string> MaterialPaths = new List<string>(); // project-relative .material files

        // Runtime cache (not persisted)
        public List<Material> ResolvedMaterials = new List<Material>();

        [Persist] public Material? Material { get; set; } = new Material();

        public override void OnEnable()
        {
            base.OnEnable();
            ResolveMaterials();
        }

        public void ResolveMaterials()
        {
            ResolvedMaterials.Clear();

            // ensure count matches submeshes if you track them; otherwise use list length
            int count = MaterialPaths != null ? MaterialPaths.Count : 0;
            for (int i = 0; i < count; i++)
            {
                string rel = MaterialPaths[i];
                var m = TryLoadRuntimeMaterial(rel);
                if (m == null) m = DefaultMaterial();
                ResolvedMaterials.Add(m);
            }

            // Ensure at least one default
            if (ResolvedMaterials.Count == 0)
                ResolvedMaterials.Add(DefaultMaterial());
        }

        private Material TryLoadRuntimeMaterial(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;

            var matAsset = ProjectService.LoadMaterialAsset(rel);      
            if (matAsset == null) return null;

            var shader = ProjectService.LoadShaderAsset(matAsset.ShaderPath);
            if (shader == null) return null;

            return MaterialRuntimeBuilder.Build(matAsset, shader);
        }

        private Material DefaultMaterial()
        {
            var m = new Material();
            m.Tint = ColorUtil.FromRGBA(1, 1, 1, 1);
            m.Lit = false;
            return m;
        }
    }


}

