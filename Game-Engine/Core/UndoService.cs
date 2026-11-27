using System;
using System.Collections.Generic;
using System.Reflection;

namespace Game_Engine.Core
{
    public interface ICmd { void Do(); void Undo(); }

    /// <summary>Delegate-backed undoable command with a human-readable label.</summary>
    public sealed class ActionCmd : ICmd
    {
        private readonly Action _undo;
        private readonly Action _redo;

        public string Label { get; }

        public ActionCmd(string label, Action undo, Action redo)
        {
            Label = string.IsNullOrWhiteSpace(label) ? "Edit" : label;
            _undo = undo ?? throw new ArgumentNullException(nameof(undo));
            _redo = redo ?? throw new ArgumentNullException(nameof(redo));
        }

        public void Do() => _redo();
        public void Undo() => _undo();
    }

    /// <summary>Groups multiple commands into a single atomic Undo/Redo step.</summary>
    public sealed class CompoundCmd : ICmd
    {
        private readonly List<ICmd> _commands;

        public string Label { get; }
        public IReadOnlyList<ICmd> Commands => _commands;

        public CompoundCmd(string label, IEnumerable<ICmd> commands)
        {
            Label = string.IsNullOrWhiteSpace(label) ? "Group Edit" : label;
            _commands = new List<ICmd>(commands);
        }

        public void Do()
        {
            for (int i = 0; i < _commands.Count; i++)
                _commands[i].Do();
        }

        public void Undo()
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Undo();
        }
    }

    public static class UndoService
    {
        private readonly struct UndoEntry
        {
            public ICmd Command { get; }
            public string Label { get; }

            public UndoEntry(ICmd command, string label)
            {
                Command = command;
                Label = label;
            }
        }

        private sealed class GroupScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                EndGroup();
            }
        }

        static readonly LinkedList<UndoEntry> _undo = new();
        static readonly LinkedList<UndoEntry> _redo = new();

        static int _maxHistory = 128;
        static int _groupDepth;
        static string _groupLabel = "Edit";
        static readonly List<ICmd> _groupCommands = new();

        static UndoService()
        {
            SceneService.SceneReplaced += ClearSilent;
        }

        /// <summary>Maximum number of undo entries kept in history (default 128).</summary>
        public static int MaxHistory
        {
            get => _maxHistory;
            set
            {
                _maxHistory = Math.Max(1, value);
                TrimUndoStack();
            }
        }

        /// <summary>True while an Undo, Redo, or command execution is in progress (prevents re-entrant recording).</summary>
        public static bool IsExecuting { get; private set; }

        /// <summary>True if at least one command can be undone.</summary>
        public static bool CanUndo => _undo.Count > 0;

        /// <summary>True if at least one command can be redone.</summary>
        public static bool CanRedo => _redo.Count > 0;

        /// <summary>Number of entries currently on the undo stack.</summary>
        public static int UndoCount => _undo.Count;

        /// <summary>Number of entries currently on the redo stack.</summary>
        public static int RedoCount => _redo.Count;

        /// <summary>Label of the next command that <see cref="Undo"/> will revert, or null.</summary>
        public static string? UndoLabel => _undo.Last?.Value.Label;

        /// <summary>Label of the next command that <see cref="Redo"/> will re-apply, or null.</summary>
        public static string? RedoLabel => _redo.Last?.Value.Label;

        /// <summary>Fired whenever the undo/redo stacks change.</summary>
        public static event Action? Changed;

        static void TrimUndoStack()
        {
            while (_undo.Count > _maxHistory)
                _undo.RemoveFirst();
        }

        static string InferLabel(ICmd c, string? explicitLabel)
        {
            if (!string.IsNullOrWhiteSpace(explicitLabel))
                return explicitLabel!;
            if (c is ActionCmd ac) return ac.Label;
            if (c is CompoundCmd cc) return cc.Label;
            return c.GetType().Name.EndsWith("Cmd", StringComparison.Ordinal)
                ? c.GetType().Name[..^3]
                : c.GetType().Name;
        }

        static void RefreshUI()
        {
#if !PLAYER
            // Re-evaluate bindings (inspector) and repaint (scene)
            SelectionService.Touch();
#endif
            SceneService.NotifyChanged();
            Changed?.Invoke();
        }

        static void PushEntry(ICmd c, string? label)
        {
            if (_groupDepth > 0)
            {
                _groupCommands.Add(c);
                _redo.Clear();
                return;
            }

            _undo.AddLast(new UndoEntry(c, InferLabel(c, label)));
            TrimUndoStack();
            _redo.Clear();
            RefreshUI();
        }

        /// <summary>Begin a compound undo transaction. Nested calls collapse into the outermost group.</summary>
        public static void BeginGroup(string label)
        {
            if (_groupDepth == 0)
            {
                _groupLabel = string.IsNullOrWhiteSpace(label) ? "Edit" : label;
                _groupCommands.Clear();
            }
            _groupDepth++;
        }

        /// <summary>End the current compound undo transaction. Outermost close commits all grouped commands as one step.</summary>
        public static void EndGroup()
        {
            if (_groupDepth <= 0) return;
            _groupDepth--;
            if (_groupDepth > 0) return;

            if (_groupCommands.Count == 0)
                return;

            ICmd cmd = _groupCommands.Count == 1
                ? _groupCommands[0]
                : new CompoundCmd(_groupLabel, _groupCommands);
            _groupCommands.Clear();

            _undo.AddLast(new UndoEntry(cmd, _groupLabel));
            TrimUndoStack();
            _redo.Clear();
            RefreshUI();
        }

        /// <summary>Disposable helper for compound transactions: <c>using (UndoService.Group("Delete Objects")) { ... }</c>.</summary>
        public static IDisposable Group(string label)
        {
            BeginGroup(label);
            return new GroupScope();
        }

        /// <summary>Execute <paramref name="c"/> and push it onto the undo stack.</summary>
        public static void Exec(ICmd c) => Exec(c, null);

        /// <summary>Execute <paramref name="c"/> with a display label and push it onto the undo stack.</summary>
        public static void Exec(ICmd c, string? label)
        {
            if (c == null || IsExecuting) return;

            IsExecuting = true;
            try
            {
                c.Do();                 // value is already set by binding, but calling Do() is harmless
            }
            finally
            {
                IsExecuting = false;
            }

            PushEntry(c, label);
        }

        /// <summary>Record an already-applied command onto the undo stack without invoking <see cref="ICmd.Do"/>.</summary>
        public static void Record(ICmd c, string? label = null)
        {
            if (c == null || IsExecuting) return;
            PushEntry(c, label);
        }

        /// <summary>
        /// Record a delegate-backed undo/redo action.
        /// Set <paramref name="executeNow"/> to true to invoke <paramref name="redo"/> immediately inside the execution guard.
        /// </summary>
        public static void RecordAction(string label, Action undo, Action redo, bool executeNow = false)
        {
            if (IsExecuting) return;
            var cmd = new ActionCmd(label, undo, redo);
            if (executeNow)
                Exec(cmd, label);
            else
                Record(cmd, label);
        }

        /// <summary>Record a reflected property change as an undoable <see cref="PropertyChangeCmd"/>.</summary>
        public static void RecordPropertyChange(object target, PropertyInfo prop, object? oldValue, object? newValue, string? label = null)
        {
            if (target == null || prop == null || IsExecuting) return;
            var cmd = new PropertyChangeCmd(target, prop, oldValue, newValue);
            Exec(cmd, label ?? $"Edit {prop.Name}");
        }

        public static void Undo()
        {
            if (_undo.Count == 0 || IsExecuting) return;
            var entry = _undo.Last!.Value;
            _undo.RemoveLast();

            IsExecuting = true;
            try
            {
                entry.Command.Undo();
            }
            finally
            {
                IsExecuting = false;
            }

            _redo.AddLast(entry);
            RefreshUI();
        }

        public static void Redo()
        {
            if (_redo.Count == 0 || IsExecuting) return;
            var entry = _redo.Last!.Value;
            _redo.RemoveLast();

            IsExecuting = true;
            try
            {
                entry.Command.Do();
            }
            finally
            {
                IsExecuting = false;
            }

            _undo.AddLast(entry);
            TrimUndoStack();
            RefreshUI();
        }

        public static void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _groupDepth = 0;
            _groupCommands.Clear();
            RefreshUI();
        }

        private static void ClearSilent()
        {
            _undo.Clear();
            _redo.Clear();
            _groupDepth = 0;
            _groupCommands.Clear();
            Changed?.Invoke();
        }
    }
}
