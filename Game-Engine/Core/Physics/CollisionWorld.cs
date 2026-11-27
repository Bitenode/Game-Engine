
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// Central registry + static/dynamic BVH broadphase with mesh-accurate narrow phase.
    public static class CollisionWorld
    {
        private struct StaticEntry
        {
            public Component.Collider Collider;
            public SN.Vector3 Min;
            public SN.Vector3 Max;
        }

        const float StaticAabbEpsilon = 1e-4f;

        static readonly List<Component.Collider> _colliders = new List<Component.Collider>();
        static readonly BVH _staticBvh = new();
        static readonly BVH _dynamicBvh = new();
        static readonly List<Component.Collider> _staticBuild = new(256);
        static readonly List<Component.Collider> _dynamicBuild = new(32);
        static readonly List<StaticEntry> _cachedStatic = new(256);
        static readonly List<Component.Collider> _queryScratch = new(64);
        static int _bvhFrame = -1;
        static bool _staticDirty = true;

        /// <summary>Number of static colliders in the broadphase after the most recent update.</summary>
        public static int LastStaticColliderCount { get; private set; }

        /// <summary>Number of dynamic colliders in the broadphase after the most recent update.</summary>
        public static int LastDynamicColliderCount { get; private set; }

        /// <summary>Total number of times the static BVH has been rebuilt.</summary>
        public static int StaticBvhRebuildCount { get; private set; }

        /// <summary>Force the static BVH to rebuild on the next broadphase update.</summary>
        public static void MarkStaticBroadphaseDirty() => _staticDirty = true;

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
            var results = new List<Component.Collider>();
            QueryAABB(min, max, results, layerMask);
            return results;
        }

        /// Populate <paramref name="results"/> with all colliders whose world AABB overlaps the given AABB.
        public static void QueryAABB(SN.Vector3 min, SN.Vector3 max, List<Component.Collider> results, int layerMask = -1)
        {
            RebuildBroadphaseIfNeeded();
            _queryScratch.Clear();
            _staticBvh.QueryAABB(min, max, _queryScratch);
            _dynamicBvh.QueryAABB(min, max, _queryScratch);
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
                if (!c.IsActiveAndEnabled) continue;
                if (!PhysicsLayerMask.Includes(layerMask, c.gameObject.Layer)) continue;
                var a = c.GetWorldAABB();
                if (Overlaps(a.Min, a.Max, min, max)) results.Add(c);
            }
        }

        /// Very basic “is anything inside me?” helper for triggers, etc.
        public static bool AnyOverlap(Component.Collider col, out Component.Collider other)
        {
            RebuildBroadphaseIfNeeded();
            var a = col.GetWorldAABB();
            _queryScratch.Clear();
            _staticBvh.QueryAABB(a.Min, a.Max, _queryScratch);
            _dynamicBvh.QueryAABB(a.Min, a.Max, _queryScratch);
            for (int i = 0; i < _queryScratch.Count; i++)
            {
                var c = _queryScratch[i];
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

        static bool IsDynamicCollider(Component.Collider c)
        {
            var go = c.gameObject;
            if (go == null) return false;
            var behaviors = go.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
            {
                var b = behaviors[i];
                if (b is Component.Rigidbody rb && rb.IsActiveAndEnabled && !rb.IsKinematic)
                    return true;
                if (b is Component.CharacterController cc && cc.IsActiveAndEnabled)
                    return true;
            }
            return false;
        }

        static bool AabbChanged(SN.Vector3 prevMin, SN.Vector3 prevMax, SN.Vector3 curMin, SN.Vector3 curMax)
            => MathF.Abs(prevMin.X - curMin.X) > StaticAabbEpsilon
            || MathF.Abs(prevMin.Y - curMin.Y) > StaticAabbEpsilon
            || MathF.Abs(prevMin.Z - curMin.Z) > StaticAabbEpsilon
            || MathF.Abs(prevMax.X - curMax.X) > StaticAabbEpsilon
            || MathF.Abs(prevMax.Y - curMax.Y) > StaticAabbEpsilon
            || MathF.Abs(prevMax.Z - curMax.Z) > StaticAabbEpsilon;

        public static IReadOnlyList<Component.Collider> All => _colliders;

        internal static void RebuildBroadphaseIfNeeded()
        {
            int frame = Time.frameCount;
            if (frame == _bvhFrame && !_staticDirty) return;
            _bvhFrame = frame;

            _staticBuild.Clear();
            _dynamicBuild.Clear();

            bool staticNeedsRebuild = _staticDirty;
            int staticIndex = 0;

            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;
                if (c is Component.PlanetCollider) continue;
                if (c is Component.MeshCollider
                    && PhysicsCache.IsTerrainAssociated(c.gameObject)
                    && !Component.TreeTrunk.IsOn(c.gameObject))
                    continue;

                if (IsDynamicCollider(c))
                {
                    _dynamicBuild.Add(c);
                    continue;
                }

                _staticBuild.Add(c);

                if (!staticNeedsRebuild)
                {
                    if (staticIndex >= _cachedStatic.Count || !ReferenceEquals(_cachedStatic[staticIndex].Collider, c))
                    {
                        staticNeedsRebuild = true;
                    }
                    else
                    {
                        var aabb = c.GetWorldAABB();
                        var prev = _cachedStatic[staticIndex];
                        if (AabbChanged(prev.Min, prev.Max, aabb.Min, aabb.Max))
                            staticNeedsRebuild = true;
                    }
                }
                staticIndex++;
            }

            if (!staticNeedsRebuild && staticIndex != _cachedStatic.Count)
                staticNeedsRebuild = true;

            if (staticNeedsRebuild)
            {
                _staticDirty = false;
                _cachedStatic.Clear();
                for (int i = 0; i < _staticBuild.Count; i++)
                {
                    var c = _staticBuild[i];
                    var aabb = c.GetWorldAABB();
                    _cachedStatic.Add(new StaticEntry { Collider = c, Min = aabb.Min, Max = aabb.Max });
                }
                _staticBvh.Build(_staticBuild);
                StaticBvhRebuildCount++;
            }

            _dynamicBvh.Build(_dynamicBuild);

            LastStaticColliderCount = _staticBuild.Count;
            LastDynamicColliderCount = _dynamicBuild.Count;
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
            _staticBvh.QueryAABB(min, max, _queryScratch);
            _dynamicBvh.QueryAABB(min, max, _queryScratch);
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
            QueryTriggerInteraction queryTriggers = QueryTriggerInteraction.Ignore,
            Component.Collider? ignore = null)
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
                if (ignore != null && (ReferenceEquals(c, ignore) || ReferenceEquals(c.gameObject, ignore.gameObject)))
                    continue;
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
            _staticBvh.OverlapSphere(center, radius, _queryScratch);
            _dynamicBvh.OverlapSphere(center, radius, _queryScratch);
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
