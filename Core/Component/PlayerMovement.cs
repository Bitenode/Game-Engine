using System;
using System.Linq;
using SN = System.Numerics;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using GEInput = Game_Engine.Core.Input.Input;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Basic player movement & camera driver.
    /// - Reads Input axes/actions
    /// - Builds world-space horizontal delta from yaw
    /// - Calls CharacterController.Simulate(...) for CCD, grounding, gravity
    /// - Drives a child Camera (first/third person)
    /// </summary>
    public sealed class PlayerMovement : Behavior
    {
        // ---- Movement tunables ----
        [Persist] public float MoveSpeed { get; set; } = 4f;
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;

        // ---- Look / camera tunables ----
        [Persist] public float LookSensitivity { get; set; } = 90f; // deg per look unit per second
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public float FirstPersonHeight { get; set; } = 1.7f;
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5); // X=right, Y=up, Z=back
        [Persist] public float CameraFollowLerp { get; set; } = 12f; // 0 = snap

        // ---- Body facing behavior ----
        [Persist] public bool RotateBodyWithLook { get; set; } = true;  // true = body yaw follows camera
        [Persist] public bool TurnBodyWhileMoving { get; set; } = false; // if false and !RotateBodyWithLook, body keeps its yaw

        // ---- Runtime ----
        CharacterController _motor;

        Camera _cam;
        Transform _camTr;

        float _yawDeg;
        float _pitchDeg;

        public override void Awake()
        {
            _motor = GetComponent<CharacterController>();
            ResolveCamera();

            // seed yaw/pitch from current transforms
            var tr = Transform;
            _yawDeg = (float)tr.Rotation.Y;
            _pitchDeg = 0f;
        }

        public override void OnEnable() => ResolveCamera();

        void ResolveCamera()
        {
            _cam = null; _camTr = null;

            // prefer child camera
            if (gameObject != null)
            {
                foreach (var c in gameObject.Children)
                {
                    var cc = c.Behaviors?.OfType<Camera>().FirstOrDefault();
                    if (cc != null && cc.Enabled) { _cam = cc; _camTr = cc.Transform; break; }
                }
            }

            // fallback: camera on same GO
            if (_cam == null) { _cam = GetComponent<Camera>(); _camTr = _cam?.Transform; }

            // last fallback: main/first enabled camera in scene
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

            // ---- Read input ----
            // Axes (smoothed): WASD/Arrows -> Horizontal/Vertical, mouse -> Mouse X/Y
            float mx = GEInput.GetAxis("Horizontal");
            float mz = GEInput.GetAxis("Vertical");
            float lookX = GEInput.GetAxis("Mouse X");
            float lookY = GEInput.GetAxis("Mouse Y");

            bool sprint = GEInput.GetAction("Sprint");
            bool jump = GEInput.GetActionDown("Jump");

            // ---- Look -> yaw/pitch ----
          //  var lookScale = LookSensitivity;
            _yawDeg -= lookX * LookSensitivity;
            _pitchDeg -= lookY * LookSensitivity;
            _pitchDeg = Clamp(_pitchDeg, -89f, 89f);
            _yawDeg = Normalize180(_yawDeg);

            // ---- Build world-space basis from yaw ----
            // Forward = +X at yaw=0; Right = +Z at yaw=0 (matches your renderer)
            float yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
            var right = new SN.Vector3(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));

            // Desired horizontal delta for this frame
            var wish = right * mx + fwd * mz;
            if (wish.LengthSquared() > 1e-6f) wish = SN.Vector3.Normalize(wish);

            float speed = MoveSpeed * (sprint ? SprintMultiplier : 1f);
            var desiredDeltaXZ = wish * (speed * dt);

            // ---- Simulate via motor (handles CCD, grounding, gravity) ----
            if (_motor != null) _motor.Simulate(desiredDeltaXZ, jump);
            else Transform.Position = new Vector3(
                Transform.Position.X + desiredDeltaXZ.X,
                Transform.Position.Y,
                Transform.Position.Z + desiredDeltaXZ.Z);

            // ---- Body facing options ----
            if (RotateBodyWithLook)
            {
                var r = Transform.Rotation; r.Y = _yawDeg; Transform.Rotation = r;
            }
            else if (TurnBodyWhileMoving && wish.LengthSquared() > 1e-6f)
            {
                var r = Transform.Rotation; r.Y = _yawDeg; Transform.Rotation = r;
            }

            // ---- Camera drive (child camera) ----
            if (_cam != null && _camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson();
                else DriveCameraThirdPerson(dt);
            }
        }

        void DriveCameraFirstPerson()
        {
            // position camera at head height above player
            var p = Transform.Position; p.Y += FirstPersonHeight;
            _camTr.Position = p;

            // camera eulers get pitch/yaw, zero roll
            var cr = _camTr.Rotation;
            cr.X = _pitchDeg; cr.Y = _yawDeg; cr.Z = 0;
            _camTr.Rotation = cr;
        }

        void DriveCameraThirdPerson(float dt)
        {
            float yawRad = Deg2Rad(_yawDeg);
            var fwd = new SN.Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
            var right = new SN.Vector3(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
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
                var t = 1f - (float)Math.Exp(-CameraFollowLerp * dt); // exp smoothing
                var blended = cur + (desiredPos - cur) * t;
                _camTr.Position = new Vector3(blended.X, blended.Y, blended.Z);
            }

            // Aim camera at head height
            var lookAt = target + up * (float)FirstPersonHeight;
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
        static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
