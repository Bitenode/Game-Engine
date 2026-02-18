#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Defines a 2D rectangle for UI layout, with anchor-based positioning
    /// relative to a parent RectTransform (or the Canvas root).
    /// Lives alongside Transform on the same GameObject.
    /// </summary>
    [ComponentCategory("UI")]
    public sealed class RectTransform : Behavior
    {
        // ── Anchors (0-1 relative to parent rect) ──
        [Persist] public float AnchorMinX { get; set; } = 0.5f;
        [Persist] public float AnchorMinY { get; set; } = 0.5f;
        [Persist] public float AnchorMaxX { get; set; } = 0.5f;
        [Persist] public float AnchorMaxY { get; set; } = 0.5f;

        // ── Pivot (0-1 local origin) ──
        [Persist] public float PivotX { get; set; } = 0.5f;
        [Persist] public float PivotY { get; set; } = 0.5f;

        // ── Position offset from anchor centre (pixels) ──
        [Persist] public float AnchoredPositionX { get; set; } = 0f;
        [Persist] public float AnchoredPositionY { get; set; } = 0f;

        // ── Size delta — when anchors are together this is the absolute size;
        //    when anchors are apart this is the delta from the anchor-stretched size ──
        [Persist] public float SizeDeltaX { get; set; } = 160f;
        [Persist] public float SizeDeltaY { get; set; } = 40f;

        // ── 2D rotation and scale ──
        [Persist] public float Rotation2D { get; set; } = 0f;
        [Persist] public float ScaleX { get; set; } = 1f;
        [Persist] public float ScaleY { get; set; } = 1f;

        // ── Helpers ──

        public Vector2 AnchorMin => new(AnchorMinX, AnchorMinY);
        public Vector2 AnchorMax => new(AnchorMaxX, AnchorMaxY);
        public Vector2 Pivot => new(PivotX, PivotY);
        public Vector2 AnchoredPosition => new(AnchoredPositionX, AnchoredPositionY);
        public Vector2 SizeDelta => new(SizeDeltaX, SizeDeltaY);

        /// <summary>
        /// Compute this element's screen-space rectangle given the parent rectangle.
        /// Returns (x, y, width, height) where (x,y) is the bottom-left corner.
        /// </summary>
        public Rect GetRect(in Rect parentRect)
        {
            // Anchor positions inside parent
            float anchorLeftX = parentRect.X + AnchorMinX * parentRect.Width;
            float anchorRightX = parentRect.X + AnchorMaxX * parentRect.Width;
            float anchorBottomY = parentRect.Y + AnchorMinY * parentRect.Height;
            float anchorTopY = parentRect.Y + AnchorMaxY * parentRect.Height;

            float anchorWidth = anchorRightX - anchorLeftX;
            float anchorHeight = anchorTopY - anchorBottomY;

            // Final size
            float width = anchorWidth + SizeDeltaX;
            float height = anchorHeight + SizeDeltaY;

            // Anchor centre
            float anchorCentreX = (anchorLeftX + anchorRightX) * 0.5f;
            float anchorCentreY = (anchorBottomY + anchorTopY) * 0.5f;

            // Position relative to anchor centre, then shift by pivot
            float x = anchorCentreX + AnchoredPositionX - PivotX * width;
            float y = anchorCentreY + AnchoredPositionY - PivotY * height;

            return new Rect(x, y, Math.Max(0f, width), Math.Max(0f, height));
        }

        /// <summary>
        /// Walk up the hierarchy to compute the final screen-space rect.
        /// The root Canvas provides the initial parent rect.
        /// </summary>
        public Rect GetWorldRect(in Rect canvasRect)
        {
            // Build the parent chain
            var chain = new System.Collections.Generic.List<RectTransform>(8);
            var current = this;
            while (current != null)
            {
                chain.Add(current);
                current = current.gameObject?.Parent?.Behaviors
                    .OfType<RectTransform>().FirstOrDefault() as RectTransform;
            }

            // Evaluate from root to leaf
            var rect = canvasRect;
            for (int i = chain.Count - 1; i >= 0; i--)
                rect = chain[i].GetRect(in rect);

            return rect;
        }

        /// <summary>
        /// Returns the four corners of the rect in screen-space (or canvas-space).
        /// Order: bottom-left, top-left, top-right, bottom-right.
        /// </summary>
        public void GetWorldCorners(in Rect canvasRect, Span<Vector2> corners)
        {
            var r = GetWorldRect(in canvasRect);
            if (corners.Length < 4) return;

            float rad = Rotation2D * MathF.PI / 180f;
            float cos = MathF.Cos(rad);
            float sin = MathF.Sin(rad);

            // Pivot point in screen-space
            float px = r.X + PivotX * r.Width;
            float py = r.Y + PivotY * r.Height;

            Vector2 Rotate(float lx, float ly)
            {
                float dx = lx - px;
                float dy = ly - py;
                return new Vector2(
                    px + dx * cos - dy * sin,
                    py + dx * sin + dy * cos);
            }

            corners[0] = Rotate(r.X, r.Y);                          // bottom-left
            corners[1] = Rotate(r.X, r.Y + r.Height);               // top-left
            corners[2] = Rotate(r.X + r.Width, r.Y + r.Height);     // top-right
            corners[3] = Rotate(r.X + r.Width, r.Y);                // bottom-right
        }

        /// <summary>
        /// Returns true if the given screen-space point falls within this element's rect.
        /// Uses axis-aligned test (ignores rotation for simplicity; override for rotated rects).
        /// </summary>
        public bool ContainsScreenPoint(Vector2 point, in Rect canvasRect)
        {
            var r = GetWorldRect(in canvasRect);
            return point.X >= r.X && point.X <= r.X + r.Width &&
                   point.Y >= r.Y && point.Y <= r.Y + r.Height;
        }

        /// <summary>Simple axis-aligned rectangle.</summary>
        public readonly struct Rect
        {
            public readonly float X, Y, Width, Height;
            public Rect(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
        }
    }
}
