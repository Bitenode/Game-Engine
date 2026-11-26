#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Rendering.ShaderGraph;

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

    // Debounced live GPU preview compilation
    private readonly DispatcherTimer _previewDebounceTimer;
    private bool _syncingRenderStateUi;

    // Visual constants
    private const float NodeWidth = 185;
    private const float PortRadius = 7;
    private const float PortHitRadius = 12;
    private const float PortSpacing = 22;
    private const float HeaderHeight = 28;

    private static readonly IBrush NodeBg = new SolidColorBrush(Color.Parse("#2D2D44"));
    private static readonly IBrush NodeHeaderBg = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush OutputNodeBg = new SolidColorBrush(Color.Parse("#443344"));
    private static readonly IBrush PropertyNodeHeaderBg = new SolidColorBrush(Color.Parse("#2F5A48"));
    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#6688FF"));
    private static readonly IBrush PortInputColor = new SolidColorBrush(Color.Parse("#44AAFF"));
    private static readonly IBrush PortOutputColor = new SolidColorBrush(Color.Parse("#FFAA44"));
    private static readonly IBrush ConnectionBrush = new SolidColorBrush(Color.Parse("#88AAFF"));
    private static readonly IBrush TempConnectionBrush = new SolidColorBrush(Color.Parse("#FFDD44"));
    private static readonly IBrush ConnectedPortStroke = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush TextColor = Brushes.White;
    private static readonly IBrush GridDotBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IBrush StatusOkBrush = new SolidColorBrush(Color.Parse("#88DDAA"));
    private static readonly IBrush StatusErrBrush = new SolidColorBrush(Color.Parse("#FF7777"));
    private static readonly IBrush StatusNeutralBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));

    public ShaderEditorPanel()
    {
        InitializeComponent();

        // _worldCanvas holds all graph visuals and gets the pan/zoom transform.
        // GraphCanvas stays untransformed so GetPosition() returns screen coords.
        GraphCanvas.Children.Add(_worldCanvas);

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDebounceTimer.Tick += (_, _) =>
        {
            _previewDebounceTimer.Stop();
            CompileToGpuPreviewNow();
        };

        CompileButton.Click += (_, _) => CompileGraph();
        AddNodeButton.Click += (_, _) => AddSelectedNodeType();
        DeleteNodeButton.Click += (_, _) => DeleteSelectedNode();
        SaveButton.Click += async (_, _) => await SaveGraph();
        LoadButton.Click += async (_, _) => await LoadGraph();

        ShaderNameBox.LostFocus += (_, _) =>
        {
            _graph.Name = ShaderNameBox.Text ?? "Custom Shader";
            ScheduleGpuPreview();
        };

        // Preview toolbar controls
        PreviewMeshCombo.SelectionChanged += (_, _) =>
        {
            GpuPreview.MeshType = PreviewMeshCombo.SelectedIndex switch
            {
                1 => ShaderPreviewMesh.Cube,
                2 => ShaderPreviewMesh.Plane,
                _ => ShaderPreviewMesh.Sphere
            };
        };

        AutoSpinCheck.IsCheckedChanged += (_, _) =>
        {
            GpuPreview.AutoSpin = AutoSpinCheck.IsChecked == true;
        };

        // Master RenderState toolbar dropdowns
        QueueCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingRenderStateUi) return;
            _graph.Output.Queue = QueueCombo.SelectedIndex switch
            {
                1 => ShaderRenderQueue.AlphaTest,
                2 => ShaderRenderQueue.Transparent,
                _ => ShaderRenderQueue.Opaque
            };
            if (_selectedNode is OutputNode) UpdateProperties();
            ScheduleGpuPreview();
        };

        CullCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingRenderStateUi) return;
            _graph.Output.Cull = CullCombo.SelectedIndex switch
            {
                1 => ShaderCullMode.Front,
                2 => ShaderCullMode.Off,
                _ => ShaderCullMode.Back
            };
            if (_selectedNode is OutputNode) UpdateProperties();
            ScheduleGpuPreview();
        };

        LightingCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingRenderStateUi) return;
            _graph.Output.Lighting = LightingCombo.SelectedIndex switch
            {
                1 => ShaderLightingModel.Unlit,
                _ => ShaderLightingModel.PBR
            };
            if (_selectedNode is OutputNode) UpdateProperties();
            ScheduleGpuPreview();
        };

        GpuPreview.CompilationResultChanged += OnGpuCompilationResult;

        GraphCanvas.PointerPressed += OnCanvasPointerPressed;
        GraphCanvas.PointerMoved += OnCanvasPointerMoved;
        GraphCanvas.PointerReleased += OnCanvasPointerReleased;
        GraphCanvas.PointerWheelChanged += OnCanvasPointerWheel;

        SyncRenderStateToolbar();
        RenderGraph();
        ScheduleGpuPreview();
    }

    private void SyncRenderStateToolbar()
    {
        _syncingRenderStateUi = true;
        try
        {
            QueueCombo.SelectedIndex = _graph.Output.Queue switch
            {
                ShaderRenderQueue.AlphaTest => 1,
                ShaderRenderQueue.Transparent => 2,
                _ => 0
            };
            CullCombo.SelectedIndex = _graph.Output.Cull switch
            {
                ShaderCullMode.Front => 1,
                ShaderCullMode.Off => 2,
                _ => 0
            };
            LightingCombo.SelectedIndex = _graph.Output.Lighting switch
            {
                ShaderLightingModel.Unlit => 1,
                _ => 0
            };
        }
        finally
        {
            _syncingRenderStateUi = false;
        }
    }

    private void OnGpuCompilationResult(bool ok, string message)
    {
        if (ok)
        {
            PreviewStatusText.Text = "Live GPU preview — drag to orbit, scroll to zoom";
            PreviewStatusText.Foreground = StatusOkBrush;
            ToolTip.SetTip(PreviewStatusText, null);
        }
        else
        {
            PreviewStatusText.Text = $"GLSL Error: {message}";
            PreviewStatusText.Foreground = StatusErrBrush;
            ToolTip.SetTip(PreviewStatusText, message);
            StatusLabel.Text = $"GLSL Error: {message}";
            StatusLabel.Foreground = StatusErrBrush;
            ToolTip.SetTip(StatusLabel, message);
        }
    }

    private void ScheduleGpuPreview()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private void CompileToGpuPreviewNow()
    {
        _graph.Name = string.IsNullOrWhiteSpace(ShaderNameBox.Text) ? "Custom Shader" : ShaderNameBox.Text!;
        var result = ShaderGraphCompiler.Compile(_graph);
        if (!result.Success)
        {
            string err = result.ErrorMessage ?? "Shader graph compile failed.";
            PreviewStatusText.Text = err;
            PreviewStatusText.Foreground = StatusErrBrush;
            ToolTip.SetTip(PreviewStatusText, err);
            StatusLabel.Text = err;
            StatusLabel.Foreground = StatusErrBrush;
            return;
        }

        var previewMat = BuildPreviewMaterial();
        GpuPreview.SetShaderSource(result.ShaderSource, _graph.Name, previewMat);
    }

    private Material BuildPreviewMaterial()
    {
        var mat = new Material { Name = _graph.Name };

        foreach (var node in _graph.Nodes)
        {
            if (node is TextureSampleNode tn && !string.IsNullOrWhiteSpace(tn.TexturePath))
            {
                var tex = ShaderPreviewControl.TryLoadPreviewTexture(tn.TexturePath);
                if (tex == null) continue;

                if (!string.IsNullOrWhiteSpace(tn.PropertyName))
                {
                    string propName = ShaderNode.SanitizeIdentifier(tn.PropertyName, "_MainTex");
                    mat.Properties.SetTexture(propName, tn.TexturePath, tex);
                }
                else
                {
                    string usage = tn.TextureSlot switch
                    {
                        1 => "Normal",
                        2 => "Specular",
                        _ => "Albedo"
                    };
                    mat.Textures.Add(new RuntimeTexSlot { Texture = tex, Usage = usage, SourcePath = tn.TexturePath });
                }
            }
            else if (node is PropertyNode pn && pn.Kind == PropertyNode.PropertyKind.Texture2D && !string.IsNullOrWhiteSpace(pn.TexturePath))
            {
                var tex = ShaderPreviewControl.TryLoadPreviewTexture(pn.TexturePath);
                if (tex != null)
                {
                    string propName = ShaderNode.SanitizeIdentifier(pn.PropertyName, "_CustomProp");
                    mat.Properties.SetTexture(propName, pn.TexturePath, tex);
                }
            }
        }

        return mat;
    }

    // ── Coordinate Conversion ──

    private Point ScreenToWorld(Point screen)
    {
        return new Point(
            (screen.X - _panX) / _zoom,
            (screen.Y - _panY) / _zoom);
    }

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
        double canvasW = Math.Max(GraphCanvas.Bounds.Width, 400);
        double canvasH = Math.Max(GraphCanvas.Bounds.Height, 300);
        var center = ScreenToWorld(new Point(canvasW / 2, canvasH / 2));
        float baseX = (float)center.X - NodeWidth / 2 + (_graph.Nodes.Count % 5) * 24;
        float baseY = (float)center.Y - 60 + (_graph.Nodes.Count % 5) * 24;

        ShaderNode node = NodeTypeCombo.SelectedIndex switch
        {
            0 => _graph.AddNode<TextureSampleNode>(baseX, baseY),
            1 => _graph.AddNode<ColorNode>(baseX, baseY),
            2 => _graph.AddNode<FloatNode>(baseX, baseY),
            3 => _graph.AddNode<MathNode>(baseX, baseY),
            4 => _graph.AddNode<CoordinateNode>(baseX, baseY),
            5 => _graph.AddNode<FresnelNode>(baseX, baseY),
            6 => _graph.AddNode<NoiseNode>(baseX, baseY),
            7 => _graph.AddNode<PropertyNode>(baseX, baseY),
            8 => _graph.AddNode<SplitNode>(baseX, baseY),
            9 => _graph.AddNode<CombineNode>(baseX, baseY),
            10 => _graph.AddNode<UVTransformNode>(baseX, baseY),
            11 => _graph.AddNode<TimeNode>(baseX, baseY),
            12 => _graph.AddNode<BlendNode>(baseX, baseY),
            _ => _graph.AddNode<FloatNode>(baseX, baseY)
        };

        _selectedNode = node;
        RenderGraph();
        UpdateProperties();
        ScheduleGpuPreview();
    }

    private void DeleteSelectedNode()
    {
        if (_selectedNode == null || _selectedNode is OutputNode)
        {
            StatusLabel.Text = "Cannot delete the Output node.";
            StatusLabel.Foreground = StatusNeutralBrush;
            return;
        }

        _graph.RemoveNode(_selectedNode);
        _selectedNode = null;
        RenderGraph();
        UpdateProperties();
        ScheduleGpuPreview();
    }

    private async void CompileGraph()
    {
        try
        {
            string shaderName = string.IsNullOrWhiteSpace(ShaderNameBox.Text) ? _graph.Name : ShaderNameBox.Text!;
            _graph.Name = shaderName;

            var compileResult = ShaderGraphCompiler.Compile(_graph);
            if (!compileResult.Success)
            {
                string err = compileResult.ErrorMessage ?? "Compilation failed.";
                StatusLabel.Text = $"Compile error: {err}";
                StatusLabel.Foreground = StatusErrBrush;
                Log.Error($"[ShaderEditor] Compile error: {err}");
                return;
            }

            string shaderContent = compileResult.ShaderSource;
            GpuPreview.SetShaderSource(shaderContent, shaderName, BuildPreviewMaterial());

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                StatusLabel.Text = $"Compiled OK ({shaderContent.Length} chars) — no window for save dialog";
                StatusLabel.Foreground = StatusOkBrush;
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Compiled Surface Shader",
                DefaultExtension = "shader",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Shader File") { Patterns = new[] { "*.shader" } }
                },
                SuggestedFileName = shaderName
            });

            if (file == null)
            {
                StatusLabel.Text = $"Compiled OK ({shaderContent.Length} chars) — save cancelled";
                StatusLabel.Foreground = StatusOkBrush;
                return;
            }

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                StatusLabel.Text = "Compiled OK — could not resolve save path";
                StatusLabel.Foreground = StatusNeutralBrush;
                return;
            }

            System.IO.File.WriteAllText(path, shaderContent);
            ShaderFileParser.InvalidateSchemaCache(path);
            CustomShaderCache.Invalidate(path);
            SceneService.RequestFrame();

            // Auto-save the .shadergraph alongside the .shader
            try
            {
                string sgPath = System.IO.Path.ChangeExtension(path, ".shadergraph");
                _graph.SaveToFile(sgPath);
                Log.Info($"[ShaderEditor] Shader graph saved alongside: {System.IO.Path.GetFileName(sgPath)}");
            }
            catch (Exception sgEx)
            {
                Log.Warning($"[ShaderEditor] Could not save .shadergraph alongside: {sgEx.Message}");
            }

            StatusLabel.Text = $"Compiled & saved: {System.IO.Path.GetFileName(path)}";
            StatusLabel.Foreground = StatusOkBrush;
            Log.Success($"[ShaderEditor] Surface shader compiled and saved to: {path}");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            StatusLabel.Foreground = StatusErrBrush;
            Log.Error($"[ShaderEditor] Compile error: {ex.Message}");
        }
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
            StatusLabel.Foreground = StatusOkBrush;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save error: {ex.Message}";
            StatusLabel.Foreground = StatusErrBrush;
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

            SyncRenderStateToolbar();
            RenderGraph();
            UpdateProperties();
            ScheduleGpuPreview();
            StatusLabel.Text = $"Loaded: {System.IO.Path.GetFileName(path)}";
            StatusLabel.Foreground = StatusOkBrush;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Load error: {ex.Message}";
            StatusLabel.Foreground = StatusErrBrush;
        }
    }

    // ── Port Hit Testing ──

    private (ShaderPort? port, ShaderNode? node) HitTestPort(Point pos)
    {
        foreach (var node in _graph.Nodes.AsEnumerable().Reverse())
        {
            for (int i = 0; i < node.Outputs.Count; i++)
            {
                var portPos = GetPortPosition(node.Outputs[i]);
                if (portPos == null) continue;
                double dist = Math.Sqrt(Math.Pow(pos.X - portPos.Value.X, 2) + Math.Pow(pos.Y - portPos.Value.Y, 2));
                if (dist <= PortHitRadius)
                    return (node.Outputs[i], node);
            }

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
                _graph.Disconnect(port);
                RenderGraph();
                UpdateProperties();
                ScheduleGpuPreview();
                return;
            }

            _isPanning = true;
            _panStartMouse = screenPos;
            _panStartX = _panX;
            _panStartY = _panY;
            return;
        }

        var (hitPort, _) = HitTestPort(worldPos);
        if (hitPort != null)
        {
            if (hitPort.IsOutput)
            {
                // Fan-out: starting a new wire from an output keeps all existing outgoing wires intact!
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
                    StatusLabel.Foreground = StatusNeutralBrush;
                }
            }

            _connectingFrom = null;
            RenderGraph();
            UpdateProperties();
            ScheduleGpuPreview();
            return;
        }

        _draggingNode = null;
    }

    private void OnCanvasPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var screenPos = e.GetPosition(GraphCanvas);

        double oldZoom = _zoom;
        double zoomDelta = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom = Math.Clamp(_zoom * zoomDelta, MinZoom, MaxZoom);

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

        foreach (var conn in _graph.Connections)
        {
            DrawConnection(conn.From, conn.To, ConnectionBrush, 2.5);
        }

        foreach (var node in _graph.Nodes)
        {
            DrawNode(node);
        }
    }

    private void DrawGridBackground()
    {
        double canvasW = GraphCanvas.Bounds.Width;
        double canvasH = GraphCanvas.Bounds.Height;
        if (canvasW < 10 || canvasH < 10) return;

        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(canvasW, canvasH));

        const double gridStep = 30;
        double dotSize = 2 / _zoom;

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

        var headerBrush = node is PropertyNode ? PropertyNodeHeaderBg : NodeHeaderBg;
        var header = new Rectangle
        {
            Width = NodeWidth,
            Height = HeaderHeight,
            Fill = headerBrush,
            RadiusX = 6, RadiusY = 6
        };
        Canvas.SetLeft(header, node.EditorX);
        Canvas.SetTop(header, node.EditorY);
        _worldCanvas.Children.Add(header);

        var headerClip = new Rectangle
        {
            Width = NodeWidth,
            Height = 8,
            Fill = headerBrush
        };
        Canvas.SetLeft(headerClip, node.EditorX);
        Canvas.SetTop(headerClip, node.EditorY + HeaderHeight - 8);
        _worldCanvas.Children.Add(headerClip);

        string displayTitle = node switch
        {
            PropertyNode pn => string.IsNullOrWhiteSpace(pn.DisplayName) ? pn.PropertyName : pn.DisplayName,
            MathNode mn => $"Math ({mn.Operation})",
            NoiseNode nn => $"Noise ({nn.Type})",
            BlendNode bn => $"Blend ({bn.Mode})",
            _ => node.Name
        };

        var title = new TextBlock
        {
            Text = displayTitle,
            Foreground = TextColor,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            MaxWidth = NodeWidth - 16,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(title, node.EditorX + 8);
        Canvas.SetTop(title, node.EditorY + 6);
        _worldCanvas.Children.Add(title);

        for (int i = 0; i < node.Inputs.Count; i++)
        {
            float py = node.EditorY + HeaderHeight + 8 + i * PortSpacing;
            DrawPort(node.Inputs[i], node.EditorX, py, false);
        }

        for (int i = 0; i < node.Outputs.Count; i++)
        {
            float py = node.EditorY + HeaderHeight + 8 + i * PortSpacing;
            DrawPort(node.Outputs[i], node.EditorX + NodeWidth, py, true);
        }
    }

    private void DrawPort(ShaderPort port, float x, float y, bool isOutput)
    {
        bool isConnected = _graph.IsPortConnected(port);

        if (isConnected)
        {
            var glow = new Ellipse
            {
                Width = PortRadius * 2 + 6,
                Height = PortRadius * 2 + 6,
                Fill = new SolidColorBrush(Color.FromArgb(35,
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
            Canvas.SetLeft(label, x - 62);
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

        var start = fromPos.Value;
        var end = mousePos;
        if (!fromPort.IsOutput)
            (start, end) = (end, start);

        DrawBezierLine(start, end, TempConnectionBrush, 2, true);
    }

    private void DrawBezierLine(Point from, Point to, IBrush brush, double thickness, bool dashed = false)
    {
        float dx = Math.Max((float)Math.Abs(to.X - from.X) * 0.5f, 50f);
        string pathData = $"M {F(from.X)},{F(from.Y)} C {F(from.X + dx)},{F(from.Y)} {F(to.X - dx)},{F(to.Y)} {F(to.X)},{F(to.Y)}";

        var path = new Avalonia.Controls.Shapes.Path
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Data = Geometry.Parse(pathData),
            Opacity = dashed ? 0.8 : 1.0
        };

        if (dashed)
            path.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 4 };

        _worldCanvas.Children.Add(path);
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

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
                Text = "Select a node to edit its properties.",
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
            Margin = new Thickness(0, 0, 0, 4)
        });

        // Show inputs
        if (_selectedNode.Inputs.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Inputs:",
                Margin = new Thickness(0, 2, 0, 2),
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
                Margin = new Thickness(0, 4, 0, 2),
                Foreground = new SolidColorBrush(Color.Parse("#FFAA44")),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            });

            foreach (var output in _selectedNode.Outputs)
            {
                int fanOut = _graph.Connections.Count(c => c.From == output);
                var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                header.Children.Add(new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = PortOutputColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                header.Children.Add(new TextBlock
                {
                    Text = fanOut > 0 ? $"{output.Name} ({output.GLSLType}) [{fanOut}]" : $"{output.Name} ({output.GLSLType})",
                    Foreground = Brushes.LightGray,
                    FontSize = 11
                });
                PropertiesPanel.Children.Add(header);
            }
        }

        PropertiesPanel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Margin = new Thickness(0, 6, 0, 4)
        });

        // Type-specific editable properties
        switch (_selectedNode)
        {
            case OutputNode outNode:
                AddEnumProperty("Queue", outNode.Queue, v => { outNode.Queue = v; SyncRenderStateToolbar(); ScheduleGpuPreview(); });
                AddEnumProperty("Cull", outNode.Cull, v => { outNode.Cull = v; SyncRenderStateToolbar(); ScheduleGpuPreview(); });
                AddEnumProperty("Lighting", outNode.Lighting, v => { outNode.Lighting = v; SyncRenderStateToolbar(); ScheduleGpuPreview(); });
                AddBoolProperty("ZWrite", outNode.ZWrite, v => { outNode.ZWrite = v; ScheduleGpuPreview(); });
                AddSliderProperty("Alpha Clip", outNode.AlphaClipThreshold, 0f, 1f, v => { outNode.AlphaClipThreshold = v; ScheduleGpuPreview(); });
                break;

            case ColorNode colorNode:
                AddSliderProperty("Red", colorNode.R, 0f, 1f, v => { colorNode.R = v; RenderGraph(); ScheduleGpuPreview(); });
                AddSliderProperty("Green", colorNode.G, 0f, 1f, v => { colorNode.G = v; RenderGraph(); ScheduleGpuPreview(); });
                AddSliderProperty("Blue", colorNode.B, 0f, 1f, v => { colorNode.B = v; RenderGraph(); ScheduleGpuPreview(); });
                AddSliderProperty("Alpha", colorNode.A, 0f, 1f, v => { colorNode.A = v; RenderGraph(); ScheduleGpuPreview(); });
                break;

            case FloatNode floatNode:
                AddFloatProperty("Value", floatNode.Value, v => { floatNode.Value = v; RenderGraph(); ScheduleGpuPreview(); });
                break;

            case TextureSampleNode texNode:
                AddIntProperty("Slot (0-2)", texNode.TextureSlot, v => { texNode.TextureSlot = Math.Clamp(v, 0, 2); ScheduleGpuPreview(); });
                AddStringProperty("Property", texNode.PropertyName, v => { texNode.PropertyName = v; ScheduleGpuPreview(); });
                AddStringProperty("Display", texNode.DisplayName, v => { texNode.DisplayName = v; ScheduleGpuPreview(); });
                AddStringProperty("Tex Path", texNode.TexturePath, v => { texNode.TexturePath = v; ScheduleGpuPreview(); });
                AddStringProperty("Default", texNode.DefaultTexture, v => { texNode.DefaultTexture = v; ScheduleGpuPreview(); });
                AddBoolProperty("Normal Map", texNode.IsNormalMap, v => { texNode.IsNormalMap = v; ScheduleGpuPreview(); });
                break;

            case MathNode mathNode:
                AddEnumProperty("Operation", mathNode.Operation, v => { mathNode.Operation = v; RenderGraph(); ScheduleGpuPreview(); });
                break;

            case CoordinateNode coordNode:
                AddEnumProperty("Source", coordNode.Source, v => { coordNode.Source = v; RenderGraph(); ScheduleGpuPreview(); });
                break;

            case NoiseNode noiseNode:
                AddEnumProperty("Noise Type", noiseNode.Type, v => { noiseNode.Type = v; RenderGraph(); ScheduleGpuPreview(); });
                AddIntProperty("Octaves", noiseNode.Octaves, v => { noiseNode.Octaves = Math.Clamp(v, 1, 8); ScheduleGpuPreview(); });
                break;

            case PropertyNode propNode:
                AddStringProperty("Uniform", propNode.PropertyName, v => { propNode.PropertyName = v; RenderGraph(); ScheduleGpuPreview(); });
                AddStringProperty("Label", propNode.DisplayName, v => { propNode.DisplayName = v; RenderGraph(); ScheduleGpuPreview(); });
                AddEnumProperty("Kind", propNode.Kind, v => { propNode.Kind = v; UpdateProperties(); RenderGraph(); ScheduleGpuPreview(); });
                if (propNode.Kind == PropertyNode.PropertyKind.Float || propNode.Kind == PropertyNode.PropertyKind.Range)
                {
                    AddFloatProperty("Default", propNode.DefaultFloat, v => { propNode.DefaultFloat = v; ScheduleGpuPreview(); });
                    if (propNode.Kind == PropertyNode.PropertyKind.Range)
                    {
                        AddFloatProperty("Min", propNode.RangeMin, v => { propNode.RangeMin = v; ScheduleGpuPreview(); });
                        AddFloatProperty("Max", propNode.RangeMax, v => { propNode.RangeMax = v; ScheduleGpuPreview(); });
                    }
                }
                else if (propNode.Kind == PropertyNode.PropertyKind.Color)
                {
                    AddSliderProperty("R", propNode.DefaultX, 0f, 1f, v => { propNode.DefaultX = v; ScheduleGpuPreview(); });
                    AddSliderProperty("G", propNode.DefaultY, 0f, 1f, v => { propNode.DefaultY = v; ScheduleGpuPreview(); });
                    AddSliderProperty("B", propNode.DefaultZ, 0f, 1f, v => { propNode.DefaultZ = v; ScheduleGpuPreview(); });
                    AddSliderProperty("A", propNode.DefaultW, 0f, 1f, v => { propNode.DefaultW = v; ScheduleGpuPreview(); });
                }
                else if (propNode.Kind == PropertyNode.PropertyKind.Vector)
                {
                    AddFloatProperty("X", propNode.DefaultX, v => { propNode.DefaultX = v; ScheduleGpuPreview(); });
                    AddFloatProperty("Y", propNode.DefaultY, v => { propNode.DefaultY = v; ScheduleGpuPreview(); });
                    AddFloatProperty("Z", propNode.DefaultZ, v => { propNode.DefaultZ = v; ScheduleGpuPreview(); });
                    AddFloatProperty("W", propNode.DefaultW, v => { propNode.DefaultW = v; ScheduleGpuPreview(); });
                }
                else if (propNode.Kind == PropertyNode.PropertyKind.Texture2D)
                {
                    AddStringProperty("Default", propNode.DefaultTexture, v => { propNode.DefaultTexture = v; ScheduleGpuPreview(); });
                    AddStringProperty("Tex Path", propNode.TexturePath, v => { propNode.TexturePath = v; ScheduleGpuPreview(); });
                }
                break;

            case UVTransformNode uvNode:
                AddFloatProperty("Tiling X", uvNode.TilingX, v => { uvNode.TilingX = v; ScheduleGpuPreview(); });
                AddFloatProperty("Tiling Y", uvNode.TilingY, v => { uvNode.TilingY = v; ScheduleGpuPreview(); });
                AddFloatProperty("Offset X", uvNode.OffsetX, v => { uvNode.OffsetX = v; ScheduleGpuPreview(); });
                AddFloatProperty("Offset Y", uvNode.OffsetY, v => { uvNode.OffsetY = v; ScheduleGpuPreview(); });
                AddFloatProperty("Scroll X", uvNode.ScrollX, v => { uvNode.ScrollX = v; ScheduleGpuPreview(); });
                AddFloatProperty("Scroll Y", uvNode.ScrollY, v => { uvNode.ScrollY = v; ScheduleGpuPreview(); });
                AddFloatProperty("Rotation", uvNode.Rotation, v => { uvNode.Rotation = v; ScheduleGpuPreview(); });
                break;

            case BlendNode blendNode:
                AddEnumProperty("Mode", blendNode.Mode, v => { blendNode.Mode = v; RenderGraph(); ScheduleGpuPreview(); });
                break;
        }
    }

    private void AddSliderProperty(string label, float value, float min, float max, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Width = 62, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 140 };
        var valLabel = new TextBlock { Text = value.ToString("F2", CultureInfo.InvariantCulture), Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        slider.ValueChanged += (_, _) =>
        {
            valLabel.Text = slider.Value.ToString("F2", CultureInfo.InvariantCulture);
            setter((float)slider.Value);
        };
        sp.Children.Add(slider);
        sp.Children.Add(valLabel);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddFloatProperty(string label, float value, Action<float> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Width = 72, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString("0.0##", CultureInfo.InvariantCulture), Width = 100, FontSize = 11 };
        tb.LostFocus += (_, _) =>
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                setter(v);
        };
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddIntProperty(string label, int value, Action<int> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Width = 72, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 80, FontSize = 11 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                setter(v);
        };
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddStringProperty(string label, string value, Action<string> setter)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Width = 72, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var tb = new TextBox { Text = value, Width = 155, FontSize = 11 };
        tb.LostFocus += (_, _) => setter(tb.Text ?? "");
        sp.Children.Add(tb);
        PropertiesPanel.Children.Add(sp);
    }

    private void AddBoolProperty(string label, bool value, Action<bool> setter)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = Brushes.White,
            FontSize = 11
        };
        cb.IsCheckedChanged += (_, _) => setter(cb.IsChecked == true);
        PropertiesPanel.Children.Add(cb);
    }

    private void AddEnumProperty<T>(string label, T currentValue, Action<T> setter) where T : struct, Enum
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Width = 72, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        var combo = new ComboBox { Width = 155, FontSize = 11 };
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
}
