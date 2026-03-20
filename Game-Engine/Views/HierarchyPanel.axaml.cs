using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Importers;

namespace Game_Engine.Views
{
    public partial class HierarchyPanel : UserControl
    {
        private const string DragFormat = "application/x-gameobject";
        private const double DragThreshold = 4.0; // px before starting a drag

        // ViewModel that never creates a new collection unless the engine has none yet.
        private sealed class HierarchyViewModel
        {
            public ObservableCollection<GameObject> Root { get; }

            public HierarchyViewModel()
            {
                // If the engine already has a root, reuse it (no scene churn)
                if (SceneService.Root is ObservableCollection<GameObject> existing)
                {
                    Root = existing;
                    return;
                }

                // Otherwise, create one and attach once
                Root = new ObservableCollection<GameObject>();
                SceneService.AttachRoot(Root);

                // Default bootstrap (only once, for a brand new empty scene)
                AddDefaultCamera();
                AddDefaultLight();
                AddPrimitiveCube();
            }

            private static void AddDefaultCamera()
            {
                var cam = new GameObject("Main Camera");
                cam.AddBehavior(new Camera());
                SceneService.Root.Add(cam);
            }

            private static void AddDefaultLight()
            {
                var light = new GameObject("Directional Light");
                light.AddBehavior(new Light());
                light.Transform.Rotation.X = 90; 
                SceneService.Root.Add(light);
            }

            public void AddEmpty(GameObject parent = null)
            {
                var go = new GameObject("GameObject");
                if (parent == null) Root.Add(go); else parent.AddChild(go);
            }

            public void Delete(GameObject go)
            {
                if (go == null) return;
                if (go.Parent == null) Root.Remove(go);
                else go.Parent.Children.Remove(go);
            }

            public void Unparent(GameObject go)
            {
                if (go == null) return;
                bool wasRoot = go.Parent == null;
                go.RemoveFromParent();
                if (!wasRoot && !Root.Contains(go))
                    Root.Add(go);
            }

            public GameObject AddPrimitiveCube(GameObject parent = null)
            {
                var go = new GameObject("Cube");
                go.AddBehavior(new MeshFilter { Mesh = Mesh.CreateCube(1f) });
                go.AddBehavior(new MeshRenderer());
                if (parent == null) Root.Add(go); else parent.AddChild(go);
                return go;
            }

            public GameObject AddPrimitiveCone(GameObject parent = null)
            {
                var go = new GameObject("Cone");
                go.AddBehavior(new MeshFilter { Mesh = Mesh.CreateCone(1) });
                go.AddBehavior(new MeshRenderer());
                if (parent == null) Root.Add(go); else parent.AddChild(go);
                return go;
            }

            public GameObject AddPrimitiveCylinder(GameObject parent = null)
            {
                var go = new GameObject("Cylinder");
                go.AddBehavior(new MeshFilter { Mesh = Mesh.CreateCylinder(1) });
                go.AddBehavior(new MeshRenderer());
                if (parent == null) Root.Add(go); else parent.AddChild(go);
                return go;
            }
        }

        private readonly HierarchyViewModel _vm;
        private GameObject _contextTarget;

        // Drag gesture state
        private bool _leftPressed;
        private Point _pressPos;
        private GameObject _pressedItem;
        private bool _isDragging;

        // Re-entrancy guard so TreeView ↔ SelectionService don't fight
        private bool _syncingSelection;

        public HierarchyPanel()
        {
            InitializeComponent();

            _vm = new HierarchyViewModel();
            DataContext = _vm;

            // Selection -> engine selection (supports multi-select)
            Tree.SelectionChanged += (_, __) =>
            {
                if (_syncingSelection) return;         // break the loop

                _syncingSelection = true;
                try
                {
                    if (Tree.SelectedItems == null || Tree.SelectedItems.Count == 0)
                    {
                        SelectionService.Clear();
                        return;
                    }

                    var selectedGOs = new List<GameObject>();
                    foreach (var item in Tree.SelectedItems)
                    {
                        var go = item as GameObject
                            ?? (item as TreeViewItem)?.DataContext as GameObject;
                        if (go != null)
                            selectedGOs.Add(go);
                    }

                    if (selectedGOs.Count == 1)
                    {
                        SelectionService.Set(selectedGOs[0]);
                        SelectionService.RequestFrame(selectedGOs[0]);
                    }
                    else if (selectedGOs.Count > 1)
                    {
                        SelectionService.SetMultiple(selectedGOs);
                        SelectionService.RequestFrame(selectedGOs[0]);
                    }
                    else
                        SelectionService.Clear();
                }
                finally { _syncingSelection = false; }
            };

            // Listen for selection changes from other sources and sync hierarchy
            SelectionService.Changed += () =>
            {
                if (_syncingSelection) return;         // we are the source, skip

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_syncingSelection) return;
                    _syncingSelection = true;
                    try
                    {
                        var wanted = SelectionService.Selected;
                        if (wanted.Count == 0)
                        {
                            Tree.UnselectAll();
                            return;
                        }

                        // Build a set of currently selected in tree
                        var already = new HashSet<GameObject>();
                        if (Tree.SelectedItems != null)
                            foreach (var item in Tree.SelectedItems)
                            {
                                var go = item as GameObject
                                    ?? (item as TreeViewItem)?.DataContext as GameObject;
                                if (go != null) already.Add(go);
                            }

                        // Check if they already match
                        if (already.Count == wanted.Count && wanted.All(w => already.Contains(w)))
                            return;

                        // Sync: clear and re-select all
                        Tree.UnselectAll();
                        foreach (var go in wanted)
                        {
                            // SelectedItems.Add works for TreeView with Multiple selection mode
                            try { Tree.SelectedItems!.Add(go); } catch { }
                        }
                    }
                    finally { _syncingSelection = false; }
                });
            };

            // Drag & drop wiring
            DragDrop.SetAllowDrop(Tree, true);

            // Drag gesture: press -> move beyond threshold -> start drag
            Tree.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            Tree.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
            Tree.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);

            // DnD target handling
            Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble);
            Tree.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);

            // Context menu target capture
            Tree.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        }

        // ---------------- Context menu target capture ----------------
        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var v = e.Source as Visual;
            var tvi = v?.FindAncestorOfType<TreeViewItem>();
            _contextTarget = tvi?.DataContext as GameObject;
        }

        // ---------------- Context menu handlers ----------------
        private void OnCreateChild(object sender, RoutedEventArgs e)
        {
            _vm.AddEmpty(_contextTarget);
            SceneService.NotifyChanged();
        }

        private void OnCreateCube(object sender, RoutedEventArgs e)
        {
            _vm.AddPrimitiveCube(_contextTarget);
            SceneService.NotifyChanged();
        }

        private void OnCreateCone(object sender, RoutedEventArgs e)
        {
            _vm.AddPrimitiveCone(_contextTarget);
            SceneService.NotifyChanged();
        }

        private void OnCreateCylinder(object sender, RoutedEventArgs e)
        {
            _vm.AddPrimitiveCylinder(_contextTarget);
            SceneService.NotifyChanged();
        }

        private async void OnImportModel(object sender, RoutedEventArgs e)
        {
            var win = this.GetVisualRoot() as Window;
            var dlg = new OpenFileDialog
            {
                Title = "Import model",
                AllowMultiple = false,
                Filters =
                {
                    new FileDialogFilter { Name = "Models", Extensions = { "fbx","obj","gltf","glb","dae" } },
                    new FileDialogFilter { Name = "All files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            if (files == null || files.Length == 0) return;

            try
            {
                var go = ModelImporter.ImportModel(files[0]);
                if (_contextTarget == null) _vm.Root.Add(go); else _contextTarget.AddChild(go);

                SelectionService.Set(go);
                SceneService.NotifyChanged();
                Log.Success("Imported model: " + files[0]);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Model import failed");
            }
        }

        private void OnUnparent(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null) return;
            _vm.Unparent(_contextTarget);
            SceneService.NotifyChanged();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null) return;
            _vm.Delete(_contextTarget);
            SceneService.NotifyChanged();
        }

        // ---------------- Prefab handlers ----------------

        private async void OnCreatePrefab(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null) return;

            var win = this.GetVisualRoot() as Window;
            if (win == null) return;

            var sfd = new SaveFileDialog
            {
                Title = "Create Prefab",
                InitialFileName = (_contextTarget.Name ?? "NewPrefab") + ".prefab",
                Filters = { new FileDialogFilter { Name = "Prefab", Extensions = { "prefab" } } }
            };

            var proj = ProjectService.Current;
            if (proj != null)
            {
                var prefabDir = System.IO.Path.Combine(proj.RootPath, "Assets", "Prefabs");
                if (!System.IO.Directory.Exists(prefabDir))
                    System.IO.Directory.CreateDirectory(prefabDir);
                sfd.Directory = prefabDir;
            }

            var dest = await sfd.ShowAsync(win);
            if (string.IsNullOrWhiteSpace(dest)) return;

            // Make project-relative
            string relPath = dest;
            if (proj != null)
                relPath = System.IO.Path.GetRelativePath(proj.RootPath, System.IO.Path.GetFullPath(dest));

            var prefab = Prefab.CreateFrom(_contextTarget, relPath);
            prefab.Save();

            SceneService.NotifyChanged();
            Log.Success($"Created prefab: {prefab.Name} → {relPath}");
        }

        private async void OnInstantiatePrefab(object sender, RoutedEventArgs e)
        {
            var win = this.GetVisualRoot() as Window;
            if (win == null) return;

            var ofd = new OpenFileDialog
            {
                Title = "Instantiate Prefab",
                AllowMultiple = false,
                Filters = { new FileDialogFilter { Name = "Prefab", Extensions = { "prefab" } } }
            };

            var files = await ofd.ShowAsync(win);
            if (files == null || files.Length == 0) return;

            // Make project-relative
            string relPath = files[0];
            var proj = ProjectService.Current;
            if (proj != null)
            {
                var abs = System.IO.Path.GetFullPath(files[0]);
                var root = System.IO.Path.GetFullPath(proj.RootPath);
                if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    relPath = System.IO.Path.GetRelativePath(root, abs);
            }

            var prefab = Prefab.Load(relPath);
            if (prefab == null)
            {
                Log.Warning($"Failed to load prefab: {relPath}");
                return;
            }

            var instance = prefab.Instantiate(_contextTarget);
            if (instance != null)
            {
                SelectionService.Set(instance);
                SceneService.NotifyChanged();
            }
        }

        private void OnApplyToPrefab(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null || !Prefab.IsPrefabInstance(_contextTarget))
            {
                Log.Warning("Selected object is not a prefab instance.");
                return;
            }

            var prefab = Prefab.Load(_contextTarget.PrefabPath);
            if (prefab == null)
            {
                Log.Warning($"Could not load prefab: {_contextTarget.PrefabPath}");
                return;
            }

            prefab.UpdateFromInstance(_contextTarget);
            prefab.ApplyToInstances();
            SceneService.NotifyChanged();
        }

        private void OnRevertToPrefab(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null || !Prefab.IsPrefabInstance(_contextTarget))
            {
                Log.Warning("Selected object is not a prefab instance.");
                return;
            }

            Prefab.RevertInstance(_contextTarget);
        }

        private void OnUnpackPrefab(object sender, RoutedEventArgs e)
        {
            if (_contextTarget == null || !Prefab.IsPrefabInstance(_contextTarget))
            {
                Log.Warning("Selected object is not a prefab instance.");
                return;
            }

            Prefab.Unpack(_contextTarget);
        }

        private void OnExpandAll(object sender, RoutedEventArgs e) => SetExpandedForScope(true);
        private void OnCollapseAll(object sender, RoutedEventArgs e) => SetExpandedForScope(false);

        private void SetExpandedForScope(bool expand)
        {
            var all = Tree.GetVisualDescendants().OfType<TreeViewItem>();
            foreach (var tvi in all)
            {
                var go = tvi.DataContext as GameObject;
                if (go == null) continue;

                if (_contextTarget == null ||
                    ReferenceEquals(go, _contextTarget) ||
                    (_contextTarget != null && _contextTarget.IsAncestorOf(go)))
                {
                    tvi.IsExpanded = expand;
                }
            }
        }

        // ---------------- Safe drag gesture (no drag on simple click/double-click/expander) ----------------
        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) return;

            // If the click happened on the TreeViewItem's expander toggle, let it expand/collapse
            var v = e.Source as Visual;
            if (v != null && v.FindAncestorOfType<ToggleButton>() != null)
                return;

            var tvi = v?.FindAncestorOfType<TreeViewItem>();
            var go = tvi?.DataContext as GameObject;
            if (go == null) return;

            _leftPressed = true;
            _pressPos = e.GetPosition(this);
            _pressedItem = go;
        }

        private async void OnPointerMoved(object sender, PointerEventArgs e)
        {
            if (!_leftPressed || _isDragging || _pressedItem == null) return;

            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _pressPos.X) < DragThreshold &&
                Math.Abs(pos.Y - _pressPos.Y) < DragThreshold)
                return;

            _isDragging = true;

            var data = new DataObject();
            data.Set(DragFormat, _pressedItem);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);

            _isDragging = false;
            _leftPressed = false;
            _pressedItem = null;
        }

        private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _leftPressed = false;
            _pressedItem = null;
        }

        // ---------------- DnD parenting + prefab drop ----------------
        private void OnDragOver(object sender, DragEventArgs e)
        {
            // Accept internal GO reparenting
            var dragged = e.Data.Get(DragFormat) as GameObject;
            if (dragged != null)
            {
                var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
                var target = tvi?.DataContext as GameObject;

                var ok = true;
                if (target != null)
                {
                    if (ReferenceEquals(target, dragged) || dragged.IsAncestorOf(target))
                        ok = false;
                }

                e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Accept .prefab files from ProjectPanel (or OS file drop)
            if (e.Data.Contains(DataFormats.FileNames))
            {
                var files = e.Data.GetFileNames()?.ToList();
                if (files != null && files.Any(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }

            // Also accept the project-node-path format (ProjectPanel internal format)
            if (e.Data.Contains("project-node-path"))
            {
                var path = e.Data.Get("project-node-path") as string;
                if (path != null && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            // Internal GO reparenting
            var dragged = e.Data.Get(DragFormat) as GameObject;
            if (dragged != null)
            {
                var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
                var target = tvi?.DataContext as GameObject;

                if (target == null)
                {
                    if (dragged.Parent != null)
                        _vm.Unparent(dragged);
                }
                else
                {
                    if (dragged.Parent == null)
                        _vm.Root.Remove(dragged);

                    target.AddChild(dragged);
                }

                e.Handled = true;
                SceneService.NotifyChanged();
                return;
            }

            // Prefab file drop
            string prefabPath = null;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var files = e.Data.GetFileNames()?.ToList();
                prefabPath = files?.FirstOrDefault(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            }
            if (prefabPath == null && e.Data.Contains("project-node-path"))
            {
                var path = e.Data.Get("project-node-path") as string;
                if (path != null && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    prefabPath = path;
            }

            if (prefabPath != null)
            {
                // Make project-relative
                string relPath = prefabPath;
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var abs = Path.GetFullPath(prefabPath);
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        relPath = Path.GetRelativePath(root, abs);
                }

                var prefab = Prefab.Load(relPath);
                if (prefab == null)
                {
                    Log.Warning($"Failed to load prefab: {relPath}");
                    return;
                }

                // Drop target
                var dropTvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
                var dropParent = dropTvi?.DataContext as GameObject;

                var instance = prefab.Instantiate(dropParent);
                if (instance != null)
                {
                    SelectionService.Set(instance);
                    SceneService.NotifyChanged();
                    Log.Success($"Instantiated prefab: {prefab.Name}");
                }

                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Converts a GameObject's PrefabId to a foreground brush.
    /// Blue (#5599FF) for prefab instances, unset (theme default) for normal objects.
    /// </summary>
    public class PrefabColorConverter : IValueConverter
    {
        public static readonly PrefabColorConverter Instance = new();

        private static readonly IBrush PrefabBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x99, 0xFF));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var id = value as string;
            if (!string.IsNullOrEmpty(id))
                return PrefabBrush;
            return AvaloniaProperty.UnsetValue;  // let the theme decide the default color
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Multi-value converter for the Hierarchy TreeView foreground colour.
    /// Values[0] = PrefabId (string?),  Values[1] = IsActiveInHierarchy (bool).
    /// Disabled objects (or children of disabled objects) are shown in red.
    /// Active prefab instances are shown in blue. Everything else uses the theme default.
    /// </summary>
    public class HierarchyForegroundConverter : IMultiValueConverter
    {
        public static readonly HierarchyForegroundConverter Instance = new();

        private static readonly IBrush DisabledBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0x44, 0x44));
        private static readonly IBrush PrefabBrush   = new SolidColorBrush(Color.FromRgb(0x55, 0x99, 0xFF));

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isActive = true;
            if (values.Count > 1 && values[1] is bool b) isActive = b;

            if (!isActive) return DisabledBrush;

            var prefabId = values.Count > 0 ? values[0] as string : null;
            if (!string.IsNullOrEmpty(prefabId)) return PrefabBrush;

            return AvaloniaProperty.UnsetValue;
        }
    }
}
