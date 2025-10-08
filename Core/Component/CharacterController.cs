using System;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Basic character controller:
    /// - Moves/rotates the owner Transform
    /// - Drives an attached/child Camera (or the "main" active camera)
    /// - Works in FirstPerson or ThirdPerson modes
    /// - Exposes SetInput(...) so you can feed axes/buttons from your UI
    /// 
    /// Notes:
    /// - Uses an assumed fixed dt (AssumedDeltaTime) so it’s deterministic even if you don’t pass real time.
    /// - If you parent a Camera under the character, FP uses local offset (nice head-height). 
    ///   TP uses an offset orbit behind the character and looks-at the character.
    /// </summary>
    public sealed class CharacterController : Behavior
    {
        // -------- Tunables (persisted) ------------------------------------------
        [Persist] public bool FirstPerson { get; set; } = true;

        [Persist] public float MoveSpeed { get; set; } = 4f;     // m/s
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;

        [Persist] public float LookSensitivity { get; set; } = 90f; // deg per "look unit" per second

        // Gravity/jump kept very light-weight (optional)
        [Persist] public bool UseGravity { get; set; } = false;
        [Persist] public float Gravity { get; set; } = 9.81f;
        [Persist] public float JumpSpeed { get; set; } = 5.5f;

        // First-person camera placement
        [Persist] public float FirstPersonHeight { get; set; } = 1.7f; // meters

        // Third-person camera placement (local relative to character forward/right/up)
        // X = right, Y = up, Z = back (usually negative Z)
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5);

        // Third-person camera follow "snappiness" (units/sec). 0 = no smoothing (teleport).
        [Persist] public float CameraFollowLerp { get; set; } = 12f;

       

        // -------- Runtime state -------------------------------------------------
        // input snapshot (you feed this every frame from your UI)
        float _inMoveX;   // strafe: -1..+1
        float _inMoveZ;   // forward: -1..+1
        float _inLookX;   // yaw: mouse/gamepad X
        float _inLookY;   // pitch: mouse/gamepad Y
        bool _inJump;
        bool _inSprint;

        // orientation in degrees (character yaw; camera pitch)
        float _yawDeg;
        float _pitchDeg;

        // simple vertical velocity (for optional gravity)
        float _vy;

        Camera _cam;               // resolved camera (same GO, child, or main)
        Transform _camTr;          // convenience reference

        public override void Awake()
        {
            // Initialize yaw/pitch from current Transform
            var tr = Transform;
            _yawDeg = (float)tr.Rotation.Y;
            _pitchDeg = (float)tr.Rotation.X;

            ResolveCamera();
        }

        public override void OnEnable()
        {
            ResolveCamera();
        }

        void ResolveCamera()
        {
            _cam = null;
            _camTr = null;

            // Prefer a camera on the same GameObject
            var localCam = GetComponent<Camera>();
            if (localCam != null && localCam.Enabled)
            {
                _cam = localCam;
                _camTr = _cam.Transform;
                return;
            }

            // Then check direct children
            if (gameObject != null)
            {
                foreach (var c in gameObject.Children)
                {
                    var childCam = c.Behaviors?.OfType<Camera>()?.FirstOrDefaultSafe();
                    if (childCam != null && childCam.Enabled)
                    {
                        _cam = childCam;
                        _camTr = childCam.Transform;
                        return;
                    }
                }
            }

            // Then "main" camera, else any enabled camera in scene
            var cams = SceneQuery.FindBehaviors<Camera>();
            foreach (var c in cams) { if (c.IsMain) { _cam = c; _camTr = c.Transform; break; } }
            if (_cam == null)
            {
                foreach (var c in cams) { _cam = c; _camTr = c.Transform; break; }
            }
        }

        // Called by your input host (e.g., GamePanel) every frame before Update():
        public void SetInput(float moveX, float moveZ, float lookX, float lookY, bool jump, bool sprint)
        {
            _inMoveX = Math.Max(-1f, Math.Min(1f, moveX));
            _inMoveZ = Math.Max(-1f, Math.Min(1f, moveZ));
            _inLookX = lookX;
            _inLookY = lookY;
            _inJump = jump;
            _inSprint = sprint;
        }

        public override void Update()
        {
            // use the real clock from GameView’s timers
            var dt = Math.Max(0.0001f, Time.deltaTime);

            // ----- Orientation (yaw/pitch) -----
            var lookScale = LookSensitivity * dt;
            _yawDeg += _inLookX * lookScale;
            _pitchDeg -= _inLookY * lookScale; // invert Y

            _pitchDeg = Math.Clamp(_pitchDeg, -89f, 89f);
            if (_yawDeg > 180f) _yawDeg -= 360f;
            else if (_yawDeg < -180f) _yawDeg += 360f;
            else if (_yawDeg < -180f) _yawDeg += 360f;

            // Body rotates only around Y (yaw)
            var tr = Transform;
            var bodyRot = tr.Rotation;
            bodyRot.Y = _yawDeg;
            tr.Rotation = bodyRot;

            // ----- Movement (in character space) -----
            // Build yaw-only forward/right
            var yawRad = (float)(Math.PI / 180.0) * _yawDeg;
            var fwd = new SN.Vector3((float)Math.Sin(yawRad), 0f, -(float)Math.Cos(yawRad));
            var right = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));

            var move = right * _inMoveX + fwd * _inMoveZ;
            if (move.LengthSquared() > 1e-6f) move = SN.Vector3.Normalize(move);

            var speed = MoveSpeed * (_inSprint ? SprintMultiplier : 1f);
            var deltaXZ = move * (speed * dt);

            // Optional gravity & jump against a simple ground plane (y = 0)
            var pos = tr.Position;
            var y = (float)pos.Y;

            if (UseGravity)
            {
                // simple ground check
                bool grounded = y <= 0.0001f;

                if (grounded)
                {
                    y = 0f;
                    _vy = 0f;
                    if (_inJump) _vy = JumpSpeed;
                }
                _vy -= Gravity * dt;
                y += _vy * dt;
                if (y < 0f) { y = 0f; _vy = 0f; } // clamp to ground
            }

            // write back position
            pos.X += deltaXZ.X;
            pos.Z += deltaXZ.Z;
            if (UseGravity) pos.Y = y;
            tr.Position = pos;

            // ----- Camera driving -----
            if (_cam != null && _camTr != null)
            {
                if (FirstPerson)
                    DriveCameraFirstPerson(tr);
                else
                    DriveCameraThirdPerson(tr, dt);
            }

            // Clear one-shot inputs (jump); keep axes/buttons “held”
            _inJump = false;
        }

        void DriveCameraFirstPerson(Transform body)
        {
            // height offset (assumes camera is child for nicest result; still works if not)
            var camPos = body.Position;
            camPos.Y += FirstPersonHeight;
            _camTr.Position = camPos;

            // Camera pitch is on X; keep yaw synced with body
            var cr = _camTr.Rotation;
            cr.X = _pitchDeg;
            cr.Y = _yawDeg;
            cr.Z = 0;
            _camTr.Rotation = cr;
        }

        void DriveCameraThirdPerson(Transform body, float dt)
        {
            // Rebuild the same yaw-only basis (matches movement)
            var yawRad = (float)(Math.PI / 180.0) * _yawDeg;
            var fwd = new SN.Vector3((float)Math.Sin(yawRad), 0f, -(float)Math.Cos(yawRad));
            var right = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
            var up = SN.Vector3.UnitY;

            // Desired camera world position from local offset (R/U/F basis)
            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            // X to right, Y to up, Z is "back" (so negative pulls behind the character)
            var desired =
                right * off.X +
                up * off.Y +
                (-fwd) * Math.Abs(off.Z);

            var target = new SN.Vector3((float)body.Position.X, (float)body.Position.Y, (float)body.Position.Z);
            var desiredPos = target + desired;

            // Smooth follow
            if (CameraFollowLerp <= 0f)
            {
                _camTr.Position = new Vector3(desiredPos.X, desiredPos.Y, desiredPos.Z);
            }
            else
            {
                var cur = new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
                var t = 1f - (float)Math.Exp(-CameraFollowLerp * dt); // exp-smoothing
                var blended = cur + (desiredPos - cur) * t;
                _camTr.Position = new Vector3(blended.X, blended.Y, blended.Z);
            }

            // Aim camera at the character chest/head
            var lookAt = target + up * (float)FirstPersonHeight;
            var dir = lookAt - new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            if (dir.LengthSquared() > 1e-6f)
            {
                dir = SN.Vector3.Normalize(dir);
                // derive euler: yaw = atan2(x, -z); pitch = atan2(y, sqrt(x^2+z^2))
                var yaw = (float)(Math.Atan2(dir.X, -dir.Z) * 180.0 / Math.PI);
                var pitch = (float)(Math.Atan2(dir.Y, Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z)) * 180.0 / Math.PI);

                var cr = _camTr.Rotation;
                cr.X = pitch;
                cr.Y = yaw;
                cr.Z = 0;
                _camTr.Rotation = cr;
            }
        }
    }

    // small helper so we can use .FirstOrDefault() without LINQ newer features if desired
    internal static class LinqHelpers
    {
        public static T FirstOrDefaultSafe<T>(this System.Collections.Generic.IEnumerable<T> src)
        {
            foreach (var x in src) return x;
            return default(T);
        }
    }
}
