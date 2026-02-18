#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.SaveSystem;

namespace Game_Engine.Core.Dialogue
{
    /// <summary>
    /// Persistent key-value store for dialogue variables.
    /// Integrates with the save system for persistence across sessions.
    /// Can also read from an AI Blackboard for runtime integration.
    /// </summary>
    public sealed class DialogueVariableStore : ISaveable
    {
        private readonly Dictionary<string, string> _variables = new();

        /// <summary>ISaveable implementation.</summary>
        public string SaveId => "DialogueVariables";

        /// <summary>Set a variable value.</summary>
        public void Set(string key, string value) => _variables[key] = value;

        /// <summary>Set a bool variable.</summary>
        public void SetBool(string key, bool value) => _variables[key] = value.ToString();

        /// <summary>Set an int variable.</summary>
        public void SetInt(string key, int value) => _variables[key] = value.ToString();

        /// <summary>Get a variable value.</summary>
        public string Get(string key, string defaultValue = "")
            => _variables.TryGetValue(key, out var val) ? val : defaultValue;

        /// <summary>Get a bool variable.</summary>
        public bool GetBool(string key, bool defaultValue = false)
        {
            if (_variables.TryGetValue(key, out var val))
                return bool.TryParse(val, out var b) ? b : defaultValue;
            return defaultValue;
        }

        /// <summary>Get an int variable.</summary>
        public int GetInt(string key, int defaultValue = 0)
        {
            if (_variables.TryGetValue(key, out var val))
                return int.TryParse(val, out var i) ? i : defaultValue;
            return defaultValue;
        }

        /// <summary>Check if a variable exists.</summary>
        public bool Has(string key) => _variables.ContainsKey(key);

        /// <summary>Remove a variable.</summary>
        public bool Remove(string key) => _variables.Remove(key);

        /// <summary>Clear all variables.</summary>
        public void Clear() => _variables.Clear();

        /// <summary>All variable keys.</summary>
        public IEnumerable<string> Keys => _variables.Keys;

        /// <summary>Check a condition: variable equals expected value.</summary>
        public bool CheckCondition(string variable, string expectedValue)
        {
            if (string.IsNullOrEmpty(variable)) return true;
            if (!_variables.TryGetValue(variable, out var actual)) return false;
            if (string.IsNullOrEmpty(expectedValue))
                return !string.IsNullOrEmpty(actual) && actual != "false" && actual != "0";
            return string.Equals(actual, expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        // ── ISaveable ──
        public void OnSave(Dictionary<string, object> data)
        {
            foreach (var (key, value) in _variables)
                data[key] = value;
        }

        public void OnLoad(Dictionary<string, object> data)
        {
            _variables.Clear();
            foreach (var (key, value) in data)
                _variables[key] = value?.ToString() ?? "";
        }
    }
}
