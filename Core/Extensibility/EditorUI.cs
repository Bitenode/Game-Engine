using System;
using System.Reflection.Emit;

namespace Game_Engine.Core.Extensibility
{
    /// Lightweight facade passed to extensions so they can declare menus.
    public sealed class EditorUI
    {
        private readonly MenuBuilder _root;

        internal EditorUI(MenuBuilder root)
        {
            _root = root;
        }

        /// Start (or get) a top-level menu (e.g., "Tools", "Custom", "AI")
        public MenuBuilder Menu(string title)
        {
            return _root.Menu(title);
        }
    }
}
