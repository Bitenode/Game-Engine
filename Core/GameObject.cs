using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Game_Engine.Core;

public class GameObject : INotifyPropertyChanged
{
    string _name;
    GameObject? _parent;

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    public GameObject? Parent
    {
        get => _parent;
        private set { if (_parent != value) { _parent = value; OnChanged(nameof(Parent)); } }
    }

    public ObservableCollection<GameObject> Children { get; } = new();
    public ObservableCollection<Behavior> Behaviors { get; } = new();

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
