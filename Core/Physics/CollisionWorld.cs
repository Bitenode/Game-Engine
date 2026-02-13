
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// Central registry + super-simple broadphase queries (AABB only for now).
    public static class CollisionWorld
    {
        static readonly List<Component.Collider> _colliders = new List<Component.Collider>();

        internal static void Register(Component.Collider c)
        {
            if (c != null && !_colliders.Contains(c)) _colliders.Add(c);
        }

        internal static void Unregister(Component.Collider c)
        {
            if (c != null) _colliders.Remove(c);
        }

        /// Return all colliders whose world AABB overlaps the given AABB.
        public static IEnumerable<Component.Collider> QueryAABB(SN.Vector3 min, SN.Vector3 max)
        {
            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;
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
        /// Cast a ray through the physics world. Returns true if any collider was hit.
        /// Tests against all AABB colliders in the scene.
        /// </summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDist, out RaycastHit hit, int layerMask = -1)
        {
            hit = default;
            direction = SN.Vector3.Normalize(direction);
            float bestT = maxDist;
            bool found = false;

            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled || c.IsTrigger) continue;

                var aabb = c.GetWorldAABB();
                if (RayAABB(origin, direction, aabb.Min, aabb.Max, out float t, out SN.Vector3 normal) && t < bestT && t >= 0f)
                {
                    bestT = t;
                    hit = new RaycastHit
                    {
                        Point = origin + direction * t,
                        Normal = normal,
                        Distance = t,
                        Collider = c
                    };
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// Cast a ray and return ALL hits (unsorted). Useful for pierce queries.
        /// </summary>
        public static List<RaycastHit> RaycastAll(SN.Vector3 origin, SN.Vector3 direction, float maxDist)
        {
            direction = SN.Vector3.Normalize(direction);
            var results = new List<RaycastHit>();

            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;

                var aabb = c.GetWorldAABB();
                if (RayAABB(origin, direction, aabb.Min, aabb.Max, out float t, out SN.Vector3 normal) && t <= maxDist && t >= 0f)
                {
                    results.Add(new RaycastHit
                    {
                        Point = origin + direction * t,
                        Normal = normal,
                        Distance = t,
                        Collider = c
                    });
                }
            }
            return results;
        }

        /// <summary>Sphere overlap query — returns all colliders within a sphere.</summary>
        public static List<Component.Collider> OverlapSphere(SN.Vector3 center, float radius)
        {
            var results = new List<Component.Collider>();
            for (int i = 0; i < _colliders.Count; i++)
            {
                var c = _colliders[i];
                if (!c.IsActiveAndEnabled) continue;

                var aabb = c.GetWorldAABB();
                // Expand AABB by radius for sphere test
                var expandedMin = aabb.Min - new SN.Vector3(radius);
                var expandedMax = aabb.Max + new SN.Vector3(radius);

                if (center.X >= expandedMin.X && center.X <= expandedMax.X &&
                    center.Y >= expandedMin.Y && center.Y <= expandedMax.Y &&
                    center.Z >= expandedMin.Z && center.Z <= expandedMax.Z)
                {
                    results.Add(c);
                }
            }
            return results;
        }

        /// <summary>Ray vs AABB intersection (slab method).</summary>
        static bool RayAABB(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 min, SN.Vector3 max,
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
