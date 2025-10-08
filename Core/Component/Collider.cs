using System;
using SN = System.Numerics;
using Game_Engine.Core.Physics;

namespace Game_Engine.Core.Component
{
    /// Common base for colliders (Box, Mesh…). Handles registration & simple AABB.
    public abstract class Collider : Behavior
    {
        [Persist] public bool IsTrigger { get; set; } = false;

        /// World-space axis-aligned bounding box.
        public struct AABB { public SN.Vector3 Min, Max; public AABB(SN.Vector3 min, SN.Vector3 max) { Min = min; Max = max; } }
        public abstract AABB GetWorldAABB();

        protected static void Encapsulate(ref SN.Vector3 min, ref SN.Vector3 max, SN.Vector3 p)
        {
            min = new SN.Vector3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new SN.Vector3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        public override void OnEnable() { base.OnEnable(); CollisionWorld.Register(this); }
        public override void OnDisable() { CollisionWorld.Unregister(this); base.OnDisable(); }

        // Convenience: quick overlap probe against the world.
        public bool AnyOverlap(out Collider other) => CollisionWorld.AnyOverlap(this, out other);
    }
}
