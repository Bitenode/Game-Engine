using Game_Engine.Core;
using Game_Engine.Core.Component;
using System;
using System.Diagnostics;
using System.Linq;
using GEInput = Game_Engine.Core.Input.Input;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Player movement & camera:
    /// - Update(): collect input, update look, drive camera
    /// - __FixedUpdate(): call CharacterController.Simulate(...) with FixedDeltaTime
    /// </summary>
    public sealed class PlayerMovement : Behavior
    {
        // ---- Movement tunables ----
        [Persist] public float MoveSpeed { get; set; } = 4f;
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;

        // ---- Look / camera tunables ----
        [Persist] public float LookSensitivity { get; set; } = 90f; // deg per look unit per second
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public Vector3 FirstPersonOffset { get; set; } = new Vector3(0, 1.7, 0);   // head
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5);
        [Persist] public float CameraFollowLerp { get; set; } = 12f;

        // ---- Body facing behavior ----
        [Persist] public bool RotateBodyWithLook { get; set; } = true;
        [Persist] public bool TurnBodyWhileMoving { get; set; } = false;

        // ---- Debug / fallback ----
        [Persist] public bool DebugBypassMotor { get; set; } = false; // MUST be false in normal play

        // ---- Debug logging ----
        //[Persist] public bool DebugLogJump { get; set; } = true;
        double _heldLogCooldown = 0; // rate-limit held logs

        // ---- Runtime ----
        CharacterController _motor;

        Camera _cam;
        Transform _camTr;

        float _yawDeg;
        float _pitchDeg;

        // input state captured in Update and consumed in FixedUpdate
        SN.Vector2 _wishLocal;       // X=right, Y=fwd (normalized)
        bool _sprintHeld;

        // Jump buffer to survive timing between Update and FixedUpdate
        [Persist] public float JumpBufferSeconds { get; set; } = 0.12f;
        float _jumpBuf;   // counts down

        public override void Awake()
        {
            _motor = GetComponent<CharacterController>();
            ResolveCamera();

            var tr = Transform;
            _yawDeg = (float)tr.Rotation.Y;
            _pitchDeg = 0f;

            if (Game_Engine.Core.Input.Input.MouseSensitivity < 0.15f)
                Game_Engine.Core.Input.Input.MouseSensitivity = 0.25f;
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

            // --- Look ---
            float lookX = GEInput.GetAxis("Mouse X");
            float lookY = GEInput.GetAxis("Mouse Y");
            _yawDeg = Normalize180(_yawDeg - lookX * LookSensitivity * dt);
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity * dt, -89f, 89f);

            // --- Move intent (normalized, camera-local) ---
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

            // sprint / jump
            _sprintHeld = GEInput.GetAction("Sprint");

            // Primary: rising edge
            if (GEInput.GetActionDown("Jump"))
            {
                _jumpBuf = JumpBufferSeconds;
             //   Debug.WriteLine("[PlayerMovement] Jump queued (ActionDown)");
            }
            // Fallback: while Space held, keep a tiny buffer alive (helps if edge got cleared before Update)
            else if (GEInput.GetAction("Jump"))
            {
                _jumpBuf = Math.Max(_jumpBuf, 0.04f);
            }

            // --- Camera + body (visual only; no physics here) ---
            if (RotateBodyWithLook || (TurnBodyWhileMoving && m2 > 1e-6f))
            {
                var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
            }

            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson();
                else DriveCameraThirdPerson(dt);
            }
        }


        public override void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            float speed = MoveSpeed * (_sprintHeld ? SprintMultiplier : 1f);
            var wishLocal3 = new SN.Vector3(_wishLocal.X, 0f, _wishLocal.Y) * (speed * dt);

            float r = Deg2Rad(_yawDeg);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            var worldDelta = new SN.Vector3(
                wishLocal3.X * c + wishLocal3.Z * s,
                0f,
                -wishLocal3.X * s + wishLocal3.Z * c);

            bool wantJump = _jumpBuf > 0f;

            if (_motor != null && !DebugBypassMotor)
            {
                _motor.Simulate(worldDelta, wantJump);

                // diagnostics
              //  if (wantJump)
              //      Debug.WriteLine($"[PlayerMovement] wantJump=TRUE  grounded={_motor.IsGrounded} vy={_motor.VerticalVelocity:F3}");

                // If we took off, clear buffer; else tick down
                if (_motor.VerticalVelocity > 0f) _jumpBuf = 0f;
                else _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else
            {
                var p = Transform.Position;
                Transform.Position = new Vector3(p.X + worldDelta.X, p.Y, p.Z + worldDelta.Z);
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
        }


        // -------- Camera --------

        void DriveCameraFirstPerson()
        {
            var p = Transform.Position;
            var head = new Vector3(
                p.X + FirstPersonOffset.X,
                p.Y + FirstPersonOffset.Y,
                p.Z + FirstPersonOffset.Z
            );
            _camTr.Position = head;

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
                _camTr.Position = new Vector3(desiredPos.X, desiredPos.Y, desiredPos.Z);
            }
            else
            {
                var cur = new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
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

        // ---- Helpers ----
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
