// /Core/Component/MeshCollider.cs
using System;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    public sealed class MeshCollider : Collider
    {
        //  use an explicit mesh set here
        [Persist] public Mesh Mesh { get; set; }

        //  if true and Mesh is null, look for a MeshFilter on this GO
        [Persist] public bool UseMeshFromFilter { get; set; } = true;

        // For future: convex hull vs triangle soup. (No effect yet.)
        [Persist] public bool Convex { get; set; } = false;

        Mesh ResolveMesh()
        {
            if (Mesh != null) return Mesh;
            if (UseMeshFromFilter && gameObject != null)
            {
                var mf = gameObject.Behaviors.OfType<MeshFilter>().FirstOrDefault(b => b.Enabled);
                if (mf != null) return mf.Mesh;
            }
            return null;
        }

        public override AABB GetWorldAABB()
        {
            var m = ResolveMesh();
            if (m == null || m.Vertices == null || m.Vertices.Length == 0)
            {
                // Fallback to a "point" at object origin
                var W0 = TransformUtil.WorldFromTransform(gameObject.Transform);
                var p = SN.Vector3.Transform(SN.Vector3.Zero, W0);
                return new AABB(p, p);
            }

            var W = TransformUtil.WorldFromTransform(gameObject.Transform);

            SN.Vector3 min = new SN.Vector3(float.MaxValue);
            SN.Vector3 max = new SN.Vector3(float.MinValue);

            var vtx = m.Vertices;
            for (int i = 0; i < vtx.Length; i++)
            {
                var p = SN.Vector3.Transform(vtx[i], W);
                Encapsulate(ref min, ref max, p);
            }

            return new AABB(min, max);
        }
    }
}
