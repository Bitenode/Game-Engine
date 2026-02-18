#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.AI
{
    /// <summary>
    /// Key-value data store for sharing state between behavior tree nodes.
    /// Each AI agent has its own Blackboard instance.
    /// </summary>
    public sealed class Blackboard
    {
        private readonly Dictionary<string, object> _data = new();

        /// <summary>Set a value by key.</summary>
        public void Set<T>(string key, T value)
        {
            _data[key] = value!;
        }

        /// <summary>Get a value by key. Returns default if not found or wrong type.</summary>
        public T Get<T>(string key, T defaultValue = default!)
        {
            if (_data.TryGetValue(key, out var val) && val is T typed)
                return typed;
            return defaultValue;
        }

        /// <summary>Check if a key exists.</summary>
        public bool Has(string key) => _data.ContainsKey(key);

        /// <summary>Remove a key.</summary>
        public bool Remove(string key) => _data.Remove(key);

        /// <summary>Clear all data.</summary>
        public void Clear() => _data.Clear();

        /// <summary>Get a float value (convenience helper).</summary>
        public float GetFloat(string key, float defaultValue = 0f)
            => Get(key, defaultValue);

        /// <summary>Get an int value (convenience helper).</summary>
        public int GetInt(string key, int defaultValue = 0)
            => Get(key, defaultValue);

        /// <summary>Get a bool value (convenience helper).</summary>
        public bool GetBool(string key, bool defaultValue = false)
            => Get(key, defaultValue);

        /// <summary>Get a string value (convenience helper).</summary>
        public string GetString(string key, string defaultValue = "")
            => Get(key, defaultValue);

        /// <summary>Get a Vector3 value (convenience helper).</summary>
        public System.Numerics.Vector3 GetVector3(string key, System.Numerics.Vector3 defaultValue = default)
            => Get(key, defaultValue);

        /// <summary>Get all keys in the blackboard.</summary>
        public IEnumerable<string> Keys => _data.Keys;

        /// <summary>Get a count of all entries.</summary>
        public int Count => _data.Count;
    }
}
