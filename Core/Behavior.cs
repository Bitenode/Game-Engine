namespace Game_Engine.Core;

/// Base class for attachable scripts/components.
public abstract class Behavior : ObservableObject
{
    bool _enabled = true;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) SceneService.NotifyChanged(); } }

    public GameObject? gameObject { get; internal set; }

    public virtual void OnEnable() { }
    public virtual void OnDisable() { }
    public virtual void Update() { }
}
