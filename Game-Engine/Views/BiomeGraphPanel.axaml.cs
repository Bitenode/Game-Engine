#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Biome.Graph;

namespace Game_Engine.Views;

public partial class BiomeGraphPanel : UserControl
{
    BiomeGraph _graph = new();
    Canvas? _canvas;
    Canvas? _worldCanvas;
    StackPanel? _propsPanel;
    TextBlock? _propsNodeName;
    ComboBox? _nodeTypeCombo;
    bool _loadedOnce;
    string _currentGraphPath = "";
    Dictionary<string, VegetationProfile> _vegProfiles = new(StringComparer.OrdinalIgnoreCase);

    BiomeNode? _selectedNode;
    BiomeNode? _draggingNode;
    BiomePort? _draggingPort;
    Point _dragStart;
    Point _dragNodeOffset;
    Avalonia.Controls.Shapes.Path? _dragLine;

    float _zoom = 1f;
    float _panX, _panY;
    bool _panning;
    Point _panStart;

    // Undo/redo stacks
    readonly Stack<string> _undoStack = new();
    readonly Stack<string> _redoStack = new();
    bool _suppressUndoCapture;

    const float NodeWidth = 160;
    const float HeaderHeight = 28;
    const float PortSpacing = 22;
    const float PortRadius = 7;

    public BiomeGraphPanel()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (_loadedOnce) return;
        _loadedOnce = true;

        _canvas = this.FindControl<Canvas>("GraphCanvas");
        _propsPanel = this.FindControl<StackPanel>("PropsPanel");
        _propsNodeName = this.FindControl<TextBlock>("PropsNodeName");
        _nodeTypeCombo = this.FindControl<ComboBox>("NodeTypeCombo");

        var addBtn = this.FindControl<Button>("AddNodeBtn");
        var delBtn = this.FindControl<Button>("DeleteNodeBtn");
        var saveBtn = this.FindControl<Button>("SaveBtn");
        var loadBtn = this.FindControl<Button>("LoadBtn");
        var compileBtn = this.FindControl<Button>("CompileBtn");
        var newBtn = this.FindControl<Button>("NewDefaultBtn");
        var previewBtn = this.FindControl<Button>("PreviewBtn");
        var validateBtn = this.FindControl<Button>("ValidateBtn");
        var undoBtn = this.FindControl<Button>("UndoBtn");
        var redoBtn = this.FindControl<Button>("RedoBtn");

        if (addBtn != null) addBtn.Click += OnAddNode;
        if (delBtn != null) delBtn.Click += OnDeleteNode;
        if (saveBtn != null) saveBtn.Click += OnSave;
        if (loadBtn != null) loadBtn.Click += OnLoad;
        if (compileBtn != null) compileBtn.Click += OnCompile;
        if (newBtn != null) newBtn.Click += OnNewDefault;
        if (previewBtn != null) previewBtn.Click += OnPreview;
        if (validateBtn != null) validateBtn.Click += OnValidate;
        if (undoBtn != null) undoBtn.Click += OnUndo;
        if (redoBtn != null) redoBtn.Click += OnRedo;

        if (_canvas != null)
        {
            _worldCanvas = new Canvas();
            _canvas.Children.Add(_worldCanvas);

            _canvas.PointerPressed += OnCanvasPointerPressed;
            _canvas.PointerMoved += OnCanvasPointerMoved;
            _canvas.PointerReleased += OnCanvasPointerReleased;
            _canvas.PointerWheelChanged += OnCanvasWheel;
            _canvas.KeyDown += OnCanvasKeyDown;
        }

        _graph = BiomeGraph.CreateDefault();
        LoadVegetationProfiles();
        CaptureUndo();
        Redraw();
    }

    // ── Undo/Redo ──

    void CaptureUndo()
    {
        if (_suppressUndoCapture) return;
        try
        {
            string tempPath = System.IO.Path.GetTempFileName();
            _graph.SaveToFile(tempPath);
            string json = System.IO.File.ReadAllText(tempPath);
            System.IO.File.Delete(tempPath);
            _undoStack.Push(json);
            _redoStack.Clear();
        }
        catch { }
    }

    void RestoreFromJson(string json)
    {
        try
        {
            string tempPath = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(tempPath, json);
            _suppressUndoCapture = true;
            _graph = BiomeGraph.LoadFromFile(tempPath);
            System.IO.File.Delete(tempPath);
            _suppressUndoCapture = false;
            _selectedNode = null;
            Redraw();
            UpdateProperties();
        }
        catch { _suppressUndoCapture = false; }
    }

    void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_undoStack.Count <= 1) return;
        _redoStack.Push(_undoStack.Pop());
        if (_undoStack.Count > 0)
            RestoreFromJson(_undoStack.Peek());
    }

    void OnRedo(object? sender, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0) return;
        var json = _redoStack.Pop();
        _undoStack.Push(json);
        RestoreFromJson(json);
    }

    void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z) { OnUndo(null, e); e.Handled = true; }
            else if (e.Key == Key.Y) { OnRedo(null, e); e.Handled = true; }
        }
    }

    // ── Coordinate transforms ──

    Point ScreenToWorld(Point screen)
    {
        return new Point(
            (screen.X - _panX) / _zoom,
            (screen.Y - _panY) / _zoom);
    }

    void ApplyCanvasTransform()
    {
        if (_worldCanvas == null) return;
        _worldCanvas.RenderTransform = new MatrixTransform(
            new Matrix(_zoom, 0, 0, _zoom, _panX, _panY));
    }

    // ── Drawing ──

    void Redraw()
    {
        if (_worldCanvas == null) return;
        _worldCanvas.Children.Clear();

        DrawGrid();

        foreach (var conn in _graph.Connections)
            DrawConnection(conn);

        foreach (var node in _graph.Nodes)
            DrawNode(node);

        ApplyCanvasTransform();
    }

    void DrawGrid()
    {
        if (_worldCanvas == null || _canvas == null) return;
        float step = 30;
        float w = (float)(_canvas.Bounds.Width / _zoom) + 200;
        float h = (float)(_canvas.Bounds.Height / _zoom) + 200;
        float ox = -_panX / _zoom;
        float oy = -_panY / _zoom;

        float startX = MathF.Floor(ox / step) * step;
        float startY = MathF.Floor(oy / step) * step;

        for (float x = startX; x < ox + w; x += step)
        {
            for (float y = startY; y < oy + h; y += step)
            {
                var dot = new Ellipse
                {
                    Width = 2, Height = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x - 1);
                Canvas.SetTop(dot, y - 1);
                _worldCanvas.Children.Add(dot);
            }
        }
    }

    void DrawNode(BiomeNode node)
    {
        if (_worldCanvas == null) return;

        int portCount = Math.Max(node.Inputs.Count, node.Outputs.Count);
        float bodyH = HeaderHeight + portCount * PortSpacing + 12;

        var shadow = new Rectangle
        {
            Width = NodeWidth + 4, Height = bodyH + 4,
            RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(shadow, node.EditorX - 2);
        Canvas.SetTop(shadow, node.EditorY + 2);
        _worldCanvas.Children.Add(shadow);

        var body = new Rectangle
        {
            Width = NodeWidth, Height = bodyH,
            RadiusX = 6, RadiusY = 6,
            Fill = new SolidColorBrush(GetNodeColor(node)),
            Stroke = _selectedNode == node
                ? new SolidColorBrush(Color.Parse("#6688FF"))
                : new SolidColorBrush(Color.FromArgb(80, 100, 100, 140)),
            StrokeThickness = _selectedNode == node ? 2 : 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(body, node.EditorX);
        Canvas.SetTop(body, node.EditorY);
        _worldCanvas.Children.Add(body);

        var header = new Rectangle
        {
            Width = NodeWidth, Height = HeaderHeight,
            RadiusX = 6, RadiusY = 6,
            Fill = new SolidColorBrush(GetNodeHeaderColor(node)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(header, node.EditorX);
        Canvas.SetTop(header, node.EditorY);
        _worldCanvas.Children.Add(header);

        var title = new TextBlock
        {
            Text = node.Name,
            FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(title, node.EditorX + 8);
        Canvas.SetTop(title, node.EditorY + 6);
        _worldCanvas.Children.Add(title);

        for (int i = 0; i < node.Inputs.Count; i++)
            DrawPort(node, node.Inputs[i], i, false);
        for (int i = 0; i < node.Outputs.Count; i++)
            DrawPort(node, node.Outputs[i], i, true);
    }

    void DrawPort(BiomeNode node, BiomePort port, int index, bool isOutput)
    {
        if (_worldCanvas == null) return;

        float px = isOutput ? node.EditorX + NodeWidth : node.EditorX;
        float py = node.EditorY + HeaderHeight + 10 + index * PortSpacing;

        bool connected = port.Connection != null ||
            _graph.Connections.Any(c => c.From == port || c.To == port);

        var circle = new Ellipse
        {
            Width = PortRadius * 2, Height = PortRadius * 2,
            Fill = connected
                ? new SolidColorBrush(Color.Parse("#FFFFFF"))
                : new SolidColorBrush(isOutput ? Color.Parse("#FF8844") : Color.Parse("#4488FF")),
            Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            StrokeThickness = connected ? 1.5 : 0.5,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(circle, px - PortRadius);
        Canvas.SetTop(circle, py - PortRadius);
        _worldCanvas.Children.Add(circle);

        var label = new TextBlock
        {
            Text = port.Name, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 220, 220, 240)),
            IsHitTestVisible = false,
        };

        if (isOutput)
        {
            Canvas.SetLeft(label, px - 8 - port.Name.Length * 5.5);
            Canvas.SetTop(label, py - 7);
        }
        else
        {
            Canvas.SetLeft(label, px + PortRadius + 4);
            Canvas.SetTop(label, py - 7);
        }
        _worldCanvas.Children.Add(label);
    }

    void DrawConnection(BiomeConnection conn)
    {
        if (_worldCanvas == null) return;

        var fromNode = conn.From.Owner;
        var toNode = conn.To.Owner;
        int fi = fromNode.Outputs.IndexOf(conn.From);
        int ti = toNode.Inputs.IndexOf(conn.To);

        float x1 = fromNode.EditorX + NodeWidth;
        float y1 = fromNode.EditorY + HeaderHeight + 10 + fi * PortSpacing;
        float x2 = toNode.EditorX;
        float y2 = toNode.EditorY + HeaderHeight + 10 + ti * PortSpacing;

        DrawBezier(x1, y1, x2, y2, Color.FromArgb(180, 200, 200, 255));
    }

    void DrawBezier(float x1, float y1, float x2, float y2, Color color)
    {
        if (_worldCanvas == null) return;
        float dist = MathF.Abs(x2 - x1) * 0.4f + 40;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Data = Geometry.Parse($"M {F(x1)},{F(y1)} C {F(x1 + dist)},{F(y1)} {F(x2 - dist)},{F(y2)} {F(x2)},{F(y2)}")
        };
        _worldCanvas.Children.Add(path);
    }

    static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    static Color GetNodeColor(BiomeNode node) => node switch
    {
        BiomeOutputNode => Color.Parse("#443344"),
        BiomeLayerNode => Color.Parse("#2D4430"),
        BiomeNoiseNode => Color.Parse("#2D3344"),
        BiomeCaveNode => Color.Parse("#442D2D"),
        BiomeAltitudeNode => Color.Parse("#2D4444"),
        BiomeSlopeNode => Color.Parse("#44442D"),
        BiomeErosionNode => Color.Parse("#3D2D44"),
        BiomeMaskNode => Color.Parse("#2D3D3D"),
        BiomeRiverNode => Color.Parse("#2D3D44"),
        _ => Color.Parse("#2D2D44"),
    };

    static Color GetNodeHeaderColor(BiomeNode node) => node switch
    {
        BiomeOutputNode => Color.Parse("#664466"),
        BiomeLayerNode => Color.Parse("#406640"),
        BiomeNoiseNode => Color.Parse("#405060"),
        BiomeCaveNode => Color.Parse("#604040"),
        BiomeAltitudeNode => Color.Parse("#406666"),
        BiomeSlopeNode => Color.Parse("#666640"),
        BiomeErosionNode => Color.Parse("#604066"),
        BiomeMaskNode => Color.Parse("#406060"),
        BiomeRiverNode => Color.Parse("#406066"),
        _ => Color.Parse("#404060"),
    };

    // ── Port hit testing ──

    (float x, float y) GetPortWorldPos(BiomePort port)
    {
        var node = port.Owner;
        bool isOutput = node.Outputs.Contains(port);
        int idx = isOutput ? node.Outputs.IndexOf(port) : node.Inputs.IndexOf(port);
        float px = isOutput ? node.EditorX + NodeWidth : node.EditorX;
        float py = node.EditorY + HeaderHeight + 10 + idx * PortSpacing;
        return (px, py);
    }

    (BiomeNode? node, BiomePort? port) HitTest(Point worldPos)
    {
        for (int i = _graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = _graph.Nodes[i];
            foreach (var p in node.Outputs)
            {
                var (px, py) = GetPortWorldPos(p);
                if (Math.Abs(worldPos.X - px) < 14 && Math.Abs(worldPos.Y - py) < 14)
                    return (node, p);
            }
            foreach (var p in node.Inputs)
            {
                var (px, py) = GetPortWorldPos(p);
                if (Math.Abs(worldPos.X - px) < 14 && Math.Abs(worldPos.Y - py) < 14)
                    return (node, p);
            }

            int portCount = Math.Max(node.Inputs.Count, node.Outputs.Count);
            float bodyH = HeaderHeight + portCount * PortSpacing + 12;
            if (worldPos.X >= node.EditorX && worldPos.X <= node.EditorX + NodeWidth &&
                worldPos.Y >= node.EditorY && worldPos.Y <= node.EditorY + bodyH)
                return (node, null);
        }
        return (null, null);
    }

    // ── Input handling ──

    void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_canvas == null) return;
        var props = e.GetCurrentPoint(_canvas).Properties;
        var screenPos = e.GetPosition(_canvas);
        var worldPos = ScreenToWorld(screenPos);

        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _panning = true;
            _panStart = screenPos;
            e.Pointer.Capture(_canvas);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            var (hitNode, hitPort) = HitTest(worldPos);

            if (hitPort != null)
            {
                if (hitPort.IsOutput)
                {
                    _draggingPort = hitPort;
                }
                else
                {
                    if (hitPort.Connection != null)
                    {
                        _draggingPort = hitPort.Connection;
                        _graph.Disconnect(hitPort);
                        Redraw();
                    }
                    else
                    {
                        _draggingPort = hitPort;
                    }
                }
                e.Pointer.Capture(_canvas);
                e.Handled = true;
                return;
            }

            if (hitNode != null)
            {
                _selectedNode = hitNode;
                _draggingNode = hitNode;
                _dragStart = worldPos;
                _dragNodeOffset = new Point(
                    worldPos.X - hitNode.EditorX,
                    worldPos.Y - hitNode.EditorY);
                e.Pointer.Capture(_canvas);
                Redraw();
                UpdateProperties();
                e.Handled = true;
                return;
            }

            _selectedNode = null;
            Redraw();
            UpdateProperties();
        }
    }

    void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_canvas == null) return;
        var screenPos = e.GetPosition(_canvas);

        if (_panning)
        {
            _panX += (float)(screenPos.X - _panStart.X);
            _panY += (float)(screenPos.Y - _panStart.Y);
            _panStart = screenPos;
            ApplyCanvasTransform();
            return;
        }

        if (_draggingPort != null)
        {
            var wp = ScreenToWorld(screenPos);
            UpdateDragLine(wp);
            return;
        }

        if (_draggingNode != null)
        {
            var wp = ScreenToWorld(screenPos);
            _draggingNode.EditorX = (float)(wp.X - _dragNodeOffset.X);
            _draggingNode.EditorY = (float)(wp.Y - _dragNodeOffset.Y);
            Redraw();
        }
    }

    void UpdateDragLine(Point worldMousePos)
    {
        if (_canvas == null || _draggingPort == null) return;

        RemoveDragLine();

        var (portX, portY) = GetPortWorldPos(_draggingPort);
        bool fromOutput = _draggingPort.IsOutput;
        float x1 = fromOutput ? portX : (float)worldMousePos.X;
        float y1 = fromOutput ? portY : (float)worldMousePos.Y;
        float x2 = fromOutput ? (float)worldMousePos.X : portX;
        float y2 = fromOutput ? (float)worldMousePos.Y : portY;

        float dist = MathF.Abs(x2 - x1) * 0.4f + 40;
        _dragLine = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 200, 100)),
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 4 },
            IsHitTestVisible = false,
            Data = Geometry.Parse($"M {F(x1)},{F(y1)} C {F(x1 + dist)},{F(y1)} {F(x2 - dist)},{F(y2)} {F(x2)},{F(y2)}")
        };
        _worldCanvas?.Children.Add(_dragLine);
    }

    void RemoveDragLine()
    {
        if (_dragLine != null && _worldCanvas != null)
        {
            _worldCanvas.Children.Remove(_dragLine);
            _dragLine = null;
        }
    }

    void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_canvas == null) return;
        e.Pointer.Capture(null);

        if (_panning) { _panning = false; return; }

        if (_draggingPort != null)
        {
            RemoveDragLine();
            var worldPos = ScreenToWorld(e.GetPosition(_canvas));
            var (hitNode, hitPort) = HitTest(worldPos);

            if (hitPort != null && hitPort != _draggingPort && hitPort.Owner != _draggingPort.Owner)
            {
                BiomePort from, to;
                if (_draggingPort.IsOutput && !hitPort.IsOutput)
                {
                    from = _draggingPort;
                    to = hitPort;
                }
                else if (!_draggingPort.IsOutput && hitPort.IsOutput)
                {
                    from = hitPort;
                    to = _draggingPort;
                }
                else
                {
                    from = to = null!;
                }

                if (from != null && to != null)
                {
                    _graph.Connect(from, to);
                    CaptureUndo();
                }
            }

            _draggingPort = null;
            Redraw();
            return;
        }

        if (_draggingNode != null)
        {
            CaptureUndo();
        }
        _draggingNode = null;
    }

    void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_canvas == null) return;
        var screenPos = e.GetPosition(_canvas);
        float oldZoom = _zoom;
        float delta = (float)(e.Delta.Y > 0 ? 1.1 : 0.9);
        _zoom = Math.Clamp(_zoom * delta, 0.2f, 3f);

        _panX = (float)(screenPos.X - (screenPos.X - _panX) * (_zoom / oldZoom));
        _panY = (float)(screenPos.Y - (screenPos.Y - _panY) * (_zoom / oldZoom));

        Redraw();
    }

    // ── Toolbar handlers ──

    void OnAddNode(object? sender, RoutedEventArgs e)
    {
        int idx = _nodeTypeCombo?.SelectedIndex ?? 0;
        BiomeNode node = idx switch
        {
            0 => new BiomeNoiseNode(),
            1 => new BiomeCoordinateNode(),
            2 => new BiomeTemperatureNode(),
            3 => new BiomeMoistureNode(),
            4 => new BiomeSelectNode(),
            5 => new BiomeLayerNode(),
            6 => new BiomeBlendNode(),
            7 => new BiomeMathNode(),
            8 => new BiomeHeightNode(),
            9 => new BiomeCaveNode(),
            10 => new BiomeAltitudeNode(),
            11 => new BiomeSlopeNode(),
            12 => new BiomeErosionNode(),
            13 => new BiomeMaskNode(),
            14 => new BiomeRiverNode(),
            _ => new BiomeNoiseNode(),
        };

        node.EditorX = -_panX / _zoom + 200;
        node.EditorY = -_panY / _zoom + 200;
        _graph.AddNode(node);
        _selectedNode = node;
        CaptureUndo();
        Redraw();
        UpdateProperties();
    }

    void OnDeleteNode(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || _selectedNode is BiomeOutputNode) return;
        _graph.RemoveNode(_selectedNode);
        _selectedNode = null;
        CaptureUndo();
        Redraw();
        UpdateProperties();
    }

    async void OnSave(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save Biome Graph",
                DefaultExtension = "biomegraph",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Biome Graph") { Patterns = new[] { "*.biomegraph" } }
                }
            });
        if (file != null)
        {
            _currentGraphPath = file.Path.LocalPath;
            _graph.SaveToFile(_currentGraphPath);
        }
    }

    async void OnLoad(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Load Biome Graph",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Biome Graph") { Patterns = new[] { "*.biomegraph" } }
                }
            });
        if (files.Count > 0)
        {
            _currentGraphPath = files[0].Path.LocalPath;
            _graph = BiomeGraph.LoadFromFile(_currentGraphPath);
            _selectedNode = null;
            _undoStack.Clear();
            _redoStack.Clear();
            CaptureUndo();
            Redraw();
            UpdateProperties();
        }
    }

    void OnCompile(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentGraphPath))
        {
            try { _graph.SaveToFile(_currentGraphPath); }
            catch { }
        }

        var result = _graph.Compile();
        Core.Log.Info($"[BiomeGraph] Compiled: Height={result.HeightAmplitude}, Caves={result.EnableCaves}, Layers={result.Layers.Length}");
        for (int i = 0; i < result.Layers.Length; i++)
        {
            var ly = result.Layers[i];
            Core.Log.Info($"  Layer[{i}] \"{ly.BiomeName}\" Color=({ly.BaseColorR:F2},{ly.BaseColorG:F2},{ly.BaseColorB:F2}) Tex={!string.IsNullOrEmpty(ly.AlbedoPath)} Mode={ly.NoiseMode}");
        }

        var planets = new List<Core.Component.PlanetTerrain>();
        foreach (var root in Core.SceneService.Root)
            FindPlanets(root, planets);

        string? storedGraphPath = ToStoredProjectPath(_currentGraphPath);
        foreach (var pt in planets)
            pt.ApplyGraphResult(result, storedGraphPath);
    }

    static string? ToStoredProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            string abs = System.IO.Path.GetFullPath(path);
            var proj = Core.ProjectService.Current;
            if (proj == null) return abs.Replace('\\', '/');

            string root = System.IO.Path.GetFullPath(proj.RootPath);
            if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rel = System.IO.Path.GetRelativePath(root, abs).Replace('\\', '/');
                return rel;
            }

            return abs.Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    static void FindPlanets(Core.GameObject go, List<Core.Component.PlanetTerrain> list)
    {
        foreach (var b in go.Behaviors)
            if (b is Core.Component.PlanetTerrain pt) list.Add(pt);
        foreach (var c in go.Children) FindPlanets(c, list);
    }

    void OnNewDefault(object? sender, RoutedEventArgs e)
    {
        _graph = BiomeGraph.CreateDefault();
        _selectedNode = null;
        _undoStack.Clear();
        _redoStack.Clear();
        CaptureUndo();
        Redraw();
        UpdateProperties();
    }

    // ── Validate ──

    void OnValidate(object? sender, RoutedEventArgs e)
    {
        var warnings = _graph.Validate();
        if (warnings.Count == 0)
        {
            Core.Log.Info("[BiomeGraph] Validation passed - no issues found.");
            ShowValidationResult("Validation OK", "No issues found.");
        }
        else
        {
            foreach (var w in warnings)
                Core.Log.Info($"[BiomeGraph] Warning: {w}");
            ShowValidationResult("Validation Warnings", string.Join("\n", warnings));
        }
    }

    void ShowValidationResult(string title, string message)
    {
        if (_propsPanel == null) return;

        while (_propsPanel.Children.Count > 2)
            _propsPanel.Children.RemoveAt(2);

        _propsNodeName!.Text = title;

        var tb = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = title.Contains("OK")
                ? new SolidColorBrush(Color.Parse("#88FF88"))
                : new SolidColorBrush(Color.Parse("#FFAA44"))
        };
        _propsPanel.Children.Add(tb);
    }

    // ── Preview ──

    void OnPreview(object? sender, RoutedEventArgs e)
    {
        var result = _graph.Compile();
        int width = 256, height = 128;

        var biomes = Game_Engine.Core.Biome.BiomeDefinition.AllPresets;

        for (int i = 0; i < biomes.Length && i < result.Layers.Length; i++)
        {
            var ly = result.Layers[i];
            if (!string.IsNullOrEmpty(ly.BiomeName))
                biomes[i].Name = ly.BiomeName;
            biomes[i].BaseColorR = ly.BaseColorR;
            biomes[i].BaseColorG = ly.BaseColorG;
            biomes[i].BaseColorB = ly.BaseColorB;
        }

        var biomeMap = new Game_Engine.Core.Biome.BiomeMap(42, biomes,
            noiseScale: 2f,
            tempLatWeight: result.TemperatureLatWeight,
            tempNoiseWeight: result.TemperatureNoiseWeight,
            moistureNoiseScale: result.MoistureNoiseScale);

        var pixelData = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            float lat = (float)y / height * MathF.PI - MathF.PI / 2f;
            for (int x = 0; x < width; x++)
            {
                float lon = (float)x / width * 2f * MathF.PI - MathF.PI;

                float cy = MathF.Sin(lat);
                float cxz = MathF.Cos(lat);
                float cx = cxz * MathF.Cos(lon);
                float cz = cxz * MathF.Sin(lon);

                var dir = new System.Numerics.Vector3(cx, cy, cz);
                var blends = biomeMap.GetBiomes(dir);

                float r = 0, g = 0, b = 0;
                for (int bi = 0; bi < blends.Length; bi++)
                {
                    var bm = blends[bi];
                    r += bm.Biome.BaseColorR * bm.Weight;
                    g += bm.Biome.BaseColorG * bm.Weight;
                    b += bm.Biome.BaseColorB * bm.Weight;
                }

                int idx = (y * width + x) * 4;
                pixelData[idx + 0] = (byte)(Math.Clamp(r, 0, 1) * 255);
                pixelData[idx + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
                pixelData[idx + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
                pixelData[idx + 3] = 255;
            }
        }

        try
        {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888);

            using (var fb = bitmap.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, fb.Address, pixelData.Length);
            }

            if (_propsPanel == null) return;
            while (_propsPanel.Children.Count > 2)
                _propsPanel.Children.RemoveAt(2);

            _propsNodeName!.Text = "Biome Preview";

            var img = new Image
            {
                Source = bitmap,
                Width = 230,
                Height = 115,
                Stretch = Stretch.Uniform,
            };
            _propsPanel.Children.Add(img);

            _propsPanel.Children.Add(new TextBlock
            {
                Text = "Equirectangular biome distribution",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        catch (Exception ex)
        {
            Core.Log.Info($"[BiomeGraph] Preview error: {ex.Message}");
        }
    }

    // ── Vegetation Profiles ──

    void LoadVegetationProfiles()
    {
        _vegProfiles = VegetationProfileLibrary.LoadAll();
    }

    void SaveVegetationProfiles()
    {
        VegetationProfileLibrary.SaveAll(_vegProfiles);
    }

    string[] GetVegetationProfileIds()
    {
        if (_vegProfiles.Count == 0)
            LoadVegetationProfiles();
        if (!_vegProfiles.ContainsKey("Default"))
            _vegProfiles["Default"] = new VegetationProfile();
        return _vegProfiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    void ApplyVegetationProfileToLayer(BiomeLayerNode layer, string profileId)
    {
        if (!_vegProfiles.TryGetValue(profileId, out var p))
            return;
        layer.VegetationProfileId = p.Id;
        layer.VegetationDensity = p.VegetationDensity;
        layer.TreeDensity = p.TreeDensity;
        layer.VegetationPatchiness = p.VegetationPatchiness;
        layer.SeasonalGrowthMultiplier = p.SeasonalGrowthMultiplier;
    }

    void SaveLayerAsVegetationProfile(BiomeLayerNode layer, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            profileId = "Default";
        _vegProfiles.TryGetValue(profileId, out var existing);
        _vegProfiles[profileId] = new VegetationProfile
        {
            Id = profileId,
            VegetationDensity = Math.Clamp(layer.VegetationDensity, 0f, 2f),
            TreeDensity = Math.Clamp(layer.TreeDensity, 0f, 2f),
            VegetationPatchiness = Math.Clamp(layer.VegetationPatchiness, 0f, 1f),
            SeasonalGrowthMultiplier = Math.Clamp(layer.SeasonalGrowthMultiplier, 0f, 3f),
            GrassModelPath = existing?.GrassModelPath ?? "",
            TreeModelPath = existing?.TreeModelPath ?? "",
            GrassItems = existing?.GrassItems?.Select(i => new VegetationProfileItem
            {
                ModelPath = i.ModelPath,
                Weight = i.Weight,
                DensityMultiplier = i.DensityMultiplier,
                MinScale = i.MinScale,
                MaxScale = i.MaxScale,
            }).ToList() ?? new List<VegetationProfileItem>(),
            TreeItems = existing?.TreeItems?.Select(i => new VegetationProfileItem
            {
                ModelPath = i.ModelPath,
                Weight = i.Weight,
                DensityMultiplier = i.DensityMultiplier,
                MinScale = i.MinScale,
                MaxScale = i.MaxScale,
            }).ToList() ?? new List<VegetationProfileItem>(),
        };
        SaveVegetationProfiles();
    }

    string BuildUniqueVegetationProfileId(string baseId)
    {
        string root = string.IsNullOrWhiteSpace(baseId) ? "Profile" : baseId.Trim();
        if (!_vegProfiles.ContainsKey(root))
            return root;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{root}_{i}";
            if (!_vegProfiles.ContainsKey(candidate))
                return candidate;
        }
        return $"{root}_{Guid.NewGuid().ToString("N")[..4]}";
    }

    void AddVegetationProfileMenu(BiomeLayerNode layer)
    {
        var ids = GetVegetationProfileIds();
        if (ids.Length == 0) ids = new[] { "Default" };
        string current = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? "Default" : layer.VegetationProfileId;

        AddPropCombo("Veg Profile", ids, current, v =>
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            ApplyVegetationProfileToLayer(layer, v);
            CaptureUndo();
            UpdateProperties();
        });

        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var newBtn = new Button { Content = "New", Padding = new Thickness(6, 2), MinWidth = 40 };
        var saveBtn = new Button { Content = "Save", Padding = new Thickness(6, 2), MinWidth = 40 };
        var delBtn = new Button { Content = "Delete", Padding = new Thickness(6, 2), MinWidth = 48 };
        var reloadBtn = new Button { Content = "Reload", Padding = new Thickness(6, 2), MinWidth = 50 };

        newBtn.Click += (_, _) =>
        {
            string id = BuildUniqueVegetationProfileId(layer.BiomeName);
            layer.VegetationProfileId = id;
            SaveLayerAsVegetationProfile(layer, id);
            CaptureUndo();
            UpdateProperties();
        };

        saveBtn.Click += (_, _) =>
        {
            string id = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? BuildUniqueVegetationProfileId(layer.BiomeName) : layer.VegetationProfileId;
            layer.VegetationProfileId = id;
            SaveLayerAsVegetationProfile(layer, id);
            CaptureUndo();
            UpdateProperties();
        };

        delBtn.Click += (_, _) =>
        {
            string id = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? "Default" : layer.VegetationProfileId;
            if (string.Equals(id, "Default", StringComparison.OrdinalIgnoreCase))
                return;
            _vegProfiles.Remove(id);
            SaveVegetationProfiles();
            layer.VegetationProfileId = "Default";
            ApplyVegetationProfileToLayer(layer, "Default");
            CaptureUndo();
            UpdateProperties();
        };

        reloadBtn.Click += (_, _) =>
        {
            LoadVegetationProfiles();
            string id = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? "Default" : layer.VegetationProfileId;
            if (_vegProfiles.ContainsKey(id))
                ApplyVegetationProfileToLayer(layer, id);
            CaptureUndo();
            UpdateProperties();
        };

        row.Children.Add(newBtn);
        row.Children.Add(saveBtn);
        row.Children.Add(delBtn);
        row.Children.Add(reloadBtn);
        _propsPanel?.Children.Add(row);

        AddPropFloat("Veg Density", layer.VegetationDensity, v => layer.VegetationDensity = Math.Clamp(v, 0f, 2f));
        AddPropFloat("Tree Density", layer.TreeDensity, v => layer.TreeDensity = Math.Clamp(v, 0f, 2f));
        AddPropFloat("Patchiness", layer.VegetationPatchiness, v => layer.VegetationPatchiness = Math.Clamp(v, 0f, 1f));
        AddPropFloat("Season Growth", layer.SeasonalGrowthMultiplier, v => layer.SeasonalGrowthMultiplier = Math.Clamp(v, 0f, 3f));

        string activeId = string.IsNullOrWhiteSpace(layer.VegetationProfileId) ? "Default" : layer.VegetationProfileId;
        if (_vegProfiles.TryGetValue(activeId, out var profile))
        {
            AddPropSeparator("Grass Types");
            AddVegetationItemsEditor(profile, isGrass: true);
            AddPropSeparator("Tree Types");
            AddVegetationItemsEditor(profile, isGrass: false);
        }
    }

    void AddVegetationItemsEditor(VegetationProfile profile, bool isGrass)
    {
        var items = isGrass ? profile.GrassItems : profile.TreeItems;
        items ??= new List<VegetationProfileItem>();
        if (isGrass) profile.GrassItems = items; else profile.TreeItems = items;

        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            var it = items[idx];
            _propsPanel?.Children.Add(new TextBlock
            {
                Text = $"{(isGrass ? "Grass" : "Tree")} #{idx + 1}",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var modelFileTypes = isGrass
                ? new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Vegetation Assets")
                    {
                        Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.prefab", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.tif", "*.tiff", "*.psd" }
                    }
                }
                : new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("3D Models")
                    {
                        Patterns = new[] { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae", "*.3ds", "*.prefab" }
                    }
                };
            AddPropFilePicker("Model", it.ModelPath, v =>
            {
                it.ModelPath = v ?? "";
                SaveVegetationProfiles();
                CaptureUndo();
            }, pickerTitle: isGrass ? "Select Grass Asset" : "Select Tree Model", fileTypeFilter: modelFileTypes);
            AddPropFloat("Weight", it.Weight, v =>
            {
                it.Weight = Math.Clamp(v, 0f, 100f);
                SaveVegetationProfiles();
                CaptureUndo();
            });
            AddPropFloat("Density Mul", it.DensityMultiplier, v =>
            {
                it.DensityMultiplier = Math.Clamp(v, 0f, 3f);
                SaveVegetationProfiles();
                CaptureUndo();
            });
            AddPropFloat("Min Scale", it.MinScale, v =>
            {
                it.MinScale = Math.Clamp(v, 0.05f, 8f);
                SaveVegetationProfiles();
                CaptureUndo();
            });
            AddPropFloat("Max Scale", it.MaxScale, v =>
            {
                it.MaxScale = Math.Clamp(v, 0.05f, 8f);
                SaveVegetationProfiles();
                CaptureUndo();
            });

            var delRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            var delBtn = new Button { Content = "Remove Item", Padding = new Thickness(6, 2), MinWidth = 90 };
            delBtn.Click += (_, _) =>
            {
                if (idx >= 0 && idx < items.Count)
                {
                    items.RemoveAt(idx);
                    SaveVegetationProfiles();
                    CaptureUndo();
                    UpdateProperties();
                }
            };
            delRow.Children.Add(delBtn);
            _propsPanel?.Children.Add(delRow);
        }

        var addRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var addBtn = new Button { Content = isGrass ? "Add Grass Type" : "Add Tree Type", Padding = new Thickness(6, 2), MinWidth = 110 };
        addBtn.Click += (_, _) =>
        {
            items.Add(new VegetationProfileItem
            {
                Weight = 1f,
                DensityMultiplier = 1f,
                MinScale = 0.9f,
                MaxScale = 1.1f
            });
            SaveVegetationProfiles();
            CaptureUndo();
            UpdateProperties();
        };
        addRow.Children.Add(addBtn);
        _propsPanel?.Children.Add(addRow);
    }

    // ── Properties panel ──

    void UpdateProperties()
    {
        if (_propsPanel == null || _propsNodeName == null) return;

        while (_propsPanel.Children.Count > 2)
            _propsPanel.Children.RemoveAt(2);

        if (_selectedNode == null)
        {
            _propsNodeName.Text = "No node selected";
            return;
        }

        _propsNodeName.Text = $"{_selectedNode.Name} ({_selectedNode.Id})";

        switch (_selectedNode)
        {
            case BiomeNoiseNode n:
                AddPropFloat("Frequency", n.Frequency, v => { n.Frequency = v; n.Inputs[0].DefaultValue = new[] { v }; });
                AddPropInt("Octaves", n.Octaves, v => { n.Octaves = v; n.Inputs[1].DefaultValue = new[] { (float)v }; });
                AddPropInt("Seed", n.Seed, v => n.Seed = v);
                AddPropCombo("Mode", new[] { "FBM", "Ridged", "Billow" }, n.NoiseMode, v => n.NoiseMode = v);
                break;
            case BiomeTemperatureNode n:
                AddPropFloat("Lat Weight", n.LatitudeWeight, v => n.LatitudeWeight = v);
                AddPropFloat("Noise Weight", n.NoiseWeight, v => n.NoiseWeight = v);
                break;
            case BiomeMoistureNode n:
                AddPropFloat("Noise Scale", n.NoiseScale, v => n.NoiseScale = v);
                break;
            case BiomeLayerNode n:
                AddPropText("Biome Name", n.BiomeName, v => n.BiomeName = v);
                AddPropFilePicker("Albedo", n.AlbedoPath, v => n.AlbedoPath = v);
                AddPropFilePicker("Normal", n.NormalPath, v => n.NormalPath = v);
                AddPropFloat("Tiling", n.Tiling, v => n.Tiling = v);
                AddPropFloat("Roughness", n.Roughness, v => n.Roughness = v);
                AddPropFloat("Metallic", n.Metallic, v => n.Metallic = v);
                AddPropColor("Base Color", n.BaseColorR, n.BaseColorG, n.BaseColorB,
                    (r, g, b) => { n.BaseColorR = r; n.BaseColorG = g; n.BaseColorB = b; });
                AddPropSeparator("Under Surface");
                AddPropFilePicker("Under Tex", n.UnderTexturePath, v => n.UnderTexturePath = v);
                AddPropFilePicker("Under Norm", n.UnderNormalPath, v => n.UnderNormalPath = v);
                AddPropFloat("Under Tiling", n.UnderTiling, v => n.UnderTiling = v);
                AddPropSeparator("Terrain Shaping");
                AddPropCombo("Noise Mode", new[] { "FBM", "Ridged", "Billow" }, n.NoiseMode, v => n.NoiseMode = v);
                AddPropInt("Octaves", n.NoiseOctaves, v => n.NoiseOctaves = v);
                AddPropFloat("Erosion Str", n.ErosionStrength, v => n.ErosionStrength = v);
                AddPropFloat("Erosion Freq", n.ErosionFrequency, v => n.ErosionFrequency = v);
                AddPropSeparator("Water");
                AddPropCheckbox("Spawn Water", n.SpawnWater, v => n.SpawnWater = v);
                if (n.SpawnWater)
                {
                    AddPropColor("Shallow Color", n.WaterShallowR, n.WaterShallowG, n.WaterShallowB,
                        (r, g, b) => { n.WaterShallowR = r; n.WaterShallowG = g; n.WaterShallowB = b; });
                    AddPropColor("Deep Color", n.WaterDeepR, n.WaterDeepG, n.WaterDeepB,
                        (r, g, b) => { n.WaterDeepR = r; n.WaterDeepG = g; n.WaterDeepB = b; });
                }
                AddPropSeparator("Vegetation");
                AddVegetationProfileMenu(n);
                AddPropSeparator("Weather");
                AddPropText("Weather Profile", n.WeatherProfileId, v => n.WeatherProfileId = v);
                AddPropFloat("Rain Chance", n.RainChance, v => n.RainChance = Math.Clamp(v, 0f, 1f));
                AddPropFloat("Snow Chance", n.SnowChance, v => n.SnowChance = Math.Clamp(v, 0f, 1f));
                AddPropFloat("Storm Chance", n.StormChance, v => n.StormChance = Math.Clamp(v, 0f, 1f));
                AddPropFloat("Wind Bias", n.WindBias, v => n.WindBias = Math.Max(0f, v));
                AddPropFloat("Cloud Bias", n.CloudCoverageBias, v => n.CloudCoverageBias = Math.Max(0f, v));
                AddPropFloat("Fog Bias", n.FogDensityBias, v => n.FogDensityBias = Math.Max(0f, v));
                break;
            case BiomeMathNode n:
                var ops = Enum.GetNames<BiomeMathOp>();
                AddPropCombo("Operation", ops, n.Operation.ToString(), v =>
                {
                    if (Enum.TryParse<BiomeMathOp>(v, out var op)) n.Operation = op;
                });
                break;
            case BiomeHeightNode n:
                AddPropFloat("Base Height", n.BaseHeight, v => n.BaseHeight = v);
                AddPropFloat("Amplitude", n.Amplitude, v => n.Amplitude = v);
                break;
            case BiomeCaveNode n:
                AddPropFloat("Frequency", n.Frequency, v => n.Frequency = v);
                AddPropFloat("Threshold", n.Threshold, v => n.Threshold = v);
                break;
            case BiomeAltitudeNode n:
                AddPropFloat("Sea Level", n.SeaLevel, v => n.SeaLevel = v);
                AddPropFloat("Max Height", n.MaxHeight, v => n.MaxHeight = v);
                break;
            case BiomeSlopeNode n:
                AddPropFloat("Slope Scale", n.SlopeScale, v => n.SlopeScale = v);
                break;
            case BiomeErosionNode n:
                AddPropFloat("Strength", n.Strength, v => n.Strength = v);
                AddPropFloat("Frequency", n.Frequency, v => n.Frequency = v);
                AddPropInt("Octaves", n.Octaves, v => n.Octaves = v);
                break;
            case BiomeMaskNode n:
                var blendModes = Enum.GetNames<BiomeMaskBlendMode>();
                AddPropCombo("Blend Mode", blendModes, n.BlendMode.ToString(), v =>
                {
                    if (Enum.TryParse<BiomeMaskBlendMode>(v, out var bm)) n.BlendMode = bm;
                });
                break;
            case BiomeRiverNode n:
                AddPropFloat("Width", n.RiverWidth, v => n.RiverWidth = v);
                AddPropFloat("Depth", n.RiverDepth, v => n.RiverDepth = v);
                AddPropFloat("Frequency", n.Frequency, v => n.Frequency = v);
                AddPropFloat("Meander", n.Meander, v => n.Meander = v);
                AddPropText("Biomes", n.AllowedBiomes, v => n.AllowedBiomes = v);
                break;
        }

        _propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        var infoBlock = new TextBlock
        {
            Text = $"Inputs: {_selectedNode.Inputs.Count}  Outputs: {_selectedNode.Outputs.Count}",
            FontSize = 10, Foreground = Brushes.Gray
        };
        _propsPanel.Children.Add(infoBlock);
    }

    void AddPropSeparator(string label)
    {
        _propsPanel?.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        _propsPanel?.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#AAAACC")),
            Margin = new Thickness(0, 2, 0, 2),
        });
    }

    void AddPropCheckbox(string label, bool value, Action<bool> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var cb = new CheckBox { IsChecked = value };
        cb.Click += (_, _) =>
        {
            setter(cb.IsChecked == true);
            CaptureUndo();
            UpdateProperties();
        };
        sp.Children.Add(cb);
        _propsPanel?.Children.Add(sp);
    }

    void AddPropFloat(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString("G5", CultureInfo.InvariantCulture), Width = 80 };
        tb.LostFocus += (_, _) =>
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                setter(v);
                CaptureUndo();
            }
        };
        sp.Children.Add(tb);
        _propsPanel?.Children.Add(sp);
    }

    void AddPropInt(string label, int value, Action<int> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString(), Width = 80 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v))
            {
                setter(v);
                CaptureUndo();
            }
        };
        sp.Children.Add(tb);
        _propsPanel?.Children.Add(sp);
    }

    void AddPropText(string label, string value, Action<string> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value, Width = 130 };
        tb.LostFocus += (_, _) =>
        {
            setter(tb.Text ?? "");
            CaptureUndo();
        };
        sp.Children.Add(tb);
        _propsPanel?.Children.Add(sp);
    }

    void AddPropFilePicker(
        string label,
        string value,
        Action<string> setter,
        string? pickerTitle = null,
        Avalonia.Platform.Storage.FilePickerFileType[]? fileTypeFilter = null)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 60, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value, Width = 100 };
        tb.LostFocus += (_, _) =>
        {
            var stored = ToStoredProjectPath(tb.Text ?? "");
            tb.Text = stored;
            setter(stored);
            CaptureUndo();
        };
        sp.Children.Add(tb);

        var browseBtn = new Button { Content = "...", Padding = new Thickness(6, 2), MinWidth = 28 };
        browseBtn.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = string.IsNullOrWhiteSpace(pickerTitle) ? $"Select {label} Texture" : pickerTitle,
                    AllowMultiple = false,
                    FileTypeFilter = fileTypeFilter ?? new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Images")
                        {
                            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.tif", "*.tiff", "*.psd" }
                        }
                    }
                });
            if (files.Count > 0)
            {
                var stored = ToStoredProjectPath(files[0].Path.LocalPath);
                tb.Text = stored;
                setter(stored);
                CaptureUndo();
            }
        };
        sp.Children.Add(browseBtn);

        var clearBtn = new Button { Content = "X", Padding = new Thickness(4, 2), MinWidth = 24 };
        clearBtn.Click += (_, _) =>
        {
            tb.Text = "";
            setter("");
            CaptureUndo();
        };
        sp.Children.Add(clearBtn);

        _propsPanel?.Children.Add(sp);
    }

    void AddPropCombo(string label, string[] options, string current, Action<string> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var cb = new ComboBox { Width = 100 };
        int selIdx = 0;
        for (int i = 0; i < options.Length; i++)
        {
            cb.Items.Add(new ComboBoxItem { Content = options[i] });
            if (options[i] == current) selIdx = i;
        }
        cb.SelectedIndex = selIdx;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem item)
            {
                setter(item.Content?.ToString() ?? "");
                CaptureUndo();
            }
        };
        sp.Children.Add(cb);
        _propsPanel?.Children.Add(sp);
    }

    void AddPropColor(string label, float r, float g, float b, Action<float, float, float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        var preview = new Rectangle
        {
            Width = 24, Height = 20,
            Fill = new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255))),
            Stroke = Brushes.Gray, StrokeThickness = 1,
        };
        sp.Children.Add(preview);

        var rBox = new TextBox { Text = r.ToString("F2", CultureInfo.InvariantCulture), Width = 40 };
        var gBox = new TextBox { Text = g.ToString("F2", CultureInfo.InvariantCulture), Width = 40 };
        var bBox = new TextBox { Text = b.ToString("F2", CultureInfo.InvariantCulture), Width = 40 };

        float curR = r, curG = g, curB = b;
        void OnChange(object? s, RoutedEventArgs _)
        {
            if (float.TryParse(rBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float rv)) curR = rv;
            if (float.TryParse(gBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float gv)) curG = gv;
            if (float.TryParse(bBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bv)) curB = bv;
            setter(curR, curG, curB);
            preview.Fill = new SolidColorBrush(Color.FromRgb(
                (byte)(Math.Clamp(curR, 0, 1) * 255),
                (byte)(Math.Clamp(curG, 0, 1) * 255),
                (byte)(Math.Clamp(curB, 0, 1) * 255)));
            CaptureUndo();
        }

        rBox.LostFocus += OnChange;
        gBox.LostFocus += OnChange;
        bBox.LostFocus += OnChange;

        sp.Children.Add(rBox); sp.Children.Add(gBox); sp.Children.Add(bBox);
        _propsPanel?.Children.Add(sp);
    }
}
