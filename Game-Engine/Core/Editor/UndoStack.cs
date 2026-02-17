using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Editor;

public readonly record struct UndoAction(bool IsInsert, int Position, string Text);

public sealed class UndoGroup
{
    public List<UndoAction> Actions { get; } = new();
}

/// <summary>
/// Undo / redo stack with time-based coalescing (groups consecutive
/// small edits of the same kind) and explicit compound-edit support.
/// </summary>
public sealed class UndoStack
{
    private readonly List<UndoGroup> _undos = new();
    private readonly List<UndoGroup> _redos = new();
    private UndoGroup? _open;
    private DateTime _lastEdit = DateTime.MinValue;
    private bool _compound;

    private const double CoalesceMs = 400;

    public bool CanUndo => _undos.Count > 0 || (_open?.Actions.Count > 0);
    public bool CanRedo => _redos.Count > 0;

    // ── Compound edits (e.g. replace-selection = delete + insert) ──

    public void BeginCompound()
    {
        FinalizeOpen();
        _open = new UndoGroup();
        _compound = true;
    }

    public void EndCompound()
    {
        _compound = false;
        // leave _open alive so it can still coalesce with the next keystroke
    }

    // ── Record edits ────────────────────────────────────────────

    public void PushInsert(int position, string text)
        => Push(new UndoAction(true, position, text));

    public void PushDelete(int position, string text)
        => Push(new UndoAction(false, position, text));

    private void Push(UndoAction action)
    {
        _redos.Clear();

        if (_compound)
        {
            _open ??= new UndoGroup();
            _open.Actions.Add(action);
            return;
        }

        var now = DateTime.UtcNow;
        bool coalesce = _open != null
            && _open.Actions.Count > 0
            && (now - _lastEdit).TotalMilliseconds < CoalesceMs
            && _open.Actions[^1].IsInsert == action.IsInsert
            && action.Text.Length <= 2;

        if (!coalesce)
            FinalizeOpen();

        _open ??= new UndoGroup();
        _open.Actions.Add(action);
        _lastEdit = now;
    }

    private void FinalizeOpen()
    {
        if (_open?.Actions.Count > 0)
            _undos.Add(_open);
        _open = null;
    }

    // ── Undo / Redo ─────────────────────────────────────────────

    public void Undo(TextBuffer buffer, CaretState caret)
    {
        FinalizeOpen();
        if (_undos.Count == 0) return;

        var group = _undos[^1];
        _undos.RemoveAt(_undos.Count - 1);

        var redo = new UndoGroup();

        for (int i = group.Actions.Count - 1; i >= 0; i--)
        {
            var a = group.Actions[i];
            if (a.IsInsert)
            {
                buffer.Delete(a.Position, a.Text.Length);
                caret.MoveTo(a.Position);
            }
            else
            {
                buffer.Insert(a.Position, a.Text);
                caret.MoveTo(a.Position + a.Text.Length);
            }
            redo.Actions.Insert(0, a);
        }

        _redos.Add(redo);
    }

    public void Redo(TextBuffer buffer, CaretState caret)
    {
        if (_redos.Count == 0) return;

        var group = _redos[^1];
        _redos.RemoveAt(_redos.Count - 1);

        var undo = new UndoGroup();

        foreach (var a in group.Actions)
        {
            if (a.IsInsert)
            {
                buffer.Insert(a.Position, a.Text);
                caret.MoveTo(a.Position + a.Text.Length);
            }
            else
            {
                buffer.Delete(a.Position, a.Text.Length);
                caret.MoveTo(a.Position);
            }
            undo.Actions.Add(a);
        }

        _undos.Add(undo);
    }

    public void Clear()
    {
        _undos.Clear();
        _redos.Clear();
        _open = null;
    }
}
