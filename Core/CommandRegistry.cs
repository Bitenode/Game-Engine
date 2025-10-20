using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core
{
    public static class CommandRegistry
    {
        public sealed class Command
        {
            public string Id;
            public string DisplayName;
            public Func<bool> CanExecute;
            public Action Execute;
            internal bool IsFromExtension;
        }

        private static readonly Dictionary<string, Command> _map =
            new(StringComparer.OrdinalIgnoreCase);

        // snapshot of commands that belong to the host app
        private static readonly HashSet<string> _builtins =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool _sealedBuiltins;

        public static void Register(string id, string displayName, Action exec, Func<bool> canExec = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(nameof(id));

            _map[id] = new Command
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                Execute = exec ?? (() => { }),
                CanExecute = canExec ?? (() => true)
            };

            // while not sealed, every registration is considered a builtin
            if (!_sealedBuiltins) _builtins.Add(id);
        }

        public static Command TryGet(string id)
            => string.IsNullOrWhiteSpace(id) ? null : (_map.TryGetValue(id, out var c) ? c : null);

        /// Call once after your app registers its own commands (before loading extensions).
        public static void SealBuiltins() => _sealedBuiltins = true;

        /// Remove commands that were added by extensions (everything not in the builtin snapshot).
        public static void ClearExtensions()
        {
            var toRemove = _map.Keys.Where(k => !_builtins.Contains(k)).ToList();
            foreach (var k in toRemove) _map.Remove(k);
        }

        // remove commands matching a predicate (e.g., all extension-owned)
        public static void UnregisterWhere(Func<Command, bool> predicate)
        {
            if (predicate == null) return;
            var toRemove = _map.Where(kvp => predicate(kvp.Value))
                               .Select(kvp => kvp.Key)
                               .ToList();
            foreach (var k in toRemove) _map.Remove(k);
        }
    }
}
