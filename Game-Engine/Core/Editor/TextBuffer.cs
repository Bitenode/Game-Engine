using System;
using System.Collections.Generic;
using System.Text;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Gap-buffer backed text storage with efficient insert/delete at the cursor
/// and O(log n) line lookup via a sorted line-start list.
/// </summary>
public sealed class TextBuffer
{
    private char[] _data;
    private int _gapStart;
    private int _gapEnd;
    private readonly List<int> _lineStarts = new() { 0 };

    private const int InitialGapSize = 256;

    public TextBuffer(string text = "")
    {
        text ??= "";
        _data = new char[text.Length + InitialGapSize];
        if (text.Length > 0)
            text.CopyTo(0, _data, 0, text.Length);
        _gapStart = text.Length;
        _gapEnd = _data.Length;
        RebuildLineStarts();
    }

    // ── Properties ──────────────────────────────────────────────

    public int Length => _data.Length - GapSize;
    private int GapSize => _gapEnd - _gapStart;
    public int LineCount => _lineStarts.Count;

    /// <summary>Fired after every Insert / Delete / SetText.</summary>
    public event Action? TextChanged;

    // ── Indexer ─────────────────────────────────────────────────

    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _data[index < _gapStart ? index : index + GapSize];
        }
    }

    // ── Bulk text access ────────────────────────────────────────

    public string GetText()
    {
        var sb = new StringBuilder(Length);
        if (_gapStart > 0) sb.Append(_data, 0, _gapStart);
        if (_gapEnd < _data.Length) sb.Append(_data, _gapEnd, _data.Length - _gapEnd);
        return sb.ToString();
    }

    public string GetText(int start, int length)
    {
        if (length == 0) return string.Empty;
        if (start < 0 || length < 0 || start + length > Length)
            throw new ArgumentOutOfRangeException();

        int end = start + length;

        if (end <= _gapStart)
            return new string(_data, start, length);

        if (start >= _gapStart)
            return new string(_data, start + GapSize, length);

        // Spans the gap
        var buf = new char[length];
        int before = _gapStart - start;
        Array.Copy(_data, start, buf, 0, before);
        Array.Copy(_data, _gapEnd, buf, before, length - before);
        return new string(buf);
    }

    // ── Line queries ────────────────────────────────────────────

    public int GetLineStartOffset(int line)
    {
        if (line < 0 || line >= _lineStarts.Count)
            throw new ArgumentOutOfRangeException(nameof(line));
        return _lineStarts[line];
    }

    public int GetLineEndOffset(int line)
    {
        if (line < 0 || line >= _lineStarts.Count)
            throw new ArgumentOutOfRangeException(nameof(line));

        if (line + 1 < _lineStarts.Count)
        {
            int nextStart = _lineStarts[line + 1];
            // back up past the line-ending that created that next-line start
            if (nextStart > 0 && this[nextStart - 1] == '\n')
                return (nextStart > 1 && this[nextStart - 2] == '\r')
                    ? nextStart - 2
                    : nextStart - 1;
            return nextStart;
        }
        return Length;
    }

    public int GetLineLength(int line)
        => GetLineEndOffset(line) - GetLineStartOffset(line);

    public string GetLineText(int line)
    {
        int start = GetLineStartOffset(line);
        int len = GetLineLength(line);
        return len > 0 ? GetText(start, len) : string.Empty;
    }

    /// <summary>Binary-search for the line containing <paramref name="position"/>.</summary>
    public int GetLineFromPosition(int position)
    {
        position = Math.Clamp(position, 0, Length);
        int lo = 0, hi = _lineStarts.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_lineStarts[mid] <= position) lo = mid + 1;
            else hi = mid - 1;
        }
        return Math.Max(0, lo - 1);
    }

    public int GetColumnFromPosition(int position)
    {
        int line = GetLineFromPosition(position);
        return position - _lineStarts[line];
    }

    public (int line, int column) GetLineAndColumn(int position)
    {
        int line = GetLineFromPosition(position);
        return (line, position - _lineStarts[line]);
    }

    public int GetPosition(int line, int column)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        int lineStart = _lineStarts[line];
        int lineLen = GetLineLength(line);
        column = Math.Clamp(column, 0, lineLen);
        return lineStart + column;
    }

    // ── Edit operations ─────────────────────────────────────────

    public void Insert(int position, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (position < 0 || position > Length)
            throw new ArgumentOutOfRangeException(nameof(position));

        MoveGapTo(position);
        EnsureGapCapacity(text.Length);
        text.CopyTo(0, _data, _gapStart, text.Length);
        _gapStart += text.Length;

        RebuildLineStarts();
        TextChanged?.Invoke();
    }

    public string Delete(int position, int length)
    {
        if (length == 0) return string.Empty;
        if (position < 0 || length < 0 || position + length > Length)
            throw new ArgumentOutOfRangeException();

        string deleted = GetText(position, length);
        MoveGapTo(position);
        _gapEnd += length;

        RebuildLineStarts();
        TextChanged?.Invoke();
        return deleted;
    }

    public void SetText(string text)
    {
        text ??= "";
        _data = new char[text.Length + InitialGapSize];
        if (text.Length > 0)
            text.CopyTo(0, _data, 0, text.Length);
        _gapStart = text.Length;
        _gapEnd = _data.Length;
        RebuildLineStarts();
        TextChanged?.Invoke();
    }

    // ── Gap-buffer internals ────────────────────────────────────

    private void MoveGapTo(int position)
    {
        if (position == _gapStart) return;

        if (position < _gapStart)
        {
            int count = _gapStart - position;
            Array.Copy(_data, position, _data, _gapEnd - count, count);
            _gapStart = position;
            _gapEnd -= count;
        }
        else
        {
            int count = position - _gapStart;
            Array.Copy(_data, _gapEnd, _data, _gapStart, count);
            _gapStart += count;
            _gapEnd += count;
        }
    }

    private void EnsureGapCapacity(int required)
    {
        if (GapSize >= required) return;

        int newSize = Math.Max(_data.Length * 2, _data.Length + required);
        var newData = new char[newSize];

        Array.Copy(_data, 0, newData, 0, _gapStart);
        int afterLen = _data.Length - _gapEnd;
        int newGapEnd = newData.Length - afterLen;
        Array.Copy(_data, _gapEnd, newData, newGapEnd, afterLen);

        _gapEnd = newGapEnd;
        _data = newData;
    }

    private void RebuildLineStarts()
    {
        _lineStarts.Clear();
        _lineStarts.Add(0);

        int len = Length;
        for (int i = 0; i < len; i++)
        {
            char c = this[i];
            if (c == '\r')
            {
                if (i + 1 < len && this[i + 1] == '\n') i++;
                _lineStarts.Add(i + 1);
            }
            else if (c == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }
}
