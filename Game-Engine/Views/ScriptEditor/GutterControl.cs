using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

/// <summary>
/// Renders line numbers in the gutter area to the left of the code canvas.
/// Click a line number to select the entire line.
/// Width auto-adjusts based on total line count.
/// </summary>
public sealed class GutterControl : Control
{
    // ── Data references (set by CodeEditorControl) ──────────────
    public TextBuffer? Buffer { get; set; }
    public CaretState? Caret { get; set; }
    public double VerticalOffset { get; set; }

    // ── Metrics (shared with CodeCanvas) ────────────────────────
    public double LineHeight { get; set; }
    public double CharWidth { get; set; }

    // ── Folding ──────────────────────────────────────────────────
    public IReadOnlyList<FoldRegion>? FoldRegions { get; set; }
    public HashSet<int>? CollapsedLines { get; set; }

    // ── Events ──────────────────────────────────────────────────
    /// <summary>Fired when the user clicks a line number (int = line index).</summary>
    public event Action<int>? LineClicked;
    /// <summary>Fired when the user clicks a fold toggle (int = start line of the region).</summary>
    public event Action<int>? FoldToggled;

    // ── Constants ───────────────────────────────────────────────
    private const double RightPad = 16;
    private const double LeftPad = 8;
    private const double FoldMarginWidth = 14;
    private const double FontSize = CodeCanvas.FontSize;
    private static readonly Typeface s_typeface = new("Consolas,Menlo,Monospace");

    // ── Brushes ─────────────────────────────────────────────────
    private static readonly IBrush s_bgBrush        = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush s_lineNumBrush   = new SolidColorBrush(Color.Parse("#858585"));
    private static readonly IBrush s_activeNumBrush = new SolidColorBrush(Color.Parse("#C6C6C6"));
    private static readonly IBrush s_borderBrush    = new SolidColorBrush(Color.Parse("#303030"));

    public GutterControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    // ── Desired width (based on digit count) ────────────────────

    public double ComputeDesiredWidth()
    {
        if (Buffer == null || CharWidth <= 0) return 50;
        int digits = Math.Max(2, Buffer.LineCount.ToString().Length);
        return LeftPad + digits * CharWidth + RightPad + FoldMarginWidth;
    }

    // ── Render ──────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        if (Buffer == null || LineHeight <= 0) return;

        var bounds = Bounds;

        // Background
        ctx.DrawRectangle(s_bgBrush, null, new Rect(bounds.Size));

        // Right border line
        ctx.DrawLine(new Pen(s_borderBrush, 1),
            new Point(bounds.Width - 0.5, 0),
            new Point(bounds.Width - 0.5, bounds.Height));

        int firstLine = Math.Max(0, (int)(VerticalOffset / LineHeight));
        int visibleCount = (int)(bounds.Height / LineHeight) + 2;
        int lastLine = Math.Min(firstLine + visibleCount, Buffer.LineCount - 1);

        int caretLine = Caret != null ? Buffer.GetLineFromPosition(Caret.Position) : -1;

        var foldStarts = new HashSet<int>();
        if (FoldRegions != null)
            foreach (var r in FoldRegions) foldStarts.Add(r.StartLine);

        for (int i = firstLine; i <= lastLine; i++)
        {
            double y = i * LineHeight - VerticalOffset;
            string num = (i + 1).ToString();
            bool isActive = (i == caretLine);

            var brush = isActive ? s_activeNumBrush : s_lineNumBrush;
            var ft = new FormattedText(
                num, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                s_typeface, FontSize, brush);

            double x = bounds.Width - RightPad - FoldMarginWidth - ft.Width;
            ctx.DrawText(ft, new Point(x, y));

            // Fold toggle arrow
            if (foldStarts.Contains(i))
            {
                bool collapsed = CollapsedLines?.Contains(i) == true;
                string arrow = collapsed ? "\u25B6" : "\u25BC"; // right / down triangle
                var aft = new FormattedText(
                    arrow, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    s_typeface, FontSize * 0.65, s_lineNumBrush);
                double ax = bounds.Width - FoldMarginWidth + (FoldMarginWidth - aft.Width) / 2;
                double ay = y + (LineHeight - aft.Height) / 2;
                ctx.DrawText(aft, new Point(ax, ay));
            }
        }
    }

    // ── Click to select line ────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Buffer == null || LineHeight <= 0) return;

        var point = e.GetPosition(this);
        int line = (int)((point.Y + VerticalOffset) / LineHeight);
        line = Math.Clamp(line, 0, Buffer.LineCount - 1);

        // If click is in the fold margin area
        if (point.X >= Bounds.Width - FoldMarginWidth)
        {
            FoldToggled?.Invoke(line);
        }
        else
        {
            LineClicked?.Invoke(line);
        }
        e.Handled = true;
    }
}
