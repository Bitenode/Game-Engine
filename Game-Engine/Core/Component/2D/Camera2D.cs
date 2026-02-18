#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Camera2D helper component — configures the Camera for 2D rendering.
    /// Automatically sets orthographic projection, pixel-perfect snapping,
    /// and provides follow/smoothing for a target object.
    /// Requires a Camera component on the same GameObject.
    /// </summary>
    [ComponentCategory("2D")]
    [Require(typeof(Camera))]
    public sealed class Camera2D : Behavior
    {
        // ── Configuration ──
        /// <summary>Enable pixel-perfect rendering (snaps to nearest pixel).</summary>
        [Persist] public bool PixelPerfect { get; set; } = false;

        /// <summary>Pixels per unit for pixel-perfect calculations.</summary>
        [Persist] public float PixelsPerUnit { get; set; } = 100f;

        /// <summary>Reference screen height in pixels (for pixel-perfect ortho size calculation).</summary>
        [Persist] public int ReferenceHeight { get; set; } = 1080;

        /// <summary>Zoom level (1 = default, 2 = 2x zoom, etc.).</summary>
        [Persist] public float Zoom { get; set; } = 1f;

        // ── Follow target ──
        /// <summary>Name of the GameObject to follow.</summary>
        [Persist] public string FollowTargetName { get; set; } = "";

        /// <summary>Smooth follow speed (0 = instant, higher = slower).</summary>
        [Persist] public float SmoothSpeed { get; set; } = 5f;

        /// <summary>Offset from the follow target.</summary>
        [Persist] public Vector3 FollowOffset { get; set; } = new(0, 0, -10);

        // ── Camera bounds (optional) ──
        /// <summary>Enable camera bounds clamping.</summary>
        [Persist] public bool UseBounds { get; set; } = false;

        [Persist] public float BoundsMinX { get; set; } = -100f;
        [Persist] public float BoundsMaxX { get; set; } = 100f;
        [Persist] public float BoundsMinY { get; set; } = -100f;
        [Persist] public float BoundsMaxY { get; set; } = 100f;

        // ── Camera shake ──
        private float _shakeIntensity;
        private float _shakeDuration;
        private float _shakeTimer;
        private readonly Random _shakeRandom = new();

        // ── Runtime ──
        private GameObject? _followTarget;

        public override void Start()
        {
            // Configure the camera for 2D
            var cam = GetComponent<Camera>();
            if (cam != null)
            {
                cam.Projection = Projection.Orthographic;
            }

            if (!string.IsNullOrEmpty(FollowTargetName))
                _followTarget = SceneQuery.FindByName(FollowTargetName);
        }

        public override void LateUpdate()
        {
            var cam = GetComponent<Camera>();
            if (cam == null) return;

            // Update orthographic size based on zoom
            if (PixelPerfect && PixelsPerUnit > 0)
            {
                // Pixel-perfect: ortho size = reference height / PPU / zoom
                cam.OrthoSize = ReferenceHeight / PixelsPerUnit / Zoom;
            }
            else
            {
                cam.OrthoSize = 12f / Zoom;
            }

            // Follow target
            if (_followTarget == null && !string.IsNullOrEmpty(FollowTargetName))
                _followTarget = SceneQuery.FindByName(FollowTargetName);

            if (_followTarget != null)
            {
                var targetPos = new Vector3(
                    _followTarget.Transform.Position.X + FollowOffset.X,
                    _followTarget.Transform.Position.Y + FollowOffset.Y,
                    _followTarget.Transform.Position.Z + FollowOffset.Z);

                if (SmoothSpeed > 0)
                {
                    float t = MathF.Min(SmoothSpeed * Time.deltaTime, 1f);
                    Transform.Position = new Vector3(
                        Transform.Position.X + (targetPos.X - Transform.Position.X) * t,
                        Transform.Position.Y + (targetPos.Y - Transform.Position.Y) * t,
                        targetPos.Z); // Z is instant (camera depth)
                }
                else
                {
                    Transform.Position = targetPos;
                }
            }

            // Apply bounds
            if (UseBounds)
            {
                float halfH = cam.OrthoSize * 0.5f;
                float aspect = 16f / 9f; // Default aspect ratio
                float halfW = halfH * aspect;

                var pos = Transform.Position;
                Transform.Position = new Vector3(
                    MathF.Max(BoundsMinX + halfW, MathF.Min(BoundsMaxX - halfW, (float)pos.X)),
                    MathF.Max(BoundsMinY + halfH, MathF.Min(BoundsMaxY - halfH, (float)pos.Y)),
                    pos.Z);
            }

            // Apply shake
            if (_shakeTimer > 0f)
            {
                _shakeTimer -= Time.deltaTime;
                float factor = _shakeTimer / _shakeDuration;
                float offsetX = (float)(_shakeRandom.NextDouble() * 2 - 1) * _shakeIntensity * factor;
                float offsetY = (float)(_shakeRandom.NextDouble() * 2 - 1) * _shakeIntensity * factor;

                Transform.Position = new Vector3(
                    Transform.Position.X + offsetX,
                    Transform.Position.Y + offsetY,
                    Transform.Position.Z);
            }

            // Pixel-perfect snapping
            if (PixelPerfect && PixelsPerUnit > 0)
            {
                float ppu = PixelsPerUnit;
                var pos = Transform.Position;
                Transform.Position = new Vector3(
                    MathF.Round((float)pos.X * ppu) / ppu,
                    MathF.Round((float)pos.Y * ppu) / ppu,
                    pos.Z);
            }
        }

        /// <summary>Trigger a camera shake effect.</summary>
        /// <param name="intensity">Shake intensity in world units.</param>
        /// <param name="duration">Shake duration in seconds.</param>
        public void Shake(float intensity, float duration)
        {
            _shakeIntensity = intensity;
            _shakeDuration = duration;
            _shakeTimer = duration;
        }

        /// <summary>Convert screen coordinates to world position (2D).</summary>
        public SN.Vector2 ScreenToWorld(float screenX, float screenY, float screenWidth, float screenHeight)
        {
            var cam = GetComponent<Camera>();
            if (cam == null) return SN.Vector2.Zero;

            float aspect = screenWidth / MathF.Max(screenHeight, 1f);
            float halfH = cam.OrthoSize * 0.5f;
            float halfW = halfH * aspect;

            float worldX = (float)Transform.Position.X + (screenX / screenWidth - 0.5f) * halfW * 2f;
            float worldY = (float)Transform.Position.Y - (screenY / screenHeight - 0.5f) * halfH * 2f;

            return new SN.Vector2(worldX, worldY);
        }
    }
}
