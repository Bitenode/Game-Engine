using System;
using System.Collections.Generic;
using System.Reflection;

namespace Game_Engine.Core
{
    public interface ICmd { void Do(); void Undo(); }

    public static class UndoService
    {
        static readonly Stack<ICmd> _undo = new();
        static readonly Stack<ICmd> _redo = new();

        static void RefreshUI()
        {
            // Re-evaluate bindings (inspector) and repaint (scene)
            SelectionService.Touch();
            SceneService.NotifyChanged();
        }

        public static void Exec(ICmd c)
        {
            c.Do();                 // value is already set by binding, but calling Do() is harmless
            _undo.Push(c);
            _redo.Clear();
            RefreshUI();
        }

        public static void Undo()
        {
            if (_undo.Count == 0) return;
            var c = _undo.Pop();
            c.Undo();
            _redo.Push(c);
            RefreshUI();
        }

        public static void Redo()
        {
            if (_redo.Count == 0) return;
            var c = _redo.Pop();
            c.Do();
            _undo.Push(c);
            RefreshUI();
        }

        public static void Clear()
        {
            _undo.Clear(); _redo.Clear();
            RefreshUI();
        }
    }
}