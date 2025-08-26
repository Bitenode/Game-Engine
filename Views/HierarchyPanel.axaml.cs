using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Game_Engine.Core;
using Game_Engine.Core.Importers;

namespace Game_Engine.Views;

public partial class HierarchyPanel : UserControl
{
    private const string DragFormat = "application/x-gameobject";

    private sealed class HierarchyViewModel
    {
        public ObservableCollection<GameObject> Root { get; } = new();

        public HierarchyViewModel()
        {
            Root.Add(new GameObject("Main Camera"));
            Root.Add(new GameObject("Directional Light"));
            Root.Add(new GameObject("Cube"));
        }

        public void AddEmpty(GameObject? parent = null)
        {
            var go = new GameObject("GameObject");
            if (parent is null) Root.Add(go); else parent.AddChild(go);
        }

        public void Delete(GameObject go)
        {
            if (go.Parent is null) Root.Remove(go);
            else go.Parent.Children.Remove(go);
        }

        public void Unparent(GameObject go)
        {
            var wasRoot = go.Parent is null;
            go.RemoveFromParent();
            if (!wasRoot && !Root.Contains(go))
                Root.Add(go);
        }
        public GameObject AddPrimitiveCube(GameObject? parent = null)
        {
            var go = new GameObject("Cube");
            go.AddBehavior(new MeshFilter { Mesh = Mesh.CreateCube(1f) });
            go.AddBehavior(new MeshRenderer());
            if (parent is null) Root.Add(go); else parent.AddChild(go);
            return go;
        }

    }

    private readonly HierarchyViewModel _vm;
    private GameObject? _contextTarget; // the item whose context menu is open

    public HierarchyPanel()
    {
        InitializeComponent();

        _vm = new HierarchyViewModel();
        DataContext = _vm;

        Game_Engine.Core.SceneService.AttachRoot(_vm.Root);


        Tree.SelectionChanged += (_, __) =>
        {
            var selected =
                Tree.SelectedItem as GameObject
                ?? (Tree.SelectedItem as TreeViewItem)?.DataContext as GameObject;

            SelectionService.Set(selected);
        };



        DragDrop.SetAllowDrop(Tree, true);
        Tree.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble);
        Tree.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);

        // Capture which node was right-clicked BEFORE the menu opens
        Tree.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    // ----- Context menu target capture -----
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        _contextTarget = tvi?.DataContext as GameObject;
    }

    // ----- Context menu handlers -----
    private void OnCreateChild(object? sender, RoutedEventArgs e)
        => _vm.AddEmpty(_contextTarget);

    private void OnUnparent(object? sender, RoutedEventArgs e)
    {
        if (_contextTarget is null) return;
        _vm.Unparent(_contextTarget);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_contextTarget is null) return;
        _vm.Delete(_contextTarget);
    }

    private void OnCreateCube(object? s, RoutedEventArgs e)
    {
        _vm.AddPrimitiveCube(_contextTarget);
        Game_Engine.Core.SceneService.NotifyChanged();
    }


    private async void OnImportModel(object? sender, RoutedEventArgs e)
    {
        var win = this.GetVisualRoot() as Window;
        var dlg = new OpenFileDialog
        {
            Title = "Import model",
            AllowMultiple = false,
            Filters =
        {
            new FileDialogFilter { Name="Models", Extensions = { "fbx","obj","gltf","glb","dae" } },
            new FileDialogFilter { Name="All files", Extensions = { "*" } }
        }
        };
        var files = await dlg.ShowAsync(win);
        if (files is null || files.Length == 0) return;

        try
        {
            var go = ModelImporter.ImportModel(files[0]);
            // Put it under the currently right-clicked target if there is one, else at root.
            if (_contextTarget is null) _vm.Root.Add(go); else _contextTarget.AddChild(go);

            // handy defaults
            SelectionService.Set(go);
            SceneService.NotifyChanged();
            Game_Engine.Core.Log.Success($"Imported model: {files[0]}");
        }
        catch (Exception ex)
        {
            Game_Engine.Core.Log.Error(ex, "Model import failed");
        }
    }




    // ----- Drag & drop parenting -----
    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        var go = tvi?.DataContext as GameObject;
        if (go is null) return;

        var data = new DataObject();
        data.Set(DragFormat, go);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var dragged = e.Data.Get(DragFormat) as GameObject;
        if (dragged is null) return;

        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        var target = tvi?.DataContext as GameObject;

        var ok = true;
        if (target is not null)
        {
            if (ReferenceEquals(target, dragged) || dragged.IsAncestorOf(target))
                ok = false; // can't drop onto self/descendant
        }

        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var dragged = e.Data.Get(DragFormat) as GameObject;
        if (dragged is null) return;

        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
        var target = tvi?.DataContext as GameObject;

        if (target is null)
        {
            // Dropped on empty area -> root
            _vm.Unparent(dragged);
        }
        else
        {
            // Prevent duplicate when dragging from root into a parent
            if (dragged.Parent is null)
                _vm.Root.Remove(dragged);

            target.AddChild(dragged);
        }

        e.Handled = true;
    }
}
