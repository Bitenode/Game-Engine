
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// Central registry + BVH broadphase with mesh-accurate narrow phase.
    public static class CollisionWorld
    {
        static readonly List<Component.Collider> _colliders = new List<Component.Collider>();
        static readonly BVH _bvh = new();
        static readonly List<Component.Collider> _bvhBuild = new(256);
        static readonly List<Component.Collider> _queryScratch = new(64);
        static int _bvhFrame = -1;

        internal static void Register(Component.Collider c)
        {
            if (c != null && !_colliders.Contains(c)) _colliders.Add(c);
        }

        internal static void Unregister(Component.Collider c)
        {
            if (c != null) _colliders.Remove(c);
        }

        /// Return all colliders whose world AABB overlaps the given AABB.
        public static IEnumerable<Component.Collider> QueryAABB(SN.Vector3 min, SN.Vector3 max, int layerMask = -1)
        {
            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;
                if (!PhysicsLayerMask.Includes(layerMask, c.gameObject.Layer)) continue;
                var a = c.GetWorldAABB();
                if (Overlaps(a.Min, a.Max, min, max)) yield return c;
            }
        }

        /// Very basic “is anything inside me?” helper for triggers, etc.
        public static bool AnyOverlap(Component.Collider col, out Component.Collider other)
        {
            var a = col.GetWorldAABB();
            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (ReferenceEquals(c, col) || !c.IsActiveAndEnabled) continue;
                var b = c.GetWorldAABB();
                if (Overlaps(a.Min, a.Max, b.Min, b.Max)) { other = c; return true; }
            }
            other = null;
            return false;
        }

        static bool Overlaps(SN.Vector3 aMin, SN.Vector3 aMax, SN.Vector3 bMin, SN.Vector3 bMax)
            => (aMin.X <= bMax.X && aMax.X >= bMin.X) &&
               (aMin.Y <= bMax.Y && aMax.Y >= bMin.Y) &&
               (aMin.Z <= bMax.Z && aMax.Z >= bMin.Z);

        public static IReadOnlyList<Component.Collider> All => _colliders;

        internal static void RebuildBroadphaseIfNeeded()
        {
            int frame = Time.frameCount;
            if (frame == _bvhFrame) return;
            _bvhFrame = frame;
            _bvhBuild.Clear();
            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;
                if (c is Component.PlanetCollider) continue;
                _bvhBuild.Add(c);
            }
            _bvh.Build(_bvhBuild);
        }

        static bool IncludeCollider(Component.Collider c, int layerMask, QueryTriggerInteraction triggers)
        {
            if (!c.IsActiveAndEnabled) return false;
            if (c is Component.PlanetCollider) return false;
            if (c.IsTrigger && triggers == QueryTriggerInteraction.Ignore) return false;
            if (!PhysicsLayerMask.Includes(layerMask, c.gameObject.Layer)) return false;
            return true;
        }

        static void CollectSwept(SN.Vector3 origin, SN.Vector3 direction, float maxDist, float radius)
        {
            RebuildBroadphaseIfNeeded();
            _queryScratch.Clear();
            if (direction.LengthSquared() < 1e-12f) direction = SN.Vector3.UnitZ;
            var end = origin + SN.Vector3.Normalize(direction) * maxDist;
            var min = SN.Vector3.Min(origin, end) - new SN.Vector3(radius);
            var max = SN.Vector3.Max(origin, end) + new SN.Vector3(radius);
            _bvh.QueryAABB(min, max, _queryScratch);
        }

        // ── Raycast API ──

        /// <summary>Result of a physics raycast.</summary>
        public struct RaycastHit
        {
            public SN.Vector3 Point;
            public SN.Vector3 Normal;
            public float Distance;
            public Component.Collider Collider;
            public GameObject? GameObject => Collider?.gameObject;
        }

        /// <summary>
        /// Cast a ray through the physics world. Mesh colliders use triangle tests; others use AABB.
        /// Planet colliders are skipped — use <see cref="Component.PlanetTerrain.RaycastDensityGameplay"/>.
        /// </summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDist, out RaycastHit hit, int layerMask = -1,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.Ignore)
        {
            hit = default;
            direction = SN.Vector3.Normalize(direction);
            CollectSwept(origin, direction, maxDist, 0.01f);
            float best = maxDist;
            bool found = false;
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!IncludeCollider(c, layerMask, queryTriggers)) continue;
                if (!ColliderQueries.Raycast(c, origin, direction, best, out var n)) continue;
                best = n.Distance;
                hit = n;
                found = true;
            }
            return found;
        }

        /// <summary>Cast a ray and return ALL hits (unsorted).</summary>
        public static List<RaycastHit> RaycastAll(SN.Vector3 origin, SN.Vector3 direction, float maxDist, int layerMask = -1,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.Collide)
        {
            direction = SN.Vector3.Normalize(direction);
            CollectSwept(origin, direction, maxDist, 0.01f);
            var results = new List<RaycastHit>();
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!IncludeCollider(c, layerMask, queryTriggers)) continue;
                if (ColliderQueries.Raycast(c, origin, direction, maxDist, out var n))
                    results.Add(n);
            }
            return results;
        }

        public static bool SphereCast(SN.Vector3 origin, SN.Vector3 direction, float radius, float maxDist,
            out RaycastHit hit, int layerMask = -1,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.Ignore)
        {
            hit = default;
            direction = SN.Vector3.Normalize(direction);
            CollectSwept(origin, direction, maxDist, Math.Max(0.01f, radius));
            float best = maxDist;
            bool found = false;
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!IncludeCollider(c, layerMask, queryTriggers)) continue;
                if (!ColliderQueries.SphereCast(c, origin, direction, radius, best, out var n)) continue;
                best = n.Distance;
                hit = n;
                found = true;
            }
            return found;
        }

        public static bool CapsuleCast(SN.Vector3 point1, SN.Vector3 point2, float radius, SN.Vector3 direction, float maxDist,
            out RaycastHit hit, int layerMask = -1,
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.Ignore)
        {
            hit = default;
            direction = SN.Vector3.Normalize(direction);
            var mid = (point1 + point2) * 0.5f;
            float extra = SN.Vector3.Distance(point1, point2) * 0.5f + radius;
            CollectSwept(mid, direction, maxDist, extra);
            float best = maxDist;
            bool found = false;
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!IncludeCollider(c, layerMask, queryTriggers)) continue;
                if (!ColliderQueries.CapsuleCast(c, point1, point2, radius, direction, best, out var n)) continue;
                best = n.Distance;
                hit = n;
                found = true;
            }
            return found;
        }

        /// <summary>Sphere overlap query — returns all colliders within a sphere.</summary>
        public static List<Component.Collider> OverlapSphere(SN.Vector3 center, float radius, int layerMask = -1)
        {
            RebuildBroadphaseIfNeeded();
            _queryScratch.Clear();
            _bvh.OverlapSphere(center, radius, _queryScratch);
            var results = new List<Component.Collider>();
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!IncludeCollider(c, layerMask, QueryTriggerInteraction.Collide)) continue;
                results.Add(c);
            }
            return results;
        }
    }
}
