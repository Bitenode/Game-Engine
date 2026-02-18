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
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp" };

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
                        _resolvedTex = Texture2D.FromFile(absPath);
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
                                _manualTex = Texture2D.FromFile(absPath);
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
            public float CenterX, CenterZ;
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
            var grassTex = ResolvedTexture;

            string relTexPath = !string.IsNullOrWhiteSpace(TexturePath) ? ToRelativePath(TexturePath) : "";

            var grassMat = new Material
            {
                Name = "VegetationGrass",
                Roughness = 0.8f,
                Metallic = 0f,
                AlphaCutoff = grassTex != null ? 0.1f : 0f,
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
                _chunks.Add(new ChunkInfo { ChunkGO = chunkGO, CenterX = centerX, CenterZ = centerZ });
            }

            GrassBuilt = placed > 0;
            SceneService.NotifyChanged();
            Log.Info($"[VegetationPainter] Built {placed} grass blades in {_chunks.Count} chunks (skipped {skipped}). " +
                     $"Draw calls = {_chunks.Count} max.");
            return placed;
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
            float camZ = (float)cam.Transform.Position.Z;
            float chunkDiag = ChunkSize * 0.7071f;
            float cullDist = FadeEndDistance + chunkDiag;
            float cullDist2 = cullDist * cullDist;

            for (int i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                float dx = chunk.CenterX - camX;
                float dz = chunk.CenterZ - camZ;
                float dist2 = dx * dx + dz * dz;
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
        private static Mesh CreateGrassBladeMesh()
        {
            var vertices = new SN.Vector3[]
            {
                new(-0.2f, 0f, 0f), new(0.2f, 0f, 0f), new(0.2f, 1f, 0f), new(-0.2f, 1f, 0f),
                new(0f, 0f, -0.2f), new(0f, 0f, 0.2f), new(0f, 1f, 0.2f), new(0f, 1f, -0.2f),
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
