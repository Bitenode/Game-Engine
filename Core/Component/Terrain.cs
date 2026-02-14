using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Defines a single texture layer for multi-material terrain painting.</summary>
    public sealed class TerrainLayer
    {
        /// <summary>Project-relative path to the albedo/diffuse texture.</summary>
        public string TexturePath { get; set; } = "";

        /// <summary>UV tiling scale (higher = more repeats).</summary>
        public float Tiling { get; set; } = 10f;

        /// <summary>Optional project-relative normal map path.</summary>
        public string NormalMapPath { get; set; } = "";

        /// <summary>Roughness value (PBR).</summary>
        public float Roughness { get; set; } = 0.8f;

        /// <summary>Metallic value (PBR).</summary>
        public float Metallic { get; set; } = 0f;
    }

    /// <summary>
    ///   heightmap Terrain.
    /// - Stores size (X,Z), height scale (Y), resolution (ResX, ResZ), and a flattened Heights array (0..1).
    /// - Writes/reads a project-relative .terrain.json beside MeshFilter.ModelPath,
    ///   or falls back to Assets/Terrain/{GameObjectName}.terrain.json.
    /// - Rebuilds a grid mesh into the attached MeshFilter and ensures a MeshRenderer exists.
    /// </summary>
    [Require(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class Terrain : Behavior
    {
        // ------ Core Terrain Parameters ----------------------------

        private int _resX = 129;   // pow2+1 typical (129 = good balance of detail & perf)
        private int _resZ = 129;
        private float _sizeX = 100f;  // world width (X)
        private float _sizeZ = 100f;  // world length (Z)
        private float _heightScale = 20f;   // world height (Y)

        [Persist]
        public int ResX
        {
            get => _resX;
            set
            {
                var v = Math.Max(2, value);
                if (Set(ref _resX, v))
                    OnResolutionChanged(); // resample Heights + rebuild
            }
        }

        [Persist]
        public int ResZ
        {
            get => _resZ;
            set
            {
                var v = Math.Max(2, value);
                if (Set(ref _resZ, v))
                    OnResolutionChanged(); // resample Heights + rebuild
            }
        }

        [Persist]
        public float SizeX
        {
            get => _sizeX;
            set
            {
                var v = (float)(double.IsFinite(value) ? value : 100f);
                if (Set(ref _sizeX, v))
                    OnGeometryChanged(); // rebuild (no resample needed)
            }
        }

        [Persist]
        public float SizeZ
        {
            get => _sizeZ;
            set
            {
                var v = (float)(double.IsFinite(value) ? value : 100f);
                if (Set(ref _sizeZ, v))
                    OnGeometryChanged(); // rebuild (no resample needed)
            }
        }

        [Persist]
        public float HeightScale
        {
            get => _heightScale;
            set
            {
                var v = (float)(double.IsFinite(value) ? value : 20f);
                if (Set(ref _heightScale, v))
                    OnGeometryChanged(); // rebuild (heights stay, Y scale changes)
            }
        }

        /// <summary>Flattened height samples (row-major: z * ResX + x), 0..1. Length must be ResX*ResZ.</summary>
        [Persist] public float[] Heights { get; set; } = new float[129 * 129];

        /// <summary>Per-vertex hole mask (row-major: z * ResX + x). True = hole (skip triangle). Null = no holes.</summary>
        [Persist] public bool[]? Holes { get; set; }

        // ------ Splatmap Multi-Material System --------------------------------

        /// <summary>Up to 8 terrain layers for multi-material painting.</summary>
        [Persist] public List<TerrainLayer> Layers { get; set; } = new();

        /// <summary>Splatmap 0 (layers 0-3): flattened RGBA per-vertex, length = ResX*ResZ*4.</summary>
        [Persist] public float[]? Splatmap0 { get; set; }

        /// <summary>Splatmap 1 (layers 4-7): flattened RGBA per-vertex, length = ResX*ResZ*4.</summary>
        [Persist] public float[]? Splatmap1 { get; set; }

        /// <summary>True when splatmap data has changed and GPU texture needs re-upload.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool SplatmapDirty { get; private set; }

        /// <summary>Monotonically increasing counter bumped on each splatmap change.
        /// Per-context caches compare their last uploaded version to detect stale data.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int SplatmapVersion { get; private set; }

        /// <summary>Mark the splatmap as needing GPU re-upload.</summary>
        public void MarkSplatmapDirty() { SplatmapDirty = true; SplatmapVersion++; }

        /// <summary>Clear the dirty flag after GPU upload.</summary>
        public void ClearSplatmapDirty() => SplatmapDirty = false;

        /// <summary>Ensure splatmaps are allocated. Layer 0 defaults to 1.0 (full coverage).</summary>
        public void EnsureSplatmaps()
        {
            int total = ResX * ResZ;
            if (Splatmap0 == null || Splatmap0.Length != total * 4)
            {
                Splatmap0 = new float[total * 4];
                for (int i = 0; i < total; i++) Splatmap0[i * 4] = 1f; // layer 0 = full
            }
            if (Splatmap1 == null || Splatmap1.Length != total * 4)
                Splatmap1 = new float[total * 4];
        }

        // ------ Chunking + LOD ------------------------------------------------

        /// <summary>Vertices per chunk edge (e.g., 65 → 64x64 quads per chunk). Must be pow2+1.
        /// Larger chunks = fewer draw calls but coarser LOD granularity.</summary>
        [Persist] public int ChunkSize { get; set; } = 65;

        /// <summary>Whether chunking is enabled. Auto-enabled when terrain is large enough.</summary>
        [Persist] public bool UseChunking { get; set; } = true;

        /// <summary>Number of LOD levels per chunk (1=no LOD, 2=full+half, 3=full+half+quarter).</summary>
        [Persist] public int LodLevels { get; set; } = 3;

        /// <summary>Internal chunk data.</summary>
        internal sealed class TerrainChunk
        {
            public int ChunkX, ChunkZ;        // chunk grid coords
            public int StartX, StartZ;         // vertex start in height array
            public int EndX, EndZ;             // vertex end (inclusive)
            public GameObject ChunkGO;
            public Mesh[] LodMeshes;           // index = LOD level (0 = full res)
            public int CurrentLod;
            public bool Dirty = true;
        }

        private TerrainChunk[,]? _chunks;
        private int _chunksX, _chunksZ;

        /// <summary>How many chunks wide/deep.</summary>
        public int ChunksX => _chunksX;
        public int ChunksZ => _chunksZ;

        /// <summary>Mark specific chunk(s) as needing rebuild based on vertex coordinate range.</summary>
        public void MarkChunksDirty(int minVx, int minVz, int maxVx, int maxVz)
        {
            if (_chunks == null) return;
            int cs = ChunkSize - 1; // quads per chunk
            int cx0 = Math.Max(0, minVx / cs);
            int cz0 = Math.Max(0, minVz / cs);
            int cx1 = Math.Min(_chunksX - 1, maxVx / cs);
            int cz1 = Math.Min(_chunksZ - 1, maxVz / cs);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                    _chunks[cz, cx].Dirty = true;
        }

        /// <summary>Mark all chunks dirty.</summary>
        public void MarkAllChunksDirty()
        {
            if (_chunks == null) return;
            for (int cz = 0; cz < _chunksZ; cz++)
                for (int cx = 0; cx < _chunksX; cx++)
                    _chunks[cz, cx].Dirty = true;
        }

        /// <summary>
        /// Rebuild only the chunks that are marked dirty.
        /// Call this after modifying Heights[] to avoid rebuilding the entire terrain.
        /// Pass rebuildCollision = true only at stroke-end, not per-tick.
        /// </summary>
        public void RebuildDirtyChunks(bool rebuildCollision = false)
        {
            if (_chunks == null || gameObject == null) return;
            for (int cz = 0; cz < _chunksZ; cz++)
                for (int cx = 0; cx < _chunksX; cx++)
                    if (_chunks[cz, cx].Dirty)
                        RebuildSingleChunk(_chunks[cz, cx]);

            // Only rebuild the expensive full-res collision mesh when explicitly requested
            // (e.g., at the end of a brush stroke, not every frame while painting)
            if (rebuildCollision)
                RebuildCollisionMesh();
        }

        /// <summary>Select LOD level for each chunk based on camera distance.</summary>
        public void UpdateLOD(SN.Vector3 cameraPos)
        {
            if (_chunks == null || LodLevels <= 1) return;
            var W = TransformUtil.WorldFromTransform(gameObject!.Transform);
            float hx = SizeX * 0.5f, hz = SizeZ * 0.5f;
            int cs = ChunkSize - 1;
            float chunkWorldSize = SizeX / _chunksX; // approximate

            for (int cz = 0; cz < _chunksZ; cz++)
            {
                for (int cx = 0; cx < _chunksX; cx++)
                {
                    var chunk = _chunks[cz, cx];
                    // Chunk center in local space
                    float centerX = -hx + ((chunk.StartX + chunk.EndX) * 0.5f / (ResX - 1)) * SizeX;
                    float centerZ = -hz + ((chunk.StartZ + chunk.EndZ) * 0.5f / (ResZ - 1)) * SizeZ;
                    var localCenter = new SN.Vector3(centerX, HeightScale * 0.5f, centerZ);
                    var worldCenter = SN.Vector3.Transform(localCenter, W);
                    float dist = SN.Vector3.Distance(worldCenter, cameraPos);

                    // LOD selection thresholds
                    int lod;
                    if (dist < chunkWorldSize * 4f)
                        lod = 0; // full detail
                    else if (dist < chunkWorldSize * 10f && LodLevels >= 2)
                        lod = 1; // half resolution
                    else if (LodLevels >= 3)
                        lod = 2; // quarter resolution
                    else
                        lod = LodLevels - 1;

                    lod = Math.Clamp(lod, 0, chunk.LodMeshes.Length - 1);
                    if (lod != chunk.CurrentLod && chunk.LodMeshes[lod] != null)
                    {
                        chunk.CurrentLod = lod;
                        // Direct iteration instead of LINQ to avoid per-frame allocations
                        var chBehaviors = chunk.ChunkGO.Behaviors;
                        for (int bi = 0; bi < chBehaviors.Count; bi++)
                        {
                            if (chBehaviors[bi] is MeshFilter mf) { mf.Mesh = chunk.LodMeshes[lod]; break; }
                        }
                    }
                }
            }
        }

        /// <summary>Build or rebuild all chunks from scratch.</summary>
        private void BuildChunks()
        {
            if (gameObject == null) return;
            EnsureValidDimensions();
            EnsureHeightsArray();

            int cs = Math.Max(3, ChunkSize); // vertices per chunk edge (min 3)
            int csQuads = cs - 1;

            _chunksX = Math.Max(1, (int)Math.Ceiling((double)(ResX - 1) / csQuads));
            _chunksZ = Math.Max(1, (int)Math.Ceiling((double)(ResZ - 1) / csQuads));

            // Remove old chunk children
            CleanupChunkChildren();

            _chunks = new TerrainChunk[_chunksZ, _chunksX];

            // Ensure MeshRenderer on parent for material reference (but disable rendering — chunks handle it)
            var parentMR = GetComponent<MeshRenderer>();
            if (parentMR == null) { parentMR = new MeshRenderer(); gameObject.AddBehavior(parentMR); }

            for (int cz = 0; cz < _chunksZ; cz++)
            {
                for (int cx = 0; cx < _chunksX; cx++)
                {
                    int sx = cx * csQuads;
                    int sz = cz * csQuads;
                    int ex = Math.Min(sx + csQuads, ResX - 1);
                    int ez = Math.Min(sz + csQuads, ResZ - 1);

                    var chunkGO = new GameObject($"Chunk_{cx}_{cz}");
                    chunkGO.AddBehavior(new MeshFilter());
                    var chunkMR = new MeshRenderer();
                    // Share material from parent
                    chunkMR.Material = parentMR.Material;
                    chunkGO.AddBehavior(chunkMR);

                    gameObject.AddChild(chunkGO);

                    int lodCount = Math.Max(1, Math.Min(LodLevels, 3));
                    var chunk = new TerrainChunk
                    {
                        ChunkX = cx, ChunkZ = cz,
                        StartX = sx, StartZ = sz,
                        EndX = ex, EndZ = ez,
                        ChunkGO = chunkGO,
                        LodMeshes = new Mesh[lodCount],
                        CurrentLod = 0,
                        Dirty = true
                    };
                    _chunks[cz, cx] = chunk;
                }
            }

            // Build all chunk meshes
            for (int cz = 0; cz < _chunksZ; cz++)
                for (int cx = 0; cx < _chunksX; cx++)
                    RebuildSingleChunk(_chunks[cz, cx]);

            // Disable parent MeshRenderer for rendering (chunks handle it)
            // But keep MeshFilter for raycasting
            parentMR.Enabled = false;

            // Build collision mesh on parent
            RebuildCollisionMesh();
        }

        private void RebuildSingleChunk(TerrainChunk chunk)
        {
            chunk.Dirty = false;
            int lodCount = chunk.LodMeshes.Length;

            for (int lod = 0; lod < lodCount; lod++)
            {
                int step = 1 << lod; // LOD 0=1, LOD 1=2, LOD 2=4
                chunk.LodMeshes[lod] = BuildChunkMesh(chunk.StartX, chunk.StartZ, chunk.EndX, chunk.EndZ, step);
            }

            // Set current LOD mesh on MeshFilter (direct iteration, no LINQ)
            {
                int lod = Math.Clamp(chunk.CurrentLod, 0, lodCount - 1);
                var chBehaviors = chunk.ChunkGO.Behaviors;
                for (int bi = 0; bi < chBehaviors.Count; bi++)
                {
                    if (chBehaviors[bi] is MeshFilter mf) { mf.Mesh = chunk.LodMeshes[lod]; break; }
                }
            }
        }

        private Mesh BuildChunkMesh(int sx, int sz, int ex, int ez, int lodStep)
        {
            int nx = ResX, nz = ResZ;
            float hx = SizeX * 0.5f, hz = SizeZ * 0.5f;

            // Compute vertices for this chunk sub-region at the given LOD step
            var vertList = new List<SN.Vector3>();
            var uvList = new List<SN.Vector2>();
            var indexMap = new Dictionary<(int, int), int>(); // (x,z) → vertex index

            for (int z = sz; z <= ez; z += lodStep)
            {
                int zc = Math.Min(z, nz - 1);
                for (int x = sx; x <= ex; x += lodStep)
                {
                    int xc = Math.Min(x, nx - 1);
                    float tx = (nx == 1) ? 0f : (float)xc / (nx - 1);
                    float tz2 = (nz == 1) ? 0f : (float)zc / (nz - 1);
                    float y = Heights[zc * nx + xc] * HeightScale;
                    float px = -hx + tx * SizeX;
                    float pz = -hz + tz2 * SizeZ;

                    indexMap[(xc, zc)] = vertList.Count;
                    vertList.Add(new SN.Vector3(px, y, pz));
                    uvList.Add(new SN.Vector2(tx, tz2));
                }
            }

            // Also add boundary vertices at full resolution for LOD stitching
            // (ensures adjacent chunks share edge vertices)
            void EnsureVertex(int x, int z)
            {
                if (indexMap.ContainsKey((x, z))) return;
                int xc = Math.Min(x, nx - 1), zc = Math.Min(z, nz - 1);
                float tx = (nx == 1) ? 0f : (float)xc / (nx - 1);
                float tz2 = (nz == 1) ? 0f : (float)zc / (nz - 1);
                float y = Heights[zc * nx + xc] * HeightScale;
                float px = -hx + tx * SizeX;
                float pz = -hz + tz2 * SizeZ;
                indexMap[(xc, zc)] = vertList.Count;
                vertList.Add(new SN.Vector3(px, y, pz));
                uvList.Add(new SN.Vector2(tx, tz2));
            }

            // Build triangles
            bool hasHoles = Holes != null && Holes.Length == nx * nz;
            var tris = new List<int>();

            for (int z = sz; z < ez; z += lodStep)
            {
                int zNext = Math.Min(z + lodStep, ez);
                for (int x = sx; x < ex; x += lodStep)
                {
                    int xNext = Math.Min(x + lodStep, ex);
                    int a_x = Math.Min(x, nx - 1), a_z = Math.Min(z, nz - 1);
                    int b_x = Math.Min(xNext, nx - 1), b_z = a_z;
                    int c_x = a_x, c_z = Math.Min(zNext, nz - 1);
                    int d_x = b_x, d_z = c_z;

                    if (hasHoles && (Holes![a_z * nx + a_x] || Holes[b_z * nx + b_x] ||
                                     Holes![c_z * nx + c_x] || Holes[d_z * nx + d_x]))
                        continue;

                    EnsureVertex(a_x, a_z);
                    EnsureVertex(b_x, b_z);
                    EnsureVertex(c_x, c_z);
                    EnsureVertex(d_x, d_z);

                    int ai = indexMap[(a_x, a_z)];
                    int bi = indexMap[(b_x, b_z)];
                    int ci = indexMap[(c_x, c_z)];
                    int di = indexMap[(d_x, d_z)];

                    // CCW winding (same as non-chunked)
                    tris.Add(ai); tris.Add(ci); tris.Add(di);
                    tris.Add(ai); tris.Add(di); tris.Add(bi);
                }
            }

            if (tris.Count == 0)
            {
                // Degenerate chunk — return minimal mesh
                return new Mesh(
                    new[] { SN.Vector3.Zero },
                    Array.Empty<int>(),
                    Array.Empty<int>()
                ) { UVs = new[] { SN.Vector2.Zero } };
            }

            var triArr = tris.ToArray();
            // Skip edge computation for chunks — edges are only for wireframe display
            // and terrain chunks never render wireframe. This saves significant time for
            // large meshes (edge building uses a HashSet of ~N unique pairs).
            var mesh = new Mesh(vertList.ToArray(), Array.Empty<int>(), triArr) { UVs = uvList.ToArray() };
            mesh.RecalculateNormalsSmooth();
            return mesh;
        }

        /// <summary>Rebuild just the collision mesh (full resolution, no LOD).</summary>
        private void RebuildCollisionMesh()
        {
            // Full-res mesh for MeshFilter (raycasting) and MeshCollider
            var fullMesh = BuildFullMesh();

            var mf = GetComponent<MeshFilter>();
            if (mf == null) { mf = new MeshFilter(); gameObject.AddBehavior(mf); }
            mf.Mesh = fullMesh;

            var mc = GetComponent<MeshCollider>();
            if (mc == null) { mc = new MeshCollider(); gameObject.AddBehavior(mc); }
            mc.Mesh = fullMesh;
        }

        /// <summary>Build the full-resolution mesh (used for collision and non-chunked rendering).</summary>
        private Mesh BuildFullMesh()
        {
            int nx = ResX, nz = ResZ;
            int vertCount = nx * nz;
            var verts = new SN.Vector3[vertCount];
            var uvs = new SN.Vector2[vertCount];
            float hx = SizeX * 0.5f, hz = SizeZ * 0.5f;

            for (int z = 0; z < nz; z++)
            {
                float tz = (nz == 1) ? 0f : (float)z / (nz - 1);
                for (int x = 0; x < nx; x++)
                {
                    float tx = (nx == 1) ? 0f : (float)x / (nx - 1);
                    float y = Heights[z * nx + x] * HeightScale;
                    int i = z * nx + x;
                    verts[i] = new SN.Vector3(-hx + tx * SizeX, y, -hz + tz * SizeZ);
                    uvs[i] = new SN.Vector2(tx, tz);
                }
            }

            bool hasHoles = Holes != null && Holes.Length == vertCount;
            var tris = new List<int>((nx - 1) * (nz - 1) * 6);
            for (int z = 0; z < nz - 1; z++)
            {
                for (int x = 0; x < nx - 1; x++)
                {
                    int a = z * nx + x, b = a + 1, c = a + nx, d = c + 1;
                    if (hasHoles && (Holes![a] || Holes[b] || Holes[c] || Holes[d])) continue;
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }
            }

            var triArr = tris.ToArray();
            // Skip edge computation — collision mesh never needs wireframe lines.
            var mesh = new Mesh(verts, Array.Empty<int>(), triArr) { UVs = uvs };
            mesh.RecalculateNormalsSmooth();
            return mesh;
        }

        private void CleanupChunkChildren()
        {
            if (gameObject == null) return;

            // Remove tracked chunks (if any) — use array dimensions directly
            // to avoid stale _chunksX/_chunksZ causing IndexOutOfRangeException
            if (_chunks != null)
            {
                int arrZ = _chunks.GetLength(0);
                int arrX = _chunks.GetLength(1);
                for (int cz = 0; cz < arrZ; cz++)
                    for (int cx = 0; cx < arrX; cx++)
                    {
                        var chunk = _chunks[cz, cx];
                        if (chunk?.ChunkGO != null)
                            chunk.ChunkGO.RemoveFromParent();
                    }
            }

            // Also remove any leftover Chunk_* children that survived scene load/restore
            // (_chunks is a runtime field and is null after deserialization, but the
            //  chunk GameObjects persist as children — causing duplicates without this)
            for (int i = gameObject.Children.Count - 1; i >= 0; i--)
            {
                var child = gameObject.Children[i];
                if (child.Name != null && child.Name.StartsWith("Chunk_", StringComparison.Ordinal))
                    child.RemoveFromParent();
            }

            _chunks = null;
            _chunksX = _chunksZ = 0;

            // Re-enable parent MeshRenderer
            var parentMR = GetComponent<MeshRenderer>();
            if (parentMR != null) parentMR.Enabled = true;
        }

        /// <summary>
        /// Project-relative .terrain.json. If empty, derive from MeshFilter.ModelPath (swap to .terrain.json).
        /// Else fallback to Assets/Terrain/{GO}.terrain.json.
        /// </summary>
        [Persist] public string TerrainAssetPath { get; set; } = null;

        // QoL
        [Persist] public bool AutoLoadOnStart { get; set; } = true;
        [Persist] public bool AutoSaveOnChange { get; set; } = false;

        private bool _didInitialAssetSetup;

        // ------ Lifecycle -------------------------------------------------------

        // Called when the component is added or re-enabled — tell views to repaint.
        public override void OnEnable()
        {
            // Make sure dimensions/arrays/path are valid
            EnsureValidDimensions();
            EnsureHeightsArray();
            EnsureTerrainAssetPath();

            // Create the file right away if missing (so the asset path exists immediately)
            var abs = ToAbsolutePath(TerrainAssetPath);
            var dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(abs))
                TrySaveToFile();   // saves a flat terrain .terrain.json on attach
            else
                TryLoadFromFile(); // if user already has a file, load it immediately

            // Build the terrain mesh now (this will override the MeshFilter’s default cube)
            RebuildMesh();

            _didInitialAssetSetup = true;
            Game_Engine.Core.SceneService.NotifyChanged();
        }

        /// <summary>
        /// Called by the scene deserializer AFTER all [Persist] properties are applied.
        /// This ensures the .terrain.json data takes precedence over stale scene-file
        /// data (Heights, ResX, ResZ etc.) that property setters overwrote after
        /// OnEnable() had already loaded the correct data.
        /// </summary>
        public override void PostDeserialize()
        {
            // Re-ensure the path using the now-restored [Persist] TerrainAssetPath
            EnsureTerrainAssetPath();
            var abs = ToAbsolutePath(TerrainAssetPath);

            if (File.Exists(abs))
            {
                // Reload from .terrain.json -- overrides stale [Persist] data
                TryLoadFromFile();
            }

            // Rebuild chunks/mesh with the definitive terrain data
            CleanupChunkChildren();
            RebuildMesh();
            Game_Engine.Core.SceneService.NotifyChanged();
        }

        public override void Awake()
        {
            EnsureValidDimensions();
            EnsureHeightsArray();
            EnsureTerrainAssetPath();
        }

        public override void Start()
        {
            if (_didInitialAssetSetup) return; // already created/loaded & rebuilt at attach-time

            // --- runtime fallback (e.g., if added via code at runtime) ---
            EnsureTerrainAssetPath();
            var abs = ToAbsolutePath(TerrainAssetPath);
            var dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool createdNew = false;
            if (!File.Exists(abs))
            {
                TrySaveToFile();
                createdNew = true;
            }

            if (AutoLoadOnStart && !createdNew)
                TryLoadFromFile();

            RebuildMesh();
            Game_Engine.Core.SceneService.NotifyChanged();
        }


        // ------ Public API ------------------------------------------------------

        /// <summary>Set a single height sample (0..1). Does not rebuild mesh.</summary>
        public void SetHeight(int x, int z, float h01)
        {
            if (!InRange(x, 0, ResX - 1) || !InRange(z, 0, ResZ - 1)) return;
            Heights[z * ResX + x] = ClampHeight(h01);
        }

        /// <summary>Get a single height sample (0..1).</summary>
        public float GetHeight(int x, int z)
        {
            if (!InRange(x, 0, ResX - 1) || !InRange(z, 0, ResZ - 1)) return 0f;
            return Heights[z * ResX + x];
        }

        /// <summary>
        /// O(1) heightmap collision: given a world-space XZ position, returns the terrain
        /// world-space Y height and approximate surface normal via bilinear interpolation.
        /// Returns false if the point is outside the terrain bounds.
        /// This replaces brute-force ray-triangle tests against 131K+ triangles.
        /// </summary>
        public bool SampleHeightWorld(float worldX, float worldZ, out float worldY, out SN.Vector3 normal)
        {
            worldY = 0f;
            normal = SN.Vector3.UnitY;
            if (gameObject == null || Heights == null) return false;

            // Terrain local space: X in [-SizeX/2, SizeX/2], Z in [-SizeZ/2, SizeZ/2]
            var W = TransformUtil.WorldFromTransform(gameObject.Transform);
            if (!SN.Matrix4x4.Invert(W, out var invW)) return false;

            // Transform world position to terrain local space
            var localPos = SN.Vector3.Transform(new SN.Vector3(worldX, 0, worldZ), invW);
            float lx = localPos.X;
            float lz = localPos.Z;

            // Convert to heightmap UV [0,1]
            float hx = SizeX * 0.5f, hz = SizeZ * 0.5f;
            float u = (lx + hx) / SizeX; // 0..1
            float v = (lz + hz) / SizeZ; // 0..1

            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

            // Heightmap grid coordinates (floating point)
            float gx = u * (ResX - 1);
            float gz = v * (ResZ - 1);

            int ix = Math.Clamp((int)gx, 0, ResX - 2);
            int iz = Math.Clamp((int)gz, 0, ResZ - 2);
            float fx = gx - ix;
            float fz = gz - iz;

            // Bilinear interpolation of height
            float h00 = Heights[iz * ResX + ix];
            float h10 = Heights[iz * ResX + ix + 1];
            float h01 = Heights[(iz + 1) * ResX + ix];
            float h11 = Heights[(iz + 1) * ResX + ix + 1];

            // Check holes
            if (Holes != null)
            {
                if (Holes[iz * ResX + ix] || Holes[iz * ResX + ix + 1] ||
                    Holes[(iz + 1) * ResX + ix] || Holes[(iz + 1) * ResX + ix + 1])
                    return false; // hole area
            }

            float h0 = h00 + (h10 - h00) * fx;
            float h1 = h01 + (h11 - h01) * fx;
            float h = h0 + (h1 - h0) * fz;

            // Height in local space (-1..1 → -HeightScale..HeightScale)
            float localY = h * HeightScale;

            // Transform back to world space
            var worldPoint = SN.Vector3.Transform(new SN.Vector3(lx, localY, lz), W);
            worldY = worldPoint.Y;

            // Approximate normal from height differences
            float dx = (h10 - h00) * HeightScale;
            float dz = (h01 - h00) * HeightScale;
            float cellSizeX = SizeX / (ResX - 1);
            float cellSizeZ = SizeZ / (ResZ - 1);
            var localNormal = SN.Vector3.Normalize(new SN.Vector3(-dx / cellSizeX, 1f, -dz / cellSizeZ));

            // Rotate normal to world space (use upper-left 3×3 of world matrix)
            normal = SN.Vector3.Normalize(SN.Vector3.TransformNormal(localNormal, W));

            return true;
        }

        /// <summary>Rebuild the underlying Mesh into the attached MeshFilter and ensure a MeshRenderer exists.</summary>
        public void RebuildMesh()
        {
            EnsureValidDimensions();
            EnsureHeightsArray();

            // Use chunking for larger terrains
            bool shouldChunk = UseChunking && ResX > 65 && ResZ > 65;

            if (shouldChunk)
            {
                if (_chunks == null || _chunksX == 0)
                    BuildChunks();
                else
                {
                    MarkAllChunksDirty();
                    RebuildDirtyChunks();
                }
            }
            else
            {
                // Non-chunked: single mesh
                CleanupChunkChildren();

                var mesh = BuildFullMesh();

                // Ensure MeshFilter
                var mf = GetComponent<MeshFilter>();
                if (mf == null) { mf = new MeshFilter(); gameObject.AddBehavior(mf); }
                mf.Mesh = mesh;

                // Ensure MeshRenderer (pairs with MeshFilter in renderers)
                var mr = GetComponent<MeshRenderer>();
                if (mr == null) { mr = new MeshRenderer(); gameObject.AddBehavior(mr); }
                mr.Enabled = true; // ensure visible

                // Ensure MeshCollider matches terrain
                var mc = GetComponent<MeshCollider>();
                if (mc == null) { mc = new MeshCollider(); gameObject.AddBehavior(mc); }
                mc.Mesh = mesh;
            }

            if (AutoSaveOnChange) TrySaveToFile();

            // Proactively notify editors/views to repaint
            Game_Engine.Core.SceneService.NotifyChanged();
        }

        /// <summary>
        /// Partial rebuild: only rebuild chunks affected by a vertex range.
        /// Falls back to full RebuildMesh if chunking is not active.
        /// Does NOT call SceneService.NotifyChanged — the caller should batch
        /// that at stroke-end to avoid per-tick full-cache invalidation.
        /// </summary>
        public void RebuildArea(int minVx, int minVz, int maxVx, int maxVz)
        {
            if (_chunks != null && _chunksX > 0)
            {
                MarkChunksDirty(minVx, minVz, maxVx, maxVz);
                RebuildDirtyChunks(rebuildCollision: false);
                // Note: we intentionally skip SceneService.NotifyChanged() here
                // to avoid invalidating the GPU cache on every brush tick.
                // The caller (SceneView) will call it at stroke-end.
            }
            else
            {
                RebuildMesh();
            }
        }

        /// <summary>
        /// Rebuild collision mesh only (call at stroke-end, not per-tick).
        /// </summary>
        public void FinalizeStroke()
        {
            if (_chunks != null && _chunksX > 0)
                RebuildCollisionMesh();
        }

        /// <summary>Saves the terrain to TerrainAssetPath (deriving a path if needed).</summary>
        public void Save() => TrySaveToFile();

        /// <summary>Loads terrain from TerrainAssetPath if it exists, otherwise keeps current data.</summary>
        public void Load() => TryLoadFromFile();

        // ------ Persistence (simple JSON) --------------------------------------

        // Use PROPERTIES so System.Text.Json writes JSON 
        private sealed class TerrainData
        {
            public int ResX { get; set; }
            public int ResZ { get; set; }
            public float SizeX { get; set; }
            public float SizeZ { get; set; }
            public float HeightScale { get; set; }
            public float[] Heights { get; set; }
            public bool[]? Holes { get; set; }
            public List<TerrainLayer>? Layers { get; set; }
            public float[]? Splatmap0 { get; set; }
            public float[]? Splatmap1 { get; set; }
        }

        private void TrySaveToFile()
        {
            EnsureTerrainAssetPath();
            try
            {
                string abs = ToAbsolutePath(TerrainAssetPath);
                string dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = new TerrainData
                {
                    ResX = ResX,
                    ResZ = ResZ,
                    SizeX = SizeX,
                    SizeZ = SizeZ,
                    HeightScale = HeightScale,
                    Heights = Heights,
                    Holes = Holes,
                    Layers = Layers?.Count > 0 ? Layers : null,
                    Splatmap0 = Splatmap0,
                    Splatmap1 = Splatmap1
                };

                var json = JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                File.WriteAllText(abs, json);

                LogSuccess("Terrain saved: " + TerrainAssetPath);
                ProjectService.TouchModified();
            }
            catch (Exception ex)
            {
                LogError(ex, "Save");
            }
        }

        private void TryLoadFromFile()
        {
            EnsureTerrainAssetPath();
            try
            {
                string abs = ToAbsolutePath(TerrainAssetPath);
                if (!File.Exists(abs)) return;

                var text = File.ReadAllText(abs);
                var data = JsonSerializer.Deserialize<TerrainData>(text);
                if (data == null || data.Heights == null) return;

                // apply without triggering intermediate rebuilds
                _resX = Math.Max(2, data.ResX);
                _resZ = Math.Max(2, data.ResZ);
                _sizeX = data.SizeX;
                _sizeZ = data.SizeZ;
                _heightScale = data.HeightScale;

                int need = _resX * _resZ;
                Heights = (data.Heights.Length == need) ? data.Heights : new float[need];
                Holes = (data.Holes != null && data.Holes.Length == need) ? data.Holes : null;

                // Only overwrite Layers/Splatmaps from file if the file actually contains them.
                // The scene serializer may have more up-to-date data (e.g., user painted layers
                // then saved the scene but not the .terrain.json file).
                if (data.Layers != null && data.Layers.Count > 0)
                    Layers = data.Layers;
                else if (Layers == null)
                    Layers = new List<TerrainLayer>();
                // else: keep existing Layers from scene deserialization

                if (data.Splatmap0 != null && data.Splatmap0.Length == need * 4)
                    Splatmap0 = data.Splatmap0;
                // else: keep existing Splatmap0 from scene deserialization

                if (data.Splatmap1 != null && data.Splatmap1.Length == need * 4)
                    Splatmap1 = data.Splatmap1;
                // else: keep existing Splatmap1 from scene deserialization
            }
            finally
            {
                EnsureHeightsArray();
            }

            // build with the loaded data
            RebuildMesh();
            Game_Engine.Core.SceneService.NotifyChanged();

            // force project-wide refresh so views rebind immediately on load
            ProjectService.TouchModified();

            LogInfo("Terrain loaded: " + TerrainAssetPath);
        }


        // ------ Helpers --------------------------------------------------------

        private void EnsureValidDimensions()
        {
            ResX = Math.Max(2, ResX);
            ResZ = Math.Max(2, ResZ);
            if (float.IsNaN(SizeX) || float.IsInfinity(SizeX)) SizeX = 100f;
            if (float.IsNaN(SizeZ) || float.IsInfinity(SizeZ)) SizeZ = 100f;
            if (float.IsNaN(HeightScale) || float.IsInfinity(HeightScale)) HeightScale = 20f;
            // Force minimum chunk size of 65 (64 quads per edge) to keep draw call count low.
            // Old terrains saved with ChunkSize=33 would create 64 chunks for a 257x257 terrain;
            // with 65, that drops to 16 chunks = 4× fewer draw calls.
            if (ChunkSize < 65) ChunkSize = 65;
        }

        private void EnsureHeightsArray()
        {
            int need = ResX * ResZ;
            if (Heights == null || Heights.Length != need)
            {
                var old = Heights;
                Heights = new float[need];
                if (old != null && old.Length > 0)
                {
                    int oldX = GuessResXFromLength(old.Length, ResZ);
                    int oldZ = (oldX > 0) ? (old.Length / oldX) : 0;

                    for (int z = 0; z < ResZ; z++)
                    {
                        int oz = (oldZ <= 1) ? 0 : (int)Math.Round((double)z * (oldZ - 1) / (ResZ - 1));
                        for (int x = 0; x < ResX; x++)
                        {
                            int ox = (oldX <= 1) ? 0 : (int)Math.Round((double)x * (oldX - 1) / (ResX - 1));
                            float v = old[Math.Min(old.Length - 1, Math.Max(0, oz * oldX + ox))];
                            Heights[z * ResX + x] = ClampHeight(v);
                        }
                    }
                }
            }
        }

        private int GuessResXFromLength(int len, int currentResZ)
        {
            if (currentResZ > 1 && (len % currentResZ) == 0) return len / currentResZ;
            int r = (int)Math.Round(Math.Sqrt(len));
            return (r > 0) ? r : len;
        }

        /// <summary>Maximum depth (in world units) that terrain can be dug below the initial flatland (height 0).</summary>
        private const float MaxDepthBelowFlatland = 30f;

        /// <summary>Clamp height. Negative values allow digging below the initial flatland up to MaxDepthBelowFlatland.</summary>
        private float ClampHeight(float v)
        {
            float minH = -MaxDepthBelowFlatland / Math.Max(0.001f, _heightScale);
            return v < minH ? minH : (v > 1f ? 1f : v);
        }
        private static bool InRange(int v, int min, int max) => v >= min && v <= max;

        private static int[] BuildEdgesFromTrianglesLocal(int[] tris)
        {
            var set = new HashSet<(int, int)>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                Add(tris[i], tris[i + 1]);
                Add(tris[i + 1], tris[i + 2]);
                Add(tris[i + 2], tris[i]);
            }
            var list = new List<int>(set.Count * 2);
            foreach (var e in set) { list.Add(e.Item1); list.Add(e.Item2); }
            return list.ToArray();

            void Add(int a, int b)
            {
                if (a > b) { int t = a; a = b; b = t; }
                set.Add((a, b));
            }
        }

        private void EnsureTerrainAssetPath()
        {
            if (!string.IsNullOrWhiteSpace(TerrainAssetPath))
                return;

            // Try MeshFilter.ModelPath -> .terrain.json
            var mf = GetComponent<MeshFilter>();
            if (mf != null && !string.IsNullOrWhiteSpace(mf.ModelPath))
            {
                string modelRel = NormalizeSlashes(mf.ModelPath);
                string dirRel = Path.GetDirectoryName(modelRel) ?? "";
                string name = Path.GetFileNameWithoutExtension(modelRel);
                string rel = string.IsNullOrEmpty(dirRel)
                                  ? name + ".terrain.json"
                                  : Path.Combine(dirRel, name + ".terrain.json");
                TerrainAssetPath = NormalizeProjectRelative(rel);
                return;
            }

            // Fallback: Assets/Terrain/{GO}.terrain.json
            string goName = gameObject?.Name ?? "Terrain";
            string relPath = Path.Combine("Assets", "Terrain", MakeSafeFileName(goName) + ".terrain.json");
            TerrainAssetPath = NormalizeProjectRelative(relPath);
        }

        private static string NormalizeSlashes(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            return p.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Terrain";
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ')) chars[i] = '_';
            }
            var s = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(s) ? "Terrain" : s;
        }

        private static string NormalizeProjectRelative(string rel)
        {
            try
            {
                var proj = ProjectService.Current;
                if (proj == null) return rel.Replace('\\', '/');
                string abs = Path.GetFullPath(Path.Combine(proj.RootPath, rel));
                string normRel = Path.GetRelativePath(proj.RootPath, abs);
                return normRel.Replace('\\', '/');
            }
            catch { return rel.Replace('\\', '/'); }
        }

        private static string ToAbsolutePath(string projectRelative)
        {
            var proj = ProjectService.Current;
            if (proj == null) return Path.GetFullPath(projectRelative);
            var p = projectRelative?.Trim();
            if (string.IsNullOrWhiteSpace(p)) return Path.Combine(proj.AssetsPath, "Terrain");
            if (Path.IsPathRooted(p)) return p; // allow absolute pass-through
            return Path.GetFullPath(Path.Combine(proj.RootPath, p));
        }

        // ---- change reaction helpers ------------------------------------------
        private void OnResolutionChanged()
        {
            // When resolution changes, re-allocate (and resample) Heights, then rebuild
            EnsureHeightsArray();
            SafeRebuildAndNotify();
        }

        private void OnGeometryChanged()
        {
            // When any geometric scalar changes (SizeX/SizeZ/HeightScale), rebuild only
            SafeRebuildAndNotify();
        }

        private void SafeRebuildAndNotify()
        {
            // Guard against early calls before attach
            if (gameObject == null) return;
            RebuildMesh();
            // RebuildMesh already saves if AutoSaveOnChange == true
            Game_Engine.Core.SceneService.NotifyChanged();
        }
    }
}