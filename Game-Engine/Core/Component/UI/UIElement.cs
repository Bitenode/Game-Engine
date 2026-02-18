#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>A single textured/colored quad emitted by a UIElement for batched rendering.</summary>
    public struct UIQuad
    {
        // Positions (screen/canvas space)
        public float X0, Y0, X1, Y1;
        // UVs
        public float U0, V0, U1, V1;
        // Vertex color (premultiplied with element tint and opacity)
        public float R, G, B, A;
        // GPU texture handle (0 = use white/solid). Set by CanvasRenderer from Texture field.
        public uint TextureHandle;
        // Engine texture to resolve to a GPU handle via ResourceCache (optional).
        public Texture2D? Texture;
        // True if this quad uses an SDF text shader
        public bool IsSDF;
    }

    /// <summary>Draw data returned by a UIElement for the CanvasRenderer to batch.</summary>
    public struct UIDrawData
    {
        public int QuadCount;
        public UIQuad[] Quads;

        public static readonly UIDrawData Empty = new() { QuadCount = 0, Quads = Array.Empty<UIQuad>() };
    }

    /// <summary>
    /// Abstract base class for all UI elements (Text, Image, Button, Panel, etc.).
    /// Requires a RectTransform for layout. Provides pointer-event callbacks and
    /// common visual properties.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public abstract class UIElement : Behavior
    {
        /// <summary>Whether this element can receive pointer events.</summary>
        [Persist] public bool Raycastable { get; set; } = true;

        /// <summary>Base tint color.</summary>
        [Persist] public Color Color { get; set; } = Colors.White;

        /// <summary>Opacity (0 = fully transparent, 1 = fully opaque).</summary>
        [Persist] public float Opacity { get; set; } = 1f;

        // ── Pointer event callbacks (virtual so concrete elements can react) ──

        /// <summary>Called when the pointer enters this element's rect.</summary>
        public virtual void OnPointerEnter() { }

        /// <summary>Called when the pointer leaves this element's rect.</summary>
        public virtual void OnPointerExit() { }

        /// <summary>Called when a pointer button is pressed over this element.</summary>
        public virtual void OnPointerDown() { }

        /// <summary>Called when a pointer button is released over this element.</summary>
        public virtual void OnPointerUp() { }

        /// <summary>Called when a click (press + release) occurs on this element.</summary>
        public virtual void OnPointerClick() { }

        /// <summary>Called each frame while the pointer is dragging over this element.</summary>
        public virtual void OnDrag(Vector2 delta) { }

        // ── Helpers for concrete element implementations ──

        /// <summary>Resolve the RectTransform on this GameObject.</summary>
        protected RectTransform? GetRectTransform()
            => GetComponent<RectTransform>();

        /// <summary>Walk up the hierarchy to find the owning Canvas.</summary>
        protected Canvas? GetCanvas()
        {
            var go = gameObject;
            while (go != null)
            {
                foreach (var b in go.Behaviors)
                    if (b is Canvas c && c.Enabled) return c;
                go = go.Parent;
            }
            return null;
        }

        /// <summary>
        /// Compute the final color with opacity applied.
        /// Returns (r, g, b, a) as 0-1 floats.
        /// </summary>
        public (float R, float G, float B, float A) GetFinalColor()
        {
            float a = (Color.A / 255f) * Opacity;
            return (Color.R / 255f, Color.G / 255f, Color.B / 255f, a);
        }

        // ── Draw data generation (override in concrete elements) ──

        /// <summary>Reusable quad buffer to avoid per-frame allocations.</summary>
        protected UIQuad[] _quadBuffer = new UIQuad[1];

        /// <summary>
        /// Generate draw data for the CanvasRenderer. Override in concrete elements
        /// to emit textured/multi-quad geometry. Default emits a single solid-color quad.
        /// </summary>
        public virtual UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            var (r, g, b, a) = GetFinalColor();
            if (a <= 0f) return UIDrawData.Empty;

            if (_quadBuffer.Length < 1) _quadBuffer = new UIQuad[1];
            _quadBuffer[0] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0f, V0 = 0f, U1 = 1f, V1 = 1f,
                R = r, G = g, B = b, A = a,
                TextureHandle = 0,
                IsSDF = false
            };
            return new UIDrawData { QuadCount = 1, Quads = _quadBuffer };
        }
    }
}
