using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Game_Engine.Core.Extensibility
{
    /// Fluent API used by extensions to declare menus.
    public sealed class MenuBuilder
    {
        private readonly Dictionary<string, MenuNode> _menus =
            new Dictionary<string, MenuNode>(StringComparer.OrdinalIgnoreCase);

        internal IEnumerable<MenuNode> TopLevelMenus => _menus.Values;

        public MenuBuilder Menu(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException(nameof(title));
            MenuNode node;
            if (!_menus.TryGetValue(title, out node))
            {
                node = new MenuNode { Kind = MenuNodeKind.Menu, Header = title };
                _menus[title] = node;
            }
            _currentStack.Clear();
            _currentStack.Add(node);
            return this;
        }

        // ----- Submenu / Items -------------------------------------------------

        public MenuBuilder Submenu(string header)
        {
            var sub = new MenuNode { Kind = MenuNodeKind.Menu, Header = header };
            Current.Children.Add(sub);
            _currentStack.Add(sub);
            return this;
        }

        public MenuBuilder EndSubmenu()
        {
            if (_currentStack.Count > 1) _currentStack.RemoveAt(_currentStack.Count - 1);
            return this;
        }

        public MenuBuilder Separator()
        {
            Current.Children.Add(new MenuNode { Kind = MenuNodeKind.Separator });
            return this;
        }

        public MenuBuilder Command(string header, string commandId)
        {
            Current.Children.Add(new MenuNode
            {
                Kind = MenuNodeKind.Item,
                Header = header,
                ActionKind = MenuItemActionKind.Command,
                CommandId = commandId
            });
            return this;
        }

        public MenuBuilder Toggle<TBehavior>(string header, string boolProperty)
        {
            Current.Children.Add(new MenuNode
            {
                Kind = MenuNodeKind.Item,
                Header = header,
                ActionKind = MenuItemActionKind.Toggle,
                BehaviorType = typeof(TBehavior).FullName ?? typeof(TBehavior).Name,
                MemberName = boolProperty
            });
            return this;
        }

        public MenuBuilder Invoke<TBehavior>(string header, string methodName)
        {
            Current.Children.Add(new MenuNode
            {
                Kind = MenuNodeKind.Item,
                Header = header,
                ActionKind = MenuItemActionKind.Invoke,
                BehaviorType = typeof(TBehavior).FullName ?? typeof(TBehavior).Name,
                MemberName = methodName
            });
            return this;
        }

        // ----- Helpers ---------------------------------------------------------

        private readonly List<MenuNode> _currentStack = new List<MenuNode>();

        private MenuNode Current
        {
            get
            {
                if (_currentStack.Count == 0)
                    throw new InvalidOperationException("Call Menu(title) first.");
                return _currentStack[_currentStack.Count - 1];
            }
        }
    }
}
