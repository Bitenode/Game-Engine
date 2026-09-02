using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

/// <summary>
/// Low-level rendering surface for the code editor.
/// Draws text, selection highlights, current-line highlight,
/// and a blinking caret using Avalonia's DrawingContext.
/// </summary>
public sealed class CodeCanvas : Control
{
    // ── Data references (set by CodeEditorControl) ──────────────

    public TextBuffer? Buffer { get; set; }
    public CaretState? Caret { get; set; }

    /// <summary>Classified spans for syntax colouring.</summary>
    public IReadOnlyList<EditorClassifiedSpan>? ClassifiedSpans { get; set; }

    /// <summary>Search match positions for find highlighting.</summary>
    public IReadOnlyList<(int start, int length)>? SearchMatches { get; set; }
    public int CurrentMatchIndex { get; set; } = -1;

    /// <summary>Collapsed fold regions (startLine, endLine). Lines startLine+1..endLine are hidden.</summary>
    public IReadOnlyList<(int startLine, int endLine)>? CollapsedRegions { get; set; }

    /// <summary>Matched bracket positions for highlight. (-1 means no match).</summary>
    public int BracketPos1 { get; set; } = -1;
    public int BracketPos2 { get; set; } = -1;

    /// <summary>Diagnostics for squiggle rendering.</summary>
    public IReadOnlyList<EditorDiagnostic>? Diagnostics { get; set; }

    // ── Scroll state ────────────────────────────────────────────

    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    /// <summary>When true, lines split at viewport width (fixed column wrap). Search, squiggles, brackets, and indent guides are not drawn in this mode.</summary>
    public bool WordWrap { get; set; }

    // ── Measured metrics (read by CodeEditorControl) ────────────

    public double LineHeight { get; private set; }
    public double CharWidth { get; private set; }

    // ── Constants ───────────────────────────────────────────────

    public const double DefaultFontSize = 14;
    public const double LeftPadding = 4;

    /// <summary>Monospace size in px (affects measure, draw, and scroll math).</summary>
    public double EditorFontSize { get; set; } = DefaultFontSize;

    double _measureCacheFont = -1;
    private static readonly Typeface s_typeface = new("Consolas,Menlo,Monospace");

    // ── Brushes ─────────────────────────────────────────────────

    private static readonly IBrush s_bgBrush         = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush s_textBrush        = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush s_selectionBrush   = new SolidColorBrush(Color.Parse("#264F78"));
    private static readonly IBrush s_currentLineBrush = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush s_caretBrush       = new SolidColorBrush(Color.Parse("#AEAFAD"));
    private static readonly IBrush s_matchBrush       = new SolidColorBrush(Color.Parse("#613214"));
    private static readonly IBrush s_activeMatchBrush = new SolidColorBrush(Color.Parse("#515C6A"));
    private static readonly IBrush s_bracketBrush     = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly Pen s_bracketPen          = new(new SolidColorBrush(Color.Parse("#888888")), 1);
    private static readonly Pen s_errorSquiggle       = new(new SolidColorBrush(Color.Parse("#F44747")), 1.2);
    private static readonly Pen s_warningSquiggle     = new(new SolidColorBrush(Color.Parse("#CCA700")), 1.2);
    private static readonly Pen s_indentGuidePen      = new(new SolidColorBrush(Color.Parse("#404040")), 1);
    private static readonly Pen s_activeIndentPen     = new(new SolidColorBrush(Color.Parse("#606060")), 1);

    // ── Caret blink ─────────────────────────────────────────────

    private readonly DispatcherTimer _caretTimer;
    private bool _caretVisible = true;

    public CodeCanvas()
    {
        ClipToBounds = true;
        IsHitTestVisible = false; // input goes to the parent CodeEditorControl

        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeasureCharSize();
        _caretTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _caretTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Make the caret visible and restart the blink cycle.</summary>
    public void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        InvalidateVisual();
    }

    // ── Char / line measurement ─────────────────────────────────

    public void MeasureCharSize()
    {
        if (Math.Abs(_measureCacheFont - EditorFontSize) > 0.0001)
        {
            CharWidth = 0;
            LineHeight = 0;
            _measureCacheFont = EditorFontSize;
        }
        if (CharWidth > 0 && LineHeight > 0) return;
        try
        {
            var ft = new FormattedText(
                new string('M', 80),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                s_typeface, EditorFontSize, s_textBrush);

            CharWidth = ft.Width / 80.0;
            LineHeight = ft.Height;
        }
        catch
        {
            // Fallback if measurement fails
        }

        if (CharWidth < 1) CharWidth = EditorFontSize * 0.6;
        if (LineHeight < 1) LineHeight = EditorFontSize * 1.35;
    }

    public int WrapColumns(double viewWidth)
    {
        MeasureCharSize();
        if (CharWidth <= 0) return 80;
        int n = (int)((viewWidth - LeftPadding - 2) / CharWidth);
        return Math.Max(8, n);
    }

    public int VisualRowsForBufferLine(int lineIdx)
    {
        if (!WordWrap || Buffer == null) return 1;
        int len = Buffer.GetLineLength(lineIdx);
        if (len == 0) return 1;
        int w = WrapColumns(Bounds.Width);
        return (len + w - 1) / w;
    }

    public int CumulativeVisualRowsBefore(int bufferLine)
    {
        if (Buffer == null) return 0;
        if (!WordWrap)
            return Math.Clamp(bufferLine, 0, Buffer.LineCount);
        int sum = 0;
        for (int i = 0; i < bufferLine && i < Buffer.LineCount; i++)
            sum += VisualRowsForBufferLine(i);
        return sum;
    }

    public int GetTotalVisualRows()
    {
        if (Buffer == null) return 1;
        if (!WordWrap) return Math.Max(1, Buffer.LineCount);
        int s = 0;
        for (int i = 0; i < Buffer.LineCount; i++)
            s += VisualRowsForBufferLine(i);
        return Math.Max(1, s);
    }

    public void DecomposeGlobalVisualRow(int vRow, out int bufferLine, out int subRow)
    {
        bufferLine = 0;
        subRow = 0;
        if (Buffer == null) return;
        if (!WordWrap)
        {
            bufferLine = Math.Clamp(vRow, 0, Math.Max(0, Buffer.LineCount - 1));
            subRow = 0;
            return;
        }

        int remaining = vRow;
        for (int i = 0; i < Buffer.LineCount; i++)
        {
            int rows = VisualRowsForBufferLine(i);
            if (remaining < rows)
            {
                bufferLine = i;
                subRow = remaining;
                return;
            }
            remaining -= rows;
        }

        bufferLine = Math.Max(0, Buffer.LineCount - 1);
        subRow = Math.Max(0, VisualRowsForBufferLine(bufferLine) - 1);
    }

    public (int bufferLine, int subRow) DecomposeGlobalVisualRowTuple(int vRow)
    {
        DecomposeGlobalVisualRow(vRow, out var bl, out var sr);
        return (bl, sr);
    }

    public void GetCaretVisualCoords(int wCols, out int globalVisualRow, out int colInChunk)
    {
        globalVisualRow = 0;
        colInChunk = 0;
        if (Buffer == null || Caret == null) return;
        int bufLine = Buffer.GetLineFromPosition(Caret.Position);
        int col = Caret.Position - Buffer.GetLineStartOffset(bufLine);
        if (!WordWrap || wCols <= 0)
        {
            globalVisualRow = bufLine;
            colInChunk = col;
            return;
        }

        int subRow = col / wCols;
        colInChunk = col % wCols;
        globalVisualRow = CumulativeVisualRowsBefore(bufLine) + subRow;
    }

    // ── Render ──────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        MeasureCharSize();
        if (Buffer == null) return;

        if (WordWrap)
        {
            RenderWordWrapped(ctx);
            return;
        }

        var bounds = Bounds;

        // Background
        ctx.DrawRectangle(s_bgBrush, null, new Rect(bounds.Size));

        int firstLine = Math.Max(0, (int)(VerticalOffset / LineHeight));
        int visibleCount = (int)(bounds.Height / LineHeight) + 2;
        int lastLine = Math.Min(firstLine + visibleCount, Buffer.LineCount - 1);

        // Current-line highlight
        if (Caret != null)
        {
            int caretLine = Buffer.GetLineFromPosition(Caret.Position);
            if (caretLine >= firstLine && caretLine <= lastLine)
            {
                double y = caretLine * LineHeight - VerticalOffset;
                ctx.DrawRectangle(s_currentLineBrush, null,
                    new Rect(0, y, bounds.Width, LineHeight));
            }
        }

        // Search match highlights
        if (SearchMatches is { Count: > 0 })
            DrawSearchMatches(ctx, firstLine, lastLine);

        // Selection rectangles
        if (Caret is { HasSelection: true })
            DrawSelection(ctx, firstLine, lastLine, bounds.Width);

        // Indent guides
        DrawIndentGuides(ctx, firstLine, lastLine);

        // Text lines
        DrawTextLines(ctx, firstLine, lastLine);

        // Diagnostic squiggles
        if (Diagnostics is { Count: > 0 })
            DrawSquiggles(ctx, firstLine, lastLine);

        // Bracket highlights
        if (BracketPos1 >= 0 && BracketPos2 >= 0)
        {
            DrawBracketHighlight(ctx, BracketPos1);
            DrawBracketHighlight(ctx, BracketPos2);
        }

        // Caret
        if (Caret != null && _caretVisible)
            DrawCaret(ctx);
    }

    private void RenderWordWrapped(DrawingContext ctx)
    {
        var bounds = Bounds;
        ctx.DrawRectangle(s_bgBrush, null, new Rect(bounds.Size));

        int wCols = WrapColumns(bounds.Width);
        int firstVis = Math.Max(0, (int)(VerticalOffset / LineHeight));
        int visibleCount = (int)(bounds.Height / LineHeight) + 2;
        int lastVis = firstVis + visibleCount;

        if (Caret != null)
        {
            int caretBufLine = Buffer!.GetLineFromPosition(Caret.Position);
            int vStart = CumulativeVisualRowsBefore(caretBufLine);
            int rows = VisualRowsForBufferLine(caretBufLine);
            for (int j = 0; j < rows; j++)
            {
                double y = (vStart + j) * LineHeight - VerticalOffset;
                if (y + LineHeight < 0 || y > bounds.Height) continue;
                ctx.DrawRectangle(s_currentLineBrush, null, new Rect(0, y, bounds.Width, LineHeight));
            }
        }

        if (Caret is { HasSelection: true })
            DrawSelectionWrapped(ctx, firstVis, lastVis, wCols);

        for (int v = firstVis; v < lastVis; v++)
        {
            DecomposeGlobalVisualRow(v, out int bl, out int sr);
            if (bl >= Buffer!.LineCount) continue;
            int lineStart = Buffer.GetLineStartOffset(bl);
            int lineLen = Buffer.GetLineLength(bl);
            int col0 = sr * wCols;
            if (col0 >= lineLen) continue;
            int chunkLen = Math.Min(wCols, lineLen - col0);
            double y = v * LineHeight - VerticalOffset;

            if (ClassifiedSpans != null && ClassifiedSpans.Count > 0)
                DrawClassifiedWrapChunk(ctx, lineStart + col0, chunkLen, y);
            else
            {
                string text = Buffer.GetText(lineStart + col0, chunkLen);
                if (text.Length == 0) continue;
                var ft = new FormattedText(
                    text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    s_typeface, EditorFontSize, s_textBrush);
                ctx.DrawText(ft, new Point(LeftPadding, y));
            }
        }

        if (Caret != null && _caretVisible)
            DrawCaretWrapped(ctx, wCols);
    }

    private void DrawClassifiedWrapChunk(DrawingContext ctx, int absStart, int length, double y)
    {
        if (Buffer == null || ClassifiedSpans == null || length <= 0) return;
        int absEnd = absStart + length;
        var spans = ClassifiedSpans;
        int si = 0;
        while (si < spans.Count && spans[si].Start + spans[si].Length <= absStart) si++;

        int pos = absStart;
        int relCol = 0;
        while (pos < absEnd)
        {
            while (si < spans.Count && spans[si].Start + spans[si].Length <= pos) si++;

            if (si < spans.Count && spans[si].Start < absEnd)
            {
                var span = spans[si];
                int spanStart = span.Start;
                int spanEnd = span.Start + span.Length;

                if (pos < spanStart && spanStart < absEnd)
                {
                    int gapLen = Math.Min(spanStart, absEnd) - pos;
                    DrawTextRun(ctx, Buffer.GetText(pos, gapLen), relCol, y, SyntaxTheme.DefaultBrush);
                    relCol += gapLen;
                    pos += gapLen;
                }

                int drawStart = Math.Max(pos, spanStart);
                int drawEnd = Math.Min(spanEnd, absEnd);
                if (drawEnd > drawStart)
                {
                    int runLen = drawEnd - drawStart;
                    DrawTextRun(ctx, Buffer.GetText(drawStart, runLen), relCol, y,
                        SyntaxTheme.GetBrush(span.Classification));
                    relCol += runLen;
                    pos = drawEnd;
                }

                if (spanEnd <= pos) si++;
            }
            else
            {
                int rem = absEnd - pos;
                DrawTextRun(ctx, Buffer.GetText(pos, rem), relCol, y, SyntaxTheme.DefaultBrush);
                break;
            }
        }
    }

    private void DrawSelectionWrapped(DrawingContext ctx, int firstVis, int lastVis, int wCols)
    {
        if (Caret == null || Buffer == null) return;
        int selStart = Math.Min(Caret.SelectionStart, Caret.SelectionEnd);
        int selEnd = Math.Max(Caret.SelectionStart, Caret.SelectionEnd);
        int startLine = Buffer.GetLineFromPosition(selStart);
        int endLine = Buffer.GetLineFromPosition(selEnd);

        for (int line = startLine; line <= endLine; line++)
        {
            int lineStart = Buffer.GetLineStartOffset(line);
            int lineLen = Buffer.GetLineLength(line);
            int ls = (line == startLine) ? selStart - lineStart : 0;
            int le = (line == endLine) ? selEnd - lineStart : lineLen;
            if (le <= ls) continue;

            int vBase = CumulativeVisualRowsBefore(line);
            int firstSub = ls / wCols;
            int lastSub = (le - 1) / wCols;
            for (int sr = firstSub; sr <= lastSub; sr++)
            {
                int vRow = vBase + sr;
                if (vRow < firstVis || vRow >= lastVis) continue;
                int chunkCol = sr * wCols;
                int chunkEnd = Math.Min(chunkCol + wCols, lineLen);
                int segStart = Math.Max(ls, chunkCol);
                int segEnd = Math.Min(le, chunkEnd);
                if (segEnd <= segStart) continue;
                double x1 = (segStart - chunkCol) * CharWidth + LeftPadding;
                double x2 = (segEnd - chunkCol) * CharWidth + LeftPadding;
                double rowY = vRow * LineHeight - VerticalOffset;
                ctx.DrawRectangle(s_selectionBrush, null,
                    new Rect(x1, rowY, Math.Max(1, x2 - x1), LineHeight));
            }
        }
    }

    private void DrawCaretWrapped(DrawingContext ctx, int wCols)
    {
        GetCaretVisualCoords(wCols, out int vRow, out int colInChunk);
        double x = colInChunk * CharWidth + LeftPadding;
        double y = vRow * LineHeight - VerticalOffset;
        ctx.DrawLine(
            new Pen(s_caretBrush, 2),
            new Point(x, y),
            new Point(x, y + LineHeight));
    }

    // ── Search matches ─────────────────────────────────────────

    private void DrawSearchMatches(DrawingContext ctx, int firstLine, int lastLine)
    {
        int viewStart = Buffer!.GetLineStartOffset(firstLine);
        int viewEnd = (lastLine + 1 < Buffer.LineCount)
            ? Buffer.GetLineStartOffset(lastLine + 1)
            : Buffer.Length;

        for (int mi = 0; mi < SearchMatches!.Count; mi++)
        {
            var (start, length) = SearchMatches[mi];
            if (start + length <= viewStart || start >= viewEnd) continue;

            var brush = (mi == CurrentMatchIndex) ? s_activeMatchBrush : s_matchBrush;

            int mStartLine = Buffer.GetLineFromPosition(start);
            int mEndLine = Buffer.GetLineFromPosition(start + length);

            for (int line = Math.Max(firstLine, mStartLine); line <= Math.Min(lastLine, mEndLine); line++)
            {
                int lineStart = Buffer.GetLineStartOffset(line);
                int lineLen = Buffer.GetLineLength(line);
                int colStart = (line == mStartLine) ? start - lineStart : 0;
                int colEnd = (line == mEndLine) ? start + length - lineStart : lineLen;

                double x1 = colStart * CharWidth + LeftPadding - HorizontalOffset;
                double x2 = colEnd * CharWidth + LeftPadding - HorizontalOffset;
                double y = line * LineHeight - VerticalOffset;

                ctx.DrawRectangle(brush, null, new Rect(x1, y, Math.Max(1, x2 - x1), LineHeight));
            }
        }
    }

    // ── Selection ───────────────────────────────────────────────

    private void DrawSelection(DrawingContext ctx, int firstLine, int lastLine, double viewWidth)
    {
        int selStart = Caret!.SelectionStart;
        int selEnd = Caret.SelectionEnd;
        int startLine = Buffer!.GetLineFromPosition(selStart);
        int endLine = Buffer.GetLineFromPosition(selEnd);

        for (int i = Math.Max(firstLine, startLine); i <= Math.Min(lastLine, endLine); i++)
        {
            int lineStart = Buffer.GetLineStartOffset(i);
            int lineLen = Buffer.GetLineLength(i);

            int colStart = (i == startLine) ? selStart - lineStart : 0;
            int colEnd = (i == endLine) ? selEnd - lineStart : lineLen;

            double x1 = colStart * CharWidth + LeftPadding - HorizontalOffset;
            double x2 = colEnd * CharWidth + LeftPadding - HorizontalOffset;
            double y = i * LineHeight - VerticalOffset;

            if (i != endLine)
                x2 = Math.Max(x2, x2 + CharWidth);

            ctx.DrawRectangle(s_selectionBrush, null,
                new Rect(x1, y, Math.Max(0, x2 - x1), LineHeight));
        }
    }

    // ── Text drawing ─

    private void DrawTextLines(DrawingContext ctx, int firstLine, int lastLine)
    {
        if (ClassifiedSpans != null && ClassifiedSpans.Count > 0)
        {
            DrawClassifiedText(ctx, firstLine, lastLine);
            return;
        }

        for (int i = firstLine; i <= lastLine; i++)
        {
            string text = Buffer!.GetLineText(i);
            if (text.Length == 0) continue;

            double y = i * LineHeight - VerticalOffset;
            double x = LeftPadding - HorizontalOffset;

            var ft = new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                s_typeface, EditorFontSize, s_textBrush);

            ctx.DrawText(ft, new Point(x, y));
        }
    }

    /// <summary>Draw text with per-span syntax colouring from Roslyn classification.</summary>
    private void DrawClassifiedText(DrawingContext ctx, int firstLine, int lastLine)
    {
        var spans = ClassifiedSpans!;
        int spanIdx = 0;

        for (int line = firstLine; line <= lastLine; line++)
        {
            int lineStart = Buffer!.GetLineStartOffset(line);
            int lineLen = Buffer.GetLineLength(line);
            if (lineLen == 0) continue;

            int lineEnd = lineStart + lineLen;
            double y = line * LineHeight - VerticalOffset;

            // Advance span index to this line
            while (spanIdx < spans.Count && spans[spanIdx].Start + spans[spanIdx].Length <= lineStart)
                spanIdx++;

            int col = 0;
            int pos = lineStart;

            // Walk through the line, drawing classified and unclassified runs
            int si = spanIdx;
            while (pos < lineEnd)
            {
                // Find next span that overlaps [pos, lineEnd)
                while (si < spans.Count && spans[si].Start + spans[si].Length <= pos) si++;

                if (si < spans.Count && spans[si].Start < lineEnd)
                {
                    var span = spans[si];
                    int spanStart = span.Start;
                    int spanEnd = span.Start + span.Length;

                    // Gap before this span
                    if (pos < spanStart && spanStart < lineEnd)
                    {
                        int gapLen = Math.Min(spanStart, lineEnd) - pos;
                        DrawTextRun(ctx, Buffer.GetText(pos, gapLen),
                            col, y, SyntaxTheme.DefaultBrush);
                        col += gapLen;
                        pos += gapLen;
                    }

                    // The classified span (clipped to this line)
                    int drawStart = Math.Max(pos, spanStart);
                    int drawEnd = Math.Min(spanEnd, lineEnd);
                    if (drawEnd > drawStart)
                    {
                        DrawTextRun(ctx, Buffer.GetText(drawStart, drawEnd - drawStart),
                            col + (drawStart - pos), y,
                            SyntaxTheme.GetBrush(span.Classification));
                        col += (drawEnd - pos);
                        pos = drawEnd;
                    }
                    if (spanEnd <= lineEnd) { si++; continue; }
                    else break; // span continues past line end
                }
                else
                {
                    // No more spans on this line – draw remainder as default
                    int rem = lineEnd - pos;
                    DrawTextRun(ctx, Buffer.GetText(pos, rem), col, y, SyntaxTheme.DefaultBrush);
                    pos = lineEnd;
                }
            }
        }
    }

    private void DrawTextRun(DrawingContext ctx, string text, int startCol, double y, IBrush brush)
    {
        if (text.Length == 0) return;
        double x = startCol * CharWidth + LeftPadding - HorizontalOffset;

        var ft = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            s_typeface, EditorFontSize, brush);
        ctx.DrawText(ft, new Point(x, y));
    }

    // ── Indent guides ────────────────────────────────────────────

    private const int IndentSize = 4;

    private void DrawIndentGuides(DrawingContext ctx, int firstLine, int lastLine)
    {
        if (CharWidth <= 0) return;

        int caretLine = Caret != null ? Buffer!.GetLineFromPosition(Caret.Position) : -1;
        int caretIndent = -1;
        if (caretLine >= 0)
        {
            string ctext = Buffer!.GetLineText(caretLine);
            int spaces = 0;
            for (int c = 0; c < ctext.Length; c++)
            {
                if (ctext[c] == ' ') spaces++;
                else if (ctext[c] == '\t') spaces += IndentSize;
                else break;
            }
            caretIndent = spaces / IndentSize;
        }

        for (int i = firstLine; i <= lastLine; i++)
        {
            string text = Buffer!.GetLineText(i);
            int lineIndent = 0;
            int spaceCount = 0;
            for (int c = 0; c < text.Length; c++)
            {
                if (text[c] == ' ') spaceCount++;
                else if (text[c] == '\t') spaceCount += IndentSize;
                else break;
            }
            lineIndent = spaceCount / IndentSize;

            // For blank lines, look at surrounding lines to get indent level
            if (text.Trim().Length == 0 && i > 0 && i < Buffer.LineCount - 1)
            {
                string prev = Buffer.GetLineText(i - 1);
                int ps = 0;
                for (int c = 0; c < prev.Length; c++)
                {
                    if (prev[c] == ' ') ps++;
                    else if (prev[c] == '\t') ps += IndentSize;
                    else break;
                }
                lineIndent = ps / IndentSize;
            }

            double y = i * LineHeight - VerticalOffset;
            for (int g = 1; g <= lineIndent; g++)
            {
                double x = g * IndentSize * CharWidth + LeftPadding - HorizontalOffset;
                var pen = (caretIndent >= 0 && g == caretIndent) ? s_activeIndentPen : s_indentGuidePen;
                ctx.DrawLine(pen, new Point(x, y), new Point(x, y + LineHeight));
            }
        }
    }

    // ── Diagnostic squiggles ────────────────────────────────────

    private void DrawSquiggles(DrawingContext ctx, int firstLine, int lastLine)
    {
        foreach (var diag in Diagnostics!)
        {
            if (diag.Length == 0) continue;
            int diagEnd = diag.StartOffset + diag.Length;

            int startLine = Buffer!.GetLineFromPosition(diag.StartOffset);
            int endLine = Buffer.GetLineFromPosition(diagEnd);

            if (endLine < firstLine || startLine > lastLine) continue;

            var pen = diag.Severity == DiagSeverity.Error ? s_errorSquiggle : s_warningSquiggle;

            for (int line = Math.Max(firstLine, startLine); line <= Math.Min(lastLine, endLine); line++)
            {
                int lineStart = Buffer.GetLineStartOffset(line);
                int lineLen = Buffer.GetLineLength(line);
                int colStart = (line == startLine) ? diag.StartOffset - lineStart : 0;
                int colEnd = (line == endLine) ? diagEnd - lineStart : lineLen;

                double x1 = colStart * CharWidth + LeftPadding - HorizontalOffset;
                double x2 = colEnd * CharWidth + LeftPadding - HorizontalOffset;
                double y = (line + 1) * LineHeight - VerticalOffset - 1;

                // Draw wavy line
                var geo = new StreamGeometry();
                using (var sgc = geo.Open())
                {
                    sgc.BeginFigure(new Point(x1, y), false);
                    double wave = 2;
                    for (double x = x1; x < x2; x += wave * 2)
                    {
                        sgc.LineTo(new Point(x + wave, y - wave));
                        sgc.LineTo(new Point(x + wave * 2, y));
                    }
                    sgc.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
            }
        }
    }

    // ── Bracket highlight ────────────────────────────────────────

    private void DrawBracketHighlight(DrawingContext ctx, int pos)
    {
        if (Buffer == null || pos < 0 || pos >= Buffer.Length) return;
        int line = Buffer.GetLineFromPosition(pos);
        int col = pos - Buffer.GetLineStartOffset(line);
        double x = col * CharWidth + LeftPadding - HorizontalOffset;
        double y = line * LineHeight - VerticalOffset;
        ctx.DrawRectangle(s_bracketBrush, s_bracketPen,
            new Rect(x, y, CharWidth, LineHeight));
    }

    // ── Caret ───────────────────────────────────────────────────

    private void DrawCaret(DrawingContext ctx)
    {
        int caretLine = Buffer!.GetLineFromPosition(Caret!.Position);
        int caretCol = Caret.Position - Buffer.GetLineStartOffset(caretLine);

        double x = caretCol * CharWidth + LeftPadding - HorizontalOffset;
        double y = caretLine * LineHeight - VerticalOffset;

        ctx.DrawLine(
            new Pen(s_caretBrush, 2),
            new Point(x, y),
            new Point(x, y + LineHeight));
    }
}

// ── Placeholder types used by later phases ──────────────────

/// <summary>A contiguous span of text sharing the same syntax classification.</summary>
public readonly struct EditorClassifiedSpan
{
    public int Start { get; init; }
    public int Length { get; init; }
    public string Classification { get; init; }
}
