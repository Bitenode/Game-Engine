#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Views
{
    // ============================================================
    //  AnimationPanel — main code-behind
    // ============================================================

    public partial class AnimationPanel : UserControl
    {
        // ── State ──
        private Animator? _animator;
        private AnimationClip? _clip;
        private GameObject? _selectedGO;

        // Preview
        private DispatcherTimer? _previewTimer;
        private bool _isPlaying;
        private float _playheadTime;
        private bool _isRecording;

        public AnimationPanel()
        {
            InitializeComponent();

            // Transport
            BtnPlay.Click += (_, _) => StartPreview();
            BtnPause.Click += (_, _) => PausePreview();
            BtnStop.Click += (_, _) => StopPreview();
            BtnRecord.Click += (_, _) => _isRecording = BtnRecord.IsChecked == true;

            // View mode
            RbTimeline.IsCheckedChanged += (_, _) => SwitchView();
            RbCurves.IsCheckedChanged += (_, _) => SwitchView();
            RbStateMachine.IsCheckedChanged += (_, _) => SwitchView();

            // GameObject selector
            CbGameObject.SelectionChanged += OnGameObjectDropdownChanged;
            BtnAddAnimator.Click += OnAddAnimatorClick;

            // Clip management
            BtnNewClip.Click += OnNewClip;
            CbClip.SelectionChanged += OnClipChanged;
            BtnAddTrack.Click += OnAddTrack;

            // Wire canvas callbacks
            Canvas.Panel = this;
            Canvas.DoubleTapped += (_, e) => Canvas.HandleDoubleTapped(e.GetPosition(Canvas));
            SmCanvas.Panel = this;

            // Listen for selection changes (both scene hierarchy and scene service)
            SelectionService.Changed += OnSceneChanged;
            SceneService.Changed += () => Dispatcher.UIThread.Post(RefreshGameObjectDropdown);

            // Preview timer (60 fps)
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _previewTimer.Tick += OnPreviewTick;

            // Initial population of the GO dropdown
            Loaded += (_, _) => RefreshGameObjectDropdown();
        }

        // ── Properties for canvas access ──
        public Animator? CurrentAnimator => _animator;
        public AnimationClip? CurrentClip => _clip;
        public float PlayheadTime { get => _playheadTime; set { _playheadTime = value; RefreshTimeDisplay(); } }
        public bool IsRecording => _isRecording;
        public bool ShowCurves => RbCurves.IsChecked == true;

        // ── Scene Selection ──

        private void OnSceneChanged()
        {
            Dispatcher.UIThread.Post(RefreshFromSelection);
        }

        public void RefreshFromSelection()
        {
            var sel = SelectionService.Current;
            if (sel == _selectedGO && _animator != null) return;

            _selectedGO = sel;
            _animator = sel?.Behaviors?.OfType<Animator>().FirstOrDefault();
            _animator?.EnsureBuilt();   // rebuild state machine from DTOs if needed (editor)
            _clip = _animator?.CurrentClip;

            // Sync the GO dropdown to match
            RefreshGameObjectDropdown();
            UpdateAddAnimatorButton();

            RefreshTransformSection();
            RefreshBoneTreeSection();
            RefreshClipDropdown();
            RefreshTrackList();
            Canvas.InvalidateVisual();
            SmCanvas.InvalidateVisual();
        }

        // ── GameObject Selector ──

        private List<GameObject> _goList = new();
        private bool _suppressGoDropdown;

        private void RefreshGameObjectDropdown()
        {
            _suppressGoDropdown = true;
            _goList.Clear();
            CollectGameObjects(SceneService.Root, _goList);

            var names = _goList.Select(g => g.Name ?? "(unnamed)").ToList();
            CbGameObject.ItemsSource = names;

            // Try to keep current selection
            if (_selectedGO != null)
            {
                int idx = _goList.IndexOf(_selectedGO);
                if (idx >= 0) CbGameObject.SelectedIndex = idx;
                else CbGameObject.SelectedIndex = -1;
            }
            else
            {
                CbGameObject.SelectedIndex = -1;
            }

            _suppressGoDropdown = false;
            UpdateAddAnimatorButton();
        }

        private static void CollectGameObjects(IEnumerable<GameObject> roots, List<GameObject> result)
        {
            foreach (var go in roots)
            {
                // Skip generated vegetation chunks
                if (go.Name?.StartsWith("__grass") == true || go.Name?.StartsWith("chunk_") == true)
                    continue;
                result.Add(go);
                if (go.Children.Count > 0)
                    CollectGameObjects(go.Children, result);
            }
        }

        private void OnGameObjectDropdownChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressGoDropdown) return;

            int idx = CbGameObject.SelectedIndex;
            if (idx < 0 || idx >= _goList.Count) return;

            var go = _goList[idx];
            SelectionService.Set(go);              // sync to hierarchy / inspector
            _selectedGO = go;
            _animator = go.Behaviors?.OfType<Animator>().FirstOrDefault();
            _animator?.EnsureBuilt();
            _clip = _animator?.CurrentClip;

            UpdateAddAnimatorButton();
            RefreshTransformSection();
            RefreshBoneTreeSection();
            RefreshClipDropdown();
            RefreshTrackList();
            Canvas.InvalidateVisual();
            SmCanvas.InvalidateVisual();
        }

        private void OnAddAnimatorClick(object? sender, RoutedEventArgs e)
        {
            if (_selectedGO == null) return;

            // Already has one?
            if (_selectedGO.Behaviors.OfType<Animator>().Any()) return;

            var anim = _selectedGO.AddBehavior<Animator>();
            _animator = anim;

            // Create a default clip so the user has something to work with
            var clipName = $"{_selectedGO.Name ?? "Object"}_DefaultClip";
            var relPath = $"Assets/Animations/{clipName}.anim";
            var defaultClip = AnimationClipAsset.CreateNew(clipName, relPath, 1.0f);
            _animator.AddState("Default", defaultClip);
            _animator.Play("Default");
            _clip = defaultClip;

            UpdateAddAnimatorButton();
            RefreshTransformSection();
            RefreshClipDropdown();
            RefreshTrackList();
            Canvas.InvalidateVisual();
            SmCanvas.InvalidateVisual();
        }

        private void UpdateAddAnimatorButton()
        {
            bool hasGO = _selectedGO != null;
            bool hasAnimator = _animator != null;
            BtnAddAnimator.IsVisible = hasGO && !hasAnimator;
        }

        // ── Clip Management ──

        private void RefreshClipDropdown()
        {
            CbClip.SelectionChanged -= OnClipChanged;
            CbClip.ItemsSource = null;

            var clips = new List<string>();
            if (_animator != null)
            {
                foreach (var (name, state) in _animator.States)
                {
                    if (state.Clip != null && !clips.Contains(state.Clip.Name))
                        clips.Add(state.Clip.Name);
                    if (state.BoneClip != null && !clips.Contains("[Bone] " + state.BoneClip.Name))
                        clips.Add("[Bone] " + state.BoneClip.Name);
                }
            }
            // Also show cached property clips
            foreach (var c in AnimationClipAsset.AllCached())
            {
                if (!clips.Contains(c.Name))
                    clips.Add(c.Name);
            }
            // Also show cached bone clips
            foreach (var c in BoneAnimationClipAsset.AllCached())
            {
                var label = "[Bone] " + c.Name;
                if (!clips.Contains(label))
                    clips.Add(label);
            }

            CbClip.ItemsSource = clips;
            // Try to select current property clip or bone clip
            string? selectedName = null;
            if (_clip != null) selectedName = _clip.Name;
            else if (_boneClip != null) selectedName = "[Bone] " + _boneClip.Name;

            if (selectedName != null && clips.Contains(selectedName))
                CbClip.SelectedItem = selectedName;
            else if (clips.Count > 0)
                CbClip.SelectedIndex = 0;

            CbClip.SelectionChanged += OnClipChanged;
        }

        private void OnClipChanged(object? sender, SelectionChangedEventArgs e)
        {
            var name = CbClip.SelectedItem as string;
            if (name == null) return;

            // Bone clip selected?
            if (name.StartsWith("[Bone] "))
            {
                var boneName = name.Substring(7);
                _clip = null; // deselect property clip
                _boneClip = null;

                if (_animator != null)
                {
                    foreach (var (_, state) in _animator.States)
                    {
                        if (state.BoneClip?.Name == boneName) { _boneClip = state.BoneClip; break; }
                    }
                }
                if (_boneClip == null)
                    _boneClip = BoneAnimationClipAsset.AllCached().FirstOrDefault(c => c.Name == boneName);

                RefreshBoneTreeSection();
                RefreshTrackList();
                Canvas.InvalidateVisual();
                return;
            }

            // Property clip selected
            _boneClip = null;

            // Find clip in animator states
            if (_animator != null)
            {
                foreach (var (_, state) in _animator.States)
                {
                    if (state.Clip?.Name == name) { _clip = state.Clip; break; }
                }
            }

            // Or from cache
            if (_clip?.Name != name)
            {
                _clip = AnimationClipAsset.AllCached().FirstOrDefault(c => c.Name == name);
            }

            RefreshTrackList();
            Canvas.InvalidateVisual();
        }

        private async void OnNewClip(object? sender, RoutedEventArgs e)
        {
            // Simple dialog: ask for name
            var nameBox = new TextBox { Text = "New Clip", Width = 200 };
            var durationBox = new TextBox { Text = "1.0", Width = 80 };
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Clip Name:" });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "Duration (seconds):" });
            panel.Children.Add(durationBox);

            var dialog = new Window
            {
                Title = "New Animation Clip",
                Width = 280,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        panel,
                        new Button { Content = "Create", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                    }
                }
            };

            var createBtn = ((StackPanel)dialog.Content).Children.OfType<Button>().First();
            createBtn.Click += (_, _) => dialog.Close();

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner != null) await dialog.ShowDialog(owner);

            var clipName = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(clipName)) return;

            float.TryParse(durationBox.Text, out float dur);
            if (dur <= 0) dur = 1f;

            var relPath = $"Assets/Animations/{clipName}.anim";
            var newClip = AnimationClipAsset.CreateNew(clipName, relPath, dur);

            // Add state to animator if present
            if (_animator != null)
            {
                _animator.AddState(clipName, newClip);
                _clip = newClip;
            }
            else
            {
                _clip = newClip;
            }

            RefreshClipDropdown();
            RefreshTrackList();
            Canvas.InvalidateVisual();
        }

        // ── Transform Editor Section (always visible when a GO is selected) ──

        private readonly Dictionary<string, TextBox> _transformBoxes = new();

        private void RefreshTransformSection()
        {
            TransformSection.Children.Clear();
            _transformBoxes.Clear();

            if (_selectedGO?.Transform == null)
            {
                TransformSection.IsVisible = false;
                TransformTrackSeparator.IsVisible = false;
                return;
            }

            TransformSection.IsVisible = true;
            TransformTrackSeparator.IsVisible = true;

            var tf = _selectedGO.Transform;

            // Header
            TransformSection.Children.Add(new TextBlock
            {
                Text = "Transform",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 230)),
                Margin = new Thickness(4, 4, 0, 2)
            });

            // Position row
            AddTransformRow("Position", "Transform.Position",
                ("X", (float)tf.Position.X), ("Y", (float)tf.Position.Y), ("Z", (float)tf.Position.Z));

            // Rotation row
            AddTransformRow("Rotation", "Transform.Rotation",
                ("X", (float)tf.Rotation.X), ("Y", (float)tf.Rotation.Y), ("Z", (float)tf.Rotation.Z));

            // Scale row
            AddTransformRow("Scale", "Transform.Scale",
                ("X", (float)tf.Scale.X), ("Y", (float)tf.Scale.Y), ("Z", (float)tf.Scale.Z));
        }

        private void AddTransformRow(string label, string groupPath,
            (string axis, float val) x, (string axis, float val) y, (string axis, float val) z)
        {
            var row = new Border
            {
                Background = TransformRowBg,
                Padding = new Thickness(4, 3),
                Margin = new Thickness(0, 1),
                CornerRadius = new CornerRadius(3)
            };

            var grid = new Avalonia.Controls.Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };

            // Label
            grid.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = TransformLabelBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = 52,
                [Avalonia.Controls.Grid.ColumnProperty] = 0
            });

            // X / Y / Z value editors
            var valPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                [Avalonia.Controls.Grid.ColumnProperty] = 1
            };

            foreach (var (axis, val) in new[] { x, y, z })
            {
                IBrush axisColor = axis switch
                {
                    "X" => AxisXBrush,
                    "Y" => AxisYBrush,
                    "Z" => AxisZBrush,
                    _ => DefaultLabelBrush
                };

                valPanel.Children.Add(new TextBlock
                {
                    Text = axis,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = axisColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 1, 0)
                });

                string fullPath = $"{groupPath}.{axis}";
                var box = new TextBox
                {
                    Text = val.ToString("F2"),
                    Classes = { "transformValue" },
                    Tag = fullPath
                };
                box.LostFocus += OnTransformValueCommit;
                box.KeyDown += OnTransformValueKeyDown;
                _transformBoxes[fullPath] = box;
                valPanel.Children.Add(box);
            }
            grid.Children.Add(valPanel);

            // Key button (adds keyframes for all 3 axes of this group)
            var keyBtn = new Button
            {
                Content = "◆ Key",
                Width = 42,
                Height = 20,
                Padding = new Thickness(2, 0),
                Classes = { "keyBtn" },
                Tag = groupPath,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [Avalonia.Controls.Grid.ColumnProperty] = 2
            };
            keyBtn.Click += OnTransformKeyClick;
            grid.Children.Add(keyBtn);

            row.Child = grid;
            TransformSection.Children.Add(row);
        }

        private void OnTransformValueCommit(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || tb.Tag is not string path) return;
            if (!float.TryParse(tb.Text, out float val)) return;

            // Apply directly to transform
            ApplyTransformValue(path, val);

            // If recording and we have a clip, add keyframe
            if (_isRecording && _clip != null)
            {
                // Ensure track exists
                if (!_clip.Tracks.ContainsKey(path))
                    _clip.SetKey(path, 0f, val);
                _clip.SetKey(path, _playheadTime, val);
                Canvas.InvalidateVisual();
            }
        }

        private void OnTransformValueKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                OnTransformValueCommit(sender, e);
                this.Focus();
            }
        }

        private void OnTransformKeyClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string groupPath) return;
            if (_clip == null || _selectedGO?.Transform == null) return;

            // Add an Animator if one doesn't exist yet
            if (_animator == null)
            {
                _animator = _selectedGO.AddBehavior<Animator>();
                var clipName = $"{_selectedGO.Name ?? "Object"}_Clip";
                var relPath = $"Assets/Animations/{clipName}.anim";
                _clip = AnimationClipAsset.CreateNew(clipName, relPath, 1.0f);
                _animator.AddState("Default", _clip);
                _animator.Play("Default");
                UpdateAddAnimatorButton();
                RefreshClipDropdown();
            }

            // Read current transform values and add keyframes
            foreach (var axis in new[] { "X", "Y", "Z" })
            {
                string fullPath = $"{groupPath}.{axis}";
                float val = ReadTransformValue(fullPath);
                _clip.SetKey(fullPath, _playheadTime, val);
            }

            RefreshTrackList();
            Canvas.InvalidateVisual();
        }

        private void ApplyTransformValue(string path, float value)
        {
            if (_selectedGO?.Transform == null) return;
            var tf = _selectedGO.Transform;

            // Parse "Transform.Position.X" etc.
            var parts = path.Split('.');
            if (parts.Length != 3) return;

            switch (parts[1])
            {
                case "Position":
                    if (parts[2] == "X") tf.Position.X = value;
                    else if (parts[2] == "Y") tf.Position.Y = value;
                    else if (parts[2] == "Z") tf.Position.Z = value;
                    break;
                case "Rotation":
                    if (parts[2] == "X") tf.Rotation.X = value;
                    else if (parts[2] == "Y") tf.Rotation.Y = value;
                    else if (parts[2] == "Z") tf.Rotation.Z = value;
                    break;
                case "Scale":
                    if (parts[2] == "X") tf.Scale.X = value;
                    else if (parts[2] == "Y") tf.Scale.Y = value;
                    else if (parts[2] == "Z") tf.Scale.Z = value;
                    break;
            }
        }

        private float ReadTransformValue(string path)
        {
            if (_selectedGO?.Transform == null) return 0f;
            var tf = _selectedGO.Transform;
            var parts = path.Split('.');
            if (parts.Length != 3) return 0f;

            return parts[1] switch
            {
                "Position" => parts[2] switch { "X" => (float)tf.Position.X, "Y" => (float)tf.Position.Y, "Z" => (float)tf.Position.Z, _ => 0f },
                "Rotation" => parts[2] switch { "X" => (float)tf.Rotation.X, "Y" => (float)tf.Rotation.Y, "Z" => (float)tf.Rotation.Z, _ => 0f },
                "Scale" => parts[2] switch { "X" => (float)tf.Scale.X, "Y" => (float)tf.Scale.Y, "Z" => (float)tf.Scale.Z, _ => 0f },
                _ => 0f
            };
        }

        /// <summary>Refresh only the numeric values in the transform boxes (no full rebuild).</summary>
        public void RefreshTransformValues()
        {
            if (_selectedGO?.Transform == null) return;
            foreach (var (path, box) in _transformBoxes)
            {
                if (box.IsFocused) continue; // don't overwrite while editing
                float val = ReadTransformValue(path);
                box.Text = val.ToString("F2");
            }
        }

        private static readonly IBrush TransformRowBg = new SolidColorBrush(Color.FromRgb(34, 37, 42));
        private static readonly IBrush TransformLabelBrush = new SolidColorBrush(Color.FromRgb(140, 155, 180));

        // ── Bone Tree Section ──

        private int _selectedBoneIndex = -1;
        private BoneAnimationClip? _boneClip;
        private Skeleton? _activeSkeleton;

        /// <summary>The currently active bone animation clip (from the Animator or loaded from file).</summary>
        public BoneAnimationClip? CurrentBoneClip => _boneClip;
        public int SelectedBoneIndex => _selectedBoneIndex;

        private static readonly IBrush BoneHeaderBrush = new SolidColorBrush(Color.FromRgb(180, 200, 230));
        private static readonly IBrush BoneLabelBrush = new SolidColorBrush(Color.FromRgb(140, 160, 185));
        private static readonly IBrush BoneIndentBrush = new SolidColorBrush(Color.FromRgb(60, 65, 75));

        private void RefreshBoneTreeSection()
        {
            BoneTreeSection.Children.Clear();
            _selectedBoneIndex = -1;
            _boneClip = null;
            _activeSkeleton = null;

            if (_selectedGO == null)
            {
                BoneTreeSection.IsVisible = false;
                BoneTrackSeparator.IsVisible = false;
                return;
            }

            // Find a SkinnedMeshRenderer on this GO or its children
            var smr = FindSkinnedMeshRenderer(_selectedGO);
            if (smr?.Skeleton == null || smr.Skeleton.BoneCount == 0)
            {
                BoneTreeSection.IsVisible = false;
                BoneTrackSeparator.IsVisible = false;
                return;
            }

            _activeSkeleton = smr.Skeleton;
            BoneTreeSection.IsVisible = true;
            BoneTrackSeparator.IsVisible = true;

            // Find bone clip from Animator
            if (_animator != null)
            {
                foreach (var (_, state) in _animator.States)
                {
                    if (state.BoneClip != null) { _boneClip = state.BoneClip; break; }
                }
            }

            // Header with bone count and import button
            var headerRow = new DockPanel { Margin = new Thickness(4, 4, 4, 2) };
            var importBtn = new Button
            {
                Content = "Import .boneanim",
                Classes = { "action" },
                Height = 20,
                FontSize = 9,
                Padding = new Thickness(6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            importBtn.Click += OnImportBoneAnim;
            DockPanel.SetDock(importBtn, Dock.Right);
            headerRow.Children.Add(importBtn);

            headerRow.Children.Add(new TextBlock
            {
                Text = $"Skeleton ({_activeSkeleton.BoneCount} bones)",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = BoneHeaderBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            BoneTreeSection.Children.Add(headerRow);

            // Build bone tree recursively from roots
            foreach (var rootIdx in _activeSkeleton.RootBoneIndices)
                AddBoneTreeItem(_activeSkeleton.Bones[rootIdx], 0);
        }

        private void AddBoneTreeItem(Bone bone, int depth)
        {
            if (_activeSkeleton == null) return;

            bool isSelected = bone.Index == _selectedBoneIndex;
            var btn = new Button
            {
                Classes = { isSelected ? "boneItemSelected" : "boneItem" },
                Tag = bone.Index,
                Margin = new Thickness(depth * 12, 0, 0, 0)
            };

            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new TextBlock
            {
                Text = depth > 0 ? "└" : "●",
                FontSize = 9,
                Foreground = BoneIndentBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = bone.Name,
                FontSize = 10,
                Foreground = isSelected ? Brushes.White : BoneLabelBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            // Show track indicator if this bone has keyframes in the current bone clip
            if (_boneClip?.GetTrack(bone.Index) != null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "◆",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 68)),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
            }

            btn.Content = content;
            btn.Click += OnBoneItemClick;
            BoneTreeSection.Children.Add(btn);

            // Recurse children
            foreach (var childIdx in bone.Children)
            {
                if (childIdx >= 0 && childIdx < _activeSkeleton.BoneCount)
                    AddBoneTreeItem(_activeSkeleton.Bones[childIdx], depth + 1);
            }
        }

        private void OnBoneItemClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int boneIdx) return;
            _selectedBoneIndex = boneIdx;
            RefreshBoneTreeSection(); // re-render to update selection highlight
            Canvas.InvalidateVisual(); // show bone keyframes in timeline
        }

        private async void OnImportBoneAnim(object? sender, RoutedEventArgs e)
        {
            if (_animator == null || _activeSkeleton == null) return;

            var dialog = new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import Bone Animation",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Bone Animation") { Patterns = new[] { "*.boneanim" } }
                }
            };

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var result = await topLevel.StorageProvider.OpenFilePickerAsync(dialog);
            if (result.Count == 0) return;

            var filePath = result[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(filePath)) return;

            // Make project-relative
            string relPath = filePath;
            var proj = ProjectService.Current;
            if (proj != null)
            {
                var root = System.IO.Path.GetFullPath(proj.RootPath);
                var abs = System.IO.Path.GetFullPath(filePath);
                if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    relPath = System.IO.Path.GetRelativePath(root, abs);
            }

            var clip = BoneAnimationClipAsset.Load(relPath);
            if (clip == null) return;

            // Remap bone indices to match current skeleton
            foreach (var track in clip.Tracks)
            {
                int idx = _activeSkeleton.FindBone(track.BoneName);
                if (idx >= 0) track.BoneIndex = idx;
            }
            clip.InvalidateCache();

            // Add to Animator as a state (or update existing)
            var stateName = clip.Name;
            if (_animator.States.ContainsKey(stateName))
                _animator.RemoveState(stateName);
            _animator.AddState(stateName, clip);
            _animator.Play(stateName);

            _boneClip = clip;
            RefreshBoneTreeSection();
            RefreshClipDropdown();
            Canvas.InvalidateVisual();
        }

        private static SkinnedMeshRenderer? FindSkinnedMeshRenderer(GameObject go)
        {
            var smr = go.Behaviors?.OfType<SkinnedMeshRenderer>().FirstOrDefault();
            if (smr != null) return smr;

            // Search children (skinned mesh might be on a child node)
            foreach (var child in go.Children)
            {
                smr = FindSkinnedMeshRenderer(child);
                if (smr != null) return smr;
            }
            return null;
        }

        // ── Track List with Inline Value Editors ──

        private readonly Dictionary<string, TextBox> _trackValueBoxes = new();

        // Color-code labels for X/Y/Z axes
        private static readonly IBrush AxisXBrush = new SolidColorBrush(Color.FromRgb(230, 90, 90));
        private static readonly IBrush AxisYBrush = new SolidColorBrush(Color.FromRgb(90, 200, 90));
        private static readonly IBrush AxisZBrush = new SolidColorBrush(Color.FromRgb(90, 150, 240));
        private static readonly IBrush DefaultLabelBrush = new SolidColorBrush(Color.FromRgb(200, 210, 225));
        private static readonly IBrush GroupHeaderBrush = new SolidColorBrush(Color.FromRgb(160, 175, 200));
        private static readonly IBrush GroupBgBrush = new SolidColorBrush(Color.FromRgb(30, 32, 36));
        private static readonly IBrush GroupBorderBrush = new SolidColorBrush(Color.FromRgb(50, 54, 62));

        public void RefreshTrackList()
        {
            TrackListPanel.Children.Clear();
            _trackValueBoxes.Clear();
            if (_clip == null) return;

            var paths = _clip.TrackPaths.ToList();

            // Group paths by component.property (e.g. "Transform.Position")
            var groups = new Dictionary<string, List<string>>();
            var ungrouped = new List<string>();

            foreach (var path in paths)
            {
                var dotParts = path.Split('.');
                if (dotParts.Length == 3)
                {
                    // e.g. Transform.Position.X => group = "Transform.Position"
                    var groupKey = $"{dotParts[0]}.{dotParts[1]}";
                    if (!groups.ContainsKey(groupKey)) groups[groupKey] = new List<string>();
                    groups[groupKey].Add(path);
                }
                else
                {
                    ungrouped.Add(path);
                }
            }

            // Render grouped tracks (Transform.Position, Transform.Rotation, Transform.Scale, etc.)
            foreach (var (groupKey, groupPaths) in groups)
            {
                var groupBorder = new Border
                {
                    Background = GroupBgBrush,
                    BorderBrush = GroupBorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 3, 4, 4),
                    Margin = new Thickness(0, 1)
                };

                var groupStack = new StackPanel { Spacing = 2 };

                // Group header row
                var headerRow = new DockPanel();
                var removeGroupBtn = new Button
                {
                    Content = "x",
                    Width = 18, Height = 18,
                    FontSize = 9,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Classes = { "remove" },
                    Tag = groupKey
                };
                removeGroupBtn.Click += OnRemoveTrackGroup;
                DockPanel.SetDock(removeGroupBtn, Dock.Right);
                headerRow.Children.Add(removeGroupBtn);

                headerRow.Children.Add(new TextBlock
                {
                    Text = groupKey,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = GroupHeaderBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0)
                });
                groupStack.Children.Add(headerRow);

                // Value row with X/Y/Z inline editors
                var valueRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3, Margin = new Thickness(8, 2, 0, 0) };
                foreach (var path in groupPaths)
                {
                    var axis = path.Split('.').Last();
                    IBrush labelColor = axis switch
                    {
                        "X" or "R" => AxisXBrush,
                        "Y" or "G" => AxisYBrush,
                        "Z" or "B" => AxisZBrush,
                        _ => DefaultLabelBrush
                    };

                    valueRow.Children.Add(new TextBlock
                    {
                        Text = axis,
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = labelColor,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 1, 0)
                    });

                    float currentVal = _animator?.ReadPropertyValue(path) ?? 0f;
                    var valBox = new TextBox
                    {
                        Text = currentVal.ToString("F2"),
                        Classes = { "trackValue" },
                        Tag = path
                    };
                    valBox.LostFocus += OnTrackValueCommit;
                    valBox.KeyDown += OnTrackValueKeyDown;
                    _trackValueBoxes[path] = valBox;
                    valueRow.Children.Add(valBox);
                }
                groupStack.Children.Add(valueRow);

                groupBorder.Child = groupStack;
                TrackListPanel.Children.Add(groupBorder);
            }

            // Render ungrouped tracks (e.g. Light.Intensity, Camera.FieldOfView)
            foreach (var path in ungrouped)
            {
                var row = new Border
                {
                    Background = GroupBgBrush,
                    BorderBrush = GroupBorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 3),
                    Margin = new Thickness(0, 1)
                };

                var rowGrid = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

                rowGrid.Children.Add(new TextBlock
                {
                    Text = path,
                    FontSize = 11,
                    Foreground = DefaultLabelBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0),
                    [Avalonia.Controls.Grid.ColumnProperty] = 0
                });

                float currentVal = _animator?.ReadPropertyValue(path) ?? 0f;
                var valBox = new TextBox
                {
                    Text = currentVal.ToString("F2"),
                    Classes = { "trackValue" },
                    Tag = path,
                    [Avalonia.Controls.Grid.ColumnProperty] = 1
                };
                valBox.LostFocus += OnTrackValueCommit;
                valBox.KeyDown += OnTrackValueKeyDown;
                _trackValueBoxes[path] = valBox;
                rowGrid.Children.Add(valBox);

                var removeBtn = new Button
                {
                    Content = "x",
                    Width = 18, Height = 18,
                    FontSize = 9,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Classes = { "remove" },
                    Tag = path,
                    Margin = new Thickness(3, 0, 0, 0),
                    [Avalonia.Controls.Grid.ColumnProperty] = 2
                };
                removeBtn.Click += OnRemoveTrack;
                rowGrid.Children.Add(removeBtn);

                row.Child = rowGrid;
                TrackListPanel.Children.Add(row);
            }
        }

        /// <summary>Refresh only the numeric values in the track boxes (no full rebuild).</summary>
        public void RefreshTrackValues()
        {
            if (_animator == null) return;
            foreach (var (path, box) in _trackValueBoxes)
            {
                if (box.IsFocused) continue; // don't overwrite while editing
                float val = _animator.ReadPropertyValue(path);
                box.Text = val.ToString("F2");
            }
        }

        private void OnTrackValueCommit(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || tb.Tag is not string path) return;
            if (!float.TryParse(tb.Text, out float val)) return;

            // Apply the value to the scene
            _animator?.ApplyPropertyValue_External(path, val);

            // If recording, add a keyframe
            if (_isRecording && _clip != null)
            {
                _clip.SetKey(path, _playheadTime, val);
                Canvas.InvalidateVisual();
            }
        }

        private void OnTrackValueKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                OnTrackValueCommit(sender, e);
                // Move focus away so LostFocus doesn't double-commit
                this.Focus();
            }
        }

        private void OnRemoveTrack(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && _clip != null)
            {
                _clip.RemoveTrack(path);
                RefreshTrackList();
                Canvas.InvalidateVisual();
            }
        }

        private void OnRemoveTrackGroup(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string groupKey && _clip != null)
            {
                // Remove all tracks matching this group (e.g. "Transform.Position.*")
                var toRemove = _clip.TrackPaths.Where(p => p.StartsWith(groupKey + ".")).ToList();
                foreach (var path in toRemove)
                    _clip.RemoveTrack(path);
                RefreshTrackList();
                Canvas.InvalidateVisual();
            }
        }

        private async void OnAddTrack(object? sender, RoutedEventArgs e)
        {
            if (_clip == null || _selectedGO == null) return;

            var available = Animator.GetAnimatableProperties(_selectedGO)
                .Where(p => !_clip.Tracks.ContainsKey(p)).ToList();

            if (available.Count == 0) return;

            // Show a simple selection dialog
            var list = new ListBox
            {
                ItemsSource = available,
                MaxHeight = 300,
                SelectionMode = SelectionMode.Multiple
            };

            var dialog = new Window
            {
                Title = "Add Property Track",
                Width = 320,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new DockPanel
                {
                    Margin = new Thickness(10),
                    Children =
                    {
                        new Button
                        {
                            Content = "Add Selected",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Margin = new Thickness(0, 8, 0, 0),
                            [DockPanel.DockProperty] = Dock.Bottom
                        },
                        list
                    }
                }
            };

            var addBtn = ((DockPanel)dialog.Content).Children.OfType<Button>().First();
            addBtn.Click += (_, _) => dialog.Close();

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner != null) await dialog.ShowDialog(owner);

            foreach (var item in list.SelectedItems ?? Array.Empty<object>())
            {
                if (item is string propPath)
                {
                    // Add a default keyframe at t=0 with current value
                    float val = _animator?.ReadPropertyValue(propPath) ?? 0f;
                    _clip.SetKey(propPath, 0f, val);
                }
            }

            RefreshTrackList();
            Canvas.InvalidateVisual();
        }

        // ── View Switching ──

        private void SwitchView()
        {
            bool sm = RbStateMachine.IsChecked == true;
            TimelineCurveGrid.IsVisible = !sm;
            StateMachineGrid.IsVisible = sm;

            if (sm)
                SmCanvas.InvalidateVisual();
            else
                Canvas.InvalidateVisual();
        }

        // ── Preview Playback ──

        private void StartPreview()
        {
            _isPlaying = true;
            _previewTimer?.Start();
        }

        private void PausePreview()
        {
            _isPlaying = false;
            _previewTimer?.Stop();
        }

        private void StopPreview()
        {
            _isPlaying = false;
            _previewTimer?.Stop();
            _playheadTime = 0f;
            ApplyPreview();
            RefreshTimeDisplay();
            Canvas.InvalidateVisual();
        }

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            if (!_isPlaying) return;

            // Determine active clip duration
            float duration = 0f;
            bool loop = false;
            if (_clip != null) { duration = _clip.Duration; loop = _clip.Loop; }
            else if (_boneClip != null) { duration = _boneClip.Duration; loop = _boneClip.Loop; }
            else return;

            _playheadTime += 0.016f; // ~60fps
            if (loop && duration > 0 && _playheadTime >= duration)
                _playheadTime -= duration;
            else if (!loop && _playheadTime >= duration)
            {
                _playheadTime = duration;
                PausePreview();
            }

            ApplyPreview();
            RefreshTimeDisplay();
            Canvas.InvalidateVisual();
        }

        public void ApplyPreview()
        {
            if (_animator != null && _clip != null)
            {
                // Apply all property tracks at current playhead time
                foreach (var path in _clip.TrackPaths)
                {
                    float val = _clip.Sample(path, _playheadTime);
                    _animator.ApplyPropertyValue_External(path, val);
                }

                // Update the inline value editors to reflect current values
                RefreshTrackValues();
                RefreshTransformValues();
            }

            // Apply bone animation preview
            if (_boneClip != null && _activeSkeleton != null)
            {
                var poses = _boneClip.SampleAllBones(_activeSkeleton.BoneCount, _playheadTime);
                if (_animator != null)
                    _animator.CurrentBonePose = poses;

                // Trigger SkinnedMeshRenderer to recompute
                var smr = _selectedGO != null ? FindSkinnedMeshRenderer(_selectedGO) : null;
                smr?.ComputeBoneMatrices();
            }
        }

        private void RefreshTimeDisplay()
        {
            int mins = (int)(_playheadTime / 60f);
            float secs = _playheadTime - mins * 60f;
            TxtTime.Text = $"{mins}:{secs:00.000}";
        }

        /// <summary>Called by canvas when user scrubs the playhead.</summary>
        public void OnPlayheadScrub(float time)
        {
            _playheadTime = Math.Max(0, time);
            ApplyPreview();
            RefreshTimeDisplay();
            RefreshTrackValues();
            RefreshTransformValues();
            Canvas.InvalidateVisual();
        }

        /// <summary>Record a keyframe at the current playhead if recording.</summary>
        public void RecordKeyframe(string path, float value)
        {
            if (!_isRecording || _clip == null) return;
            _clip.SetKey(path, _playheadTime, value);
            Canvas.InvalidateVisual();
        }
    }

    // ============================================================
    //  AnimationCanvas — custom-drawn timeline / curve editor
    // ============================================================

    public class AnimationCanvas : Control
    {
        public AnimationPanel? Panel { get; set; }

        // Interaction state
        private float _scrollX;
        private float _pixelsPerSecond = 150f;
        private int _dragKeyTrack = -1;
        private int _dragKeyIdx = -1;
        private bool _draggingPlayhead;
        private Point _lastMouse;

        // Track colors for curve view
        private static readonly IBrush[] TrackColors = new IBrush[]
        {
            new SolidColorBrush(Color.FromRgb(230, 80, 80)),   // red
            new SolidColorBrush(Color.FromRgb(80, 200, 80)),   // green
            new SolidColorBrush(Color.FromRgb(80, 140, 230)),  // blue
            new SolidColorBrush(Color.FromRgb(230, 180, 60)),  // yellow
            new SolidColorBrush(Color.FromRgb(180, 80, 220)),  // purple
            new SolidColorBrush(Color.FromRgb(80, 210, 210)),  // cyan
            new SolidColorBrush(Color.FromRgb(230, 130, 60)),  // orange
            new SolidColorBrush(Color.FromRgb(160, 160, 160)), // gray
        };

        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(26, 27, 30));
        private static readonly IBrush RulerBg = new SolidColorBrush(Color.FromRgb(36, 38, 42));
        private static readonly IBrush GridLine = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        private static readonly IBrush RulerText = new SolidColorBrush(Color.FromRgb(140, 150, 170));
        private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.FromRgb(70, 160, 255));
        private static readonly IBrush KeyBrush = new SolidColorBrush(Color.FromRgb(255, 200, 60));
        private static readonly IBrush KeySelectedBrush = new SolidColorBrush(Color.FromRgb(255, 120, 40));
        private static readonly IBrush TrackBg1 = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
        private static readonly IBrush TrackBg2 = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));

        private static readonly Pen PlayheadPen = new Pen(PlayheadBrush, 2);
        private static readonly Pen GridPen = new Pen(GridLine, 1);
        private static readonly Pen CurvePenTemplate = new Pen(Brushes.White, 1.5);

        private const float RulerHeight = 24f;
        private const float TrackHeight = 22f;
        private const float KeyDiamond = 5f;

        public override void Render(DrawingContext dc)
        {
            var clip = Panel?.CurrentClip;
            var bounds = Bounds;
            double w = bounds.Width, h = bounds.Height;

            // Background
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

            // Ruler bar
            dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerHeight));

            if (clip == null)
            {
                var ft = new FormattedText("Select a GameObject with an Animator",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12,
                    new SolidColorBrush(Color.FromRgb(100, 110, 130)));
                dc.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
                return;
            }

            bool showCurves = Panel?.ShowCurves == true;

            // ── Time ruler ──
            DrawRuler(dc, w, clip.Duration);

            // ── Grid lines ──
            DrawGrid(dc, w, h, clip.Duration);

            if (showCurves)
                DrawCurveView(dc, w, h, clip);
            else
                DrawTimelineView(dc, w, h, clip);

            // ── Playhead ──
            float phTime = Panel?.PlayheadTime ?? 0f;
            double phX = TimeToX(phTime);
            if (phX >= 0 && phX <= w)
            {
                dc.DrawLine(PlayheadPen, new Point(phX, 0), new Point(phX, h));
                // Playhead triangle
                var tri = new StreamGeometry();
                using (var ctx = tri.Open())
                {
                    ctx.BeginFigure(new Point(phX - 5, 0), true);
                    ctx.LineTo(new Point(phX + 5, 0));
                    ctx.LineTo(new Point(phX, 8));
                    ctx.EndFigure(true);
                }
                dc.DrawGeometry(PlayheadBrush, null, tri);
            }
        }

        private void DrawRuler(DrawingContext dc, double w, float duration)
        {
            // Tick marks and labels
            float step = CalculateTickStep();
            float startTime = XToTime(0);
            float endTime = XToTime((float)w);

            float t = MathF.Floor(startTime / step) * step;
            while (t <= endTime)
            {
                if (t >= 0)
                {
                    double x = TimeToX(t);
                    dc.DrawLine(GridPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));

                    var ft = new FormattedText($"{t:0.##}s",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 9, RulerText);
                    dc.DrawText(ft, new Point(x - ft.Width / 2, 2));
                }
                t += step;
            }
        }

        private void DrawGrid(DrawingContext dc, double w, double h, float duration)
        {
            float step = CalculateTickStep();
            float startTime = XToTime(0);
            float endTime = XToTime((float)w);

            float t = MathF.Floor(startTime / step) * step;
            while (t <= endTime)
            {
                if (t >= 0)
                {
                    double x = TimeToX(t);
                    dc.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, h));
                }
                t += step;
            }
        }

        private void DrawTimelineView(DrawingContext dc, double w, double h, AnimationClip clip)
        {
            var tracks = clip.Tracks.Keys.ToList();
            for (int i = 0; i < tracks.Count; i++)
            {
                float y = RulerHeight + i * TrackHeight;
                var bg = i % 2 == 0 ? TrackBg1 : TrackBg2;
                dc.DrawRectangle(bg, null, new Rect(0, y, w, TrackHeight));

                var keys = clip.Tracks[tracks[i]];
                foreach (var kf in keys)
                {
                    double kx = TimeToX(kf.Time);
                    double ky = y + TrackHeight / 2;

                    bool selected = (i == _dragKeyTrack && keys.IndexOf(kf) == _dragKeyIdx);
                    var brush = selected ? KeySelectedBrush : KeyBrush;

                    // Diamond shape
                    var diamond = new StreamGeometry();
                    using (var ctx = diamond.Open())
                    {
                        ctx.BeginFigure(new Point(kx, ky - KeyDiamond), true);
                        ctx.LineTo(new Point(kx + KeyDiamond, ky));
                        ctx.LineTo(new Point(kx, ky + KeyDiamond));
                        ctx.LineTo(new Point(kx - KeyDiamond, ky));
                        ctx.EndFigure(true);
                    }
                    dc.DrawGeometry(brush, null, diamond);
                }
            }
        }

        private void DrawCurveView(DrawingContext dc, double w, double h, AnimationClip clip)
        {
            var tracks = clip.Tracks.Keys.ToList();
            double graphTop = RulerHeight + 10;
            double graphBottom = h - 10;
            double graphH = graphBottom - graphTop;
            if (graphH < 20) return;

            // Find value range across all visible tracks
            float minVal = float.MaxValue, maxVal = float.MinValue;
            foreach (var path in tracks)
            {
                foreach (var kf in clip.Tracks[path])
                {
                    if (kf.Value < minVal) minVal = kf.Value;
                    if (kf.Value > maxVal) maxVal = kf.Value;
                }
            }
            float valRange = maxVal - minVal;
            if (valRange < 0.001f) { valRange = 2f; minVal -= 1f; maxVal += 1f; }
            // Add 10% padding
            float pad = valRange * 0.1f;
            minVal -= pad; maxVal += pad; valRange = maxVal - minVal;

            // Draw value axis labels
            int valSteps = Math.Max(2, (int)(graphH / 40));
            for (int i = 0; i <= valSteps; i++)
            {
                float frac = (float)i / valSteps;
                double y = graphBottom - frac * graphH;
                float val = minVal + frac * valRange;

                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));

                var ft = new FormattedText($"{val:0.##}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9, RulerText);
                dc.DrawText(ft, new Point(2, y - ft.Height / 2));
            }

            // Draw curves
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var path = tracks[ti];
                var keys = clip.Tracks[path];
                if (keys.Count == 0) continue;

                var color = TrackColors[ti % TrackColors.Length];
                var pen = new Pen(color, 1.5);

                // Sample the curve at pixel intervals
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    bool first = true;
                    for (double px = 0; px <= w; px += 2)
                    {
                        float t = XToTime((float)px);
                        float val = clip.Sample(path, t);
                        double y = graphBottom - ((val - minVal) / valRange) * graphH;

                        if (first) { ctx.BeginFigure(new Point(px, y), false); first = false; }
                        else ctx.LineTo(new Point(px, y));
                    }
                }
                dc.DrawGeometry(null, pen, geometry);

                // Draw keyframe dots
                foreach (var kf in keys)
                {
                    double kx = TimeToX(kf.Time);
                    double ky = graphBottom - ((kf.Value - minVal) / valRange) * graphH;
                    dc.DrawEllipse(color, null, new Point(kx, ky), 4, 4);

                    // Draw tangent handles for bezier
                    if (kf.Interpolation == InterpMode.CubicBezier)
                    {
                        double tanLen = 30;
                        // Out tangent
                        double outX = kx + tanLen;
                        double outY = ky - kf.OutTangent * tanLen / _pixelsPerSecond * (graphH / valRange);
                        dc.DrawLine(new Pen(color, 0.8), new Point(kx, ky), new Point(outX, outY));
                        dc.DrawEllipse(color, null, new Point(outX, outY), 3, 3);

                        // In tangent
                        double inX = kx - tanLen;
                        double inY = ky + kf.InTangent * tanLen / _pixelsPerSecond * (graphH / valRange);
                        dc.DrawLine(new Pen(color, 0.8), new Point(kx, ky), new Point(inX, inY));
                        dc.DrawEllipse(color, null, new Point(inX, inY), 3, 3);
                    }
                }
            }
        }

        // ── Time <-> pixel conversion ──

        private double TimeToX(float time) => (time * _pixelsPerSecond) - _scrollX;
        private float XToTime(float x) => (x + _scrollX) / _pixelsPerSecond;

        private float CalculateTickStep()
        {
            float[] steps = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f };
            float minPixels = 60f;
            foreach (var s in steps)
            {
                if (s * _pixelsPerSecond >= minPixels) return s;
            }
            return 10f;
        }

        // ── Input handling ──

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pos = e.GetPosition(this);
            _lastMouse = pos;

            // Check if clicking on the ruler area (playhead scrub)
            if (pos.Y < RulerHeight)
            {
                _draggingPlayhead = true;
                float time = XToTime((float)pos.X);
                Panel?.OnPlayheadScrub(Math.Max(0, time));
                e.Handled = true;
                return;
            }

            // Check for keyframe hit (in timeline mode)
            if (Panel?.ShowCurves != true)
            {
                var clip = Panel?.CurrentClip;
                if (clip != null)
                {
                    var tracks = clip.Tracks.Keys.ToList();
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        float y = RulerHeight + i * TrackHeight + TrackHeight / 2;
                        var keys = clip.Tracks[tracks[i]];
                        for (int k = 0; k < keys.Count; k++)
                        {
                            double kx = TimeToX(keys[k].Time);
                            if (Math.Abs(pos.X - kx) < KeyDiamond + 2 && Math.Abs(pos.Y - y) < KeyDiamond + 2)
                            {
                                _dragKeyTrack = i;
                                _dragKeyIdx = k;
                                InvalidateVisual();
                                e.Handled = true;

                                // Right-click to delete
                                if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                                {
                                    clip.RemoveKey(tracks[i], keys[k].Time);
                                    _dragKeyTrack = -1;
                                    _dragKeyIdx = -1;
                                    Panel?.RefreshTrackList();
                                    InvalidateVisual();
                                }
                                return;
                            }
                        }
                    }
                }
            }

            // Click on empty area: move playhead
            {
                float time = XToTime((float)pos.X);
                Panel?.OnPlayheadScrub(Math.Max(0, time));
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);

            if (_draggingPlayhead)
            {
                float time = XToTime((float)pos.X);
                Panel?.OnPlayheadScrub(Math.Max(0, time));
                return;
            }

            // Drag keyframe
            if (_dragKeyTrack >= 0 && _dragKeyIdx >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var clip = Panel?.CurrentClip;
                if (clip != null)
                {
                    var tracks = clip.Tracks.Keys.ToList();
                    if (_dragKeyTrack < tracks.Count)
                    {
                        var path = tracks[_dragKeyTrack];
                        var keys = clip.Tracks[path];
                        if (_dragKeyIdx < keys.Count)
                        {
                            float newTime = Math.Max(0, XToTime((float)pos.X));
                            var old = keys[_dragKeyIdx];
                            keys[_dragKeyIdx] = new AnimKeyframe(newTime, old.Value, old.Interpolation, old.InTangent, old.OutTangent);
                            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                            // Update drag index after sort
                            _dragKeyIdx = keys.FindIndex(k => MathF.Abs(k.Time - newTime) < 0.001f);
                            InvalidateVisual();
                        }
                    }
                }
            }

            _lastMouse = pos;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _draggingPlayhead = false;
            _dragKeyTrack = -1;
            _dragKeyIdx = -1;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // Zoom with mouse wheel
            float zoomFactor = e.Delta.Y > 0 ? 1.15f : 0.87f;
            float mouseTime = XToTime((float)e.GetPosition(this).X);

            _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * zoomFactor, 20f, 2000f);

            // Keep mouse position stable
            _scrollX = mouseTime * _pixelsPerSecond - (float)e.GetPosition(this).X;
            _scrollX = Math.Max(0, _scrollX);

            InvalidateVisual();
            e.Handled = true;
        }

        public void HandleDoubleTapped(Point pos)
        {
            // Double-click in timeline: add keyframe
            if (Panel?.ShowCurves != true && pos.Y > RulerHeight)
            {
                var clip = Panel?.CurrentClip;
                if (clip == null) return;

                var tracks = clip.Tracks.Keys.ToList();
                int trackIdx = (int)((pos.Y - RulerHeight) / TrackHeight);
                if (trackIdx >= 0 && trackIdx < tracks.Count)
                {
                    float time = XToTime((float)pos.X);
                    float value = Panel?.CurrentAnimator?.ReadPropertyValue(tracks[trackIdx]) ?? 0f;
                    clip.SetKey(tracks[trackIdx], time, value);
                    InvalidateVisual();
                }
            }
        }
    }

    // ============================================================
    //  StateMachineCanvas — visual state machine editor
    // ============================================================

    public class StateMachineCanvas : Control
    {
        public AnimationPanel? Panel { get; set; }

        // Interaction
        private int _dragNodeIdx = -1;
        private Point _dragOffset;
        private Point _lastMouse;
        private float _panX, _panY;

        private static readonly IBrush NodeBg = new SolidColorBrush(Color.FromRgb(50, 52, 58));
        private static readonly IBrush NodeActiveBg = new SolidColorBrush(Color.FromRgb(40, 90, 140));
        private static readonly IBrush NodeBorder = new SolidColorBrush(Color.FromRgb(80, 85, 95));
        private static readonly IBrush NodeText = Brushes.White;
        private static readonly IBrush ArrowBrush = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        private static readonly IBrush ArrowLabelBrush = new SolidColorBrush(Color.FromRgb(180, 200, 220));
        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(26, 27, 30));
        private static readonly Pen NodePen = new Pen(NodeBorder, 1.5);
        private static readonly Pen ArrowPen = new Pen(ArrowBrush, 1.5);

        private const float NodeW = 120, NodeH = 40;

        public override void Render(DrawingContext dc)
        {
            var anim = Panel?.CurrentAnimator;
            var bounds = Bounds;
            double w = bounds.Width, h = bounds.Height;

            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

            if (anim == null || anim.States.Count == 0)
            {
                var ft = new FormattedText("Right-click to add states",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12,
                    new SolidColorBrush(Color.FromRgb(100, 110, 130)));
                dc.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
                return;
            }

            var states = anim.States.Values.ToList();

            // Assign default positions if needed
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].EditorPosition == default)
                    states[i].EditorPosition = new System.Numerics.Vector2(80 + i * 160, 60 + (i % 3) * 80);
            }

            // Draw transitions (arrows)
            foreach (var t in anim.Transitions)
            {
                var from = states.FirstOrDefault(s => s.Name == t.FromState);
                var to = states.FirstOrDefault(s => s.Name == t.ToState);
                if (from == null || to == null) continue;

                var fp = NodeCenter(from);
                var tp = NodeCenter(to);

                dc.DrawLine(ArrowPen, fp, tp);

                // Arrowhead
                var dir = tp - fp;
                double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                if (len > 1)
                {
                    var nd = new Point(dir.X / len, dir.Y / len);
                    var mid = new Point((fp.X + tp.X) / 2, (fp.Y + tp.Y) / 2);

                    // Arrow tip near target node
                    var tipBase = new Point(tp.X - nd.X * (NodeW / 2 + 5), tp.Y - nd.Y * (NodeH / 2 + 5));
                    var perp = new Point(-nd.Y, nd.X);
                    var arrow = new StreamGeometry();
                    using (var ctx = arrow.Open())
                    {
                        ctx.BeginFigure(tipBase, true);
                        ctx.LineTo(new Point(tipBase.X - nd.X * 10 + perp.X * 5, tipBase.Y - nd.Y * 10 + perp.Y * 5));
                        ctx.LineTo(new Point(tipBase.X - nd.X * 10 - perp.X * 5, tipBase.Y - nd.Y * 10 - perp.Y * 5));
                        ctx.EndFigure(true);
                    }
                    dc.DrawGeometry(ArrowBrush, null, arrow);

                    // Label
                    string label = !string.IsNullOrEmpty(t.Condition) ? t.Condition : (t.HasExitTime ? $"exit@{t.ExitTime:0.##}" : "");
                    if (!string.IsNullOrEmpty(label))
                    {
                        var ft = new FormattedText(label,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 9, ArrowLabelBrush);
                        dc.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - ft.Height - 2));
                    }
                }
            }

            // Draw nodes
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var rect = NodeRect(state);
                bool isActive = anim.CurrentStateName == state.Name;

                dc.DrawRectangle(isActive ? NodeActiveBg : NodeBg, NodePen,
                    new Rect(rect.X, rect.Y, rect.Width, rect.Height), 6, 6);

                var ft = new FormattedText(state.Name,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, isActive ? FontWeight.Bold : FontWeight.Normal), 12, NodeText);
                dc.DrawText(ft, new Point(rect.X + rect.Width / 2 - ft.Width / 2, rect.Y + rect.Height / 2 - ft.Height / 2));
            }
        }

        private Point NodeCenter(AnimState state)
        {
            var ep = state.EditorPosition;
            return new Point(ep.X + _panX + NodeW / 2, ep.Y + _panY + NodeH / 2);
        }

        private Rect NodeRect(AnimState state)
        {
            var ep = state.EditorPosition;
            return new Rect(ep.X + _panX, ep.Y + _panY, NodeW, NodeH);
        }

        // ── Input ──

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pos = e.GetPosition(this);
            _lastMouse = pos;

            var anim = Panel?.CurrentAnimator;
            if (anim == null) return;

            // Right-click: context menu
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                ShowContextMenu(pos, anim);
                return;
            }

            // Check if clicking on a node
            var states = anim.States.Values.ToList();
            for (int i = 0; i < states.Count; i++)
            {
                var rect = NodeRect(states[i]);
                if (rect.Contains(pos))
                {
                    _dragNodeIdx = i;
                    _dragOffset = new Point(pos.X - rect.X, pos.Y - rect.Y);
                    e.Handled = true;
                    return;
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);

            if (_dragNodeIdx >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var anim = Panel?.CurrentAnimator;
                if (anim != null)
                {
                    var states = anim.States.Values.ToList();
                    if (_dragNodeIdx < states.Count)
                    {
                        states[_dragNodeIdx].EditorPosition = new System.Numerics.Vector2(
                            (float)(pos.X - _dragOffset.X - _panX),
                            (float)(pos.Y - _dragOffset.Y - _panY));
                        InvalidateVisual();
                    }
                }
            }
            else if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            {
                // Pan
                _panX += (float)(pos.X - _lastMouse.X);
                _panY += (float)(pos.Y - _lastMouse.Y);
                InvalidateVisual();
            }

            _lastMouse = pos;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragNodeIdx = -1;
        }

        private void ShowContextMenu(Point pos, Animator anim)
        {
            var menu = new ContextMenu();

            var addState = new MenuItem { Header = "Add State" };
            addState.Click += async (_, _) =>
            {
                var nameBox = new TextBox { Text = "New State", Width = 180 };
                var dialog = new Window
                {
                    Title = "Add State",
                    Width = 250, Height = 140,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(16), Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "State Name:" },
                            nameBox,
                            new Button { Content = "Add", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                        }
                    }
                };
                var btn = ((StackPanel)dialog.Content).Children.OfType<Button>().First();
                btn.Click += (_, _) => dialog.Close();

                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner != null) await dialog.ShowDialog(owner);

                var name = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(name)) return;

                var clip = new AnimationClip { Name = name, Duration = 1f };
                anim.AddState(name, clip);

                // Set editor position near click
                var states = anim.States;
                if (states.TryGetValue(name, out var state))
                    state.EditorPosition = new System.Numerics.Vector2((float)(pos.X - _panX), (float)(pos.Y - _panY));

                InvalidateVisual();
            };
            menu.Items.Add(addState);

            // Add transition (if states exist)
            if (anim.States.Count >= 2)
            {
                var addTrans = new MenuItem { Header = "Add Transition" };
                addTrans.Click += async (_, _) =>
                {
                    var stateNames = anim.States.Keys.ToList();
                    var fromBox = new ComboBox { ItemsSource = stateNames, SelectedIndex = 0, MinWidth = 120 };
                    var toBox = new ComboBox { ItemsSource = stateNames, SelectedIndex = Math.Min(1, stateNames.Count - 1), MinWidth = 120 };
                    var condBox = new TextBox { Watermark = "Parameter name (optional)", Width = 180 };

                    var dialog = new Window
                    {
                        Title = "Add Transition", Width = 300, Height = 260,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(16), Spacing = 6,
                            Children =
                            {
                                new TextBlock { Text = "From:" }, fromBox,
                                new TextBlock { Text = "To:" }, toBox,
                                new TextBlock { Text = "Condition:" }, condBox,
                                new Button { Content = "Add", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                            }
                        }
                    };
                    var btn = ((StackPanel)dialog.Content).Children.OfType<Button>().First();
                    btn.Click += (_, _) => dialog.Close();

                    var owner = TopLevel.GetTopLevel(this) as Window;
                    if (owner != null) await dialog.ShowDialog(owner);

                    var from = fromBox.SelectedItem as string;
                    var to = toBox.SelectedItem as string;
                    if (from == null || to == null || from == to) return;

                    anim.AddTransition(new AnimTransition
                    {
                        FromState = from,
                        ToState = to,
                        Condition = condBox.Text?.Trim() ?? "",
                        TransitionDuration = 0.2f
                    });

                    InvalidateVisual();
                };
                menu.Items.Add(addTrans);
            }

            // Delete state (check if right-clicked on a node)
            var states2 = anim.States.Values.ToList();
            for (int i = 0; i < states2.Count; i++)
            {
                var rect = NodeRect(states2[i]);
                if (rect.Contains(pos))
                {
                    var deleteName = states2[i].Name;
                    var deleteItem = new MenuItem { Header = $"Delete '{deleteName}'" };
                    deleteItem.Click += (_, _) =>
                    {
                        anim.RemoveState(deleteName);
                        InvalidateVisual();
                    };
                    menu.Items.Add(new Separator());
                    menu.Items.Add(deleteItem);
                    break;
                }
            }

            menu.Open(this);
        }
    }
}
