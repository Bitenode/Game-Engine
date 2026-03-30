#nullable enable
using System;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Non-interactive progress indicator. Renders a track and fill (same layout rules as <see cref="UISlider"/>).
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIProgressBar : UIElement
    {
        [Persist] public float MinValue { get; set; }
        [Persist] public float MaxValue { get; set; } = 1f;
        [Persist] public float Value { get; set; } = 0.5f;

        [Persist] public SliderDirection Direction { get; set; } = SliderDirection.LeftToRight;

        [Persist] public Color BackgroundColor { get; set; } = Color.FromRgb(0x40, 0x40, 0x40);
        [Persist] public Color FillColor { get; set; } = Color.FromRgb(0x40, 0xA0, 0xFF);

        /// <summary>Fill amount in 0–1 range (clamped).</summary>
        public float NormalizedValue
        {
            get
            {
                float min = Math.Min(MinValue, MaxValue);
                float max = Math.Max(MinValue, MaxValue);
                float v = Math.Clamp(Value, min, max);
                return max > min ? (v - min) / (max - min) : 0f;
            }
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            float norm = NormalizedValue;
            if (_quadBuffer.Length < 2) _quadBuffer = new UIQuad[2];
            int qi = 0;

            float bgA = BackgroundColor.A / 255f * Opacity;
            _quadBuffer[qi++] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                R = BackgroundColor.R / 255f, G = BackgroundColor.G / 255f, B = BackgroundColor.B / 255f, A = bgA,
                TextureHandle = 0, IsSDF = false
            };

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

            return new UIDrawData { QuadCount = qi, Quads = _quadBuffer };
        }
    }
}
