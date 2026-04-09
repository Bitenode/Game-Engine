using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Streams square terrain tiles in a ring around a world focus point (camera).
    /// Each tile is a child <see cref="GameObject"/> with a <see cref="Terrain"/> component
    /// and an asset path under <see cref="TilesSubfolder"/> (e.g. <c>tile_0_0.terrain.json</c>).
    /// Call <see cref="SyncAll"/> from the render loop with the active camera position.
    /// </summary>
    [ComponentCategory("Environment")]
    public sealed class TerrainStreamer : Behavior
    {
        static readonly List<TerrainStreamer> Instances = new();
        static readonly object InstanceLock = new();

        /// <summary>When false, tile set is not modified by <see cref="SyncStreaming"/> (fixed layout).</summary>
        [Persist] public bool StreamingEnabled { get; set; } = true;

        /// <summary>Update streaming for every active streamer using the given world-space focus (e.g. camera).</summary>
        public static void SyncAll(in SN.Vector3 worldFocus)
        {
            lock (InstanceLock)
            {
                for (int i = 0; i < Instances.Count; i++)
                {
                    var s = Instances[i];
                    if (s.IsActiveAndEnabled)
                        s.SyncStreaming(worldFocus);
                }
            }
        }

        /// <summary>Project-relative folder for tile assets (e.g. <c>Assets/Terrain/StreamedWorld</c>).</summary>
        [Persist] public string TilesSubfolder { get; set; } = "Assets/Terrain/StreamedWorld";

        /// <summary>Edge length of each square tile in world units (matches <see cref="Terrain.SizeX"/> / <see cref="Terrain.SizeZ"/>).</summary>
        [Persist] public float TileWorldSize { get; set; } = 100f;

        /// <summary>Chebyshev radius: tiles with max(|dx|,|dz|) &lt;= this value stay loaded around the focus tile.</summary>
        [Persist] public int RingRadius { get; set; } = 2;

        /// <summary>Heightmap resolution per tile (X).</summary>
        [Persist] public int TileResolutionX { get; set; } = 129;

        /// <summary>Heightmap resolution per tile (Z).</summary>
        [Persist] public int TileResolutionZ { get; set; } = 129;

        [Persist] public float TileHeightScale { get; set; } = 20f;

        [Persist] public int TileChunkSize { get; set; } = 65;
        [Persist] public bool TileUseChunking { get; set; } = true;
        [Persist] public int TileLodLevels { get; set; } = 3;

        /// <summary>When true, tile files use <c>.terrain.bin</c> instead of <c>.terrain.json</c>.</summary>
        [Persist] public bool SaveTilesAsBinary { get; set; }

        /// <summary>
        /// If &gt;= 0, only tiles within this Chebyshev distance of the center tile keep an enabled <see cref="MeshCollider"/>.
        /// Outer tiles remain visible but use <see cref="Terrain.SampleHeightWorld"/> / height queries only.
        /// </summary>
        [Persist] public int CollisionRingRadius { get; set; } = 1;

        readonly Dictionary<(int tx, int tz), GameObject> _active = new();

        public override void OnEnable()
        {
            lock (InstanceLock) { if (!Instances.Contains(this)) Instances.Add(this); }
            RebuildActiveFromChildren();
            base.OnEnable();
        }

        public override void OnDisable()
        {
            UnloadAll(save: true);
            lock (InstanceLock) { Instances.Remove(this); }
            base.OnDisable();
        }

        public override void PostDeserialize()
        {
            _active.Clear();
            RebuildActiveFromChildren();
        }

        /// <summary>Remove all streamed tiles, optionally saving dirty terrains first.</summary>
        public void UnloadAll(bool save)
        {
            foreach (var kv in _active)
            {
                var go = kv.Value;
                if (go == null) continue;
                if (save)
                    TrySaveTile(go);
                go.RemoveFromParent();
            }
            _active.Clear();
            SceneService.NotifyChanged();
        }

        void TrySaveTile(GameObject tileGo)
        {
            foreach (var b in tileGo.Behaviors)
            {
                if (b is Terrain t && t.NeedsAssetSave)
                {
                    t.Save();
                    t.NeedsAssetSave = false;
                }
            }
        }

        public void SyncStreaming(in SN.Vector3 worldFocus)
        {
            if (gameObject == null || !StreamingEnabled)
                return;

            float T = Math.Max(0.001f, TileWorldSize);
            var streamerW = WorldMatrixFromRoot(gameObject);
            if (!SN.Matrix4x4.Invert(streamerW, out var invStreamer))
                return;

            var local = SN.Vector3.Transform(worldFocus, invStreamer);
            int cx = (int)Math.Floor((local.X + T * 0.5f) / T);
            int cz = (int)Math.Floor((local.Z + T * 0.5f) / T);

            int r = Math.Max(0, RingRadius);
            var want = new HashSet<(int, int)>();
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                    want.Add((cx + dx, cz + dz));
            }

            var remove = new List<(int, int)>();
            foreach (var key in _active.Keys)
            {
                if (!want.Contains(key))
                    remove.Add(key);
            }

            foreach (var key in remove)
            {
                if (_active.TryGetValue(key, out var go) && go != null)
                {
                    TrySaveTile(go);
                    go.RemoveFromParent();
                }
                _active.Remove(key);
            }

            foreach (var key in want)
            {
                if (_active.ContainsKey(key)) continue;
                var go = CreateTile(key.Item1, key.Item2);
                _active[key] = go;
                gameObject.AddChild(go);
            }

            foreach (var kv in _active)
            {
                if (kv.Value == null) continue;
                ApplyCollisionRing(kv.Key.Item1, kv.Key.Item2, cx, cz, kv.Value);
            }

            SceneService.NotifyChanged();
        }

        void ApplyCollisionRing(int tx, int tz, int centerX, int centerZ, GameObject tileGo)
        {
            int cr = CollisionRingRadius;
            if (cr < 0)
            {
                foreach (var b in tileGo.Behaviors)
                {
                    if (b is MeshCollider mc)
                        mc.Enabled = true;
                }
                return;
            }

            int d = Math.Max(Math.Abs(tx - centerX), Math.Abs(tz - centerZ));
            bool coll = d <= cr;
            foreach (var b in tileGo.Behaviors)
            {
                if (b is MeshCollider mc)
                    mc.Enabled = coll;
            }
        }

        GameObject CreateTile(int tx, int tz)
        {
            float T = Math.Max(0.001f, TileWorldSize);
            string ext = SaveTilesAsBinary ? ".terrain.bin" : ".terrain.json";
            string folder = NormalizeSlashes(TilesSubfolder.Trim());
            if (string.IsNullOrWhiteSpace(folder))
                folder = "Assets/Terrain/StreamedWorld";
            string rel = string.IsNullOrEmpty(folder)
                ? $"tile_{tx}_{tz}{ext}"
                : Path.Combine(folder, $"tile_{tx}_{tz}{ext}").Replace('\\', '/');

            var go = new GameObject($"TerrainTile_{tx}_{tz}");
            go.Transform.Position = new Vector3(tx * T, 0, tz * T);

            var terr = new Terrain
            {
                TerrainAssetPath = rel,
                ResX = Math.Max(2, TileResolutionX),
                ResZ = Math.Max(2, TileResolutionZ),
                SizeX = T,
                SizeZ = T,
                HeightScale = TileHeightScale,
                ChunkSize = Math.Max(3, TileChunkSize),
                UseChunking = TileUseChunking,
                LodLevels = Math.Clamp(TileLodLevels, 1, 8),
                AutoLoadOnStart = true,
                AutoSaveOnChange = false
            };

            go.AddBehavior(new MeshFilter());
            go.AddBehavior(new MeshRenderer());
            go.AddBehavior(new MeshCollider());
            go.AddBehavior(terr);

            terr.NeedsAssetSave = false;
            return go;
        }

        static SN.Matrix4x4 WorldMatrixFromRoot(GameObject go)
        {
            var path = new List<GameObject>();
            for (var n = go; n != null; n = n.Parent)
                path.Add(n);
            SN.Matrix4x4 w = SN.Matrix4x4.Identity;
            for (int i = path.Count - 1; i >= 0; i--)
                w = w * TransformUtil.WorldFromTransform(path[i].Transform);
            return w;
        }

        static string NormalizeSlashes(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            return p.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        void RebuildActiveFromChildren()
        {
            if (gameObject == null) return;
            foreach (var ch in gameObject.Children)
            {
                if (ch.Name == null || !ch.Name.StartsWith("TerrainTile_", StringComparison.Ordinal)) continue;
                if (!TryParseTileName(ch.Name, out int tx, out int tz)) continue;
                _active[(tx, tz)] = ch;
            }
        }

        static bool TryParseTileName(string name, out int tx, out int tz)
        {
            tx = tz = 0;
            // TerrainTile_{tx}_{tz}
            const string prefix = "TerrainTile_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var rest = name.AsSpan(prefix.Length);
            int u = rest.IndexOf('_');
            if (u <= 0 || u >= rest.Length - 1) return false;
            var sx = rest[..u];
            var sz = rest[(u + 1)..];
            return int.TryParse(sx, out tx) && int.TryParse(sz, out tz);
        }
    }
}
