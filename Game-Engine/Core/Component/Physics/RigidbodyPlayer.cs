#nullable enable
using System;
using System.Linq;
using Game_Engine.Core;
using GEInput = Game_Engine.Core.Input.Input;
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

        // ── Body facing ──
        [Persist] public bool RotateBodyWithLook { get; set; } = true;
        [Persist] public bool TurnBodyWhileMoving { get; set; } = false;

        // ── Jump buffering ──
        [Persist] public float JumpBufferSeconds { get; set; } = 0.12f;

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
                _cameraUp = _rb.LocalUp;
            }
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
            float maxPitch = (_rb != null && _rb.IsUnderwater) ? 89f : 89f;
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity * dt, -maxPitch, maxPitch);

            // ── Move intent ──
            int zFwd = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.W) ? 1 : 0)
                     - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.S) ? 1 : 0);
            int xRight = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.D) ? 1 : 0)
                       - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.A) ? 1 : 0);

            var local = new SN.Vector2(xRight, -zFwd);
            float m2 = local.X * local.X + local.Y * local.Y;
            if (m2 > 1e-6f)
            {
                float inv = 1f / MathF.Sqrt(m2);
                local.X *= inv; local.Y *= inv;
            }
            _wishLocal = local;

            _sprintHeld = GEInput.GetAction("Sprint");

            if (GEInput.GetActionDown("Jump"))
                _jumpBuf = JumpBufferSeconds;
            else if (GEInput.GetAction("Jump"))
                _jumpBuf = Math.Max(_jumpBuf, 0.04f);

            // ── Body rotation (yaw only, same as original) ──
            if (RotateBodyWithLook || (TurnBodyWhileMoving && m2 > 1e-6f))
            {
                var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
            }

            // ── Camera ──
            var localUp = _rb?.LocalUp ?? SN.Vector3.UnitY;
            // Smooth camera up to avoid visible jitter/roll snaps as terrain normals change.
            float upLerp = 1f - MathF.Exp(-12f * dt);
            _cameraUp = SN.Vector3.Normalize(_cameraUp + (localUp - _cameraUp) * upLerp);
            if (_cam != null)
                _cam.WorldUp = _cameraUp;

            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson(_cameraUp);
                else DriveCameraThirdPerson(dt, _cameraUp);
            }
        }

        public override void FixedUpdate()
        {
            if (_rb == null) return;

            float dt = Time.fixedDeltaTime;
            bool grounded = _rb.IsGrounded;
            bool underwater = _rb.IsUnderwater;

            if (underwater)
            {
                FixedUpdateSwimming(dt);
                return;
            }

            var localUp = _rb.LocalUp;
            bool onPlanet = localUp.Y < 0.999f || localUp.X * localUp.X + localUp.Z * localUp.Z > 0.001f;

            // ── Convert local wish to world direction ──
            float r = Deg2Rad(_yawDeg);
            float c = MathF.Cos(r), s = MathF.Sin(r);

            SN.Vector3 wishWorld;
            if (onPlanet)
            {
                // On a planet: derive movement basis from the actual camera forward,
                // then project onto the local tangent plane to avoid W/S flips.
                var srcTr = _camTr ?? Transform;
                var camFwd = TransformUtil.ForwardFrom(srcTr);

                var fwd = camFwd - localUp * SN.Vector3.Dot(camFwd, localUp);
                if (fwd.LengthSquared() <= 1e-8f)
                {
                    var seed = MathF.Abs(localUp.Y) < 0.99f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                    fwd = SN.Vector3.Cross(seed, localUp);
                }
                fwd = SN.Vector3.Normalize(fwd);

                var right = SN.Vector3.Cross(fwd, localUp);
                if (right.LengthSquared() <= 1e-8f)
                    right = new SN.Vector3(c, 0f, -s);
                else
                    right = SN.Vector3.Normalize(right);

                wishWorld = right * _wishLocal.X + fwd * (-_wishLocal.Y);
                wishWorld -= localUp * SN.Vector3.Dot(wishWorld, localUp);
                if (wishWorld.LengthSquared() > 1e-6f)
                    wishWorld = SN.Vector3.Normalize(wishWorld);
            }
            else
            {
                // Flat world: original formula
                wishWorld = new SN.Vector3(
                    _wishLocal.X * c + _wishLocal.Y * s,
                    0f,
                    -_wishLocal.X * s + _wishLocal.Y * c);
            }

            float speed = MoveForce * (_sprintHeld ? SprintMultiplier : 1f);
            float control = grounded ? 1f : AirControlFactor;

            if (onPlanet)
            {
                // Tangent velocity (remove component along local up)
                var tangentVel = _rb.Velocity - localUp * SN.Vector3.Dot(_rb.Velocity, localUp);
                float currentSpeed = SN.Vector3.Dot(tangentVel, wishWorld);
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

                // Drag (tangent plane only)
                float dragFactor = MathF.Max(0f, 1f - (grounded ? GroundDrag : AirDrag) * dt);
                var vel2 = _rb.Velocity;
                float radialComp = SN.Vector3.Dot(vel2, localUp);
                var tangent = vel2 - localUp * radialComp;
                _rb.Velocity = tangent * dragFactor + (grounded ? SN.Vector3.Zero : localUp * radialComp);

                // Jump (along local up)
                if (_jumpBuf > 0f && grounded)
                {
                    var vel3 = _rb.Velocity;
                    float upComp = SN.Vector3.Dot(vel3, localUp);
                    if (upComp < 0f)
                        _rb.Velocity = vel3 - localUp * upComp;
                    _rb.AddImpulse(localUp * JumpImpulse);
                    _jumpBuf = 0f;
                }
                else
                {
                    _jumpBuf = Math.Max(0f, _jumpBuf - dt);
                }
            }
            else
            {
                // ── Original flat-world physics ──
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

            if (wishDir.LengthSquared() > 1e-6f)
            {
                float currentSpeed = SN.Vector3.Dot(_rb.Velocity, SN.Vector3.Normalize(wishDir));
                float addSpeed = maxSwimSpd - currentSpeed;
                if (addSpeed > 0f)
                {
                    float accel = MathF.Min(swimSpeed * dt, addSpeed);
                    _rb.AddForce(wishDir * accel / MathF.Max(dt, 0.001f));
                }
            }

            float dragFactor = MathF.Max(0f, 1f - SwimDrag * dt);
            _rb.Velocity *= dragFactor;

            _jumpBuf = 0f;
        }

        // ── Camera helpers ──

        void DriveCameraFirstPerson(SN.Vector3 localUp)
        {
            var p = Transform.Position;
            float offY = (float)FirstPersonOffset.Y;

            // Use localUp for head offset on planets, world Y on flat ground
            if (localUp.Y > 0.999f)
            {
                _camTr!.Position = new Vector3(
                    p.X + FirstPersonOffset.X,
                    p.Y + offY,
                    p.Z + FirstPersonOffset.Z);
            }
            else
            {
                var pos = new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
                var head = pos + localUp * offY;
                _camTr!.Position = new Vector3(head.X, head.Y, head.Z);
            }

            var cr = _camTr.Rotation;
            cr.X = _pitchDeg;
            cr.Y = _yawDeg;
            cr.Z = 0;
            _camTr.Rotation = cr;
        }

        void DriveCameraThirdPerson(float dt, SN.Vector3 localUp)
        {
            float yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad));
            var right = new SN.Vector3(-MathF.Sin(yawRad), 0f, MathF.Cos(yawRad));

            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            var desired = right * off.X + localUp * off.Y + (-fwd) * MathF.Abs(off.Z);

            var target = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var desiredPos = target + desired;

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

            var lookAt = target + localUp * (float)FirstPersonOffset.Y;
            var dir = lookAt - new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            if (dir.LengthSquared() > 1e-6f)
            {
                dir = SN.Vector3.Normalize(dir);
                var yaw = (float)(Math.Atan2(dir.X, -dir.Z) * 180.0 / Math.PI);
                var pitch = (float)(Math.Atan2(dir.Y, Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z)) * 180.0 / Math.PI);
                var cr = _camTr.Rotation; cr.X = pitch; cr.Y = yaw; cr.Z = 0; _camTr.Rotation = cr;
            }
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
    }
}
