using System;
using SN = System.Numerics;
using CoreVec3 = Game_Engine.Core.Vector3;

namespace Game_Engine.Core.Component
{
    /// A capsule defined in LOCAL space:
    /// - Direction: which local axis the capsule runs along
    /// - Height: full height including the hemispheres (>= 2*Radius; clamped if smaller)
    /// - Radius: radius of the hemispheres/cylinder
    /// The world AABB accounts for non-uniform scale via row-length expansion.
    public sealed class CapsuleCollider : Collider
    {
        public enum Axis { X, Y, Z }

        [Persist] public CoreVec3 Center { get; set; } = new CoreVec3(0, 1, 0);
        [Persist] public float Height { get; set; } = 2.0f;
        [Persist] public float Radius { get; set; } = 0.4f;
        [Persist] public Axis Direction { get; set; } = Axis.Y;

        /// Returns the two local-space sphere centers (ends) and the clamped radius/height.
        void GetLocalCapsule(out SN.Vector3 a, out SN.Vector3 b, out float r)
        {
            r = Math.Max(0.0001f, Radius);
            var h = Math.Max(2f * r, Height);               // Clamp so cyl part is non-negative
            var halfCyl = 0.5f * (h - 2f * r);              // distance from center to each sphere center along axis

            var c = new SN.Vector3((float)Center.X, (float)Center.Y, (float)Center.Z);
            SN.Vector3 axis;
            switch (Direction)
            {
                case Axis.X: axis = new SN.Vector3(1, 0, 0); break;
                case Axis.Z: axis = new SN.Vector3(0, 0, 1); break;
                default: axis = new SN.Vector3(0, 1, 0); break; // Y
            }

            a = c + axis * halfCyl;
            b = c - axis * halfCyl;
            // (When Height == 2r, a == b and you get a pure sphere.)
        }

        public override AABB GetWorldAABB()
        {
            // Build world matrix from this GO's transform
            var W = TransformUtil.WorldFromTransform(gameObject.Transform);

            // Local capsule ends
            GetLocalCapsule(out var la, out var lb, out var r);

            // Transform ends to world
            var wa = SN.Vector3.Transform(la, W);
            var wb = SN.Vector3.Transform(lb, W);

            // For a sphere transformed by linear part of W (3x3), the axis-aligned extents
            // per world axis are r * ||row_i(W3x3)||. Use that to expand both endpoints.
            var ex = new SN.Vector3(W.M11, W.M12, W.M13);
            var ey = new SN.Vector3(W.M21, W.M22, W.M23);
            var ez = new SN.Vector3(W.M31, W.M32, W.M33);

            float rx = r * ex.Length();   // extent along world X from sphere radius under transform
            float ry = r * ey.Length();   // extent along world Y
            float rz = r * ez.Length();   // extent along world Z

            // Start with min/max of the two centers
            SN.Vector3 min = new SN.Vector3(
                Math.Min(wa.X, wb.X),
                Math.Min(wa.Y, wb.Y),
                Math.Min(wa.Z, wb.Z));
            SN.Vector3 max = new SN.Vector3(
                Math.Max(wa.X, wb.X),
                Math.Max(wa.Y, wb.Y),
                Math.Max(wa.Z, wb.Z));

            // Expand by the sphere extents
            min = new SN.Vector3(min.X - rx, min.Y - ry, min.Z - rz);
            max = new SN.Vector3(max.X + rx, max.Y + ry, max.Z + rz);

            return new AABB(min, max);
        }
    }
}
