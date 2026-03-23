#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Canopy shape used for procedural tree generation.</summary>
    public enum CanopyShape { Sphere, Cone, LayeredCone }

    /// <summary>
    /// Tree component — procedural generation + imported model support.
    /// Generates trunk + canopy meshes, populates TreeLOD variants,
    /// and exposes per-tree wind parameters for the renderer.
    /// </summary>
    [ComponentCategory("Environment")]
    [Require(typeof(MeshFilter), typeof(MeshRenderer), typeof(TreeLOD))]
    public sealed class Tree : Behavior
    {
    sealed class ImportedModelTemplate
    {
        public Mesh? Mesh;
        public Material? Material;
    }

    static readonly Dictionary<string, ImportedModelTemplate> s_importTemplateCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object s_importTemplateLock = new();

        // ── Procedural trunk parameters ──
        [Persist] public float TrunkHeight { get; set; } = 3f;
        [Persist] public float TrunkRadiusBottom { get; set; } = 0.25f;
        [Persist] public float TrunkRadiusTop { get; set; } = 0.12f;
        [Persist] public int TrunkSegments { get; set; } = 8;

        // ── Procedural canopy parameters ──
        [Persist] public CanopyShape Shape { get; set; } = CanopyShape.Sphere;
        [Persist] public float CanopyRadius { get; set; } = 2f;
        [Persist] public float CanopyHeight { get; set; } = 2.5f;
        [Persist] public int CanopySegments { get; set; } = 10;
        [Persist] public int CanopyLayers { get; set; } = 3; // for LayeredCone

        // ── Import mode (overrides procedural when set) ──
        [Persist] public string ModelPath { get; set; } = "";
        [Persist] public string Lod1Path { get; set; } = "";
        [Persist] public string Lod2Path { get; set; } = "";

        // ── Materials ──
        [Persist] public string TrunkMaterialPath { get; set; } = "";
        [Persist] public string CanopyMaterialPath { get; set; } = "";

        // ── Wind ──
        [Persist] public float WindSway { get; set; } = 0.6f;   // 0..1
        [Persist] public float WindSpeed { get; set; } = 1f;     // multiplier
        [Persist] public bool IsVegetation { get; set; } = true; // enable wind vertex anim

        // ── Runtime ──
        private bool _meshDirty = true;

        /// <summary>True if using an imported model rather than procedural generation.</summary>
        public bool IsImportMode => !string.IsNullOrEmpty(ModelPath);

        /// <summary>Marks the tree for rebuild on next access.</summary>
        public void MarkDirty() { _meshDirty = true; SceneService.NotifyChanged(); }

        public override void Awake() => RebuildTree();

        /// <summary>Rebuild the tree mesh (procedural or imported).</summary>
        public void RebuildTree()
        {
            _meshDirty = false;
            var mf = GetComponent<MeshFilter>();
            var mrSelf = GetComponent<MeshRenderer>();
            var lod = GetComponent<TreeLOD>();
            if (mf == null) return;

            bool importedResolved = false;
            if (IsImportMode)
            {
                // Import mode — resolve meshes immediately for runtime-spawned vegetation.
                if (!string.IsNullOrEmpty(ModelPath))
                {
                    mf.ModelPath = ModelPath;
                    var lod0 = TryResolveModelMesh(ModelPath);
                    if (lod0 != null)
                    {
                        mf.Mesh = lod0;
                        importedResolved = true;
                        if (lod != null)
                            lod.Lod0 = lod0;
                    }

                    // Keep imported tree materials runtime-only (do not bind .material asset paths).
                    if (mrSelf != null)
                    {
                        mrSelf.MaterialPaths.Clear();
                        mrSelf.ResolvedMaterials.Clear();
                        var importedMat = TryResolveModelRuntimeMaterial(ModelPath);
                        if (importedMat != null)
                            mrSelf.Material = importedMat;
                    }
                }

                if (lod != null)
                {
                    var lod1 = TryResolveModelMesh(Lod1Path);
                    if (lod1 != null) lod.Lod1 = lod1;
                    var lod2 = TryResolveModelMesh(Lod2Path);
                    if (lod2 != null) lod.Lod2 = lod2;
                }
            }

            if (!IsImportMode || !importedResolved)
            {
                // Procedural mode, or fallback when import mesh failed to resolve.
                var fullMesh = GenerateProceduralTree(1f);
                mf.Mesh = fullMesh;

                if (lod != null)
                {
                    lod.Lod0 = fullMesh;
                    lod.Lod1 = GenerateProceduralTree(0.5f);
                    lod.Lod2 = GenerateProceduralTree(0.25f);
                }
            }
        }

        static Mesh? TryResolveModelMesh(string? modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return null;
            string? abs = ResolveModelPath(modelPath);
            if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
                return null;
        return ResolveImportedTemplate(abs).Mesh;
        }

        static Material? TryResolveModelRuntimeMaterial(string? modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return null;
            string? abs = ResolveModelPath(modelPath);
            if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
                return null;
        var mat = ResolveImportedTemplate(abs).Material;
        if (mat == null) return null;
        var rt = mat.Clone(copyTextures: true);
        rt.Name = string.IsNullOrWhiteSpace(rt.Name) ? "TreeImportedRuntime" : rt.Name;
        rt.ShaderAssetPath = "";
        return rt;
        }

    static ImportedModelTemplate ResolveImportedTemplate(string absModelPath)
    {
        lock (s_importTemplateLock)
        {
            if (s_importTemplateCache.TryGetValue(absModelPath, out var cached))
                return cached;
        }

        var tpl = new ImportedModelTemplate();
        try
        {
            if (SceneSerialization.ResolveMeshesFromModelPath != null)
            {
                var list = SceneSerialization.ResolveMeshesFromModelPath(absModelPath);
                if (list != null && list.Count > 0)
                    tpl.Mesh = list[0];
            }
            if (tpl.Mesh == null && SceneSerialization.ResolveMeshFromModelPath != null)
                tpl.Mesh = SceneSerialization.ResolveMeshFromModelPath(absModelPath);
        }
        catch { }

        // One-time importer pass per model path to capture source material/mesh fallback.
        try
        {
            var root = Importers.ModelImporter.ImportModel(absModelPath);
            if (tpl.Mesh == null)
                tpl.Mesh = FindFirstComponent<MeshFilter>(root)?.Mesh;

            var impMr = FindFirstComponent<MeshRenderer>(root);
            Material? src = impMr?.Material;
            if (src == null && impMr != null && impMr.MaterialPaths.Count > 0)
            {
                try { src = ProjectService.MaterialsLoad(impMr.MaterialPaths[0]); } catch { }
            }
            if (src != null)
            {
                tpl.Material = src.Clone(copyTextures: true);
                tpl.Material.ShaderAssetPath = "";
            }
        }
        catch { }

        lock (s_importTemplateLock)
        {
            if (!s_importTemplateCache.ContainsKey(absModelPath))
                s_importTemplateCache[absModelPath] = tpl;
            return s_importTemplateCache[absModelPath];
        }
    }

        static T? FindFirstComponent<T>(GameObject go) where T : Behavior
        {
            foreach (var b in go.Behaviors)
                if (b is T t) return t;
            foreach (var c in go.Children)
            {
                var t = FindFirstComponent<T>(c);
                if (t != null) return t;
            }
            return null;
        }

        static string? ResolveModelPath(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                if (Path.IsPathRooted(stored)) return stored;
                var proj = ProjectService.Current;
                if (proj == null) return stored;

                string rel = stored.Replace('\\', '/');
                string fromRoot = Path.Combine(proj.RootPath, rel);
                if (File.Exists(fromRoot)) return fromRoot;

                if (!string.IsNullOrWhiteSpace(proj.AssetsPath))
                {
                    string fromAssets = Path.Combine(proj.AssetsPath, rel);
                    if (File.Exists(fromAssets)) return fromAssets;
                }

                return fromRoot;
            }
            catch
            {
                return stored;
            }
        }

        /// <summary>
        /// Generate a procedural tree mesh (combined trunk + canopy).
        /// <paramref name="detail"/> is 0..1 multiplier for segment counts.
        /// </summary>
        Mesh GenerateProceduralTree(float detail)
        {
            detail = Math.Clamp(detail, 0.1f, 1f);
            int trunkSides = Math.Max(4, (int)(TrunkSegments * detail));
            int canopySeg = Math.Max(4, (int)(CanopySegments * detail));

            // ── Build trunk (tapered cylinder) ──
            var (trunkVerts, trunkNorms, trunkUVs, trunkTris) = BuildTrunk(trunkSides);

            // ── Build canopy ──
            var (canopyVerts, canopyNorms, canopyUVs, canopyTris) = Shape switch
            {
                CanopyShape.Cone => BuildCanopyCone(canopySeg),
                CanopyShape.LayeredCone => BuildCanopyLayered(canopySeg),
                _ => BuildCanopySphere(canopySeg)
            };

            // Offset canopy to top of trunk
            float canopyBaseY = TrunkHeight;
            for (int i = 0; i < canopyVerts.Count; i++)
            {
                var v = canopyVerts[i];
                canopyVerts[i] = new SN.Vector3(v.X, v.Y + canopyBaseY, v.Z);
            }
            for (int i = 0; i < canopyNorms.Count; i++)
            {
                var n = canopyNorms[i];
                canopyNorms[i] = n; // normals don't change with translation
            }

            // ── Merge trunk + canopy ──
            int trunkVertCount = trunkVerts.Count;
            var allVerts = new List<SN.Vector3>(trunkVerts.Count + canopyVerts.Count);
            var allNorms = new List<SN.Vector3>(trunkNorms.Count + canopyNorms.Count);
            var allUVs = new List<SN.Vector2>(trunkUVs.Count + canopyUVs.Count);
            var allTris = new List<int>(trunkTris.Count + canopyTris.Count);

            allVerts.AddRange(trunkVerts);
            allNorms.AddRange(trunkNorms);
            allUVs.AddRange(trunkUVs);
            allTris.AddRange(trunkTris);

            // Offset canopy triangle indices
            for (int i = 0; i < canopyTris.Count; i++)
                allTris.Add(canopyTris[i] + trunkVertCount);

            allVerts.AddRange(canopyVerts);
            allNorms.AddRange(canopyNorms);
            allUVs.AddRange(canopyUVs);

            // Build line indices from triangles
            var lineSet = new HashSet<(int, int)>();
            for (int i = 0; i < allTris.Count; i += 3)
            {
                AddEdge(lineSet, allTris[i], allTris[i + 1]);
                AddEdge(lineSet, allTris[i + 1], allTris[i + 2]);
                AddEdge(lineSet, allTris[i + 2], allTris[i]);
            }
            var lines = new List<int>(lineSet.Count * 2);
            foreach (var (a, b) in lineSet) { lines.Add(a); lines.Add(b); }

            var mesh = new Mesh(allVerts.ToArray(), lines.ToArray(), allTris.ToArray())
            {
                Normals = allNorms.ToArray(),
                UVs = allUVs.ToArray()
            };
            return mesh;
        }

        static void AddEdge(HashSet<(int, int)> set, int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            set.Add((a, b));
        }

        // ── Trunk: tapered cylinder along Y, from 0 to TrunkHeight ──
        (List<SN.Vector3> verts, List<SN.Vector3> norms, List<SN.Vector2> uvs, List<int> tris)
        BuildTrunk(int sides)
        {
            int rings = 4; // bottom, lower-mid, upper-mid, top
            int vertCount = (sides + 1) * rings;
            var verts = new List<SN.Vector3>(vertCount);
            var norms = new List<SN.Vector3>(vertCount);
            var uvs = new List<SN.Vector2>(vertCount);
            var tris = new List<int>();

            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1); // 0..1
                float y = t * TrunkHeight;
                float radius = MathF.Max(0.01f, TrunkRadiusBottom + (TrunkRadiusTop - TrunkRadiusBottom) * t);

                for (int s = 0; s <= sides; s++)
                {
                    float u = s / (float)sides;
                    float angle = u * MathF.Tau;
                    float x = MathF.Cos(angle) * radius;
                    float z = MathF.Sin(angle) * radius;

                    verts.Add(new SN.Vector3(x, y, z));
                    norms.Add(SN.Vector3.Normalize(new SN.Vector3(MathF.Cos(angle), 0, MathF.Sin(angle))));
                    uvs.Add(new SN.Vector2(u, t));
                }
            }

            int stride = sides + 1;
            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int a = r * stride + s;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;

                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            return (verts, norms, uvs, tris);
        }

        // ── Canopy sphere ──
        (List<SN.Vector3> verts, List<SN.Vector3> norms, List<SN.Vector2> uvs, List<int> tris)
        BuildCanopySphere(int seg)
        {
            int lon = seg, lat = Math.Max(3, seg / 2);
            var verts = new List<SN.Vector3>();
            var norms = new List<SN.Vector3>();
            var uvs = new List<SN.Vector2>();
            var tris = new List<int>();

            float rx = CanopyRadius, ry = CanopyHeight * 0.5f, rz = CanopyRadius;
            float cy = ry; // center of sphere is at canopyHeight/2

            // Top pole
            verts.Add(new SN.Vector3(0, cy + ry, 0));
            norms.Add(SN.Vector3.UnitY);
            uvs.Add(new SN.Vector2(0.5f, 0f));

            int vertsPerRing = lon + 1;
            for (int y = 1; y < lat; y++)
            {
                float v = y / (float)lat;
                float phi = v * MathF.PI;
                float sy = MathF.Cos(phi);
                float sr = MathF.Sin(phi);

                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float th = u * MathF.Tau;

                    float px = sr * MathF.Cos(th) * rx;
                    float py = sy * ry + cy;
                    float pz = sr * MathF.Sin(th) * rz;

                    verts.Add(new SN.Vector3(px, py, pz));
                    norms.Add(SN.Vector3.Normalize(new SN.Vector3(px / (rx * rx), (py - cy) / (ry * ry), pz / (rz * rz))));
                    uvs.Add(new SN.Vector2(u, v));
                }
            }

            // Bottom pole
            int botIdx = verts.Count;
            verts.Add(new SN.Vector3(0, cy - ry, 0));
            norms.Add(-SN.Vector3.UnitY);
            uvs.Add(new SN.Vector2(0.5f, 1f));

            // Top cap
            for (int x = 0; x < lon; x++)
            {
                tris.Add(0); tris.Add(1 + x + 1); tris.Add(1 + x);
            }

            // Middle
            for (int y = 1; y < lat - 1; y++)
            {
                int r0 = 1 + (y - 1) * vertsPerRing;
                int r1 = r0 + vertsPerRing;
                for (int x = 0; x < lon; x++)
                {
                    int a = r0 + x, b = r0 + x + 1;
                    int c = r1 + x, d = r1 + x + 1;
                    tris.Add(a); tris.Add(b); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(c);
                }
            }

            // Bottom cap
            int lastRing = 1 + (lat - 2) * vertsPerRing;
            for (int x = 0; x < lon; x++)
            {
                tris.Add(lastRing + x); tris.Add(lastRing + x + 1); tris.Add(botIdx);
            }

            return (verts, norms, uvs, tris);
        }

        // ── Canopy cone ──
        (List<SN.Vector3> verts, List<SN.Vector3> norms, List<SN.Vector2> uvs, List<int> tris)
        BuildCanopyCone(int seg)
        {
            var verts = new List<SN.Vector3>();
            var norms = new List<SN.Vector3>();
            var uvs = new List<SN.Vector2>();
            var tris = new List<int>();

            float r = CanopyRadius;
            float h = CanopyHeight;

            // Apex
            verts.Add(new SN.Vector3(0, h, 0));
            norms.Add(SN.Vector3.UnitY);
            uvs.Add(new SN.Vector2(0.5f, 0f));

            // Base ring
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg;
                float angle = u * MathF.Tau;
                float x = MathF.Cos(angle) * r;
                float z = MathF.Sin(angle) * r;
                verts.Add(new SN.Vector3(x, 0, z));

                // Cone normal
                float slopeAngle = MathF.Atan2(r, h);
                float ny = MathF.Sin(slopeAngle);
                float nr = MathF.Cos(slopeAngle);
                norms.Add(SN.Vector3.Normalize(new SN.Vector3(MathF.Cos(angle) * nr, ny, MathF.Sin(angle) * nr)));
                uvs.Add(new SN.Vector2(u, 1f));
            }

            // Side tris (apex → base ring)
            for (int i = 0; i < seg; i++)
            {
                tris.Add(0); tris.Add(1 + i + 1); tris.Add(1 + i);
            }

            // Base cap center
            int baseCenter = verts.Count;
            verts.Add(new SN.Vector3(0, 0, 0));
            norms.Add(-SN.Vector3.UnitY);
            uvs.Add(new SN.Vector2(0.5f, 0.5f));

            for (int i = 0; i < seg; i++)
            {
                tris.Add(baseCenter); tris.Add(1 + i); tris.Add(1 + i + 1);
            }

            return (verts, norms, uvs, tris);
        }

        // ── Canopy layered cone (stacked smaller cones) ──
        (List<SN.Vector3> verts, List<SN.Vector3> norms, List<SN.Vector2> uvs, List<int> tris)
        BuildCanopyLayered(int seg)
        {
            int layers = Math.Max(1, CanopyLayers);
            var allVerts = new List<SN.Vector3>();
            var allNorms = new List<SN.Vector3>();
            var allUVs = new List<SN.Vector2>();
            var allTris = new List<int>();

            float totalH = CanopyHeight;
            float layerH = totalH / layers;
            float maxR = CanopyRadius;

            for (int layer = 0; layer < layers; layer++)
            {
                float baseY = layer * layerH * 0.6f; // overlap layers
                float layerScale = 1f - layer * 0.25f / layers;
                float r = maxR * layerScale;
                float h = layerH * 1.2f;

                int baseIdx = allVerts.Count;

                // Apex
                allVerts.Add(new SN.Vector3(0, baseY + h, 0));
                allNorms.Add(SN.Vector3.UnitY);
                allUVs.Add(new SN.Vector2(0.5f, 0f));

                // Ring
                for (int i = 0; i <= seg; i++)
                {
                    float u = i / (float)seg;
                    float angle = u * MathF.Tau;
                    allVerts.Add(new SN.Vector3(MathF.Cos(angle) * r, baseY, MathF.Sin(angle) * r));

                    float slopeAngle = MathF.Atan2(r, h);
                    float ny = MathF.Sin(slopeAngle);
                    float nr = MathF.Cos(slopeAngle);
                    allNorms.Add(SN.Vector3.Normalize(new SN.Vector3(MathF.Cos(angle) * nr, ny, MathF.Sin(angle) * nr)));
                    allUVs.Add(new SN.Vector2(u, 1f));
                }

                for (int i = 0; i < seg; i++)
                {
                    allTris.Add(baseIdx); allTris.Add(baseIdx + 1 + i + 1); allTris.Add(baseIdx + 1 + i);
                }
            }

            return (allVerts, allNorms, allUVs, allTris);
        }
    }
}
