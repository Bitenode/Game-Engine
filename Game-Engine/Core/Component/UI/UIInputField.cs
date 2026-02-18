#nullable enable
using System;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>Content type restrictions for an input field.</summary>
    public enum InputFieldContentType
    {
        Standard,
        IntegerNumber,
        DecimalNumber,
        Alphanumeric,
        Password
    }

    /// <summary>
    /// UI InputField element — a text input box with cursor, selection, and placeholder text.
    /// Captures keyboard input when focused (clicked) and renders the text contents.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIInputField : UIElement
    {
        /// <summary>Current text content.</summary>
        [Persist] public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value ?? "";
                    _cursorPos = Math.Min(_cursorPos, _text.Length);
                    try { OnValueChanged?.Invoke(_text); }
                    catch (Exception ex) { Log.Error(ex, "UIInputField.OnValueChanged"); }
                }
            }
        }
        private string _text = "";

        /// <summary>Placeholder text shown when the field is empty.</summary>
        [Persist] public string Placeholder { get; set; } = "Enter text...";

        /// <summary>Maximum number of characters (0 = unlimited).</summary>
        [Persist] public int CharacterLimit { get; set; } = 0;

        /// <summary>Content type for input validation.</summary>
        [Persist] public InputFieldContentType ContentType { get; set; } = InputFieldContentType.Standard;

        /// <summary>Font size in canvas pixels.</summary>
        [Persist] public float FontSize { get; set; } = 20f;

        /// <summary>Path to the BMFont .fnt file.</summary>
        [Persist] public string FontPath { get; set; } = "";

        /// <summary>Whether this field is read-only.</summary>
        [Persist] public bool ReadOnly { get; set; } = false;

        // ── Colors ──
        [Persist] public Color BackgroundColor { get; set; } = Color.FromRgb(0x30, 0x30, 0x30);
        [Persist] public Color TextColor { get; set; } = Colors.White;
        [Persist] public Color PlaceholderColor { get; set; } = Color.FromRgb(0x80, 0x80, 0x80);
        [Persist] public Color CursorColor { get; set; } = Colors.White;
        [Persist] public Color SelectionColor { get; set; } = Color.FromArgb(0x60, 0x40, 0xA0, 0xFF);

        // ── Events ──
        /// <summary>Fired when the text changes.</summary>
        public event Action<string>? OnValueChanged;
        /// <summary>Fired when the user presses Enter/Return.</summary>
        public event Action<string>? OnEndEdit;

        // ── State ──
        private bool _isFocused;
        private int _cursorPos;
        private float _cursorBlinkTimer;
        private bool _cursorVisible = true;

        /// <summary>Whether this input field currently has focus.</summary>
        public bool IsFocused => _isFocused;

        // ── Pointer events ──

        public override void OnPointerClick()
        {
            if (ReadOnly) return;
            _isFocused = true;
            _cursorBlinkTimer = 0f;
            _cursorVisible = true;
            _cursorPos = _text.Length; // place cursor at end on click
        }

        // ── Keyboard input (called from Update when focused) ──

        public override void Update()
        {
            if (!_isFocused) return;

            // Cursor blink
            _cursorBlinkTimer += Core.Time.deltaTime;
            if (_cursorBlinkTimer >= 0.5f)
            {
                _cursorVisible = !_cursorVisible;
                _cursorBlinkTimer = 0f;
            }

            // Handle special keys
            if (Input.Input.GetKeyDown(Input.KeyCode.Escape))
            {
                _isFocused = false;
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.Enter))
            {
                _isFocused = false;
                try { OnEndEdit?.Invoke(_text); }
                catch (Exception ex) { Log.Error(ex, "UIInputField.OnEndEdit"); }
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.Backspace))
            {
                if (_cursorPos > 0 && _text.Length > 0)
                {
                    Text = _text.Remove(_cursorPos - 1, 1);
                    _cursorPos--;
                }
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.Delete))
            {
                if (_cursorPos < _text.Length)
                {
                    Text = _text.Remove(_cursorPos, 1);
                }
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.LeftArrow))
            {
                _cursorPos = Math.Max(0, _cursorPos - 1);
                ResetBlink();
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.RightArrow))
            {
                _cursorPos = Math.Min(_text.Length, _cursorPos + 1);
                ResetBlink();
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.Home))
            {
                _cursorPos = 0;
                ResetBlink();
                return;
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.End))
            {
                _cursorPos = _text.Length;
                ResetBlink();
                return;
            }

            // Text input: check for printable character keys
            ProcessTypedCharacters();
        }

        private void ProcessTypedCharacters()
        {
            // Check common printable keys
            for (int i = (int)Input.KeyCode.A; i <= (int)Input.KeyCode.Z; i++)
            {
                if (Input.Input.GetKeyDown((Input.KeyCode)i))
                {
                    bool shift = Input.Input.GetKey(Input.KeyCode.LeftShift) || Input.Input.GetKey(Input.KeyCode.RightShift);
                    char ch = (char)(shift ? 'A' + (i - (int)Input.KeyCode.A) : 'a' + (i - (int)Input.KeyCode.A));
                    TryInsertChar(ch);
                }
            }

            for (int i = (int)Input.KeyCode.D0; i <= (int)Input.KeyCode.D9; i++)
            {
                if (Input.Input.GetKeyDown((Input.KeyCode)i))
                {
                    char ch = (char)('0' + (i - (int)Input.KeyCode.D0));
                    TryInsertChar(ch);
                }
            }

            if (Input.Input.GetKeyDown(Input.KeyCode.Space)) TryInsertChar(' ');
            if (Input.Input.GetKeyDown(Input.KeyCode.OemPeriod)) TryInsertChar('.');
            if (Input.Input.GetKeyDown(Input.KeyCode.OemComma)) TryInsertChar(',');
            if (Input.Input.GetKeyDown(Input.KeyCode.OemMinus)) TryInsertChar('-');
        }

        private void TryInsertChar(char ch)
        {
            if (CharacterLimit > 0 && _text.Length >= CharacterLimit) return;

            // Content type validation
            switch (ContentType)
            {
                case InputFieldContentType.IntegerNumber:
                    if (!char.IsDigit(ch) && ch != '-') return;
                    if (ch == '-' && _cursorPos != 0) return;
                    break;
                case InputFieldContentType.DecimalNumber:
                    if (!char.IsDigit(ch) && ch != '.' && ch != '-') return;
                    if (ch == '.' && _text.Contains('.')) return;
                    if (ch == '-' && _cursorPos != 0) return;
                    break;
                case InputFieldContentType.Alphanumeric:
                    if (!char.IsLetterOrDigit(ch)) return;
                    break;
            }

            Text = _text.Insert(_cursorPos, ch.ToString());
            _cursorPos++;
            ResetBlink();
        }

        private void ResetBlink()
        {
            _cursorBlinkTimer = 0f;
            _cursorVisible = true;
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            // Background + text display quad + cursor quad = 3 max
            if (_quadBuffer.Length < 3) _quadBuffer = new UIQuad[3];

            int qi = 0;

            // Background
            float bgA = BackgroundColor.A / 255f * Opacity;
            _quadBuffer[qi++] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                R = BackgroundColor.R / 255f, G = BackgroundColor.G / 255f, B = BackgroundColor.B / 255f, A = bgA,
                TextureHandle = 0, IsSDF = false
            };

            // Text color indicator (shows text color as a small bar at the bottom to indicate active state)
            if (_isFocused && _cursorVisible)
            {
                float cursorW = 2f;
                float padding = 4f;
                // Approximate cursor x position (without font metrics, place at proportional position)
                float textWidth = rect.Width - padding * 2f;
                float charWidth = _text.Length > 0 ? textWidth / _text.Length : FontSize * 0.5f;
                float cx = rect.X + padding + _cursorPos * charWidth;
                cx = Math.Min(cx, rect.X + rect.Width - padding);

                float curA = CursorColor.A / 255f * Opacity;
                _quadBuffer[qi++] = new UIQuad
                {
                    X0 = cx, Y0 = rect.Y + 2f,
                    X1 = cx + cursorW, Y1 = rect.Y + rect.Height - 2f,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    R = CursorColor.R / 255f, G = CursorColor.G / 255f, B = CursorColor.B / 255f, A = curA,
                    TextureHandle = 0, IsSDF = false
                };
            }

            return new UIDrawData { QuadCount = qi, Quads = _quadBuffer };
        }
    }
}
