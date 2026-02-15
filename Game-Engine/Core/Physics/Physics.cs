#nullable enable
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// <summary>
    /// Static convenience wrapper for physics queries — mirrors the Unity-style API
    /// so game scripts can call Physics.Raycast(), Physics.RaycastAll(), etc.
    /// Delegates to CollisionWorld internally.
    /// </summary>
    public static class Physics
    {
        /// <summary>
        /// Cast a ray and return true if any collider is hit.
        /// </summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDistance = 1000f)
        {
            return CollisionWorld.Raycast(origin, direction, maxDistance, out _);
        }

        /// <summary>
        /// Cast a ray and return hit information for the closest collider.
        /// </summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, out CollisionWorld.RaycastHit hit, float maxDistance = 1000f)
        {
            return CollisionWorld.Raycast(origin, direction, maxDistance, out hit);
        }

        /// <summary>
        /// Cast a ray and return all hits along the ray.
        /// </summary>
        public static List<CollisionWorld.RaycastHit> RaycastAll(SN.Vector3 origin, SN.Vector3 direction, float maxDistance = 1000f)
        {
            return CollisionWorld.RaycastAll(origin, direction, maxDistance);
        }

        /// <summary>
        /// Find all colliders within a sphere centered at <paramref name="center"/>.
        /// </summary>
        public static List<Component.Collider> OverlapSphere(SN.Vector3 center, float radius)
        {
            return CollisionWorld.OverlapSphere(center, radius);
        }

        /// <summary>Gravity vector used by the default physics simulation.</summary>
        public static SN.Vector3 Gravity { get; set; } = new SN.Vector3(0, -9.81f, 0);
    }
}
