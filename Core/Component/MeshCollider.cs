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

            var path = BuildPath(mf.gameObject);
            if (!TargetPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                TargetPaths.Add(path);
        }

        public void RemoveTarget(MeshFilter mf)
        {
            if (mf == null) return;
            _targets.Remove(mf);

            var path = BuildPath(mf.gameObject);
            TargetPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        // ---- core logic ----

        public IEnumerable<(Mesh mesh, SN.Matrix4x4 world)> EnumerateTargetMeshesWorld()
        {
            // manual override takes precedence
            if (Mesh != null && Mesh.Vertices != null && Mesh.Vertices.Length > 0)
            {
                var W = TransformUtil.WorldFromTransform(gameObject.Transform);
                yield return (Mesh, W);
                yield break;
            }

            EnsureTargetsResolved();

            if (_targets.Count == 0)
            {
                // fallback to this GO's MeshFilter
                var here = gameObject.Behaviors.OfType<MeshFilter>().FirstOrDefault(b => b.Enabled && b.Mesh != null);
                if (here != null)
                {
                    var W = BindToTargetTransform
                        ? TransformUtil.WorldFromTransform(here.gameObject.Transform)
                        : TransformUtil.WorldFromTransform(gameObject.Transform);
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
                    ? TransformUtil.WorldFromTransform(mf.gameObject.Transform)
                    : TransformUtil.WorldFromTransform(gameObject.Transform);

                yield return (mf.Mesh, W);
            }
        }

        public override AABB GetWorldAABB()
        {
            // 0) Manual override mesh = single AABB
            if (Mesh != null && Mesh.Vertices != null && Mesh.Vertices.Length > 0)
            {
                var W = TransformUtil.WorldFromTransform(gameObject.Transform);
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
                        ? TransformUtil.WorldFromTransform(mf.gameObject.Transform)
                        : TransformUtil.WorldFromTransform(gameObject.Transform);

                    var a = AABBForMesh(mf.Mesh, W);
                    Encapsulate(ref min, ref max, a.Min);
                    Encapsulate(ref min, ref max, a.Max);
                }

                if (min.X != float.MaxValue) // at least one valid
                    return new AABB(min, max);
            }

            //Fallback: MeshFilter on THIS GameObject, or a point at origin
            var here = gameObject.Behaviors.OfType<MeshFilter>().FirstOrDefault(b => b.Enabled && b.Mesh != null);
            if (here != null)
            {
                var W = BindToTargetTransform
                    ? TransformUtil.WorldFromTransform(here.gameObject.Transform)
                    : TransformUtil.WorldFromTransform(gameObject.Transform);
                return AABBForMesh(here.Mesh, W);
            }

            var W0 = TransformUtil.WorldFromTransform(gameObject.Transform);
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
            // Keep any already-set runtime refs; fill missing ones from paths.
            for (int i = 0; i < TargetPaths.Count; i++)
            {
                var path = TargetPaths[i];
                if (_targets.Any(tf => string.Equals(BuildPath(tf?.gameObject), path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var go = FindByPath(path);
                var mf = go?.Behaviors.OfType<MeshFilter>().FirstOrDefault(b => b.Enabled && b.Mesh != null);
                if (mf != null) _targets.Add(mf);
            }

            // Clean up stale entries (paths that no longer resolve)
            _targets.RemoveAll(tf =>
            {
                var p = BuildPath(tf?.gameObject);
                return !TargetPaths.Any(t => string.Equals(t, p, StringComparison.OrdinalIgnoreCase));
            });
        }

        // -------- path helpers (same logic as earlier single-target version) --------
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
