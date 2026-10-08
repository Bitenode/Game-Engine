#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using AvGrid = Avalonia.Controls.Grid;
using PathShape = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;
using Game_Engine.Core.Blueprint;

namespace Game_Engine.Views;

public partial class BlueprintGraphPanel : UserControl
{
    const double NodeW = 190;
    const double HeaderH = 36;
    const double PinSpacing = 18;
    const double CommentNodeH = 52;
    const double CategoryBarW = 4;
    const double InPinX = 14;
    const double OutPinX = NodeW - 14;
    const double PinDotSize = 11;
    const double WireCompletePinRadius = 28;
    const double MinBodyH = 16;

    BlueprintDocument _doc = new();
    string _activeGraphKey = "__event";
    string? _currentFileAbs;
    bool _wired;
    bool _dirty;
    bool _syncPropFields;
    bool _syncGraphTabs;
    bool _suppressUndoCapture;

    readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    string? _primaryId;

    readonly Dictionary<(string NodeId, string PinId), Point> _pinCenters = new();

    readonly Stack<string> _undoStack = new();
    readonly Stack<string> _redoStack = new();

    /// <summary>Holds nodes/wires; receives pan/zoom. Parent <see cref="GraphCanvas"/> stays untransformed.</summary>
    readonly Canvas _worldCanvas = new()
    {
        Background = null,
        IsHitTestVisible = true
    };

    double _viewZoom = 1;
    Point _viewPan;

    bool _middlePanning;
    Point _middlePanPointerStart;
    Point _viewPanAtMiddleStart;

    List<BlueprintNode>? _dragNodes;
    Dictionary<string, Point>? _dragNodeStartPos;
    Point _dragPointerStartCanvas;
    Border? _dragCaptureBorder;
    bool _dragUndoCaptured;

    string? _wireFromId;
    string _wireFromPin = BlueprintFlowRuntime.PinExecOut;
    BlueprintPinType _wireFromType = BlueprintPinType.Exec;
    Line? _wirePreviewLine;

    BlueprintVariableDecl? _selectedVar;
    BlueprintFunctionDecl? _selectedFunc;

    Popup? _palettePopup;

    BlueprintGraph ActiveGraph => GetGraphByKey(_activeGraphKey);

    public BlueprintGraphPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    BlueprintGraph GetGraphByKey(string key)
    {
        if (string.Equals(key, "__event", StringComparison.OrdinalIgnoreCase))
            return _doc.Graph;
        var fn = _doc.Functions.FirstOrDefault(f =>
            string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
        return fn?.Graph ?? _doc.Graph;
    }

    void OnLoaded(object? s, RoutedEventArgs e)
    {
        if (_wired) return;
        _wired = true;

        HookMenu(MiNew, (_, _) => _ = OnNewAsync());
        HookMenu(MiOpen, (_, _) => _ = OnOpenAsync());
        HookMenu(MiSave, (_, _) => _ = OnSaveAsync());
        HookMenu(MiSaveAs, (_, _) => _ = OnSaveAsAsync());
        HookMenu(MiUndo, (_, _) => OnUndo());
        HookMenu(MiRedo, (_, _) => OnRedo());
        HookMenu(MiClear, (_, _) => ClearActiveGraph());
        HookMenu(MiDuplicate, (_, _) => DuplicateSelectedNode());
        HookMenu(MiDeleteNode, (_, _) => DeleteSelectedNode());
        HookMenu(MiValidate, (_, _) => RunValidation());
        HookMenu(MiSmokeChecks, (_, _) => RunSmokeChecks());

        if (MiInsertRoot != null)
        {
            MiInsertRoot.Items.Clear();
            foreach (var kind in BlueprintNodeCatalog.AuthoringPaletteOrdered)
            {
                var def = BlueprintNodeCatalog.Resolve(kind);
                var item = new MenuItem { Header = $"{def.DefaultTitle}\t({kind})" };
                var k = kind;
                item.Click += (_, _) => AddNodeKind(k);
                MiInsertRoot.Items.Add(item);
            }
        }

        if (NodeKindCombo != null)
        {
            NodeKindCombo.Items.Clear();
            foreach (var kind in BlueprintNodeCatalog.AuthoringPaletteOrdered)
            {
                var def = BlueprintNodeCatalog.Resolve(kind);
                NodeKindCombo.Items.Add(new ComboBoxItem
                {
                    Content = def.DefaultTitle,
                    Tag = kind
                });
            }
            NodeKindCombo.SelectedIndex = 0;
        }

        if (BtnAddNode != null) BtnAddNode.Click += (_, _) => AddNodeFromCombo();
        if (BtnValidate != null) BtnValidate.Click += (_, _) => RunValidation();
        if (BtnAddVar != null) BtnAddVar.Click += (_, _) => AddVariable();
        if (BtnAddFunction != null) BtnAddFunction.Click += (_, _) => AddFunction();

        if (VarList != null)
        {
            VarList.SelectionChanged += OnVarListSelectionChanged;
            VarList.DoubleTapped += (_, _) =>
            {
                if (_selectedVar != null)
                    AddVarAccessNode("GetVar", _selectedVar, 120 + ActiveGraph.Nodes.Count * 12, 120);
            };
            VarList.ContextMenu = BuildVarContextMenu();
        }

        if (FuncList != null)
        {
            FuncList.SelectionChanged += OnFuncListSelectionChanged;
            FuncList.DoubleTapped += (_, _) =>
            {
                if (_selectedFunc != null)
                    SwitchToGraph(_selectedFunc.Name);
            };
        }

        if (GraphTabs != null)
            GraphTabs.SelectionChanged += OnGraphTabsSelectionChanged;

        if (GraphCanvas != null)
        {
            GraphCanvas.Focusable = true;
            if (!GraphCanvas.Children.Contains(_worldCanvas))
                GraphCanvas.Children.Add(_worldCanvas);
            GraphCanvas.PointerMoved += OnGraphCanvasPointerMoved;
            GraphCanvas.PointerReleased += OnGraphCanvasPointerReleased;
            GraphCanvas.KeyDown += OnGraphCanvasKeyDown;
            GraphCanvas.PointerPressed += OnGraphCanvasPointerPressed;
            GraphCanvas.PointerWheelChanged += OnGraphViewportPointerWheelChanged;
            ApplyViewTransform();
        }

        PointerMoved += OnPanelPointerMoved;
        PointerReleased += OnPanelPointerReleased;

        if (TxtPropTitle != null)
            TxtPropTitle.LostFocus += OnPropTitleLostFocus;

        KeyDown += OnPanelKeyDown;

        CaptureUndo();
        RefreshPathDisplay();
        RefreshUi();
    }

    // ── Undo / Redo ──

    void CaptureUndo()
    {
        if (_suppressUndoCapture) return;
        _undoStack.Push(BlueprintPersistence.SnapshotJson(_doc));
        _redoStack.Clear();
        UpdateUndoRedoMenu();
    }

    void RestoreDocument(string json)
    {
        try
        {
            _suppressUndoCapture = true;
            _doc = BlueprintPersistence.CloneFromSnapshot(json);
            _suppressUndoCapture = false;
            if (!GraphKeyExists(_activeGraphKey))
                _activeGraphKey = "__event";
            _selectedIds.Clear();
            _primaryId = null;
            _selectedVar = null;
            _selectedFunc = null;
            RebuildGraphTabs(selectKey: _activeGraphKey);
            RefreshUi();
        }
        catch
        {
            _suppressUndoCapture = false;
        }
    }

    void OnUndo()
    {
        if (_undoStack.Count <= 1) return;
        _redoStack.Push(_undoStack.Pop());
        if (_undoStack.Count > 0)
            RestoreDocument(_undoStack.Peek());
        SetStatus("Undo.");
    }

    void OnRedo()
    {
        if (_redoStack.Count == 0) return;
        var json = _redoStack.Pop();
        _undoStack.Push(json);
        RestoreDocument(json);
        SetStatus("Redo.");
    }

    void UpdateUndoRedoMenu()
    {
        if (MiUndo != null) MiUndo.IsEnabled = _undoStack.Count > 1;
        if (MiRedo != null) MiRedo.IsEnabled = _redoStack.Count > 0;
    }

    bool GraphKeyExists(string key)
    {
        if (string.Equals(key, "__event", StringComparison.OrdinalIgnoreCase)) return true;
        return _doc.Functions.Any(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    // ── Graph tabs & lists ──

    void RebuildGraphTabs(string? selectKey = null)
    {
        if (GraphTabs == null) return;
        _syncGraphTabs = true;
        try
        {
            var key = selectKey ?? _activeGraphKey;
            GraphTabs.Items.Clear();
            GraphTabs.Items.Add(new TabItem { Header = "Event Graph", Tag = "__event" });
            foreach (var fn in _doc.Functions)
            {
                GraphTabs.Items.Add(new TabItem { Header = fn.Name, Tag = fn.Name });
            }

            for (int i = 0; i < GraphTabs.Items.Count; i++)
            {
                if (GraphTabs.Items[i] is TabItem ti
                    && ti.Tag is string tag
                    && string.Equals(tag, key, StringComparison.OrdinalIgnoreCase))
                {
                    GraphTabs.SelectedIndex = i;
                    return;
                }
            }
            GraphTabs.SelectedIndex = 0;
            _activeGraphKey = "__event";
        }
        finally
        {
            _syncGraphTabs = false;
        }
    }

    void OnGraphTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncGraphTabs) return;
        if (GraphTabs?.SelectedItem is not TabItem ti || ti.Tag is not string key) return;
        if (string.Equals(key, _activeGraphKey, StringComparison.OrdinalIgnoreCase)) return;
        _activeGraphKey = key;
        _selectedIds.Clear();
        _primaryId = null;
        RefreshUi();
    }

    void SwitchToGraph(string functionName)
    {
        _activeGraphKey = functionName;
        RebuildGraphTabs(selectKey: functionName);
        _selectedIds.Clear();
        _primaryId = null;
        RefreshUi();
    }

    void RefreshVarFuncLists()
    {
        if (VarList != null)
        {
            VarList.ItemsSource = _doc.Variables
                .Select(v => $"{v.Name}  ({v.Type})")
                .ToList();
            if (_selectedVar != null)
            {
                var idx = _doc.Variables.FindIndex(v =>
                    string.Equals(v.Name, _selectedVar.Name, StringComparison.OrdinalIgnoreCase));
                VarList.SelectedIndex = idx >= 0 ? idx : -1;
            }
        }

        if (FuncList != null)
        {
            FuncList.ItemsSource = _doc.Functions.Select(f => f.Name).ToList();
            if (_selectedFunc != null)
            {
                var idx = _doc.Functions.FindIndex(f =>
                    string.Equals(f.Name, _selectedFunc.Name, StringComparison.OrdinalIgnoreCase));
                FuncList.SelectedIndex = idx >= 0 ? idx : -1;
            }
        }
    }

    void OnVarListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VarList?.SelectedIndex is int idx && idx >= 0 && idx < _doc.Variables.Count)
            _selectedVar = _doc.Variables[idx];
        else
            _selectedVar = null;
    }

    void OnFuncListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FuncList?.SelectedIndex is int idx && idx >= 0 && idx < _doc.Functions.Count)
            _selectedFunc = _doc.Functions[idx];
        else
            _selectedFunc = null;
    }

    ContextMenu BuildVarContextMenu()
    {
        var menu = new ContextMenu();
        var getItem = new MenuItem { Header = "Add Get node" };
        getItem.Click += (_, _) =>
        {
            if (_selectedVar != null)
                AddVarAccessNode("GetVar", _selectedVar, 100, 100);
        };
        var setItem = new MenuItem { Header = "Add Set node" };
        setItem.Click += (_, _) =>
        {
            if (_selectedVar != null)
                AddVarAccessNode("SetVar", _selectedVar, 140, 140);
        };
        menu.Items.Add(getItem);
        menu.Items.Add(setItem);
        return menu;
    }

    void AddVariable()
    {
        CaptureUndo();
        var name = UniqueVarName();
        _doc.Variables.Add(new BlueprintVariableDecl
        {
            Name = name,
            Type = BlueprintPinType.Float,
            DefaultLiteral = "0",
            InstanceEditable = true
        });
        MarkDirty(true);
        RefreshUi();
        SetStatus($"Added variable '{name}'.");
    }

    string UniqueVarName(string baseName = "NewVar")
    {
        if (!_doc.Variables.Any(v => string.Equals(v.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}{i}";
            if (!_doc.Variables.Any(v => string.Equals(v.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return Guid.NewGuid().ToString("N")[..8];
    }

    void AddFunction()
    {
        CaptureUndo();
        var name = UniqueFunctionName();
        var fn = new BlueprintFunctionDecl { Name = name };
        var entry = fn.Graph.AddNode("FunctionEntry", name, 80, 80);
        entry.FunctionName = name;
        _doc.Functions.Add(fn);
        _selectedFunc = fn;
        MarkDirty(true);
        RebuildGraphTabs(selectKey: name);
        _activeGraphKey = name;
        RefreshUi();
        SetStatus($"Added function '{name}'.");
    }

    string UniqueFunctionName(string baseName = "NewFunction")
    {
        if (!_doc.Functions.Any(f => string.Equals(f.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}{i}";
            if (!_doc.Functions.Any(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return Guid.NewGuid().ToString("N")[..8];
    }

    void AddVarAccessNode(string kind, BlueprintVariableDecl var, double x, double y)
    {
        CaptureUndo();
        var title = string.Equals(kind, "GetVar", StringComparison.OrdinalIgnoreCase)
            ? $"Get {var.Name}"
            : $"Set {var.Name}";
        var n = ActiveGraph.AddNode(kind, title, x, y);
        n.VariableName = var.Name;
        MarkDirty(true);
        RefreshUi();
        SetStatus($"Added {kind} for '{var.Name}'.");
    }

    // ── View transform (on _worldCanvas — never on GraphCanvas) ──

    Point ScreenToWorld(Point screen) =>
        new((screen.X - _viewPan.X) / _viewZoom, (screen.Y - _viewPan.Y) / _viewZoom);

    void ApplyViewTransform()
    {
        _worldCanvas.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        _worldCanvas.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_viewZoom, _viewZoom),
                new TranslateTransform(_viewPan.X, _viewPan.Y)
            }
        };
    }

    void ResetGraphView()
    {
        _viewZoom = 1;
        _viewPan = default;
        ApplyViewTransform();
    }

    void OnGraphViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (GraphCanvas == null) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        double oldZ = _viewZoom;
        double factor = e.Delta.Y > 0 ? 1.1 : (e.Delta.Y < 0 ? 1 / 1.1 : 1);
        if (Math.Abs(factor - 1) < 0.001) return;
        double newZ = Math.Clamp(oldZ * factor, 0.25, 3.0);
        var pos = e.GetPosition(GraphCanvas);
        double gX = (pos.X - _viewPan.X) / oldZ;
        double gY = (pos.Y - _viewPan.Y) / oldZ;
        _viewZoom = newZ;
        _viewPan = new Point(pos.X - gX * newZ, pos.Y - gY * newZ);
        ApplyViewTransform();
    }

    // ── Input ──

    void OnGraphCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z) { OnUndo(); e.Handled = true; return; }
            if (e.Key == Key.Y) { OnRedo(); e.Handled = true; return; }
        }

        if (e.Key == Key.Delete && _selectedIds.Count > 0)
        {
            DeleteNodesByIds(_selectedIds.ToArray());
            e.Handled = true;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            ResetGraphView();
            e.Handled = true;
        }
    }

    void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = OnSaveAsync();
            e.Handled = true;
        }
    }

    void OnGraphCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GraphCanvas == null) return;
        var pt = e.GetCurrentPoint(GraphCanvas);

        if (pt.Properties.IsMiddleButtonPressed)
        {
            _middlePanning = true;
            _middlePanPointerStart = e.GetPosition(GraphCanvas);
            _viewPanAtMiddleStart = _viewPan;
            e.Pointer.Capture(GraphCanvas);
            e.Handled = true;
            return;
        }

        // Empty-canvas clicks only (not bubbling from nodes)
        bool onEmpty = ReferenceEquals(e.Source, GraphCanvas) || ReferenceEquals(e.Source, _worldCanvas);

        if (pt.Properties.IsRightButtonPressed && onEmpty)
        {
            var pos = ScreenToWorld(e.GetPosition(GraphCanvas));
            ShowNodePaletteAt(pos, _wireFromType != BlueprintPinType.Exec ? _wireFromType : null);
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsLeftButtonPressed && onEmpty)
        {
            ClearGraphSelection(redraw: true);
            GraphCanvas.Focus();
            e.Handled = true;
        }
    }

    void OnGraphCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (GraphCanvas == null) return;

        if (_middlePanning && ReferenceEquals(e.Pointer.Captured, GraphCanvas))
        {
            var p = e.GetPosition(GraphCanvas);
            _viewPan = _viewPanAtMiddleStart + (p - _middlePanPointerStart);
            ApplyViewTransform();
            e.Handled = true;
            return;
        }

        if (_dragNodes != null && _dragNodeStartPos != null && _dragCaptureBorder != null
            && ReferenceEquals(e.Pointer.Captured, _dragCaptureBorder))
        {
            var cur = ScreenToWorld(e.GetPosition(GraphCanvas));
            var delta = cur - _dragPointerStartCanvas;
            foreach (var n in _dragNodes)
            {
                if (_dragNodeStartPos.TryGetValue(n.Id, out var start))
                {
                    n.X = Math.Max(0, start.X + delta.X);
                    n.Y = Math.Max(0, start.Y + delta.Y);
                }
            }
            SyncDraggedBorderPositions();
            RecomputePinCentersFromGraph();
            RefreshWirePathsOnly();
            e.Handled = true;
        }
    }

    void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (GraphCanvas == null) return;
        if (!string.IsNullOrEmpty(_wireFromId) && _wirePreviewLine != null
            && ReferenceEquals(e.Pointer.Captured, this))
        {
            _wirePreviewLine.EndPoint = ScreenToWorld(e.GetPosition(GraphCanvas));
            e.Handled = true;
        }
    }

    void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (GraphCanvas == null) return;
        if (!string.IsNullOrEmpty(_wireFromId) && ReferenceEquals(e.Pointer.Captured, this))
        {
            var end = ScreenToWorld(e.GetPosition(GraphCanvas));
            var connected = TryCompleteWire(end);
            if (_wirePreviewLine != null)
            {
                _worldCanvas.Children.Remove(_wirePreviewLine);
                _wirePreviewLine = null;
            }

            if (!connected)
                ShowNodePaletteAt(end, _wireFromType != BlueprintPinType.Exec ? _wireFromType : null);
            else
            {
                MarkDirty(true);
                RefreshUi();
            }

            _wireFromId = null;
            _wireFromPin = BlueprintFlowRuntime.PinExecOut;
            _wireFromType = BlueprintPinType.Exec;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    void OnGraphCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (GraphCanvas == null) return;

        if (_middlePanning && ReferenceEquals(e.Pointer.Captured, GraphCanvas))
        {
            _middlePanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_dragNodes != null && _dragCaptureBorder != null
            && ReferenceEquals(e.Pointer.Captured, _dragCaptureBorder))
        {
            e.Pointer.Capture(null);
            _dragNodes = null;
            _dragNodeStartPos = null;
            _dragCaptureBorder = null;
            _dragUndoCaptured = false;
            MarkDirty(true);
            RefreshUi();
            e.Handled = true;
        }
    }

    // ── Node palette popup ──

    void ShowNodePaletteAt(Point canvasPos, BlueprintPinType? filterType)
    {
        ClosePalettePopup();

        var filterBox = new TextBox
        {
            Watermark = "Search nodes…",
            FontSize = 11,
            Margin = new Thickness(6)
        };

        var list = new ListBox
        {
            MaxHeight = 280,
            Width = 260,
            FontSize = 11,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA))
        };

        void RefreshList(string filter)
        {
            var q = filter.Trim();
            var items = BlueprintNodeCatalog.AuthoringPaletteOrdered
                .Select(k => (Kind: k, Def: BlueprintNodeCatalog.Resolve(k)))
                .Where(x => string.IsNullOrEmpty(q)
                            || x.Def.DefaultTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || x.Kind.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Where(x => !filterType.HasValue || NodeKindMatchesPinFilter(x.Kind, filterType.Value))
                .Select(x => new ComboBoxItem { Content = x.Def.DefaultTitle, Tag = x.Kind })
                .Cast<object>()
                .ToList();
            list.ItemsSource = items;
        }

        RefreshList("");
        filterBox.TextChanged += (_, _) => RefreshList(filterBox.Text ?? "");

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ComboBoxItem cbi && cbi.Tag is string kind)
            {
                ClosePalettePopup();
                AddNodeKind(kind, canvasPos.X, canvasPos.Y);
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(filterBox);
        panel.Children.Add(list);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = panel
        };

        _palettePopup = new Popup
        {
            PlacementTarget = GraphCanvas,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            Child = border
        };

        if (GraphCanvas != null)
        {
            _palettePopup.HorizontalOffset = canvasPos.X * _viewZoom + _viewPan.X;
            _palettePopup.VerticalOffset = canvasPos.Y * _viewZoom + _viewPan.Y;
        }

        _palettePopup.Closed += (_, _) => _palettePopup = null;
        _palettePopup.Open();
        filterBox.Focus();
    }

    static bool NodeKindMatchesPinFilter(string kind, BlueprintPinType filterType)
    {
        if (filterType == BlueprintPinType.Exec) return true;
        var def = BlueprintNodeCatalog.Resolve(kind);
        return def.Pins.Any(p =>
            p.Direction == BlueprintPinDirection.Input
            && BlueprintValue.AreTypesCompatible(filterType, p.Type));
    }

    void ClosePalettePopup()
    {
        if (_palettePopup != null)
        {
            _palettePopup.IsOpen = false;
            _palettePopup = null;
        }
    }

    // ── Pin layout ──

    static double ComputeNodeHeight(BlueprintNode n, BlueprintDocument doc)
    {
        var def = BlueprintNodeCatalog.Resolve(n.Kind);
        if (def.Category == BlueprintNodeCategory.Comment)
            return CommentNodeH;

        var pins = BlueprintNodeCatalog.ResolvePins(n, doc);
        int inCount = pins.Count(p => p.Direction == BlueprintPinDirection.Input);
        int outCount = pins.Count(p => p.Direction == BlueprintPinDirection.Output);
        int rows = Math.Max(inCount, outCount);
        if (rows == 0) return HeaderH + MinBodyH;
        return HeaderH + rows * PinSpacing + 8;
    }

    static double PinCenterY(BlueprintNode n, int rowIndex)
        => n.Y + HeaderH + 4 + rowIndex * PinSpacing + PinSpacing * 0.5;

    void RegisterPinCenter(BlueprintNode n, string pinId, bool isInput, int rowIndex)
    {
        double x = isInput ? n.X + InPinX : n.X + OutPinX;
        double y = PinCenterY(n, rowIndex);
        _pinCenters[(n.Id, pinId)] = new Point(x, y);
    }

    Point GetPinCenter(string nodeId, string pinId)
    {
        if (_pinCenters.TryGetValue((nodeId, pinId), out var p))
            return p;
        var n = FindNode(nodeId);
        if (n == null) return default;
        var pins = BlueprintNodeCatalog.ResolvePins(n, _doc);
        var inputs = pins.Where(p => p.Direction == BlueprintPinDirection.Input).ToList();
        var outputs = pins.Where(p => p.Direction == BlueprintPinDirection.Output).ToList();
        for (int i = 0; i < inputs.Count; i++)
        {
            if (string.Equals(inputs[i].Id, pinId, StringComparison.OrdinalIgnoreCase))
                return new Point(n.X + InPinX, PinCenterY(n, i));
        }
        for (int i = 0; i < outputs.Count; i++)
        {
            if (string.Equals(outputs[i].Id, pinId, StringComparison.OrdinalIgnoreCase))
                return new Point(n.X + OutPinX, PinCenterY(n, i));
        }
        return new Point(n.X + NodeW * 0.5, n.Y + HeaderH);
    }

    void RecomputePinCentersFromGraph()
    {
        _pinCenters.Clear();
        foreach (var n in ActiveGraph.Nodes)
        {
            var pins = BlueprintNodeCatalog.ResolvePins(n, _doc);
            var inputs = pins.Where(p => p.Direction == BlueprintPinDirection.Input).ToList();
            var outputs = pins.Where(p => p.Direction == BlueprintPinDirection.Output).ToList();
            for (int i = 0; i < inputs.Count; i++)
                RegisterPinCenter(n, inputs[i].Id, true, i);
            for (int i = 0; i < outputs.Count; i++)
                RegisterPinCenter(n, outputs[i].Id, false, i);
        }
    }

    static Ellipse CreatePinDot(BlueprintPinDef pin, bool isOutput)
    {
        var (r, g, b) = BlueprintPinColors.For(pin.Type);
        var color = Color.FromRgb(r, g, b);
        var ellipse = new Ellipse
        {
            Width = PinDotSize,
            Height = PinDotSize,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(isOutput ? StandardCursorType.Cross : StandardCursorType.Arrow)
        };

        if (pin.Type == BlueprintPinType.Exec)
        {
            ellipse.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x34));
            ellipse.Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        }
        else if (isOutput)
        {
            ellipse.Fill = new SolidColorBrush(color);
            ellipse.Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        }
        else
        {
            ellipse.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x34));
            ellipse.Stroke = new SolidColorBrush(color);
        }

        return ellipse;
    }

    // ── Wires ──

    bool TryCompleteWire(Point releaseCanvas)
    {
        if (string.IsNullOrEmpty(_wireFromId)) return false;
        var fromNode = FindNode(_wireFromId);
        if (fromNode == null) return false;
        var fromPinDef = BlueprintNodeCatalog.FindPin(fromNode, _wireFromPin, _doc);
        if (fromPinDef == null || fromPinDef.Direction != BlueprintPinDirection.Output)
        {
            SetStatus("Wire cancelled — invalid source pin.");
            return false;
        }

        string? bestNodeId = null;
        string? bestPinId = null;
        double bestDist = WireCompletePinRadius;

        foreach (var n in ActiveGraph.Nodes)
        {
            if (n.Id == _wireFromId) continue;
            var pins = BlueprintNodeCatalog.ResolvePins(n, _doc);
            foreach (var pin in pins.Where(p => p.Direction == BlueprintPinDirection.Input))
            {
                if (!ArePinsCompatible(fromPinDef, pin)) continue;
                var center = GetPinCenter(n.Id, pin.Id);
                double dx = center.X - releaseCanvas.X;
                double dy = center.Y - releaseCanvas.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNodeId = n.Id;
                    bestPinId = pin.Id;
                }
            }
        }

        if (bestNodeId == null || bestPinId == null)
        {
            SetStatus("Wire cancelled — drop on a compatible input pin.");
            return false;
        }

        CaptureUndo();

        var wireKind = fromPinDef.Type == BlueprintPinType.Exec
            ? BlueprintWireKind.Exec
            : BlueprintWireKind.Data;

        ActiveGraph.Wires.RemoveAll(w =>
            string.Equals(w.ToNodeId, bestNodeId, StringComparison.Ordinal)
            && string.Equals(w.ToPin, bestPinId, StringComparison.OrdinalIgnoreCase));

        if (wireKind == BlueprintWireKind.Exec)
        {
            ActiveGraph.Wires.RemoveAll(w =>
                string.Equals(w.FromNodeId, _wireFromId, StringComparison.Ordinal)
                && string.Equals(w.FromPin, _wireFromPin, StringComparison.OrdinalIgnoreCase));
        }

        ActiveGraph.Wires.Add(new BlueprintWire
        {
            FromNodeId = _wireFromId,
            ToNodeId = bestNodeId,
            FromPin = _wireFromPin,
            ToPin = bestPinId,
            Kind = wireKind
        });

        SetStatus(wireKind == BlueprintWireKind.Exec ? "Exec flow linked." : "Data wire linked.");
        return true;
    }

    static bool ArePinsCompatible(BlueprintPinDef from, BlueprintPinDef to)
    {
        if (from.Type == BlueprintPinType.Exec || to.Type == BlueprintPinType.Exec)
            return from.Type == BlueprintPinType.Exec && to.Type == BlueprintPinType.Exec;
        return BlueprintValue.AreTypesCompatible(from.Type, to.Type);
    }

    void StartWireFromPin(BlueprintNode nodeRef, string fromPin, BlueprintPinType pinType, PointerPressedEventArgs ev)
    {
        if (GraphCanvas == null) return;
        _wireFromId = nodeRef.Id;
        _wireFromPin = fromPin;
        _wireFromType = pinType;
        var start = GetPinCenter(nodeRef.Id, fromPin);
        var (r, g, b) = BlueprintPinColors.For(pinType);
        _wirePreviewLine = new Line
        {
            StartPoint = start,
            EndPoint = start,
            Stroke = new SolidColorBrush(Color.FromRgb(r, g, b)),
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            IsHitTestVisible = false
        };
        _worldCanvas.Children.Add(_wirePreviewLine);
        ev.Pointer.Capture(this);
        ev.Handled = true;
    }

    PathShape? CreateWirePath(BlueprintWire wire)
    {
        var a = FindNode(wire.FromNodeId);
        var b = FindNode(wire.ToNodeId);
        if (a == null || b == null) return null;

        var fromPin = BlueprintNodeCatalog.FindPin(a, wire.FromPin, _doc);
        var toPin = BlueprintNodeCatalog.FindPin(b, wire.ToPin, _doc);
        if (fromPin == null || toPin == null) return null;

        Color stroke;
        if (wire.Kind == BlueprintWireKind.Exec
            || fromPin.Type == BlueprintPinType.Exec
            || toPin.Type == BlueprintPinType.Exec)
        {
            stroke = Color.FromRgb(0xC8, 0xD4, 0xEE);
        }
        else
        {
            var (r, g, bb) = BlueprintPinColors.For(fromPin.Type);
            stroke = Color.FromRgb(r, g, bb);
        }

        var path = BuildWirePathShape(GetPinCenter(a.Id, wire.FromPin), GetPinCenter(b.Id, wire.ToPin), stroke);
        path.Tag = wire;
        path.Cursor = new Cursor(StandardCursorType.Hand);
        var wref = wire;
        path.PointerPressed += (_, ev) =>
        {
            if (!ev.GetCurrentPoint(path).Properties.IsRightButtonPressed) return;
            var menu = new ContextMenu();
            var del = new MenuItem { Header = "Delete wire" };
            del.Click += (_, _) =>
            {
                CaptureUndo();
                ActiveGraph.Wires.Remove(wref);
                MarkDirty(true);
                RefreshUi();
            };
            menu.Items.Add(del);
            menu.Open(path);
            ev.Handled = true;
        };
        return path;
    }

    static PathShape BuildWirePathShape(Point p0, Point p1, Color strokeRgb)
    {
        double dx = Math.Max(48, Math.Abs(p1.X - p0.X) * 0.45);
        var c1 = new Point(p0.X + dx, p0.Y);
        var c2 = new Point(p1.X - dx, p1.Y);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p0, false);
            ctx.CubicBezierTo(c1, c2, p1);
        }
        return new PathShape
        {
            Data = geo,
            Stroke = new SolidColorBrush(strokeRgb),
            StrokeThickness = 2.5,
            Fill = null,
            IsHitTestVisible = true
        };
    }

    void RefreshWirePathsOnly()
    {
        for (int i = _worldCanvas.Children.Count - 1; i >= 0; i--)
            if (_worldCanvas.Children[i] is PathShape)
                _worldCanvas.Children.RemoveAt(i);

        int insertAt = 0;
        for (; insertAt < _worldCanvas.Children.Count; insertAt++)
            if (_worldCanvas.Children[insertAt] is Border) break;

        foreach (var w in ActiveGraph.Wires)
        {
            var path = CreateWirePath(w);
            if (path == null) continue;
            _worldCanvas.Children.Insert(insertAt, path);
            insertAt++;
        }
    }

    // ── Canvas rebuild ──

    void RebuildGraphCanvas()
    {
        if (GraphCanvas == null) return;
        if (!GraphCanvas.Children.Contains(_worldCanvas))
            GraphCanvas.Children.Add(_worldCanvas);

        _worldCanvas.Children.Clear();
        _pinCenters.Clear();
        ApplyViewTransform();

        try
        {
            foreach (var w in ActiveGraph.Wires)
            {
                var path = CreateWirePath(w);
                if (path != null)
                    _worldCanvas.Children.Add(path);
            }

            foreach (var n in ActiveGraph.Nodes)
                _worldCanvas.Children.Add(BuildNodeBorder(n));
        }
        catch (Exception ex)
        {
            Log.Warning($"Blueprint canvas rebuild failed: {ex}");
            SetStatus($"Canvas rebuild failed: {ex.Message}", error: true);
        }
    }

    Border BuildNodeBorder(BlueprintNode n)
    {
        var def = BlueprintNodeCatalog.Resolve(n.Kind);
        bool sel = _selectedIds.Contains(n.Id);
        bool isComment = def.Category == BlueprintNodeCategory.Comment;
        double nh = ComputeNodeHeight(n, _doc);
        var accentBrush = new SolidColorBrush(Color.FromRgb(def.HeaderR, def.HeaderG, def.HeaderB));

        var border = new Border
        {
            Width = NodeW,
            Height = nh,
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x34)),
            BorderBrush = sel
                ? new SolidColorBrush(Color.FromRgb(0x6A, 0xC0, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x55)),
            BorderThickness = new Thickness(sel ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Arrow)
        };

        var title = new TextBlock
        {
            Text = n.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NodeW - 24
        };
        var kindLbl = new TextBlock
        {
            Text = isComment ? "Note" : n.Kind,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)),
            FontSize = 9
        };

        var header = new StackPanel { Spacing = 2, Margin = new Thickness(8, 6, 8, 4) };
        header.Children.Add(title);
        header.Children.Add(kindLbl);

        Control body;
        if (isComment)
        {
            body = header;
        }
        else
        {
            var pins = BlueprintNodeCatalog.ResolvePins(n, _doc);
            var inputs = pins.Where(p => p.Direction == BlueprintPinDirection.Input).ToList();
            var outputs = pins.Where(p => p.Direction == BlueprintPinDirection.Output).ToList();

            var leftCol = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top };
            var rightCol = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top };

            var nodeRef = n;
            for (int i = 0; i < inputs.Count; i++)
            {
                var pin = inputs[i];
                RegisterPinCenter(n, pin.Id, true, i);
                leftCol.Children.Add(BuildPinRow(pin, isOutput: false, nodeRef, i));
            }

            for (int i = 0; i < outputs.Count; i++)
            {
                var pin = outputs[i];
                RegisterPinCenter(n, pin.Id, false, i);
                rightCol.Children.Add(BuildPinRow(pin, isOutput: true, nodeRef, i));
            }

            var pinRow = new AvGrid
            {
                Margin = new Thickness(2, 0, 2, 6),
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1, GridUnitType.Star))
                }
            };
            AvGrid.SetColumn(leftCol, 0);
            AvGrid.SetColumn(rightCol, 1);
            pinRow.Children.Add(leftCol);
            pinRow.Children.Add(rightCol);

            var root = new AvGrid();
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            AvGrid.SetRow(header, 0);
            AvGrid.SetRow(pinRow, 1);
            root.Children.Add(header);
            root.Children.Add(pinRow);
            body = root;
        }

        var bar = new Border
        {
            Width = CategoryBarW,
            Background = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        DockPanel.SetDock(bar, Dock.Left);
        var chrome = new DockPanel();
        chrome.Children.Add(bar);
        chrome.Children.Add(body);
        border.Child = chrome;

        Canvas.SetLeft(border, n.X);
        Canvas.SetTop(border, n.Y);
        border.Tag = n.Id;

        var nodeRef2 = n;
        border.PointerPressed += (_, ev) =>
        {
            if (ev.Source is Ellipse) return;

            if (ev.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!_selectedIds.Add(nodeRef2.Id))
                    _selectedIds.Remove(nodeRef2.Id);
                _primaryId = _selectedIds.Contains(nodeRef2.Id) ? nodeRef2.Id : _selectedIds.FirstOrDefault();
                RefreshPropertiesPanel();
                RebuildGraphCanvas();
                ev.Handled = true;
                return;
            }

            if (!_selectedIds.Contains(nodeRef2.Id))
            {
                _selectedIds.Clear();
                _selectedIds.Add(nodeRef2.Id);
            }
            _primaryId = nodeRef2.Id;
            RefreshPropertiesPanel();

            if (!_dragUndoCaptured)
            {
                CaptureUndo();
                _dragUndoCaptured = true;
            }

            _dragNodes = [];
            _dragNodeStartPos = new Dictionary<string, Point>(StringComparer.Ordinal);
            foreach (var id in _selectedIds)
            {
                var dn = FindNode(id);
                if (dn != null)
                {
                    _dragNodes.Add(dn);
                    _dragNodeStartPos[dn.Id] = new Point(dn.X, dn.Y);
                }
            }
            _dragPointerStartCanvas = ScreenToWorld(ev.GetPosition(GraphCanvas!));
            _dragCaptureBorder = border;
            ev.Pointer.Capture(border);
            ev.Handled = true;
            GraphCanvas?.Focus();
        };

        return border;
    }

    Control BuildPinRow(BlueprintPinDef pin, bool isOutput, BlueprintNode nodeRef, int rowIndex)
    {
        var dot = CreatePinDot(pin, isOutput);
        var label = new TextBlock
        {
            Text = pin.DisplayName,
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NodeW * 0.45
        };

        if (isOutput)
        {
            var pnm = pin.Id;
            var pType = pin.Type;
            dot.PointerPressed += (_, ev) => StartWireFromPin(nodeRef, pnm, pType, ev);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 4, 0),
                Height = PinSpacing,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(label);
            row.Children.Add(dot);
            return row;
        }

        var inRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 0, 0, 0),
            Height = PinSpacing,
            VerticalAlignment = VerticalAlignment.Center
        };
        inRow.Children.Add(dot);
        inRow.Children.Add(label);
        return inRow;
    }

    void SyncDraggedBorderPositions()
    {
        foreach (var child in _worldCanvas.Children)
        {
            if (child is Border b && b.Tag is string tid)
            {
                var n = FindNode(tid);
                if (n != null)
                {
                    Canvas.SetLeft(b, n.X);
                    Canvas.SetTop(b, n.Y);
                }
            }
        }
    }

    BlueprintNode? FindNode(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var n in ActiveGraph.Nodes)
            if (n.Id == id) return n;
        return null;
    }

    void ClearGraphSelection(bool redraw)
    {
        _selectedIds.Clear();
        _primaryId = null;
        RefreshPropertiesPanel();
        if (redraw) RebuildGraphCanvas();
    }

    // ── File I/O ──

    async System.Threading.Tasks.Task OnNewAsync()
    {
        CaptureUndo();
        _doc = new BlueprintDocument();
        _activeGraphKey = "__event";
        _currentFileAbs = null;
        _selectedIds.Clear();
        _primaryId = null;
        _selectedVar = null;
        _selectedFunc = null;
        MarkDirty(false);
        RebuildGraphTabs(selectKey: "__event");
        RefreshUi();
        SetStatus(ProjectService.Current != null
            ? "New blueprint — wire Begin Play → actions, save, then add Visual Blueprint on a GameObject."
            : "New blueprint — open a project to save under Assets/Blueprints.");
    }

    async System.Threading.Tasks.Task OnOpenAsync()
    {
        if (!EnsureProject()) return;
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        var proj = ProjectService.Current!;
        var startDir = BlueprintPersistence.EnsureBlueprintsFolder(proj.RootPath);
        var dlg = new OpenFileDialog
        {
            Title = "Open blueprint",
            Directory = startDir,
            AllowMultiple = false,
            Filters = new List<FileDialogFilter>
            {
                new() { Name = "Blueprint", Extensions = { "blueprint" } },
                new() { Name = "All", Extensions = { "*" } }
            }
        };

        var files = await dlg.ShowAsync(win);
        var path = files?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var doc = BlueprintPersistence.LoadDocument(path);
            _doc = doc;
            _activeGraphKey = "__event";
            _currentFileAbs = System.IO.Path.GetFullPath(path);
            _selectedIds.Clear();
            _primaryId = null;
            _selectedVar = null;
            _selectedFunc = null;
            MarkDirty(false);
            _undoStack.Clear();
            _redoStack.Clear();
            CaptureUndo();
            RebuildGraphTabs(selectKey: "__event");
            RefreshPathDisplay();
            RefreshUi();
            SetStatus("Opened.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Blueprint open failed: {ex.Message}");
            SetStatus($"Open failed: {ex.Message}", error: true);
        }
    }

    async System.Threading.Tasks.Task OnSaveAsync()
    {
        if (!EnsureProject()) return;
        if (string.IsNullOrWhiteSpace(_currentFileAbs))
        {
            await OnSaveAsAsync();
            return;
        }

        try
        {
            BlueprintPersistence.SaveDocument(_currentFileAbs, _doc);
            ProjectService.TouchModified();
            MarkDirty(false);
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Blueprint save failed: {ex.Message}");
            SetStatus($"Save failed: {ex.Message}", error: true);
        }
    }

    async System.Threading.Tasks.Task OnSaveAsAsync()
    {
        if (!EnsureProject()) return;
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        var proj = ProjectService.Current!;
        var startDir = BlueprintPersistence.EnsureBlueprintsFolder(proj.RootPath);
        var dlg = new SaveFileDialog
        {
            Title = "Save blueprint",
            Directory = startDir,
            DefaultExtension = "blueprint",
            InitialFileName = "NewBlueprint.blueprint",
            Filters = new List<FileDialogFilter>
            {
                new() { Name = "Blueprint", Extensions = { "blueprint" } }
            }
        };

        var path = await dlg.ShowAsync(win);
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!path.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
            path += ".blueprint";

        try
        {
            BlueprintPersistence.SaveDocument(path, _doc);
            _currentFileAbs = System.IO.Path.GetFullPath(path);
            ProjectService.TouchModified();
            MarkDirty(false);
            RefreshPathDisplay();
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Blueprint save failed: {ex.Message}");
            SetStatus($"Save failed: {ex.Message}", error: true);
        }
    }

    bool EnsureProject()
    {
        if (ProjectService.Current != null) return true;
        SetStatus("Open a project first (File → Open Project).", error: true);
        return false;
    }

    void RunValidation()
    {
        var issues = BlueprintValidation.Validate(_doc);
        var errors = issues.Where(i => i.IsError).ToList();
        if (errors.Count == 0)
        {
            SetStatus(issues.Count == 0
                ? "Validation passed — no issues."
                : $"Validation passed with {issues.Count} warning(s).");
            return;
        }

        var first = errors.Take(3).Select(i => i.Message);
        SetStatus("Validation failed: " + string.Join(" · ", first), error: true);
    }

    void RunSmokeChecks()
    {
        try
        {
            var report = BlueprintSmokeChecks.RunAll();
            var failed = report.Contains("FAILED", StringComparison.Ordinal);
            var summary = report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? report;
            SetStatus(summary, error: failed);
            Log.Info("[Blueprint smoke]\n" + report);
        }
        catch (Exception ex)
        {
            SetStatus("Smoke checks crashed: " + ex.Message, error: true);
            Log.Warning($"Blueprint smoke checks failed: {ex}");
        }
    }

    // ── Node CRUD ──

    void AddNodeFromCombo()
    {
        int idx = NodeKindCombo?.SelectedIndex ?? 0;
        var palette = BlueprintNodeCatalog.AuthoringPaletteOrdered;
        if (idx < 0 || idx >= palette.Length) idx = 0;
        AddNodeKind(palette[idx]);
    }

    void AddNodeKind(string kind, double? x = null, double? y = null)
    {
        CaptureUndo();
        var t = BlueprintNodeCatalog.Resolve(kind);
        int i = ActiveGraph.Nodes.Count;
        double px = x ?? 80 + (i % 5) * 210;
        double py = y ?? 80 + (i / 5) * 130;
        var n = ActiveGraph.AddNode(kind, t.DefaultTitle + " " + (i + 1), px, py);
        foreach (var kv in t.DefaultProperties)
            n.Properties[kv.Key] = kv.Value;
        foreach (var pin in BlueprintNodeCatalog.ResolvePins(n, _doc))
        {
            if (pin.Direction == BlueprintPinDirection.Input
                && pin.Type != BlueprintPinType.Exec
                && !string.IsNullOrEmpty(pin.DefaultLiteral))
                n.SetPinLiteral(pin.Id, pin.DefaultLiteral);
        }
        MarkDirty(true);
        RefreshUi();
        ClearStatus();
    }

    void ClearActiveGraph()
    {
        CaptureUndo();
        ActiveGraph.Nodes.Clear();
        ActiveGraph.Wires.Clear();
        _selectedIds.Clear();
        _primaryId = null;
        MarkDirty(true);
        RefreshUi();
        SetStatus("Graph cleared (unsaved).");
    }

    void DeleteSelectedNode()
    {
        if (_selectedIds.Count == 0)
        {
            SetStatus("Select node(s) on the canvas first.", error: true);
            return;
        }
        DeleteNodesByIds(_selectedIds.ToArray());
    }

    void DeleteNodesByIds(params string[] ids)
    {
        var set = ids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return;

        CaptureUndo();
        ActiveGraph.Wires.RemoveAll(w => set.Contains(w.FromNodeId) || set.Contains(w.ToNodeId));
        ActiveGraph.Nodes.RemoveAll(n => set.Contains(n.Id));
        _selectedIds.ExceptWith(set);
        if (_primaryId != null && !_selectedIds.Contains(_primaryId))
            _primaryId = _selectedIds.Count > 0 ? _selectedIds.First() : null;
        MarkDirty(true);
        RefreshUi();
        ClearStatus();
    }

    void DuplicateSelectedNode()
    {
        var sourceIds = _selectedIds.Count > 0
            ? _selectedIds.ToArray()
            : _primaryId is { } pid ? new[] { pid } : Array.Empty<string>();
        if (sourceIds.Length == 0)
        {
            SetStatus("Select node(s) first.", error: true);
            return;
        }

        CaptureUndo();
        _selectedIds.Clear();
        string? newPrimary = null;
        for (int i = 0; i < sourceIds.Length; i++)
        {
            var n = FindNode(sourceIds[i]);
            if (n == null) continue;
            var copy = new BlueprintNode
            {
                Kind = n.Kind,
                Title = n.Title + " copy",
                X = n.X + 40 + i * 10,
                Y = n.Y + 40 + i * 10,
                VariableName = n.VariableName,
                FunctionName = n.FunctionName
            };
            foreach (var kv in n.Properties)
                copy.Properties[kv.Key] = kv.Value;
            foreach (var kv in n.PinLiterals)
                copy.PinLiterals[kv.Key] = kv.Value;
            foreach (var dp in n.DynamicPins)
                copy.DynamicPins.Add(dp);
            ActiveGraph.Nodes.Add(copy);
            _selectedIds.Add(copy.Id);
            newPrimary = copy.Id;
        }
        _primaryId = newPrimary;
        MarkDirty(true);
        RefreshUi();
        SetStatus(sourceIds.Length == 1 ? "Node duplicated." : $"{sourceIds.Length} nodes duplicated.");
    }

    void OnPropTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_syncPropFields) return;
        var n = FindNode(_primaryId);
        if (n == null || TxtPropTitle == null) return;
        var t = (TxtPropTitle.Text ?? "").Trim();
        if (t.Length == 0) return;
        if (string.Equals(t, n.Title, StringComparison.Ordinal)) return;
        CaptureUndo();
        n.Title = t;
        MarkDirty(true);
        RefreshUi();
    }

    // ── Properties panel ──

    void RefreshPropertiesPanel()
    {
        if (TblPropKind == null || TblPropId == null || TblPropWires == null || TxtPropTitle == null
            || TblPropDesc == null)
            return;

        _syncPropFields = true;
        try
        {
            var n = FindNode(_primaryId);
            if (n == null)
            {
                TxtPropTitle.Text = "";
                TxtPropTitle.IsEnabled = false;
                TblPropKind.Text = "—";
                TblPropId.Text = "—";
                TblPropDesc.Text = "";
                TblPropWires.Text = "";
                RebuildPropExtras(null);
                return;
            }

            if (_selectedIds.Count > 1)
            {
                TxtPropTitle.Text = n.Title;
                TxtPropTitle.IsEnabled = false;
                TblPropKind.Text = $"{n.Kind} · {_selectedIds.Count} selected";
                TblPropId.Text = n.Id;
                TblPropDesc.Text = "";
                int i0 = ActiveGraph.Wires.Count(w => string.Equals(w.ToNodeId, n.Id, StringComparison.Ordinal));
                int o0 = ActiveGraph.Wires.Count(w => string.Equals(w.FromNodeId, n.Id, StringComparison.Ordinal));
                TblPropWires.Text = $"Primary node wires: {i0} in · {o0} out. Title edit disabled for multi‑select.";
                RebuildPropExtras(null);
                return;
            }

            var def = BlueprintNodeCatalog.Resolve(n.Kind);
            TxtPropTitle.Text = n.Title;
            TxtPropTitle.IsEnabled = true;
            TblPropKind.Text = n.Kind;
            TblPropId.Text = n.Id;
            TblPropDesc.Text = def.Description;
            int incoming = ActiveGraph.Wires.Count(w => string.Equals(w.ToNodeId, n.Id, StringComparison.Ordinal));
            int outgoing = ActiveGraph.Wires.Count(w => string.Equals(w.FromNodeId, n.Id, StringComparison.Ordinal));
            TblPropWires.Text = $"Wires: {incoming} in · {outgoing} out — right‑click a wire to delete.";
            RebuildPropExtras(n);
        }
        finally
        {
            _syncPropFields = false;
        }
    }

    void RebuildPropExtras(BlueprintNode? n)
    {
        if (PropExtrasHost == null) return;
        PropExtrasHost.Children.Clear();
        if (n == null) return;

        if (string.Equals(n.Kind, "GetVar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Kind, "SetVar", StringComparison.OrdinalIgnoreCase))
        {
            BuildVariableNameEditor(n);
        }

        if (string.Equals(n.Kind, "ReflectGet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Kind, "ReflectSet", StringComparison.OrdinalIgnoreCase))
        {
            BuildReflectPropertyEditors(n);
        }
        else
        {
            var t = BlueprintNodeCatalog.Resolve(n.Kind);
            if (t.EditablePropertyKeys.Length > 0)
            {
                PropExtrasHost.Children.Add(new TextBlock
                {
                    Text = "Legacy parameters",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
                });
                foreach (var key in t.EditablePropertyKeys)
                    AddLegacyPropertyEditor(n, key);
            }
        }

        BuildPinLiteralEditors(n);
    }

    void BuildVariableNameEditor(BlueprintNode n)
    {
        var muted = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99));
        PropExtrasHost!.Children.Add(new TextBlock
        {
            Text = "Variable",
            FontSize = 10,
            Foreground = muted
        });

        var names = _doc.Variables.Select(v => v.Name).ToList();
        if (names.Count == 0)
        {
            PropExtrasHost.Children.Add(new TextBlock
            {
                Text = "(no variables — use + in My Blueprint)",
                FontSize = 10,
                Foreground = muted
            });
            return;
        }

        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = names
        };
        var cur = n.VariableName ?? names[0];
        combo.SelectedItem = names.FirstOrDefault(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase)) ?? names[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (_syncPropFields) return;
            if (combo.SelectedItem is not string sel) return;
            if (string.Equals(n.VariableName, sel, StringComparison.OrdinalIgnoreCase)) return;
            CaptureUndo();
            n.VariableName = sel;
            n.Title = string.Equals(n.Kind, "GetVar", StringComparison.OrdinalIgnoreCase)
                ? $"Get {sel}"
                : $"Set {sel}";
            MarkDirty(true);
            RebuildGraphCanvas();
            RefreshPropertiesPanel();
        };
        PropExtrasHost.Children.Add(combo);
    }

    void AddLegacyPropertyEditor(BlueprintNode n, string key)
    {
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(new TextBlock
        {
            Text = key,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
        });
        n.Properties.TryGetValue(key, out var val);
        var tb = new TextBox { FontSize = 11, Text = val ?? "" };
        var keyCap = key;
        var nodeCap = n;
        tb.LostFocus += (_, _) =>
        {
            var nt = (tb.Text ?? "").Trim();
            if (nodeCap.Properties.TryGetValue(keyCap, out var old) && string.Equals(old, nt, StringComparison.Ordinal))
                return;
            CaptureUndo();
            nodeCap.Properties[keyCap] = nt;
            MarkDirty(true);
            RebuildGraphCanvas();
        };
        row.Children.Add(tb);
        PropExtrasHost!.Children.Add(row);
    }

    void BuildPinLiteralEditors(BlueprintNode n)
    {
        var pins = BlueprintNodeCatalog.ResolvePins(n, _doc);
        var unwiredInputs = pins
            .Where(p => p.Direction == BlueprintPinDirection.Input
                        && p.Type != BlueprintPinType.Exec
                        && ActiveGraph.FindIncomingDataWire(n.Id, p.Id) == null)
            .ToList();
        if (unwiredInputs.Count == 0) return;

        PropExtrasHost!.Children.Add(new TextBlock
        {
            Text = "Pin defaults (unwired inputs)",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99)),
            Margin = new Thickness(0, 4, 0, 0)
        });

        foreach (var pin in unwiredInputs)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = $"{pin.DisplayName} ({pin.Type})",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
            });
            var lit = n.GetPinLiteral(pin.Id, pin.DefaultLiteral);
            var tb = new TextBox { FontSize = 11, Text = lit };
            var pinId = pin.Id;
            var nodeCap = n;
            tb.LostFocus += (_, _) =>
            {
                var nt = (tb.Text ?? "").Trim();
                var old = nodeCap.GetPinLiteral(pinId, pin.DefaultLiteral);
                if (string.Equals(old, nt, StringComparison.Ordinal)) return;
                CaptureUndo();
                nodeCap.SetPinLiteral(pinId, nt);
                MarkDirty(true);
            };
            row.Children.Add(tb);
            PropExtrasHost.Children.Add(row);
        }
    }

    void BuildReflectPropertyEditors(BlueprintNode n)
    {
        void Commit(string key, string value)
        {
            value = value.Trim();
            if (n.Properties.TryGetValue(key, out var old) && string.Equals(old, value, StringComparison.Ordinal))
                return;
            CaptureUndo();
            n.Properties[key] = value;
            MarkDirty(true);
            RebuildGraphCanvas();
        }

        var muted = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99));
        PropExtrasHost!.Children.Add(new TextBlock
        {
            Text = "Parameters — pick types and members (or type custom names)",
            FontSize = 10,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap
        });

        var modeNow = (n.Properties.TryGetValue("mode", out var m) ? m : "Instance").Trim();
        var instance = string.Equals(modeNow, "Instance", StringComparison.OrdinalIgnoreCase);
        var scopeNow = (n.Properties.TryGetValue("scope", out var sc) ? sc : "Self").Trim();

        static StackPanel LabeledRow(string label) => new()
        {
            Spacing = 2,
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
                }
            }
        };

        var rowMode = LabeledRow("mode");
        var modeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Instance", "Static" }
        };
        modeCombo.SelectedItem = new[] { "Instance", "Static" }
            .FirstOrDefault(x => string.Equals(x, modeNow, StringComparison.OrdinalIgnoreCase)) ?? "Instance";
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncPropFields) return;
            if (modeCombo.SelectedItem is not string sel) return;
            Commit("mode", sel);
            RebuildPropExtras(n);
        };
        rowMode.Children.Add(modeCombo);
        PropExtrasHost.Children.Add(rowMode);

        var rowScope = LabeledRow("scope (instance only)");
        rowScope.IsVisible = instance;
        var scopeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Self", "Other" }
        };
        scopeCombo.SelectedItem = new[] { "Self", "Other" }
            .FirstOrDefault(x => string.Equals(x, scopeNow, StringComparison.OrdinalIgnoreCase)) ?? "Self";
        scopeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncPropFields) return;
            if (scopeCombo.SelectedItem is not string sel) return;
            Commit("scope", sel);
            RebuildPropExtras(n);
        };
        rowScope.Children.Add(scopeCombo);
        PropExtrasHost.Children.Add(rowScope);

        var showTargets = instance && string.Equals(scopeNow, "Other", StringComparison.OrdinalIgnoreCase);
        var rowPath = LabeledRow("targetPath (hierarchy path)");
        rowPath.IsVisible = showTargets;
        var tbPath = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("targetPath", out var tp) ? tp : "" };
        tbPath.LostFocus += (_, _) => Commit("targetPath", tbPath.Text ?? "");
        rowPath.Children.Add(tbPath);
        PropExtrasHost.Children.Add(rowPath);

        var rowName = LabeledRow("targetName (scene search)");
        rowName.IsVisible = showTargets;
        var tbName = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("targetName", out var tn) ? tn : "" };
        tbName.LostFocus += (_, _) => Commit("targetName", tbName.Text ?? "");
        rowName.Children.Add(tbName);
        PropExtrasHost.Children.Add(rowName);

        var componentOptions = BlueprintReflectionBrowse.GetComponentTypeOptions();
        var staticOptions = BlueprintReflectionBrowse.GetStaticTypeOptions();

        List<string> BuildMemberList()
        {
            if (instance)
            {
                var comp = (n.Properties.TryGetValue("componentType", out var c) ? c : "Transform").Trim();
                var rt = BlueprintReflectionBrowse.ResolveComponentRootType(comp);
                return BlueprintReflectionBrowse.GetMemberPathSuggestions(rt);
            }
            var typeNm = (n.Properties.TryGetValue("typeName", out var tnv) ? tnv : "").Trim();
            var st = BlueprintReflection.ResolveNamedType(typeNm);
            return BlueprintReflectionBrowse.GetStaticMemberPathSuggestions(st);
        }

        AutoCompleteBox? memberAc = null;

        var rowStaticType = LabeledRow("typeName (static — engine types with static members)");
        rowStaticType.IsVisible = !instance;
        var staticDisplays = staticOptions.Select(o => o.Display).ToList();
        var staticAc = new AutoCompleteBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.Contains,
            Watermark = "Search types…",
            ItemsSource = staticDisplays
        };
        {
            var curStored = (n.Properties.TryGetValue("typeName", out var ts) ? ts : "").Trim();
            var pick = staticOptions.FirstOrDefault(o => string.Equals(o.Stored, curStored, StringComparison.OrdinalIgnoreCase));
            staticAc.Text = !string.IsNullOrEmpty(pick.Stored) ? pick.Display : curStored;
        }
        staticAc.LostFocus += (_, _) =>
        {
            var text = (staticAc.Text ?? "").Trim();
            var opt = staticOptions.FirstOrDefault(o => string.Equals(o.Display, text, StringComparison.OrdinalIgnoreCase)
                                                         || string.Equals(o.Stored, text, StringComparison.OrdinalIgnoreCase));
            var stored = opt.Stored ?? text;
            Commit("typeName", stored);
            if (memberAc != null)
            {
                memberAc.ItemsSource = BuildMemberList();
                memberAc.Text = n.Properties.TryGetValue("memberPath", out var mp) ? mp : "";
            }
        };
        rowStaticType.Children.Add(staticAc);
        PropExtrasHost.Children.Add(rowStaticType);

        var rowComp = LabeledRow("componentType (instance)");
        rowComp.IsVisible = instance;
        var compDisplays = componentOptions.Select(o => o.Display).ToList();
        var compAc = new AutoCompleteBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.Contains,
            Watermark = "Search components…",
            ItemsSource = compDisplays
        };
        {
            var curStored = (n.Properties.TryGetValue("componentType", out var cs) ? cs : "Transform").Trim();
            var pick = componentOptions.FirstOrDefault(o => string.Equals(o.Stored, curStored, StringComparison.OrdinalIgnoreCase));
            compAc.Text = !string.IsNullOrEmpty(pick.Stored) ? pick.Display : curStored;
        }
        compAc.LostFocus += (_, _) =>
        {
            var text = (compAc.Text ?? "").Trim();
            var opt = componentOptions.FirstOrDefault(o => string.Equals(o.Display, text, StringComparison.OrdinalIgnoreCase)
                                                           || string.Equals(o.Stored, text, StringComparison.OrdinalIgnoreCase));
            var stored = opt.Stored ?? text;
            Commit("componentType", stored);
            if (memberAc != null)
            {
                memberAc.ItemsSource = BuildMemberList();
                memberAc.Text = n.Properties.TryGetValue("memberPath", out var kp) ? kp : "";
            }
        };
        rowComp.Children.Add(compAc);
        PropExtrasHost.Children.Add(rowComp);

        var rowMember = LabeledRow("memberPath");
        memberAc = new AutoCompleteBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.Contains,
            Watermark = "Search properties…",
            ItemsSource = BuildMemberList()
        };
        memberAc.Text = n.Properties.TryGetValue("memberPath", out var mem) ? mem : "";
        memberAc.LostFocus += (_, _) => Commit("memberPath", memberAc.Text ?? "");
        rowMember.Children.Add(memberAc);
        PropExtrasHost.Children.Add(rowMember);

        if (string.Equals(n.Kind, "ReflectGet", StringComparison.OrdinalIgnoreCase))
        {
            var rowVk = LabeledRow("varKey");
            var tbVk = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("varKey", out var vk) ? vk : "" };
            tbVk.LostFocus += (_, _) => Commit("varKey", tbVk.Text ?? "");
            rowVk.Children.Add(tbVk);
            PropExtrasHost.Children.Add(rowVk);
        }
        else
        {
            var rowVal = LabeledRow("value (literal, or leave empty to use valueVarKey)");
            var tbVal = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("value", out var vv) ? vv : "" };
            tbVal.LostFocus += (_, _) => Commit("value", tbVal.Text ?? "");
            rowVal.Children.Add(tbVal);
            PropExtrasHost.Children.Add(rowVal);

            var rowVvk = LabeledRow("valueVarKey");
            var tbVvk = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("valueVarKey", out var vvk) ? vvk : "" };
            tbVvk.LostFocus += (_, _) => Commit("valueVarKey", tbVvk.Text ?? "");
            rowVvk.Children.Add(tbVvk);
            PropExtrasHost.Children.Add(rowVvk);
        }
    }

    // ── UI refresh ──

    static void HookMenu(MenuItem? mi, EventHandler<RoutedEventArgs> h)
    {
        if (mi != null) mi.Click += h;
    }

    void MarkDirty(bool dirty)
    {
        _dirty = dirty;
        RefreshPathDisplay();
    }

    void RefreshPathDisplay()
    {
        if (TxtDocumentPath == null) return;
        var proj = ProjectService.Current;
        if (proj == null)
        {
            TxtDocumentPath.Text = "(open a project)";
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentFileAbs))
        {
            TxtDocumentPath.Text = _dirty ? "Unsaved blueprint *" : "Unsaved blueprint";
            return;
        }

        var rel = BlueprintPersistence.TryGetDisplayPath(_currentFileAbs, proj.RootPath);
        TxtDocumentPath.Text = (rel ?? _currentFileAbs) + (_dirty ? " *" : "");
    }

    void SetStatus(string msg, bool error = false)
    {
        if (TxtStatus == null) return;
        TxtStatus.Text = msg;
        TxtStatus.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88))
            : new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x99));
    }

    void ClearStatus()
    {
        if (TxtStatus != null) TxtStatus.Text = "";
    }

    void RefreshUi()
    {
        RefreshVarFuncLists();
        UpdateUndoRedoMenu();

        if (TxtSummary != null)
        {
            var graphSummary = BlueprintGraphDescribe.Summarize(ActiveGraph);
            var extra = $"Variables: {_doc.Variables.Count} · Functions: {_doc.Functions.Count} · Active: {ActiveGraphKeyLabel()}";
            TxtSummary.Text = extra + Environment.NewLine + graphSummary;
        }

        RefreshPropertiesPanel();
        RebuildGraphCanvas();
    }

    string ActiveGraphKeyLabel() =>
        string.Equals(_activeGraphKey, "__event", StringComparison.OrdinalIgnoreCase)
            ? "Event Graph"
            : _activeGraphKey;
}
