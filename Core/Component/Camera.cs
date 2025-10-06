using SN = System.Numerics;
using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    public enum Projection { Perspective, Orthographic }
    public enum ClearFlags { Skybox, SolidColor, DepthOnly, Nothing }

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

        public SN.Matrix4x4 GetViewMatrix()
        {
            static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);
            var go = gameObject;
            if (go is null) return SN.Matrix4x4.Identity;
            var tr = go.Transform;

            var r = SN.Matrix4x4.CreateFromYawPitchRoll(
                Deg2Rad(tr.Rotation.Y), Deg2Rad(tr.Rotation.X), Deg2Rad(tr.Rotation.Z));

            var forward = SN.Vector3.TransformNormal(new SN.Vector3(0, 0, -1), r);
            var eye = new SN.Vector3((float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);
            return SN.Matrix4x4.CreateLookAt(eye, eye + SN.Vector3.Normalize(forward), SN.Vector3.UnitY);
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
