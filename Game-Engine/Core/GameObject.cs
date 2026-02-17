using System.Collections.ObjectModel;
using System.ComponentModel;
using Game_Engine.Core.Component;

namespace Game_Engine.Core;

public class GameObject : INotifyPropertyChanged
{
    string _name;
    GameObject? _parent;
    bool _enabled = true;

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    /// <summary>
    /// Enable or disable this GameObject. Disabled GameObjects (and all their children)
    /// are skipped during Update/Render. The Hierarchy shows them in red.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                OnChanged(nameof(Enabled));
                OnChanged(nameof(IsActiveInHierarchy));
                PropagateActiveChanged();
                SceneService.NotifyChanged();
            }
        }
    }

    /// <summary>
    /// True only when this GameObject AND every ancestor is enabled.
    /// </summary>
    public bool IsActiveInHierarchy
    {
        get
        {
            if (!_enabled) return false;
            return _parent?.IsActiveInHierarchy ?? true;
        }
    }

    /// Notify this object and all descendants that the effective active state may have changed.
    void PropagateActiveChanged()
    {
        foreach (var child in Children)
        {
            child.OnChanged(nameof(IsActiveInHierarchy));
            child.PropagateActiveChanged();
        }
    }

    public GameObject? Parent
    {
        get => _parent;
        private set
        {
            if (_parent != value)
            {
                _parent = value;
                OnChanged(nameof(Parent));
                OnChanged(nameof(IsActiveInHierarchy));
                PropagateActiveChanged();
            }
        }
    }

    public ObservableCollection<GameObject> Children { get; } = new();
    public ObservableCollection<Behavior> Behaviors { get; } = new();

    // Prefab tracking
    string? _prefabId;
    string? _prefabPath;

    public string? PrefabId
    {
        get => _prefabId;
        set { if (_prefabId != value) { _prefabId = value; OnChanged(nameof(PrefabId)); } }
    }

    public string? PrefabPath
    {
        get => _prefabPath;
        set { if (_prefabPath != value) { _prefabPath = value; OnChanged(nameof(PrefabPath)); } }
    }

    // Mandatory component
    public Transform Transform { get; } = new();

    public GameObject(string name)
    {
        _name = name;
        Transform.gameObject = this;
    }

    public void AddChild(GameObject child)
    {
        if (child == this) return;
        if (child.IsAncestorOf(this)) return;
        child.Parent?.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void RemoveFromParent()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    public bool IsAncestorOf(GameObject node)
    {
        var p = node.Parent;
        while (p is not null) { if (p == this) return true; p = p.Parent; }
        return false;
    }

    public T AddBehavior<T>() where T : Behavior, new()
        => (T)AddBehavior(new T());

    public Behavior AddBehavior(Behavior b)
    {
        b.gameObject = this;
        Behaviors.Add(b);
        b.OnEnable();
        return b;
    }

    public void RemoveBehavior(Behavior b)
    {
        if (b is Transform) return; // never remove
        if (Behaviors.Remove(b)) { b.OnDisable(); b.gameObject = null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
