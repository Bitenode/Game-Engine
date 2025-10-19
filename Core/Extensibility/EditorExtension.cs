using System;

namespace Game_Engine.Core.Extensibility
{
    /// Users extend this class in their own scripts to add menus/UI to the editor.
    public abstract class EditorExtension
    {
        /// Called when the editor is ready to collect contributions.
        /// Use the provided UI builder to declare menus and items.
        public abstract void Contribute(EditorUI ui);
    }
}
