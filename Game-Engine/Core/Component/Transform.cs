using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

public sealed class Transform : Behavior
{
    Vector3 _position = new Vector3();
    Vector3 _rotation = new Vector3();
    Vector3 _scale = new Vector3(1, 1, 1);
    SN.Quaternion? _explicitRotation;
    SN.Matrix4x4? _explicitRotationMatrix;

    [Persist] public Vector3 Position { get => _position; set { if (Set(ref _position, value)) Hook(_position); } }
    [Persist] public Vector3 Rotation
    {
        get => _rotation;
        set
        {
            _explicitRotation = null;
            _explicitRotationMatrix = null;
            if (Set(ref _rotation, value)) Hook(_rotation);
        }
    }
    [Persist] public Vector3 Scale { get => _scale; set { if (Set(ref _scale, value)) Hook(_scale); } }

    public Transform() { Hook(_position); Hook(_rotation); Hook(_scale); }

    /// <summary>
    /// Planet standing cannot be stored as yaw/pitch/roll without losing the radial up axis.
    /// When set, this quaternion is used for the world matrix instead of Euler.
    /// </summary>
    public SN.Quaternion GetRotationQuaternion()
    {
        if (_explicitRotation.HasValue)
            return _explicitRotation.Value;
        return SN.Quaternion.CreateFromYawPitchRoll(
            TransformUtil.Deg2Rad(_rotation.Y),
            TransformUtil.Deg2Rad(_rotation.X),
            TransformUtil.Deg2Rad(_rotation.Z));
    }

    public bool TryGetExplicitRotationMatrix(out SN.Matrix4x4 matrix)
    {
        if (_explicitRotationMatrix.HasValue)
        {
            matrix = _explicitRotationMatrix.Value;
            return true;
        }
        matrix = default;
        return false;
    }

    public void SetExplicitRotationMatrix(in SN.Matrix4x4 matrix)
    {
        _explicitRotationMatrix = matrix;
        _explicitRotation = SN.Quaternion.CreateFromRotationMatrix(matrix);
    }

    public void SetRotationQuaternion(SN.Quaternion q)
    {
        _explicitRotation = q.LengthSquared() < 1e-12f
            ? SN.Quaternion.Identity
            : SN.Quaternion.Normalize(q);
        _explicitRotationMatrix = SN.Matrix4x4.CreateFromQuaternion(_explicitRotation.Value);
    }

    void Hook(Vector3 v)
    {
        v.PropertyChanged -= OnVecChanged;
        v.PropertyChanged += OnVecChanged;
        SceneService.NotifyChanged();
    }

    void OnVecChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Inspector live-edits fire per-component; do not drop planet standing.
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
