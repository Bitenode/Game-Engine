using System.Collections.Generic;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Finds the matching bracket/brace/paren for a given position.
/// Uses a stack-based scan that respects nesting.
/// </summary>
public static class BracketMatcher
{
    private static readonly Dictionary<char, char> s_openToClose = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
    };

    private static readonly Dictionary<char, char> s_closeToOpen = new()
    {
        [')'] = '(',
        [']'] = '[',
        ['}'] = '{',
    };

    /// <summary>Auto-close pairs: typing the key inserts the value after the caret.</summary>
    public static readonly Dictionary<char, char> AutoClosePairs = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        ['"'] = '"',
        ['\''] = '\'',
    };

    /// <summary>
    /// Try to find the position of the matching bracket for the character
    /// at <paramref name="position"/> or just before it.
    /// Returns (bracketPos, matchPos) if found, or (-1, -1) if not.
    /// </summary>
    public static (int bracketPos, int matchPos) FindMatch(TextBuffer buffer, int position)
    {
        // Check character at position (right of caret)
        if (position < buffer.Length)
        {
            char c = buffer[position];
            if (s_openToClose.TryGetValue(c, out char close))
                return (position, ScanForward(buffer, position, c, close));
            if (s_closeToOpen.TryGetValue(c, out char open))
                return (position, ScanBackward(buffer, position, c, open));
        }

        // Check character just before position (left of caret)
        if (position > 0)
        {
            char c = buffer[position - 1];
            if (s_openToClose.TryGetValue(c, out char close))
                return (position - 1, ScanForward(buffer, position - 1, c, close));
            if (s_closeToOpen.TryGetValue(c, out char open))
                return (position - 1, ScanBackward(buffer, position - 1, c, open));
        }

        return (-1, -1);
    }

    private static int ScanForward(TextBuffer buffer, int start, char open, char close)
    {
        int depth = 0;
        for (int i = start; i < buffer.Length; i++)
        {
            char c = buffer[i];
            if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static int ScanBackward(TextBuffer buffer, int start, char close, char open)
    {
        int depth = 0;
        for (int i = start; i >= 0; i--)
        {
            char c = buffer[i];
            if (c == close) depth++;
            else if (c == open) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
