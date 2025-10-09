using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Kinematic character controller:
    /// - WASD-style XZ move with sprint
    /// - Yaw/pitch look
    /// - First/Third person camera drive
    /// - Grounding by raycast against MeshCollider triangles (exact) + AABB fallback
    /// - Simple gravity + jump
    /// - Horizontal unstick/slide against AABBs so you don't get glued to walls
    /// </summary>
    public sealed class CharacterController : Behavior
    {
        // ---------- Tunables ----------
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public float MoveSpeed { get; set; } = 4f;
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;
        [Persist] public float LookSensitivity { get; set; } = 90f;
        [Persist] public bool UseGravity { get; set; } = true;
        [Persist] public float Gravity { get; set; } = 9.81f;
        [Persist] public float JumpSpeed { get; set; } = 5.5f;
        [Persist] public float FirstPersonHeight { get; set; } = 1.7f;
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5);
        [Persist] public float CameraFollowLerp { get; set; } = 12f;

        // Collider sampling
        [Persist] public float StepUpMax { get; set; } = 0.5f;   // how high a step we can auto-climb
        [Persist] public float GroundSnapDistance { get; set; } = 0.7f; // how far we search down to find ground
        [Persist] public float WallPush { get; set; } = 0.15f;   // how far to push out of walls horizontally

        // If you have a CapsuleCollider on this GO, we’ll use its dimensions
        CapsuleCollider _capsule;

        // ---------- Runtime ----------
        float _inMoveX, _inMoveZ, _inLookX, _inLookY;
        bool _inJump, _inSprint;

        float _yawDeg, _pitchDeg;
        float _vy;         // vertical velocity
        bool _grounded;    // sticky ground

        Camera _cam;
        Transform _camTr;

        // --------- Input API ----------
        public void SetInput(float moveX, float moveZ, float lookX, float lookY, bool jump, bool sprint)
        {
            _inMoveX = Math.Clamp(moveX, -1f, 1f);
            _inMoveZ = Math.Clamp(moveZ, -1f, 1f);
            _inLookX = lookX;
            _inLookY = lookY;
            _inJump = jump;
            _inSprint = sprint;
        }

        public override void Awake()
        {
            var tr = Transform;
            _yawDeg = (float)tr.Rotation.Y;
            _pitchDeg = (float)tr.Rotation.X;

            _capsule = GetComponent<CapsuleCollider>();
            ResolveCamera();
        }

        public override void OnEnable() => ResolveCamera();

        void ResolveCamera()
        {
            _cam = GetComponent<Camera>();
            _camTr = _cam?.Transform;

            if (_cam == null && gameObject != null)
            {
                foreach (var c in gameObject.Children)
                {
                    var cc = c.Behaviors?.OfType<Camera>().FirstOrDefault();
                    if (cc != null && cc.Enabled) { _cam = cc; _camTr = cc.Transform; break; }
                }
            }
            if (_cam == null)
            {
                var cams = SceneQuery.FindBehaviors<Camera>();
                _cam = cams.FirstOrDefault(c => c.IsMain) ?? cams.FirstOrDefault();
                _camTr = _cam?.Transform;
            }
        }

        public override void Update()
        {
            var dt = Math.Max(0.0001f, Time.deltaTime);

            // ----- Look (yaw/pitch) -----
            var lookScale = LookSensitivity * dt;
            _yawDeg += _inLookX * lookScale;
            _pitchDeg = Math.Clamp(_pitchDeg - _inLookY * lookScale, -89f, 89f);
            _yawDeg = Normalize180(_yawDeg);

            var tr = Transform;

            // apply yaw to body (Y axis only)
            var r = tr.Rotation;
            r.Y = _yawDeg;
            tr.Rotation = r;

            // yaw-only forward/right
            var yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3((float)Math.Sin(yawRad), 0f, -(float)Math.Cos(yawRad));
            var right = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));

            // ----- Move input -----
            var wish = right * _inMoveX + fwd * _inMoveZ;
            if (wish.LengthSquared() > 1e-6f) wish = SN.Vector3.Normalize(wish);
            var speed = MoveSpeed * (_inSprint ? SprintMultiplier : 1f);
            var deltaXZ = wish * (speed * dt);

            // ----- Grounding (raycast down) -----
            var pos = new SN.Vector3((float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

            // Capsule size
            float rad, halfCyl;
            GetCapsule(out rad, out halfCyl);

            // Ground ray starts a bit above feet
            var rayStart = pos + new SN.Vector3(0, Math.Max(StepUpMax, 0.2f) + 0.001f, 0);
            var rayDir = new SN.Vector3(0, -1, 0);
            var rayLen = 500f;

            var groundHit = RaycastGround(rayStart, rayDir, rayLen, out var hitY, out var hitN);

            // grounded?
            var feetY = pos.Y - (halfCyl + rad);
            var diff = feetY - hitY;
            _grounded = groundHit && diff >= -0.02f && diff <= StepUpMax + 0.02f && hitN.Y > 0.5f;

            // project horizontal wish onto ground when grounded (no skating)
            if (_grounded)
            {
                var n = SN.Vector3.Normalize(hitN);
                deltaXZ -= n * SN.Vector3.Dot(deltaXZ, n);   // remove into-ground component
            }

            // Jump / gravity
            if (UseGravity)
            {
                if (_grounded && _vy <= 0f)
                {
                    pos.Y = hitY + (halfCyl + rad);
                    _vy = 0f;
                    if (_inJump) _vy = JumpSpeed;
                }

                if (!_grounded || _vy > 0f)
                {
                    _vy -= Gravity * dt;
                    pos.Y += _vy * dt;
                }

                if (groundHit)
                {
                    var newFeetY = pos.Y - (halfCyl + rad);
                    if (newFeetY < hitY - 0.001f)
                    {
                        pos.Y = hitY + (halfCyl + rad);
                        if (_vy < 0f) _vy = 0f;
                    }
                }
            }

            // ----- Horizontal move with anti-tunneling & wall sliding -----
            // Split the frame’s move into small steps and slide on hit.
            float moveLen = MathF.Sqrt(deltaXZ.X * deltaXZ.X + deltaXZ.Z * deltaXZ.Z);
            if (moveLen > 0f)
            {
                var dirXZ = new SN.Vector3(deltaXZ.X, 0, deltaXZ.Z) / moveLen;

                // step size ~ radius/3 so we can’t tunnel through thin walls
                float stepLen = MathF.Max(0.01f, rad / 3f);
                int steps = Math.Max(1, (int)MathF.Ceiling(moveLen / stepLen));
                var step = dirXZ * (moveLen / steps);

                for (int i = 0; i < steps; i++)
                {
                    var remain = step;

                    // project intended step if a wall is immediately ahead
                    SlideAgainstWalls(ref pos, ref remain, rad, halfCyl);

                    // apply the (possibly reduced) step
                    pos += remain;

                    // POP OUT of mesh walls (triangle planes from MeshCollider)
                    ResolveHorizontalMeshWalls(ref pos, halfCyl, rad);

                    // POP OUT of box/capsule/etc (AABB-based)
                    ResolveHorizontalAABB(ref pos, halfCyl, rad);
                }

            }

            // write back transform
            tr.Position = new Vector3(pos.X, pos.Y, pos.Z);


            // ----- Camera drive -----
            if (_cam != null && _camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson(tr);
                else DriveCameraThirdPerson(tr, dt);
            }

            _inJump = false; // one-shot
        }

        void DriveCameraFirstPerson(Transform body)
        {
            var p = body.Position; p.Y += FirstPersonHeight;
            _camTr.Position = p;

            var cr = _camTr.Rotation;
            cr.X = _pitchDeg;
            cr.Y = _yawDeg;
            cr.Z = 0;
            _camTr.Rotation = cr;
        }

        void DriveCameraThirdPerson(Transform body, float dt)
        {
            var yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3((float)Math.Sin(yawRad), 0f, -(float)Math.Cos(yawRad));
            var right = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
            var up = SN.Vector3.UnitY;

            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            var desired = right * off.X + up * off.Y + (-fwd) * Math.Abs(off.Z);

            var target = new SN.Vector3((float)body.Position.X, (float)body.Position.Y, (float)body.Position.Z);
            var desiredPos = target + desired;

            if (CameraFollowLerp <= 0f)
                _camTr.Position = new Vector3(desiredPos.X, desiredPos.Y, desiredPos.Z);
            else
            {
                var cur = new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
                var t = 1f - (float)Math.Exp(-CameraFollowLerp * dt);
                var blended = cur + (desiredPos - cur) * t;
                _camTr.Position = new Vector3(blended.X, blended.Y, blended.Z);
            }

            var lookAt = target + up * (float)FirstPersonHeight;
            var dir = lookAt - new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            if (dir.LengthSquared() > 1e-6f)
            {
                dir = SN.Vector3.Normalize(dir);
                var yaw = (float)(Math.Atan2(dir.X, -dir.Z) * 180.0 / Math.PI);
                var pitch = (float)(Math.Atan2(dir.Y, Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z)) * 180.0 / Math.PI);

                var cr = _camTr.Rotation; cr.X = pitch; cr.Y = yaw; cr.Z = 0;
                _camTr.Rotation = cr;
            }
        }

        // ---------- Collision Helpers ----------

        void GetCapsule(out float radius, out float halfCyl)
        {
            if (_capsule != null)
            {
                var rr = Math.Max(0.0001f, _capsule.Radius);
                var hh = Math.Max(2f * rr, _capsule.Height);
                radius = rr;
                halfCyl = 0.5f * (hh - 2f * rr);
            }
            else
            {
                // light defaults if no capsule is present
                radius = 0.35f;
                halfCyl = 0.6f;
            }
        }

        bool RaycastGround(
            SN.Vector3 start,
            SN.Vector3 dir,
            float maxDist,
            out float groundY,
            out SN.Vector3 groundN)
        {
            // If caller passed something tiny, use a safe large default (good for big scenes)
            if (maxDist < 5f) maxDist = 500f;

            // normalize & force downward
            if (dir.LengthSquared() < 1e-8f) dir = new SN.Vector3(0, -1, 0);
            dir = SN.Vector3.Normalize(dir);
            if (dir.Y >= -1e-5f) dir = new SN.Vector3(dir.X, -MathF.Abs(dir.Y) - 1e-3f, dir.Z);

            // probe ring based on capsule footprint
            float radius, halfCyl;
            GetCapsule(out radius, out halfCyl);
            var ring = MathF.Max(0.05f, radius * 0.6f);

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
                // must be below the origin (we’re casting down)
                if (p.Y > s.Y + 1e-4f) return;

                if (p.Y > bestY)
                {
                    bestY = p.Y;
                    bestN = n;
                    anyHit = true;
                }
            }

            // ----MeshCollider triangles (enabled & non-trigger) ----
            var meshCols = SceneQuery.FindBehaviors<MeshCollider>()
                                     .Where(mc => mc.Enabled && !mc.IsTrigger);

            foreach (var mc in meshCols)
            {
                foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                {
                    if (mesh == null || mesh.Vertices == null || mesh.Vertices.Length == 0 || mesh.TriIndices == null)
                        continue;

                    var vtx = mesh.Vertices;
                    var tri = mesh.TriIndices;

                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var a = SN.Vector3.Transform(vtx[tri[i]], W);
                        var b = SN.Vector3.Transform(vtx[tri[i + 1]], W);
                        var c = SN.Vector3.Transform(vtx[tri[i + 2]], W);

                        // precompute triangle normal (ok if not unit length)
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

            // ---- AABB top faces for other colliders (enabled, non-trigger, not self) ----
            var cols = SceneQuery.FindBehaviors<Collider>()
                                 .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject);

            if (dir.Y < -1e-6f) // only if pointing down
            {
                foreach (var col in cols)
                {
                    if (col is MeshCollider) continue; // triangles already tested

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

        // Two-sided Möller–Trumbore
        static bool RayTri_TwoSided(
            SN.Vector3 ro, SN.Vector3 rd,
            SN.Vector3 a, SN.Vector3 b, SN.Vector3 c,
            out float t)
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



        void ResolveHorizontalAABB(ref SN.Vector3 pos, float halfCyl, float radius)
        {
            const float EPS = 1e-5f;
            float pad = Math.Max(0f, WallPush); // small extra clearance if you want it

            var cols = SceneQuery.FindBehaviors<Collider>()
                .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject);

            foreach (var col in cols)
            {
                var aabb = col.GetWorldAABB();

                // vertical overlap? (use capsule full extent)
                var bodyMinY = pos.Y - (halfCyl + radius);
                var bodyMaxY = pos.Y + (halfCyl + radius);
                if (bodyMaxY < aabb.Min.Y || bodyMinY > aabb.Max.Y) continue;

                // Closest point on AABB to our circle center in XZ
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
                        // On an edge or corner: push along the axis of least penetration
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


        // Slide along walls if a forward probe would hit one within (radius + stepLen)
        void SlideAgainstWalls(ref SN.Vector3 pos, ref SN.Vector3 step, float radius, float halfCyl)
        {
            if (step.LengthSquared() < 1e-10f) return;

            var dir = new SN.Vector3(step.X, 0, step.Z);
            var len = dir.Length();
            if (len < 1e-6f) return;
            dir /= len;

            // probe a bit beyond intended step
            float probe = radius + 0.05f + len;

            // ray origin: waist (middle of capsule) so we catch window rails etc.
            var origin = pos + new SN.Vector3(0, 0.5f * (halfCyl + radius * 2f), 0);

            if (RaycastWallForward(origin, dir, probe, out float t, out SN.Vector3 nHit))
            {
                // only slide if we are moving INTO the wall (dot < 0)
                var nHoriz = SN.Vector3.Normalize(new SN.Vector3(nHit.X, 0, nHit.Z));
                float into = SN.Vector3.Dot(step, nHoriz);
                if (into < 0f)
                {
                    step -= nHoriz * into; // remove into-wall component -> slide
                }
            }
        }


        // Raycast forward against vertical-ish triangles and AABB side planes within a vertical band.
        bool RaycastWallForward(SN.Vector3 start, SN.Vector3 dir, float maxDist, out float tHit, out SN.Vector3 nHit)
        {
            float bestT = float.PositiveInfinity;
            SN.Vector3 bestN = SN.Vector3.UnitX;
            bool any = false;

            // vertical band (waist +/- half height) to avoid catching floor/ceiling edges
            GetCapsule(out float radius, out float halfCyl);
            float bandMinY = start.Y - (0.5f * (halfCyl + radius));
            float bandMaxY = start.Y + (0.5f * (halfCyl + radius));

            // --- Mesh triangles (solid mesh colliders) ---
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

            // --- AABB planes (non-mesh colliders) ---
            foreach (var c in SceneQuery.FindBehaviors<Collider>()
                     .Where(c => c.Enabled && !c.IsTrigger && c.gameObject != this.gameObject && c is not MeshCollider))
            {
                var a = c.GetWorldAABB();

                // quick vertical band reject
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


        // After a small horizontal step, push the capsule out of vertical-ish MeshCollider triangles.
        void ResolveHorizontalMeshWalls(ref SN.Vector3 pos, float halfCyl, float radius, float pad = 0.02f)
        {
            float rr = radius + MathF.Max(0f, WallPush) + pad;

            // Vertical band (waist-height span) so we ignore floor/ceiling triangles.
            float bandMinY = pos.Y - (0.5f * (halfCyl + radius));
            float bandMaxY = pos.Y + (0.5f * (halfCyl + radius));

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

                        // Triangle normal
                        var n = SN.Vector3.Cross(b - a, c - a);
                        var len2 = n.LengthSquared();
                        if (len2 < 1e-12f) continue;
                        n /= MathF.Sqrt(len2);

                        // We only treat **walls** here
                        if (MathF.Abs(n.Y) > 0.45f) continue;

                        // Quick vertical reject: capsule’s band vs triangle’s Y range
                        float triMinY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                        float triMaxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                        if (triMaxY < bandMinY || triMinY > bandMaxY) continue;

                        // Horizontal wall normal and signed distance from pos to plane
                        var nh = new SN.Vector3(n.X, 0f, n.Z);
                        float nhLen2 = nh.LengthSquared();
                        if (nhLen2 < 1e-10f) continue;
                        nh /= MathF.Sqrt(nhLen2);

                        // Plane point can be 'a'; signed horizontal distance (ignore Y)
                        var toA = new SN.Vector3(pos.X - a.X, 0f, pos.Z - a.Z);
                        float dist = SN.Vector3.Dot(toA, nh); // +ve when pos is in front of the wall

                        // If we are **behind** or exactly on the wall but closer than rr, push out
                        if (dist < rr)
                        {
                            // Project capsule center onto the triangle plane (horizontally)
                            var proj = new SN.Vector3(pos.X, a.Y, pos.Z) - nh * (dist - 0f); // horizontal projection

                            // Full 3D projection needed for barycentric; lift Y to plane using plane eq
                            // plane: dot(n, X-a) = 0 -> solve Y so proj lies on plane
                            // n.X*(px-a.X) + n.Y*(py-a.Y) + n.Z*(pz-a.Z) = 0
                            // -> py = a.Y - (n.X*(px-a.X) + n.Z*(pz-a.Z))/n.Y  (guard n.Y≈0 with current wall filter)
                            float py = proj.Y;
                            if (MathF.Abs(n.Y) > 1e-4f)
                                py = a.Y - (n.X * (proj.X - a.X) + n.Z * (proj.Z - a.Z)) / n.Y;
                            var proj3 = new SN.Vector3(proj.X, py, proj.Z);

                            // Inside-triangle test via barycentric
                            if (PointInTri(proj3, a, b, c))
                            {
                                float push = rr - dist;
                                if (push > 0f)
                                {
                                    pos.X += nh.X * push;
                                    pos.Z += nh.Z * push;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Barycentric "inside triangle" (two-sided).
        static bool PointInTri(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c)
        {
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;

            float d00 = SN.Vector3.Dot(v0, v0);
            float d01 = SN.Vector3.Dot(v0, v1);
            float d02 = SN.Vector3.Dot(v0, v2);
            float d11 = SN.Vector3.Dot(v1, v1);
            float d12 = SN.Vector3.Dot(v1, v2);

            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) < 1e-10f) return false;

            float v = (d11 * d02 - d01 * d12) / denom;
            float w = (d00 * d12 - d01 * d02) / denom;
            float u = 1f - v - w;

            return (u >= -1e-4f && v >= -1e-4f && w >= -1e-4f);
        }


        static float Deg2Rad(float d) => (float)(Math.PI / 180.0) * d;
        static float Normalize180(float a)
        {
            while (a > 180f) a -= 360f;
            while (a < -180f) a += 360f;
            return a;
        }
    }
}
