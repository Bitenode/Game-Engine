using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Extensibility
{
    public enum MenuNodeKind { Root, Menu, Item, Separator }

    public enum MenuItemActionKind
    {
        Command,   // calls CommandRegistry id
        Toggle,    // toggles bool property on Behavior
        Invoke     // calls parameterless method on Behavior
    }

    public sealed class MenuNode
    {
        public MenuNodeKind Kind;
        public string Header; // for Menu/Item
        public List<MenuNode> Children = new List<MenuNode>();

        // For Item:
        public MenuItemActionKind ActionKind;
        public string CommandId;      // when ActionKind==Command
        public string BehaviorType;   // when Toggle/Invoke
        public string MemberName;     // property or method name
    }
}
