using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
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

        private int _resX = 257;   // pow2+1 typical
        private int _resZ = 257;
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
        [Persist] public float[] Heights { get; set; } = new float[257 * 257];

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
            Heights[z * ResX + x] = Clamp01(h01);
        }

        /// <summary>Get a single height sample (0..1).</summary>
        public float GetHeight(int x, int z)
        {
            if (!InRange(x, 0, ResX - 1) || !InRange(z, 0, ResZ - 1)) return 0f;
            return Heights[z * ResX + x];
        }

        /// <summary>Rebuild the underlying Mesh into the attached MeshFilter and ensure a MeshRenderer exists.</summary>
        public void RebuildMesh()
        {
            EnsureValidDimensions();
            EnsureHeightsArray();

            int nx = ResX, nz = ResZ;
            int vertCount = nx * nz;

            var verts = new SN.Vector3[vertCount];
            var uvs = new SN.Vector2[vertCount];

            // Centered on origin
            float hx = SizeX * 0.5f;
            float hz = SizeZ * 0.5f;

            for (int z = 0; z < nz; z++)
            {
                float tz = (nz == 1) ? 0f : (float)z / (nz - 1);
                for (int x = 0; x < nx; x++)
                {
                    float tx = (nx == 1) ? 0f : (float)x / (nx - 1);

                    float y = Heights[z * nx + x] * HeightScale;
                    float px = -hx + tx * SizeX;
                    float pz = -hz + tz * SizeZ;

                    int i = z * nx + x;
                    verts[i] = new SN.Vector3(px, y, pz);
                    uvs[i] = new SN.Vector2(tx, tz);
                }
            }

            // Indices (two tris per quad) — CCW
            var tris = new List<int>((nx - 1) * (nz - 1) * 6);
            for (int z = 0; z < nz - 1; z++)
            {
                for (int x = 0; x < nx - 1; x++)
                {
                    int a = z * nx + x;
                    int b = a + 1;
                    int c = a + nx;
                    int d = c + 1;
                    tris.Add(a); tris.Add(b); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(c);
                }
            }

            var tArr = tris.ToArray();
            var lArr = BuildEdgesFromTrianglesLocal(tArr);

            var mesh = new Game_Engine.Core.Mesh(verts, lArr, tArr) { UVs = uvs };
            mesh.RecalculateNormalsSmooth();

            // Ensure MeshFilter
            var mf = GetComponent<MeshFilter>();
            if (mf == null) { mf = new MeshFilter(); gameObject.AddBehavior(mf); }
            mf.Mesh = mesh;

            // Ensure MeshRenderer (pairs with MeshFilter in renderers)
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) { mr = new MeshRenderer(); gameObject.AddBehavior(mr); }

            // Ensure MeshCollider matches terrain (since it's required)
            var mc = GetComponent<MeshCollider>();
            if (mc == null) { mc = new MeshCollider(); gameObject.AddBehavior(mc); }
            mc.Mesh = mesh;

            if (AutoSaveOnChange) TrySaveToFile();

            // Proactively notify editors/views to repaint
            Game_Engine.Core.SceneService.NotifyChanged();
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
                    Heights = Heights
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
                            Heights[z * ResX + x] = Clamp01(v);
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

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
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