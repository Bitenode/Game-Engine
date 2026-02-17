using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core;

namespace Game_Engine.Views;

public partial class ProfilerPanel : UserControl
{
    private readonly DispatcherTimer _refreshTimer;

    public ProfilerPanel()
    {
        InitializeComponent();

        EnableToggle.IsCheckedChanged += (_, _) =>
        {
            Profiler.Enabled = EnableToggle.IsChecked == true;
        };

        // Refresh the UI at ~10 Hz to avoid excessive overhead
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) => RefreshUI();
        _refreshTimer.Start();
    }

    private void RefreshUI()
    {
        if (!Profiler.Enabled || Profiler.FrameCount == 0)
        {
            FpsLabel.Text = "FPS: --";
            FrameTimeLabel.Text = "Frame: -- ms";
            return;
        }

        var latest = Profiler.Latest;
        double fps = Profiler.FPS;
        double avgMs = Profiler.AverageFrameMs();

        FpsLabel.Text = $"FPS: {fps:F0}";
        FrameTimeLabel.Text = $"Frame: {latest.TotalFrameMs:F1} ms";

        RenderLabel.Text = $"Render: {latest.RenderMs:F2} ms";
        PhysicsLabel.Text = $"Physics: {latest.PhysicsMs:F2} ms";
        ScriptsLabel.Text = $"Scripts: {latest.ScriptsMs:F2} ms";
        AudioLabel.Text = $"Audio: {latest.AudioMs:F2} ms";
        AnimLabel.Text = $"Anim: {latest.AnimationMs:F2} ms";

        DrawCallsLabel.Text = $"Draw Calls: {latest.DrawCalls}";
        TrianglesLabel.Text = $"Triangles: {latest.TriangleCount:N0}";
        BatchesLabel.Text = $"Batches: {latest.BatchCount}";

        GameObjectsLabel.Text = $"GameObjects: {latest.ActiveGameObjects}";
        CollidersLabel.Text = $"Colliders: {latest.ActiveColliders}";
        AvgFpsLabel.Text = $"Avg FPS: {fps:F0}";
        AvgFrameLabel.Text = $"Avg Frame: {avgMs:F1} ms";

        DrawGraph();
    }

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();

        double w = GraphCanvas.Bounds.Width;
        double h = GraphCanvas.Bounds.Height;
        if (w < 10 || h < 10) return;

        int frameCount = Math.Min(Profiler.FrameCount, (int)w);
        if (frameCount < 2) return;

        // Find max frame time for scaling (cap at 33ms = 30fps minimum)
        double maxMs = 33.0;
        for (int i = 0; i < frameCount; i++)
        {
            var f = Profiler.GetFrame(i);
            if (f.TotalFrameMs > maxMs) maxMs = f.TotalFrameMs;
        }
        maxMs *= 1.1; // 10% headroom

        // Draw target lines
        DrawHorizontalLine(h, maxMs, 16.67, w, Brushes.Green, "60fps");   // 60 fps
        DrawHorizontalLine(h, maxMs, 33.33, w, Brushes.Yellow, "30fps");  // 30 fps

        // Draw frame time bars
        double barWidth = Math.Max(1, w / frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            var f = Profiler.GetFrame(frameCount - 1 - i);
            double barH = (f.TotalFrameMs / maxMs) * h;

            // Color based on frame time
            IBrush color;
            if (f.TotalFrameMs <= 16.67) color = Brushes.LimeGreen;
            else if (f.TotalFrameMs <= 33.33) color = Brushes.Orange;
            else color = Brushes.OrangeRed;

            var rect = new Rectangle
            {
                Width = Math.Max(1, barWidth - 1),
                Height = Math.Max(1, barH),
                Fill = color,
                Opacity = 0.7
            };
            Canvas.SetLeft(rect, i * barWidth);
            Canvas.SetTop(rect, h - barH);
            GraphCanvas.Children.Add(rect);
        }
    }

    private void DrawHorizontalLine(double canvasH, double maxMs, double targetMs,
                                     double canvasW, IBrush brush, string label)
    {
        double y = canvasH - (targetMs / maxMs) * canvasH;
        if (y < 0 || y > canvasH) return;

        var line = new Line
        {
            StartPoint = new Point(0, y),
            EndPoint = new Point(canvasW, y),
            Stroke = brush,
            StrokeThickness = 1,
            Opacity = 0.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 }
        };
        GraphCanvas.Children.Add(line);

        var text = new TextBlock
        {
            Text = label,
            Foreground = brush,
            FontSize = 10,
            Opacity = 0.7
        };
        Canvas.SetLeft(text, 4);
        Canvas.SetTop(text, y - 14);
        GraphCanvas.Children.Add(text);
    }
}
