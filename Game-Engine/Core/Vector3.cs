namespace Game_Engine.Core;

public sealed class Vector3 : ObservableObject
{
    double _x, _y, _z;
    public double X { get => _x; set { if (Set(ref _x, value)) SceneService.NotifyChanged(); } }
    public double Y { get => _y; set { if (Set(ref _y, value)) SceneService.NotifyChanged(); } }
    public double Z { get => _z; set { if (Set(ref _z, value)) SceneService.NotifyChanged(); } }

    public Vector3() : this(0, 0, 0) { }
    public Vector3(double x, double y, double z) { _x = x; _y = y; _z = z; }
}
