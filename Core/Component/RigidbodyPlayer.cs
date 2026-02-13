#nullable enable
using System;
using System.Linq;
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

        public override void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ResolveCamera();

            _yawDeg = (float)Transform.Rotation.Y;
            _pitchDeg = 0f;

            if (GEInput.MouseSensitivity < 0.15f)
                GEInput.MouseSensitivity = 0.25f;

            // Configure the Rigidbody for player use
            if (_rb != null)
            {
                _rb.FreezeRotation = true; // no tumbling
                _rb.Drag = 0f;             // we manage drag ourselves
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
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity * dt, -89f, 89f);

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

            // ── Body rotation ──
            if (RotateBodyWithLook || (TurnBodyWhileMoving && m2 > 1e-6f))
            {
                var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
            }

            // ── Camera ──
            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson();
                else DriveCameraThirdPerson(dt);
            }
        }

        public override void FixedUpdate()
        {
            if (_rb == null) return;

            float dt = Time.fixedDeltaTime;
            bool grounded = _rb.IsGrounded;

            // ── Convert local wish to world direction ──
            float r = Deg2Rad(_yawDeg);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            var wishWorld = new SN.Vector3(
                _wishLocal.X * c + _wishLocal.Y * s,
                0f,
                -_wishLocal.X * s + _wishLocal.Y * c);

            float speed = MoveForce * (_sprintHeld ? SprintMultiplier : 1f);
            float control = grounded ? 1f : AirControlFactor;

            // Only apply force if under max speed in the wish direction
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

            // ── Drag (ground friction vs air resistance) ──
            if (grounded)
            {
                // Apply ground drag to horizontal velocity only
                float dragFactor = 1f - GroundDrag * dt;
                dragFactor = MathF.Max(0f, dragFactor);
                var vel = _rb.Velocity;
                _rb.Velocity = new SN.Vector3(vel.X * dragFactor, vel.Y, vel.Z * dragFactor);
            }
            else
            {
                float dragFactor = 1f - AirDrag * dt;
                dragFactor = MathF.Max(0f, dragFactor);
                var vel = _rb.Velocity;
                _rb.Velocity = new SN.Vector3(vel.X * dragFactor, vel.Y, vel.Z * dragFactor);
            }

            // ── Jump ──
            if (_jumpBuf > 0f && grounded)
            {
                // Set vertical velocity directly for a snappy jump
                var vel = _rb.Velocity;
                _rb.Velocity = new SN.Vector3(vel.X, MathF.Max(vel.Y, 0f), vel.Z);
                _rb.AddImpulse(SN.Vector3.UnitY * JumpImpulse);
                _jumpBuf = 0f;
            }
            else
            {
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
        }

        // ── Camera helpers (same as PlayerMovement) ──

        void DriveCameraFirstPerson()
        {
            var p = Transform.Position;
            var head = new Vector3(
                p.X + FirstPersonOffset.X,
                p.Y + FirstPersonOffset.Y,
                p.Z + FirstPersonOffset.Z);
            _camTr!.Position = head;

            var cr = _camTr.Rotation;
            cr.X = _pitchDeg;
            cr.Y = _yawDeg;
            cr.Z = 0;
            _camTr.Rotation = cr;
        }

        void DriveCameraThirdPerson(float dt)
        {
            float yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad));
            var right = new SN.Vector3(-MathF.Sin(yawRad), 0f, MathF.Cos(yawRad));
            var up = SN.Vector3.UnitY;

            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            var desired = right * off.X + up * off.Y + (-fwd) * Math.Abs(off.Z);

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

            var lookAt = target + up * (float)FirstPersonOffset.Y;
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
