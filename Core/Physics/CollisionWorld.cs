
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
    }
}
