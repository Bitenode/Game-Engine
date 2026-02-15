using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;


namespace Game_Engine.Core.Component
{
    public sealed class MeshCollider : Collider
    {
        // RUNTIME cache of resolved filters (not persisted)
        readonly List<MeshFilter> _targets = new List<MeshFilter>();
        public IReadOnlyList<MeshFilter> TargetFilters => _targets;

        // Persisted scene paths for each target filter
        [Persist] public List<string> TargetPaths { get; private set; } = new List<string>();

        [Persist] public bool BindToTargetTransform { get; set; } = true;

        // manual override (if set, we ignore TargetFilters/Paths)
        [Persist] public Mesh Mesh { get; set; }

        // ---- public API used by Inspector ----
        public void ClearTargets()
        {
            _targets.Clear();
            TargetPaths.Clear();
        }

        public void AddTarget(MeshFilter mf)
        {
            if (mf == null) return;
            if (!_targets.Contains(mf)) _targets.Add(mf);

            var key = BuildKey(mf);
            if (!TargetPaths.Any(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase)))
                TargetPaths.Add(key);
        }

        public void RemoveTarget(MeshFilter mf)
        {
            if (mf == null) return;
            _targets.Remove(mf);
            var key = BuildKey(mf);
            TargetPaths.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
        }

        


        static string BuildKey(MeshFilter mf)
        {
            var path = BuildPath(mf.gameObject);
            var ord = GetOrdinalOnOwner(mf);
            return $"{path}#mf:{ord}";
        }

        static bool TryParseKey(string key, out string basePath, out int ordinal)
        {
            basePath = key;
            ordinal = 0;
            var i = key.LastIndexOf("#mf:", StringComparison.Ordinal);
            if (i < 0) return false;
            basePath = key.Substring(0, i);
            var tail = key.Substring(i + 4);
            return int.TryParse(tail, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out ordinal);
        }

        static int GetOrdinalOnOwner(MeshFilter mf)
        {
            var list = mf.gameObject?.Behaviors?.OfType<MeshFilter>().ToList();
            if (list == null) return 0;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], mf)) return i;
            return 0;
        }


        // ---- core logic ----

        public IEnumerable<(Mesh mesh, SN.Matrix4x4 world)> EnumerateTargetMeshesWorld()
        {
            // manual override takes precedence
            if (Mesh != null && Mesh.Vertices != null && Mesh.Vertices.Length > 0)
            {
                var W = SceneGraphUtil.AccumulateWorld(gameObject);
                yield return (Mesh, W);
                yield break;
            }

            EnsureTargetsResolved();

            if (_targets.Count == 0)
            {
                // fallback to ALL MeshFilters on this GO (multi-layer models have multiple)
                foreach (var here in gameObject.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled && b.Mesh != null))
                {
                    var W = BindToTargetTransform
                        ? SceneGraphUtil.AccumulateWorld(here.gameObject)
                        : SceneGraphUtil.AccumulateWorld(gameObject);
                    yield return (here.Mesh, W);
                }
                yield break;
            }

            // each target mesh, using its own transform if requested
            for (int i = 0; i < _targets.Count; i++)
            {
                var mf = _targets[i];
                if (mf == null || mf.Mesh == null || mf.Mesh.Vertices == null || mf.Mesh.Vertices.Length == 0)
                    continue;

                var W = BindToTargetTransform && mf.gameObject != null
                    ? SceneGraphUtil.AccumulateWorld(mf.gameObject)
                    : SceneGraphUtil.AccumulateWorld(gameObject);

                yield return (mf.Mesh, W);
            }
        }

        public override AABB GetWorldAABB()
        {
            // 0) Manual override mesh = single AABB
            if (Mesh != null && Mesh.Vertices != null && Mesh.Vertices.Length > 0)
            {
                var W = SceneGraphUtil.AccumulateWorld(gameObject);
                return AABBForMesh(Mesh, W);
            }

            // Ensure runtime targets reflect persisted paths
            EnsureTargetsResolved();

            // If we have multiple targets, union their AABBs
            if (_targets.Count > 0)
            {
                SN.Vector3 min = new SN.Vector3(float.MaxValue);
                SN.Vector3 max = new SN.Vector3(float.MinValue);

                for (int i = 0; i < _targets.Count; i++)
                {
                    var mf = _targets[i];
                    if (mf == null || mf.Mesh == null || mf.Mesh.Vertices == null || mf.Mesh.Vertices.Length == 0) continue;

                    var W = BindToTargetTransform && mf.gameObject != null
                        ? SceneGraphUtil.AccumulateWorld(mf.gameObject)
                        : SceneGraphUtil.AccumulateWorld(gameObject);

                    var a = AABBForMesh(mf.Mesh, W);
                    Encapsulate(ref min, ref max, a.Min);
                    Encapsulate(ref min, ref max, a.Max);
                }

                if (min.X != float.MaxValue) // at least one valid
                    return new AABB(min, max);
            }

            //Fallback: ALL MeshFilters on THIS GameObject, or a point at origin
            {
                SN.Vector3 min = new SN.Vector3(float.MaxValue);
                SN.Vector3 max = new SN.Vector3(float.MinValue);
                bool any = false;
                foreach (var here in gameObject.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled && b.Mesh != null))
                {
                    var W = BindToTargetTransform
                        ? SceneGraphUtil.AccumulateWorld(here.gameObject)
                        : SceneGraphUtil.AccumulateWorld(gameObject);
                    var a = AABBForMesh(here.Mesh, W);
                    Encapsulate(ref min, ref max, a.Min);
                    Encapsulate(ref min, ref max, a.Max);
                    any = true;
                }
                if (any) return new AABB(min, max);
            }

            var W0 = SceneGraphUtil.AccumulateWorld(gameObject);
            var p0 = SN.Vector3.Transform(SN.Vector3.Zero, W0);
            return new AABB(p0, p0);
        }

        static AABB AABBForMesh(Mesh mesh, SN.Matrix4x4 W)
        {
            SN.Vector3 min = new SN.Vector3(float.MaxValue);
            SN.Vector3 max = new SN.Vector3(float.MinValue);
            var vtx = mesh.Vertices;
            for (int i = 0; i < vtx.Length; i++)
            {
                var p = SN.Vector3.Transform(vtx[i], W);
                Encapsulate(ref min, ref max, p);
            }
            return new AABB(min, max);
        }

        void EnsureTargetsResolved()
        {
            // keep already-resolved
            for (int i = 0; i < TargetPaths.Count; i++)
            {
                var key = TargetPaths[i];
                // already present?
                if (_targets.Any(tf => string.Equals(BuildKey(tf), key, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // parse key
                string basePath; int ord;
                if (!TryParseKey(key, out basePath, out ord))
                {
                    // back-compat: key without #mf: — default to first filter
                    basePath = key;
                    ord = 0;
                }

                var go = FindByPath(basePath);
                if (go == null) continue;

                var list = go.Behaviors.OfType<MeshFilter>().ToList();
                if (list.Count == 0) continue;

                if (ord < 0 || ord >= list.Count) ord = 0;  // clamp
                var mf = list[ord];
                if (mf != null && mf.Mesh != null) _targets.Add(mf);
            }

            // drop stale
            _targets.RemoveAll(tf =>
            {
                var k = BuildKey(tf);
                return !TargetPaths.Any(p => string.Equals(p, k, StringComparison.OrdinalIgnoreCase));
            });
        }

        // -------- path helpers  --------
        static string BuildPath(GameObject go)
        {
            if (go == null) return string.Empty;
            var stack = new Stack<string>();
            var n = go;
            while (n != null) { stack.Push(n.Name ?? "GameObject"); n = n.Parent; }
            return string.Join("/", stack.ToArray());
        }

        static GameObject FindByPath(string path)
        {
            var parts = (path ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            foreach (var root in Core.SceneService.Root)
            {
                if (!string.Equals(root.Name ?? "", parts[0], StringComparison.OrdinalIgnoreCase))
                    continue;

                var found = Walk(root, parts, 1);
                if (found != null) return found;
            }
            foreach (var root in Core.SceneService.Root)
            {
                var found = Walk(root, parts, 0);
                if (found != null) return found;
            }
            return null;

            GameObject Walk(GameObject node, string[] tokens, int index)
            {
                if (index >= tokens.Length) return node;
                var next = tokens[index];
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (string.Equals(child.Name ?? "", next, StringComparison.OrdinalIgnoreCase))
                        return Walk(child, tokens, index + 1);
                }
                return null;
            }
        }
    }
}
