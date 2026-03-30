#nullable enable
using System;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// UI Toggle element — a checkbox/toggle that switches between on and off states.
    /// Renders a background box and a checkmark indicator.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIToggle : UIElement
    {
        /// <summary>Whether the toggle is currently on.</summary>
        [Persist] public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn != value)
                {
                    _isOn = value;
                    try { OnValueChanged?.Invoke(_isOn); }
                    catch (Exception ex) { Log.Error(ex, "UIToggle.OnValueChanged"); }
                }
            }
        }
        private bool _isOn = false;

        /// <summary>Whether the toggle can be interacted with.</summary>
        [Persist] public bool Interactable { get; set; } = true;

        // ── Colors ──
        /// <summary>Background color when off.</summary>
        [Persist] public Color BackgroundColor { get; set; } = Color.FromRgb(0x50, 0x50, 0x50);
        /// <summary>Background color when on.</summary>
        [Persist] public Color ActiveColor { get; set; } = Color.FromRgb(0x40, 0xA0, 0xFF);
        /// <summary>Checkmark/indicator color.</summary>
        [Persist] public Color CheckmarkColor { get; set; } = Colors.White;

        /// <summary>When alpha &gt; 0 and the toggle is interactable, blended over the background while hovered.</summary>
        [Persist] public Color HoverBackgroundColor { get; set; } = Color.FromArgb(0, 255, 255, 255);

        /// <summary>Inset of the checkmark relative to the toggle box (0-0.5).</summary>
        [Persist] public float CheckmarkInset { get; set; } = 0.15f;

        /// <summary>Fired when the toggle value changes.</summary>
        public event Action<bool>? OnValueChanged;

        // ── Pointer interaction ──

        public override void OnPointerClick()
        {
            if (!Interactable) return;
            IsOn = !IsOn;
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            if (_quadBuffer.Length < 2) _quadBuffer = new UIQuad[2];

            int qi = 0;

            // Background box
            var bgColor = _isOn ? ActiveColor : BackgroundColor;
            if (Interactable && IsPointerOver && HoverBackgroundColor.A > 0)
            {
                float t = HoverBackgroundColor.A / 255f;
                bgColor = LerpRgb(bgColor, HoverBackgroundColor, Math.Clamp(t, 0f, 1f));
            }

            float bgA = bgColor.A / 255f * Opacity;
            _quadBuffer[qi++] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                R = bgColor.R / 255f, G = bgColor.G / 255f, B = bgColor.B / 255f, A = bgA,
                TextureHandle = 0, IsSDF = false
            };

            // Checkmark (visible when on) — a smaller square inside the toggle
            if (_isOn)
            {
                float inset = Math.Clamp(CheckmarkInset, 0f, 0.45f);
                float ix = rect.Width * inset;
                float iy = rect.Height * inset;
                float cmA = CheckmarkColor.A / 255f * Opacity;

                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = rect.X + ix, Y0 = rect.Y + iy,
                    X1 = rect.X + rect.Width - ix, Y1 = rect.Y + rect.Height - iy,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = CheckmarkColor.R / 255f, G = CheckmarkColor.G / 255f, B = CheckmarkColor.B / 255f, A = cmA,
                    TextureHandle = 0, IsSDF = false
                };
            }

            return new UIDrawData { QuadCount = qi, Quads = _quadBuffer };
        }

        static Color LerpRgb(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            byte R = (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0f, 255f);
            byte G = (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0f, 255f);
            byte Bc = (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0f, 255f);
            byte A = (byte)Math.Clamp(a.A + (b.A - a.A) * t, 0f, 255f);
            return Color.FromArgb(A, R, G, Bc);
        }
    }
}
