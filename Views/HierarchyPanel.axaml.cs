using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        public HierarchyPanel()
        {
            InitializeComponent();

            _vm = new HierarchyViewModel();
            DataContext = _vm;

            // Selection -> engine selection
            Tree.SelectionChanged += (_, __) =>
            {
                var selected =
                    Tree.SelectedItem as GameObject
                    ?? (Tree.SelectedItem as TreeViewItem)?.DataContext as GameObject;
                SelectionService.Set(selected);
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

        // ---------------- DnD parenting ----------------
        private void OnDragOver(object sender, DragEventArgs e)
        {
            var dragged = e.Data.Get(DragFormat) as GameObject;
            if (dragged == null) return;

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
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            var dragged = e.Data.Get(DragFormat) as GameObject;
            if (dragged == null) return;

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
        }
    }
}
