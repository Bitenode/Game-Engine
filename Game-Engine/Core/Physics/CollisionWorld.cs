
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// <summary>
    /// Central registry + BVH-accelerated broadphase queries.
    /// Maintains a BVH that is rebuilt each frame for O(log n) spatial queries
    /// (raycasts, AABB overlaps, sphere overlaps) instead of linear scans.
    /// </summary>
    public static class CollisionWorld
    {
        static readonly List<Component.Collider> _colliders = new List<Component.Collider>();

        /// <summary>BVH acceleration structure — rebuilt each physics frame.</summary>
        private static readonly BVH _bvh = new();
        private static int _lastBuildFrame = -1;

        internal static void Register(Component.Collider c)
        {
            if (c != null && !_colliders.Contains(c)) _colliders.Add(c);
        }

        internal static void Unregister(Component.Collider c)
        {
            if (c != null) _colliders.Remove(c);
        }

        /// <summary>Ensure the BVH is up to date for this frame.</summary>
        private static void EnsureBVH()
        {
            int frame = Time.frameCount;
            if (frame == _lastBuildFrame) return;
            _lastBuildFrame = frame;

            // Filter to active colliders and build BVH
            var active = new List<Component.Collider>(_colliders.Count);
            for (int i = 0; i < _colliders.Count; i++)
            {
                if (_colliders[i].IsActiveAndEnabled)
                    active.Add(_colliders[i]);
            }
            _bvh.Build(active);
        }

        /// <summary>Force a BVH rebuild on next query.</summary>
        public static void InvalidateBVH() => _lastBuildFrame = -1;

        /// <summary>Return all colliders whose world AABB overlaps the given AABB.</summary>
        public static IEnumerable<Component.Collider> QueryAABB(SN.Vector3 min, SN.Vector3 max)
        {
            EnsureBVH();
            var results = new List<Component.Collider>();
            _bvh.QueryAABB(min, max, results);
            return results;
        }

        /// <summary>Very basic "is anything inside me?" helper for triggers, etc.</summary>
        public static bool AnyOverlap(Component.Collider col, out Component.Collider other)
        {
            var a = col.GetWorldAABB();
            // Use BVH for broad-phase, then narrow-phase AABB check
            EnsureBVH();
            var candidates = new List<Component.Collider>();
            _bvh.QueryAABB(a.Min, a.Max, candidates);

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
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
        /// Cast a ray through the physics world using BVH acceleration.
        /// Returns true if any collider was hit.
        /// </summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDist, out RaycastHit hit, int layerMask = -1)
        {
            hit = default;
            direction = SN.Vector3.Normalize(direction);

            EnsureBVH();

            if (_bvh.Raycast(origin, direction, maxDist,
                    out var hitPoint, out var hitNormal, out var hitDist, out var hitCollider))
            {
                hit = new RaycastHit
                {
                    Point = hitPoint,
                    Normal = hitNormal,
                    Distance = hitDist,
                    Collider = hitCollider!
                };
                return true;
            }
            return false;
        }

        /// <summary>
        /// Cast a ray and return ALL hits (unsorted). Uses BVH for acceleration.
        /// </summary>
        public static List<RaycastHit> RaycastAll(SN.Vector3 origin, SN.Vector3 direction, float maxDist)
        {
            direction = SN.Vector3.Normalize(direction);
            var results = new List<RaycastHit>();

            EnsureBVH();
            var bvhHits = new List<(Component.Collider collider, float distance, SN.Vector3 point, SN.Vector3 normal)>();
            _bvh.RaycastAll(origin, direction, maxDist, bvhHits);

            for (int i = 0; i < bvhHits.Count; i++)
            {
                results.Add(new RaycastHit
                {
                    Point = bvhHits[i].point,
                    Normal = bvhHits[i].normal,
                    Distance = bvhHits[i].distance,
                    Collider = bvhHits[i].collider
                });
            }
            return results;
        }

        /// <summary>Sphere overlap query — returns all colliders within the sphere. BVH-accelerated.</summary>
        public static List<Component.Collider> OverlapSphere(SN.Vector3 center, float radius)
        {
            EnsureBVH();
            var results = new List<Component.Collider>();
            _bvh.OverlapSphere(center, radius, results);
            return results;
        }

        /// <summary>Ray vs AABB intersection (slab method). Kept for legacy/direct use.</summary>
        internal static bool RayAABB(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 min, SN.Vector3 max,
            out float tHit, out SN.Vector3 normal)
        {
            tHit = 0;
            normal = SN.Vector3.Zero;

            float tmin = float.NegativeInfinity;
            float tmax = float.PositiveInfinity;
            int hitAxis = 0;
            bool hitMin = false;

            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
                float bmin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float bmax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < bmin || o > bmax) return false;
                }
                else
                {
                    float t1 = (bmin - o) / d;
                    float t2 = (bmax - o) / d;

                    bool swapped = false;
                    if (t1 > t2) { (t1, t2) = (t2, t1); swapped = true; }

                    if (t1 > tmin) { tmin = t1; hitAxis = axis; hitMin = !swapped; }
                    if (t2 < tmax) tmax = t2;

                    if (tmin > tmax) return false;
                }
            }

            if (tmax < 0) return false;
            tHit = tmin >= 0 ? tmin : tmax;

            // Compute hit normal
            normal = SN.Vector3.Zero;
            switch (hitAxis)
            {
                case 0: normal = hitMin ? -SN.Vector3.UnitX : SN.Vector3.UnitX; break;
                case 1: normal = hitMin ? -SN.Vector3.UnitY : SN.Vector3.UnitY; break;
                case 2: normal = hitMin ? -SN.Vector3.UnitZ : SN.Vector3.UnitZ; break;
            }
            return true;
        }
    }
}
