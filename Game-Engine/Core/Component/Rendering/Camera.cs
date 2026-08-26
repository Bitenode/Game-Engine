using SN = System.Numerics;
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Core.Component
{
    public enum Projection { Perspective, Orthographic }
    public enum ClearFlags { Skybox, SolidColor, DepthOnly, Nothing }

    [ComponentCategory("Rendering")]
    public sealed class Camera : Behavior
    {
        // Lens / projection
        [Persist] public Projection Projection { get; set; } = Projection.Perspective;
        [Persist] public float FieldOfView { get; set; } = 60f;   // degrees
        [Persist] public float OrthoSize { get; set; } = 12f;   // world-units height

        // Clipping
        [Persist] public float Near { get; set; } = 0.1f;
        [Persist] public float Far { get; set; } = 1000f;

        // Viewport (normalized 0..1)
        [Persist] public float ViewportX { get; set; } = 0f;
        [Persist] public float ViewportY { get; set; } = 0f;
        [Persist] public float ViewportW { get; set; } = 1f;
        [Persist] public float ViewportH { get; set; } = 1f;

        // Clearing
        [Persist] public ClearFlags Clear { get; set; } = ClearFlags.Skybox;
        [Persist] public Color Background { get; set; } = Color.Parse("#202020");

        // Default camera for Game mode
        [Persist] public bool IsMain { get; set; } = false;

        /// <summary>
        /// World-space "up" for the view matrix. Defaults to +Y.
        /// Set by planet-aware player controllers to align the horizon with the terrain.
        /// </summary>
        public SN.Vector3 WorldUp { get; set; } = SN.Vector3.UnitY;

        /// <summary>When true, <see cref="GetViewMatrix"/> uses the look vectors below instead of the transform.</summary>
        public bool UseLookOverride { get; set; }
        public SN.Vector3 LookEye { get; set; }
        public SN.Vector3 LookForward { get; set; } = -SN.Vector3.UnitZ;
        public SN.Vector3 LookUp { get; set; } = SN.Vector3.UnitY;

        public SN.Matrix4x4 GetViewMatrix()
        {
            var go = gameObject;
            if (go is null) return SN.Matrix4x4.Identity;

            if (UseLookOverride)
            {
                var eyeO = LookEye;
                var fwdO = LookForward.LengthSquared() > 1e-10f ? SN.Vector3.Normalize(LookForward) : new SN.Vector3(0f, 0f, -1f);
                var upO = LookUp.LengthSquared() > 1e-10f ? SN.Vector3.Normalize(LookUp) : SN.Vector3.UnitY;
                upO -= fwdO * SN.Vector3.Dot(upO, fwdO);
                if (upO.LengthSquared() <= 1e-8f)
                {
                    var seed = MathF.Abs(fwdO.Y) < 0.99f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                    upO = SN.Vector3.Normalize(seed - fwdO * SN.Vector3.Dot(seed, fwdO));
                }
                else
                    upO = SN.Vector3.Normalize(upO);
                return SN.Matrix4x4.CreateLookAt(eyeO, eyeO + fwdO, upO);
            }

            // Child cameras (Player/PlayerCamera) store a local offset. Using
            // Transform.Position as the eye put the Game view at ~ (0,1.7,0)
            // — inside the planet water sphere.
            var world = SceneGraphUtil.AccumulateWorld(go);
            var eye = new SN.Vector3(world.M41, world.M42, world.M43);

            var forward = SN.Vector3.TransformNormal(new SN.Vector3(0f, 0f, -1f), world);
            if (forward.LengthSquared() <= 1e-10f)
                forward = new SN.Vector3(0f, 0f, -1f);
            else
                forward = SN.Vector3.Normalize(forward);

            var up = WorldUp.LengthSquared() > 1e-8f
                ? SN.Vector3.Normalize(WorldUp)
                : SN.Vector3.TransformNormal(new SN.Vector3(0f, 1f, 0f), world);
            if (up.LengthSquared() <= 1e-10f)
                up = SN.Vector3.UnitY;
            else
                up = SN.Vector3.Normalize(up);

            // Keep LookAt stable even if forward and up become nearly collinear.
            up -= forward * SN.Vector3.Dot(up, forward);
            if (up.LengthSquared() <= 1e-8f)
            {
                var seed = MathF.Abs(forward.Y) < 0.99f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                up = SN.Vector3.Normalize(seed - forward * SN.Vector3.Dot(seed, forward));
            }
            else
            {
                up = SN.Vector3.Normalize(up);
            }

            return SN.Matrix4x4.CreateLookAt(eye, eye + forward, up);
        }

        public SN.Matrix4x4 GetProjectionMatrix(float aspect)
        {
            aspect = Math.Max(0.0001f, aspect);
            float n = Math.Max(0.0001f, Near);
            float f = Math.Max(n + 1e-3f, Far);

            return Projection == Projection.Perspective
                ? SN.Matrix4x4.CreatePerspectiveFieldOfView(
                    Math.Clamp(FieldOfView, 1f, 179f) * (float)Math.PI / 180f,
                    aspect, n, f)
                : SN.Matrix4x4.CreateOrthographic(OrthoSize * aspect, OrthoSize, n, f); // top-down OK
        }

        public SN.Matrix4x4 GetProjectionMatrix(Avalonia.Size viewport)
        {
            var aspect = viewport.Width <= 0 || viewport.Height <= 0
                ? 1f : (float)(viewport.Width / viewport.Height);
            return GetProjectionMatrix(aspect);
        }

        public override void OnEnable() => CameraService.Register(this);
        public override void OnDisable() => CameraService.Unregister(this);
    }
}
