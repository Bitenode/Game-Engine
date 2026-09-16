
using System.Reflection;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// Generic property change command (works for any Behavior, Transform, etc.).
    public sealed class PropertyChangeCmd : ICmd
    {
        readonly object _target;
        readonly PropertyInfo _prop;
        readonly object? _oldValue;
        readonly object? _newValue;

        public PropertyChangeCmd(object target, PropertyInfo prop, object? oldValue, object? newValue)
        {
            _target = target;
            _prop = prop;
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public void Do() => Apply(_newValue);
        public void Undo() => Apply(_oldValue);

        void Apply(object? value)
        {
            _prop.SetValue(_target, CloneForAssign(_prop.PropertyType, value));
            NotifyTarget(_target, _prop.Name);
            if (_target is Behavior b && b.gameObject != null && Prefab.IsPrefabInstance(b.gameObject))
                Prefab.RecordOverride(b.gameObject, b.GetType().Name + "." + _prop.Name, value);
            else if (_target is GameObject go && Prefab.IsPrefabInstance(go))
                Prefab.RecordOverride(go, "GameObject." + _prop.Name, value);
            else if (_target is Transform tr && tr.gameObject != null && Prefab.IsPrefabInstance(tr.gameObject))
                Prefab.RecordOverride(tr.gameObject, "Transform." + _prop.Name, value);
        }

        static void NotifyTarget(object target, string name)
        {
            switch (target)
            {
                case ObservableObject o:
                    o.NotifyPropertyChanged(name);
                    break;
                case GameObject go:
                    go.NotifyPropertyChanged(name);
                    break;
            }
        }

        // Create a safe instance for assignment when needed (e.g., mutable classes).
        static object? CloneForAssign(Type t, object? v)
        {
            if (v is null) return null;

            // Deep-copy your Core.Vector3 class (so undo/redo doesn't share the same instance).
            if (t == typeof(Game_Engine.Core.Vector3) && v is Game_Engine.Core.Vector3 vv)
                return new Game_Engine.Core.Vector3(vv.X, vv.Y, vv.Z);

            // Most other types (Color struct, enums, numbers, Mesh reference) can be assigned directly.
            return v;
        }
    }
}

