#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Physics;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Rigidbody component — adds physics simulation (velocity, gravity, collision response)
    /// to a GameObject. Works with the CollisionWorld for overlap detection and triggers.
    /// Supports terrain heightmap collision, MeshCollider triangle collision,
    /// planet-relative gravity (pulls toward nearest planet center), and triggers.
    /// </summary>
    [ComponentCategory("Physics")]
    public sealed class Rigidbody : Behavior
    {
        // ── Properties ──
        [Persist] public float Mass { get; set; } = 1f;
        [Persist] public float Drag { get; set; } = 0.05f;
        [Persist] public float AngularDrag { get; set; } = 0.1f;
        [Persist] public bool UseGravity { get; set; } = true;
        [Persist] public bool IsKinematic { get; set; } = false;
        [Persist] public float Bounciness { get; set; } = 0.3f;
        [Persist] public float Friction { get; set; } = 0.5f;
        [Persist] public bool FreezeRotation { get; set; } = false;

        // ── Constraints ──
        [Persist] public bool FreezePositionX { get; set; } = false;
        [Persist] public bool FreezePositionY { get; set; } = false;
        [Persist] public bool FreezePositionZ { get; set; } = false;

        // ── Runtime state ──
        public SN.Vector3 Velocity { get; set; } = SN.Vector3.Zero;
        public SN.Vector3 AngularVelocity { get; set; } = SN.Vector3.Zero;
        public bool IsGrounded { get; private set; }
        public bool IsSleeping { get; private set; }
        public bool IsUnderwater { get; private set; }
        public float UnderwaterDepth { get; private set; }
        public SN.Vector3 GroundNormal { get; private set; } = SN.Vector3.UnitY;

        /// <summary>Local "up" direction: toward planet surface if on a planet, else world +Y.</summary>
        public SN.Vector3 LocalUp { get; private set; } = SN.Vector3.UnitY;

        private SN.Vector3 _forceAccum = SN.Vector3.Zero;
        private SN.Vector3 _impulseAccum = SN.Vector3.Zero;
        private float _sleepTimer;
        private const float SleepThreshold = 0.01f;
        private const float SleepDelay = 2f;

        // ── Trigger tracking ──
        private readonly HashSet<Collider> _currentTriggers = new();
        private readonly HashSet<Collider> _previousTriggers = new();

        // ── Events ──
        public event Action<Collider>? OnTriggerEnter;
        public event Action<Collider>? OnTriggerStay;
        public event Action<Collider>? OnTriggerExit;
        public event Action<Collider, SN.Vector3>? OnCollisionEnter; // collider, contact normal

        // ── Registry ──
        private static readonly List<Rigidbody> _all = new(64);
        public static IReadOnlyList<Rigidbody> All => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        /// <summary>Apply a force (continuous, multiplied by dt in FixedUpdate).</summary>
        public void AddForce(SN.Vector3 force) => _forceAccum += force;

        /// <summary>Apply an instant impulse (not multiplied by dt).</summary>
        public void AddImpulse(SN.Vector3 impulse) => _impulseAccum += impulse;

        /// <summary>Apply force at a position (generates torque).</summary>
        public void AddForceAtPosition(SN.Vector3 force, SN.Vector3 worldPoint)
        {
            _forceAccum += force;
            if (!FreezeRotation)
            {
                var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
                var r = worldPoint - pos;
                AngularVelocity += SN.Vector3.Cross(r, force) / Mass;
            }
        }

        public override void FixedUpdate()
        {
            if (IsKinematic) return;

            float dt = (float)Time.fixedDeltaTime;
            if (dt <= 0f) return;

            // Refresh shared physics cache (only rebuilds once per tick)
            PhysicsCache.RefreshFrame();

            // Wake up if forces applied
            if (_forceAccum.LengthSquared() > 0.001f || _impulseAccum.LengthSquared() > 0.001f)
            {
                IsSleeping = false;
                _sleepTimer = 0f;
            }

            if (IsSleeping)
            {
                // Even sleeping bodies should check triggers
                CheckTriggersOnly();
                return;
            }

            // ── Underwater detection ──
            var pos0 = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var underwaterState = UnderwaterQuery.GetState(pos0);
            IsUnderwater = underwaterState.HasValue;
            UnderwaterDepth = underwaterState?.Depth ?? 0f;

            // ── Planet detection ──
            var planet = FindNearestPlanet(pos0, out var planetCenter, out float planetSurfaceR);
            bool onPlanet = planet != null;

            if (onPlanet)
            {
                var toBody = pos0 - planetCenter;
                float dist = toBody.Length();
                LocalUp = dist > 1e-6f ? toBody / dist : SN.Vector3.UnitY;
            }
            else
            {
                LocalUp = SN.Vector3.UnitY;
            }

            // Gravity (planet-relative or world -Y)
            if (UseGravity)
            {
                float gravityScale = IsUnderwater ? 0.15f : 1f;
                var gravDir = onPlanet ? -LocalUp : new SN.Vector3(0, -1, 0);
                Velocity += gravDir * (9.81f * gravityScale) * dt;
            }

            // Buoyancy: upward force when underwater, stronger the deeper you go
            if (underwaterState.HasValue)
            {
                var uw = underwaterState.Value;
                float buoyancy = uw.Buoyancy;
                float submersionFactor = MathF.Min(UnderwaterDepth / 2f, 1f);
                Velocity += LocalUp * buoyancy * submersionFactor * dt;

                if (UnderwaterDepth < 1.5f)
                {
                    float pullStrength = 2f * (1f - UnderwaterDepth / 1.5f);
                    Velocity += LocalUp * pullStrength * dt;
                }
            }

            // Apply accumulated forces (F = ma → a = F/m)
            if (Mass > 0.001f)
                Velocity += (_forceAccum / Mass) * dt;
            Velocity += _impulseAccum;
            _forceAccum = SN.Vector3.Zero;
            _impulseAccum = SN.Vector3.Zero;

            // Drag (greatly increased underwater)
            float dragMultiplier = underwaterState?.Drag ?? 1f;
            Velocity *= (1f - Drag * dragMultiplier * dt);
            if (!FreezeRotation)
                AngularVelocity *= (1f - AngularDrag * dt);

            // Integrate position
            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var newPos = pos + Velocity * dt;

            // Apply position constraints
            if (FreezePositionX) newPos.X = pos.X;
            if (FreezePositionY) newPos.Y = pos.Y;
            if (FreezePositionZ) newPos.Z = pos.Z;

            // ── Ground collision ──
            IsGrounded = false;
            GroundNormal = LocalUp;

            if (onPlanet)
            {
                float halfHeight = GetColliderHalfHeight();
                var toNew = newPos - planetCenter;
                float distFromCenter = toNew.Length();
                var surfNorm = distFromCenter > 1e-6f ? toNew / distFromCenter : LocalUp;

                float actualSurfaceR = planet!.SampleSurfaceRadius(surfNorm);
                float feetDist = distFromCenter - halfHeight;

                if (feetDist < actualSurfaceR)
                {
                    // Planet surface = ground. Strip the velocity component going
                    // into the ground and keep only the tangent (no bounce).
                    float vDotN = SN.Vector3.Dot(Velocity, surfNorm);
                    if (vDotN < 0f)
                        Velocity -= surfNorm * vDotN;

                    newPos = planetCenter + surfNorm * (actualSurfaceR + halfHeight);
                    IsGrounded = true;
                    GroundNormal = surfNorm;
                }
            }
            else if (PhysicsCache.SampleTerrainHeight(newPos.X, newPos.Z, out float terrainY, out var terrainNormal))
            {
                float halfHeight = GetColliderHalfHeight();
                float feetY = newPos.Y - halfHeight;

                if (feetY < terrainY && Velocity.Y <= 0f)
                {
                    newPos.Y = terrainY + halfHeight;

                    float vDotN = SN.Vector3.Dot(Velocity, terrainNormal);
                    if (vDotN < 0f)
                    {
                        float impactSpeed = MathF.Abs(vDotN);
                        var normalComponent = vDotN * terrainNormal;
                        var tangentComponent = Velocity - normalComponent;

                        const float ImpactThreshold = 0.5f;
                        if (impactSpeed > ImpactThreshold)
                            Velocity = tangentComponent * (1f - Friction) + (-normalComponent * Bounciness);
                        else
                            Velocity = tangentComponent;

                        if (MathF.Abs(Velocity.Y) < 0.1f)
                            Velocity = new SN.Vector3(Velocity.X, 0f, Velocity.Z);
                    }

                    IsGrounded = true;
                    GroundNormal = terrainNormal;
                    OnCollisionEnter?.Invoke(null!, terrainNormal);
                }
            }
            else
            {
                // Fallback: Y=0 ground plane if no terrain or planet
                float halfHeight = GetColliderHalfHeight();
                float feetY = newPos.Y - halfHeight;
                if (feetY < 0f && Velocity.Y < 0f)
                {
                    newPos.Y = halfHeight;
                    float impactSpeed = MathF.Abs(Velocity.Y);

                    const float ImpactThreshold = 0.5f;
                    if (impactSpeed > ImpactThreshold)
                        Velocity = new SN.Vector3(Velocity.X * (1f - Friction), -Velocity.Y * Bounciness, Velocity.Z * (1f - Friction));
                    else
                        Velocity = new SN.Vector3(Velocity.X, 0f, Velocity.Z);

                    IsGrounded = true;

                    if (MathF.Abs(Velocity.Y) < 0.1f)
                        Velocity = new SN.Vector3(Velocity.X, 0f, Velocity.Z);
                }
            }

            // ── AABB collision with non-mesh colliders (BoxCollider, CapsuleCollider) ──
            var myCollider = GetComponent<Collider>();
            if (myCollider != null)
            {
                var myAABB = myCollider.GetWorldAABB();
                var delta = newPos - pos;

                // Test against non-mesh colliders
                for (int i = 0; i < PhysicsCache.NonMeshColliders.Count; i++)
                {
                    var other = PhysicsCache.NonMeshColliders[i];
                    if (ReferenceEquals(other, myCollider) || !other.IsActiveAndEnabled) continue;
                    if (other.gameObject == gameObject) continue; // skip own colliders

                    var otherAABB = other.GetWorldAABB();
                    var testMin = myAABB.Min + delta;
                    var testMax = myAABB.Max + delta;

                    if (Overlaps(testMin, testMax, otherAABB.Min, otherAABB.Max))
                    {
                        // Simple collision response: push out along smallest overlap axis
                        var overlap = ComputeOverlap(testMin, testMax, otherAABB.Min, otherAABB.Max);
                        newPos += overlap;

                        // Reflect velocity along collision normal
                        var normal = SN.Vector3.Normalize(overlap);
                        float vDotN = SN.Vector3.Dot(Velocity, normal);
                        if (vDotN < 0f)
                            Velocity -= (1f + Bounciness) * vDotN * normal;

                        // If pushed up, could be grounded on this collider
                        if (normal.Y > 0.5f)
                        {
                            IsGrounded = true;
                            GroundNormal = normal;
                        }

                        OnCollisionEnter?.Invoke(other, normal);
                    }
                }

                // ── MeshCollider triangle collision ──
                for (int mi = 0; mi < PhysicsCache.MeshColliders.Count; mi++)
                {
                    var mc = PhysicsCache.MeshColliders[mi];
                    if (mc.gameObject == gameObject) continue;

                    // First, rough AABB check to skip distant MeshColliders
                    var mcAABB = mc.GetWorldAABB();
                    float expand = 1.0f; // slight expansion for moving objects
                    var testMin2 = new SN.Vector3(
                        MathF.Min(myAABB.Min.X + delta.X, myAABB.Min.X) - expand,
                        MathF.Min(myAABB.Min.Y + delta.Y, myAABB.Min.Y) - expand,
                        MathF.Min(myAABB.Min.Z + delta.Z, myAABB.Min.Z) - expand);
                    var testMax2 = new SN.Vector3(
                        MathF.Max(myAABB.Max.X + delta.X, myAABB.Max.X) + expand,
                        MathF.Max(myAABB.Max.Y + delta.Y, myAABB.Max.Y) + expand,
                        MathF.Max(myAABB.Max.Z + delta.Z, myAABB.Max.Z) + expand);

                    if (!Overlaps(testMin2, testMax2, mcAABB.Min, mcAABB.Max))
                        continue;

                    // Detailed triangle test using a downward ray and a movement-direction ray
                    foreach (var (mesh, W) in mc.EnumerateTargetMeshesWorld())
                    {
                        if (mesh?.Vertices == null || mesh.TriIndices == null) continue;
                        var vtx = mesh.Vertices;
                        var tri = mesh.TriIndices;

                        for (int t = 0; t < tri.Length; t += 3)
                        {
                            if (tri[t] >= vtx.Length || tri[t + 1] >= vtx.Length || tri[t + 2] >= vtx.Length)
                                continue;

                            var a = SN.Vector3.Transform(vtx[tri[t]], W);
                            var b = SN.Vector3.Transform(vtx[tri[t + 1]], W);
                            var c = SN.Vector3.Transform(vtx[tri[t + 2]], W);

                            var triNorm = SN.Vector3.Cross(b - a, c - a);
                            float len2 = triNorm.LengthSquared();
                            if (len2 < 1e-12f) continue;
                            triNorm /= MathF.Sqrt(len2);

                            // Sphere-triangle test: check if newPos sphere overlaps this triangle
                            float halfH = GetColliderHalfHeight();
                            float radius = GetColliderRadius();
                            float sphereR = MathF.Max(radius, 0.25f);

                            // Test center point against triangle plane
                            float distToPlane = SN.Vector3.Dot(newPos - a, triNorm);
                            if (MathF.Abs(distToPlane) > sphereR) continue;

                            // Project sphere center onto triangle plane
                            var projected = newPos - triNorm * distToPlane;

                            // Check if projected point is inside triangle (or close enough)
                            if (!PointInTriangleExpanded(projected, a, b, c, sphereR * 0.5f)) continue;

                            // Resolve: push out along triangle normal
                            float penetration = sphereR - distToPlane;
                            if (penetration > 0f && SN.Vector3.Dot(Velocity, triNorm) < 0f)
                            {
                                newPos += triNorm * penetration;

                                // Reflect velocity
                                float vDotN = SN.Vector3.Dot(Velocity, triNorm);
                                Velocity -= (1f + Bounciness) * vDotN * triNorm;

                                if (triNorm.Y > 0.5f)
                                {
                                    IsGrounded = true;
                                    GroundNormal = triNorm;
                                }

                                OnCollisionEnter?.Invoke(mc, triNorm);
                            }
                        }
                    }
                }

                // ── Trigger detection (happens AFTER position resolution) ──
                _currentTriggers.Clear();
                var finalMin = myAABB.Min + (newPos - pos);
                var finalMax = myAABB.Max + (newPos - pos);

                for (int i = 0; i < PhysicsCache.TriggerColliders.Count; i++)
                {
                    var trigger = PhysicsCache.TriggerColliders[i];
                    if (ReferenceEquals(trigger, myCollider)) continue;
                    if (trigger.gameObject == gameObject) continue;

                    var trigAABB = trigger.GetWorldAABB();
                    if (Overlaps(finalMin, finalMax, trigAABB.Min, trigAABB.Max))
                        _currentTriggers.Add(trigger);
                }
            }

            // Process trigger events
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

            // Apply new position
            Transform.Position = new Vector3((float)newPos.X, (float)newPos.Y, (float)newPos.Z);

            // Apply rotation
            if (!FreezeRotation && AngularVelocity.LengthSquared() > 0.0001f)
            {
                var rot = Transform.Rotation;
                var deg = AngularVelocity * (180f / MathF.PI) * dt;
                Transform.Rotation = new Vector3((float)(rot.X + deg.X), (float)(rot.Y + deg.Y), (float)(rot.Z + deg.Z));
            }

            // Sleep detection
            if (Velocity.LengthSquared() < SleepThreshold && AngularVelocity.LengthSquared() < SleepThreshold)
            {
                _sleepTimer += dt;
                if (_sleepTimer >= SleepDelay)
                {
                    IsSleeping = true;
                    Velocity = SN.Vector3.Zero;
                    AngularVelocity = SN.Vector3.Zero;
                }
            }
            else
            {
                _sleepTimer = 0f;
            }
        }

        /// <summary>
        /// Even sleeping bodies check trigger overlaps so that enter/exit events
        /// fire when another object moves into/out of them.
        /// </summary>
        private void CheckTriggersOnly()
        {
            var myCollider = GetComponent<Collider>();
            if (myCollider == null) return;

            _currentTriggers.Clear();
            var myAABB = myCollider.GetWorldAABB();

            for (int i = 0; i < PhysicsCache.TriggerColliders.Count; i++)
            {
                var trigger = PhysicsCache.TriggerColliders[i];
                if (ReferenceEquals(trigger, myCollider)) continue;
                if (trigger.gameObject == gameObject) continue;

                var trigAABB = trigger.GetWorldAABB();
                if (Overlaps(myAABB.Min, myAABB.Max, trigAABB.Min, trigAABB.Max))
                    _currentTriggers.Add(trigger);
            }

            foreach (var t in _currentTriggers)
            {
                if (_previousTriggers.Contains(t))
                    OnTriggerStay?.Invoke(t);
                else
                {
                    // Wake the body when a trigger first overlaps
                    WakeUp();
                    OnTriggerEnter?.Invoke(t);
                }
            }
            foreach (var t in _previousTriggers)
            {
                if (!_currentTriggers.Contains(t))
                    OnTriggerExit?.Invoke(t);
            }
            _previousTriggers.Clear();
            foreach (var t in _currentTriggers) _previousTriggers.Add(t);
        }

        /// <summary>Wake up a sleeping rigidbody.</summary>
        public void WakeUp()
        {
            IsSleeping = false;
            _sleepTimer = 0f;
        }

        // ── Collider size helpers ──

        /// <summary>Get the half-height from the object's collider (bottom to center).</summary>
        private float GetColliderHalfHeight()
        {
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null) return capsule.Height * 0.5f;

            var box = GetComponent<BoxCollider>();
            if (box != null) return (float)(box.Size.Y * 0.5);

            return 0f; // point object
        }

        /// <summary>Get the effective radius of the object's collider.</summary>
        private float GetColliderRadius()
        {
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null) return capsule.Radius;

            var box = GetComponent<BoxCollider>();
            if (box != null) return MathF.Max((float)box.Size.X, (float)box.Size.Z) * 0.5f;

            return 0.25f;
        }

        // ── Geometry helpers ──

        static bool Overlaps(SN.Vector3 aMin, SN.Vector3 aMax, SN.Vector3 bMin, SN.Vector3 bMax)
            => (aMin.X <= bMax.X && aMax.X >= bMin.X) &&
               (aMin.Y <= bMax.Y && aMax.Y >= bMin.Y) &&
               (aMin.Z <= bMax.Z && aMax.Z >= bMin.Z);

        static SN.Vector3 ComputeOverlap(SN.Vector3 aMin, SN.Vector3 aMax, SN.Vector3 bMin, SN.Vector3 bMax)
        {
            float ox1 = bMax.X - aMin.X;
            float ox2 = aMax.X - bMin.X;
            float oy1 = bMax.Y - aMin.Y;
            float oy2 = aMax.Y - bMin.Y;
            float oz1 = bMax.Z - aMin.Z;
            float oz2 = aMax.Z - bMin.Z;

            float minOx = MathF.Abs(ox1) < MathF.Abs(ox2) ? ox1 : -ox2;
            float minOy = MathF.Abs(oy1) < MathF.Abs(oy2) ? oy1 : -oy2;
            float minOz = MathF.Abs(oz1) < MathF.Abs(oz2) ? oz1 : -oz2;

            if (MathF.Abs(minOx) <= MathF.Abs(minOy) && MathF.Abs(minOx) <= MathF.Abs(minOz))
                return new SN.Vector3(minOx, 0, 0);
            if (MathF.Abs(minOy) <= MathF.Abs(minOz))
                return new SN.Vector3(0, minOy, 0);
            return new SN.Vector3(0, 0, minOz);
        }

        /// <summary>
        /// Check if a point is inside a triangle (with expansion for sphere radius).
        /// Uses barycentric coordinates with an epsilon for near-edge hits.
        /// </summary>
        static bool PointInTriangleExpanded(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, float expand)
        {
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;

            float dot00 = SN.Vector3.Dot(v0, v0);
            float dot01 = SN.Vector3.Dot(v0, v1);
            float dot02 = SN.Vector3.Dot(v0, v2);
            float dot11 = SN.Vector3.Dot(v1, v1);
            float dot12 = SN.Vector3.Dot(v1, v2);

            float inv = dot00 * dot11 - dot01 * dot01;
            if (MathF.Abs(inv) < 1e-10f) return false;
            inv = 1f / inv;

            float u = (dot11 * dot02 - dot01 * dot12) * inv;
            float v = (dot00 * dot12 - dot01 * dot02) * inv;

            float edgeLen = MathF.Max(MathF.Sqrt(dot00), MathF.Max(MathF.Sqrt(dot11), (b - c).Length()));
            float eps = edgeLen > 1e-6f ? expand / edgeLen : 0.1f;

            return u >= -eps && v >= -eps && (u + v) <= 1f + eps;
        }

        // ── Planet helpers (shared across physics components) ──

        /// <summary>
        /// Finds the nearest active PlanetTerrain and returns its center position
        /// and effective surface radius (base radius + max biome height amplitude).
        /// </summary>
        internal static PlanetTerrain? FindNearestPlanet(SN.Vector3 worldPos, out SN.Vector3 center, out float surfaceRadius)
        {
            center = SN.Vector3.Zero;
            surfaceRadius = 0f;
            PlanetTerrain? best = null;
            float bestDist2 = float.MaxValue;

            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var pt = PlanetTerrain.ActivePlanets[i];
                if (pt?.Config == null || pt.gameObject == null) continue;

                var W = SceneGraphUtil.AccumulateWorld(pt.gameObject);
                var pc = new SN.Vector3(W.M41, W.M42, W.M43);
                float d2 = (worldPos - pc).LengthSquared();
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    best = pt;
                    center = pc;

                    float maxAmp = 0f;
                    foreach (var b in pt.Config.Biomes)
                        maxAmp = Math.Max(maxAmp, b.HeightAmplitude);
                    surfaceRadius = pt.Config.Radius + maxAmp;
                }
            }
            return best;
        }

        /// <summary>
        /// Computes the local "up" direction from a planet center to a position.
        /// Returns UnitY if no planet is active or position is at the center.
        /// </summary>
        internal static SN.Vector3 GetPlanetUp(SN.Vector3 worldPos)
        {
            var planet = FindNearestPlanet(worldPos, out var center, out _);
            if (planet == null) return SN.Vector3.UnitY;
            var up = worldPos - center;
            float len = up.Length();
            return len > 1e-6f ? up / len : SN.Vector3.UnitY;
        }
    }
}
