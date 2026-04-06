using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;
using Game_Engine.Core.Input;
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;
using System.Linq;
using static Game_Engine.Core.Input.Input;

namespace Game_Engine.Views
{
    public partial class InputRemappingWindow : Window
    {
        private enum WaitingKind { None, AxisPositive, AxisNegative, ActionKey, ActionMouse }
        private WaitingKind _waiting = WaitingKind.None;
        private string _targetName;

        // Snapshot for "Reset to Defaults"
        private readonly List<AxisBindingInfo> _defaultAxes;
        private readonly List<ActionBindingInfo> _defaultActions;

        private readonly float _defaultMouseSensitivity;

        public InputRemappingWindow()
        {
            InitializeComponent();

            if (ProjectService.Current != null)
                Input.TryLoadBindingsFromProject();

            _defaultMouseSensitivity = Input.MouseSensitivity;

            // Take a snapshot of current bindings when the window opens
            _defaultAxes = Input.GetAxisNames()
                                .Select(Input.GetAxisInfo)
                                .Where(a => a != null)
                                .ToList();
            _defaultActions = Input.GetActionNames()
                                   .Select(Input.GetActionInfo)
                                   .Where(a => a != null)
                                   .ToList();

            BuildAxesUI();
            BuildActionsUI();

            BtnReset.Click += OnResetClicked;
            BtnClose.Click += (_, __) => Close();
            BtnSave.Click += OnSaveClicked;
            AddActionBtn.Click += OnAddActionClicked;

            // Listen for rebind input
            KeyDown += OnHostKeyDown;
            PointerPressed += OnHostPointerPressed;

            UpdateTitleWithPath();
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

                row.Children.Add(new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeight.SemiBold
                });

                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                var posLbl = new TextBlock { Text = "Positive:", VerticalAlignment = VerticalAlignment.Center, Width = 70 };
                var posKeys = new TextBlock { Text = string.Join(", ", info.Positive.Select(k => k.ToString())), Width = 220 };

                var negLbl = new TextBlock { Text = "Negative:", VerticalAlignment = VerticalAlignment.Center, Width = 70 };
                var negKeys = new TextBlock { Text = string.Join(", ", info.Negative.Select(k => k.ToString())), Width = 220 };

                var posBtn = new Button { Content = "Rebind +", Tag = name, IsEnabled = !info.IsMouseX && !info.IsMouseY };
                var negBtn = new Button { Content = "Rebind -", Tag = name, IsEnabled = !info.IsMouseX && !info.IsMouseY };

                posBtn.Click += delegate { BeginAxisRebind(name, true); };
                negBtn.Click += delegate { BeginAxisRebind(name, false); };

                line.Children.Add(posLbl);
                line.Children.Add(posKeys);
                line.Children.Add(posBtn);
                line.Children.Add(negLbl);
                line.Children.Add(negKeys);
                line.Children.Add(negBtn);

                if (info.IsMouseX || info.IsMouseY)
                {
                    row.Children.Add(new TextBlock { Text = "Mouse axis (read-only)", Opacity = 0.7 });
                }

                row.Children.Add(line);
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

                // Header with Delete button (right-aligned)
                var header = new DockPanel();
                var delBtn = new Button { Content = "Delete", Tag = name };
                DockPanel.SetDock(delBtn, Dock.Right);
                header.Children.Add(delBtn);
                header.Children.Add(new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                row.Children.Add(header);

                delBtn.Click += delegate
                {
                    // remove and rebuild list
                    if (Input.RemoveAction(name))
                        BuildActionsUI();
                };

                // Keys line
                var line1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var keyLbl = new TextBlock { Text = "Keys:", Width = 70, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var keyList = new TextBlock { Text = string.Join(", ", info.Keys.Select(k => k.ToString())), Width = 260 };
                var rebindKeyBtn = new Button { Content = "Rebind Keys (add)", Tag = name };
                rebindKeyBtn.Click += delegate { BeginActionKeyRebind(name); };

                line1.Children.Add(keyLbl);
                line1.Children.Add(keyList);
                line1.Children.Add(rebindKeyBtn);

                // Mouse line
                var line2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var mouseLbl = new TextBlock { Text = "Mouse:", Width = 70, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var mouseList = new TextBlock { Text = string.Join(", ", info.MouseButtons.Select(m => m.ToString())), Width = 260 };
                var rebindMouseBtn = new Button { Content = "Rebind Mouse (add)", Tag = name };
                rebindMouseBtn.Click += delegate { BeginActionMouseRebind(name); };

                line2.Children.Add(mouseLbl);
                line2.Children.Add(mouseList);
                line2.Children.Add(rebindMouseBtn);

                row.Children.Add(line1);
                row.Children.Add(line2);
                row.Children.Add(new Separator());
                ActionsHost.Children.Add(row);
            }
        }


        // ---------- Rebinding ----------
        private void BeginAxisRebind(string axisName, bool positive)
        {
            _targetName = axisName;
            _waiting = positive ? WaitingKind.AxisPositive : WaitingKind.AxisNegative;
            Title = "Input Remapping — waiting for key… (Esc to cancel)";
        }

        private void BeginActionKeyRebind(string actionName)
        {
            _targetName = actionName;
            _waiting = WaitingKind.ActionKey;
            Title = "Input Remapping — waiting for key… (Esc to cancel)";
        }

        private void BeginActionMouseRebind(string actionName)
        {
            _targetName = actionName;
            _waiting = WaitingKind.ActionMouse;
            Title = "Input Remapping — click a mouse button… (Esc to cancel)";
        }

        private void CancelWaiting()
        {
            _waiting = WaitingKind.None;
            _targetName = null;
            UpdateTitleWithPath();
        }

        private void OnHostKeyDown(object sender, KeyEventArgs e)
        {
            if (_waiting == WaitingKind.None) return;

            if (e.Key == Avalonia.Input.Key.Escape)
            {
                CancelWaiting();
                return;
            }

            KeyCode code;
            // full Avalonia Key → KeyCode map (letters, digits, F-keys, OEM, media, etc.)
            if (!KeyMap.TryFromAvalonia(e.Key, out code))
                return;

            if (_waiting == WaitingKind.AxisPositive || _waiting == WaitingKind.AxisNegative)
            {
                var info = Input.GetAxisInfo(_targetName);
                if (info != null)
                {
                    var list = _waiting == WaitingKind.AxisPositive ? info.Positive : info.Negative;
                    if (!list.Contains(code)) list.Add(code);
                    Input.SetAxis(info.Name, info.Positive, info.Negative, info.Sensitivity, info.Gravity, info.Snap);
                }
                BuildAxesUI();
            }
            else if (_waiting == WaitingKind.ActionKey)
            {
                var info = Input.GetActionInfo(_targetName);
                if (info != null && !info.Keys.Contains(code))
                {
                    info.Keys.Add(code);
                    Input.SetAction(info.Name, info.Keys, info.MouseButtons);
                }
                BuildActionsUI();
            }

            CancelWaiting();
        }

        private void OnHostPointerPressed(object sender, PointerPressedEventArgs e)
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
                    Input.SetAction(info.Name, info.Keys, info.MouseButtons);
                }
                BuildActionsUI();
                CancelWaiting();
            }
        }

        private void OnResetClicked(object sender, RoutedEventArgs e)
        {
            // Remove any actions that were created after the snapshot
            var defaultActionNames = new HashSet<string>(
                _defaultActions.Select(a => a.Name), StringComparer.Ordinal);

            var existingActions = Input.GetActionNames();
            for (int i = 0; i < existingActions.Count; i++)
            {
                var name = existingActions[i];
                if (!defaultActionNames.Contains(name))
                    Input.RemoveAction(name); 
            }

            // Restore axes to snapshot values
            for (int i = 0; i < _defaultAxes.Count; i++)
            {
                var a = _defaultAxes[i];
                Input.SetAxis(a.Name, a.Positive, a.Negative, a.Sensitivity, a.Gravity, a.Snap);
            }

            // Restore actions to snapshot values (re-creates any deleted built-ins)
            for (int j = 0; j < _defaultActions.Count; j++)
            {
                var ac = _defaultActions[j];
                Input.SetAction(ac.Name, ac.Keys, ac.MouseButtons);
            }

            //  restore mouse sensitivity to snapshot
            Input.MouseSensitivity = _defaultMouseSensitivity;

            //Rebuild UI
            BuildAxesUI();
            BuildActionsUI();
            UpdateTitleWithPath();
        }


        private void OnSaveClicked(object sender, RoutedEventArgs e)
        {
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

        private void OnAddActionClicked(object sender, RoutedEventArgs e)
        {
            var name = (NewActionNameBox.Text ?? "").Trim();
            if (!IsValidActionName(name)) { AddActionHint.Text = "Invalid name"; return; }

            if (Input.GetActionInfo(name) != null)
            {
                AddActionHint.Text = "Action already exists";
                return;
            }

            // create empty custom action
            Input.SetAction(name, new List<KeyCode>(), new List<Core.Input.MouseButton>());
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
