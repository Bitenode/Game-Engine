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
/// Scaled-down overview of the entire document on the right edge.
/// Syntax colours are preserved as thin lines. A semi-transparent viewport
/// rectangle shows the visible region. Click/drag to scroll.
/// </summary>
public sealed class MinimapControl : Control
{
    // ── Data references ─────────────────────────────────────────
    public TextBuffer? Buffer { get; set; }
    public IReadOnlyList<EditorClassifiedSpan>? ClassifiedSpans { get; set; }

    // ── Scroll state (read from parent) ─────────────────────────
    public double VerticalOffset { get; set; }
    public double ViewportHeight { get; set; }
    public double FullDocumentHeight { get; set; }

    // ── Events ──────────────────────────────────────────────────
    /// <summary>User clicked/dragged to scroll. Value = desired vertical offset in pixels.</summary>
    public event Action<double>? ScrollRequested;

    // ── Constants ───────────────────────────────────────────────
    private const double MinimapWidth = 80;
    private const double ScaleY = 2.0;    // pixels per line in the minimap
    private const double ScaleX = 1.2;    // pixels per character
    private static readonly IBrush s_bgBrush       = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush s_viewportBrush = new SolidColorBrush(Color.Parse("#264F7844"));
    private static readonly Pen s_viewportPen      = new(new SolidColorBrush(Color.Parse("#505050")), 1);
    private static readonly IBrush s_defaultLine   = new SolidColorBrush(Color.Parse("#555555"));

    private bool _isDragging;

    public MinimapControl()
    {
        Width = MinimapWidth;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    // ── Render ──────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        if (Buffer == null) return;
        var bounds = Bounds;

        ctx.DrawRectangle(s_bgBrush, null, new Rect(bounds.Size));

        int lineCount = Buffer.LineCount;
        double totalH = lineCount * ScaleY;

        // How much of the minimap is visible (scroll the minimap if doc is huge)
        double minimapScroll = 0;
        if (totalH > bounds.Height && FullDocumentHeight > 0)
        {
            double ratio = VerticalOffset / Math.Max(1, FullDocumentHeight - ViewportHeight);
            minimapScroll = ratio * (totalH - bounds.Height);
        }

        // Draw line representations
        if (ClassifiedSpans is { Count: > 0 })
            DrawClassifiedMinimap(ctx, lineCount, minimapScroll);
        else
            DrawPlainMinimap(ctx, lineCount, minimapScroll);

        // Viewport rectangle
        if (FullDocumentHeight > 0)
        {
            double vpTop = (VerticalOffset / FullDocumentHeight) * totalH - minimapScroll;
            double vpH = (ViewportHeight / FullDocumentHeight) * totalH;
            vpH = Math.Max(vpH, 10);

            ctx.DrawRectangle(s_viewportBrush, s_viewportPen,
                new Rect(0, vpTop, bounds.Width, vpH));
        }
    }

    private void DrawPlainMinimap(DrawingContext ctx, int lineCount, double scroll)
    {
        for (int i = 0; i < lineCount; i++)
        {
            double y = i * ScaleY - scroll;
            if (y > Bounds.Height) break;
            if (y + ScaleY < 0) continue;

            int len = Buffer!.GetLineLength(i);
            if (len == 0) continue;

            double width = Math.Min(len * ScaleX, Bounds.Width - 4);
            ctx.DrawRectangle(s_defaultLine, null, new Rect(2, y, width, Math.Max(1, ScaleY - 0.5)));
        }
    }

    private void DrawClassifiedMinimap(DrawingContext ctx, int lineCount, double scroll)
    {
        // Build per-line brush from first classified span on each line
        var spans = ClassifiedSpans!;
        int si = 0;

        for (int i = 0; i < lineCount; i++)
        {
            double y = i * ScaleY - scroll;
            if (y > Bounds.Height) break;
            if (y + ScaleY < 0) { continue; }

            int lineStart = Buffer!.GetLineStartOffset(i);
            int lineLen = Buffer.GetLineLength(i);
            if (lineLen == 0) continue;
            int lineEnd = lineStart + lineLen;

            // Advance span index
            while (si < spans.Count && spans[si].Start + spans[si].Length <= lineStart) si++;

            // Draw the line with colored segments
            int col = 0;
            int savedSi = si;
            int tempSi = savedSi;

            while (col < lineLen && col * ScaleX < Bounds.Width - 4)
            {
                // Find a span covering current position
                int pos = lineStart + col;
                while (tempSi < spans.Count && spans[tempSi].Start + spans[tempSi].Length <= pos) tempSi++;

                IBrush brush = s_defaultLine;
                int segEnd = lineLen;

                if (tempSi < spans.Count && spans[tempSi].Start <= pos && spans[tempSi].Start < lineEnd)
                {
                    brush = SyntaxTheme.GetBrush(spans[tempSi].Classification);
                    segEnd = Math.Min(spans[tempSi].Start + spans[tempSi].Length - lineStart, lineLen);
                    tempSi++;
                }
                else if (tempSi < spans.Count && spans[tempSi].Start < lineEnd)
                {
                    segEnd = spans[tempSi].Start - lineStart;
                }

                double x1 = col * ScaleX + 2;
                double x2 = Math.Min(segEnd * ScaleX + 2, Bounds.Width - 2);
                ctx.DrawRectangle(brush, null, new Rect(x1, y, Math.Max(1, x2 - x1), Math.Max(1, ScaleY - 0.5)));
                col = segEnd;
            }
        }
    }

    // ── Mouse interaction ───────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _isDragging = true;
        e.Pointer.Capture(this);
        HandleScrollClick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging) HandleScrollClick(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private void HandleScrollClick(Point point)
    {
        if (Buffer == null || FullDocumentHeight <= 0) return;

        int lineCount = Buffer.LineCount;
        double totalH = lineCount * ScaleY;

        double minimapScroll = 0;
        if (totalH > Bounds.Height && FullDocumentHeight > 0)
        {
            double ratio = VerticalOffset / Math.Max(1, FullDocumentHeight - ViewportHeight);
            minimapScroll = ratio * (totalH - Bounds.Height);
        }

        double clickLine = (point.Y + minimapScroll) / ScaleY;
        double newOffset = (clickLine / lineCount) * FullDocumentHeight - ViewportHeight / 2;
        ScrollRequested?.Invoke(Math.Clamp(newOffset, 0, FullDocumentHeight - ViewportHeight));
    }
}
