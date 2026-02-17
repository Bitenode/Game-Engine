using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

public partial class FindReplaceOverlay : UserControl
{
    private TextBuffer? _buffer;
    private CaretState? _caret;
    private List<(int start, int length)> _matches = new();
    private int _currentMatch = -1;

    public event Action? MatchesChanged;
    public event Action<int>? NavigatedToMatch;

    public IReadOnlyList<(int start, int length)> Matches => _matches;
    public int CurrentMatchIndex => _currentMatch;

    public FindReplaceOverlay()
    {
        InitializeComponent();

        FindBox.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        FindBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) PerformSearch();
        };

        BtnNext.Click += (_, _) => NavigateNext();
        BtnPrev.Click += (_, _) => NavigatePrev();
        BtnReplace.Click += (_, _) => ReplaceCurrent();
        BtnReplaceAll.Click += (_, _) => ReplaceAll();
        BtnCloseFind.Click += (_, _) => Hide();

        BtnCase.Click += (_, _) => PerformSearch();
        BtnRegex.Click += (_, _) => PerformSearch();
    }

    public void Bind(TextBuffer buffer, CaretState caret)
    {
        _buffer = buffer;
        _caret = caret;
    }

    /// <summary>Show as Find mode (Ctrl+F).</summary>
    public void ShowFind()
    {
        IsVisible = true;
        ReplaceRow.IsVisible = false;
        ReplaceBox.IsVisible = false;
        FindBox.Focus();
        FindBox.SelectAll();
        PerformSearch();
    }

    /// <summary>Show as Find+Replace mode (Ctrl+H).</summary>
    public void ShowReplace()
    {
        IsVisible = true;
        ReplaceRow.IsVisible = true;
        ReplaceBox.IsVisible = true;
        FindBox.Focus();
        FindBox.SelectAll();
        PerformSearch();
    }

    public new void Hide()
    {
        IsVisible = false;
        _matches.Clear();
        _currentMatch = -1;
        MatchesChanged?.Invoke();
    }

    // ── Search ──────────────────────────────────────────────────

    public void PerformSearch()
    {
        _matches.Clear();
        _currentMatch = -1;

        if (_buffer == null || string.IsNullOrEmpty(FindBox.Text))
        {
            MatchCount.Text = "0 results";
            MatchesChanged?.Invoke();
            return;
        }

        string text = _buffer.GetText();
        string pattern = FindBox.Text ?? "";
        bool caseSensitive = BtnCase.IsChecked == true;
        bool useRegex = BtnRegex.IsChecked == true;

        try
        {
            if (useRegex)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                foreach (Match m in Regex.Matches(text, pattern, options))
                {
                    if (m.Length > 0) _matches.Add((m.Index, m.Length));
                }
            }
            else
            {
                var comparison = caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                int idx = 0;
                while (idx <= text.Length - pattern.Length)
                {
                    int found = text.IndexOf(pattern, idx, comparison);
                    if (found < 0) break;
                    _matches.Add((found, pattern.Length));
                    idx = found + 1;
                }
            }
        }
        catch { } // Bad regex

        MatchCount.Text = _matches.Count == 0 ? "No results" : $"{_matches.Count} results";

        // Jump to nearest match from caret
        if (_matches.Count > 0 && _caret != null)
        {
            int pos = _caret.Position;
            _currentMatch = 0;
            for (int i = 0; i < _matches.Count; i++)
            {
                if (_matches[i].start >= pos) { _currentMatch = i; break; }
            }
        }

        MatchesChanged?.Invoke();
    }

    public void NavigateNext()
    {
        if (_matches.Count == 0) return;
        _currentMatch = (_currentMatch + 1) % _matches.Count;
        JumpToCurrentMatch();
    }

    public void NavigatePrev()
    {
        if (_matches.Count == 0) return;
        _currentMatch = (_currentMatch - 1 + _matches.Count) % _matches.Count;
        JumpToCurrentMatch();
    }

    private void JumpToCurrentMatch()
    {
        if (_currentMatch < 0 || _currentMatch >= _matches.Count) return;
        var (start, length) = _matches[_currentMatch];
        if (_caret != null)
        {
            _caret.AnchorPosition = start;
            _caret.Position = start + length;
        }
        MatchCount.Text = $"{_currentMatch + 1} of {_matches.Count}";
        NavigatedToMatch?.Invoke(start);
    }

    // ── Replace ─────────────────────────────────────────────────

    private void ReplaceCurrent()
    {
        if (_buffer == null || _matches.Count == 0 || _currentMatch < 0) return;
        var (start, length) = _matches[_currentMatch];
        string replacement = ReplaceBox.Text ?? "";

        _buffer.Delete(start, length);
        _buffer.Insert(start, replacement);

        PerformSearch();
    }

    private void ReplaceAll()
    {
        if (_buffer == null || _matches.Count == 0) return;
        string replacement = ReplaceBox.Text ?? "";

        // Replace from end to start to preserve offsets
        for (int i = _matches.Count - 1; i >= 0; i--)
        {
            var (start, length) = _matches[i];
            _buffer.Delete(start, length);
            _buffer.Insert(start, replacement);
        }

        PerformSearch();
    }

    // ── Keyboard shortcuts within find box ──────────────────────

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || e.Key == Key.F3)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                NavigatePrev();
            else
                NavigateNext();
            e.Handled = true;
        }
    }
}
