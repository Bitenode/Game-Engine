using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Game_Engine.Core;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.ShaderGraph;
using SN = System.Numerics;

namespace Game_Engine.Views;

public partial class ShaderEditorPanel : UserControl
{
    private ShaderGraph _graph = new();
    private ShaderNode? _selectedNode;
    private ShaderNode? _draggingNode;
    private Point _dragOffset;

    // Connection dragging state
    private ShaderPort? _connectingFrom;
    private Point _connectMousePos;

    // Pan & zoom — transform lives on _worldCanvas, events stay on GraphCanvas
    private readonly Canvas _worldCanvas = new();
    private double _panX, _panY;
    private double _zoom = 1.0;
    private bool _isPanning;
    private Point _panStartMouse;
    private double _panStartX, _panStartY;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 3.0;

    // Visual constants
    private const float NodeWidth = 180;
    private const float NodeHeight = 120;
    private const float PortRadius = 7;
    private const float PortHitRadius = 12;
    private const float PortSpacing = 22;
    private const float HeaderHeight = 28;

    // Preview
    private const int PreviewSize = 220;
    private WriteableBitmap? _previewBitmap;

    private static readonly IBrush NodeBg = new SolidColorBrush(Color.Parse("#2D2D44"));
    private static readonly IBrush NodeHeaderBg = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush OutputNodeBg = new SolidColorBrush(Color.Parse("#443344"));
    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#6688FF"));
    private static readonly IBrush PortInputColor = new SolidColorBrush(Color.Parse("#44AAFF"));
    private static readonly IBrush PortOutputColor = new SolidColorBrush(Color.Parse("#FFAA44"));
    private static readonly IBrush ConnectionBrush = new SolidColorBrush(Color.Parse("#88AAFF"));
    private static readonly IBrush TempConnectionBrush = new SolidColorBrush(Color.Parse("#FFDD44"));
    private static readonly IBrush ConnectedPortStroke = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush TextColor = Brushes.White;
    private static readonly IBrush GridDotBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));

    public ShaderEditorPanel()
    {
        InitializeComponent();

        // _worldCanvas holds all graph visuals and gets the pan/zoom transform.
        // GraphCanvas stays untransformed so GetPosition() returns screen coords.
        GraphCanvas.Children.Add(_worldCanvas);

        CompileButton.Click += (_, _) => CompileGraph();
        AddNodeButton.Click += (_, _) => AddSelectedNodeType();
        DeleteNodeButton.Click += (_, _) => DeleteSelectedNode();
        SaveButton.Click += async (_, _) => await SaveGraph();
        LoadButton.Click += async (_, _) => await LoadGraph();

        ShaderNameBox.LostFocus += (_, _) =>
        {
            _graph.Name = ShaderNameBox.Text ?? "Custom Shader";
        };

        GraphCanvas.PointerPressed += OnCanvasPointerPressed;
        GraphCanvas.PointerMoved += OnCanvasPointerMoved;
        GraphCanvas.PointerReleased += OnCanvasPointerReleased;
        GraphCanvas.PointerWheelChanged += OnCanvasPointerWheel;

        RenderGraph();
        RenderPreview();
    }

    // ── Coordinate Conversion ──

    /// <summary>Convert screen-space mouse position to graph world coordinates.</summary>
    private Point ScreenToWorld(Point screen)
    {
        return new Point(
            (screen.X - _panX) / _zoom,
            (screen.Y - _panY) / _zoom);
    }

    /// <summary>Update the RenderTransform on the child world canvas (not GraphCanvas).</summary>
    private void ApplyCanvasTransform()
    {
        _worldCanvas.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_panX, _panY)
            }
        };
    }

    // ── Toolbar Actions ──

    private void AddSelectedNodeType()
    {
        // Place new nodes in the center of the current visible area
        double canvasW = Math.Max(GraphCanvas.Bounds.Width, 400);
        double canvasH = Math.Max(GraphCanvas.Bounds.Height, 300);
        var center = ScreenToWorld(new Point(canvasW / 2, canvasH / 2));
        float baseX = (float)center.X - NodeWidth / 2 + (_graph.Nodes.Count % 5) * 25;
        float baseY = (float)center.Y - 60 + (_graph.Nodes.Count % 5) * 25;

        ShaderNode node = NodeTypeCombo.SelectedIndex switch
        {
            0 => _graph.AddNode<TextureSampleNode>(baseX, baseY),
            1 => _graph.AddNode<ColorNode>(baseX, baseY + 100),
            2 => _graph.AddNode<FloatNode>(baseX, baseY + 200),
            3 => _graph.AddNode<MathNode>(baseX, baseY + 50),
            4 => _graph.AddNode<CoordinateNode>(baseX, baseY + 150),
            5 => _graph.AddNode<FresnelNode>(baseX, baseY + 250),
            6 => _graph.AddNode<NoiseNode>(baseX, baseY + 300),
            _ => _graph.AddNode<FloatNode>(baseX, baseY)
        };

        _selectedNode = node;
        RenderGraph();
        UpdateProperties();
    }

    private void DeleteSelectedNode()
    {
        if (_selectedNode == null || _selectedNode is OutputNode)
        {
            StatusLabel.Text = "Cannot delete the Output node.";
            return;
        }

        _graph.RemoveNode(_selectedNode);
        _selectedNode = null;
        RenderGraph();
        UpdateProperties();
        RenderPreview();
    }

    private async void CompileGraph()
    {
        try
        {
            var (vertex, fragment) = _graph.Compile();
            RenderPreview();

            // Build the .shader file content
            string shaderName = ShaderNameBox.Text ?? _graph.Name ?? "CustomShader";
            string shaderContent =
$@"// ── {shaderName}.shader ──
// Auto-generated by Visual Shader Editor
// {DateTime.Now:yyyy-MM-dd HH:mm:ss}

Shader ""{shaderName}""
{{
    // ── Vertex Shader ──
    VERTEX
    {{
{IndentBlock(vertex, 8)}
    }}

    // ── Fragment Shader ──
    FRAGMENT
    {{
{IndentBlock(fragment, 8)}
    }}
}}
";

            // Ask user where to save
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusLabel.Text = $"Compiled OK ({fragment.Length} chars) — no window for save dialog";
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Compiled Shader",
                DefaultExtension = "shader",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Shader File") { Patterns = new[] { "*.shader" } }
                },
                SuggestedFileName = shaderName
            });

            if (file == null)
            {
                StatusLabel.Text = $"Compiled OK ({fragment.Length} chars) — save cancelled";
                return;
            }

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                StatusLabel.Text = "Compiled OK — could not resolve save path";
                return;
            }

            System.IO.File.WriteAllText(path, shaderContent);

            // Auto-save the .shadergraph alongside the .shader so the material
            // preview can load the node graph for accurate PBR blending
            try
            {
                string sgPath = System.IO.Path.ChangeExtension(path, ".shadergraph");
                _graph.Name = shaderName;
                _graph.SaveToFile(sgPath);
                Log.Info($"[ShaderEditor] Shader graph saved alongside: {System.IO.Path.GetFileName(sgPath)}");
            }
            catch (Exception sgEx)
            {
                Log.Warning($"[ShaderEditor] Could not save .shadergraph alongside: {sgEx.Message}");
            }

            StatusLabel.Text = $"Compiled & saved: {System.IO.Path.GetFileName(path)}  ({fragment.Length} chars)";
            Log.Success($"[ShaderEditor] Shader compiled and saved to: {path}");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            Log.Error($"[ShaderEditor] Compile error: {ex.Message}");
        }
    }

    private static string IndentBlock(string text, int spaces)
    {
        string indent = new string(' ', spaces);
        var lines = text.Split('\n');
        return string.Join('\n', lines.Select(l => indent + l));
    }

    private async System.Threading.Tasks.Task SaveGraph()
    {
        try
        {
            _graph.Name = ShaderNameBox.Text ?? "Custom Shader";

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Shader Graph",
                DefaultExtension = "shadergraph",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Shader Graph") { Patterns = new[] { "*.shadergraph" } }
                },
                SuggestedFileName = _graph.Name
            });

            if (file == null) return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            _graph.SaveToFile(path);
            StatusLabel.Text = $"Saved: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task LoadGraph()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load Shader Graph",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Shader Graph") { Patterns = new[] { "*.shadergraph" } }
                }
            });

            if (result.Count == 0) return;
            var path = result[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            _graph = ShaderGraph.LoadFromFile(path);
            ShaderNameBox.Text = _graph.Name;
            _selectedNode = null;
            _connectingFrom = null;

            RenderGraph();
            UpdateProperties();
            RenderPreview();
            StatusLabel.Text = $"Loaded: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Load error: {ex.Message}";
        }
    }

    // ── Port Hit Testing ──

    private (ShaderPort? port, ShaderNode? node) HitTestPort(Point pos)
    {
        // Check all ports on all nodes (reverse for z-order)
        foreach (var node in _graph.Nodes.AsEnumerable().Reverse())
        {
            // Check output ports
            for (int i = 0; i < node.Outputs.Count; i++)
            {
                var portPos = GetPortPosition(node.Outputs[i]);
                if (portPos == null) continue;
                double dist = Math.Sqrt(Math.Pow(pos.X - portPos.Value.X, 2) + Math.Pow(pos.Y - portPos.Value.Y, 2));
                if (dist <= PortHitRadius)
                    return (node.Outputs[i], node);
            }

            // Check input ports
            for (int i = 0; i < node.Inputs.Count; i++)
            {
                var portPos = GetPortPosition(node.Inputs[i]);
                if (portPos == null) continue;
                double dist = Math.Sqrt(Math.Pow(pos.X - portPos.Value.X, 2) + Math.Pow(pos.Y - portPos.Value.Y, 2));
                if (dist <= PortHitRadius)
                    return (node.Inputs[i], node);
            }
        }
        return (null, null);
    }

    private ShaderNode? HitTestNode(Point pos)
    {
        foreach (var node in _graph.Nodes.AsEnumerable().Reverse())
        {
            float totalH = HeaderHeight + Math.Max(node.Inputs.Count, node.Outputs.Count) * PortSpacing + 12;
            if (pos.X >= node.EditorX && pos.X <= node.EditorX + NodeWidth &&
                pos.Y >= node.EditorY && pos.Y <= node.EditorY + totalH)
            {
                return node;
            }
        }
        return null;
    }

    // ── Canvas Pointer Events ──

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var screenPos = e.GetPosition(GraphCanvas);
        var worldPos = ScreenToWorld(screenPos);
        var props = e.GetCurrentPoint(GraphCanvas).Properties;

        // Middle-mouse button starts canvas panning
        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartMouse = screenPos;
            _panStartX = _panX;
            _panStartY = _panY;
            e.Handled = true;
            return;
        }

        // Right-click on a port to disconnect
        if (props.IsRightButtonPressed)
        {
            var (port, _) = HitTestPort(worldPos);
            if (port != null)
            {
                _graph.Disconnect(port.IsOutput ? port : port);
                if (port.IsOutput)
                {
                    var toRemove = _graph.Connections.Where(c => c.From == port).ToList();
                    foreach (var conn in toRemove)
                    {
                        _graph.Disconnect(conn.To);
                    }
                }
                RenderGraph();
                UpdateProperties();
                RenderPreview();
                return;
            }

            // Right-click on empty space also pans (alternative to middle-click)
            _isPanning = true;
            _panStartMouse = screenPos;
            _panStartX = _panX;
            _panStartY = _panY;
            return;
        }

        // Check ports first (higher priority than node body)
        var (hitPort, hitNode) = HitTestPort(worldPos);

        if (hitPort != null)
        {
            if (hitPort.IsOutput)
            {
                _connectingFrom = hitPort;
                _connectMousePos = worldPos;
            }
            else
            {
                if (hitPort.Connection != null)
                {
                    var fromPort = hitPort.Connection;
                    _graph.Disconnect(hitPort);
                    _connectingFrom = fromPort;
                    _connectMousePos = worldPos;
                }
                else
                {
                    _connectingFrom = hitPort;
                    _connectMousePos = worldPos;
                }
            }

            _selectedNode = hitPort.Owner;
            RenderGraph();
            UpdateProperties();
            return;
        }

        // Check node body
        _connectingFrom = null;
        var clickedNode = HitTestNode(worldPos);

        if (clickedNode != null)
        {
            _selectedNode = clickedNode;
            _draggingNode = clickedNode;
            _dragOffset = new Point(worldPos.X - clickedNode.EditorX, worldPos.Y - clickedNode.EditorY);
        }
        else
        {
            // Left-click on empty space also pans
            _isPanning = true;
            _panStartMouse = screenPos;
            _panStartX = _panX;
            _panStartY = _panY;
            _selectedNode = null;
            _draggingNode = null;
        }

        RenderGraph();
        UpdateProperties();
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var screenPos = e.GetPosition(GraphCanvas);

        // Canvas panning
        if (_isPanning)
        {
            _panX = _panStartX + (screenPos.X - _panStartMouse.X);
            _panY = _panStartY + (screenPos.Y - _panStartMouse.Y);
            RenderGraph();
            return;
        }

        var worldPos = ScreenToWorld(screenPos);

        if (_connectingFrom != null)
        {
            _connectMousePos = worldPos;
            RenderGraph();
            DrawTempConnection(_connectingFrom, worldPos);
            return;
        }

        if (_draggingNode != null)
        {
            _draggingNode.EditorX = (float)(worldPos.X - _dragOffset.X);
            _draggingNode.EditorY = (float)(worldPos.Y - _dragOffset.Y);
            RenderGraph();
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            return;
        }

        if (_connectingFrom != null)
        {
            var worldPos = ScreenToWorld(e.GetPosition(GraphCanvas));
            var (targetPort, _) = HitTestPort(worldPos);

            if (targetPort != null && targetPort != _connectingFrom && targetPort.Owner != _connectingFrom.Owner)
            {
                ShaderPort? from = null, to = null;

                if (_connectingFrom.IsOutput && !targetPort.IsOutput)
                {
                    from = _connectingFrom;
                    to = targetPort;
                }
                else if (!_connectingFrom.IsOutput && targetPort.IsOutput)
                {
                    from = targetPort;
                    to = _connectingFrom;
                }

                if (from != null && to != null)
                {
                    _graph.Connect(from, to);
                    StatusLabel.Text = $"Connected {from.Owner.Name}.{from.Name} -> {to.Owner.Name}.{to.Name}";
                }
            }

            _connectingFrom = null;
            RenderGraph();
            UpdateProperties();
            RenderPreview();
            return;
        }

        _draggingNode = null;
    }

    private void OnCanvasPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var screenPos = e.GetPosition(GraphCanvas);

        // Zoom toward/away from cursor position
        double oldZoom = _zoom;
        double zoomDelta = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom = Math.Clamp(_zoom * zoomDelta, MinZoom, MaxZoom);

        // Adjust pan so the point under the cursor stays fixed
        _panX = screenPos.X - (screenPos.X - _panX) * (newZoom / oldZoom);
        _panY = screenPos.Y - (screenPos.Y - _panY) * (newZoom / oldZoom);
        _zoom = newZoom;

        RenderGraph();
        e.Handled = true;
    }

    // ── Graph Rendering ──

    private void RenderGraph()
    {
        _worldCanvas.Children.Clear();
        ApplyCanvasTransform();

        DrawGridBackground();

        // Draw connections first (behind nodes)
        foreach (var conn in _graph.Connections)
        {
            DrawConnection(conn.From, conn.To, ConnectionBrush, 2.5);
        }

        // Draw nodes
        foreach (var node in _graph.Nodes)
        {
            DrawNode(node);
        }
    }

    private void DrawGridBackground()
    {
        // Get visible area in world coordinates
        double canvasW = GraphCanvas.Bounds.Width;
        double canvasH = GraphCanvas.Bounds.Height;
        if (canvasW < 10 || canvasH < 10) return;

        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(canvasW, canvasH));

        const double gridStep = 30;
        double dotSize = 2 / _zoom; // Keep dot size consistent on screen

        double startX = Math.Floor(topLeft.X / gridStep) * gridStep;
        double startY = Math.Floor(topLeft.Y / gridStep) * gridStep;

        for (double x = startX; x <= bottomRight.X; x += gridStep)
        {
            for (double y = startY; y <= bottomRight.Y; y += gridStep)
            {
                var dot = new Ellipse { Width = dotSize, Height = dotSize, Fill = GridDotBrush };
                Canvas.SetLeft(dot, x - dotSize / 2);
                Canvas.SetTop(dot, y - dotSize / 2);
                _worldCanvas.Children.Add(dot);
            }
        }
    }

    private void DrawNode(ShaderNode node)
    {
        bool isOutput = node is OutputNode;
        bool isSelected = node == _selectedNode;
        float totalH = HeaderHeight + Math.Max(node.Inputs.Count, node.Outputs.Count) * PortSpacing + 12;

        // Drop shadow
        var shadow = new Rectangle
        {
            Width = NodeWidth + 4,
            Height = totalH + 4,
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            RadiusX = 8, RadiusY = 8
        };
        Canvas.SetLeft(shadow, node.EditorX - 2);
        Canvas.SetTop(shadow, node.EditorY + 2);
        _worldCanvas.Children.Add(shadow);

        // Node background
        var bg = new Rectangle
        {
            Width = NodeWidth,
            Height = totalH,
            Fill = isOutput ? OutputNodeBg : NodeBg,
            RadiusX = 6, RadiusY = 6,
            Stroke = isSelected ? SelectedBorder : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            StrokeThickness = isSelected ? 2 : 1
        };
        Canvas.SetLeft(bg, node.EditorX);
        Canvas.SetTop(bg, node.EditorY);
        _worldCanvas.Children.Add(bg);

        // Header
        var header = new Rectangle
        {
            Width = NodeWidth,
            Height = HeaderHeight,
            Fill = NodeHeaderBg,
            RadiusX = 6, RadiusY = 6
        };
        Canvas.SetLeft(header, node.EditorX);
        Canvas.SetTop(header, node.EditorY);
        _worldCanvas.Children.Add(header);

        // Clip header bottom corners (cover the rounded corners at the bottom of the header)
        var headerClip = new Rectangle
        {
            Width = NodeWidth,
            Height = 8,
            Fill = NodeHeaderBg
        };
        Canvas.SetLeft(headerClip, node.EditorX);
        Canvas.SetTop(headerClip, node.EditorY + HeaderHeight - 8);
        _worldCanvas.Children.Add(headerClip);

        // Title
        var title = new TextBlock
        {
            Text = node.Name,
            Foreground = TextColor,
            FontSize = 12,
            FontWeight = FontWeight.Bold
        };
        Canvas.SetLeft(title, node.EditorX + 10);
        Canvas.SetTop(title, node.EditorY + 6);
        _worldCanvas.Children.Add(title);

        // Input ports
        for (int i = 0; i < node.Inputs.Count; i++)
        {
            float py = node.EditorY + HeaderHeight + 8 + i * PortSpacing;
            DrawPort(node.Inputs[i], node.EditorX, py, false);
        }

        // Output ports
        for (int i = 0; i < node.Outputs.Count; i++)
        {
            float py = node.EditorY + HeaderHeight + 8 + i * PortSpacing;
            DrawPort(node.Outputs[i], node.EditorX + NodeWidth, py, true);
        }
    }

    private void DrawPort(ShaderPort port, float x, float y, bool isOutput)
    {
        bool isConnected = port.Connection != null;

        // Outer glow when connected
        if (isConnected)
        {
            var glow = new Ellipse
            {
                Width = PortRadius * 2 + 6,
                Height = PortRadius * 2 + 6,
                Fill = new SolidColorBrush(Color.FromArgb(30,
                    isOutput ? (byte)255 : (byte)68,
                    isOutput ? (byte)170 : (byte)170,
                    isOutput ? (byte)68 : (byte)255)),
            };
            Canvas.SetLeft(glow, x - PortRadius - 3);
            Canvas.SetTop(glow, y - PortRadius - 3);
            _worldCanvas.Children.Add(glow);
        }

        var circle = new Ellipse
        {
            Width = PortRadius * 2,
            Height = PortRadius * 2,
            Fill = isConnected
                ? (isOutput ? PortOutputColor : PortInputColor)
                : new SolidColorBrush(Color.FromArgb(120,
                    isOutput ? (byte)255 : (byte)68,
                    isOutput ? (byte)170 : (byte)170,
                    isOutput ? (byte)68 : (byte)255)),
            Stroke = isConnected ? ConnectedPortStroke : Brushes.Transparent,
            StrokeThickness = isConnected ? 1.5 : 0
        };
        Canvas.SetLeft(circle, x - PortRadius);
        Canvas.SetTop(circle, y - PortRadius);
        _worldCanvas.Children.Add(circle);

        var label = new TextBlock
        {
            Text = port.Name,
            Foreground = TextColor,
            FontSize = 10,
            Opacity = 0.85
        };

        if (isOutput)
        {
            Canvas.SetLeft(label, x - 65);
            Canvas.SetTop(label, y - 7);
        }
        else
        {
            Canvas.SetLeft(label, x + PortRadius + 5);
            Canvas.SetTop(label, y - 7);
        }
        _worldCanvas.Children.Add(label);
    }

    private void DrawConnection(ShaderPort from, ShaderPort to, IBrush brush, double thickness)
    {
        var fromPos = GetPortPosition(from);
        var toPos = GetPortPosition(to);

        if (fromPos == null || toPos == null) return;

        DrawBezierLine(fromPos.Value, toPos.Value, brush, thickness);
    }

    private void DrawTempConnection(ShaderPort fromPort, Point mousePos)
    {
        var fromPos = GetPortPosition(fromPort);
        if (fromPos == null) return;

        // If connecting from input, swap direction visually
        var start = fromPos.Value;
        var end = mousePos;

        if (!fromPort.IsOutput)
        {
            // Swap so the bezier curves the right way
            (start, end) = (end, start);
        }

        DrawBezierLine(start, end, TempConnectionBrush, 2, true);
    }

    private void DrawBezierLine(Point from, Point to, IBrush brush, double thickness, bool dashed = false)
    {
        float dx = (float)Math.Abs(to.X - from.X) * 0.5f;
        dx = Math.Max(dx, 50f);

        string pathData = $"M {F(from.X)},{F(from.Y)} C {F(from.X + dx)},{F(from.Y)} {F(to.X - dx)},{F(to.Y)} {F(to.X)},{F(to.Y)}";

        var path = new Avalonia.Controls.Shapes.Path
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Data = Geometry.Parse(pathData),
            Opacity = dashed ? 0.8 : 1.0
        };

        if (dashed)
        {
            path.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 4 };
        }

        _worldCanvas.Children.Add(path);
    }

    // Format double for geometry path strings (invariant culture)
    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private Point? GetPortPosition(ShaderPort port)
    {
        var node = port.Owner;
        if (port.IsOutput)
        {
            int idx = node.Outputs.IndexOf(port);
            if (idx < 0) return null;
            return new Point(node.EditorX + NodeWidth, node.EditorY + HeaderHeight + 8 + idx * PortSpacing);
        }
        else
        {
            int idx = node.Inputs.IndexOf(port);
            if (idx < 0) return null;
            return new Point(node.EditorX, node.EditorY + HeaderHeight + 8 + idx * PortSpacing);
        }
    }

    // ── Properties Panel ──

    private void UpdateProperties()
    {
        PropertiesPanel.Children.Clear();

        if (_selectedNode == null)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Select a node to see its properties.",
                Foreground = Brushes.Gray,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = _selectedNode.GetType().Name.Replace("Node", ""),
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = Brushes.White
        });

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"ID: {_selectedNode.Id}",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6)
        });

        // Show inputs
        if (_selectedNode.Inputs.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Inputs:",
                Margin = new Thickness(0, 4, 0, 2),
                Foreground = new SolidColorBrush(Color.Parse("#88AAFF")),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });

            foreach (var input in _selectedNode.Inputs)
            {
                var sp = new StackPanel { Spacing = 2 };
                var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                header.Children.Add(new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = input.Connection != null ? PortInputColor : new SolidColorBrush(Color.FromArgb(80, 68, 170, 255)),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                header.Children.Add(new TextBlock
                {
                    Text = $"{input.Name} ({input.GLSLType})",
                    Foreground = input.Connection != null ? Brushes.LightGreen : Brushes.LightGray,
                    FontSize = 11
                });
                sp.Children.Add(header);

                if (input.Connection != null)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"  <- {input.Connection.Owner.Name}.{input.Connection.Name}",
                        Foreground = Brushes.CornflowerBlue,
                        FontSize = 10,
                        Margin = new Thickness(12, 0, 0, 0)
                    });
                }

                PropertiesPanel.Children.Add(sp);
            }
        }

        // Show outputs
        if (_selectedNode.Outputs.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Outputs:",
                Margin = new Thickness(0, 6, 0, 2),
                Foreground = new SolidColorBrush(Color.Parse("#FFAA44")),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });

            foreach (var output in _selectedNode.Outputs)
            {
                var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                header.Children.Add(new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = PortOutputColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                header.Children.Add(new TextBlock
                {
                    Text = $"{output.Name} ({output.GLSLType})",
                    Foreground = Brushes.LightGray,
                    FontSize = 11
                });
                PropertiesPanel.Children.Add(header);
            }
        }

        // Separator
        PropertiesPanel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Margin = new Thickness(0, 8, 0, 4)
        });

        // Type-specific editable properties
        if (_selectedNode is ColorNode colorNode)
        {
            AddColorProperty("Red", colorNode.R, v => { colorNode.R = v; RenderGraph(); RenderPreview(); });
            AddColorProperty("Green", colorNode.G, v => { colorNode.G = v; RenderGraph(); RenderPreview(); });
            AddColorProperty("Blue", colorNode.B, v => { colorNode.B = v; RenderGraph(); RenderPreview(); });
            AddColorProperty("Alpha", colorNode.A, v => { colorNode.A = v; RenderGraph(); RenderPreview(); });
        }
        else if (_selectedNode is FloatNode floatNode)
        {
            AddFloatProperty("Value", floatNode.Value, v => { floatNode.Value = v; RenderGraph(); RenderPreview(); });
        }
        else if (_selectedNode is TextureSampleNode texNode)
        {
            AddIntProperty("Texture Slot", texNode.TextureSlot, v => { texNode.TextureSlot = v; RenderGraph(); RenderPreview(); });
        }
        else if (_selectedNode is MathNode mathNode)
        {
            AddEnumProperty("Operation", mathNode.Operation, v => { mathNode.Operation = v; RenderGraph(); RenderPreview(); });
        }
        else if (_selectedNode is CoordinateNode coordNode)
        {
            AddEnumProperty("Source", coordNode.Source, v => { coordNode.Source = v; RenderGraph(); RenderPreview(); });
        }
    }

    private void AddColorProperty(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 50, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, Width = 140 };
        slider.ValueChanged += (_, _) => setter((float)slider.Value);
        sp.Children.Add(slider);
        var valLabel = new TextBlock { Text = value.ToString("F2"), Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        slider.ValueChanged += (_, _) => valLabel.Text = slider.Value.ToString("F2");
        sp.Children.Add(valLabel);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddFloatProperty(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 50, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString("F2"), Width = 80 };
        tb.LostFocus += (_, _) =>
        {
            if (float.TryParse(tb.Text, out float v)) setter(v);
        };
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddIntProperty(string label, int value, Action<int> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 80, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString(), Width = 60 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) setter(v);
        };
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddEnumProperty<T>(string label, T currentValue, Action<T> setter) where T : struct, Enum
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 70, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        var combo = new ComboBox { Width = 130 };
        var values = Enum.GetValues<T>();
        int selectedIdx = 0;
        for (int i = 0; i < values.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = values[i].ToString() });
            if (EqualityComparer<T>.Default.Equals(values[i], currentValue)) selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < values.Length)
                setter(values[combo.SelectedIndex]);
        };
        sp.Children.Add(combo);
        PropertiesPanel.Children.Add(sp);
    }

    // ── Material Preview (CPU Sphere Raytrace) ──

    private void RenderPreview()
    {
        try
        {
            var pbrParams = ExtractPBRParameters();
            _previewBitmap = MaterialPreviewRenderer.Render(pbrParams, PreviewSize);
            PreviewImage.Source = _previewBitmap;
        }
        catch (Exception ex)
        {
            Log.Error($"[ShaderEditor] Preview error: {ex.Message}");
        }
    }

    private MaterialPreviewRenderer.PBRParams ExtractPBRParameters()
    {
        var output = _graph.Output;

        var albedo = EvalVec3(output.Inputs[0], new SN.Vector3(1f, 1f, 1f));
        float metallic = EvalFloat(output.Inputs[2], 0f);
        float roughness = EvalFloat(output.Inputs[3], 0.5f);
        var emission = EvalVec3(output.Inputs[4], SN.Vector3.Zero);
        float ao = EvalFloat(output.Inputs[6], 1f);

        // Detect noise in the albedo chain for per-pixel variation
        bool hasNoiseAlbedo = false;
        var albedoBase = albedo;
        float noiseScale = 10f;
        var albedoConn = output.Inputs[0].Connection;
        if (albedoConn?.Owner is MathNode albedoMath)
        {
            bool aIsNoise = albedoMath.Inputs[0].Connection?.Owner is NoiseNode;
            bool bIsNoise = albedoMath.Inputs[1].Connection?.Owner is NoiseNode;
            if (aIsNoise || bIsNoise)
            {
                hasNoiseAlbedo = true;
                var colorPort = aIsNoise ? albedoMath.Inputs[1] : albedoMath.Inputs[0];
                var noisePort = aIsNoise ? albedoMath.Inputs[0] : albedoMath.Inputs[1];
                albedoBase = EvalVec3(colorPort, new SN.Vector3(0.5f));
                var noiseNode = (NoiseNode)noisePort.Connection!.Owner;
                noiseScale = EvalFloat(noiseNode.Inputs[1], 10f);
            }
        }

        // Detect Fresnel in the emission chain — walk ALL nodes reachable from
        // the emission port to find any FresnelNode
        bool hasFresnel = false;
        var fresnelColor = SN.Vector3.Zero;
        float fresnelPower = 5f;

        // Direct scan: find FresnelNode by iterating all graph nodes connected to emission
        if (output.Inputs[4].Connection != null)
        {
            var fresnelNode = FindFresnelNodeInChain(output.Inputs[4]);
            if (fresnelNode != null)
            {
                hasFresnel = true;
                fresnelPower = EvalFloat(fresnelNode.Inputs[0], 5f);
                fresnelColor = FindFresnelColor(output.Inputs[4]);

                // Fallback: if FindFresnelColor returned white (couldn't determine),
                // use the evaluated emission (which has the color baked in) divided
                // by the Fresnel estimate of 0.5
                if (fresnelColor == SN.Vector3.One && emission.Length() > 0.01f)
                {
                    fresnelColor = emission * 2f;
                }
            }
        }

        return new MaterialPreviewRenderer.PBRParams
        {
            Albedo = albedo,
            Metallic = Math.Clamp(metallic, 0f, 1f),
            Roughness = Math.Clamp(roughness, 0f, 1f),
            Emission = hasFresnel ? SN.Vector3.Zero : emission,
            AO = Math.Clamp(ao, 0f, 1f),
            HasFresnel = hasFresnel,
            FresnelColor = fresnelColor,
            FresnelPower = fresnelPower,
            HasNoiseAlbedo = hasNoiseAlbedo,
            AlbedoBase = albedoBase,
            NoiseScale = noiseScale
        };
    }

    private FresnelNode? FindFresnelNodeInChain(ShaderPort input, int depth = 0)
    {
        if (depth > 10) return null;
        var conn = input.Connection;
        if (conn == null) return null;

        if (conn.Owner is FresnelNode fn) return fn;

        foreach (var inp in conn.Owner.Inputs)
        {
            var found = FindFresnelNodeInChain(inp, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Evaluate a port's effective float value by walking the node chain.</summary>
    private float EvalFloat(ShaderPort input, float fallback)
    {
        var conn = input.Connection;
        if (conn == null) return fallback;

        var node = conn.Owner;
        return node switch
        {
            FloatNode f => f.Value,
            ColorNode c => conn.Name switch
            {
                "R" => c.R, "G" => c.G, "B" => c.B, "A" => c.A,
                _ => (c.R + c.G + c.B) / 3f // RGB average
            },
            MathNode m => EvalMath(m),
            FresnelNode => 0.5f, // Fresnel is view-dependent; use mid-range estimate
            NoiseNode => 0.5f,   // Noise is spatial; use mid-range estimate
            _ => fallback
        };
    }

    /// <summary>Evaluate a port's effective vec3 value by walking the node chain.</summary>
    private SN.Vector3 EvalVec3(ShaderPort input, SN.Vector3 fallback)
    {
        var conn = input.Connection;
        if (conn == null) return fallback;

        var node = conn.Owner;
        return node switch
        {
            ColorNode c => conn.Name switch
            {
                "RGB" or "RGBA" => new SN.Vector3(c.R, c.G, c.B),
                "R" => new SN.Vector3(c.R),
                "G" => new SN.Vector3(c.G),
                "B" => new SN.Vector3(c.B),
                _ => new SN.Vector3(c.R, c.G, c.B)
            },
            FloatNode f => new SN.Vector3(f.Value),
            MathNode m => EvalMathVec3(m),
            FresnelNode => new SN.Vector3(0.5f),
            NoiseNode => new SN.Vector3(0.5f),
            _ => fallback
        };
    }

    /// <summary>Approximate a Math node's scalar result from its inputs.</summary>
    private float EvalMath(MathNode m)
    {
        float a = EvalFloat(m.Inputs[0], 0f);
        float b = EvalFloat(m.Inputs[1], 1f);
        return m.Operation switch
        {
            MathNode.MathOp.Add => a + b,
            MathNode.MathOp.Subtract => a - b,
            MathNode.MathOp.Multiply => a * b,
            MathNode.MathOp.Divide => b != 0 ? a / b : 0f,
            MathNode.MathOp.Power => MathF.Pow(Math.Max(a, 0f), b),
            MathNode.MathOp.Min => Math.Min(a, b),
            MathNode.MathOp.Max => Math.Max(a, b),
            MathNode.MathOp.Lerp => a + (b - a) * 0.5f,
            _ => a
        };
    }

    /// <summary>Evaluate a Math node per-component as vec3, preserving color channels.</summary>
    private SN.Vector3 EvalMathVec3(MathNode m)
    {
        var a = EvalVec3(m.Inputs[0], SN.Vector3.Zero);
        var b = EvalVec3(m.Inputs[1], SN.Vector3.One);
        return m.Operation switch
        {
            MathNode.MathOp.Add => a + b,
            MathNode.MathOp.Subtract => a - b,
            MathNode.MathOp.Multiply => a * b,
            MathNode.MathOp.Divide => new SN.Vector3(
                b.X != 0 ? a.X / b.X : 0f,
                b.Y != 0 ? a.Y / b.Y : 0f,
                b.Z != 0 ? a.Z / b.Z : 0f),
            MathNode.MathOp.Power => new SN.Vector3(
                MathF.Pow(Math.Max(a.X, 0f), b.X),
                MathF.Pow(Math.Max(a.Y, 0f), b.Y),
                MathF.Pow(Math.Max(a.Z, 0f), b.Z)),
            MathNode.MathOp.Min => SN.Vector3.Min(a, b),
            MathNode.MathOp.Max => SN.Vector3.Max(a, b),
            MathNode.MathOp.Lerp => SN.Vector3.Lerp(a, b, 0.5f),
            _ => a
        };
    }

    /// <summary>Check if there's a FresnelNode anywhere in the chain feeding an input.</summary>
    private bool HasNodeInChain(ShaderPort input, int depth = 0)
    {
        if (depth > 8) return false;
        var conn = input.Connection;
        if (conn == null) return false;
        if (conn.Owner is FresnelNode) return true;

        foreach (var inp in conn.Owner.Inputs)
        {
            if (HasNodeInChain(inp, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Find the color that gets multiplied with a Fresnel in the chain.</summary>
    private SN.Vector3 FindFresnelColor(ShaderPort input, int depth = 0)
    {
        if (depth > 8) return SN.Vector3.One;
        var conn = input.Connection;
        if (conn == null) return SN.Vector3.One;

        if (conn.Owner is MathNode m)
        {
            // Check if one input is Fresnel and the other is a color
            bool aIsFresnel = HasNodeInChain(m.Inputs[0], depth + 1) || m.Inputs[0].Connection?.Owner is FresnelNode;
            bool bIsFresnel = HasNodeInChain(m.Inputs[1], depth + 1) || m.Inputs[1].Connection?.Owner is FresnelNode;

            if (aIsFresnel && !bIsFresnel)
                return EvalVec3(m.Inputs[1], SN.Vector3.One);
            if (bIsFresnel && !aIsFresnel)
                return EvalVec3(m.Inputs[0], SN.Vector3.One);
        }

        foreach (var inp in conn.Owner.Inputs)
        {
            var c = FindFresnelColor(inp, depth + 1);
            if (c != SN.Vector3.One) return c;
        }
        return SN.Vector3.One;
    }

    /// <summary>Find the Fresnel power value in the chain.</summary>
    private float FindFresnelPower(ShaderPort input, int depth = 0)
    {
        if (depth > 8) return 5f;
        var conn = input.Connection;
        if (conn == null) return 5f;
        if (conn.Owner is FresnelNode fn)
            return EvalFloat(fn.Inputs[0], 5f);

        foreach (var inp in conn.Owner.Inputs)
        {
            float p = FindFresnelPower(inp, depth + 1);
            if (p != 5f) return p;
        }
        return 5f;
    }

}
