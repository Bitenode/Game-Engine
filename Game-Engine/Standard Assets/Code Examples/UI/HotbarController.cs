#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using Game_Engine.Core.Input;
using EngineInput = Game_Engine.Core.Input.Input;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Bottom-of-screen item hotbar driven by Canvas UI widgets (panel, image, button, text, progress bar).
    /// Attach to the root of the Standard Assets <c>Hotbar.prefab</c> (or any matching hierarchy).
    /// Number keys 1–9 (and 0 for slot 10) plus slot clicks change the selection.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(Canvas))]
    [Require(typeof(RectTransform))]
    public sealed class HotbarController : Behavior
    {
        public const int MaxSlots = 10;

        [Persist] public string BarName { get; set; } = "Bar";
        [Persist] public string SlotNamePrefix { get; set; } = "Slot ";

        /// <summary>How many slots to show (1–10). Extra prefab slots are hidden; missing slots are left unused.</summary>
        [Persist, Range(1, 10)]
        public int SlotCount { get; set; } = 9;

        [Persist] public bool UseNumberKeys { get; set; } = true;

        /// <summary>
        /// When true, <see cref="Start"/> fills the first five slots with colored placeholders
        /// so the prefab looks populated without item textures.
        /// </summary>
        [Persist] public bool PopulateDemoItems { get; set; }

        [Persist] public Color SlotNormalColor { get; set; } = Color.FromRgb(0x1E, 0x28, 0x36);
        [Persist] public Color SlotHighlightedColor { get; set; } = Color.FromRgb(0x3A, 0x5A, 0x7A);
        [Persist] public Color SlotPressedColor { get; set; } = Color.FromRgb(0x16, 0x20, 0x30);
        [Persist] public Color SlotSelectedColor { get; set; } = Color.FromRgb(0x2A, 0x4A, 0x6A);
        [Persist] public Color SlotSelectedHighlightColor { get; set; } = Color.FromRgb(0x4A, 0x7A, 0xA8);

        int _selectedIndex;
        Canvas? _canvas;
        bool _wasMouseDown;
        readonly bool[] _digitWasDown = new bool[MaxSlots];
        SlotView[] _slots = Array.Empty<SlotView>();
        readonly List<(UIButton Button, Action Handler)> _clickBindings = new();

        /// <summary>Zero-based selected slot. Clamped to the bound slot count.</summary>
        [Persist]
        public int SelectedIndex
        {
            get => _selectedIndex;
            set => Select(value);
        }

        /// <summary>Number of slots currently bound from the hierarchy.</summary>
        public int BoundSlotCount => _slots.Length;

        /// <summary>Fired after the selected slot changes (argument is the new zero-based index).</summary>
        public event Action<int>? SelectionChanged;

        public override void Start()
        {
            _canvas = GetComponent<Canvas>();
            BindSlots();
            if (PopulateDemoItems)
                FillDemoItems();
            ApplySelectionVisuals();
        }

        public override void Update()
        {
            if (_slots.Length == 0) return;

            if (UseNumberKeys)
                HandleNumberKeys();

            HandlePointerClicks();
        }

        public override void OnDestroy()
        {
            UnbindClicks();
            base.OnDestroy();
        }

        /// <summary>Select a slot by zero-based index. No-ops when the index is unchanged or out of range.</summary>
        public void Select(int index)
        {
            if (_slots.Length == 0)
            {
                _selectedIndex = Math.Clamp(index, 0, Math.Max(0, SlotCount - 1));
                return;
            }

            int clamped = Math.Clamp(index, 0, _slots.Length - 1);
            if (clamped == _selectedIndex)
            {
                ApplySelectionVisuals();
                return;
            }

            _selectedIndex = clamped;
            ApplySelectionVisuals();
            try { SelectionChanged?.Invoke(_selectedIndex); }
            catch (Exception ex) { LogError(ex, "SelectionChanged"); }
        }

        /// <summary>Set the icon sprite for a slot. An empty path leaves a solid tinted square.</summary>
        public void SetSlotIcon(int index, string? spritePath, Color? tint = null)
        {
            if (!TryGet(index, out var slot) || slot.Icon == null) return;
            slot.Icon.SpritePath = spritePath ?? "";
            if (tint.HasValue)
                slot.Icon.Color = tint.Value;
            slot.Icon.Opacity = 1f;
        }

        /// <summary>Clear the icon (hides the image) on a slot.</summary>
        public void ClearSlotIcon(int index)
        {
            if (!TryGet(index, out var slot) || slot.Icon == null) return;
            slot.Icon.SpritePath = "";
            slot.Icon.Opacity = 0f;
        }

        /// <summary>Stack count label. Values ≤ 1 hide the label (hotbar convention).</summary>
        public void SetSlotCount(int index, int count)
        {
            if (!TryGet(index, out var slot) || slot.Count == null) return;
            if (count > 1)
            {
                slot.Count.Text = count.ToString();
                slot.Count.Opacity = 1f;
            }
            else
            {
                slot.Count.Text = "";
                slot.Count.Opacity = 0f;
            }
        }

        /// <summary>Cooldown overlay, 0 = none, 1 = full cover (uses a child <see cref="UIProgressBar"/>).</summary>
        public void SetSlotCooldown(int index, float normalized)
        {
            if (!TryGet(index, out var slot) || slot.Cooldown == null) return;
            slot.Cooldown.Value = Math.Clamp(normalized, 0f, 1f);
        }

        /// <summary>Optional label under the key hint (item name). Empty hides it.</summary>
        public void SetSlotLabel(int index, string? text)
        {
            if (!TryGet(index, out var slot) || slot.Label == null) return;
            slot.Label.Text = text ?? "";
            slot.Label.Opacity = string.IsNullOrEmpty(text) ? 0f : 1f;
        }

        public bool TryGetSlotName(int index, out string? name)
        {
            name = null;
            if (!TryGet(index, out var slot)) return false;
            name = slot.Root.Name;
            return true;
        }

        /// <summary>Sample placeholder items (sword / pick / torch / bread / potion).</summary>
        public void FillDemoItems()
        {
            if (_slots.Length == 0) BindSlots();
            ClearAllSlots();

            SetSlotIcon(0, "", Color.FromRgb(0xC4, 0x5C, 0x4A));
            SetSlotCount(0, 1);
            SetSlotLabel(0, "");

            SetSlotIcon(1, "", Color.FromRgb(0x8B, 0x73, 0x55));
            SetSlotCount(1, 1);

            SetSlotIcon(2, "", Color.FromRgb(0xE8, 0xA0, 0x30));
            SetSlotCount(2, 16);

            SetSlotIcon(3, "", Color.FromRgb(0xD4, 0xA0, 0x56));
            SetSlotCount(3, 8);

            SetSlotIcon(4, "", Color.FromRgb(0x5B, 0x8D, 0xEF));
            SetSlotCount(4, 3);
        }

        public void ClearAllSlots()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ClearSlotIcon(i);
                SetSlotCount(i, 0);
                SetSlotCooldown(i, 0f);
                SetSlotLabel(i, "");
            }
        }

        void HandleNumberKeys()
        {
            if (UIInputField.AnyFocused) return;

            int n = _slots.Length;
            for (int i = 0; i < n && i < 9; i++)
            {
                bool down = EngineInput.GetKey((KeyCode)((int)KeyCode.D1 + i))
                            || EngineInput.GetKey((KeyCode)((int)KeyCode.NumPad1 + i));
                if (down && !_digitWasDown[i])
                    Select(i);
                _digitWasDown[i] = down;
            }

            for (int i = n; i < 9; i++)
                _digitWasDown[i] = false;

            if (n >= 10)
            {
                bool down = EngineInput.GetKey(KeyCode.D0) || EngineInput.GetKey(KeyCode.NumPad0);
                if (down && !_digitWasDown[9])
                    Select(9);
                _digitWasDown[9] = down;
            }
            else
                _digitWasDown[9] = false;
        }

        void HandlePointerClicks()
        {
            bool mouseIsDown = EngineInput.GetMouse(MouseButton.Left);
            bool clicked = mouseIsDown && !_wasMouseDown;
            _wasMouseDown = mouseIsDown;
            if (!clicked || _canvas == null) return;

            var vp = EngineInput.ViewportSize;
            if (vp.X <= 0f || vp.Y <= 0f) return;

            var canvasRect = _canvas.GetCanvasRect(vp.X, vp.Y);
            float scale = _canvas.GetScaleFactor(vp.X, vp.Y);
            var mousePos = EngineInput.MousePosition;
            var canvasPoint = new Vector2(mousePos.X / scale, (vp.Y - mousePos.Y) / scale);

            for (int i = 0; i < _slots.Length; i++)
            {
                var rt = _slots[i].Rect;
                if (rt != null && rt.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    Select(i);
                    return;
                }
            }
        }

        void BindSlots()
        {
            UnbindClicks();
            _slots = Array.Empty<SlotView>();

            var root = gameObject;
            if (root == null) return;

            var bar = FindChild(root, BarName) ?? root;
            int wanted = Math.Clamp(SlotCount, 1, MaxSlots);
            var list = new List<SlotView>(wanted);

            for (int i = 0; i < wanted; i++)
            {
                var go = FindChild(bar, SlotNamePrefix + (i + 1));
                if (go == null)
                    go = FindChild(root, SlotNamePrefix + (i + 1));
                if (go == null)
                    break;

                go.Enabled = true;
                var view = SlotView.From(go);
                // UIButton is the click target; the sibling UIImage only draws the slot chrome.
                if (view.Background != null)
                    view.Background.Raycastable = false;
                list.Add(view);

                if (view.Button != null)
                {
                    int captured = i;
                    Action handler = () => Select(captured);
                    view.Button.OnClick += handler;
                    _clickBindings.Add((view.Button, handler));
                }
            }

            // Hide leftover prefab slots when SlotCount is reduced.
            for (int i = list.Count; i < MaxSlots; i++)
            {
                var extra = FindChild(bar, SlotNamePrefix + (i + 1))
                            ?? FindChild(root, SlotNamePrefix + (i + 1));
                if (extra != null)
                    extra.Enabled = false;
            }

            _slots = list.ToArray();
            if (_slots.Length == 0)
            {
                LogWarning($"No slots found (expected '{SlotNamePrefix}1'.. under '{BarName}').");
                return;
            }

            _selectedIndex = Math.Clamp(_selectedIndex, 0, _slots.Length - 1);
        }

        void ApplySelectionVisuals()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                bool on = i == _selectedIndex;

                if (slot.Selection != null)
                    slot.Selection.Enabled = on;

                if (slot.SelectionGo != null)
                    slot.SelectionGo.Enabled = on;

                if (slot.Button != null)
                {
                    slot.Button.NormalColor = on ? SlotSelectedColor : SlotNormalColor;
                    slot.Button.HighlightedColor = on ? SlotSelectedHighlightColor : SlotHighlightedColor;
                    slot.Button.PressedColor = SlotPressedColor;
                }
                else if (slot.Background != null)
                {
                    slot.Background.Color = on ? SlotSelectedColor : SlotNormalColor;
                }
            }
        }

        void UnbindClicks()
        {
            foreach (var (button, handler) in _clickBindings)
                button.OnClick -= handler;
            _clickBindings.Clear();
        }

        bool TryGet(int index, out SlotView slot)
        {
            slot = default!;
            if ((uint)index >= (uint)_slots.Length) return false;
            slot = _slots[index];
            return true;
        }

        static GameObject? FindChild(GameObject? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.Children)
            {
                if (child.Name == name) return child;
                var found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        static T? BehaviorOf<T>(GameObject? go) where T : Behavior
        {
            if (go == null) return null;
            foreach (var b in go.Behaviors)
                if (b is T typed) return typed;
            return null;
        }

        sealed class SlotView
        {
            public GameObject Root = null!;
            public RectTransform? Rect;
            public UIButton? Button;
            public UIImage? Background;
            public UIImage? Icon;
            public UIText? Count;
            public UIText? KeyHint;
            public UIText? Label;
            public UIProgressBar? Cooldown;
            public UIElement? Selection;
            public GameObject? SelectionGo;

            public static SlotView From(GameObject go)
            {
                var iconGo = FindChild(go, "Icon");
                var countGo = FindChild(go, "Count");
                var keyGo = FindChild(go, "KeyHint");
                var labelGo = FindChild(go, "Label");
                var cdGo = FindChild(go, "Cooldown");
                var selGo = FindChild(go, "Selection");

                return new SlotView
                {
                    Root = go,
                    Rect = BehaviorOf<RectTransform>(go),
                    Button = BehaviorOf<UIButton>(go),
                    Background = BehaviorOf<UIImage>(go),
                    Icon = BehaviorOf<UIImage>(iconGo),
                    Count = BehaviorOf<UIText>(countGo),
                    KeyHint = BehaviorOf<UIText>(keyGo),
                    Label = BehaviorOf<UIText>(labelGo),
                    Cooldown = BehaviorOf<UIProgressBar>(cdGo),
                    Selection = BehaviorOf<UIElement>(selGo),
                    SelectionGo = selGo
                };
            }
        }
    }
}
