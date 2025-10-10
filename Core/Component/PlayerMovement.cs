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

            // make mouse feel reasonable if defaults are tiny
          //  Game_Engine.Core.Log.Debug($"[PM] Awake: MoveSpeed={MoveSpeed} LookSensitivity={LookSensitivity} Input.MouseSensitivity={Game_Engine.Core.Input.Input.MouseSensitivity}");
            if (Game_Engine.Core.Input.Input.MouseSensitivity < 0.15f)
                Game_Engine.Core.Input.Input.MouseSensitivity = 0.25f;
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

            Game_Engine.Core.Log.Debug(_camTr == null
                ? "[PM] ResolveCamera: NO camera found"
                : $"[PM] ResolveCamera: bound to camera on GO='{_cam.gameObject?.Name ?? "?"}' pos=({_camTr.Position.X:F2},{_camTr.Position.Y:F2},{_camTr.Position.Z:F2}) rot=({_camTr.Rotation.X:F1},{_camTr.Rotation.Y:F1},{_camTr.Rotation.Z:F1})");
        }

        [Persist] public bool DebugBypassMotor { get; set; } = true;


        public override void Update()
        {
            var dt = Math.Max(0.0001f, Time.deltaTime);

            // --- Look (mouse deltas are per-frame) ---
            float lookX = GEInput.GetAxis("Mouse X");
            float lookY = GEInput.GetAxis("Mouse Y");
            _yawDeg = Normalize180(_yawDeg - lookX * LookSensitivity);
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity, -89f, 89f);

            // --- Raw keys -> local intent (Z = forward/back, X = right/left) ---
            int zFwd = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.W) ? 1 : 0)
                       - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.S) ? 1 : 0);
            int xRight = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.D) ? 1 : 0)
                       - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.A) ? 1 : 0);

            // IMPORTANT: flip local.Z so W moves toward -Z at yaw=0 (typical camera forward)
            var local = new SN.Vector3(xRight, 0f, -zFwd);

            float m2 = local.X * local.X + local.Z * local.Z;
            if (m2 > 1e-6f)
            {
                float inv = 1f / MathF.Sqrt(m2);
                local.X *= inv; local.Z *= inv;
            }

            bool sprint = GEInput.GetAction("Sprint");
            float speed = MoveSpeed * (sprint ? SprintMultiplier : 1f);
            local *= speed * dt;

            // --- Rotate local (X right, Z fwd) by yaw about +Y into world ---
            float r = (float)(Math.PI / 180.0) * _yawDeg;
            float c = MathF.Cos(r), s = MathF.Sin(r);
            // worldX = localX*c + localZ*s
            // worldZ = -localX*s + localZ*c
            var worldDelta = new SN.Vector3(local.X * c + local.Z * s, 0f,
                                            -local.X * s + local.Z * c);

            bool jump = GEInput.GetActionDown("Jump");

            var before = Transform.Position;
            if (_motor != null && !DebugBypassMotor)
                _motor.Simulate(worldDelta, jump);
            else
                Transform.Position = new Vector3(before.X + worldDelta.X, before.Y, before.Z + worldDelta.Z);
            var after = Transform.Position;

            if (m2 > 1e-6f && (RotateBodyWithLook || TurnBodyWhileMoving))
            {
                var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
            }

            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson();
                else DriveCameraThirdPerson(dt);
            }

          //  if (m2 > 0 || lookX != 0 || lookY != 0 || jump || sprint)
             //   Log.Debug($"[PM] W/S={(zFwd):+0;-0;0}, A/D={(xRight):+0;-0;0}  local=({local.X:F3},{local.Z:F3}) worldΔ=({worldDelta.X:F3},{worldDelta.Z:F3})");
        }










        void DriveCameraFirstPerson()
        {
            var before = _camTr.Rotation;

            // position camera at head height above player
            var p = Transform.Position; p.Y += FirstPersonHeight;
            _camTr.Position = p;

            // set camera eulers
            var cr = _camTr.Rotation;
            cr.X = _pitchDeg; cr.Y = _yawDeg; cr.Z = 0;
            _camTr.Rotation = cr;

         //   Game_Engine.Core.Log.Debug($"[PM] DriveFP: set cam rot from ({before.X:F1},{before.Y:F1}) -> ({cr.X:F1},{cr.Y:F1}), pos=({p.X:F2},{p.Y:F2},{p.Z:F2})");
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
