using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Game_Engine.Core.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace Game_Engine.Views;

/// <summary>
/// Full custom code-editor control: gap-buffer model, custom rendering via
/// <see cref="CodeCanvas"/>, undo/redo, keyboard + mouse input, scrolling.
/// </summary>
public class CodeEditorControl : UserControl
{
    // ── Model ───────────────────────────────────────────────────
    private readonly TextBuffer _buffer = new();
    private readonly CaretState _caret = new();
    private readonly UndoStack _undoStack = new();
    private readonly CSharpClassifier _classifier = new();
    private readonly CompletionProvider _completionProvider = new();
    private readonly DiagnosticService _diagnosticService = new();

    // ── Child controls ──────────────────────────────────────────
    private readonly GutterControl _gutter;
    private readonly CodeCanvas _canvas;
    private readonly ScrollBar _vScroll;
    private readonly ScrollBar _hScroll;
    private readonly FindReplaceOverlay _findOverlay;
    private readonly AutoCompletePopup _autoComplete;
    private readonly ImportUsingPopup _importPopup = new();
    private readonly MinimapControl _minimap;
    private readonly DispatcherTimer _importHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Point _lastHoverCanvasPoint;
    private IReadOnlyList<EditorDiagnostic>? _liveDiagnostics;
    private readonly List<EditorDiagnostic> _sortedDiagnosticNav = new();
    private EditorDiagnostic? _pendingImportDiag;
    private int _importPopupAnchorDiagStart = -1;

    // ── State ───────────────────────────────────────────────────
    private bool _isDirty;
    private bool _isMouseSelecting;
    private int _classifyVersion;
    private List<FoldRegion> _foldRegions = new();
    private readonly HashSet<int> _collapsedLines = new();
    private const string IndentToken = "    ";

    // ── Public API ──────────────────────────────────────────────

    /// <summary>Fired after any buffer edit (for dirty tracking in the host window).</summary>
    public event Action? TextModified;

    /// <summary>Fired when the caret moves (for status-bar line/col).</summary>
    public event Action? CaretMoved;

    /// <summary>Fired after background diagnostics run (same payload as canvas underlines).</summary>
    public event Action<IReadOnlyList<EditorDiagnostic>>? DiagnosticsUpdated;
    public event Action<int>? GoToDefinitionRequestedAtOffset;

    public bool IsDirty => _isDirty;
    public TextBuffer Buffer => _buffer;
    public CaretState Caret => _caret;

    /// <summary>Absolute path of the file shown in the host (for multi-file Go to Definition).</summary>
    public string? DocumentPath { get; set; }
    public bool MinimapVisible
    {
        get => _minimap.IsVisible;
        set => _minimap.IsVisible = value;
    }

    public void SetText(string text)
    {
        _buffer.SetText(text);
        _caret.MoveTo(0);
        _undoStack.Clear();
        _isDirty = false;
        _canvas.VerticalOffset = 0;
        _canvas.HorizontalOffset = 0;
        UpdateScrollBars();
        RequestClassification();
        _canvas.InvalidateVisual();
    }

    public string GetText() => _buffer.GetText();

    public void ClearDirty() => _isDirty = false;

    public (int line, int column) GetCaretLineColumn()
        => _buffer.GetLineAndColumn(_caret.Position);

    /// <param name="line1Based">1-based line number (as in compiler output).</param>
    public void GoToLine1Based(int line1Based)
    {
        int line0 = Math.Clamp(line1Based - 1, 0, Math.Max(0, _buffer.LineCount - 1));
        int start = _buffer.GetLineStartOffset(line0);
        _caret.MoveTo(start);
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
        _canvas.InvalidateVisual();
    }

    public async System.Threading.Tasks.Task FormatDocumentAsync()
    {
        var text = GetText();
        if (string.IsNullOrEmpty(text)) return;
        var parseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(text, parseOpts);
        var root = await tree.GetRootAsync();
        using var ws = new AdhocWorkspace();
        var proj = ws.AddProject("FmtScratch", LanguageNames.CSharp);
        var doc = ws.AddDocument(proj.Id, "scratch.cs", SourceText.From(text));
        var docRoot = await doc.GetSyntaxRootAsync();
        if (docRoot == null) return;
        var formatted = Formatter.Format(docRoot, ws);
        SetText(formatted.ToFullString());
    }

    /// <summary>Change code font size (gutter matches). Step typically ±1; clamped 8–40 px.</summary>
    public void AdjustEditorFontSize(int step)
    {
        double next = Math.Clamp(_canvas.EditorFontSize + step, 8, 40);
        if (Math.Abs(next - _canvas.EditorFontSize) < 0.01) return;
        _canvas.EditorFontSize = next;
        _gutter.LineNumberFontSize = next;
        UpdateScrollBars();
        _canvas.InvalidateVisual();
        _gutter.InvalidateVisual();
    }

    public void ResetEditorFontSize()
    {
        _canvas.EditorFontSize = CodeCanvas.DefaultFontSize;
        _gutter.LineNumberFontSize = CodeCanvas.DefaultFontSize;
        UpdateScrollBars();
        _canvas.InvalidateVisual();
        _gutter.InvalidateVisual();
    }

    public void GoToFirstDiagnostic()
    {
        HideImportUsingPopup();
        if (_sortedDiagnosticNav.Count == 0) return;
        JumpToDiagnostic(_sortedDiagnosticNav[0]);
    }

    /// <summary>Next/previous diagnostic: errors first, then warnings, then by source position. Wraps at ends.</summary>
    public void GoToNextDiagnostic(bool previous)
    {
        HideImportUsingPopup();
        if (_sortedDiagnosticNav.Count == 0) return;
        int caret = _caret.Position;
        if (!previous)
        {
            for (int i = 0; i < _sortedDiagnosticNav.Count; i++)
            {
                if (_sortedDiagnosticNav[i].StartOffset > caret)
                {
                    JumpToDiagnostic(_sortedDiagnosticNav[i]);
                    return;
                }
            }
            JumpToDiagnostic(_sortedDiagnosticNav[0]);
        }
        else
        {
            for (int i = _sortedDiagnosticNav.Count - 1; i >= 0; i--)
            {
                if (_sortedDiagnosticNav[i].StartOffset < caret)
                {
                    JumpToDiagnostic(_sortedDiagnosticNav[i]);
                    return;
                }
            }
            JumpToDiagnostic(_sortedDiagnosticNav[^1]);
        }
    }

    void JumpToDiagnostic(EditorDiagnostic d)
    {
        _caret.MoveTo(d.StartOffset);
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
        _canvas.InvalidateVisual();
    }

    static void RebuildSortedDiagnosticsNavList(IReadOnlyList<EditorDiagnostic> src, List<EditorDiagnostic> dest)
    {
        dest.Clear();
        foreach (var d in src) dest.Add(d);
        dest.Sort(CompareDiagnosticNavOrder);
    }

    void RebuildSortedDiagnosticsNav(IReadOnlyList<EditorDiagnostic> diags)
        => RebuildSortedDiagnosticsNavList(diags, _sortedDiagnosticNav);

    static int CompareDiagnosticNavOrder(EditorDiagnostic a, EditorDiagnostic b)
    {
        int oa = a.Severity == DiagSeverity.Error ? 0 : a.Severity == DiagSeverity.Warning ? 1 : 2;
        int ob = b.Severity == DiagSeverity.Error ? 0 : b.Severity == DiagSeverity.Warning ? 1 : 2;
        int c = oa.CompareTo(ob);
        return c != 0 ? c : a.StartOffset.CompareTo(b.StartOffset);
    }

    // ── Constructor ─────────────────────────────────────────────

    public CodeEditorControl()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Background = Brushes.Transparent;

        _canvas = new CodeCanvas { Buffer = _buffer, Caret = _caret };
        _gutter = new GutterControl { Buffer = _buffer, Caret = _caret };
        _findOverlay = new FindReplaceOverlay();
        _findOverlay.Bind(_buffer, _caret);
        _autoComplete = new AutoCompletePopup();
        _minimap = new MinimapControl { Buffer = _buffer };

        _vScroll = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 1, LargeChange = 10,
            Minimum = 0,
        };

        _hScroll = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            SmallChange = 20, LargeChange = 100,
            Minimum = 0,
        };

        BuildVisualTree();
        WireEvents();
        _importHoverTimer.Tick += OnImportHoverTimerTick;
    }

    private void BuildVisualTree()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));  // gutter
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));  // canvas
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));  // minimap
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));  // v-scroll
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(_gutter, 0);
        Grid.SetColumn(_gutter, 0);
        Grid.SetRow(_canvas, 0);
        Grid.SetColumn(_canvas, 1);
        Grid.SetRow(_minimap, 0);
        Grid.SetColumn(_minimap, 2);
        Grid.SetRow(_vScroll, 0);
        Grid.SetColumn(_vScroll, 3);
        Grid.SetRow(_hScroll, 1);
        Grid.SetColumn(_hScroll, 0);
        Grid.SetColumnSpan(_hScroll, 2);

        grid.Children.Add(_gutter);
        grid.Children.Add(_canvas);
        grid.Children.Add(_minimap);
        grid.Children.Add(_vScroll);
        grid.Children.Add(_hScroll);

        // Overlay sits on top of the canvas column
        Grid.SetRow(_findOverlay, 0);
        Grid.SetColumn(_findOverlay, 1);
        grid.Children.Add(_findOverlay);

        // Autocomplete popup (floating)
        Grid.SetRow(_autoComplete, 0);
        Grid.SetColumn(_autoComplete, 1);
        grid.Children.Add(_autoComplete);

        Grid.SetRow(_importPopup, 0);
        Grid.SetColumn(_importPopup, 1);
        _importPopup.ZIndex = 50;
        grid.Children.Add(_importPopup);

        Content = grid;
    }

    private void WireEvents()
    {
        _buffer.TextChanged += () =>
        {
            HideImportUsingPopup();
            UpdateScrollBars();
            RequestClassification();
            _canvas.InvalidateVisual();
        };

        _classifier.ClassificationReady += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _canvas.ClassifiedSpans = _classifier.Spans;
                _canvas.InvalidateVisual();
            });
        };

        _findOverlay.MatchesChanged += () =>
        {
            _canvas.SearchMatches = _findOverlay.Matches;
            _canvas.CurrentMatchIndex = _findOverlay.CurrentMatchIndex;
            _canvas.InvalidateVisual();
        };
        _findOverlay.NavigatedToMatch += pos =>
        {
            EnsureCaretVisible();
            _canvas.SearchMatches = _findOverlay.Matches;
            _canvas.CurrentMatchIndex = _findOverlay.CurrentMatchIndex;
            _canvas.InvalidateVisual();
        };

        _minimap.ScrollRequested += offset =>
        {
            _canvas.VerticalOffset = Math.Clamp(offset, 0, Math.Max(0, _vScroll.Maximum));
            UpdateScrollBars();
            _canvas.InvalidateVisual();
            _minimap.InvalidateVisual();
        };

        _diagnosticService.DiagnosticsReady += diags =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _liveDiagnostics = diags;
                RebuildSortedDiagnosticsNav(diags);
                _canvas.Diagnostics = diags;
                _canvas.InvalidateVisual();
                DiagnosticsUpdated?.Invoke(diags);
            });
        };

        _importPopup.NamespaceChosen += OnImportNamespaceChosen;

        _autoComplete.CompletionCommitted += text =>
        {
            // Replace the partial word with the completion
            int wordStart = FindWordBoundaryBackward(_caret.Position);
            if (wordStart < _caret.Position)
            {
                int len = _caret.Position - wordStart;
                _undoStack.BeginCompound();
                string deleted = _buffer.GetText(wordStart, len);
                _undoStack.PushDelete(wordStart, deleted);
                _buffer.Delete(wordStart, len);
                _caret.MoveTo(wordStart);
                _undoStack.PushInsert(wordStart, text);
                _buffer.Insert(wordStart, text);
                _caret.MoveTo(wordStart + text.Length);
                _undoStack.EndCompound();
            }
            else
            {
                InsertText(text);
            }
            MarkDirty();
            EnsureCaretVisible();
            _canvas.ResetCaretBlink();
        };

        _gutter.FoldToggled += line =>
        {
            var region = _foldRegions.Find(r => r.StartLine == line);
            if (region.LineSpan > 0)
            {
                if (_collapsedLines.Contains(line))
                    _collapsedLines.Remove(line);
                else
                    _collapsedLines.Add(line);

                _canvas.CollapsedRegions = GetCollapsedRegions();
                _gutter.CollapsedLines = _collapsedLines;
                UpdateScrollBars();
                _canvas.InvalidateVisual();
                _gutter.InvalidateVisual();
            }
        };

        _gutter.LineClicked += line =>
        {
            int start = _buffer.GetLineStartOffset(line);
            int end = (line + 1 < _buffer.LineCount)
                ? _buffer.GetLineStartOffset(line + 1)
                : _buffer.Length;
            _caret.AnchorPosition = start;
            _caret.Position = end;
            _caret.DesiredColumn = -1;
            _canvas.ResetCaretBlink();
            _canvas.InvalidateVisual();
            _gutter.InvalidateVisual();
            CaretMoved?.Invoke();
        };

        _vScroll.ValueChanged += (_, e) =>
        {
            _canvas.VerticalOffset = e.NewValue;
            _canvas.InvalidateVisual();
        };

        _hScroll.ValueChanged += (_, e) =>
        {
            _canvas.HorizontalOffset = e.NewValue;
            _canvas.InvalidateVisual();
        };

        // Recalculate scroll bars when the control resizes
        this.GetObservable(BoundsProperty).Subscribe(_ => UpdateScrollBars());
    }

    // ── Layout ──────────────────────────────────────────────────

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _canvas.ResetCaretBlink();
    }

    // ── Text input ──────────────────────────────────────────────

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            char ch = e.Text[0];

            // Skip-over: if typing a closing bracket that was auto-inserted
            if (e.Text.Length == 1 && _caret.Position < _buffer.Length &&
                BracketMatcher.AutoClosePairs.ContainsValue(ch) &&
                _buffer[_caret.Position] == ch)
            {
                _caret.MoveTo(_caret.Position + 1);
                _canvas.ResetCaretBlink();
                _canvas.InvalidateVisual();
                UpdateBracketMatch();
                CaretMoved?.Invoke();
                e.Handled = true;
                return;
            }

            InsertText(e.Text);

            // Auto-close brackets/quotes
            if (e.Text.Length == 1 && BracketMatcher.AutoClosePairs.TryGetValue(ch, out char close))
            {
                // Don't auto-close quotes if preceded by a word character
                if ((ch == '"' || ch == '\'') && _caret.Position >= 2 &&
                    char.IsLetterOrDigit(_buffer[_caret.Position - 2]))
                {
                    // skip
                }
                else
                {
                    int insertPos = _caret.Position;
                    _buffer.Insert(insertPos, close.ToString());
                    // Caret stays between the pair
                }
            }

            UpdateBracketMatch();

            // Trigger autocomplete on '.'
            if (ch == '.')
                TriggerAutoComplete();
            // Update autocomplete filter if visible
            else if (_autoComplete.IsVisible)
            {
                int ws = FindWordBoundaryBackward(_caret.Position);
                string f = ws < _caret.Position ? _buffer.GetText(ws, _caret.Position - ws) : "";
                _autoComplete.UpdateFilter(f);
            }

            e.Handled = true;
        }
    }

    // ── Keyboard ────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl  = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt   = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (alt) { base.OnKeyDown(e); return; }

        if (_importPopup.IsVisible && _importPopup.HandleKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        bool handled = true;

        if (e.Key == Key.Left || e.Key == Key.Right ||
            e.Key == Key.Up   || e.Key == Key.Down)
            HandleArrowKey(e.Key, shift, ctrl);
        else if (e.Key == Key.Home)
            HandleHome(shift, ctrl);
        else if (e.Key == Key.End)
            HandleEnd(shift, ctrl);
        else if (e.Key == Key.PageUp || e.Key == Key.PageDown)
            HandlePageUpDown(e.Key == Key.PageDown, shift);
        else if (e.Key == Key.Back)
            HandleBackspace(ctrl);
        else if (e.Key == Key.Delete)
            HandleDelete(ctrl);
        else if (e.Key == Key.Return || e.Key == Key.Enter)
            HandleEnter();
        else if (e.Key == Key.Tab)
            HandleTab(shift);
        else if (ctrl && e.Key == Key.A)
            HandleSelectAll();
        else if (ctrl && e.Key == Key.C)
            HandleCopy();
        else if (ctrl && e.Key == Key.X)
            HandleCut();
        else if (ctrl && e.Key == Key.V)
            HandlePaste();
        else if (ctrl && shift && e.Key == Key.Z)
            HandleRedo();
        else if (ctrl && e.Key == Key.Z)
            HandleUndo();
        else if (ctrl && e.Key == Key.Y)
            HandleRedo();
        else if (ctrl && e.Key == Key.F)
            _findOverlay.ShowFind();
        else if (ctrl && e.Key == Key.H)
            _findOverlay.ShowReplace();
        else if (e.Key == Key.F3)
        {
            if (shift) _findOverlay.NavigatePrev();
            else _findOverlay.NavigateNext();
        }
        else if (e.Key == Key.Escape && _findOverlay.IsVisible)
            _findOverlay.Hide();
        else if (e.Key == Key.Escape && _importPopup.IsVisible)
            HideImportUsingPopup();
        else if (e.Key == Key.Escape && _autoComplete.IsVisible)
            _autoComplete.Hide();
        else if (ctrl && e.Key == Key.Space)
            TriggerAutoComplete();
        else if (ctrl && e.Key == Key.D)
            HandleDuplicateLine();
        else if (ctrl && e.Key == Key.OemQuestion)
            HandleToggleComment();
        else if (ctrl && e.Key == Key.G)
            HandleGotoLine();
        else if (ctrl && e.Key == Key.L)
            HandleSelectLine();
        else if (ctrl && shift && e.Key == Key.K)
            HandleDeleteLine();
        else if (_autoComplete.IsVisible && _autoComplete.HandleKey(e.Key))
            { /* handled by popup */ }
        else
            handled = false;

        if (handled) e.Handled = true;
        else base.OnKeyDown(e);
    }

    // ── Mouse ───────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!SourceIsInsideImportPopup(e.Source))
            HideImportUsingPopup();

        var point = e.GetPosition(_canvas);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        int pos = HitTestPosition(point);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        var clickCount = e.ClickCount;
        if (shift && clickCount == 1)
        {
            // Match common IDE behavior: Shift+Click attempts go-to-definition.
            _caret.MoveTo(pos, extending: false);
            _caret.DesiredColumn = -1;
            _canvas.ResetCaretBlink();
            CaretMoved?.Invoke();
            GoToDefinitionRequestedAtOffset?.Invoke(pos);
            e.Handled = true;
            return;
        }

        if (clickCount == 2)
        {
            // Double-click: select word
            var (start, end) = GetWordAt(pos);
            _caret.AnchorPosition = start;
            _caret.Position = end;
        }
        else if (clickCount >= 3)
        {
            // Triple-click: select line
            int line = _buffer.GetLineFromPosition(pos);
            _caret.AnchorPosition = _buffer.GetLineStartOffset(line);
            int endOff = (line + 1 < _buffer.LineCount)
                ? _buffer.GetLineStartOffset(line + 1)
                : _buffer.Length;
            _caret.Position = endOff;
        }
        else
        {
            _caret.MoveTo(pos, extending: shift);
        }

        _caret.DesiredColumn = -1;
        _isMouseSelecting = true;
        e.Pointer.Capture(this);
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(_canvas);
        if (_isMouseSelecting)
        {
            int pos = HitTestPosition(point);

            _caret.Position = pos;
            _caret.DesiredColumn = -1;

            EnsureCaretVisible();
            _canvas.InvalidateVisual();
            CaretMoved?.Invoke();
            HideImportUsingPopup();
            return;
        }

        UpdateImportHover(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isMouseSelecting)
        {
            _isMouseSelecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (SourceIsInsideImportPopup(e.Source))
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            AdjustEditorFontSize(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        HideImportUsingPopup();
        base.OnPointerWheelChanged(e);
        _canvas.MeasureCharSize();
        double delta = e.Delta.Y * _canvas.LineHeight * 3;
        _canvas.VerticalOffset = Math.Clamp(
            _canvas.VerticalOffset - delta,
            0,
            Math.Max(0, _vScroll.Maximum));
        UpdateScrollBars();
        _canvas.InvalidateVisual();
        e.Handled = true;
    }

    // ── Arrow-key navigation ────────────────────────────────────

    private void HandleArrowKey(Key key, bool shift, bool ctrl)
    {
        int pos = _caret.Position;
        var (line, col) = _buffer.GetLineAndColumn(pos);

        switch (key)
        {
            case Key.Left:
                if (!shift && _caret.HasSelection)
                { pos = _caret.SelectionStart; }
                else if (ctrl)
                { pos = FindWordBoundaryBackward(pos); }
                else if (pos > 0)
                {
                    pos--;
                    if (pos > 0 && _buffer[pos] == '\n' && _buffer[pos - 1] == '\r')
                        pos--;
                }
                _caret.DesiredColumn = -1;
                break;

            case Key.Right:
                if (!shift && _caret.HasSelection)
                { pos = _caret.SelectionEnd; }
                else if (ctrl)
                { pos = FindWordBoundaryForward(pos); }
                else if (pos < _buffer.Length)
                {
                    pos++;
                    if (pos < _buffer.Length && _buffer[pos - 1] == '\r' && _buffer[pos] == '\n')
                        pos++;
                }
                _caret.DesiredColumn = -1;
                break;

            case Key.Up:
                if (line > 0)
                {
                    int dc = _caret.DesiredColumn >= 0 ? _caret.DesiredColumn : col;
                    _caret.DesiredColumn = dc;
                    int nl = line - 1;
                    pos = _buffer.GetPosition(nl, Math.Min(dc, _buffer.GetLineLength(nl)));
                }
                break;

            case Key.Down:
                if (line < _buffer.LineCount - 1)
                {
                    int dc = _caret.DesiredColumn >= 0 ? _caret.DesiredColumn : col;
                    _caret.DesiredColumn = dc;
                    int nl = line + 1;
                    pos = _buffer.GetPosition(nl, Math.Min(dc, _buffer.GetLineLength(nl)));
                }
                break;
        }

        _caret.MoveTo(pos, extending: shift);
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        UpdateBracketMatch();
        CaretMoved?.Invoke();
    }

    private void HandleHome(bool shift, bool ctrl)
    {
        int pos;
        if (ctrl)
        {
            pos = 0;
        }
        else
        {
            int line = _buffer.GetLineFromPosition(_caret.Position);
            int lineStart = _buffer.GetLineStartOffset(line);
            string text = _buffer.GetLineText(line);
            int firstNonSpace = 0;
            while (firstNonSpace < text.Length && (text[firstNonSpace] == ' ' || text[firstNonSpace] == '\t'))
                firstNonSpace++;

            int currentCol = _caret.Position - lineStart;
            pos = (currentCol == firstNonSpace) ? lineStart : lineStart + firstNonSpace;
        }

        _caret.MoveTo(pos, extending: shift);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
    }

    private void HandleEnd(bool shift, bool ctrl)
    {
        int pos;
        if (ctrl)
        {
            pos = _buffer.Length;
        }
        else
        {
            int line = _buffer.GetLineFromPosition(_caret.Position);
            pos = _buffer.GetLineStartOffset(line) + _buffer.GetLineLength(line);
        }

        _caret.MoveTo(pos, extending: shift);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
    }

    private void HandlePageUpDown(bool down, bool shift)
    {
        var (line, col) = _buffer.GetLineAndColumn(_caret.Position);
        int visibleLines = Math.Max(1, (int)(_canvas.Bounds.Height / _canvas.LineHeight));
        int dc = _caret.DesiredColumn >= 0 ? _caret.DesiredColumn : col;
        _caret.DesiredColumn = dc;

        int nl = down
            ? Math.Min(_buffer.LineCount - 1, line + visibleLines)
            : Math.Max(0, line - visibleLines);

        int pos = _buffer.GetPosition(nl, Math.Min(dc, _buffer.GetLineLength(nl)));
        _caret.MoveTo(pos, extending: shift);
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
    }

    // ── Edit helpers ────────────────────────────────────────────

    private void InsertText(string text)
    {
        bool hadSel = _caret.HasSelection;
        if (hadSel) _undoStack.BeginCompound();

        if (hadSel)
        {
            int start = _caret.SelectionStart;
            string deleted = _buffer.GetText(start, _caret.SelectionLength);
            _undoStack.PushDelete(start, deleted);
            _buffer.Delete(start, _caret.SelectionLength);
            _caret.MoveTo(start);
        }

        _undoStack.PushInsert(_caret.Position, text);
        _buffer.Insert(_caret.Position, text);
        _caret.MoveTo(_caret.Position + text.Length);

        if (hadSel) _undoStack.EndCompound();

        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    private void DeleteSelection()
    {
        if (!_caret.HasSelection) return;
        int start = _caret.SelectionStart;
        int length = _caret.SelectionLength;
        string deleted = _buffer.GetText(start, length);
        _undoStack.PushDelete(start, deleted);
        _buffer.Delete(start, length);
        _caret.MoveTo(start);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    private void HandleBackspace(bool ctrl)
    {
        if (_caret.HasSelection) { DeleteSelection(); return; }
        if (_caret.Position == 0) return;

        int deleteStart;
        if (ctrl)
        {
            deleteStart = FindWordBoundaryBackward(_caret.Position);
        }
        else
        {
            deleteStart = _caret.Position - 1;
            if (deleteStart > 0 && _buffer[deleteStart] == '\n' && _buffer[deleteStart - 1] == '\r')
                deleteStart--;
        }

        int len = _caret.Position - deleteStart;
        string deleted = _buffer.GetText(deleteStart, len);
        _undoStack.PushDelete(deleteStart, deleted);
        _buffer.Delete(deleteStart, len);
        _caret.MoveTo(deleteStart);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    private void HandleDelete(bool ctrl)
    {
        if (_caret.HasSelection) { DeleteSelection(); return; }
        if (_caret.Position >= _buffer.Length) return;

        int deleteEnd;
        if (ctrl)
        {
            deleteEnd = FindWordBoundaryForward(_caret.Position);
        }
        else
        {
            deleteEnd = _caret.Position + 1;
            if (_caret.Position < _buffer.Length - 1 &&
                _buffer[_caret.Position] == '\r' && _buffer[_caret.Position + 1] == '\n')
                deleteEnd++;
        }

        int len = deleteEnd - _caret.Position;
        string deleted = _buffer.GetText(_caret.Position, len);
        _undoStack.PushDelete(_caret.Position, deleted);
        _buffer.Delete(_caret.Position, len);
        _caret.DesiredColumn = -1;
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    private void HandleEnter()
    {
        int currentLine = _buffer.GetLineFromPosition(_caret.Position);
        string indent = GetLineIndentation(currentLine);
        InsertText("\n" + indent);
    }

    private void HandleTab(bool shift)
    {
        if (!_caret.HasSelection)
        {
            if (shift)
                OutdentLineRaw(_buffer.GetLineFromPosition(_caret.Position));
            else
                InsertText(IndentToken);
            return;
        }

        int startLine = _buffer.GetLineFromPosition(_caret.SelectionStart);
        int endLine = _buffer.GetLineFromPosition(_caret.SelectionEnd);

        _undoStack.BeginCompound();

        for (int i = startLine; i <= endLine; i++)
        {
            if (shift) OutdentLineRaw(i);
            else IndentLineRaw(i);
        }

        _undoStack.EndCompound();

        _caret.AnchorPosition = _buffer.GetLineStartOffset(startLine);
        int endOff = _buffer.GetLineStartOffset(endLine) + _buffer.GetLineLength(endLine);
        _caret.Position = endOff;

        MarkDirty();
        _canvas.ResetCaretBlink();
        EnsureCaretVisible();
        CaretMoved?.Invoke();
    }

    private void IndentLineRaw(int line)
    {
        int lineStart = _buffer.GetLineStartOffset(line);
        _undoStack.PushInsert(lineStart, IndentToken);
        _buffer.Insert(lineStart, IndentToken);
    }

    private void OutdentLineRaw(int line)
    {
        int lineStart = _buffer.GetLineStartOffset(line);
        string text = _buffer.GetLineText(line);

        int remove = 0;
        if (text.Length > 0 && text[0] == '\t')
            remove = 1;
        else
            while (remove < IndentToken.Length && remove < text.Length && text[remove] == ' ')
                remove++;

        if (remove == 0) return;

        string deleted = _buffer.GetText(lineStart, remove);
        _undoStack.PushDelete(lineStart, deleted);
        _buffer.Delete(lineStart, remove);
    }

    private void HandleSelectAll()
    {
        _caret.SelectAll(_buffer.Length);
        _canvas.InvalidateVisual();
        CaretMoved?.Invoke();
    }

    // ── Clipboard ───────────────────────────────────────────────

    private async void HandleCopy()
    {
        if (!_caret.HasSelection) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string text = _buffer.GetText(_caret.SelectionStart, _caret.SelectionLength);
        await clipboard.SetTextAsync(text);
    }

    private async void HandleCut()
    {
        if (!_caret.HasSelection) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string text = _buffer.GetText(_caret.SelectionStart, _caret.SelectionLength);
        await clipboard.SetTextAsync(text);
        DeleteSelection();
    }

    private async void HandlePaste()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string? text = await clipboard.GetTextAsync();
        if (!string.IsNullOrEmpty(text))
            InsertText(text);
    }

    // ── Undo / Redo ─────────────────────────────────────────────

    private void HandleUndo()
    {
        _undoStack.Undo(_buffer, _caret);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        _canvas.InvalidateVisual();
        CaretMoved?.Invoke();
    }

    private void HandleRedo()
    {
        _undoStack.Redo(_buffer, _caret);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        _canvas.InvalidateVisual();
        CaretMoved?.Invoke();
    }

    // ── Extra editing shortcuts ────────────────────────────────

    private void HandleDuplicateLine()
    {
        int line = _buffer.GetLineFromPosition(_caret.Position);
        int lineStart = _buffer.GetLineStartOffset(line);
        string text = _buffer.GetLineText(line);
        string newline = (lineStart + text.Length < _buffer.Length) ? "\n" : "";
        string insert = newline + text;
        int insertPos = lineStart + text.Length;

        // If we're not at the end, skip the newline char
        if (insertPos < _buffer.Length)
        {
            char ch = _buffer[insertPos];
            if (ch == '\r' && insertPos + 1 < _buffer.Length && _buffer[insertPos + 1] == '\n')
                insertPos += 2;
            else if (ch == '\n')
                insertPos += 1;
            insert = text + "\n";
        }
        else
        {
            insert = "\n" + text;
        }

        _undoStack.PushInsert(insertPos, insert);
        _buffer.Insert(insertPos, insert);
        _caret.MoveTo(_caret.Position + text.Length + 1);
        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    private void HandleToggleComment()
    {
        int startLine, endLine;
        if (_caret.HasSelection)
        {
            startLine = _buffer.GetLineFromPosition(_caret.SelectionStart);
            endLine = _buffer.GetLineFromPosition(_caret.SelectionEnd);
        }
        else
        {
            startLine = endLine = _buffer.GetLineFromPosition(_caret.Position);
        }

        // Check if all lines are already commented
        bool allCommented = true;
        for (int i = startLine; i <= endLine; i++)
        {
            string txt = _buffer.GetLineText(i).TrimStart();
            if (txt.Length > 0 && !txt.StartsWith("//"))
            {
                allCommented = false;
                break;
            }
        }

        _undoStack.BeginCompound();

        for (int i = startLine; i <= endLine; i++)
        {
            int ls = _buffer.GetLineStartOffset(i);
            string txt = _buffer.GetLineText(i);

            if (allCommented)
            {
                // Remove comment prefix
                int idx = txt.IndexOf("//");
                if (idx >= 0)
                {
                    int removeLen = (idx + 2 < txt.Length && txt[idx + 2] == ' ') ? 3 : 2;
                    string deleted = _buffer.GetText(ls + idx, removeLen);
                    _undoStack.PushDelete(ls + idx, deleted);
                    _buffer.Delete(ls + idx, removeLen);
                }
            }
            else
            {
                // Find first non-whitespace and insert "// "
                int firstNonSpace = 0;
                while (firstNonSpace < txt.Length && (txt[firstNonSpace] == ' ' || txt[firstNonSpace] == '\t'))
                    firstNonSpace++;
                _undoStack.PushInsert(ls + firstNonSpace, "// ");
                _buffer.Insert(ls + firstNonSpace, "// ");
            }
        }

        _undoStack.EndCompound();
        MarkDirty();
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        CaretMoved?.Invoke();
    }

    private async void HandleGotoLine()
    {
        // Simple goto-line dialog
        var dialog = new Avalonia.Controls.Window
        {
            Title = "Go to Line",
            Width = 300,
            Height = 130,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var input = new Avalonia.Controls.TextBox
        {
            Watermark = $"Line number (1–{_buffer.LineCount})",
            Margin = new Thickness(12, 12, 12, 8),
        };

        var okBtn = new Avalonia.Controls.Button
        {
            Content = "Go",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Width = 60,
        };

        var panel = new Avalonia.Controls.StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(okBtn);
        dialog.Content = panel;

        int targetLine = -1;
        okBtn.Click += (_, _) =>
        {
            if (int.TryParse(input.Text, out int n))
            {
                targetLine = Math.Clamp(n - 1, 0, _buffer.LineCount - 1);
            }
            dialog.Close();
        };

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                if (int.TryParse(input.Text, out int n))
                    targetLine = Math.Clamp(n - 1, 0, _buffer.LineCount - 1);
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Avalonia.Controls.Window parentWin)
            await dialog.ShowDialog(parentWin);
        else
            dialog.Show();

        if (targetLine >= 0)
        {
            int pos = _buffer.GetLineStartOffset(targetLine);
            _caret.MoveTo(pos);
            _caret.DesiredColumn = -1;
            EnsureCaretVisible();
            _canvas.ResetCaretBlink();
            CaretMoved?.Invoke();
        }
    }

    private void HandleSelectLine()
    {
        int line = _buffer.GetLineFromPosition(_caret.Position);
        int start = _buffer.GetLineStartOffset(line);
        int end = (line + 1 < _buffer.LineCount)
            ? _buffer.GetLineStartOffset(line + 1)
            : _buffer.Length;
        _caret.AnchorPosition = start;
        _caret.Position = end;
        _caret.DesiredColumn = -1;
        _canvas.ResetCaretBlink();
        _canvas.InvalidateVisual();
        _gutter.InvalidateVisual();
        CaretMoved?.Invoke();
    }

    private void HandleDeleteLine()
    {
        int line = _buffer.GetLineFromPosition(_caret.Position);
        int start = _buffer.GetLineStartOffset(line);
        int end;
        if (line + 1 < _buffer.LineCount)
            end = _buffer.GetLineStartOffset(line + 1);
        else if (line > 0)
        {
            // Last line: also remove the preceding newline
            start = _buffer.GetLineStartOffset(line);
            int prevEnd = _buffer.GetLineStartOffset(line - 1) + _buffer.GetLineLength(line - 1);
            start = prevEnd;
            end = _buffer.Length;
        }
        else
            end = _buffer.Length;

        if (end > start)
        {
            string deleted = _buffer.GetText(start, end - start);
            _undoStack.PushDelete(start, deleted);
            _buffer.Delete(start, end - start);
            _caret.MoveTo(Math.Min(start, _buffer.Length));
        }

        _caret.DesiredColumn = -1;
        EnsureCaretVisible();
        _canvas.ResetCaretBlink();
        MarkDirty();
        CaretMoved?.Invoke();
    }

    // ── Word boundary helpers ───────────────────────────────────

    private static int CharClass(char c)
    {
        if (char.IsLetterOrDigit(c) || c == '_') return 1;
        if (char.IsWhiteSpace(c)) return 0;
        return 2;
    }

    private int FindWordBoundaryForward(int pos)
    {
        if (pos >= _buffer.Length) return _buffer.Length;
        int cls = CharClass(_buffer[pos]);
        while (pos < _buffer.Length && CharClass(_buffer[pos]) == cls) pos++;
        return pos;
    }

    private int FindWordBoundaryBackward(int pos)
    {
        if (pos <= 0) return 0;
        pos--;
        int cls = CharClass(_buffer[pos]);
        if (cls == 0)
        {
            while (pos > 0 && CharClass(_buffer[pos - 1]) == 0) pos--;
            if (pos == 0) return 0;
            pos--;
            cls = CharClass(_buffer[pos]);
        }
        while (pos > 0 && CharClass(_buffer[pos - 1]) == cls) pos--;
        return pos;
    }

    private (int start, int end) GetWordAt(int pos)
    {
        if (_buffer.Length == 0) return (0, 0);
        pos = Math.Clamp(pos, 0, _buffer.Length - 1);
        char c = _buffer[pos];
        int cls = CharClass(c);

        int start = pos;
        while (start > 0 && CharClass(_buffer[start - 1]) == cls) start--;
        int end = pos;
        while (end < _buffer.Length && CharClass(_buffer[end]) == cls) end++;
        return (start, end);
    }

    private string GetLineIndentation(int line)
    {
        string text = _buffer.GetLineText(line);
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return text[..i];
    }

    // ── Scroll helpers ──────────────────────────────────────────

    private void SyncGutter()
    {
        _gutter.LineHeight = _canvas.LineHeight;
        _gutter.CharWidth = _canvas.CharWidth;
        _gutter.LineNumberFontSize = _canvas.EditorFontSize;
        _gutter.VerticalOffset = _canvas.VerticalOffset;
        _gutter.Width = _gutter.ComputeDesiredWidth();
        _gutter.InvalidateVisual();
    }

    private void SyncMinimap()
    {
        _minimap.VerticalOffset = _canvas.VerticalOffset;
        _minimap.ViewportHeight = _canvas.Bounds.Height;
        _minimap.FullDocumentHeight = _buffer.LineCount * _canvas.LineHeight;
        _minimap.ClassifiedSpans = _canvas.ClassifiedSpans;
        _minimap.InvalidateVisual();
    }

    private void UpdateScrollBars()
    {
        _canvas.MeasureCharSize();
        double lh = _canvas.LineHeight;
        double cw = _canvas.CharWidth;
        if (lh <= 0 || cw <= 0) return;

        SyncGutter();
        SyncMinimap();

        double totalH = _buffer.LineCount * lh;
        double viewH = _canvas.Bounds.Height;
        _vScroll.Maximum = Math.Max(0, totalH - viewH);
        _vScroll.ViewportSize = viewH;

        double maxW = 0;
        for (int i = 0; i < _buffer.LineCount; i++)
        {
            double w = _buffer.GetLineLength(i) * cw + CodeCanvas.LeftPadding * 2;
            if (w > maxW) maxW = w;
        }
        double viewW = _canvas.Bounds.Width;
        _hScroll.Maximum = Math.Max(0, maxW - viewW);
        _hScroll.ViewportSize = viewW;
    }

    private void EnsureCaretVisible()
    {
        _canvas.MeasureCharSize();
        double lh = _canvas.LineHeight;
        double cw = _canvas.CharWidth;
        if (lh <= 0 || cw <= 0) return;

        var (line, col) = _buffer.GetLineAndColumn(_caret.Position);
        double caretY = line * lh;
        double caretX = col * cw + CodeCanvas.LeftPadding;

        if (caretY < _canvas.VerticalOffset)
            _canvas.VerticalOffset = caretY;
        else if (caretY + lh > _canvas.VerticalOffset + _canvas.Bounds.Height)
            _canvas.VerticalOffset = caretY + lh - _canvas.Bounds.Height;

        if (caretX < _canvas.HorizontalOffset + CodeCanvas.LeftPadding)
            _canvas.HorizontalOffset = Math.Max(0, caretX - 20);
        else if (caretX > _canvas.HorizontalOffset + _canvas.Bounds.Width - 20)
            _canvas.HorizontalOffset = caretX - _canvas.Bounds.Width + 40;

        _canvas.VerticalOffset = Math.Max(0, _canvas.VerticalOffset);
        _canvas.HorizontalOffset = Math.Max(0, _canvas.HorizontalOffset);
        UpdateScrollBars();
        _canvas.InvalidateVisual();
    }

    private int HitTestPosition(Point point)
    {
        _canvas.MeasureCharSize();
        double lh = _canvas.LineHeight;
        double cw = _canvas.CharWidth;
        if (lh <= 0 || cw <= 0) return 0;

        int line = (int)((point.Y + _canvas.VerticalOffset) / lh);
        line = Math.Clamp(line, 0, _buffer.LineCount - 1);

        int col = (int)Math.Round((point.X + _canvas.HorizontalOffset - CodeCanvas.LeftPadding) / cw);
        col = Math.Clamp(col, 0, _buffer.GetLineLength(line));

        return _buffer.GetLineStartOffset(line) + col;
    }

    // ── Hover: add missing using ───────────────────────────────

    void HideImportUsingPopup()
    {
        _importPopup.Hide();
        _importHoverTimer.Stop();
        _pendingImportDiag = null;
        _importPopupAnchorDiagStart = -1;
    }

    void OnImportNamespaceChosen(string ns)
    {
        var src = _buffer.GetText();
        if (!UsingImportQuickFix.TryBuildInsertion(src, ns, out var off, out var ins))
            return;

        _undoStack.BeginCompound();
        _undoStack.PushInsert(off, ins);
        _buffer.Insert(off, ins);
        _undoStack.EndCompound();

        if (_caret.Position >= off)
            _caret.MoveTo(_caret.Position + ins.Length);

        MarkDirty();
        RequestClassification();
        EnsureCaretVisible();
        _canvas.InvalidateVisual();
        HideImportUsingPopup();
    }

    EditorDiagnostic? FindImportDiagnosticAt(int offset)
    {
        if (_liveDiagnostics == null) return null;
        foreach (var d in _liveDiagnostics)
        {
            if (offset < d.StartOffset || offset >= d.StartOffset + d.Length) continue;
            if (!UsingImportQuickFix.IsImportRelatedDiagnostic(in d)) continue;
            return d;
        }
        return null;
    }

    bool SourceIsInsideImportPopup(object? src)
    {
        if (src is not Visual v) return false;
        if (ReferenceEquals(v, _importPopup)) return true;
        foreach (var a in v.GetVisualAncestors())
            if (ReferenceEquals(a, _importPopup)) return true;
        return false;
    }

    void UpdateImportHover(PointerEventArgs e)
    {
        if (SourceIsInsideImportPopup(e.Source))
        {
            _importHoverTimer.Stop();
            return;
        }

        var canvasPoint = e.GetPosition(_canvas);
        _lastHoverCanvasPoint = canvasPoint;

        bool overCanvas = canvasPoint.X >= 0 && canvasPoint.Y >= 0 &&
                          canvasPoint.X <= _canvas.Bounds.Width && canvasPoint.Y <= _canvas.Bounds.Height;
        if (!overCanvas)
        {
            _importHoverTimer.Stop();
            _pendingImportDiag = null;
            if (!_importPopup.IsVisible)
                Cursor = new Cursor(StandardCursorType.Ibeam);
            return;
        }

        int pos = HitTestPosition(canvasPoint);
        var diag = FindImportDiagnosticAt(pos);
        if (diag == null)
        {
            _importHoverTimer.Stop();
            _pendingImportDiag = null;
            if (!_importPopup.IsVisible)
                Cursor = new Cursor(StandardCursorType.Ibeam);
            else
                HideImportUsingPopup();
            return;
        }

        Cursor = new Cursor(StandardCursorType.Hand);
        var d = diag.Value;
        if (_importPopup.IsVisible && _importPopupAnchorDiagStart == d.StartOffset)
            return;

        _pendingImportDiag = d;
        _importHoverTimer.Stop();
        _importHoverTimer.Start();
    }

    void OnImportHoverTimerTick(object? sender, EventArgs e)
    {
        _importHoverTimer.Stop();
        var diag = _pendingImportDiag;
        if (diag == null) return;

        int pos = HitTestPosition(_lastHoverCanvasPoint);
        var cur = FindImportDiagnosticAt(pos);
        if (cur == null || cur.Value.StartOffset != diag.Value.StartOffset || cur.Value.Length != diag.Value.Length)
            return;

        var anchor = diag.Value;
        var src = _buffer.GetText();
        _ = Task.Run(() => UsingImportQuickFix.SuggestNamespaces(src, anchor))
            .ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                var list = t.Result;
                if (list.Count == 0) return;
                Dispatcher.UIThread.Post(() =>
                {
                    int pos2 = HitTestPosition(_lastHoverCanvasPoint);
                    var cur2 = FindImportDiagnosticAt(pos2);
                    if (cur2 == null || cur2.Value.StartOffset != anchor.StartOffset) return;

                    _autoComplete.Hide();
                    _canvas.MeasureCharSize();
                    var (line, col) = _buffer.GetLineAndColumn(anchor.StartOffset);
                    double x = col * _canvas.CharWidth + CodeCanvas.LeftPadding - _canvas.HorizontalOffset;
                    double y = (line + 1) * _canvas.LineHeight - _canvas.VerticalOffset;
                    _importPopup.Show(list, new Point(x, y));
                    _importPopupAnchorDiagStart = anchor.StartOffset;
                });
            }, TaskScheduler.Default);
    }

    // ── Autocomplete ─────────────────────────────────────────────

    private async void TriggerAutoComplete()
    {
        _canvas.MeasureCharSize();
        var text = _buffer.GetText();
        var items = await _completionProvider.GetCompletionsAsync(text, _caret.Position);
        if (items.Count == 0) { _autoComplete.Hide(); return; }

        // Calculate popup position near caret
        var (line, col) = _buffer.GetLineAndColumn(_caret.Position);
        double x = col * _canvas.CharWidth + CodeCanvas.LeftPadding - _canvas.HorizontalOffset;
        double y = (line + 1) * _canvas.LineHeight - _canvas.VerticalOffset;

        // Get the partial word for filtering
        int wordStart = FindWordBoundaryBackward(_caret.Position);
        string filter = wordStart < _caret.Position
            ? _buffer.GetText(wordStart, _caret.Position - wordStart)
            : "";

        _autoComplete.Show(items, filter, new Avalonia.Point(x, y));
    }

    // ── Bracket matching ─────────────────────────────────────────

    private void UpdateBracketMatch()
    {
        var (p1, p2) = BracketMatcher.FindMatch(_buffer, _caret.Position);
        _canvas.BracketPos1 = p1;
        _canvas.BracketPos2 = p2;
    }

    // ── Syntax classification ────────────────────────────────────

    private void RequestClassification()
    {
        _classifyVersion++;
        var text = _buffer.GetText();
        _classifier.UpdateText(text, _classifyVersion);
        _diagnosticService.UpdateSource(text, DocumentPath);

        // Update fold regions (runs on bg thread via Task.Run for large files)
        System.Threading.Tasks.Task.Run(() =>
        {
            var regions = FoldingProvider.GetFoldRegions(text);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _foldRegions = regions;
                _gutter.FoldRegions = regions;
                _gutter.CollapsedLines = _collapsedLines;
                _gutter.InvalidateVisual();
            });
        });
    }

    private List<(int startLine, int endLine)> GetCollapsedRegions()
    {
        var result = new List<(int, int)>();
        foreach (var line in _collapsedLines)
        {
            var region = _foldRegions.Find(r => r.StartLine == line);
            if (region.LineSpan > 0)
                result.Add((region.StartLine, region.EndLine));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    // ── Dirty tracking ──────────────────────────────────────────

    private void MarkDirty()
    {
        _isDirty = true;
        TextModified?.Invoke();
    }
}
