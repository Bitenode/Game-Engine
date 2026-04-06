#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
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
    const double NodeW = 170;
    const double NodeH = 68;
    const double CommentNodeH = 52;
    const double CategoryBarW = 4;
    /// <summary>Y of exec pin center from node top (bar + header + pin row).</summary>
    const double ExecPinCenterY = 52;
    const double InPinX = 18;
    const double OutPinX = 156;
    const double WireCompletePinRadius = 28;

    BlueprintGraph _graph = new();
    ListBox? _nodeList;
    TextBlock? _txtSummary;
    TextBlock? _txtDocumentPath;
    TextBlock? _txtStatus;
    ComboBox? _kindCombo;
    TextBox? _txtPropTitle;
    TextBlock? _tblPropKind;
    TextBlock? _tblPropId;
    TextBlock? _tblPropDesc;
    TextBlock? _tblPropWires;
    StackPanel? _propExtrasHost;
    Border? _graphViewport;
    Canvas? _graphCanvas;
    ScaleTransform? _viewScale;
    TranslateTransform? _viewTranslate;
    bool _wired;
    bool _syncPropFields;
    string? _currentFileAbs;
    bool _dirty;
    bool _syncGraphSelection;

    /// <summary>All selected node ids (canvas + Delete/Duplicate).</summary>
    readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

    /// <summary>Node shown in list + properties (last clicked / toggled).</summary>
    string? _primaryId;

    double _viewZoom = 1;
    Point _viewPan;

    bool _middlePanning;
    Point _middlePanPointerStart;
    Point _viewPanAtMiddleStart;

    // Node drag (single or group)
    List<BlueprintNode>? _dragNodes;
    Dictionary<string, Point>? _dragNodeStartPos;
    Point _dragPointerStartCanvas;
    Border? _dragCaptureBorder;

    // Wire drag
    string? _wireFromId;
    string _wireFromPin = BlueprintFlowRuntime.PinExecOut;
    Line? _wirePreviewLine;

    public BlueprintGraphPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object? s, RoutedEventArgs e)
    {
        if (_wired) return;
        _wired = true;
        _nodeList = this.FindControl<ListBox>("NodeList");
        _txtSummary = this.FindControl<TextBlock>("TxtSummary");
        _txtDocumentPath = this.FindControl<TextBlock>("TxtDocumentPath");
        _txtStatus = this.FindControl<TextBlock>("TxtStatus");
        _kindCombo = this.FindControl<ComboBox>("NodeKindCombo");
        _txtPropTitle = this.FindControl<TextBox>("TxtPropTitle");
        _tblPropKind = this.FindControl<TextBlock>("TblPropKind");
        _tblPropId = this.FindControl<TextBlock>("TblPropId");
        _tblPropDesc = this.FindControl<TextBlock>("TblPropDesc");
        _tblPropWires = this.FindControl<TextBlock>("TblPropWires");
        _propExtrasHost = this.FindControl<StackPanel>("PropExtrasHost");
        _graphViewport = this.FindControl<Border>("GraphViewport");
        _graphCanvas = this.FindControl<Canvas>("GraphCanvas");
        var add = this.FindControl<Button>("BtnAddNode");

        if (add != null) add.Click += (_, _) => AddNodeFromCombo();

        HookMenu(this.FindControl<MenuItem>("MiNew"), (_, _) => _ = OnNewAsync());
        HookMenu(this.FindControl<MenuItem>("MiOpen"), (_, _) => _ = OnOpenAsync());
        HookMenu(this.FindControl<MenuItem>("MiSave"), (_, _) => _ = OnSaveAsync());
        HookMenu(this.FindControl<MenuItem>("MiSaveAs"), (_, _) => _ = OnSaveAsAsync());
        HookMenu(this.FindControl<MenuItem>("MiClear"), (_, _) => ClearGraph());
        HookMenu(this.FindControl<MenuItem>("MiDuplicate"), (_, _) => DuplicateSelectedNode());
        HookMenu(this.FindControl<MenuItem>("MiDeleteNode"), (_, _) => DeleteSelectedNode());

        if (this.FindControl<MenuItem>("MiInsertRoot") is { } insertRoot)
        {
            insertRoot.Items.Clear();
            foreach (var kind in BlueprintNodeCatalog.AuthoringPaletteOrdered)
            {
                var def = BlueprintNodeCatalog.Resolve(kind);
                var item = new MenuItem { Header = $"{def.DefaultTitle}\t({kind})" };
                var k = kind;
                item.Click += (_, _) => AddNodeKind(k);
                insertRoot.Items.Add(item);
            }
        }

        if (_kindCombo != null)
        {
            _kindCombo.Items.Clear();
            foreach (var kind in BlueprintNodeCatalog.AuthoringPaletteOrdered)
            {
                var def = BlueprintNodeCatalog.Resolve(kind);
                _kindCombo.Items.Add(new ComboBoxItem
                {
                    Content = def.DefaultTitle,
                    Tag = kind
                });
            }
            _kindCombo.SelectedIndex = 0;
        }

        if (_nodeList != null)
        {
            _nodeList.SelectionChanged += OnListSelectionChanged;
            _nodeList.KeyDown += OnNodeListKeyDown;
        }

        if (_graphCanvas != null)
        {
            _graphCanvas.Focusable = true;
            _graphCanvas.PointerMoved += OnGraphCanvasPointerMoved;
            _graphCanvas.PointerReleased += OnGraphCanvasPointerReleased;
            _graphCanvas.KeyDown += OnGraphCanvasKeyDown;
            _graphCanvas.PointerPressed += OnGraphCanvasPointerPressed;
            EnsureViewTransform();
        }

        if (_graphViewport != null)
        {
            _graphViewport.PointerWheelChanged += OnGraphViewportPointerWheelChanged;
            _graphViewport.PointerPressed += OnGraphViewportPointerPressed;
            _graphViewport.PointerMoved += OnGraphViewportPointerMoved;
            _graphViewport.PointerReleased += OnGraphViewportPointerReleased;
        }

        PointerMoved += OnPanelPointerMoved;
        PointerReleased += OnPanelPointerReleased;

        if (_txtPropTitle != null)
            _txtPropTitle.LostFocus += OnPropTitleLostFocus;

        KeyDown += OnPanelKeyDown;
        RefreshPathDisplay();
        RefreshUi();
    }

    void OnGraphCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedIds.Count > 0)
        {
            DeleteNodesByIds(_selectedIds.ToArray());
            e.Handled = true;
        }
        bool ctrl0 = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                     && (e.Key == Key.D0 || e.Key == Key.NumPad0);
        if (ctrl0)
        {
            ResetGraphView();
            e.Handled = true;
        }
    }

    void OnGraphCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_graphCanvas == null || !ReferenceEquals(e.Source, _graphCanvas)) return;
        if (e.GetCurrentPoint(_graphCanvas).Properties.IsLeftButtonPressed)
        {
            ClearGraphSelection(redraw: true);
            _graphViewport?.Focus();
            e.Handled = true;
        }
    }

    void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncGraphSelection) return;
        if (_nodeList?.SelectedIndex is int idx && idx >= 0 && idx < _graph.Nodes.Count)
        {
            var id = _graph.Nodes[idx].Id;
            _selectedIds.Clear();
            _selectedIds.Add(id);
            _primaryId = id;
        }
        else
        {
            _selectedIds.Clear();
            _primaryId = null;
        }
        RefreshPropertiesPanel();
        RebuildGraphCanvas();
    }

    static void HookMenu(MenuItem? mi, EventHandler<RoutedEventArgs> h)
    {
        if (mi != null) mi.Click += h;
    }

    void OnNodeListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedNode();
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

    void EnsureViewTransform()
    {
        if (_graphCanvas == null || _viewScale != null) return;
        _viewScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        _viewTranslate = new TranslateTransform();
        _graphCanvas.RenderTransform = new TransformGroup
        {
            Children = { _viewScale, _viewTranslate }
        };
        ApplyViewTransform();
    }

    void ApplyViewTransform()
    {
        if (_viewScale != null)
        {
            _viewScale.ScaleX = _viewZoom;
            _viewScale.ScaleY = _viewZoom;
        }
        if (_viewTranslate != null)
        {
            _viewTranslate.X = _viewPan.X;
            _viewTranslate.Y = _viewPan.Y;
        }
    }

    void ResetGraphView()
    {
        _viewZoom = 1;
        _viewPan = default;
        ApplyViewTransform();
    }

    void OnGraphViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_graphViewport == null) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        double oldZ = _viewZoom;
        double factor = e.Delta.Y > 0 ? 1.1 : (e.Delta.Y < 0 ? 1 / 1.1 : 1);
        if (Math.Abs(factor - 1) < 0.001) return;
        double newZ = Math.Clamp(oldZ * factor, 0.25, 3.0);
        var pos = e.GetPosition(_graphViewport);
        double gX = (pos.X - _viewPan.X) / oldZ;
        double gY = (pos.Y - _viewPan.Y) / oldZ;
        _viewZoom = newZ;
        _viewPan = new Point(pos.X - gX * newZ, pos.Y - gY * newZ);
        ApplyViewTransform();
    }

    void OnGraphViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_graphViewport == null) return;
        if (e.GetCurrentPoint(_graphViewport).Properties.IsMiddleButtonPressed)
        {
            _middlePanning = true;
            _middlePanPointerStart = e.GetPosition(_graphViewport);
            _viewPanAtMiddleStart = _viewPan;
            e.Pointer.Capture(_graphViewport);
            e.Handled = true;
        }
    }

    void OnGraphViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_graphViewport == null || !_middlePanning || !ReferenceEquals(e.Pointer.Captured, _graphViewport))
            return;
        var p = e.GetPosition(_graphViewport);
        _viewPan = _viewPanAtMiddleStart + (p - _middlePanPointerStart);
        ApplyViewTransform();
        e.Handled = true;
    }

    void OnGraphViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, _graphViewport)) return;
        _middlePanning = false;
        e.Pointer.Capture(null);
    }

    void OnGraphCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var canvas = _graphCanvas;
        if (canvas == null) return;

        if (_dragNodes != null && _dragNodeStartPos != null && _dragCaptureBorder != null
            && ReferenceEquals(e.Pointer.Captured, _dragCaptureBorder))
        {
            var cur = e.GetPosition(canvas);
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
            RefreshWirePathsOnly();
            e.Handled = true;
        }
    }

    void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_graphCanvas == null) return;
        if (!string.IsNullOrEmpty(_wireFromId) && _wirePreviewLine != null
            && ReferenceEquals(e.Pointer.Captured, this))
        {
            _wirePreviewLine.EndPoint = e.GetPosition(_graphCanvas);
            e.Handled = true;
        }
    }

    void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_graphCanvas == null) return;
        if (!string.IsNullOrEmpty(_wireFromId) && ReferenceEquals(e.Pointer.Captured, this))
        {
            var end = e.GetPosition(_graphCanvas);
            TryCompleteWire(end);
            if (_wirePreviewLine != null)
            {
                _graphCanvas.Children.Remove(_wirePreviewLine);
                _wirePreviewLine = null;
            }
            _wireFromId = null;
            _wireFromPin = BlueprintFlowRuntime.PinExecOut;
            e.Pointer.Capture(null);
            MarkDirty(true);
            RefreshUi();
            e.Handled = true;
        }
    }

    void OnGraphCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var canvas = _graphCanvas;
        if (canvas == null) return;

        if (_dragNodes != null && _dragCaptureBorder != null
            && ReferenceEquals(e.Pointer.Captured, _dragCaptureBorder))
        {
            e.Pointer.Capture(null);
            _dragNodes = null;
            _dragNodeStartPos = null;
            _dragCaptureBorder = null;
            MarkDirty(true);
            RefreshUi();
            e.Handled = true;
        }
    }

    void SyncDraggedBorderPositions()
    {
        if (_graphCanvas == null) return;
        foreach (var child in _graphCanvas.Children)
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

    void ClearGraphSelection(bool redraw)
    {
        _selectedIds.Clear();
        _primaryId = null;
        SyncListPrimary();
        RefreshPropertiesPanel();
        if (redraw) RebuildGraphCanvas();
    }

    void SyncListPrimary()
    {
        if (_nodeList == null) return;
        _syncGraphSelection = true;
        if (_primaryId != null)
        {
            for (int i = 0; i < _graph.Nodes.Count; i++)
            {
                if (_graph.Nodes[i].Id == _primaryId)
                {
                    _nodeList.SelectedIndex = i;
                    _syncGraphSelection = false;
                    return;
                }
            }
        }
        _nodeList.SelectedIndex = -1;
        _syncGraphSelection = false;
    }

    void TryCompleteWire(Point releaseCanvas)
    {
        if (string.IsNullOrEmpty(_wireFromId)) return;
        var fromNode = FindNode(_wireFromId);
        var fromDef = fromNode != null ? BlueprintNodeCatalog.Resolve(fromNode.Kind) : null;
        if (fromDef == null || fromDef.ExecOut <= 0)
        {
            SetStatus("Wire cancelled — source has no exec output.");
            return;
        }

        foreach (var n in _graph.Nodes)
        {
            if (n.Id == _wireFromId) continue;
            var def = BlueprintNodeCatalog.Resolve(n.Kind);
            if (def.ExecIn <= 0 || def.Category == BlueprintNodeCategory.Comment) continue;

            var pin = GetInPinCenter(n);
            double dxh = pin.X - releaseCanvas.X, dyh = pin.Y - releaseCanvas.Y;
            if (Math.Sqrt(dxh * dxh + dyh * dyh) < WireCompletePinRadius)
            {
                if (_graph.HasExecConnection(_wireFromId, _wireFromPin, n.Id))
                {
                    SetStatus("Already linked.");
                    return;
                }

                _graph.Wires.RemoveAll(w =>
                    w.FromNodeId == _wireFromId
                    && string.Equals(w.FromPin, _wireFromPin, StringComparison.OrdinalIgnoreCase));
                _graph.Wires.RemoveAll(w =>
                    w.ToNodeId == n.Id && BlueprintFlowRuntime.IsExecInPin(w.ToPin));

                _graph.Wires.Add(new BlueprintWire
                {
                    FromNodeId = _wireFromId,
                    ToNodeId = n.Id,
                    FromPin = _wireFromPin,
                    ToPin = BlueprintFlowRuntime.PinExecIn
                });
                SetStatus("Exec flow linked.");
                return;
            }
        }
        SetStatus("Wire cancelled — drop on an exec In pin (left, lighter pin).");
    }

    static Point GetOutPinCenter(BlueprintNode n, string fromPin)
    {
        var def = BlueprintNodeCatalog.Resolve(n.Kind);
        if (def.Category == BlueprintNodeCategory.Comment)
            return new Point(n.X + OutPinX, n.Y + CommentNodeH / 2);

        var outs = BlueprintNodeCatalog.OutboundExecPinNames(def);
        if (outs.Count >= 2)
        {
            if (string.Equals(fromPin, outs[0], StringComparison.OrdinalIgnoreCase))
                return new Point(n.X + OutPinX, n.Y + ExecPinCenterY - 8);
            if (string.Equals(fromPin, outs[1], StringComparison.OrdinalIgnoreCase))
                return new Point(n.X + OutPinX, n.Y + ExecPinCenterY + 8);
        }
        return new Point(n.X + OutPinX, n.Y + ExecPinCenterY);
    }

    static Point GetInPinCenter(BlueprintNode n)
    {
        if (BlueprintNodeCatalog.Resolve(n.Kind).Category == BlueprintNodeCategory.Comment)
            return new Point(n.X + InPinX, n.Y + CommentNodeH / 2);
        return new Point(n.X + InPinX, n.Y + ExecPinCenterY);
    }

    BlueprintNode? FindNode(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var n in _graph.Nodes)
            if (n.Id == id) return n;
        return null;
    }

    void RefreshWirePathsOnly()
    {
        if (_graphCanvas == null) return;
        for (int i = _graphCanvas.Children.Count - 1; i >= 0; i--)
            if (_graphCanvas.Children[i] is PathShape)
                _graphCanvas.Children.RemoveAt(i);

        int insertAt = 0;
        for (; insertAt < _graphCanvas.Children.Count; insertAt++)
            if (_graphCanvas.Children[insertAt] is Border) break;

        foreach (var w in _graph.Wires)
        {
            var path = CreateWirePath(w);
            if (path == null) continue;
            _graphCanvas.Children.Insert(insertAt, path);
            insertAt++;
        }
    }

    PathShape? CreateWirePath(BlueprintWire wire)
    {
        var a = FindNode(wire.FromNodeId);
        var b = FindNode(wire.ToNodeId);
        if (a == null || b == null) return null;
        if (!BlueprintFlowRuntime.IsExecOutPin(wire.FromPin) || !BlueprintFlowRuntime.IsExecInPin(wire.ToPin))
            return null;
        var da = BlueprintNodeCatalog.Resolve(a.Kind);
        var db = BlueprintNodeCatalog.Resolve(b.Kind);
        if (da.ExecOut <= 0 || db.ExecIn <= 0) return null;

        var path = BuildWirePathShape(GetOutPinCenter(a, wire.FromPin), GetInPinCenter(b),
            Color.FromRgb(0xC8, 0xD4, 0xEE));
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
                _graph.Wires.Remove(wref);
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

    void StartWireFromPin(BlueprintNode nodeRef, string fromPin, PointerPressedEventArgs ev)
    {
        if (_graphCanvas == null) return;
        _wireFromId = nodeRef.Id;
        _wireFromPin = fromPin;
        var start = GetOutPinCenter(nodeRef, fromPin);
        _wirePreviewLine = new Line
        {
            StartPoint = start,
            EndPoint = start,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0xE8, 0xFF)),
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            IsHitTestVisible = false
        };
        _graphCanvas.Children.Add(_wirePreviewLine);
        ev.Pointer.Capture(this);
        ev.Handled = true;
    }

    void RebuildGraphCanvas()
    {
        if (_graphCanvas == null) return;
        _graphCanvas.Children.Clear();

        foreach (var w in _graph.Wires)
        {
            var path = CreateWirePath(w);
            if (path != null)
                _graphCanvas.Children.Add(path);
        }

        foreach (var n in _graph.Nodes)
        {
            var def = BlueprintNodeCatalog.Resolve(n.Kind);
            bool sel = _selectedIds.Contains(n.Id);
            bool isComment = def.Category == BlueprintNodeCategory.Comment;
            var outNames = BlueprintNodeCatalog.OutboundExecPinNames(def);
            bool dualOut = outNames.Count >= 2;
            double nh = isComment ? CommentNodeH : (dualOut ? NodeH + 18 : NodeH);

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

            if (isComment)
            {
                var bar = new Border
                {
                    Width = CategoryBarW,
                    Background = accentBrush,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                DockPanel.SetDock(bar, Dock.Left);
                var chrome = new DockPanel();
                chrome.Children.Add(bar);
                chrome.Children.Add(header);
                border.Child = chrome;
            }
            else
            {
                var inPin = new Ellipse
                {
                    Width = 11,
                    Height = 11,
                    Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x34)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xD0, 0xDA, 0xEC)),
                    StrokeThickness = 1.5,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Cross),
                    IsVisible = def.ExecIn > 0,
                    IsHitTestVisible = def.ExecIn > 0
                };

                var pinRow = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
                DockPanel.SetDock(inPin, Dock.Left);

                var nodeRef = n;
                if (dualOut)
                {
                    var outStack = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        Spacing = 6,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 4, 0)
                    };
                    foreach (var pinName in outNames)
                    {
                        var row = new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 4,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        };
                        row.Children.Add(new TextBlock
                        {
                            Text = pinName,
                            FontSize = 8,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Width = 32
                        });
                        var eOut = new Ellipse
                        {
                            Width = 11,
                            Height = 11,
                            Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x34)),
                            Stroke = new SolidColorBrush(Color.FromRgb(0xD0, 0xDA, 0xEC)),
                            StrokeThickness = 1.5,
                            Cursor = new Cursor(StandardCursorType.Cross)
                        };
                        var pnm = pinName;
                        eOut.PointerPressed += (_, ev) => StartWireFromPin(nodeRef, pnm, ev);
                        row.Children.Add(eOut);
                        outStack.Children.Add(row);
                    }
                    DockPanel.SetDock(outStack, Dock.Right);
                    pinRow.Children.Add(inPin);
                    pinRow.Children.Add(outStack);
                }
                else if (def.ExecOut > 0 && outNames.Count > 0)
                {
                    var eOut = new Ellipse
                    {
                        Width = 11,
                        Height = 11,
                        Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x34)),
                        Stroke = new SolidColorBrush(Color.FromRgb(0xD0, 0xDA, 0xEC)),
                        StrokeThickness = 1.5,
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Cursor = new Cursor(StandardCursorType.Cross)
                    };
                    var pnm = outNames[0];
                    eOut.PointerPressed += (_, ev) => StartWireFromPin(nodeRef, pnm, ev);
                    DockPanel.SetDock(eOut, Dock.Right);
                    pinRow.Children.Add(inPin);
                    pinRow.Children.Add(eOut);
                }
                else
                {
                    pinRow.Children.Add(inPin);
                }

                pinRow.Children.Add(new Border { Height = 1 });

                var root = new AvGrid();
                root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                AvGrid.SetRow(header, 0);
                AvGrid.SetRow(pinRow, 1);
                root.Children.Add(header);
                root.Children.Add(pinRow);

                var bar = new Border
                {
                    Width = CategoryBarW,
                    Background = accentBrush,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                DockPanel.SetDock(bar, Dock.Left);
                var chrome = new DockPanel();
                chrome.Children.Add(bar);
                chrome.Children.Add(root);
                border.Child = chrome;
            }

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
                    SyncListPrimary();
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
                SyncListPrimary();
                RefreshPropertiesPanel();

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
                _dragPointerStartCanvas = ev.GetPosition(_graphCanvas);
                _dragCaptureBorder = border;
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0xC0, 0xFF));
                border.BorderThickness = new Thickness(2);
                ev.Pointer.Capture(border);
                ev.Handled = true;
                _graphCanvas?.Focus();
            };

            _graphCanvas.Children.Add(border);
        }
    }

    async System.Threading.Tasks.Task OnNewAsync()
    {
        _graph = new BlueprintGraph();
        _currentFileAbs = null;
        _selectedIds.Clear();
        _primaryId = null;
        MarkDirty(false);
        RefreshPathDisplay();
        RefreshUi();
        SetStatus(ProjectService.Current != null
            ? "New graph — wire Begin Play → actions, save, then add Visual Blueprint on a GameObject."
            : "New graph — open a project to save under Assets/Blueprints.");
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
            _graph = doc.Graph;
            _currentFileAbs = System.IO.Path.GetFullPath(path);
            _selectedIds.Clear();
            _primaryId = null;
            MarkDirty(false);
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
            BlueprintPersistence.Save(_currentFileAbs, _graph);
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
            BlueprintPersistence.Save(path, _graph);
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

    void AddNodeFromCombo()
    {
        int idx = _kindCombo?.SelectedIndex ?? 0;
        var palette = BlueprintNodeCatalog.AuthoringPaletteOrdered;
        if (idx < 0 || idx >= palette.Length) idx = 0;
        AddNodeKind(palette[idx]);
    }

    void AddNodeKind(string kind)
    {
        var t = BlueprintNodeCatalog.Resolve(kind);
        int i = _graph.Nodes.Count;
        double px = 80 + (i % 5) * 200;
        double py = 80 + (i / 5) * 130;
        var n = _graph.AddNode(kind, t.DefaultTitle + " " + (i + 1), px, py);
        foreach (var kv in t.DefaultProperties)
            n.Properties[kv.Key] = kv.Value;
        MarkDirty(true);
        RefreshUi();
        ClearStatus();
    }

    void ClearGraph()
    {
        _graph.Nodes.Clear();
        _graph.Wires.Clear();
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
            SetStatus("Select node(s) on the canvas or list first.", error: true);
            return;
        }
        DeleteNodesByIds(_selectedIds.ToArray());
    }

    void OnPropTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_syncPropFields) return;
        var n = FindNode(_primaryId);
        if (n == null || _txtPropTitle == null) return;
        var t = (_txtPropTitle.Text ?? "").Trim();
        if (t.Length == 0) return;
        if (string.Equals(t, n.Title, StringComparison.Ordinal)) return;
        n.Title = t;
        MarkDirty(true);
        RefreshUi();
    }

    void DuplicateSelectedNode()
    {
        var sourceIds = _selectedIds.Count > 0
            ? _selectedIds.ToArray()
            : _primaryId is { } pid ? new[] { pid } : Array.Empty<string>();
        if (sourceIds.Length == 0 && _nodeList?.SelectedIndex is int si && si >= 0 && si < _graph.Nodes.Count)
            sourceIds = new[] { _graph.Nodes[si].Id };
        if (sourceIds.Length == 0)
        {
            SetStatus("Select node(s) first.", error: true);
            return;
        }

        _selectedIds.Clear();
        var newPrimary = (string?)null;
        for (int i = 0; i < sourceIds.Length; i++)
        {
            var n = FindNode(sourceIds[i]);
            if (n == null) continue;
            var copy = new BlueprintNode
            {
                Kind = n.Kind,
                Title = n.Title + " copy",
                X = n.X + 40 + i * 10,
                Y = n.Y + 40 + i * 10
            };
            foreach (var kv in n.Properties)
                copy.Properties[kv.Key] = kv.Value;
            _graph.Nodes.Add(copy);
            _selectedIds.Add(copy.Id);
            newPrimary = copy.Id;
        }
        _primaryId = newPrimary;
        SyncListPrimary();
        MarkDirty(true);
        RefreshUi();
        SetStatus(sourceIds.Length == 1 ? "Node duplicated." : $"{sourceIds.Length} nodes duplicated.");
    }

    void RefreshPropertiesPanel()
    {
        if (_tblPropKind == null || _tblPropId == null || _tblPropWires == null || _txtPropTitle == null
            || _tblPropDesc == null)
            return;

        _syncPropFields = true;
        try
        {
            var n = FindNode(_primaryId);
            if (n == null)
            {
                _txtPropTitle.Text = "";
                _txtPropTitle.IsEnabled = false;
                _tblPropKind.Text = "—";
                _tblPropId.Text = "—";
                _tblPropDesc.Text = "";
                _tblPropWires.Text = "";
                RebuildPropExtras(null);
                return;
            }

            if (_selectedIds.Count > 1)
            {
                _txtPropTitle.Text = n.Title;
                _txtPropTitle.IsEnabled = false;
                _tblPropKind.Text = $"{n.Kind} · {_selectedIds.Count} selected";
                _tblPropId.Text = n.Id;
                _tblPropDesc.Text = "";
                int i0 = _graph.Wires.Count(w => string.Equals(w.ToNodeId, n.Id, StringComparison.Ordinal));
                int o0 = _graph.Wires.Count(w => string.Equals(w.FromNodeId, n.Id, StringComparison.Ordinal));
                _tblPropWires.Text = $"Primary node wires: {i0} in · {o0} out. Title edit disabled for multi‑select.";
                RebuildPropExtras(null);
                return;
            }

            var def = BlueprintNodeCatalog.Resolve(n.Kind);
            _txtPropTitle.Text = n.Title;
            _txtPropTitle.IsEnabled = true;
            _tblPropKind.Text = n.Kind;
            _tblPropId.Text = n.Id;
            _tblPropDesc.Text = def.Description;
            int incoming = _graph.Wires.Count(w => string.Equals(w.ToNodeId, n.Id, StringComparison.Ordinal));
            int outgoing = _graph.Wires.Count(w => string.Equals(w.FromNodeId, n.Id, StringComparison.Ordinal));
            _tblPropWires.Text =
                $"Exec wires: {incoming} in · {outgoing} out — right‑click a wire to delete.";
            RebuildPropExtras(n);
        }
        finally
        {
            _syncPropFields = false;
        }
    }

    void RebuildPropExtras(BlueprintNode? n)
    {
        if (_propExtrasHost == null) return;
        _propExtrasHost.Children.Clear();
        if (n == null) return;

        var t = BlueprintNodeCatalog.Resolve(n.Kind);
        if (string.Equals(n.Kind, "ReflectGet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Kind, "ReflectSet", StringComparison.OrdinalIgnoreCase))
        {
            BuildReflectPropertyEditors(n);
            return;
        }

        if (t.EditablePropertyKeys.Length == 0) return;

        _propExtrasHost.Children.Add(new TextBlock
        {
            Text = "Parameters",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
        });

        foreach (var key in t.EditablePropertyKeys)
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
                nodeCap.Properties[keyCap] = nt;
                MarkDirty(true);
                RebuildGraphCanvas();
            };
            row.Children.Add(tb);
            _propExtrasHost.Children.Add(row);
        }
    }

    void BuildReflectPropertyEditors(BlueprintNode n)
    {
        if (_propExtrasHost == null) return;

        void Commit(string key, string value)
        {
            value = value.Trim();
            if (n.Properties.TryGetValue(key, out var old) && string.Equals(old, value, StringComparison.Ordinal))
                return;
            n.Properties[key] = value;
            MarkDirty(true);
            RebuildGraphCanvas();
        }

        var muted = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99));
        _propExtrasHost.Children.Add(new TextBlock
        {
            Text = "Parameters — pick types and members (or type custom names)",
            FontSize = 10,
            Foreground = muted,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var modeNow = (n.Properties.TryGetValue("mode", out var m) ? m : "Instance").Trim();
        var instance = string.Equals(modeNow, "Instance", StringComparison.OrdinalIgnoreCase);
        var scopeNow = (n.Properties.TryGetValue("scope", out var sc) ? sc : "Self").Trim();

        static StackPanel LabeledRow(string label)
        {
            return new StackPanel
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
        }

        // mode
        var rowMode = LabeledRow("mode");
        var modeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Instance", "Static" }
        };
        modeCombo.SelectedItem = new[] { "Instance", "Static" }.FirstOrDefault(x => string.Equals(x, modeNow, StringComparison.OrdinalIgnoreCase)) ?? "Instance";
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncPropFields) return;
            if (modeCombo.SelectedItem is not string sel) return;
            Commit("mode", sel);
            RebuildPropExtras(n);
        };
        rowMode.Children.Add(modeCombo);
        _propExtrasHost.Children.Add(rowMode);

        // scope (instance)
        var rowScope = LabeledRow("scope (instance only)");
        rowScope.IsVisible = instance;
        var scopeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Self", "Other" }
        };
        scopeCombo.SelectedItem = new[] { "Self", "Other" }.FirstOrDefault(x => string.Equals(x, scopeNow, StringComparison.OrdinalIgnoreCase)) ?? "Self";
        scopeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncPropFields) return;
            if (scopeCombo.SelectedItem is not string sel) return;
            Commit("scope", sel);
            RebuildPropExtras(n);
        };
        rowScope.Children.Add(scopeCombo);
        _propExtrasHost.Children.Add(rowScope);

        var showTargets = instance && string.Equals(scopeNow, "Other", StringComparison.OrdinalIgnoreCase);
        var rowPath = LabeledRow("targetPath (hierarchy path)");
        rowPath.IsVisible = showTargets;
        var tbPath = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("targetPath", out var tp) ? tp : "" };
        tbPath.LostFocus += (_, _) => Commit("targetPath", tbPath.Text ?? "");
        rowPath.Children.Add(tbPath);
        _propExtrasHost.Children.Add(rowPath);

        var rowName = LabeledRow("targetName (scene search)");
        rowName.IsVisible = showTargets;
        var tbName = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("targetName", out var tn) ? tn : "" };
        tbName.LostFocus += (_, _) => Commit("targetName", tbName.Text ?? "");
        rowName.Children.Add(tbName);
        _propExtrasHost.Children.Add(rowName);

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

        // typeName (static)
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
        _propExtrasHost.Children.Add(rowStaticType);

        // componentType (instance)
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
                var keep = n.Properties.TryGetValue("memberPath", out var kp) ? kp : "";
                memberAc.Text = keep;
            }
        };
        rowComp.Children.Add(compAc);
        _propExtrasHost.Children.Add(rowComp);

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
        _propExtrasHost.Children.Add(rowMember);

        if (string.Equals(n.Kind, "ReflectGet", StringComparison.OrdinalIgnoreCase))
        {
            var rowVk = LabeledRow("varKey");
            var tbVk = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("varKey", out var vk) ? vk : "" };
            tbVk.LostFocus += (_, _) => Commit("varKey", tbVk.Text ?? "");
            rowVk.Children.Add(tbVk);
            _propExtrasHost.Children.Add(rowVk);
        }
        else
        {
            var rowVal = LabeledRow("value (literal, or leave empty to use valueVarKey)");
            var tbVal = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("value", out var vv) ? vv : "" };
            tbVal.LostFocus += (_, _) => Commit("value", tbVal.Text ?? "");
            rowVal.Children.Add(tbVal);
            _propExtrasHost.Children.Add(rowVal);

            var rowVvk = LabeledRow("valueVarKey");
            var tbVvk = new TextBox { FontSize = 11, Text = n.Properties.TryGetValue("valueVarKey", out var vvk) ? vvk : "" };
            tbVvk.LostFocus += (_, _) => Commit("valueVarKey", tbVvk.Text ?? "");
            rowVvk.Children.Add(tbVvk);
            _propExtrasHost.Children.Add(rowVvk);
        }
    }

    void DeleteNodesByIds(params string[] ids)
    {
        var set = ids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return;

        _graph.Wires.RemoveAll(w => set.Contains(w.FromNodeId) || set.Contains(w.ToNodeId));
        _graph.Nodes.RemoveAll(n => set.Contains(n.Id));
        _selectedIds.ExceptWith(set);
        if (_primaryId != null && !_selectedIds.Contains(_primaryId))
            _primaryId = _selectedIds.Count > 0 ? _selectedIds.First() : null;
        MarkDirty(true);
        RefreshUi();
        ClearStatus();
    }

    void MarkDirty(bool dirty)
    {
        _dirty = dirty;
        RefreshPathDisplay();
    }

    void RefreshPathDisplay()
    {
        if (_txtDocumentPath == null) return;
        var proj = ProjectService.Current;
        if (proj == null)
        {
            _txtDocumentPath.Text = "(open a project)";
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentFileAbs))
        {
            _txtDocumentPath.Text = _dirty ? "Unsaved blueprint *" : "Unsaved blueprint";
            return;
        }

        var rel = BlueprintPersistence.TryGetDisplayPath(_currentFileAbs, proj.RootPath);
        _txtDocumentPath.Text = (rel ?? _currentFileAbs) + (_dirty ? " *" : "");
    }

    void SetStatus(string msg, bool error = false)
    {
        if (_txtStatus == null) return;
        _txtStatus.Text = msg;
        _txtStatus.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88))
            : new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x99));
    }

    void ClearStatus()
    {
        if (_txtStatus != null) _txtStatus.Text = "";
    }

    void RefreshUi()
    {
        if (_nodeList != null)
        {
            _syncGraphSelection = true;
            var items = _graph.Nodes.Select(n => $"{n.Kind}: {n.Title} ({n.Id})").ToList();
            _nodeList.ItemsSource = items;
            if (_primaryId != null)
            {
                for (int i = 0; i < _graph.Nodes.Count; i++)
                {
                    if (_graph.Nodes[i].Id == _primaryId)
                    {
                        _nodeList.SelectedIndex = i;
                        break;
                    }
                }
            }
            else
                _nodeList.SelectedIndex = -1;
            _syncGraphSelection = false;
        }
        if (_txtSummary != null)
            _txtSummary.Text = BlueprintGraphDescribe.Summarize(_graph);

        RefreshPropertiesPanel();
        RebuildGraphCanvas();
    }
}
