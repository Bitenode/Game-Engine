using System;
using SN = System.Numerics;
using CoreVec3 = Game_Engine.Core.Vector3;

namespace Game_Engine.Core.Component
{
    /// Axis-aligned box in LOCAL space, transformed by the GameObject transform.
    [ComponentCategory("Physics")]
    public sealed class BoxCollider : Collider
    {
        [Persist] public CoreVec3 Center { get; set; } = new CoreVec3(0, 0, 0);
        [Persist] public CoreVec3 Size { get; set; } = new CoreVec3(1, 1, 1);

        // 8 corners of the local OBB
        void GetLocalCorners(SN.Vector3[] corners)
        {
            var c = new SN.Vector3((float)Center.X, (float)Center.Y, (float)Center.Z);
            var s = new SN.Vector3((float)Math.Max(1e-6, Size.X),
                                   (float)Math.Max(1e-6, Size.Y),
                                   (float)Math.Max(1e-6, Size.Z));
            var e = s * 0.5f;

            int i = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        corners[i++] = c + new SN.Vector3(e.X * x, e.Y * y, e.Z * z);
        }

        public override AABB GetWorldAABB()
        {
            var W = SceneGraphUtil.AccumulateWorld(gameObject);
            var lc = new SN.Vector3[8];
            GetLocalCorners(lc);

            SN.Vector3 min = new SN.Vector3(float.MaxValue);
            SN.Vector3 max = new SN.Vector3(float.MinValue);

            for (int i = 0; i < 8; i++)
            {
                var p = SN.Vector3.Transform(lc[i], W);
                Encapsulate(ref min, ref max, p);
            }
            return new AABB(min, max);
        }
    }
}
