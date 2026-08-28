#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Physics;
using Game_Engine.Core.Planet;
using GEInput = Game_Engine.Core.Input.Input;
using GEPhysics = Game_Engine.Core.Physics.Physics;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Physics-based player movement using Rigidbody instead of CharacterController.
    /// Gives a momentum-based feel: sliding on slopes, natural pushing of other objects,
    /// and inertia when stopping/turning. Drop-in alternative to PlayerMovement.
    ///
    /// Attach to a GameObject with a Rigidbody and CapsuleCollider.
    /// The [Require] attribute auto-adds these if missing.
    /// </summary>
    [ComponentCategory("Physics")]
    [Require(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class RigidbodyPlayer : Behavior
    {
        // ── Movement ──
        [Persist] public float MoveForce { get; set; } = 50f;
        [Persist] public float MaxSpeed { get; set; } = 7f;
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;
        [Persist] public float JumpImpulse { get; set; } = 5f;
        [Persist] public float AirControlFactor { get; set; } = 0.3f;
        [Persist] public float GroundDrag { get; set; } = 5f;
        [Persist] public float AirDrag { get; set; } = 0.5f;

        // ── Swimming ──
        [Persist] public float SwimForce { get; set; } = 30f;
        [Persist] public float SwimMaxSpeed { get; set; } = 4f;
        [Persist] public float SwimVerticalSpeed { get; set; } = 3f;
        [Persist] public float SwimDrag { get; set; } = 4f;

        // ── Look / camera ──
        [Persist] public float LookSensitivity { get; set; } = 90f;
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public Vector3 FirstPersonOffset { get; set; } = new Vector3(0, 1.7, 0);
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5);
        [Persist] public float CameraFollowLerp { get; set; } = 12f;
        [Persist] public float MaxLookPitch { get; set; } = 89f;
        [Persist] public bool AvoidCameraGroundClip { get; set; } = true;
        [Persist] public float CameraCollisionPadding { get; set; } = 0.2f;
        [Persist] public float CameraCollisionStartOffset { get; set; } = 0.05f;
        [Persist] public float CameraUpSmoothing { get; set; } = 10f;

        // ── Body facing ──
        [Persist] public bool RotateBodyWithLook { get; set; } = true;
        [Persist] public bool TurnBodyWhileMoving { get; set; } = false;

        // ── Jump buffering ──
        [Persist] public float JumpBufferSeconds { get; set; } = 0.12f;

        // ── Planet density grounding (matches CharacterController) ──
        [Persist] public float StepUpMax { get; set; } = 0.5f;
        [Persist] public float GroundSnapDistance { get; set; } = 0.7f;

        // ── Runtime ──
        Rigidbody? _rb;
        Camera? _cam;
        Transform? _camTr;

        float _yawDeg;
        float _pitchDeg;
        SN.Vector2 _wishLocal;
        bool _sprintHeld;
        float _jumpBuf;
        SN.Vector3 _cameraUp = SN.Vector3.UnitY;
        SN.Vector3 _lastMoveForward = new(0f, 0f, -1f);
        bool _airborne;
        float _verticalVel;
        readonly Stopwatch _walkClock = new();
        SN.Vector3 _lastAlignUp;
        float _lastAlignYaw = float.NaN;

        public override void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ResolveCamera();

            _yawDeg = (float)Transform.Rotation.Y;
            _pitchDeg = 0f;

            if (GEInput.MouseSensitivity < 0.15f)
                GEInput.MouseSensitivity = 0.25f;

            if (_rb != null)
            {
                _rb.FreezeRotation = true;
                _rb.Drag = 0f;
            }

            var spawnPos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var spawnPlanet = Rigidbody.FindNearestPlanet(spawnPos, out var spawnCenter, out _);
            if (spawnPlanet != null)
            {
                var radial = spawnPos - spawnCenter;
                _cameraUp = radial.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(radial) : SN.Vector3.UnitY;
                ApplyPlanetCamera(_cameraUp, true, 0f);
            }
            else
                _cameraUp = SN.Vector3.UnitY;
        }

        public override void OnDisable()
        {
            if (_cam != null)
                _cam.UseLookOverride = false;
            base.OnDisable();
        }

        public override void OnEnable() => ResolveCamera();

        void ResolveCamera()
        {
            _cam = null; _camTr = null;

            if (gameObject != null)
            {
                foreach (var c in gameObject.Children)
                {
                    var cc = c.Behaviors?.OfType<Camera>().FirstOrDefault();
                    if (cc != null && cc.Enabled) { _cam = cc; _camTr = cc.Transform; break; }
                }
            }
            if (_cam == null) { _cam = GetComponent<Camera>(); _camTr = _cam?.Transform; }
            if (_cam == null)
            {
                var cams = SceneQuery.FindBehaviors<Camera>();
                _cam = cams.FirstOrDefault(c => c.IsMain) ?? cams.FirstOrDefault();
                _camTr = _cam?.Transform;
            }
        }

        public override void Update()
        {
            float dt = Math.Max(0.0001f, Time.deltaTime);

            // ── Look ──
            float lookX = GEInput.GetAxis("Mouse X");
            float lookY = GEInput.GetAxis("Mouse Y");
            _yawDeg = Normalize180(_yawDeg - lookX * LookSensitivity * dt);
            float maxPitch = Clamp(MaxLookPitch, 10f, 89f);
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity * dt, -maxPitch, maxPitch);

            // ── Move intent ──
            GEInput.PollHardwareHeldKeys();
            int zFwd = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.W) ? 1 : 0)
                     - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.S) ? 1 : 0);
            int xRight = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.D) ? 1 : 0)
                       - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.A) ? 1 : 0);
            if (zFwd == 0 && xRight == 0)
            {
                float axisV = GEInput.GetAxis("Vertical");
                float axisH = GEInput.GetAxis("Horizontal");
                if (MathF.Abs(axisV) > 0.15f) zFwd = axisV > 0f ? 1 : -1;
                if (MathF.Abs(axisH) > 0.15f) xRight = axisH > 0f ? 1 : -1;
            }

            var local = new SN.Vector2(xRight, -zFwd);
            float m2 = local.X * local.X + local.Y * local.Y;
            if (m2 > 1e-6f)
            {
                float inv = 1f / MathF.Sqrt(m2);
                local.X *= inv; local.Y *= inv;
            }
            _wishLocal = local;

            _sprintHeld = GEInput.GetAction("Sprint")
                || GEInput.GetKey(Game_Engine.Core.Input.KeyCode.LeftShift);

            if (GEInput.GetActionDown("Jump") || GEInput.GetKeyDown(Game_Engine.Core.Input.KeyCode.Space))
                _jumpBuf = JumpBufferSeconds;
            else if (GEInput.GetAction("Jump") || GEInput.GetKey(Game_Engine.Core.Input.KeyCode.Space))
                _jumpBuf = Math.Max(_jumpBuf, 0.04f);

            var posNow = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var planet = Rigidbody.FindNearestPlanet(posNow, out var planetCenter, out _);
            bool onPlanet = planet != null;
            var planetUp = SN.Vector3.UnitY;
            if (onPlanet)
            {
                var radial = posNow - planetCenter;
                planetUp = radial.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(radial) : SN.Vector3.UnitY;
            }

            if (RotateBodyWithLook || (TurnBodyWhileMoving && m2 > 1e-6f) || onPlanet)
            {
                if (onPlanet)
                {
                    bool yawMoved = float.IsNaN(_lastAlignYaw) || MathF.Abs(_yawDeg - _lastAlignYaw) > 0.2f;
                    bool upMoved = _lastAlignUp.LengthSquared() < 1e-8f || SN.Vector3.Dot(_lastAlignUp, planetUp) < 0.9995f;
                    if (yawMoved || upMoved)
                    {
                        float yawRad = Deg2Rad(_yawDeg);
                        var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                        TransformUtil.AlignLocalUp(Transform, planetUp, yawForward);
                        _lastAlignYaw = _yawDeg;
                        _lastAlignUp = planetUp;
                    }
                }
                else
                {
                    var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
                }
            }

            if (onPlanet)
            {
                float walkDt = _walkClock.IsRunning ? (float)_walkClock.Elapsed.TotalSeconds : dt;
                _walkClock.Restart();
                if (walkDt > 0.25f) walkDt = 0.25f;
                if (walkDt < 0.0001f) walkDt = 0.0001f;
                WalkOnPlanetSurface(walkDt, planetUp);
            }

            ApplyPlanetCamera(planetUp, onPlanet, dt);
        }

        void ApplyPlanetCamera(SN.Vector3 planetUp, bool onPlanet, float dt)
        {
            if (_cam == null)
                ResolveCamera();
            if (_cam == null) return;

            if (onPlanet)
                _cameraUp = planetUp;
            else if (dt > 0f)
            {
                var desired = _rb != null ? SafeNormalize(_rb.LocalUp, SN.Vector3.UnitY) : SN.Vector3.UnitY;
                float upLerp = 1f - MathF.Exp(-MathF.Max(0f, CameraUpSmoothing) * dt);
                _cameraUp = SafeNormalize(SN.Vector3.Lerp(_cameraUp, desired, upLerp), desired);
            }

            _cam.WorldUp = _cameraUp;
            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson(_cameraUp);
                else DriveCameraThirdPerson(Math.Max(dt, 0.0001f), _cameraUp);
            }
        }

        void WalkOnPlanetSurface(float dt, SN.Vector3 planetUp)
        {
            _rb ??= GetComponent<Rigidbody>();

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var planet = Rigidbody.FindNearestPlanet(pos, out var center, out _);
            if (planet == null) return;

            var up = pos - center;
            if (up.LengthSquared() < 1e-8f) up = planetUp;
            else up = SN.Vector3.Normalize(up);

            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(up, yawForward, out var fwd, out var right);

            var wish = right * _wishLocal.X + fwd * (-_wishLocal.Y);
            wish -= up * SN.Vector3.Dot(wish, up);
            float speed = MathF.Max(1f, MaxSpeed) * (_sprintHeld ? MathF.Max(1f, SprintMultiplier) : 1f);

            float radius = 0.4f;
            float capsuleH = 1f;
            var cap = GetComponent<CapsuleCollider>();
            if (cap != null)
            {
                radius = MathF.Max(0.05f, cap.Radius);
                capsuleH = MathF.Max(radius, cap.Height * 0.5f);
            }

            const float stepUp = Rigidbody.PlanetWalkStepUp;
            const float groundSnap = Rigidbody.PlanetWalkGroundSnap;
            float probeDist = capsuleH + stepUp + groundSnap;

            if (wish.LengthSquared() > 1e-8f)
            {
                wish = SN.Vector3.Normalize(wish);
                float altBefore = (pos - center).Length();
                pos += wish * speed * dt;
                var toNew = pos - center;
                float newDist = toNew.Length();
                if (newDist > 1e-6f)
                    pos = center + (toNew / newDist) * altBefore;
                up = SN.Vector3.Normalize(pos - center);
            }

            planet.ResolveDensityPenetration(ref pos, radius);
            RefreshRadialUp(pos, center, ref up);

            bool densityHit = Rigidbody.TryDensityGroundProbe(
                planet, pos, up, radius, probeDist, out var hit);
            bool standable = densityHit && !hit.StartedInside;
            if (standable)
            {
                var n = hit.Normal.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(hit.Normal) : up;
                float minSlope = MathF.Cos(55f * (MathF.PI / 180f));
                if (SN.Vector3.Dot(n, up) < minSlope * 0.5f)
                    standable = false;
            }

            float feetDiff = float.PositiveInfinity;
            if (standable)
                feetDiff = SN.Vector3.Dot((pos - up * capsuleH) - hit.Point, up);
            bool onContact = standable && feetDiff >= -0.02f && feetDiff <= stepUp + 0.02f;

            if (!_airborne && _jumpBuf > 0f && onContact)
            {
                _airborne = true;
                _verticalVel = JumpImpulse;
                _jumpBuf = 0f;
            }

            if (_airborne)
            {
                _verticalVel -= 9.81f * dt;
                pos += up * (_verticalVel * dt);
                RefreshRadialUp(pos, center, ref up);

                bool wasInside = planet.ResolveDensityPenetration(ref pos, radius);
                RefreshRadialUp(pos, center, ref up);

                var landStart = pos + up * 0.02f;
                float landProbe = capsuleH + 0.05f;
                bool landHit = false;
                PlanetDensityHit land = default;
                if (!wasInside)
                {
                    if (planet.Spherecast(landStart, -up, radius * 0.25f, landProbe, out land))
                        landHit = true;
                    else if (planet.RaycastDensity(landStart, -up, landProbe, out land))
                        landHit = true;
                }
                if (landHit && !land.StartedInside && land.Distance < capsuleH)
                {
                    pos = land.Point + up * capsuleH;
                    if (_verticalVel < 0f) _verticalVel = 0f;
                    _airborne = false;
                    planet.ResolveDensityPenetration(ref pos, radius);
                }
                else if (_verticalVel <= 0f &&
                         Rigidbody.IsNearOuterHeightfield(planet, pos, center, up))
                {
                    float stand = planet.SampleHeightfieldRadius(up) + capsuleH;
                    float dist = (pos - center).Length();
                    if (dist <= stand + 0.02f)
                    {
                        pos = center + up * stand;
                        _airborne = false;
                        _verticalVel = 0f;
                    }
                }
            }
            else if (onContact)
            {
                pos = hit.Point + up * capsuleH;
                planet.ResolveDensityPenetration(ref pos, radius);
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (densityHit && hit.StartedInside)
            {
                planet.ResolveDensityPenetration(ref pos, radius);
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (densityHit)
            {
                // Floor is beyond step-up (ledge / cave mouth). Fall; do not teleport to the shell.
                _airborne = true;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (Rigidbody.IsNearOuterHeightfield(planet, pos, center, up, radialSlack: 1f))
            {
                float stand = planet.SampleHeightfieldRadius(up) + capsuleH;
                pos = center + up * stand;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else
            {
                _airborne = true;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }

            var tan = wish.LengthSquared() > 1e-8f ? wish * speed : SN.Vector3.Zero;
            if (_rb != null)
                _rb.Velocity = tan + up * _verticalVel;

            Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);
        }

        static void RefreshRadialUp(SN.Vector3 pos, SN.Vector3 center, ref SN.Vector3 up)
        {
            var radial = pos - center;
            if (radial.LengthSquared() > 1e-8f)
                up = SN.Vector3.Normalize(radial);
        }

        public override void FixedUpdate()
        {
            _rb ??= GetComponent<Rigidbody>();
            if (_rb == null) return;

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) dt = 0.02f;

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            if (Rigidbody.FindNearestPlanet(pos, out _, out _) != null)
                return;

            bool grounded = _rb.IsGrounded;
            bool underwater = _rb.IsUnderwater && !grounded;

            if (underwater && !_airborne)
            {
                FixedUpdateSwimming(dt);
                return;
            }

            // ── Flat-world physics ──
            float r = Deg2Rad(_yawDeg);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            var wishWorld = new SN.Vector3(
                _wishLocal.X * c + _wishLocal.Y * s,
                0f,
                -_wishLocal.X * s + _wishLocal.Y * c);

            float speed = MoveForce * (_sprintHeld ? SprintMultiplier : 1f);
            float control = grounded ? 1f : AirControlFactor;
            var horizVel = new SN.Vector3(_rb.Velocity.X, 0, _rb.Velocity.Z);
            float currentSpeed = SN.Vector3.Dot(horizVel, wishWorld);
            float maxSpd = MaxSpeed * (_sprintHeld ? SprintMultiplier : 1f);

            if (wishWorld.LengthSquared() > 1e-6f)
            {
                float addSpeed = maxSpd - currentSpeed;
                if (addSpeed > 0f)
                {
                    float accel = MathF.Min(speed * control * dt, addSpeed);
                    _rb.AddForce(wishWorld * accel / MathF.Max(dt, 0.001f));
                }
            }

            float dragFactor = MathF.Max(0f, 1f - (grounded ? GroundDrag : AirDrag) * dt);
            var vel = _rb.Velocity;
            _rb.Velocity = new SN.Vector3(vel.X * dragFactor, vel.Y, vel.Z * dragFactor);

            if (_jumpBuf > 0f && grounded)
            {
                var vel2 = _rb.Velocity;
                _rb.Velocity = new SN.Vector3(vel2.X, MathF.Max(vel2.Y, 0f), vel2.Z);
                _rb.AddImpulse(SN.Vector3.UnitY * JumpImpulse);
                _jumpBuf = 0f;
            }
            else
            {
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
        }

        /// <summary>
        /// Swimming physics: 3D movement following camera direction.
        /// </summary>
        void FixedUpdateSwimming(float dt)
        {
            if (_rb == null) return;

            float yawRad = Deg2Rad(_yawDeg);
            float pitchRad = Deg2Rad(_pitchDeg);

            float cosPitch = MathF.Cos(pitchRad);
            float sinPitch = MathF.Sin(pitchRad);
            float cosYaw = MathF.Cos(yawRad);
            float sinYaw = MathF.Sin(yawRad);

            var forward = new SN.Vector3(
                sinYaw * cosPitch,
                -sinPitch,
                cosYaw * cosPitch);
            if (forward.LengthSquared() > 1e-6f)
                forward = SN.Vector3.Normalize(forward);

            var right = new SN.Vector3(cosYaw, 0f, -sinYaw);

            var wishDir = SN.Vector3.Zero;
            if (_wishLocal.LengthSquared() > 1e-6f)
            {
                wishDir = right * _wishLocal.X + forward * _wishLocal.Y;
                if (wishDir.LengthSquared() > 1e-6f)
                    wishDir = SN.Vector3.Normalize(wishDir);
            }

            bool jumpHeld = GEInput.GetAction("Jump");
            if (jumpHeld)
                wishDir += SN.Vector3.UnitY * SwimVerticalSpeed * 0.5f;

            float swimSpeed = SwimForce * (_sprintHeld ? SprintMultiplier : 1f);
            float maxSwimSpd = SwimMaxSpeed * (_sprintHeld ? SprintMultiplier : 1f);

            var vel = _rb.Velocity;
            if (wishDir.LengthSquared() > 1e-6f)
            {
                wishDir = SN.Vector3.Normalize(wishDir);
                float currentSpeed = SN.Vector3.Dot(vel, wishDir);
                float addSpeed = maxSwimSpd - currentSpeed;
                if (addSpeed > 0f)
                    vel += wishDir * MathF.Min(swimSpeed * dt, addSpeed);
            }

            float dragFactor = MathF.Max(0f, 1f - SwimDrag * dt);
            vel *= dragFactor;

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            pos += vel * dt;
            _rb.Velocity = vel;
            Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);

            _jumpBuf = 0f;
        }

        // ── Camera helpers ──

        void DriveCameraFirstPerson(SN.Vector3 localUp)
        {
            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(localUp, yawForward, out var fwd, out var right);

            var off = new SN.Vector3((float)FirstPersonOffset.X, (float)FirstPersonOffset.Y, (float)FirstPersonOffset.Z);
            float pitchRad = Deg2Rad(_pitchDeg);
            float cp = MathF.Cos(pitchRad), sp = MathF.Sin(pitchRad);
            var lookFwd = fwd * cp + localUp * sp;
            if (lookFwd.LengthSquared() > 1e-8f)
                lookFwd = SN.Vector3.Normalize(lookFwd);
            var eye = pos + right * off.X + localUp * off.Y + fwd * off.Z;

            bool nested = _cam.gameObject?.Parent == gameObject;
            if (nested)
            {
                _camTr!.Position = new Vector3(off.X, off.Y, off.Z);
                var cr = _camTr.Rotation;
                cr.X = _pitchDeg;
                cr.Y = 0;
                cr.Z = 0;
                _camTr.Rotation = cr;
            }
            else
            {
                _camTr!.Position = new Vector3(eye.X, eye.Y, eye.Z);
                var cr = _camTr.Rotation;
                cr.X = _pitchDeg;
                cr.Y = _yawDeg;
                cr.Z = 0;
                _camTr.Rotation = cr;
            }

            _cam.WorldUp = localUp;
            _cam.UseLookOverride = true;
            _cam.LookEye = eye;
            _cam.LookForward = lookFwd;
            _cam.LookUp = localUp;
        }

        void DriveCameraThirdPerson(float dt, SN.Vector3 localUp)
        {
            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(localUp, yawForward, out var fwd, out var right);

            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            var desired = right * off.X + localUp * off.Y + (-fwd) * MathF.Abs(off.Z);

            var target = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var lookAt = target + localUp * (float)FirstPersonOffset.Y;
            var desiredPos = target + desired;
            if (AvoidCameraGroundClip)
                desiredPos = ResolveThirdPersonCameraObstruction(lookAt, desiredPos);

            if (CameraFollowLerp <= 0f)
            {
                _camTr!.Position = new Vector3(desiredPos.X, desiredPos.Y, desiredPos.Z);
            }
            else
            {
                var cur = new SN.Vector3((float)_camTr!.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
                var t = 1f - (float)Math.Exp(-CameraFollowLerp * dt);
                var blended = cur + (desiredPos - cur) * t;
                _camTr.Position = new Vector3(blended.X, blended.Y, blended.Z);
            }

            var dir = lookAt - new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            if (dir.LengthSquared() > 1e-6f)
                dir = SN.Vector3.Normalize(dir);
            else
                dir = fwd;

            _cam.WorldUp = localUp;
            _cam.UseLookOverride = true;
            _cam.LookEye = new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            _cam.LookForward = dir;
            _cam.LookUp = localUp;
        }

        // ── Helpers ──
        static float Deg2Rad(float d) => (float)(Math.PI / 180.0) * d;
        static float Normalize180(float a)
        {
            while (a > 180f) a -= 360f;
            while (a < -180f) a += 360f;
            return a;
        }
        static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            if (lenSq <= 1e-10f) return fallback;
            return v / MathF.Sqrt(lenSq);
        }

        void BuildTangentBasis(SN.Vector3 localUp, SN.Vector3 preferredForward, out SN.Vector3 forward, out SN.Vector3 right)
        {
            localUp = SafeNormalize(localUp, SN.Vector3.UnitY);
            forward = preferredForward - localUp * SN.Vector3.Dot(preferredForward, localUp);
            if (forward.LengthSquared() <= 1e-8f)
                forward = _lastMoveForward - localUp * SN.Vector3.Dot(_lastMoveForward, localUp);
            if (forward.LengthSquared() <= 1e-8f)
            {
                var seed = MathF.Abs(localUp.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                forward = SN.Vector3.Cross(seed, localUp);
            }
            forward = SafeNormalize(forward, SN.Vector3.UnitZ);

            right = SN.Vector3.Cross(forward, localUp);
            if (right.LengthSquared() <= 1e-8f)
                right = SN.Vector3.Cross(MathF.Abs(localUp.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX, localUp);
            right = SafeNormalize(right, SN.Vector3.UnitX);

            forward = SafeNormalize(SN.Vector3.Cross(localUp, right), forward);
            _lastMoveForward = forward;
        }

        SN.Vector3 ResolveThirdPersonCameraObstruction(SN.Vector3 anchor, SN.Vector3 desiredPos)
        {
            var toCam = desiredPos - anchor;
            float dist = toCam.Length();
            if (dist <= 1e-4f) return desiredPos;

            var dir = toCam / dist;
            float startOffset = MathF.Max(0f, CameraCollisionStartOffset);
            var rayOrigin = anchor + dir * startOffset;
            float maxDist = MathF.Max(0f, dist - startOffset);
            if (maxDist <= 1e-4f) return desiredPos;

            var hits = GEPhysics.RaycastAll(rayOrigin, dir, maxDist);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < hits.Count; i++)
            {
                var c = hits[i].Collider;
                var hitGo = c?.gameObject;
                if (hitGo == null) continue;
                if (IsSelfOrDescendant(hitGo)) continue;
                if (hits[i].Distance < nearest) nearest = hits[i].Distance;
            }

            if (float.IsPositiveInfinity(nearest)) return desiredPos;

            float pad = MathF.Max(0.02f, CameraCollisionPadding);
            float safeDist = MathF.Max(0.05f, nearest - pad);
            return rayOrigin + dir * safeDist;
        }

        bool IsSelfOrDescendant(GameObject go)
        {
            if (gameObject == null) return false;
            for (GameObject? cur = go; cur != null; cur = cur.Parent)
            {
                if (ReferenceEquals(cur, gameObject))
                    return true;
            }
            return false;
        }
    }
}
