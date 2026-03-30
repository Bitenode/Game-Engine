#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Game_Engine.Core.Component.UI;
using Game_Engine.Core.Input;

namespace Game_Engine.Core.Rendering.UI
{
    /// <summary>
    /// Processes pointer input each frame and delivers events to UI elements.
    /// Handles raycasting against RectTransforms in screen-space canvases.
    /// Call ProcessEvents() once per frame before Update().
    /// </summary>
    public static class UIEventSystem
    {
        private static UIElement? _hoveredElement;
        private static UIElement? _pressedElement;
        private static Vector2 _pressPosition;
        private static bool _isDragging;

        // Track mouse held state ourselves so we derive press/release edges
        // from Input.GetMouse() (held-state), which is always reliable regardless
        // of when NewFrame/EndFrame clear the per-frame edge sets.
        private static bool _wasMouseDown;

        /// <summary>The element currently under the pointer (null if none).</summary>
        public static UIElement? HoveredElement => _hoveredElement;

        /// <summary>
        /// Returns true if the UI consumed a click this frame (the game should ignore it).
        /// </summary>
        public static bool PointerOverUI => _hoveredElement != null;

        /// <summary>
        /// Process pointer input for all active screen-space overlay canvases.
        /// <paramref name="viewportWidth"/> and <paramref name="viewportHeight"/> must
        /// be in the same coordinate space as <see cref="Input.Input.MousePosition"/>
        /// (logical / DIP pixels).
        /// </summary>
        public static void ProcessEvents(float viewportWidth, float viewportHeight)
        {
            var mousePos = Input.Input.MousePosition;

            // Convert from top-left origin (Avalonia) to bottom-left origin (GL/Canvas)
            var canvasMousePos = new Vector2(mousePos.X, viewportHeight - mousePos.Y);

            // Raycast: find the topmost raycastable element under the pointer
            UIElement? hit = null;

            // Sort canvases by SortOrder descending (front-to-back) for hit testing
            var canvases = Canvas.All
                .Where(c => c.IsActiveAndEnabled && c.RenderMode == CanvasRenderMode.ScreenSpaceOverlay)
                .OrderByDescending(c => c.SortOrder)
                .ToList();

            foreach (var canvas in canvases)
            {
                var canvasRect = canvas.GetCanvasRect(viewportWidth, viewportHeight);
                float scale = canvas.GetScaleFactor(viewportWidth, viewportHeight);

                // Convert mouse position from screen pixels to canvas coordinates
                var canvasPoint = new Vector2(canvasMousePos.X / scale, canvasMousePos.Y / scale);

                hit = RaycastCanvas(canvas, canvasPoint, in canvasRect);
                if (hit != null)
                    break;
            }

            // ── Hover events ──
            if (hit != _hoveredElement)
            {
                _hoveredElement?.SyncPointerHover(false);
                _hoveredElement?.OnPointerExit();
                _hoveredElement = hit;
                _hoveredElement?.SyncPointerHover(true);
                _hoveredElement?.OnPointerEnter();
            }

            // ── Derive press/release edges from the held state ──
            // Input.GetMouse reads sHeldMouse which is always accurate — it is
            // set by FeedMouseButtonDown and cleared by FeedMouseButtonUp, and is
            // never reset by NewFrame or EndFrame.
            bool mouseIsDown = Input.Input.GetMouse(MouseButton.Left);
            bool mouseDown = mouseIsDown && !_wasMouseDown;
            bool mouseUp = !mouseIsDown && _wasMouseDown;
            _wasMouseDown = mouseIsDown;

            if (mouseDown)
            {
                UIInputField.ApplyDeselectOnPointerDown(hit);

                if (hit != null)
                {
                    _pressedElement = hit;
                    _pressPosition = canvasMousePos;
                    _isDragging = false;
                    hit.SyncPointerPressed(true);
                    hit.OnPointerDown();
                }
            }

            if (mouseUp)
            {
                if (_pressedElement != null)
                {
                    _pressedElement.SyncPointerPressed(false);
                    _pressedElement.OnPointerUp();

                    // Click: release on the same element that was pressed
                    if (_pressedElement == hit && !_isDragging)
                        _pressedElement.OnPointerClick();

                    _pressedElement = null;
                    _isDragging = false;
                }
            }

            // ── Drag ──
            if (_pressedElement != null && mouseIsDown)
            {
                float dist = Vector2.Distance(canvasMousePos, _pressPosition);
                if (dist > 3f) // drag threshold
                {
                    _isDragging = true;
                    var delta = Input.Input.MouseDelta;
                    _pressedElement.OnDrag(new Vector2(delta.X, delta.Y));
                }
            }
        }

        /// <summary>
        /// Raycast a single canvas hierarchy, returning the topmost hit element.
        /// Uses reverse depth-first traversal (last child drawn on top = tested first).
        /// </summary>
        private static UIElement? RaycastCanvas(Canvas canvas, Vector2 point, in RectTransform.Rect canvasRect)
        {
            var go = canvas.gameObject;
            if (go == null) return null;

            return RaycastHierarchy(go, point, in canvasRect);
        }

        private static UIElement? RaycastHierarchy(GameObject go, Vector2 point, in RectTransform.Rect canvasRect)
        {
            if (!go.Enabled) return null;

            // Children are drawn after parents (on top), so check children first (reverse order = topmost first)
            for (int i = go.Children.Count - 1; i >= 0; i--)
            {
                var result = RaycastHierarchy(go.Children[i], point, in canvasRect);
                if (result != null) return result;
            }

            // Check this object's UI elements
            foreach (var b in go.Behaviors)
            {
                if (b is UIElement element && element.Enabled && element.Raycastable)
                {
                    var rt = go.Behaviors.OfType<RectTransform>().FirstOrDefault();
                    if (rt != null && rt.ContainsScreenPoint(point, in canvasRect))
                        return element;
                }
            }

            return null;
        }

        /// <summary>Reset event system state (e.g., when switching scenes or stopping play mode).</summary>
        public static void Reset()
        {
            _hoveredElement?.SyncPointerHover(false);
            _pressedElement?.SyncPointerPressed(false);
            _hoveredElement = null;
            _pressedElement = null;
            _isDragging = false;
            _wasMouseDown = false;
        }
    }
}
