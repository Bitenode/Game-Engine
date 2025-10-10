using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// CharacterController (Motor-only):
    /// - Finds a CapsuleCollider (preferred) or falls back to defaults
    /// - Provides grounding check + gravity integration
    /// - Kinematic horizontal motion with CCD, wall slide, AABB unstick
    /// - No input, no camera. Call Simulate(...) from your player script.
    /// 
    /// Usage (from another Behavior):
    ///   var motor = GetComponent<CharacterController>();
    ///   var wish = (right * mx + fwd * mz) * (speed * Time.deltaTime);
    ///   motor.Simulate(wish, jumpPressed);
    ///   // read motor.IsGrounded, motor.VerticalVelocity, etc.
    /// </summary>
    public sealed class CharacterController : Behavior
    {
        // ---------- Tunables (motor only) ----------
        [Persist] public bool UseGravity { get; set; } = true;
        [Persist] public float Gravity { get; set; } = 9.81f;

        [Persist] public float StepUpMax { get; set; } = 0.5f;        // max auto step height
        [Persist] public float GroundSnapDistance { get; set; } = 0.7f;
        [Persist] public float WallPush { get; set; } = 0f;        // extra unstick clearance
        [Persist] public float MaxSlopeAngleDeg { get; set; } = 55f;  // slopes steeper than this are not "ground"

        // Capsule fallback if no CapsuleCollider on this GO
        [Persist] public float FallbackCapsuleRadius { get; set; } = 0.35f;
        [Persist] public float FallbackCapsuleHeight { get; set; } = 1.8f;

        [Persist] public bool UnstickIgnoreHuge = true;
        [Persist] public float UnstickMaxExtent = 25f; // meters per axis
        [Persist] public bool UnstickSkipIfInside = true;

        // ---------- Runtime (read-only to others) ----------
        public bool IsGrounded { get; private set; }
        public SN.Vector3 GroundNormal { get; private set; } = SN.Vector3.UnitY;
        public float VerticalVelocity { get; private set; }  // +up
        public float CapsuleRadius { get; private set; }
        public float CapsuleHalfCylinder { get; private set; }

        CapsuleCollider _capsule;

        public override void Awake()
        {
            _capsule = GetComponent<CapsuleCollider>();
            CacheCapsuleDims();
        }

        public override void OnEnable()
        {
            _capsule = GetComponent<CapsuleCollider>();
            CacheCapsuleDims();
        }

        void CacheCapsuleDims()
        {
            if (_capsule != null)
            {
                var rr = Math.Max(0.0001f, _capsule.Radius);
                var hh = Math.Max(2f * rr, _capsule.Height);
                CapsuleRadius = rr;
                CapsuleHalfCylinder = 0.5f * (hh - 2f * rr);
            }
            else
            {
                var rr = Math.Max(0.0001f, FallbackCapsuleRadius);
                var hh = Math.Max(2f * rr, FallbackCapsuleHeight);
                CapsuleRadius = rr;
                CapsuleHalfCylinder = 0.5f * (hh - 2f * rr);
            }
        }

        /// <summary>
        /// Main entry point:
        /// desiredHorizontalDelta: world-space XZ motion for this frame (meters), Y will be ignored.
        /// jump: set true to attempt a jump this frame (only works when grounded).
        /// </summary>
        public void Simulate(SN.Vector3 desiredHorizontalDelta, bool jump)
        {
            var tr = Transform;
            var dt = Math.Max(0.0001f, Time.deltaTime);

            // current position
            var pos = new SN.Vector3((float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

            // ---- Grounding ----
            var minGroundY = MathF.Cos(MaxSlopeAngleDeg * (float)(Math.PI / 180.0)); // >= this Y counts as ground
            var rayStart = pos + new SN.Vector3(0, Math.Max(StepUpMax, 0.2f) + 0.001f, 0);
            var rayDir = new SN.Vector3(0, -1, 0);

            bool groundHit = RaycastGround(rayStart, rayDir, Math.Max(GroundSnapDistance * 2f, 2f),
                                           out float hitY, out SN.Vector3 hitN);

            // feet altitude
            float feetY = pos.Y - (CapsuleHalfCylinder + CapsuleRadius);
            float diff = feetY - hitY;
            IsGrounded = groundHit && diff >= -0.02f && diff <= StepUpMax + 0.02f && hitN.Y >= minGroundY;
            GroundNormal = IsGrounded ? hitN : SN.Vector3.UnitY;

            // ---- Horizontal (XZ) move; project onto ground if grounded ----
            var deltaXZ = desiredHorizontalDelta; deltaXZ.Y = 0f;
            if (IsGrounded)
            {
                var n = SN.Vector3.Normalize(GroundNormal);
                deltaXZ -= n * SN.Vector3.Dot(deltaXZ, n); // remove normal component to avoid "climbing" up
            }

            // conservative micro-steps + slide
            float moveLen = new SN.Vector2(deltaXZ.X, deltaXZ.Z).Length();
            if (moveLen > 0f)
            {
                var dirXZ = new SN.Vector3(deltaXZ.X, 0, deltaXZ.Z) / moveLen;
                float stepLen = MathF.Max(0.01f, CapsuleRadius / 4f);
                int steps = Math.Max(1, (int)MathF.Ceiling(moveLen / stepLen));
                var micro = dirXZ * (moveLen / steps);
                for (int i = 0; i < steps; i++)
                {
                    CCD_AdvanceAndSlide(ref pos, micro, CapsuleRadius, CapsuleHalfCylinder);
                    ResolveHorizontalAABB(ref pos, CapsuleHalfCylinder, CapsuleRadius);
                }
            }

            // ---- Gravity / Jump ----
            if (UseGravity)
            {
                if (IsGrounded && VerticalVelocity <= 0f)
                {
                    // snap to ground, and allow jump
                    pos.Y = hitY + (CapsuleHalfCylinder + CapsuleRadius);
                    VerticalVelocity = 0f;
                    if (jump) VerticalVelocity = _capsule != null ? Math.Max(0.1f, _capsule.Height * 0.55f) : 5.5f; // sensible default
                }

                // integrate
                VerticalVelocity -= Gravity * dt;
                pos.Y += VerticalVelocity * dt;

                // prevent pushing through ground
                if (groundHit)
                {
                    var newFeetY = pos.Y - (CapsuleHalfCylinder + CapsuleRadius);
                    if (newFeetY < hitY - 0.001f)
                    {
                        pos.Y = hitY + (CapsuleHalfCylinder + CapsuleRadius);
                        if (VerticalVelocity < 0f) VerticalVelocity = 0f;
                    }
                }
            }

            // write back to Transform
            var p3 = tr.Position;
            p3.X = pos.X; p3.Y = pos.Y; p3.Z = pos.Z;
            tr.Position = p3;
        }

        /// <summary>Instantly set vertical velocity (e.g., external jump tuning).</summary>
        public void SetVerticalVelocity(float vy) => VerticalVelocity = vy;

        /// <summary>Zero all vertical motion (e.g., when teleporting or landing hard).</summary>
        public void ResetVertical() => VerticalVelocity = 0f;

        // ---------- Collision helpers (unchanged core math, trimmed to motor use) ----------

        bool RaycastGround(SN.Vector3 start, SN.Vector3 dir, float maxDist, out float groundY, out SN.Vector3 groundN)
        {
            if (maxDist < 5f) maxDist = 500f;

            if (dir.LengthSquared() < 1e-8f) dir = new SN.Vector3(0, -1, 0);
            dir = SN.Vector3.Normalize(dir);
            if (dir.Y >= -1e-5f) dir = new SN.Vector3(dir.X, -MathF.Abs(dir.Y) - 1e-3f, dir.Z);

            var ring = MathF.Max(0.05f, CapsuleRadius * 0.6f);
            var starts = new[]
            {
                start,
                start + new SN.Vector3(+ring, 0, 0),
                start + new SN.Vector3(-ring, 0, 0),
                start + new SN.Vector3(0, 0, +ring),
                start + new SN.Vector3(0, 0, -ring),
            };

            float bestY = float.NegativeInfinity;
            SN.Vector3 bestN = SN.Vector3.UnitY;
            bool anyHit = false;

            void Consider(in SN.Vector3 s, float t, in SN.Vector3 n)
            {
                if (t < 0f || t > maxDist) return;
                var p = s + dir * t;
                if (p.Y > s.Y + 1e-4f) return; // must be below
                if (p.Y > bestY) { bestY = p.Y; bestN = n; anyHit = true; }
            }

            // Triangles from MeshColliders
            foreach (var mc in SceneQuery.FindBehaviors<MeshCollider>().Where(m => m.Enabled && !m.IsTrigger))
            {
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.Vertices == null || mesh.TriIndices == null) continue;

                    var vtx = mesh.Vertices;
                    var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        var n = SN.Vector3.Cross(b - a, c - a);
                        var len2 = n.LengthSquared();
                        if (len2 < 1e-12f) continue;
                        n /= MathF.Sqrt(len2);

                        for (int r = 0; r < starts.Length; r++)
                        {
                            if (RayTri_TwoSided(starts[r], dir, a, b, c, out float t))
                                Consider(starts[r], t, n);
                        }
                    }
                }
            }

            // AABB tops from other colliders
            var cols = SceneQuery.FindBehaviors<Collider>()
                                 .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject);

            if (dir.Y < -1e-6f)
            {
                foreach (var col in cols)
                {
                    if (col is MeshCollider) continue;
                    var aabb = col.GetWorldAABB();

                    for (int r = 0; r < starts.Length; r++)
                    {
                        var s = starts[r];
                        float t = (aabb.Max.Y - s.Y) / dir.Y; // dir.Y < 0
                        if (t >= 0f && t <= maxDist)
                        {
                            var p = s + dir * t;
                            if (p.X >= aabb.Min.X && p.X <= aabb.Max.X &&
                                p.Z >= aabb.Min.Z && p.Z <= aabb.Max.Z)
                            {
                                Consider(s, t, SN.Vector3.UnitY);
                            }
                        }
                    }
                }
            }

            groundY = bestY;
            groundN = bestN;
            return anyHit;
        }

        static bool RayTri_TwoSided(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, out float t)
        {
            const float EPS = 1e-8f;
            var ab = b - a;
            var ac = c - a;

            var pvec = SN.Vector3.Cross(rd, ac);
            float det = SN.Vector3.Dot(ab, pvec);
            if (MathF.Abs(det) < EPS) { t = 0f; return false; }

            float invDet = 1f / det;
            var tvec = ro - a;
            float u = SN.Vector3.Dot(tvec, pvec) * invDet; if (u < 0f || u > 1f) { t = 0f; return false; }
            var qvec = SN.Vector3.Cross(tvec, ab);
            float v = SN.Vector3.Dot(rd, qvec) * invDet; if (v < 0f || u + v > 1f) { t = 0f; return false; }

            t = SN.Vector3.Dot(ac, qvec) * invDet;
            return t >= 0f;
        }

        void CCD_AdvanceAndSlide(ref SN.Vector3 pos, SN.Vector3 step, float radius, float halfCyl)
        {
            float len = new SN.Vector3(step.X, 0, step.Z).Length();
            if (len < 1e-8f) { pos += step; return; }

            var dir = new SN.Vector3(step.X, 0, step.Z) / len;
            float skin = MathF.Max(0.01f, radius * 0.2f);
            float remainLen = len;

            // ray origin at “waist”
            var originBase = pos + new SN.Vector3(0, 0.5f * (halfCyl + radius), 0);

            for (int iter = 0; iter < 2 && remainLen > 1e-5f; iter++)
            {
                float probe = remainLen + radius + skin;

                if (RaycastWallForward(originBase, dir, probe, out float tHit, out SN.Vector3 nHit))
                {
                    float advance = MathF.Max(0f, tHit - (radius + skin));

                    if (advance > 0f)
                    {
                        var adv = dir * MathF.Min(advance, remainLen);
                        pos += adv;
                        originBase += adv;
                        remainLen -= adv.Length();
                    }

                    if (remainLen <= 1e-5f)
                        break;

                    var nH = new SN.Vector3(nHit.X, 0, nHit.Z);
                    float nLen = nH.Length();
                    if (nLen > 1e-6f)
                    {
                        nH /= nLen;
                        var rem = dir * remainLen;
                        float into = SN.Vector3.Dot(rem, nH);
                        if (into <= 0f) rem -= nH * into; // slide
                        remainLen = new SN.Vector3(rem.X, 0, rem.Z).Length();
                        if (remainLen > 1e-6f) dir = new SN.Vector3(rem.X, 0, rem.Z) / remainLen;
                        else break;
                    }
                    else break;
                }
                else
                {
                    var adv = dir * remainLen;
                    pos += adv;
                    remainLen = 0f;
                    break;
                }
            }

            if (remainLen > 1e-6f)
                pos += dir * remainLen;
        }

        void ResolveHorizontalAABB(ref SN.Vector3 pos, float halfCyl, float radius)
        {
            const float EPS = 1e-5f;
            float pad = Math.Max(0f, WallPush);

            var cols = SceneQuery.FindBehaviors<Collider>()
                .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject && c is not MeshCollider);

            foreach (var col in cols)
            {
                var aabb = col.GetWorldAABB();

                //  skip "world-sized" colliders (environment shells)
                if (UnstickIgnoreHuge)
                {
                    var sx = aabb.Max.X - aabb.Min.X;
                    var sy = aabb.Max.Y - aabb.Min.Y;
                    var sz = aabb.Max.Z - aabb.Min.Z;
                    if (sx > UnstickMaxExtent || sy > UnstickMaxExtent || sz > UnstickMaxExtent)
                        continue;
                }

                //  if we're fully inside the box (typical of an interior container), skip
                if (UnstickSkipIfInside &&
                    pos.X > aabb.Min.X && pos.X < aabb.Max.X &&
                    pos.Y > aabb.Min.Y && pos.Y < aabb.Max.Y &&
                    pos.Z > aabb.Min.Z && pos.Z < aabb.Max.Z)
                {
                    continue;
                }

                // vertical overlap? (use capsule full extent)
                var bodyMinY = pos.Y - (halfCyl + radius);
                var bodyMaxY = pos.Y + (halfCyl + radius);
                if (bodyMaxY < aabb.Min.Y || bodyMinY > aabb.Max.Y) continue;

                // Closest point on AABB to capsule center (XZ)
                float cx = Math.Clamp(pos.X, aabb.Min.X, aabb.Max.X);
                float cz = Math.Clamp(pos.Z, aabb.Min.Z, aabb.Max.Z);

                float dx = pos.X - cx;
                float dz = pos.Z - cz;
                float d2 = dx * dx + dz * dz;

                float rr = (radius + pad);
                if (d2 < rr * rr - 1e-6f)
                {
                    float d = (float)Math.Sqrt(Math.Max(d2, 1e-12f));
                    if (d < EPS)
                    {
                        // On an edge or corner: push along least-penetration axis
                        float dl = Math.Abs(pos.X - aabb.Min.X);
                        float dr = Math.Abs(aabb.Max.X - pos.X);
                        float dn = Math.Abs(pos.Z - aabb.Min.Z);
                        float df = Math.Abs(aabb.Max.Z - pos.Z);
                        if (Math.Min(dl, dr) < Math.Min(dn, df))
                            pos.X += (dl < dr ? -(rr) : +(rr));
                        else
                            pos.Z += (dn < df ? -(rr) : +(rr));
                    }
                    else
                    {
                        float push = rr - d;
                        pos.X += (dx / d) * push;
                        pos.Z += (dz / d) * push;
                    }
                }
            }
        }


        bool RaycastWallForward(SN.Vector3 start, SN.Vector3 dir, float maxDist, out float tHit, out SN.Vector3 nHit)
        {
            float bestT = float.PositiveInfinity;
            SN.Vector3 bestN = SN.Vector3.UnitX;
            bool any = false;

            float bandMinY = start.Y - (0.5f * (CapsuleHalfCylinder + CapsuleRadius));
            float bandMaxY = start.Y + (0.5f * (CapsuleHalfCylinder + CapsuleRadius));

            foreach (var mc in SceneQuery.FindBehaviors<MeshCollider>().Where(m => m.Enabled && !m.IsTrigger))
            {
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.Vertices == null || mesh.TriIndices == null) continue;

                    var vtx = mesh.Vertices;
                    var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        var n = SN.Vector3.Cross(b - a, c - a);
                        var len2 = n.LengthSquared();
                        if (len2 < 1e-12f) continue;
                        n /= MathF.Sqrt(len2);

                        // ignore floors/ceilings
                        if (MathF.Abs(n.Y) > 0.45f) continue;

                        if (RayTri_TwoSided(start, dir, a, b, c, out float t) && t >= 0f && t <= maxDist)
                        {
                            var p = start + dir * t;
                            if (p.Y >= bandMinY && p.Y <= bandMaxY)
                            {
                                if (t < bestT) { bestT = t; bestN = n; any = true; }
                            }
                        }
                    }
                }
            }

            foreach (var c in SceneQuery.FindBehaviors<Collider>()
                     .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject && c is not MeshCollider))
            {
                var a = c.GetWorldAABB();
                if (a.Max.Y < bandMinY || a.Min.Y > bandMaxY) continue;

                TestSide(a.Min.X, new SN.Vector3(-1, 0, 0)); // left
                TestSide(a.Max.X, new SN.Vector3(+1, 0, 0)); // right
                TestFront(a.Min.Z, new SN.Vector3(0, 0, -1)); // near
                TestFront(a.Max.Z, new SN.Vector3(0, 0, +1)); // far

                void TestSide(float xPlane, SN.Vector3 n)
                {
                    if (MathF.Abs(dir.X) < 1e-6f) return;
                    float t = (xPlane - start.X) / dir.X;
                    if (t < 0f || t > maxDist) return;
                    var p = start + dir * t;
                    if (p.Y >= a.Min.Y && p.Y <= a.Max.Y && p.Z >= a.Min.Z && p.Z <= a.Max.Z)
                        if (t < bestT) { bestT = t; bestN = n; any = true; }
                }
                void TestFront(float zPlane, SN.Vector3 n)
                {
                    if (MathF.Abs(dir.Z) < 1e-6f) return;
                    float t = (zPlane - start.Z) / dir.Z;
                    if (t < 0f || t > maxDist) return;
                    var p = start + dir * t;
                    if (p.Y >= a.Min.Y && p.Y <= a.Max.Y && p.X >= a.Min.X && p.X <= a.Max.X)
                        if (t < bestT) { bestT = t; bestN = n; any = true; }
                }
            }

            tHit = bestT; nHit = bestN;
            return any;
        }
    }
}
