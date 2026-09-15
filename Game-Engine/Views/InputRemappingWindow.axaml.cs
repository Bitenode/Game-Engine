using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;
using Game_Engine.Core.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Threading;
using System.Linq;
using static Game_Engine.Core.Input.Input;

namespace Game_Engine.Views
{
    public partial class InputRemappingWindow : Window
    {
        private enum WaitingKind { None, AxisPositive, AxisNegative, AxisPositivePad, AxisNegativePad, AxisAnalog, ActionKey, ActionMouse, ActionGamepad }
        private WaitingKind _waiting = WaitingKind.None;
        private string? _targetName;

        private readonly DispatcherTimer _padTimer;

        public InputRemappingWindow()
        {
            InitializeComponent();

            if (ProjectService.Current != null)
                Input.TryLoadBindingsFromProject();

            DeadzoneBox.Text = Input.GamepadDeadzone.ToString("0.##", CultureInfo.InvariantCulture);
            LookScaleBox.Text = Input.GamepadLookSensitivity.ToString("0.##", CultureInfo.InvariantCulture);
            DeadzoneBox.LostFocus += (_, __) => CommitScalarFields();
            LookScaleBox.LostFocus += (_, __) => CommitScalarFields();

            BuildAxesUI();
            BuildActionsUI();
            UpdatePadStatus();

            BtnReset.Click += OnResetClicked;
            BtnClose.Click += (_, __) => Close();
            BtnSave.Click += OnSaveClicked;
            AddActionBtn.Click += OnAddActionClicked;

            KeyDown += OnHostKeyDown;
            PointerPressed += OnHostPointerPressed;

            _padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _padTimer.Tick += OnPadTimerTick;
            _padTimer.Start();

            Closed += (_, __) => _padTimer.Stop();

            UpdateTitleWithPath();
        }

        void CommitScalarFields()
        {
            if (float.TryParse(DeadzoneBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dz))
                Input.GamepadDeadzone = Math.Clamp(dz, 0f, 0.9f);
            if (float.TryParse(LookScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var look))
                Input.GamepadLookSensitivity = Math.Clamp(look, 0f, 80f);
        }

        void OnPadTimerTick(object? sender, EventArgs e)
        {
            if (!GameView.IsAnyViewPlaying)
                Input.NewFrame(0.016f);
            Input.PollGamepads();
            UpdatePadStatus();
            if (_waiting == WaitingKind.None) return;

            if (_waiting == WaitingKind.ActionGamepad ||
                _waiting == WaitingKind.AxisPositivePad ||
                _waiting == WaitingKind.AxisNegativePad)
            {
                foreach (GamepadButton b in Enum.GetValues<GamepadButton>())
                {
                    if (b == GamepadButton.None) continue;
                    if (!Input.GetGamepadButtonDown(b)) continue;
                    if (_waiting == WaitingKind.ActionGamepad)
                    {
                        var info = Input.GetActionInfo(_targetName!);
                        if (info != null && !info.GamepadButtons.Contains(b))
                        {
                            info.GamepadButtons.Add(b);
                            Input.ApplyActionInfo(info);
                        }
                        BuildActionsUI();
                    }
                    else
                    {
                        var info = Input.GetAxisInfo(_targetName!);
                        if (info != null)
                        {
                            var list = _waiting == WaitingKind.AxisPositivePad ? info.PositiveButtons : info.NegativeButtons;
                            if (!list.Contains(b)) list.Add(b);
                            Input.ApplyAxisInfo(info);
                        }
                        BuildAxesUI();
                    }
                    CancelWaiting();
                    return;
                }
            }

            if (_waiting == WaitingKind.AxisAnalog)
            {
                GamepadAxis picked = GamepadAxis.None;
                float best = 0.6f;
                TryStick(GamepadAxis.LeftStickX, ref picked, ref best);
                TryStick(GamepadAxis.LeftStickY, ref picked, ref best);
                TryStick(GamepadAxis.RightStickX, ref picked, ref best);
                TryStick(GamepadAxis.RightStickY, ref picked, ref best);
                float lt = Input.GetGamepadAxis(GamepadAxis.LeftTrigger);
                float rt = Input.GetGamepadAxis(GamepadAxis.RightTrigger);
                if (lt > best) { best = lt; picked = GamepadAxis.LeftTrigger; }
                if (rt > best) { picked = GamepadAxis.RightTrigger; }

                if (picked != GamepadAxis.None)
                {
                    var info = Input.GetAxisInfo(_targetName);
                    if (info != null)
                    {
                        info.AnalogAxis = picked;
                        Input.ApplyAxisInfo(info);
                    }
                    BuildAxesUI();
                    CancelWaiting();
                }
            }
        }

        static void TryStick(GamepadAxis axis, ref GamepadAxis picked, ref float best)
        {
            float a = Math.Abs(Input.GetGamepadAxis(axis));
            if (a > best)
            {
                best = a;
                picked = axis;
            }
        }

        void UpdatePadStatus()
        {
            int n = Input.ConnectedGamepadCount;
            PadStatusText.Text = n > 0
                ? $"Gamepad: {n} connected (XInput). Left stick = move, right stick = look, A = Jump, RT/RB = Fire1, X = Interact, B = Crouch."
                : "Gamepad: none (connect an Xbox-compatible pad; Windows XInput).";
        }

        // ---------- UI Builders ----------
        private void BuildAxesUI()
        {
            AxesHost.Children.Clear();

            var axes = Input.GetAxisNames();
            for (int i = 0; i < axes.Count; i++)
            {
                var name = axes[i];
                var info = Input.GetAxisInfo(name);
                if (info == null) continue;

                var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

                row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });

                if (info.IsMouseX || info.IsMouseY)
                    row.Children.Add(new TextBlock { Text = "Mouse delta + optional look stick", Opacity = 0.7 });

                row.Children.Add(LabeledChips("Positive keys", info.Positive, k =>
                {
                    info.Positive.Remove(k);
                    Input.ApplyAxisInfo(info);
                    BuildAxesUI();
                }));
                row.Children.Add(LabeledChips("Negative keys", info.Negative, k =>
                {
                    info.Negative.Remove(k);
                    Input.ApplyAxisInfo(info);
                    BuildAxesUI();
                }));
                row.Children.Add(LabeledChips("Pad +", info.PositiveButtons, b =>
                {
                    info.PositiveButtons.Remove(b);
                    Input.ApplyAxisInfo(info);
                    BuildAxesUI();
                }));
                row.Children.Add(LabeledChips("Pad −", info.NegativeButtons, b =>
                {
                    info.NegativeButtons.Remove(b);
                    Input.ApplyAxisInfo(info);
                    BuildAxesUI();
                }));

                var analogLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                analogLine.Children.Add(new TextBlock
                {
                    Text = "Analog: " + (info.AnalogAxis == GamepadAxis.None ? "(none)" : info.AnalogAxis.ToString()),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 180
                });
                var analogBtn = new Button { Content = "Rebind analog", Tag = name };
                analogBtn.Click += (_, __) => BeginWait(name, WaitingKind.AxisAnalog, "Move a stick or pull a trigger… (Esc to cancel)");
                var clearAnalog = new Button { Content = "Clear analog" };
                clearAnalog.Click += (_, __) =>
                {
                    info.AnalogAxis = GamepadAxis.None;
                    Input.ApplyAxisInfo(info);
                    BuildAxesUI();
                };
                var invert = new CheckBox
                {
                    Content = "Invert",
                    IsChecked = info.InvertAnalog,
                    VerticalAlignment = VerticalAlignment.Center
                };
                invert.IsCheckedChanged += (_, __) =>
                {
                    info.InvertAnalog = invert.IsChecked == true;
                    Input.ApplyAxisInfo(info);
                };
                analogLine.Children.Add(analogBtn);
                analogLine.Children.Add(clearAnalog);
                analogLine.Children.Add(invert);
                row.Children.Add(analogLine);

                var keyBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var posBtn = new Button { Content = "Add + key", IsEnabled = !info.IsMouseX && !info.IsMouseY };
                var negBtn = new Button { Content = "Add − key", IsEnabled = !info.IsMouseX && !info.IsMouseY };
                var posPadBtn = new Button { Content = "Add pad +" };
                var negPadBtn = new Button { Content = "Add pad −" };
                posBtn.Click += (_, __) => BeginWait(name, WaitingKind.AxisPositive, "Press a key for + … (Esc to cancel)");
                negBtn.Click += (_, __) => BeginWait(name, WaitingKind.AxisNegative, "Press a key for − … (Esc to cancel)");
                posPadBtn.Click += (_, __) => BeginWait(name, WaitingKind.AxisPositivePad, "Press a gamepad button for + … (Esc to cancel)");
                negPadBtn.Click += (_, __) => BeginWait(name, WaitingKind.AxisNegativePad, "Press a gamepad button for − … (Esc to cancel)");
                keyBtns.Children.Add(posBtn);
                keyBtns.Children.Add(negBtn);
                keyBtns.Children.Add(posPadBtn);
                keyBtns.Children.Add(negPadBtn);
                row.Children.Add(keyBtns);

                row.Children.Add(new Separator());
                AxesHost.Children.Add(row);
            }
        }

        private void BuildActionsUI()
        {
            ActionsHost.Children.Clear();

            var actions = Input.GetActionNames();
            for (int i = 0; i < actions.Count; i++)
            {
                var name = actions[i];
                var info = Input.GetActionInfo(name);
                if (info == null) continue;

                var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

                var header = new DockPanel();
                var delBtn = new Button { Content = "Delete", Tag = name };
                DockPanel.SetDock(delBtn, Dock.Right);
                header.Children.Add(delBtn);
                header.Children.Add(new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(header);

                delBtn.Click += (_, __) =>
                {
                    if (Input.RemoveAction(name))
                        BuildActionsUI();
                };

                row.Children.Add(LabeledChips("Keys", info.Keys, k =>
                {
                    info.Keys.Remove(k);
                    Input.ApplyActionInfo(info);
                    BuildActionsUI();
                }));
                row.Children.Add(LabeledChips("Mouse", info.MouseButtons, m =>
                {
                    info.MouseButtons.Remove(m);
                    Input.ApplyActionInfo(info);
                    BuildActionsUI();
                }));
                row.Children.Add(LabeledChips("Gamepad", info.GamepadButtons, g =>
                {
                    info.GamepadButtons.Remove(g);
                    Input.ApplyActionInfo(info);
                    BuildActionsUI();
                }));

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var rebindKeyBtn = new Button { Content = "Add key" };
                rebindKeyBtn.Click += (_, __) => BeginWait(name, WaitingKind.ActionKey, "Press a key… (Esc to cancel)");
                var rebindMouseBtn = new Button { Content = "Add mouse" };
                rebindMouseBtn.Click += (_, __) => BeginWait(name, WaitingKind.ActionMouse, "Click a mouse button… (Esc to cancel)");
                var rebindPadBtn = new Button { Content = "Add gamepad" };
                rebindPadBtn.Click += (_, __) => BeginWait(name, WaitingKind.ActionGamepad, "Press a gamepad button or trigger… (Esc to cancel)");
                btns.Children.Add(rebindKeyBtn);
                btns.Children.Add(rebindMouseBtn);
                btns.Children.Add(rebindPadBtn);
                row.Children.Add(btns);

                row.Children.Add(new Separator());
                ActionsHost.Children.Add(row);
            }
        }

        static StackPanel LabeledChips<T>(string label, List<T> items, Action<T> onRemove)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new TextBlock
            {
                Text = label + ":",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center
            });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            if (items.Count == 0)
            {
                wrap.Children.Add(new TextBlock { Text = "(none)", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                foreach (var item in items.ToList())
                {
                    var captured = item;
                    var chip = new Button
                    {
                        Content = captured + "  ×",
                        Padding = new Avalonia.Thickness(8, 2),
                        Margin = new Avalonia.Thickness(0, 0, 4, 2)
                    };
                    chip.Click += (_, __) => onRemove(captured);
                    wrap.Children.Add(chip);
                }
            }
            line.Children.Add(wrap);
            return line;
        }

        void BeginWait(string target, WaitingKind kind, string title)
        {
            _targetName = target;
            _waiting = kind;
            Title = "Input Remapping — " + title;
        }

        private void CancelWaiting()
        {
            _waiting = WaitingKind.None;
            _targetName = null;
            UpdateTitleWithPath();
        }

        private void OnHostKeyDown(object? sender, KeyEventArgs e)
        {
            if (_waiting == WaitingKind.None) return;

            if (e.Key == Avalonia.Input.Key.Escape)
            {
                CancelWaiting();
                e.Handled = true;
                return;
            }

            if (_waiting == WaitingKind.ActionGamepad || _waiting == WaitingKind.ActionMouse ||
                _waiting == WaitingKind.AxisAnalog || _waiting == WaitingKind.AxisPositivePad ||
                _waiting == WaitingKind.AxisNegativePad)
                return;

            if (!KeyMap.TryFromAvalonia(e.Key, out var code))
                return;

            if (_waiting == WaitingKind.AxisPositive || _waiting == WaitingKind.AxisNegative)
            {
                var info = Input.GetAxisInfo(_targetName);
                if (info != null)
                {
                    var list = _waiting == WaitingKind.AxisPositive ? info.Positive : info.Negative;
                    if (!list.Contains(code)) list.Add(code);
                    Input.ApplyAxisInfo(info);
                }
                BuildAxesUI();
            }
            else if (_waiting == WaitingKind.ActionKey)
            {
                var info = Input.GetActionInfo(_targetName);
                if (info != null && !info.Keys.Contains(code))
                {
                    info.Keys.Add(code);
                    Input.ApplyActionInfo(info);
                }
                BuildActionsUI();
            }

            CancelWaiting();
            e.Handled = true;
        }

        private void OnHostPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_waiting != WaitingKind.ActionMouse) return;

            var p = e.GetCurrentPoint(this);
            Core.Input.MouseButton? mb = null;
            if (p.Properties.IsLeftButtonPressed) mb = Core.Input.MouseButton.Left;
            else if (p.Properties.IsRightButtonPressed) mb = Core.Input.MouseButton.Right;
            else if (p.Properties.IsMiddleButtonPressed) mb = Core.Input.MouseButton.Middle;

            if (mb.HasValue)
            {
                var info = Input.GetActionInfo(_targetName);
                if (info != null && !info.MouseButtons.Contains(mb.Value))
                {
                    info.MouseButtons.Add(mb.Value);
                    Input.ApplyActionInfo(info);
                }
                BuildActionsUI();
                CancelWaiting();
                e.Handled = true;
            }
        }

        private void OnResetClicked(object? sender, RoutedEventArgs e)
        {
            Input.ResetToEngineDefaults();
            DeadzoneBox.Text = Input.GamepadDeadzone.ToString("0.##", CultureInfo.InvariantCulture);
            LookScaleBox.Text = Input.GamepadLookSensitivity.ToString("0.##", CultureInfo.InvariantCulture);
            BuildAxesUI();
            BuildActionsUI();
            UpdateTitleWithPath();
        }

        private void OnSaveClicked(object? sender, RoutedEventArgs e)
        {
            CommitScalarFields();
            if (ProjectService.Current == null)
            {
                Title = "Input Remapping — open a project to save";
                return;
            }
            Input.SaveBindingsToProject();
            ProjectService.TouchModified();
            Title = "Input Remapping — saved";
            var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            restore.Tick += (_, __) =>
            {
                restore.Stop();
                UpdateTitleWithPath();
            };
            restore.Start();
        }

        private void OnAddActionClicked(object? sender, RoutedEventArgs e)
        {
            var name = (NewActionNameBox.Text ?? "").Trim();
            if (!IsValidActionName(name)) { AddActionHint.Text = "Invalid name"; return; }

            if (Input.GetActionInfo(name) != null)
            {
                AddActionHint.Text = "Action already exists";
                return;
            }

            Input.SetAction(name, new List<KeyCode>(), new List<Core.Input.MouseButton>(), new List<GamepadButton>());
            BuildActionsUI();
            AddActionHint.Text = "(added)";
            NewActionNameBox.Text = "";
        }

        private static bool IsValidActionName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ')) return false;
            }
            return true;
        }

        private void UpdateTitleWithPath()
        {
            var root = ProjectService.Current?.RootPath;
            var p = Input.GetBindingsPathForCurrentProject();
            if (p != null && root != null)
            {
                try
                {
                    var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(p))
                        .Replace('\\', '/');
                    Title = $"Input Remapping — {rel}";
                }
                catch
                {
                    Title = $"Input Remapping — {p}";
                }
            }
            else if (ProjectService.Current == null)
                Title = "Input Remapping (no project — open one to persist)";
            else
                Title = "Input Remapping";
        }
    }
}
