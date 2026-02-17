#nullable enable
using System;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// UI Button element — an interactive button that responds to pointer events.
    /// Drives a sibling UIImage's color based on hover/press/disabled state.
    /// Exposes an OnClick event for scripting.
    /// </summary>
    [Require(typeof(RectTransform))]
    [Require(typeof(UIImage))]
    public sealed class UIButton : UIElement
    {
        /// <summary>Whether the button can be interacted with.</summary>
        [Persist] public bool Interactable { get; set; } = true;

        // ── Color transitions ──
        [Persist] public Color NormalColor { get; set; } = Color.FromRgb(0xFF, 0xFF, 0xFF);
        [Persist] public Color HighlightedColor { get; set; } = Color.FromRgb(0xE0, 0xE0, 0xE0);
        [Persist] public Color PressedColor { get; set; } = Color.FromRgb(0xB0, 0xB0, 0xB0);
        [Persist] public Color DisabledColor { get; set; } = Color.FromRgb(0x80, 0x80, 0x80);

        /// <summary>Color transition speed (0 = instant, higher = faster).</summary>
        [Persist] public float FadeDuration { get; set; } = 0.1f;

        // ── Events ──
        /// <summary>Fired when the button is clicked.</summary>
        public event Action? OnClick;

        // ── State ──
        private enum ButtonState { Normal, Highlighted, Pressed, Disabled }
        private ButtonState _state = ButtonState.Normal;
        private float _blendR, _blendG, _blendB;
        private bool _initialised;

        public override void OnEnable()
        {
            base.OnEnable();
            UpdateTargetColor(instant: true);
        }

        public override void Update()
        {
            if (!Interactable && _state != ButtonState.Disabled)
            {
                _state = ButtonState.Disabled;
                UpdateTargetColor(instant: false);
            }
            else if (Interactable && _state == ButtonState.Disabled)
            {
                _state = ButtonState.Normal;
                UpdateTargetColor(instant: false);
            }

            // Smooth color transition
            var target = GetTargetColor();
            float tr = target.R / 255f, tg = target.G / 255f, tb = target.B / 255f;

            if (!_initialised)
            {
                _blendR = tr; _blendG = tg; _blendB = tb;
                _initialised = true;
            }

            if (FadeDuration > 0f)
            {
                float t = Math.Min(1f, Core.Time.deltaTime / Math.Max(0.001f, FadeDuration));
                _blendR += (tr - _blendR) * t;
                _blendG += (tg - _blendG) * t;
                _blendB += (tb - _blendB) * t;
            }
            else
            {
                _blendR = tr; _blendG = tg; _blendB = tb;
            }

            // Apply to sibling UIImage
            var img = GetComponent<UIImage>();
            if (img != null)
            {
                img.Color = Color.FromArgb(img.Color.A,
                    (byte)Math.Clamp(_blendR * 255f, 0f, 255f),
                    (byte)Math.Clamp(_blendG * 255f, 0f, 255f),
                    (byte)Math.Clamp(_blendB * 255f, 0f, 255f));
            }
        }

        // ── Pointer event overrides ──

        public override void OnPointerEnter()
        {
            if (!Interactable) return;
            _state = ButtonState.Highlighted;
            UpdateTargetColor(instant: false);
        }

        public override void OnPointerExit()
        {
            if (!Interactable) return;
            _state = ButtonState.Normal;
            UpdateTargetColor(instant: false);
        }

        public override void OnPointerDown()
        {
            if (!Interactable) return;
            _state = ButtonState.Pressed;
            UpdateTargetColor(instant: false);
        }

        public override void OnPointerUp()
        {
            if (!Interactable) return;
            _state = ButtonState.Highlighted;
            UpdateTargetColor(instant: false);
        }

        public override void OnPointerClick()
        {
            if (!Interactable) return;
            try { OnClick?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "UIButton.OnClick"); }
        }

        // ── Don't emit draw data — the sibling UIImage handles rendering ──
        public override UIDrawData GetDrawData(in RectTransform.Rect rect) => UIDrawData.Empty;

        private Color GetTargetColor() => _state switch
        {
            ButtonState.Normal => NormalColor,
            ButtonState.Highlighted => HighlightedColor,
            ButtonState.Pressed => PressedColor,
            ButtonState.Disabled => DisabledColor,
            _ => NormalColor
        };

        private void UpdateTargetColor(bool instant)
        {
            if (instant)
            {
                var c = GetTargetColor();
                _blendR = c.R / 255f;
                _blendG = c.G / 255f;
                _blendB = c.B / 255f;
            }
        }
    }
}
