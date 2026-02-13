#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Physics;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// CharacterController (motor-only; kinematic):
    ///  - Capsule dims from CapsuleCollider if present (fallback otherwise)
    ///  - Robust grounding w/ stick-to-ground + short grace ("coyote") time
    ///  - Kinematic horiz motion w/ CCD ray + wall slide + AABB unstick
    ///  - Gravity/jump integration (no input here; call Simulate from your player)
    ///  - Uses O(1) heightmap collision for Terrain instead of brute-force mesh tests
    ///  - Pushes Rigidbody objects on contact
    ///  - OnTriggerEnter/Stay/Exit events for trigger volumes
    /// </summary>
    public sealed class CharacterController : Behavior
    {
        // ---------------- Tunables ----------------
        [Persist] public bool UseGravity { get; set; } = true;

        // Default gravity 9.81 Earthlike Gravity
        [Persist] public float Gravity { get; set; } = 9.81f;

        // Desired jump apex height (meters in world units)
        [Persist] public float JumpHeight { get; set; } = 1.2f;

        [Persist] public float StepUpMax { get; set; } = 0.5f;
        [Persist] public float GroundSnapDistance { get; set; } = 0.7f;

        // Extra clearance when unsticking from AABBs (not mesh)
        [Persist] public float WallPush { get; set; } = 0f;

        [Persist] public float MaxSlopeAngleDeg { get; set; } = 55f;

        // Jump grace (after leaving ground)
        [Persist] public float CoyoteTimeSeconds { get; set; } = 0.12f;

        // Capsule fallback if no CapsuleCollider
        [Persist] public float FallbackCapsuleRadius { get; set; } = 0.35f;
        [Persist] public float FallbackCapsuleHeight { get; set; } = 1.8f;

        // AABB unstick filters
        [Persist] public bool UnstickIgnoreHuge { get; set; } = true;
        [Persist] public float UnstickMaxExtent { get; set; } = 5f;
        [Persist] public bool UnstickSkipIfInside { get; set; } = true;

        // ── Push interaction ──
        /// <summary>Force multiplier when pushing Rigidbody objects on contact.</summary>
        [Persist] public float PushForce { get; set; } = 3.0f;

        // ---------------- Runtime (read-only) ----------------
        public bool IsGrounded { get; private set; }
        public SN.Vector3 GroundNormal { get; private set; } = SN.Vector3.UnitY;
        public float VerticalVelocity { get; private set; }  // +up
        public float CapsuleRadius { get; private set; }
        public float CapsuleHalfCylinder { get; private set; }

        float _coyoteTimer = 0f;
        float _lastHitY = float.NegativeInfinity;
        SN.Vector3 _lastHitN = SN.Vector3.UnitY;

        CapsuleCollider? _capsule;

        // ── Trigger tracking ──
        private readonly HashSet<Collider> _currentTriggers = new();
        private readonly HashSet<Collider> _previousTriggers = new();

        /// <summary>Fired when the player enters a trigger volume.</summary>
        public event Action<Collider>? OnTriggerEnter;
        /// <summary>Fired every frame while the player stays inside a trigger volume.</summary>
        public event Action<Collider>? OnTriggerStay;
        /// <summary>Fired when the player exits a trigger volume.</summary>
        public event Action<Collider>? OnTriggerExit;

        public override void Awake() => RefreshCapsule();
        public override void OnEnable() => RefreshCapsule();

        void RefreshCapsule()
        {
            _capsule = GetComponent<CapsuleCollider>();
            float rr = Math.Max(0.0001f, _capsule?.Radius ?? FallbackCapsuleRadius);
            float hh = Math.Max(2f * rr, _capsule?.Height ?? FallbackCapsuleHeight);
            CapsuleRadius = rr;
            CapsuleHalfCylinder = 0.5f * (hh - 2f * rr);
        }

        /// <summary>
        /// Call from FixedUpdate. desiredHorizontalDelta is world XZ (meters) built using fixedDeltaTime.
        /// jump=true to attempt jump this step.
        /// </summary>
        public void Simulate(SN.Vector3 desiredHorizontalDelta, bool jump)
        {
            // Refresh shared physics cache (only rebuilds once per tick)
            PhysicsCache.RefreshFrame();

            var tr = Transform;
            float dt = Math.Max(0.0001f, Time.fixedDeltaTime);

            var pos = new SN.Vector3((float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

            // ---------- Ground probe ----------
            float minGroundY = MathF.Cos(MaxSlopeAngleDeg * (float)(Math.PI / 180.0));
            var rayStart = pos + new SN.Vector3(0, Math.Max(StepUpMax, 0.2f) + 0.002f, 0);

            bool groundHit = RaycastGround(rayStart, new SN.Vector3(0, -1, 0),
                                           GroundSnapDistance + StepUpMax + CapsuleRadius + 0.75f,
                                           out float hitY, out SN.Vector3 hitN);

            float feetY = pos.Y - (CapsuleHalfCylinder + CapsuleRadius);
            float diff = feetY - hitY;
            bool slopeOk = groundHit && hitN.Y >= minGroundY;

            IsGrounded = slopeOk && diff >= -0.02f && diff <= StepUpMax + 0.02f;
            GroundNormal = IsGrounded ? hitN : SN.Vector3.UnitY;

            if (groundHit) { _lastHitY = hitY; _lastHitN = hitN; }

            // Maintain coyote window so jump survives ground flicker
            if (IsGrounded) _coyoteTimer = CoyoteTimeSeconds;
            else _coyoteTimer = Math.Max(0f, _coyoteTimer - dt);

            // ---------- Horizontal (project along ground if grounded) ----------
            var deltaXZ = desiredHorizontalDelta; deltaXZ.Y = 0f;
            if (IsGrounded)
            {
                var n = SN.Vector3.Normalize(GroundNormal);
                deltaXZ -= n * SN.Vector3.Dot(deltaXZ, n);
            }

            float moveLen = new SN.Vector2(deltaXZ.X, deltaXZ.Z).Length();
            SN.Vector3 moveDir = SN.Vector3.Zero;
            if (moveLen > 0f)
            {
                moveDir = new SN.Vector3(deltaXZ.X, 0, deltaXZ.Z) / moveLen;
                var dirXZ = moveDir;
                float stepLen = MathF.Max(0.01f, CapsuleRadius / 4f);
                int steps = Math.Max(1, (int)MathF.Ceiling(moveLen / stepLen));
                var micro = dirXZ * (moveLen / steps);
                for (int i = 0; i < steps; i++)
                {
                    CCD_AdvanceAndSlide(ref pos, micro, CapsuleRadius, CapsuleHalfCylinder);
                    ResolveHorizontalAABB(ref pos, CapsuleHalfCylinder, CapsuleRadius);
                }
            }

            // ── Push Rigidbody objects ──
            PushRigidbodies(pos, moveDir, moveLen, dt);

            // Re-probe quickly after horizontal move (helps big steps)
            rayStart = pos + new SN.Vector3(0, Math.Max(StepUpMax, 0.2f) + 0.002f, 0);
            groundHit = RaycastGround(rayStart, new SN.Vector3(0, -1, 0),
                                      GroundSnapDistance + StepUpMax + CapsuleRadius + 0.75f,
                                      out hitY, out hitN);
            feetY = pos.Y - (CapsuleHalfCylinder + CapsuleRadius);
            diff = feetY - hitY;
            slopeOk = groundHit && hitN.Y >= minGroundY;
            IsGrounded = slopeOk && diff >= -0.02f && diff <= StepUpMax + 0.02f;
            if (groundHit) { _lastHitY = hitY; _lastHitN = hitN; }

            // ---------- Gravity / Jump / Ceiling ----------
            if (UseGravity)
            {
                // Jump allowed when grounded or within coyote time
                if (jump && _coyoteTimer > 0f)
                {
                    float g = Math.Max(0.0001f, Gravity);
                    float h = Math.Max(0.01f, JumpHeight);
                    VerticalVelocity = MathF.Sqrt(2f * g * h); // kinematic takeoff speed

                    // Slight lift so we don't re-snap this frame
                    if (groundHit)
                        pos.Y = Math.Max(pos.Y, hitY + (CapsuleHalfCylinder + CapsuleRadius) + 0.002f);

                    IsGrounded = false;
                    _coyoteTimer = 0f;
                }
                else
                {
                    // Stick to ground when grounded and not already going up
                    if (IsGrounded && VerticalVelocity <= 0f)
                    {
                        if (groundHit)
                            pos.Y = hitY + (CapsuleHalfCylinder + CapsuleRadius);

                        VerticalVelocity = 0f;
                    }
                }

                // Integrate velocity
                VerticalVelocity -= Gravity * dt;

                // Ceiling clamp (if moving up)
                if (VerticalVelocity > 0f)
                {
                    float headY = pos.Y + (CapsuleHalfCylinder + CapsuleRadius);
                    float travel = VerticalVelocity * dt + 0.02f; // small skin
                    if (RaycastCeiling(new SN.Vector3(pos.X, headY, pos.Z), SN.Vector3.UnitY, travel, out float ceilY))
                    {
                        pos.Y = ceilY - (CapsuleHalfCylinder + CapsuleRadius);
                        VerticalVelocity = 0f;
                    }
                }

                // Apply vertical displacement
                pos.Y += VerticalVelocity * dt;

                // Prevent sinking below ground on descent
                if (groundHit)
                {
                    float newFeetY = pos.Y - (CapsuleHalfCylinder + CapsuleRadius);
                    if (newFeetY < hitY - 0.001f)
                    {
                        pos.Y = hitY + (CapsuleHalfCylinder + CapsuleRadius);
                        if (VerticalVelocity < 0f) VerticalVelocity = 0f;
                        IsGrounded = slopeOk;
                    }
                }
            }

            // ---------- write back ----------
            var p3 = tr.Position;
            p3.X = pos.X; p3.Y = pos.Y; p3.Z = pos.Z;
            tr.Position = p3;

            GroundNormal = IsGrounded ? (slopeOk ? hitN : _lastHitN) : SN.Vector3.UnitY;

            // ── Trigger detection (after final position) ──
            CheckTriggers(pos);
        }

        // ---------- External helpers ----------
        public void SetVerticalVelocity(float vy) => VerticalVelocity = vy;
        public void ResetVertical() => VerticalVelocity = 0f;

        // ================= Push Rigidbodies =================

        /// <summary>
        /// After horizontal movement, check if the player's capsule overlaps any
        /// Rigidbody-owning colliders. If so, push the Rigidbody with an impulse.
        /// </summary>
        void PushRigidbodies(SN.Vector3 pos, SN.Vector3 moveDir, float moveLen, float dt)
        {
            if (PushForce <= 0f || moveLen < 1e-6f) return;

            // Build a rough capsule AABB for the player at the current position
            float r = CapsuleRadius + 0.1f; // slight expansion
            float halfH = CapsuleHalfCylinder + CapsuleRadius;
            var playerMin = new SN.Vector3(pos.X - r, pos.Y - halfH, pos.Z - r);
            var playerMax = new SN.Vector3(pos.X + r, pos.Y + halfH, pos.Z + r);

            for (int i = 0; i < Rigidbody.All.Count; i++)
            {
                var rb = Rigidbody.All[i];
                if (rb.IsKinematic || rb.gameObject == gameObject) continue;

                var rbCollider = rb.gameObject?.Behaviors?.OfType<Collider>().FirstOrDefault();
                if (rbCollider == null) continue;

                var rbAABB = rbCollider.GetWorldAABB();
                if (!OverlapsAABB(playerMin, playerMax, rbAABB.Min, rbAABB.Max)) continue;

                // Compute push direction (prefer movement direction, fallback to displacement)
                var rbPos = new SN.Vector3(
                    (float)rb.Transform.Position.X,
                    (float)rb.Transform.Position.Y,
                    (float)rb.Transform.Position.Z);
                var pushDir = moveDir;
                pushDir.Y = 0f;
                if (pushDir.LengthSquared() < 1e-6f)
                {
                    pushDir = rbPos - pos;
                    pushDir.Y = 0f;
                    if (pushDir.LengthSquared() < 1e-6f) continue;
                }
                pushDir = SN.Vector3.Normalize(pushDir);

                // Impulse proportional to PushForce and inversely proportional to mass
                float impulseStrength = PushForce / MathF.Max(0.1f, rb.Mass);
                rb.WakeUp();
                rb.AddImpulse(pushDir * impulseStrength * dt);
            }
        }

        // ================= Trigger System =================

        void CheckTriggers(SN.Vector3 pos)
        {
            _currentTriggers.Clear();

            // Build player AABB
            float r = CapsuleRadius;
            float halfH = CapsuleHalfCylinder + CapsuleRadius;
            var playerMin = new SN.Vector3(pos.X - r, pos.Y - halfH, pos.Z - r);
            var playerMax = new SN.Vector3(pos.X + r, pos.Y + halfH, pos.Z + r);

            for (int i = 0; i < PhysicsCache.TriggerColliders.Count; i++)
            {
                var trigger = PhysicsCache.TriggerColliders[i];
                if (trigger.gameObject == gameObject) continue;

                var trigAABB = trigger.GetWorldAABB();
                if (OverlapsAABB(playerMin, playerMax, trigAABB.Min, trigAABB.Max))
                    _currentTriggers.Add(trigger);
            }

            // Fire events
            foreach (var t in _currentTriggers)
            {
                if (_previousTriggers.Contains(t))
                    OnTriggerStay?.Invoke(t);
                else
                    OnTriggerEnter?.Invoke(t);
            }
            foreach (var t in _previousTriggers)
            {
                if (!_currentTriggers.Contains(t))
                    OnTriggerExit?.Invoke(t);
            }
            _previousTriggers.Clear();
            foreach (var t in _currentTriggers) _previousTriggers.Add(t);
        }

        static bool OverlapsAABB(SN.Vector3 aMin, SN.Vector3 aMax, SN.Vector3 bMin, SN.Vector3 bMax)
            => (aMin.X <= bMax.X && aMax.X >= bMin.X) &&
               (aMin.Y <= bMax.Y && aMax.Y >= bMin.Y) &&
               (aMin.Z <= bMax.Z && aMax.Z >= bMin.Z);

        // ================= Grounding / collision helpers =================

        bool RaycastGround(SN.Vector3 start, SN.Vector3 dir, float maxDist, out float groundY, out SN.Vector3 groundN)
        {
            if (dir.LengthSquared() < 1e-8f) dir = new SN.Vector3(0, -1, 0);
            dir = SN.Vector3.Normalize(dir);
            if (dir.Y >= -1e-5f) dir = new SN.Vector3(dir.X, -MathF.Abs(dir.Y) - 1e-3f, dir.Z);

            // 5-sample ring so we don't "fall through" triangle edges
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

            void ConsiderHeight(float hitY, in SN.Vector3 n, float rayStartY)
            {
                if (hitY > rayStartY + 1e-4f) return; // must be below start
                if (hitY > bestY) { bestY = hitY; bestN = n; anyHit = true; }
            }

            void ConsiderRay(in SN.Vector3 s, float t, in SN.Vector3 n)
            {
                if (t < 0f || t > maxDist) return;
                var p = s + dir * t;
                if (p.Y > s.Y + 1e-4f) return;
                if (p.Y > bestY) { bestY = p.Y; bestN = n; anyHit = true; }
            }

            // ---- O(1) Terrain heightmap collision (from PhysicsCache) ----
            var terrains = PhysicsCache.Terrains;
            for (int ti = 0; ti < terrains.Count; ti++)
            {
                var terrain = terrains[ti];
                for (int r2 = 0; r2 < starts.Length; r2++)
                {
                    if (terrain.SampleHeightWorld(starts[r2].X, starts[r2].Z, out float hY, out SN.Vector3 hN))
                        ConsiderHeight(hY, hN, starts[r2].Y);
                }
            }

            // ---- Non-terrain MeshColliders (buildings, props, etc.) ----
            var meshColliders = PhysicsCache.MeshColliders;
            for (int mi = 0; mi < meshColliders.Count; mi++)
            {
                var mc = meshColliders[mi];
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.Vertices == null || mesh.TriIndices == null) continue;
                    var vtx = mesh.Vertices; var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        var n = SN.Vector3.Cross(b - a, c - a);
                        var len2 = n.LengthSquared(); if (len2 < 1e-12f) continue;
                        n /= MathF.Sqrt(len2);

                        for (int r2 = 0; r2 < starts.Length; r2++)
                            if (RayTri_TwoSided(starts[r2], dir, a, b, c, out float t))
                                ConsiderRay(starts[r2], t, n);
                    }
                }
            }

            // ---- AABB tops (non-mesh colliders) ----
            var nonMeshColliders = PhysicsCache.NonMeshColliders;
            if (dir.Y < -1e-6f)
            {
                for (int ci = 0; ci < nonMeshColliders.Count; ci++)
                {
                    var col = nonMeshColliders[ci];
                    if (col.gameObject == this.gameObject) continue;
                    var aabb = col.GetWorldAABB();
                    for (int r2 = 0; r2 < starts.Length; r2++)
                    {
                        var s = starts[r2];
                        float t = (aabb.Max.Y - s.Y) / dir.Y;
                        if (t >= 0f && t <= maxDist)
                        {
                            var p = s + dir * t;
                            if (p.X >= aabb.Min.X && p.X <= aabb.Max.X &&
                                p.Z >= aabb.Min.Z && p.Z <= aabb.Max.Z)
                                ConsiderRay(s, t, SN.Vector3.UnitY);
                        }
                    }
                }
            }

            groundY = bestY;
            groundN = bestN;
            return anyHit;
        }

        bool RaycastCeiling(SN.Vector3 start, SN.Vector3 dir, float maxDist, out float ceilY)
        {
            if (dir.LengthSquared() < 1e-8f) dir = new SN.Vector3(0, +1, 0);
            dir = SN.Vector3.Normalize(dir);
            if (dir.Y <= 1e-5f) dir = new SN.Vector3(dir.X, MathF.Abs(dir.Y) + 1e-3f, dir.Z);

            var ring = MathF.Max(0.05f, CapsuleRadius * 0.6f);
            var starts = new[]
            {
                start,
                start + new SN.Vector3(+ring, 0, 0),
                start + new SN.Vector3(-ring, 0, 0),
                start + new SN.Vector3(0, 0, +ring),
                start + new SN.Vector3(0, 0, -ring),
            };

            float bestY = float.PositiveInfinity;
            bool anyHit = false;

            void Consider(in SN.Vector3 s, float t)
            {
                if (t < 0f || t > maxDist) return;
                var p = s + dir * t;
                if (p.Y < s.Y - 1e-4f) return;
                if (p.Y < bestY) { bestY = p.Y; anyHit = true; }
            }

            // Non-terrain MeshColliders only (terrain is ground, never ceiling)
            var meshColliders = PhysicsCache.MeshColliders;
            for (int mi = 0; mi < meshColliders.Count; mi++)
            {
                var mc = meshColliders[mi];
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.Vertices == null || mesh.TriIndices == null) continue;
                    var vtx = mesh.Vertices; var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        for (int r2 = 0; r2 < starts.Length; r2++)
                            if (RayTri_TwoSided(starts[r2], dir, a, b, c, out float t))
                                Consider(starts[r2], t);
                    }
                }
            }

            // AABB bottoms (ceilings)
            var nonMeshColliders = PhysicsCache.NonMeshColliders;
            if (dir.Y > 1e-6f)
            {
                for (int ci = 0; ci < nonMeshColliders.Count; ci++)
                {
                    var col = nonMeshColliders[ci];
                    if (col.gameObject == this.gameObject) continue;
                    var aabb = col.GetWorldAABB();
                    for (int r2 = 0; r2 < starts.Length; r2++)
                    {
                        var s = starts[r2];
                        float t = (aabb.Min.Y - s.Y) / dir.Y;
                        if (t >= 0f && t <= maxDist)
                        {
                            var p = s + dir * t;
                            if (p.X >= aabb.Min.X && p.X <= aabb.Max.X &&
                                p.Z >= aabb.Min.Z && p.Z <= aabb.Max.Z)
                                Consider(s, t);
                        }
                    }
                }
            }

            ceilY = bestY;
            return anyHit;
        }

        static bool RayTri_TwoSided(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, out float t)
        {
            const float EPS = 1e-8f;
            var ab = b - a; var ac = c - a;
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

            // ray origin at "waist"
            var originBase = pos + new SN.Vector3(0, 0.5f * (halfCyl + radius), 0);

            for (int iter = 0; iter < 4 && remainLen > 1e-5f; iter++)
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
                        if (into <= 0f) rem -= nH * into; // slide along wall
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
            var nonMeshColliders = PhysicsCache.NonMeshColliders;

            for (int ci = 0; ci < nonMeshColliders.Count; ci++)
            {
                var col = nonMeshColliders[ci];
                if (col.gameObject == this.gameObject) continue;
                var aabb = col.GetWorldAABB();

                // ignore "world hulls"
                if (UnstickIgnoreHuge)
                {
                    var sx = aabb.Max.X - aabb.Min.X;
                    var sy = aabb.Max.Y - aabb.Min.Y;
                    var sz = aabb.Max.Z - aabb.Min.Z;
                    if (sx > UnstickMaxExtent || sy > UnstickMaxExtent || sz > UnstickMaxExtent)
                        continue;
                }

                // skip if fully inside
                if (UnstickSkipIfInside &&
                    pos.X > aabb.Min.X && pos.X < aabb.Max.X &&
                    pos.Y > aabb.Min.Y && pos.Y < aabb.Max.Y &&
                    pos.Z > aabb.Min.Z && pos.Z < aabb.Max.Z)
                {
                    continue;
                }

                // vertical overlap?
                var bodyMinY = pos.Y - (halfCyl + radius);
                var bodyMaxY = pos.Y + (halfCyl + radius);
                if (bodyMaxY < aabb.Min.Y || bodyMinY > aabb.Max.Y) continue;

                // closest pt on AABB in XZ
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

            // Non-terrain MeshColliders only (terrain has no vertical walls)
            var meshColliders = PhysicsCache.MeshColliders;
            for (int mi = 0; mi < meshColliders.Count; mi++)
            {
                var mc = meshColliders[mi];
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh?.Vertices == null || mesh.TriIndices == null) continue;

                    var vtx = mesh.Vertices; var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        var n = SN.Vector3.Cross(b - a, c - a);
                        var len2 = n.LengthSquared(); if (len2 < 1e-12f) continue;
                        n /= MathF.Sqrt(len2);

                        // ignore floors & ceilings here
                        if (MathF.Abs(n.Y) > 0.45f) continue;

                        if (RayTri_TwoSided(start, dir, a, b, c, out float t) && t >= 0f && t <= maxDist)
                        {
                            var p = start + dir * t;
                            if (p.Y >= bandMinY && p.Y <= bandMaxY)
                                if (t < bestT) { bestT = t; bestN = n; any = true; }
                        }
                    }
                }
            }

            // simple planes of AABBs
            var nonMeshColliders = PhysicsCache.NonMeshColliders;
            for (int ci = 0; ci < nonMeshColliders.Count; ci++)
            {
                var c = nonMeshColliders[ci];
                if (c.gameObject == this.gameObject) continue;
                var a2 = c.GetWorldAABB();
                if (a2.Max.Y < bandMinY || a2.Min.Y > bandMaxY) continue;

                TestSide(a2.Min.X, new SN.Vector3(-1, 0, 0));
                TestSide(a2.Max.X, new SN.Vector3(+1, 0, 0));
                TestFront(a2.Min.Z, new SN.Vector3(0, 0, -1));
                TestFront(a2.Max.Z, new SN.Vector3(0, 0, +1));

                void TestSide(float xPlane, SN.Vector3 n)
                {
                    if (MathF.Abs(dir.X) < 1e-6f) return;
                    float t = (xPlane - start.X) / dir.X;
                    if (t < 0f || t > maxDist) return;
                    var p = start + dir * t;
                    if (p.Y >= a2.Min.Y && p.Y <= a2.Max.Y && p.Z >= a2.Min.Z && p.Z <= a2.Max.Z)
                        if (t < bestT) { bestT = t; bestN = n; any = true; }
                }
                void TestFront(float zPlane, SN.Vector3 n)
                {
                    if (MathF.Abs(dir.Z) < 1e-6f) return;
                    float t = (zPlane - start.Z) / dir.Z;
                    if (t < 0f || t > maxDist) return;
                    var p = start + dir * t;
                    if (p.Y >= a2.Min.Y && p.Y <= a2.Max.Y && p.X >= a2.Min.X && p.X <= a2.Max.X)
                        if (t < bestT) { bestT = t; bestN = n; any = true; }
                }
            }

            tHit = bestT; nHit = bestN;
            return any;
        }
    }
}
