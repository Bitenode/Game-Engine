#nullable enable
using System;
using System.Numerics;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>Slider direction.</summary>
    public enum SliderDirection { LeftToRight, RightToLeft, BottomToTop, TopToBottom }

    /// <summary>
    /// UI Slider element — a draggable slider for selecting a value in a range.
    /// Renders a background track and a filled portion based on the current value.
    /// </summary>
    [Require(typeof(RectTransform))]
    public sealed class UISlider : UIElement
    {
        /// <summary>Minimum value of the slider.</summary>
        [Persist] public float MinValue { get; set; } = 0f;

        /// <summary>Maximum value of the slider.</summary>
        [Persist] public float MaxValue { get; set; } = 1f;

        /// <summary>Current value of the slider.</summary>
        [Persist] public float Value
        {
            get => _value;
            set
            {
                float clamped = Math.Clamp(value, MinValue, MaxValue);
                if (Math.Abs(_value - clamped) > 1e-6f)
                {
                    _value = clamped;
                    try { OnValueChanged?.Invoke(_value); }
                    catch (Exception ex) { Log.Error(ex, "UISlider.OnValueChanged"); }
                }
            }
        }
        private float _value = 0f;

        /// <summary>Whether to restrict value to whole numbers.</summary>
        [Persist] public bool WholeNumbers { get; set; } = false;

        /// <summary>Slider direction.</summary>
        [Persist] public SliderDirection Direction { get; set; } = SliderDirection.LeftToRight;

        // ── Colors ──
        /// <summary>Background track color.</summary>
        [Persist] public Color BackgroundColor { get; set; } = Color.FromRgb(0x40, 0x40, 0x40);
        /// <summary>Fill bar color.</summary>
        [Persist] public Color FillColor { get; set; } = Color.FromRgb(0x40, 0xA0, 0xFF);
        /// <summary>Handle color.</summary>
        [Persist] public Color HandleColor { get; set; } = Colors.White;
        /// <summary>Handle size as fraction of slider height (0-1).</summary>
        [Persist] public float HandleSize { get; set; } = 0.8f;

        /// <summary>Fired when the value changes.</summary>
        public event Action<float>? OnValueChanged;

        /// <summary>Normalised value (0-1).</summary>
        public float NormalizedValue
        {
            get => MaxValue > MinValue ? (_value - MinValue) / (MaxValue - MinValue) : 0f;
            set => Value = MinValue + Math.Clamp(value, 0f, 1f) * (MaxValue - MinValue);
        }

        // ── Pointer interaction ──

        public override void OnPointerDown()
        {
            UpdateValueFromPointer();
        }

        public override void OnDrag(Vector2 delta)
        {
            UpdateValueFromPointer();
        }

        private void UpdateValueFromPointer()
        {
            var rt = GetRectTransform();
            var canvas = GetCanvas();
            if (rt == null || canvas == null) return;

            // Use actual viewport size (DIP space, matching Input.MousePosition)
            var vp = Input.Input.ViewportSize;
            float vpW = vp.X > 0 ? vp.X : canvas.ReferenceResolutionX;
            float vpH = vp.Y > 0 ? vp.Y : canvas.ReferenceResolutionY;
            var canvasRect = canvas.GetCanvasRect(vpW, vpH);
            var rect = rt.GetWorldRect(in canvasRect);

            var mousePos = Input.Input.MousePosition;
            // Convert from screen-space top-left to bottom-left canvas coords
            float scale = canvas.GetScaleFactor(vpW, vpH);
            float mx = mousePos.X / scale;
            float my = (vpH - mousePos.Y) / scale;

            float normalised;
            bool horizontal = Direction == SliderDirection.LeftToRight || Direction == SliderDirection.RightToLeft;

            if (horizontal)
            {
                normalised = rect.Width > 0 ? Math.Clamp((mx - rect.X) / rect.Width, 0f, 1f) : 0f;
                if (Direction == SliderDirection.RightToLeft) normalised = 1f - normalised;
            }
            else
            {
                normalised = rect.Height > 0 ? Math.Clamp((my - rect.Y) / rect.Height, 0f, 1f) : 0f;
                if (Direction == SliderDirection.TopToBottom) normalised = 1f - normalised;
            }

            NormalizedValue = normalised;
            if (WholeNumbers) Value = MathF.Round(Value);
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            if (_quadBuffer.Length < 3) _quadBuffer = new UIQuad[3];

            float norm = NormalizedValue;
            int qi = 0;

            // Background track
            float bgA = BackgroundColor.A / 255f * Opacity;
            _quadBuffer[qi++] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                R = BackgroundColor.R / 255f, G = BackgroundColor.G / 255f, B = BackgroundColor.B / 255f, A = bgA,
                TextureHandle = 0, IsSDF = false
            };

            // Fill bar
            float fillA = FillColor.A / 255f * Opacity;
            bool horizontal = Direction == SliderDirection.LeftToRight || Direction == SliderDirection.RightToLeft;

            if (horizontal)
            {
                float fillW = rect.Width * norm;
                float fx0 = Direction == SliderDirection.LeftToRight ? rect.X : rect.X + rect.Width - fillW;
                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = fx0, Y0 = rect.Y,
                    X1 = fx0 + fillW, Y1 = rect.Y + rect.Height,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = FillColor.R / 255f, G = FillColor.G / 255f, B = FillColor.B / 255f, A = fillA,
                    TextureHandle = 0, IsSDF = false
                };
            }
            else
            {
                float fillH = rect.Height * norm;
                float fy0 = Direction == SliderDirection.BottomToTop ? rect.Y : rect.Y + rect.Height - fillH;
                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = rect.X, Y0 = fy0,
                    X1 = rect.X + rect.Width, Y1 = fy0 + fillH,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = FillColor.R / 255f, G = FillColor.G / 255f, B = FillColor.B / 255f, A = fillA,
                    TextureHandle = 0, IsSDF = false
                };
            }

            // Handle (square knob at the current value position)
            float handleA = HandleColor.A / 255f * Opacity;
            float hs = horizontal ? rect.Height * HandleSize : rect.Width * HandleSize;

            if (horizontal)
            {
                float hx = Direction == SliderDirection.LeftToRight
                    ? rect.X + rect.Width * norm - hs * 0.5f
                    : rect.X + rect.Width * (1f - norm) - hs * 0.5f;
                float hy = rect.Y + (rect.Height - hs) * 0.5f;
                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = hx, Y0 = hy, X1 = hx + hs, Y1 = hy + hs,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = HandleColor.R / 255f, G = HandleColor.G / 255f, B = HandleColor.B / 255f, A = handleA,
                    TextureHandle = 0, IsSDF = false
                };
            }
            else
            {
                float hy = Direction == SliderDirection.BottomToTop
                    ? rect.Y + rect.Height * norm - hs * 0.5f
                    : rect.Y + rect.Height * (1f - norm) - hs * 0.5f;
                float hx = rect.X + (rect.Width - hs) * 0.5f;
                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = hx, Y0 = hy, X1 = hx + hs, Y1 = hy + hs,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = HandleColor.R / 255f, G = HandleColor.G / 255f, B = HandleColor.B / 255f, A = handleA,
                    TextureHandle = 0, IsSDF = false
                };
            }

            return new UIDrawData { QuadCount = qi, Quads = _quadBuffer };
        }
    }
}
