using System;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Tracks caret position, selection anchor, and the "desired column"
/// used to preserve horizontal intent during vertical navigation.
/// </summary>
public sealed class CaretState
{
    public int Position { get; set; }
    public int AnchorPosition { get; set; }

    public bool HasSelection => Position != AnchorPosition;
    public int SelectionStart => Math.Min(Position, AnchorPosition);
    public int SelectionEnd => Math.Max(Position, AnchorPosition);
    public int SelectionLength => SelectionEnd - SelectionStart;

    /// <summary>
    /// Preserved column for Up/Down arrow keys.
    /// Reset to -1 on any horizontal movement.
    /// </summary>
    public int DesiredColumn { get; set; } = -1;

    public void MoveTo(int position, bool extending = false)
    {
        Position = position;
        if (!extending) AnchorPosition = position;
    }

    public void SelectAll(int documentLength)
    {
        AnchorPosition = 0;
        Position = documentLength;
    }

    public void ClearSelection()
    {
        AnchorPosition = Position;
    }
}
