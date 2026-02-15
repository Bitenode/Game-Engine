using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Game_Engine.Core;
using Game_Engine.Core.Rendering.ShaderGraph;

namespace Game_Engine.Views;

public partial class ShaderEditorPanel : UserControl
{
    private ShaderGraph _graph = new();
    private ShaderNode? _selectedNode;
    private ShaderNode? _draggingNode;
    private Point _dragOffset;
    private ShaderPort? _connectingFrom;

    // Visual constants
    private const float NodeWidth = 180;
    private const float NodeHeight = 120;
    private const float PortRadius = 6;
    private const float PortSpacing = 22;

    private static readonly IBrush NodeBg = new SolidColorBrush(Color.Parse("#2D2D44"));
    private static readonly IBrush NodeHeaderBg = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush OutputNodeBg = new SolidColorBrush(Color.Parse("#443344"));
    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#6688FF"));
    private static readonly IBrush PortInputColor = new SolidColorBrush(Color.Parse("#44AAFF"));
    private static readonly IBrush PortOutputColor = new SolidColorBrush(Color.Parse("#FFAA44"));
    private static readonly IBrush ConnectionLine = new SolidColorBrush(Color.Parse("#88AAFF"));
    private static readonly IBrush TextColor = Brushes.White;

    public ShaderEditorPanel()
    {
        InitializeComponent();

        CompileButton.Click += (_, _) => CompileGraph();
        AddNodeButton.Click += (_, _) => AddSelectedNodeType();

        GraphCanvas.PointerPressed += OnCanvasPointerPressed;
        GraphCanvas.PointerMoved += OnCanvasPointerMoved;
        GraphCanvas.PointerReleased += OnCanvasPointerReleased;

        // Initial render
        RenderGraph();
    }

    private void AddSelectedNodeType()
    {
        ShaderNode node = NodeTypeCombo.SelectedIndex switch
        {
            0 => _graph.AddNode<TextureSampleNode>(100, 100),
            1 => _graph.AddNode<ColorNode>(100, 200),
            2 => _graph.AddNode<FloatNode>(100, 300),
            3 => _graph.AddNode<MathNode>(100, 150),
            4 => _graph.AddNode<CoordinateNode>(100, 250),
            5 => _graph.AddNode<FresnelNode>(100, 350),
            6 => _graph.AddNode<NoiseNode>(100, 400),
            _ => _graph.AddNode<FloatNode>(100, 100)
        };

        _selectedNode = node;
        RenderGraph();
        UpdateProperties();
    }

    private void CompileGraph()
    {
        try
        {
            var (vertex, fragment) = _graph.Compile();
            StatusLabel.Text = $"Compiled! Vertex: {vertex.Length} chars, Fragment: {fragment.Length} chars";
            Log.Success("[ShaderEditor] Shader compiled successfully.");
            Log.Info($"[ShaderEditor] Fragment shader:\n{fragment}");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            Log.Error($"[ShaderEditor] Compile error: {ex.Message}");
        }
    }

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();

        // Draw connections first (behind nodes)
        foreach (var conn in _graph.Connections)
        {
            DrawConnection(conn.From, conn.To);
        }

        // Draw nodes
        foreach (var node in _graph.Nodes)
        {
            DrawNode(node);
        }
    }

    private void DrawNode(ShaderNode node)
    {
        bool isOutput = node is OutputNode;
        bool isSelected = node == _selectedNode;
        float headerH = 28;
        float totalH = headerH + Math.Max(node.Inputs.Count, node.Outputs.Count) * PortSpacing + 12;

        // Node background
        var bg = new Rectangle
        {
            Width = NodeWidth,
            Height = totalH,
            Fill = isOutput ? OutputNodeBg : NodeBg,
            RadiusX = 6, RadiusY = 6,
            Stroke = isSelected ? SelectedBorder : Brushes.Transparent,
            StrokeThickness = isSelected ? 2 : 0
        };
        Canvas.SetLeft(bg, node.EditorX);
        Canvas.SetTop(bg, node.EditorY);
        GraphCanvas.Children.Add(bg);

        // Header
        var header = new Rectangle
        {
            Width = NodeWidth,
            Height = headerH,
            Fill = NodeHeaderBg,
            RadiusX = 6, RadiusY = 6
        };
        Canvas.SetLeft(header, node.EditorX);
        Canvas.SetTop(header, node.EditorY);
        GraphCanvas.Children.Add(header);

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
        GraphCanvas.Children.Add(title);

        // Input ports
        for (int i = 0; i < node.Inputs.Count; i++)
        {
            float py = node.EditorY + headerH + 8 + i * PortSpacing;
            DrawPort(node.Inputs[i], node.EditorX, py, false);
        }

        // Output ports
        for (int i = 0; i < node.Outputs.Count; i++)
        {
            float py = node.EditorY + headerH + 8 + i * PortSpacing;
            DrawPort(node.Outputs[i], node.EditorX + NodeWidth, py, true);
        }
    }

    private void DrawPort(ShaderPort port, float x, float y, bool isOutput)
    {
        var circle = new Ellipse
        {
            Width = PortRadius * 2,
            Height = PortRadius * 2,
            Fill = isOutput ? PortOutputColor : PortInputColor,
            Stroke = port.Connection != null ? Brushes.White : Brushes.Transparent,
            StrokeThickness = port.Connection != null ? 1.5 : 0
        };
        Canvas.SetLeft(circle, x - PortRadius);
        Canvas.SetTop(circle, y - PortRadius);
        GraphCanvas.Children.Add(circle);

        var label = new TextBlock
        {
            Text = port.Name,
            Foreground = TextColor,
            FontSize = 10,
            Opacity = 0.8
        };

        if (isOutput)
        {
            Canvas.SetLeft(label, x - 60);
            Canvas.SetTop(label, y - 8);
        }
        else
        {
            Canvas.SetLeft(label, x + PortRadius + 4);
            Canvas.SetTop(label, y - 8);
        }
        GraphCanvas.Children.Add(label);
    }

    private void DrawConnection(ShaderPort from, ShaderPort to)
    {
        var fromPos = GetPortPosition(from);
        var toPos = GetPortPosition(to);

        if (fromPos == null || toPos == null) return;

        // Bezier curve for the connection
        float dx = (float)Math.Abs(toPos.Value.X - fromPos.Value.X) * 0.5f;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Stroke = ConnectionLine,
            StrokeThickness = 2,
            Data = Geometry.Parse($"M {fromPos.Value.X},{fromPos.Value.Y} C {fromPos.Value.X + dx},{fromPos.Value.Y} {toPos.Value.X - dx},{toPos.Value.Y} {toPos.Value.X},{toPos.Value.Y}")
        };
        GraphCanvas.Children.Add(path);
    }

    private Point? GetPortPosition(ShaderPort port)
    {
        var node = port.Owner;
        float headerH = 28;

        if (port.IsOutput)
        {
            int idx = node.Outputs.IndexOf(port);
            return new Point(node.EditorX + NodeWidth, node.EditorY + headerH + 8 + idx * PortSpacing);
        }
        else
        {
            int idx = node.Inputs.IndexOf(port);
            return new Point(node.EditorX, node.EditorY + headerH + 8 + idx * PortSpacing);
        }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(GraphCanvas);

        // Find which node was clicked
        _selectedNode = null;
        foreach (var node in _graph.Nodes.AsEnumerable().Reverse()) // Reverse for z-order
        {
            float headerH = 28;
            float totalH = headerH + Math.Max(node.Inputs.Count, node.Outputs.Count) * PortSpacing + 12;

            if (pos.X >= node.EditorX && pos.X <= node.EditorX + NodeWidth &&
                pos.Y >= node.EditorY && pos.Y <= node.EditorY + totalH)
            {
                _selectedNode = node;
                _draggingNode = node;
                _dragOffset = new Point(pos.X - node.EditorX, pos.Y - node.EditorY);
                break;
            }
        }

        RenderGraph();
        UpdateProperties();
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingNode == null) return;

        var pos = e.GetPosition(GraphCanvas);
        _draggingNode.EditorX = (float)(pos.X - _dragOffset.X);
        _draggingNode.EditorY = (float)(pos.Y - _dragOffset.Y);
        RenderGraph();
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingNode = null;
    }

    private void UpdateProperties()
    {
        PropertiesPanel.Children.Clear();

        if (_selectedNode == null)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Select a node to see its properties.",
                Foreground = Brushes.Gray,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"Type: {_selectedNode.GetType().Name}",
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        });

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"ID: {_selectedNode.Id}",
            Foreground = Brushes.Gray,
            FontSize = 10
        });

        // Show inputs with default values
        if (_selectedNode.Inputs.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Inputs:",
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = Brushes.White
            });

            foreach (var input in _selectedNode.Inputs)
            {
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                sp.Children.Add(new TextBlock
                {
                    Text = $"{input.Name} ({input.GLSLType})",
                    Width = 120,
                    Foreground = input.Connection != null ? Brushes.LightGreen : Brushes.LightGray,
                    FontSize = 11
                });

                if (input.Connection != null)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"<- {input.Connection.Owner.Name}.{input.Connection.Name}",
                        Foreground = Brushes.CornflowerBlue,
                        FontSize = 11
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
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = Brushes.White
            });

            foreach (var output in _selectedNode.Outputs)
            {
                PropertiesPanel.Children.Add(new TextBlock
                {
                    Text = $"{output.Name} ({output.GLSLType})",
                    Foreground = Brushes.LightGray,
                    FontSize = 11
                });
            }
        }

        // Type-specific properties
        if (_selectedNode is ColorNode colorNode)
        {
            AddColorProperty("Red", colorNode.R, v => { colorNode.R = v; RenderGraph(); });
            AddColorProperty("Green", colorNode.G, v => { colorNode.G = v; RenderGraph(); });
            AddColorProperty("Blue", colorNode.B, v => { colorNode.B = v; RenderGraph(); });
            AddColorProperty("Alpha", colorNode.A, v => { colorNode.A = v; RenderGraph(); });
        }
        else if (_selectedNode is FloatNode floatNode)
        {
            AddFloatProperty("Value", floatNode.Value, v => { floatNode.Value = v; RenderGraph(); });
        }
        else if (_selectedNode is TextureSampleNode texNode)
        {
            AddIntProperty("Texture Slot", texNode.TextureSlot, v => { texNode.TextureSlot = v; RenderGraph(); });
        }
    }

    private void AddColorProperty(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 50, Foreground = Brushes.White, FontSize = 11 });
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, Width = 120 };
        slider.ValueChanged += (_, _) => setter((float)slider.Value);
        sp.Children.Add(slider);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddFloatProperty(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = label, Width = 50, Foreground = Brushes.White, FontSize = 11 });
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
        sp.Children.Add(new TextBlock { Text = label, Width = 80, Foreground = Brushes.White, FontSize = 11 });
        var tb = new TextBox { Text = value.ToString(), Width = 60 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) setter(v);
        };
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }
}
