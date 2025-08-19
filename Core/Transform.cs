namespace Game_Engine.Core;

public sealed class Transform : Behavior
{
    Vector3 _position = new Vector3();
    Vector3 _rotation = new Vector3();
    Vector3 _scale = new Vector3(1, 1, 1);

    public Vector3 Position { get => _position; set { if (Set(ref _position, value)) Hook(_position); } }
    public Vector3 Rotation { get => _rotation; set { if (Set(ref _rotation, value)) Hook(_rotation); } }
    public Vector3 Scale { get => _scale; set { if (Set(ref _scale, value)) Hook(_scale); } }

    public Transform() { Hook(_position); Hook(_rotation); Hook(_scale); }

    void Hook(Vector3 v)
    {
        v.PropertyChanged -= OnVecChanged;
        v.PropertyChanged += OnVecChanged;
        SceneService.NotifyChanged();
    }

    void OnVecChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // bubble notifications + repaint
        Raise(nameof(Position));
        Raise(nameof(Rotation));
        Raise(nameof(Scale));
        SceneService.NotifyChanged();
    }

    // Transform is always enabled and cannot be removed.
    public new bool Enabled
    {
        get => true;
        set { /* ignore */ }
    }
}
