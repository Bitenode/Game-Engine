using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Game_Engine.Views;

/// <summary>Vector toolbar icons for terrain tools and brush masks (replaces emoji / unicode glyphs).</summary>
internal static class TerrainInspectorIcons
{
    static readonly IBrush StrokeBrush = Brushes.Gray;
    static readonly IBrush FillBrush = Brushes.Gray;
    const double Box = 20;

    static Viewbox Wrap(Control child) =>
        new() { Width = Box, Height = Box, Stretch = Stretch.Uniform, Child = child };

    static Grid MaskCrossedCircle()
    {
        var g = new Grid { Width = 18, Height = 18 };
        g.Children.Add(new Shapes.Ellipse { Width = 18, Height = 18, Stroke = StrokeBrush, StrokeThickness = 1.5, Fill = Brushes.Transparent });
        g.Children.Add(new Shapes.Line { StartPoint = new(9, 2), EndPoint = new(9, 16), Stroke = StrokeBrush, StrokeThickness = 1.2 });
        g.Children.Add(new Shapes.Line { StartPoint = new(2, 9), EndPoint = new(16, 9), Stroke = StrokeBrush, StrokeThickness = 1.2 });
        return g;
    }

    static Grid MaskStarburst()
    {
        var g = new Grid { Width = 18, Height = 18 };
        g.Children.Add(new Shapes.Ellipse { Width = 16, Height = 16, Stroke = StrokeBrush, StrokeThickness = 1.2, Fill = Brushes.Transparent });
        g.Children.Add(new Shapes.Line { StartPoint = new(9, 1), EndPoint = new(9, 17), Stroke = StrokeBrush, StrokeThickness = 1 });
        g.Children.Add(new Shapes.Line { StartPoint = new(1, 9), EndPoint = new(17, 9), Stroke = StrokeBrush, StrokeThickness = 1 });
        return g;
    }

    static Shapes.Path IconPath(string pathData, bool strokeOnly = false)
    {
        var p = new Shapes.Path
        {
            Data = StreamGeometry.Parse(pathData),
            Fill = strokeOnly ? Brushes.Transparent : FillBrush,
            Stroke = strokeOnly ? StrokeBrush : null,
            StrokeThickness = strokeOnly ? 1.35 : 0,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return p;
    }

    /// <summary>Terrain sculpt/paint tool icons (ids 0–9).</summary>
    public static Control TerrainToolContent(int toolId) => toolId switch
    {
        0 => Wrap(IconPath("M2,15 L12,3 L22,15 Z")),
        1 => Wrap(new Shapes.Ellipse { Width = 16, Height = 16, Stroke = StrokeBrush, StrokeThickness = 2, Fill = Brushes.Transparent }),
        2 => Wrap(IconPath("M3,10 L7,6 L11,10 L15,6 L19,10", strokeOnly: true)),
        3 => Wrap(IconPath("M2,12 Q12,5 22,12 Q12,19 2,12")),
        4 => Wrap(IconPath("M5,17 L5,9 L9,5 L13,9 L17,5 L21,9 L21,17 Z")),
        5 => Wrap(IconPath("M2,14 L22,14", strokeOnly: true)),
        6 => Wrap(IconPath("M7,18 L17,4 L17,18 Z")),
        7 => Wrap(IconPath("M4,5 L20,5 L20,9 L4,9 Z M4,11 L20,11 L20,15 L4,15 Z")),
        8 => Wrap(IconPath("M2,12 C6,6 10,18 14,12 C18,6 22,12 22,12", strokeOnly: true)),
        9 => Wrap(IconPath("M12,4 L17,12 L7,12 Z M9,12 L9,14 L15,14 L15,12 L19,18 L5,18 Z")),
        _ => new TextBlock { Text = "?", FontSize = 12 }
    };

    /// <summary>Brush mask preset icons (indices 0–7).</summary>
    public static Control BrushMaskContent(int maskIndex) => maskIndex switch
    {
        0 => Wrap(new Shapes.Ellipse { Width = 18, Height = 18, Fill = FillBrush }),
        1 => Wrap(new Shapes.Ellipse { Width = 18, Height = 18, Stroke = StrokeBrush, StrokeThickness = 2, Fill = Brushes.Transparent }),
        2 => Wrap(new Shapes.Ellipse { Width = 14, Height = 14, Fill = FillBrush }),
        3 => Wrap(MaskCrossedCircle()),
        4 => Wrap(IconPath("M12,2 L22,12 L12,22 L2,12 Z")),
        5 => Wrap(IconPath("M12,4 L16,11 L8,11 Z M10,11 L14,17 L18,11", strokeOnly: true)),
        6 => Wrap(MaskStarburst()),
        7 => Wrap(IconPath("M4,12 L8,8 L12,12 L16,8 L20,12 L16,16 L12,12 L8,16 Z")),
        _ => new TextBlock { Text = "?", FontSize = 12 }
    };
}
