#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Type of vegetation instance.</summary>
    public enum VegetationType { Grass, Rock, Debris, Custom }

    /// <summary>
    /// A single vegetation instance (grass blade, rock, etc.) with position, rotation, scale.
    /// Stored in bulk by VegetationPainter for GPU instanced rendering.
    /// </summary>
    public struct VegetationData
    {
        public SN.Vector3 Position;
        public float Rotation;        // Y-axis rotation in radians
        public float Scale;
        public SN.Vector3 Color;       // tint variation
        public VegetationType Type;
    }

    /// <summary>
    /// Vegetation painter component — paints GPU-instanced grass, rocks, and debris
    /// on terrain surfaces. Supports LOD-based density and wind animation.
    /// </summary>
    [ComponentCategory("Environment")]
    public sealed class VegetationPainter : Behavior
    {
        // ── Painting Settings ──
        [Persist] public VegetationType ActiveType { get; set; } = VegetationType.Grass;
        [Persist] public float BrushRadius { get; set; } = 5f;
        [Persist] public float Density { get; set; } = 10f;         // instances per unit area
        [Persist] public float MinScale { get; set; } = 0.5f;
        [Persist] public float MaxScale { get; set; } = 1.5f;
        [Persist] public bool RandomRotation { get; set; } = true;

        // ── Grass Appearance ──
        [Persist] public float GrassHeight { get; set; } = 1.0f;
        [Persist] public float GrassWidth { get; set; } = 0.4f;
        [Persist] public SN.Vector3 GrassBaseColor { get; set; } = new SN.Vector3(0.2f, 0.5f, 0.15f);
        [Persist] public SN.Vector3 GrassTipColor { get; set; } = new SN.Vector3(0.4f, 0.7f, 0.2f);
        [Persist] public float GrassColorVariation { get; set; } = 0.15f;

        // ── Wind ──
        [Persist] public float WindStrength { get; set; } = 0.5f;
        [Persist] public float WindSpeed { get; set; } = 1f;

        // ── LOD ──
        [Persist] public float FadeStartDistance { get; set; } = 30f;
        [Persist] public float FadeEndDistance { get; set; } = 50f;

        // ── Custom mesh / texture path ──
        private string _customMeshPath = "";
        [Persist] public string CustomMeshPath
        {
            get => _customMeshPath;
            set
            {
                // Normalize to project-relative if possible
                var rel = ToRelativePath(value ?? "");
                if (_customMeshPath != rel)
                {
                    _customMeshPath = rel;
                    _resolvedCachePath = null;   // force re-resolve on next access
                    _resolvedMesh = null;
                    _resolvedTex = null;
                    _resolvedMat = null;
                }
            }
        }

        // ── Explicit texture path (overrides model's built-in texture) ──
        private string _texturePath = "";
        [Persist] public string TexturePath
        {
            get => _texturePath;
            set
            {
                var rel = ToRelativePath(value ?? "");
                if (_texturePath != rel)
                {
                    _texturePath = rel;
                    _manualTex = null;
                    _manualTexLoaded = false;
                }
            }
        }
        private Texture2D? _manualTex;
        private bool _manualTexLoaded;

        // ── Cached resolved assets ──
        private string? _resolvedCachePath;
        private Mesh? _resolvedMesh;
        private Texture2D? _resolvedTex;
        private Material? _resolvedMat;

        /// <summary>Image extensions recognised as textures (vs 3D models).</summary>
        private static readonly HashSet<string> s_imageExts = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp", ".psd", ".psb" };

        /// <summary>3D model extensions.</summary>
        private static readonly HashSet<string> s_modelExts = new(StringComparer.OrdinalIgnoreCase)
            { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds" };

        /// <summary>
        /// Resolve the CustomMeshPath: if it's a 3D model, load it and cache the mesh + material.
        /// If it's an image, cache it as a texture.
        /// </summary>
        private void EnsureResolved()
        {
            if (_resolvedCachePath == CustomMeshPath) return;
            _resolvedCachePath = CustomMeshPath;
            _resolvedMesh = null;
            _resolvedTex = null;
            _resolvedMat = null;

            if (string.IsNullOrWhiteSpace(CustomMeshPath)) return;

            string ext = Path.GetExtension(CustomMeshPath);

            // ── 3D model ──
            if (s_modelExts.Contains(ext))
            {
                try
                {
                    string? absPath = ResolvePath(CustomMeshPath);
                    if (absPath != null && File.Exists(absPath))
                    {
                        var rootGO = Importers.ModelImporter.ImportModel(absPath);
                        // Find first MeshFilter in the imported hierarchy
                        var mf = FindFirstComponent<MeshFilter>(rootGO);
                        if (mf?.Mesh != null)
                            _resolvedMesh = mf.Mesh;

                        // Find first MeshRenderer and grab its material (has textures)
                        var mr = FindFirstComponent<MeshRenderer>(rootGO);
                        if (mr?.Material != null)
                            _resolvedMat = mr.Material;

                        Log.Info($"[VegetationPainter] Loaded custom mesh from '{CustomMeshPath}' " +
                                 $"({_resolvedMesh?.Vertices?.Length ?? 0} verts, mat={_resolvedMat != null})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[VegetationPainter] Failed to load model '{CustomMeshPath}': {ex.Message}");
                }
                return;
            }

            // ── Image texture (billboard grass) ──
            if (s_imageExts.Contains(ext))
            {
                try
                {
                    string? absPath = ResolvePath(CustomMeshPath);
                    if (absPath != null && File.Exists(absPath))
                        _resolvedTex = TryLoadTextureWithFallback(absPath);
                }
                catch { /* silently ignore bad textures */ }
            }
        }

        /// <summary>The custom 3D mesh loaded from CustomMeshPath (null if path is empty or an image).</summary>
        public Mesh? ResolvedMesh { get { EnsureResolved(); return _resolvedMesh; } }

        /// <summary>The material (with textures) from the loaded 3D model (null if not a model).</summary>
        public Material? ResolvedMaterial { get { EnsureResolved(); return _resolvedMat; } }

        /// <summary>
        /// Returns the texture to use for vegetation rendering.
        /// Priority: 1) explicit TexturePath  2) model's albedo texture  3) image from CustomMeshPath
        /// </summary>
        public Texture2D? ResolvedTexture
        {
            get
            {
                // 1) Explicit TexturePath always wins
                if (!string.IsNullOrWhiteSpace(TexturePath))
                {
                    if (!_manualTexLoaded)
                    {
                        _manualTexLoaded = true;
                        try
                        {
                            string? absPath = ResolvePath(TexturePath);
                            if (absPath != null && File.Exists(absPath))
                                _manualTex = TryLoadTextureWithFallback(absPath);
                        }
                        catch { _manualTex = null; }
                    }
                    if (_manualTex != null) return _manualTex;
                }

                EnsureResolved();

                // 2) Extract albedo from model's material
                if (_resolvedMat != null && _resolvedTex == null)
                {
                    foreach (var slot in _resolvedMat.Textures)
                    {
                        if (slot is RuntimeTexSlot rts && rts.Texture != null)
                        {
                            var u = rts.Usage?.ToLowerInvariant() ?? "";
                            if (u.Contains("albedo") || u == "" || u.Contains("base") || u.Contains("diff"))
                            {
                                _resolvedTex = rts.Texture;
                                break;
                            }
                        }
                    }
                }

                // 3) Image loaded from CustomMeshPath (when it's an image, not a model)
                return _resolvedTex;
            }
        }

        static Texture2D? TryLoadTextureWithFallback(string absPath)
        {
            try
            {
                return Texture2D.FromFile(absPath);
            }
            catch
            {
                // PSD decode can fail depending on codec support; try sibling flattened exports.
                string ext = Path.GetExtension(absPath).ToLowerInvariant();
                if (!string.Equals(ext, ".psd", StringComparison.OrdinalIgnoreCase))
                    return null;

                string dir = Path.GetDirectoryName(absPath) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(absPath);
                string[] fallbackExts = { ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".tiff", ".webp" };
                for (int i = 0; i < fallbackExts.Length; i++)
                {
                    string candidate = Path.Combine(dir, baseName + fallbackExts[i]);
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        return Texture2D.FromFile(candidate);
                    }
                    catch { }
                }

                // Broader fallback for texture-pack naming differences:
                // pick the closest image filename in the same folder.
                try
                {
                    if (!Directory.Exists(dir)) return null;

                    static string Norm(string s)
                    {
                        var chars = s.Where(char.IsLetterOrDigit).ToArray();
                        return new string(chars).ToLowerInvariant();
                    }

                    string normBase = Norm(baseName);
                    var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(p =>
                        {
                            string e = Path.GetExtension(p).ToLowerInvariant();
                            return e == ".png" || e == ".tga" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".tiff" || e == ".webp";
                        })
                        .Select(p => new
                        {
                            Path = p,
                            Name = Path.GetFileNameWithoutExtension(p),
                            Ext = Path.GetExtension(p).ToLowerInvariant()
                        })
                        .ToList();

                    if (files.Count == 0) return null;

                    int Score(string name, string ext2)
                    {
                        string n = Norm(name);
                        if (n == normBase) return 1000;
                        if (n.StartsWith(normBase, StringComparison.Ordinal) || normBase.StartsWith(n, StringComparison.Ordinal)) return 800;
                        if (n.Contains(normBase, StringComparison.Ordinal) || normBase.Contains(n, StringComparison.Ordinal)) return 600;

                        // Token overlap fallback
                        var t1 = normBase.Chunk(4).Select(c => new string(c)).ToHashSet(StringComparer.Ordinal);
                        var t2 = n.Chunk(4).Select(c => new string(c)).ToHashSet(StringComparer.Ordinal);
                        int overlap = t1.Count == 0 ? 0 : t1.Count(x => t2.Contains(x));
                        int extPref = ext2 switch
                        {
                            ".png" => 40,
                            ".tga" => 30,
                            ".jpg" => 20,
                            ".jpeg" => 18,
                            ".bmp" => 10,
                            ".tiff" => 8,
                            ".webp" => 6,
                            _ => 0
                        };
                        return overlap * 5 + extPref;
                    }

                    var best = files
                        .Select(f => new { f.Path, Score = Score(f.Name, f.Ext) })
                        .OrderByDescending(x => x.Score)
                        .FirstOrDefault();

                    if (best != null && best.Score > 0)
                    {
                        try { return Texture2D.FromFile(best.Path); } catch { }
                    }
                }
                catch { }

                return null;
            }
        }

        /// <summary>Find the first component of type T in a GameObject hierarchy.</summary>
        private static T? FindFirstComponent<T>(GameObject go) where T : Behavior
        {
            foreach (var b in go.Behaviors)
                if (b is T t) return t;
            foreach (var child in go.Children)
            {
                var t = FindFirstComponent<T>(child);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Resolve a path the same way other importers do.</summary>
        internal static string? ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Path.IsPathRooted(path) && File.Exists(path)) return path;

            var root = ProjectService.Current?.RootPath;
            if (!string.IsNullOrEmpty(root))
            {
                var candidate = Path.GetFullPath(Path.Combine(root, path));
                if (File.Exists(candidate)) return candidate;

                candidate = Path.GetFullPath(Path.Combine(root, "Assets", path));
                if (File.Exists(candidate)) return candidate;

                var assetsDir = Path.Combine(root, "Assets");
                if (Directory.Exists(assetsDir))
                {
                    try
                    {
                        var found = Directory.GetFiles(assetsDir, Path.GetFileName(path), SearchOption.AllDirectories);
                        if (found.Length > 0) return found[0];
                    }
                    catch { }
                }
            }

            if (File.Exists(path)) return Path.GetFullPath(path);
            return null;
        }

        // ── Instance data ──
        private List<VegetationData> _instances = new(4096);
        private bool _dirty = true;

        /// <summary>All vegetation instances (data only, for legacy/GPU path).</summary>
        public IReadOnlyList<VegetationData> Instances => _instances;
        /// <summary>Count of active chunks (each chunk = one draw call).</summary>
        public int InstanceCount
        {
            get
            {
                if (_chunks.Count > 0) return _chunks.Count;
                if (gameObject != null)
                {
                    foreach (var child in gameObject.Children)
                        if (child.Name == "Grass") return child.Children.Count;
                }
                return _instances.Count;
            }
        }
        public bool IsDirty => _dirty;

        /// <summary>Mark data as needing GPU re-upload.</summary>
        public void MarkDirty() => _dirty = true;
        public void ClearDirty() => _dirty = false;

        /// <summary>Add instances in a circular brush area.</summary>
        public void Paint(SN.Vector3 center, float radius, Terrain? terrain = null)
        {
            var rng = new Random();
            int count = (int)(Density * radius * radius * MathF.PI);

            for (int i = 0; i < count; i++)
            {
                // Random position within circle
                float angle = (float)rng.NextDouble() * MathF.Tau;
                float dist = MathF.Sqrt((float)rng.NextDouble()) * radius;
                float x = center.X + MathF.Cos(angle) * dist;
                float z = center.Z + MathF.Sin(angle) * dist;
                float y = center.Y;

                // Sample terrain height if available
                if (terrain != null)
                {
                    if (terrain.SampleHeightWorld(x, z, out float sampledY, out _))
                        y = sampledY;
                }

                float scale = MinScale + (float)rng.NextDouble() * (MaxScale - MinScale);
                float rot = RandomRotation ? (float)rng.NextDouble() * MathF.Tau : 0f;

                // Color variation
                var baseColor = ActiveType == VegetationType.Grass ? GrassBaseColor : SN.Vector3.One;
                var colorVar = new SN.Vector3(
                    (float)(rng.NextDouble() - 0.5) * 2f * GrassColorVariation,
                    (float)(rng.NextDouble() - 0.5) * 2f * GrassColorVariation,
                    (float)(rng.NextDouble() - 0.5) * 2f * GrassColorVariation);

                _instances.Add(new VegetationData
                {
                    Position = new SN.Vector3(x, y, z),
                    Rotation = rot,
                    Scale = scale,
                    Color = baseColor + colorVar,
                    Type = ActiveType
                });
            }

            _dirty = true;
            SceneService.NotifyChanged();
        }

        /// <summary>Erase instances within a radius.</summary>
        public void Erase(SN.Vector3 center, float radius)
        {
            float r2 = radius * radius;
            _instances.RemoveAll(v =>
            {
                float dx = v.Position.X - center.X;
                float dz = v.Position.Z - center.Z;
                return dx * dx + dz * dz <= r2;
            });
            _dirty = true;
            SceneService.NotifyChanged();
        }

        /// <summary>Clear all vegetation GameObjects and instance data.</summary>
        public void ClearAll()
        {
            _instances.Clear();
            _dirty = true;
            _chunks.Clear();
            GrassBuilt = false;

            // Remove all child GameObjects created by BuildOnTerrain
            if (gameObject != null)
            {
                for (int i = gameObject.Children.Count - 1; i >= 0; i--)
                {
                    var child = gameObject.Children[i];
                    if (child.Name == "Grass" || child.Name.StartsWith("grass_") || child.Name.StartsWith("chunk_"))
                        child.RemoveFromParent();
                }
            }

            SceneService.NotifyChanged();
        }

        /// <summary>Max total instances as real GameObjects.</summary>
        private const int MaxBuildInstances = 5000;

        /// <summary>Exclusion radius around existing models — grass won't spawn within this distance.</summary>
        [Persist] public float ModelExclusionRadius { get; set; } = 2f;

        /// <summary>If true, grass only spawns in areas covered by Water components.</summary>
        [Persist] public bool IsWaterPlant { get; set; } = false;

        /// <summary>If true, grass was built and should auto-rebuild on scene load.</summary>
        [Persist] public bool GrassBuilt { get; set; } = false;

        /// <summary>
        /// Convert a path to project-relative if possible.
        /// </summary>
        internal static string ToRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var root = ProjectService.Current?.RootPath;
                if (string.IsNullOrWhiteSpace(root)) return path.Replace('\\', '/');
                var abs = Path.GetFullPath(path);
                var projRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
                if (abs.StartsWith(projRoot, StringComparison.OrdinalIgnoreCase))
                    return abs.Substring(projRoot.Length).Replace('\\', '/');
            }
            catch { }
            return path.Replace('\\', '/');
        }

        /// <summary>Axis-aligned bounding box for model exclusion (XZ footprint).</summary>
        private struct ExclusionBox
        {
            public float MinX, MaxX, MinZ, MaxZ;
            public bool Contains(float x, float z, float pad)
                => x >= MinX - pad && x <= MaxX + pad && z >= MinZ - pad && z <= MaxZ + pad;
        }

        /// <summary>Size of each spatial chunk in world units. Chunks are culled as a group.</summary>
        private const float ChunkSize = 100f;

        /// <summary>Cached chunk data for fast per-frame culling.</summary>
        private struct ChunkInfo
        {
            public GameObject ChunkGO;
            public float CenterX, CenterY, CenterZ;
        }
        private readonly List<ChunkInfo> _chunks = new();

        /// <summary>Temporary per-chunk data accumulated during placement pass.</summary>
        private struct BladeInstance
        {
            public float X, Y, Z, RotY, Scale;
        }

        /// <summary>
        /// Build grass across the entire terrain surface using merged meshes per chunk.
        /// Each chunk is ONE draw call with all grass blade geometry baked in.
        /// Hierarchy: VegetationPainter > Grass > chunk_0_0, chunk_1_0, ...
        /// </summary>
        public int BuildOnTerrain()
        {
            ClearAll();
            if (gameObject == null) return 0;

            // Find terrain
            Terrain? terrain = null;
            foreach (var root in SceneService.Root)
            {
                terrain = FindTerrain(root);
                if (terrain != null) break;
            }

            // Find all water
            var waterComponents = new List<Water>();
            foreach (var root in SceneService.Root)
                FindAllComponents(root, waterComponents);

            // Resolve mesh and texture
            var grassMesh = ResolvedMesh ?? CreateGrassBladeMesh();
            var grassTex = PrepareGrassTextureForCutout(ResolvedTexture);

            string relTexPath = !string.IsNullOrWhiteSpace(TexturePath) ? ToRelativePath(TexturePath) : "";

            var grassMat = BuildGrassMaterial(grassTex, relTexPath);

            // Model exclusion
            var excludeBoxes = new List<ExclusionBox>();
            CollectModelAABBs(SceneService.Root, excludeBoxes);
            float pad = ModelExclusionRadius;

            // Create "Grass" container
            var grassParent = new GameObject("Grass");
            gameObject.AddChild(grassParent);

            // Terrain bounds (centered)
            float sizeX = 40f, sizeZ = 40f;
            float ox = 0f, oy = 0f, oz = 0f;
            if (terrain != null)
            {
                var tgo = terrain.gameObject;
                ox = (float)(tgo?.Transform?.Position.X ?? 0);
                oy = (float)(tgo?.Transform?.Position.Y ?? 0);
                oz = (float)(tgo?.Transform?.Position.Z ?? 0);
                sizeX = terrain.SizeX;
                sizeZ = terrain.SizeZ;
            }

            float minX = ox - sizeX * 0.5f;
            float minZ = oz - sizeZ * 0.5f;

            // Chunk grid
            int chunksX = Math.Max(1, (int)Math.Ceiling(sizeX / ChunkSize));
            int chunksZ = Math.Max(1, (int)Math.Ceiling(sizeZ / ChunkSize));
            float chunkW = sizeX / chunksX;
            float chunkH = sizeZ / chunksZ;

            // Accumulate blades per chunk
            var chunkBlades = new List<BladeInstance>[chunksX, chunksZ];
            for (int cx = 0; cx < chunksX; cx++)
            for (int cz = 0; cz < chunksZ; cz++)
                chunkBlades[cx, cz] = new List<BladeInstance>();

            // Density capping
            float area = sizeX * sizeZ;
            float effectiveDensity = Density;
            if (area * effectiveDensity > MaxBuildInstances)
            {
                effectiveDensity = MaxBuildInstances / area;
                Log.Warning($"[VegetationPainter] Density reduced from {Density:F1} to {effectiveDensity:F2} " +
                            $"to cap at {MaxBuildInstances} for {sizeX:F0}x{sizeZ:F0} terrain.");
            }

            var rng = new Random(42);
            int targetCount = Math.Min((int)(area * effectiveDensity), MaxBuildInstances);
            int placed = 0;
            int skipped = 0;

            // ── Placement pass: generate valid positions ──
            for (int i = 0; i < targetCount; i++)
            {
                float x = minX + (float)rng.NextDouble() * sizeX;
                float z = minZ + (float)rng.NextDouble() * sizeZ;
                float y = oy;

                if (terrain != null)
                {
                    if (!terrain.SampleHeightWorld(x, z, out float sampledY, out _))
                    { skipped++; continue; }
                    y = sampledY;
                }

                // Water check
                if (IsWaterPlant)
                {
                    bool under = false;
                    foreach (var w in waterComponents) { if (y <= w.SampleHeight(x, z)) { under = true; break; } }
                    if (!under) { skipped++; continue; }
                }
                else
                {
                    bool under = false;
                    foreach (var w in waterComponents) { if (y <= w.SampleHeight(x, z)) { under = true; break; } }
                    if (under) { skipped++; continue; }
                }

                // Model exclusion
                bool blocked = false;
                for (int e = 0; e < excludeBoxes.Count; e++)
                {
                    if (excludeBoxes[e].Contains(x, z, pad)) { blocked = true; break; }
                }
                if (blocked) { skipped++; continue; }

                float scale = MinScale + (float)rng.NextDouble() * (MaxScale - MinScale);
                float rotY = RandomRotation ? (float)rng.NextDouble() * 360f : 0f;

                int ci = Math.Clamp((int)((x - minX) / chunkW), 0, chunksX - 1);
                int cj = Math.Clamp((int)((z - minZ) / chunkH), 0, chunksZ - 1);
                chunkBlades[ci, cj].Add(new BladeInstance { X = x, Y = y, Z = z, RotY = rotY, Scale = scale });
                placed++;
            }

            // ── Build merged mesh per chunk ──
            var srcVerts = grassMesh.Vertices ?? Array.Empty<SN.Vector3>();
            var srcNorms = grassMesh.Normals ?? Array.Empty<SN.Vector3>();
            var srcUVs = grassMesh.UVs ?? Array.Empty<SN.Vector2>();
            var srcTris = grassMesh.TriIndices ?? Array.Empty<int>();
            int vPerBlade = srcVerts.Length;
            int tPerBlade = srcTris.Length;

            _chunks.Clear();

            for (int cx = 0; cx < chunksX; cx++)
            for (int cz = 0; cz < chunksZ; cz++)
            {
                var blades = chunkBlades[cx, cz];
                if (blades.Count == 0) continue;

                int totalV = blades.Count * vPerBlade;
                int totalT = blades.Count * tPerBlade;
                var mergedVerts = new SN.Vector3[totalV];
                var mergedNorms = new SN.Vector3[totalV];
                var mergedUVs = new SN.Vector2[totalV];
                var mergedTris = new int[totalT];

                for (int b = 0; b < blades.Count; b++)
                {
                    var blade = blades[b];
                    float rad = blade.RotY * MathF.PI / 180f;
                    float cosR = MathF.Cos(rad), sinR = MathF.Sin(rad);
                    float s = blade.Scale;

                    int vOff = b * vPerBlade;
                    int tOff = b * tPerBlade;

                    // Transform each vertex: scale, rotate Y, translate
                    for (int v = 0; v < vPerBlade; v++)
                    {
                        float lx = srcVerts[v].X * s;
                        float ly = srcVerts[v].Y * s;
                        float lz = srcVerts[v].Z * s;
                        // Rotate around Y
                        float rx = lx * cosR + lz * sinR;
                        float rz = -lx * sinR + lz * cosR;
                        mergedVerts[vOff + v] = new SN.Vector3(rx + blade.X, ly + blade.Y, rz + blade.Z);

                        // Rotate normal
                        if (v < srcNorms.Length)
                        {
                            float nx = srcNorms[v].X * cosR + srcNorms[v].Z * sinR;
                            float nz = -srcNorms[v].X * sinR + srcNorms[v].Z * cosR;
                            mergedNorms[vOff + v] = new SN.Vector3(nx, srcNorms[v].Y, nz);
                        }
                        else mergedNorms[vOff + v] = SN.Vector3.UnitY;

                        mergedUVs[vOff + v] = v < srcUVs.Length ? srcUVs[v] : SN.Vector2.Zero;
                    }

                    // Offset triangle indices
                    for (int t = 0; t < tPerBlade; t++)
                        mergedTris[tOff + t] = srcTris[t] + vOff;
                }

                var chunkMesh = new Mesh(mergedVerts, Array.Empty<int>(), mergedTris)
                {
                    Normals = mergedNorms,
                    UVs = mergedUVs
                };

                // Create chunk GameObject with ONE MeshFilter + MeshRenderer
                var chunkGO = new GameObject($"chunk_{cx}_{cz}");

                var mf = new MeshFilter { Mesh = chunkMesh };
                chunkGO.AddBehavior(mf);

                var mr = new MeshRenderer { Material = grassMat, DoubleSided = true };
                chunkGO.AddBehavior(mr);

                grassParent.AddChild(chunkGO);

                float centerX = minX + (cx + 0.5f) * chunkW;
                float centerZ = minZ + (cz + 0.5f) * chunkH;
                _chunks.Add(new ChunkInfo { ChunkGO = chunkGO, CenterX = centerX, CenterY = oy, CenterZ = centerZ });
            }

            GrassBuilt = placed > 0;
            SceneService.NotifyChanged();
            Log.Info($"[VegetationPainter] Built {placed} grass blades in {_chunks.Count} chunks (skipped {skipped}). " +
                     $"Draw calls = {_chunks.Count} max.");
            return placed;
        }

        /// <summary>
        /// Build a single grass patch on a planet surface around a sphere direction.
        /// Keeps Terrain workflow untouched; this is planet-only authoring support.
        /// </summary>
        public int BuildOnPlanetPatch(PlanetTerrain planet, SN.Vector3 centerDir, float patchRadius, int bladeCount)
        {
            if (planet == null || planet.gameObject == null) return 0;
            if (bladeCount <= 0) return 0;

            ClearAll();

            // Resolve mesh/texture for this painter's current asset setup.
            var grassMesh = ResolvedMesh ?? CreateGrassBladeMesh();
            var grassTex = PrepareGrassTextureForCutout(ResolvedTexture);
            string relTexPath = !string.IsNullOrWhiteSpace(TexturePath) ? ToRelativePath(TexturePath) : "";
            var grassMat = BuildGrassMaterial(grassTex, relTexPath);

            // Create a single chunk for this clump patch.
            var chunkGO = new GameObject("chunk_planet_0");
            gameObject.AddChild(chunkGO);

            var rng = new Random(42 + bladeCount);
            var center = SceneGraphUtil.AccumulateWorld(planet.gameObject);
            var planetCenter = new SN.Vector3(center.M41, center.M42, center.M43);
            var selfWorld = SceneGraphUtil.AccumulateWorld(gameObject!);
            SN.Matrix4x4.Invert(selfWorld, out var selfWorldInv);

            var n = SN.Vector3.Normalize(centerDir);
            var t = SN.Vector3.Cross(MathF.Abs(n.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, n);
            if (t.LengthSquared() < 1e-8f) t = SN.Vector3.UnitX;
            t = SN.Vector3.Normalize(t);
            var b = SN.Vector3.Normalize(SN.Vector3.Cross(n, t));

            var srcVerts = grassMesh.Vertices ?? Array.Empty<SN.Vector3>();
            var srcNorms = grassMesh.Normals ?? Array.Empty<SN.Vector3>();
            var srcUVs = grassMesh.UVs ?? Array.Empty<SN.Vector2>();
            var srcTris = grassMesh.TriIndices ?? Array.Empty<int>();
            int vPerBlade = srcVerts.Length;
            int tPerBlade = srcTris.Length;
            if (vPerBlade == 0 || tPerBlade == 0) return 0;
            float srcMinY = srcVerts[0].Y;
            for (int vi = 1; vi < vPerBlade; vi++)
                if (srcVerts[vi].Y < srcMinY) srcMinY = srcVerts[vi].Y;

            int totalV = bladeCount * vPerBlade;
            int totalT = bladeCount * tPerBlade;
            var mergedVerts = new SN.Vector3[totalV];
            var mergedNorms = new SN.Vector3[totalV];
            var mergedUVs = new SN.Vector2[totalV];
            var mergedTris = new int[totalT];

            SN.Vector3 centerAccum = SN.Vector3.Zero;
            for (int bi = 0; bi < bladeCount; bi++)
            {
                float ang = (float)rng.NextDouble() * MathF.Tau;
                float rad = MathF.Sqrt((float)rng.NextDouble()) * Math.Max(0.05f, patchRadius);
                var offset = t * (MathF.Cos(ang) * rad) + b * (MathF.Sin(ang) * rad);
                var approx = planetCenter + n * planet.SampleSurfaceRadius(n) + offset;
                var dir = SN.Vector3.Normalize(approx - planetCenter);
                float surf = planet.SampleSurfaceRadius(dir);
                var basePos = planetCenter + dir * surf;
                var surfN = SamplePlanetSurfaceNormal(planet, planetCenter, dir);
                // Use radial-up as the contact anchor and blend in slope normal for visual tilt.
                // This prevents floating caused by aggressive normal deviations on steep relief.
                var placeUp = SafeNormalize(SN.Vector3.Lerp(dir, surfN, 0.45f), dir);

                // Build local basis where Y aligns to sampled terrain surface normal.
                var side = SN.Vector3.Cross(MathF.Abs(placeUp.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, placeUp);
                if (side.LengthSquared() < 1e-8f) side = SN.Vector3.UnitX;
                side = SN.Vector3.Normalize(side);
                var fwd = SN.Vector3.Normalize(SN.Vector3.Cross(placeUp, side));
                float yaw = RandomRotation ? (float)rng.NextDouble() * MathF.Tau : 0f;
                float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
                var xAxis = side * cy + fwd * sy;
                var zAxis = -side * sy + fwd * cy;

                float scale = MinScale + (float)rng.NextDouble() * (MaxScale - MinScale);
                float rootEmbed = Math.Clamp(Math.Max(0.10f, GrassHeight * 0.28f) * Math.Max(0.6f, scale), 0.10f, 0.65f);
                int vOff = bi * vPerBlade;
                int triOff = bi * tPerBlade;

                for (int vi = 0; vi < vPerBlade; vi++)
                {
                    // Anchor to mesh bottom so imported meshes whose pivot is centered
                    // still sit on the terrain surface correctly.
                    var sv = srcVerts[vi];
                    var lv = new SN.Vector3(sv.X * scale, (sv.Y - srcMinY) * scale, sv.Z * scale);
                    var wp = (basePos - dir * rootEmbed) + xAxis * lv.X + placeUp * lv.Y + zAxis * lv.Z;
                    mergedVerts[vOff + vi] = SN.Vector3.Transform(wp, selfWorldInv);

                    var ln = vi < srcNorms.Length ? srcNorms[vi] : SN.Vector3.UnitY;
                    var wn = xAxis * ln.X + placeUp * ln.Y + zAxis * ln.Z;
                    if (wn.LengthSquared() <= 1e-8f) wn = placeUp;
                    wn = SN.Vector3.Normalize(wn);
                    mergedNorms[vOff + vi] = SN.Vector3.Normalize(SN.Vector3.TransformNormal(wn, selfWorldInv));
                    mergedUVs[vOff + vi] = vi < srcUVs.Length ? srcUVs[vi] : SN.Vector2.Zero;
                }

                centerAccum += basePos;

                for (int ti = 0; ti < tPerBlade; ti++)
                    mergedTris[triOff + ti] = srcTris[ti] + vOff;
            }

            var chunkMesh = new Mesh(mergedVerts, Array.Empty<int>(), mergedTris)
            {
                Normals = mergedNorms,
                UVs = mergedUVs
            };

            var mf = new MeshFilter { Mesh = chunkMesh };
            chunkGO.AddBehavior(mf);
            var mr = new MeshRenderer { Material = grassMat, DoubleSided = true };
            chunkGO.AddBehavior(mr);
            _chunks.Add(new ChunkInfo
            {
                ChunkGO = chunkGO,
                CenterX = (centerAccum / bladeCount).X,
                CenterY = (centerAccum / bladeCount).Y,
                CenterZ = (centerAccum / bladeCount).Z
            });

            GrassBuilt = true;
            SceneService.NotifyChanged();
            return bladeCount;
        }

        static SN.Vector3 SamplePlanetSurfaceNormal(PlanetTerrain planet, SN.Vector3 planetCenter, SN.Vector3 dir)
        {
            if (dir.LengthSquared() < 1e-8f)
                return SN.Vector3.UnitY;
            dir = SN.Vector3.Normalize(dir);

            var t = SN.Vector3.Cross(MathF.Abs(dir.Y) > 0.95f ? SN.Vector3.UnitX : SN.Vector3.UnitY, dir);
            if (t.LengthSquared() < 1e-8f)
                t = SN.Vector3.UnitX;
            t = SN.Vector3.Normalize(t);
            var b = SN.Vector3.Normalize(SN.Vector3.Cross(dir, t));

            // Small angular offset to estimate local slope from neighboring samples.
            const float eps = 0.0018f;
            var dirT = SN.Vector3.Normalize(dir + t * eps);
            var dirB = SN.Vector3.Normalize(dir + b * eps);

            var p0 = planetCenter + dir * planet.SampleSurfaceRadius(dir);
            var pT = planetCenter + dirT * planet.SampleSurfaceRadius(dirT);
            var pB = planetCenter + dirB * planet.SampleSurfaceRadius(dirB);

            var n = SN.Vector3.Cross(pT - p0, pB - p0);
            if (n.LengthSquared() < 1e-8f)
                return dir;
            n = SN.Vector3.Normalize(n);
            if (SN.Vector3.Dot(n, dir) < 0f)
                n = -n;
            return n;
        }

        static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
        {
            float lsq = v.LengthSquared();
            if (lsq < 1e-8f) return fallback;
            return v / MathF.Sqrt(lsq);
        }

        static Material BuildGrassMaterial(Texture2D? grassTex, string relTexPath)
        {
            grassTex ??= CreateFallbackGrassCutoutTexture();
            var grassMat = new Material
            {
                Name = "VegetationGrass",
                BaseColor = ColorUtil.FromRGBA(154f / 255f, 196f / 255f, 122f / 255f, 1f),
                Roughness = 0.8f,
                Metallic = 0f,
                // Always keep grass in alpha-cutout mode to avoid opaque billboard cards.
                AlphaCutoff = 0.56f,
            };
            if (grassTex != null)
            {
                grassMat.Textures.Add(new RuntimeTexSlot
                {
                    Texture = grassTex,
                    Usage = "Albedo",
                    SourcePath = relTexPath
                });
            }
            return grassMat;
        }

        static Texture2D CreateFallbackGrassCutoutTexture()
        {
            const int w = 32;
            const int h = 128;
            var rgba = new byte[w * h * 4];
            float cx = (w - 1) * 0.5f;
            float halfBlade = w * 0.13f;
            for (int y = 0; y < h; y++)
            {
                float fy = y / (float)(h - 1);
                // Blade narrows toward the tip.
                float bladeHalf = halfBlade * (1f - fy * 0.65f);
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float dx = MathF.Abs(x - cx);
                    float edge = 1f - Math.Clamp((dx - bladeHalf) / Math.Max(0.001f, bladeHalf * 0.55f), 0f, 1f);
                    float tip = 1f - MathF.Pow(fy, 1.8f);
                    float a = edge * Math.Clamp(tip + 0.15f, 0f, 1f);
                    if (a < 0.04f)
                    {
                        rgba[i + 0] = 0;
                        rgba[i + 1] = 0;
                        rgba[i + 2] = 0;
                        rgba[i + 3] = 0;
                        continue;
                    }

                    float g = 0.45f + fy * 0.35f;
                    float r = 0.16f + fy * 0.12f;
                    float b = 0.12f + fy * 0.08f;
                    rgba[i + 0] = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                    rgba[i + 1] = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                    rgba[i + 2] = (byte)Math.Clamp((int)(b * 255f), 0, 255);
                    rgba[i + 3] = (byte)Math.Clamp((int)(a * 255f), 0, 255);
                }
            }
            return new Texture2D(w, h, rgba);
        }

        Texture2D? PrepareGrassTextureForCutout(Texture2D? src)
        {
            if (src == null) return null;
            if (ActiveType != VegetationType.Grass) return src;
            if (src.Rgba == null || src.Rgba.Length < 4) return src;

            int w = src.Width;
            int h = src.Height;
            if (w <= 2 || h <= 2) return src;

            // If image already carries useful alpha, keep it as-is.
            bool hasExistingAlpha = false;
            for (int i = 3; i < src.Rgba.Length; i += 4)
            {
                byte a = src.Rgba[i];
                if (a > 4 && a < 250) { hasExistingAlpha = true; break; }
            }
            if (hasExistingAlpha) return src;

            byte[] rgba = new byte[src.Rgba.Length];
            Buffer.BlockCopy(src.Rgba, 0, rgba, 0, rgba.Length);

            // Estimate "background" color from image corners.
            int bandX = Math.Max(2, w / 10);
            int bandY = Math.Max(2, h / 10);
            float bgR = 0f, bgG = 0f, bgB = 0f;
            int bgCount = 0;

            void Acc(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x * 4;
                        bgR += rgba[i + 0];
                        bgG += rgba[i + 1];
                        bgB += rgba[i + 2];
                        bgCount++;
                    }
                }
            }

            Acc(0, 0, bandX, bandY);
            Acc(w - bandX, 0, w, bandY);
            Acc(0, h - bandY, bandX, h);
            Acc(w - bandX, h - bandY, w, h);
            if (bgCount > 0)
            {
                bgR /= bgCount;
                bgG /= bgCount;
                bgB /= bgCount;
            }

            // Build alpha mask from color-distance to background + green chroma.
            int kept = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    float r = rgba[i + 0];
                    float g = rgba[i + 1];
                    float b = rgba[i + 2];

                    float dr = r - bgR;
                    float dg = g - bgG;
                    float db = b - bgB;
                    float dist = MathF.Sqrt(dr * dr + dg * dg + db * db) / 441.67294f; // max rgb distance normalize

                    float nr = r / 255f;
                    float ng = g / 255f;
                    float nb = b / 255f;
                    float max = MathF.Max(nr, MathF.Max(ng, nb));
                    float min = MathF.Min(nr, MathF.Min(ng, nb));
                    float sat = max > 1e-4f ? (max - min) / max : 0f;
                    float greenScore = ng - MathF.Max(nr, nb); // >0 means greener than red/blue

                    float aDist = SmoothStep(0.08f, 0.28f, dist);
                    float aGreen = SmoothStep(0.020f, 0.20f, greenScore);
                    float aSat = SmoothStep(0.12f, 0.40f, sat);
                    float alpha = MathF.Max(aGreen, aDist * 0.55f + aSat * 0.28f);

                    // Hard reject obvious background.
                    if (dist < 0.06f && sat < 0.14f) alpha = 0f;
                    if (greenScore < 0.012f && dist < 0.16f) alpha = 0f;
                    if (alpha < 0.20f) alpha = 0f;

                    byte outA = (byte)Math.Clamp((int)(MathF.Pow(alpha, 0.90f) * 255f), 0, 255);
                    rgba[i + 3] = outA;
                    if (outA > 20) kept++;
                }
            }

            // If too much of the image remains opaque, apply a stricter second pass
            // to prevent giant background cards from surviving.
            float keepRatio = kept / (float)(w * h);
            if (keepRatio > 0.42f)
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        float r = rgba[i + 0];
                        float g = rgba[i + 1];
                        float b = rgba[i + 2];
                        float dr = r - bgR;
                        float dg = g - bgG;
                        float db = b - bgB;
                        float dist = MathF.Sqrt(dr * dr + dg * dg + db * db) / 441.67294f;
                        float nr = r / 255f;
                        float ng = g / 255f;
                        float nb = b / 255f;
                        float max = MathF.Max(nr, MathF.Max(ng, nb));
                        float min = MathF.Min(nr, MathF.Min(ng, nb));
                        float sat = max > 1e-4f ? (max - min) / max : 0f;
                        float greenScore = ng - MathF.Max(nr, nb);
                        float alpha = SmoothStep(0.14f, 0.36f, dist) * 0.65f
                                    + SmoothStep(0.04f, 0.24f, greenScore) * 0.55f
                                    + SmoothStep(0.16f, 0.48f, sat) * 0.25f;
                        if (dist < 0.09f && sat < 0.20f) alpha = 0f;
                        if (greenScore < 0.03f && dist < 0.22f) alpha = 0f;
                        if (alpha < 0.28f) alpha = 0f;
                        rgba[i + 3] = (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(alpha, 0f, 1f), 0.95f) * 255f), 0, 255);
                    }
                }
            }

            // Crop out fully transparent borders to reduce billboard/card feeling.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int a = rgba[row + x * 4 + 3];
                    if (a <= 16) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
                return new Texture2D(w, h, rgba);

            int cw = maxX - minX + 1;
            int ch = maxY - minY + 1;
            // Avoid tiny over-crops on already good atlases.
            if (cw >= w * 0.92f && ch >= h * 0.92f)
                return new Texture2D(w, h, rgba);

            byte[] cropped = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++)
            {
                int srcRow = (minY + y) * w * 4;
                int dstRow = y * cw * 4;
                Buffer.BlockCopy(rgba, srcRow + minX * 4, cropped, dstRow, cw * 4);
            }
            return new Texture2D(cw, ch, cropped);
        }

        static float SmoothStep(float e0, float e1, float x)
        {
            if (e1 <= e0) return x >= e1 ? 1f : 0f;
            float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Per-frame distance culling: enable/disable chunk MeshRenderers based on camera distance.
        /// Only chunks within FadeEndDistance render. Cost: one distance check per chunk.
        /// </summary>
        public override void Update()
        {
            if (_chunks.Count == 0) return;

            var cam = CameraService.MainOrFirst();
            if (cam?.gameObject == null) return;

            float camX = (float)cam.Transform.Position.X;
            float camY = (float)cam.Transform.Position.Y;
            float camZ = (float)cam.Transform.Position.Z;
            float chunkDiag = ChunkSize * 0.9f;
            float cullDist = FadeEndDistance + chunkDiag;
            float cullDist2 = cullDist * cullDist;

            for (int i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                float dx = chunk.CenterX - camX;
                float dy = chunk.CenterY - camY;
                float dz = chunk.CenterZ - camZ;
                float dist2 = dx * dx + dy * dy + dz * dz;
                bool visible = dist2 <= cullDist2;

                // Toggle chunk's own MeshRenderer (one per chunk)
                foreach (var b in chunk.ChunkGO.Behaviors)
                {
                    if (b is MeshRenderer mr && mr.Enabled != visible)
                        mr.SetEnabledSilent(visible);
                }
            }
        }

        /// <summary>Create a simple cross-billboard grass mesh as fallback.</summary>
        private Mesh CreateGrassBladeMesh()
        {
            float halfW = Math.Clamp(GrassWidth * 0.5f, 0.02f, 0.35f);
            float h = Math.Clamp(GrassHeight, 0.2f, 3.5f);
            var vertices = new SN.Vector3[]
            {
                new(-halfW, 0f, 0f), new(halfW, 0f, 0f), new(halfW, h, 0f), new(-halfW, h, 0f),
                new(0f, 0f, -halfW), new(0f, 0f, halfW), new(0f, h, halfW), new(0f, h, -halfW),
            };
            var normals = new SN.Vector3[]
            {
                new(0,0,1), new(0,0,1), new(0,0,1), new(0,0,1),
                new(1,0,0), new(1,0,0), new(1,0,0), new(1,0,0),
            };
            var uvs = new SN.Vector2[]
            {
                new(0,1), new(1,1), new(1,0), new(0,0),
                new(0,1), new(1,1), new(1,0), new(0,0),
            };
            var tris = new int[]
            {
                0,1,2, 0,2,3, 0,2,1, 0,3,2,
                4,5,6, 4,6,7, 4,6,5, 4,7,6,
            };
            return new Mesh(vertices, Array.Empty<int>(), tris) { Normals = normals, UVs = uvs };
        }

        /// <summary>Collect world-space axis-aligned bounding boxes for all non-terrain, non-vegetation models.</summary>
        private void CollectModelAABBs(IEnumerable<GameObject> roots, List<ExclusionBox> boxes)
        {
            foreach (var root in roots)
                CollectModelAABBsRecursive(root, SN.Matrix4x4.Identity, boxes);
        }

        private void CollectModelAABBsRecursive(GameObject go, SN.Matrix4x4 parentWorld, List<ExclusionBox> boxes)
        {
            // Skip our own vegetation hierarchy and terrain chunks
            if (go == gameObject) return;
            if (go.Name == "Grass" || go.Name.StartsWith("grass_")) return;
            if (go.Name.StartsWith("Chunk_")) return;

            // Compute world matrix for this GO
            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

            bool isTerrain = false;
            bool isVegetation = false;
            bool isWater = false;
            bool hasMeshRenderer = false;
            MeshFilter? meshFilter = null;

            foreach (var b in go.Behaviors)
            {
                if (b is Terrain) isTerrain = true;
                if (b is VegetationPainter) isVegetation = true;
                if (b is Water) isWater = true;
                if (b is MeshRenderer) hasMeshRenderer = true;
                if (b is MeshFilter mf) meshFilter = mf;
            }

            // Build an AABB for objects that have a mesh and aren't terrain/vegetation/water
            if (hasMeshRenderer && !isTerrain && !isVegetation && !isWater)
            {
                if (meshFilter?.Mesh?.Vertices != null && meshFilter.Mesh.Vertices.Length > 0)
                {
                    var verts = meshFilter.Mesh.Vertices;
                    // Transform all vertices to world space and compute XZ bounding box
                    var first = SN.Vector3.Transform(verts[0], world);
                    float bMinX = first.X, bMaxX = first.X;
                    float bMinZ = first.Z, bMaxZ = first.Z;

                    for (int vi = 1; vi < verts.Length; vi++)
                    {
                        var wp = SN.Vector3.Transform(verts[vi], world);
                        if (wp.X < bMinX) bMinX = wp.X;
                        if (wp.X > bMaxX) bMaxX = wp.X;
                        if (wp.Z < bMinZ) bMinZ = wp.Z;
                        if (wp.Z > bMaxZ) bMaxZ = wp.Z;
                    }

                    boxes.Add(new ExclusionBox { MinX = bMinX, MaxX = bMaxX, MinZ = bMinZ, MaxZ = bMaxZ });
                }
                else
                {
                    // No mesh data — use the object's world position as a small box
                    float cx = world.M41, cz = world.M43;
                    boxes.Add(new ExclusionBox { MinX = cx - 1f, MaxX = cx + 1f, MinZ = cz - 1f, MaxZ = cz + 1f });
                }
            }

            foreach (var child in go.Children)
                CollectModelAABBsRecursive(child, world, boxes);
        }

        /// <summary>Find all components of type T in the scene.</summary>
        private static void FindAllComponents<T>(GameObject go, List<T> results) where T : Behavior
        {
            foreach (var b in go.Behaviors)
                if (b is T t) results.Add(t);
            foreach (var child in go.Children)
                FindAllComponents(child, results);
        }

        /// <summary>Recursively search for a Terrain component.</summary>
        private static Terrain? FindTerrain(GameObject go)
        {
            foreach (var b in go.Behaviors)
                if (b is Terrain t && t.IsActiveAndEnabled) return t;
            foreach (var child in go.Children)
            {
                var t = FindTerrain(child);
                if (t != null) return t;
            }
            return null;
        }

        // ── Registry ──
        private static readonly List<VegetationPainter> _all = new(4);
        public static IReadOnlyList<VegetationPainter> AllPainters => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
        }

        // Grass auto-rebuild is handled by SceneService.LoadFromFile() → RebuildVegetation()
        // since grass chunks are excluded from serialization.

        // ReapplyMaterials is no longer needed — grass is not serialized.
        // On load, GrassBuilt triggers a full BuildOnTerrain() rebuild.

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }
    }
}
