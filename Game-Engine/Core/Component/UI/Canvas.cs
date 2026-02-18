#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>How the Canvas is rendered.</summary>
    public enum CanvasRenderMode
    {
        /// <summary>Drawn after post-processing, always on top. Coordinates in pixels.</summary>
        ScreenSpaceOverlay,
        /// <summary>Rendered relative to a specific Camera, affected by post-processing.</summary>
        ScreenSpaceCamera,
        /// <summary>Lives in 3D world space on a GameObject.</summary>
        WorldSpace
    }

    /// <summary>How the Canvas scales to fit different screen sizes.</summary>
    public enum CanvasScaleMode
    {
        /// <summary>UI elements retain their pixel size regardless of screen size.</summary>
        ConstantPixelSize,
        /// <summary>UI scales with screen size based on a reference resolution.</summary>
        ScaleWithScreenSize,
        /// <summary>UI elements retain their physical size (DPI-aware).</summary>
        ConstantPhysicalSize
    }

    /// <summary>
    /// Root component for runtime UI rendering. Attach to a GameObject to enable
    /// UI rendering for this object and all children that have RectTransform + UIElement.
    /// Similar to Unity's Canvas component.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class Canvas : Behavior
    {
        // ── Render Mode ──
        [Persist] public CanvasRenderMode RenderMode { get; set; } = CanvasRenderMode.ScreenSpaceOverlay;

        /// <summary>Drawing priority among canvases (higher = drawn later / on top).</summary>
        [Persist] public int SortOrder { get; set; } = 0;

        /// <summary>Snap to pixel grid for crisp edges.</summary>
        [Persist] public bool PixelPerfect { get; set; } = true;

        // ── Scaler ──
        [Persist] public CanvasScaleMode ScaleMode { get; set; } = CanvasScaleMode.ScaleWithScreenSize;

        /// <summary>Design resolution width for ScaleWithScreenSize mode.</summary>
        [Persist] public float ReferenceResolutionX { get; set; } = 1920f;
        /// <summary>Design resolution height for ScaleWithScreenSize mode.</summary>
        [Persist] public float ReferenceResolutionY { get; set; } = 1080f;

        /// <summary>0 = match width, 1 = match height, 0.5 = balanced.</summary>
        [Persist] public float MatchWidthOrHeight { get; set; } = 0.5f;

        // ── World-Space settings ──
        /// <summary>Width in world units for WorldSpace mode.</summary>
        [Persist] public float WorldSizeX { get; set; } = 5f;
        /// <summary>Height in world units for WorldSpace mode.</summary>
        [Persist] public float WorldSizeY { get; set; } = 3f;

        // ── Static registry ──
        private static readonly List<Canvas> _all = new(8);
        public static IReadOnlyList<Canvas> All => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        /// <summary>Clear all registered canvases. Call during scene teardown to prevent stale entries.</summary>
        public static void ClearAll() => _all.Clear();

        /// <summary>
        /// Compute the root canvas rect for the current frame given the screen/viewport size.
        /// For ScreenSpaceOverlay this is the full viewport in pixels.
        /// </summary>
        public RectTransform.Rect GetCanvasRect(float viewportWidth, float viewportHeight)
        {
            switch (ScaleMode)
            {
                case CanvasScaleMode.ScaleWithScreenSize:
                {
                    float logWidth = MathF.Log2(viewportWidth / ReferenceResolutionX);
                    float logHeight = MathF.Log2(viewportHeight / ReferenceResolutionY);
                    float logScale = logWidth * (1f - MatchWidthOrHeight) + logHeight * MatchWidthOrHeight;
                    float scale = MathF.Pow(2f, logScale);
                    float w = viewportWidth / scale;
                    float h = viewportHeight / scale;
                    return new RectTransform.Rect(0, 0, w, h);
                }
                case CanvasScaleMode.ConstantPhysicalSize:
                case CanvasScaleMode.ConstantPixelSize:
                default:
                    return new RectTransform.Rect(0, 0, viewportWidth, viewportHeight);
            }
        }

        /// <summary>
        /// Returns the scale factor used to convert canvas coordinates to screen pixels.
        /// </summary>
        public float GetScaleFactor(float viewportWidth, float viewportHeight)
        {
            if (ScaleMode != CanvasScaleMode.ScaleWithScreenSize)
                return 1f;

            float logWidth = MathF.Log2(viewportWidth / ReferenceResolutionX);
            float logHeight = MathF.Log2(viewportHeight / ReferenceResolutionY);
            float logScale = logWidth * (1f - MatchWidthOrHeight) + logHeight * MatchWidthOrHeight;
            return MathF.Pow(2f, logScale);
        }
    }
}
