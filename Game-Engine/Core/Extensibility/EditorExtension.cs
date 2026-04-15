using System;

namespace Game_Engine.Core.Extensibility
{
    /// Users extend this class in their own scripts to add menus/UI to the editor.
    public abstract class EditorExtension
    {
        /// Called when the editor is ready to collect contributions.
        /// Use the provided UI builder to declare menus and items.
        public abstract void Contribute(EditorUI ui);

        /// <summary>
        /// Called before this instance is discarded (project closed, scripts recompiled, or extension refresh).
        /// Override to unsubscribe from events or release resources.
        /// </summary>
        public virtual void Dispose() { }
    }
}
