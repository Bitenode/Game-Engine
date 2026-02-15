#if !PLAYER
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    public sealed class SetTransformPositionCmd : ICmd
    {
        readonly Transform _t;
        readonly Vector3 _from, _to;
        public SetTransformPositionCmd(Transform t, Vector3 from, Vector3 to) { _t = t; _from = from; _to = to; }
        public void Do() => _t.Position = _to;
        public void Undo() => _t.Position = _from;
    }

    public sealed class SetTransformRotationCmd : ICmd
    {
        readonly Transform _t; readonly Vector3 _from, _to;
        public SetTransformRotationCmd(Transform t, Vector3 from, Vector3 to) { _t = t; _from = from; _to = to; }
        public void Do() => _t.Rotation = _to;
        public void Undo() => _t.Rotation = _from;
    }

    public sealed class SetTransformScaleCmd : ICmd
    {
        readonly Transform _t; readonly Vector3 _from, _to;
        public SetTransformScaleCmd(Transform t, Vector3 from, Vector3 to) { _t = t; _from = from; _to = to; }
        public void Do() => _t.Scale = _to;
        public void Undo() => _t.Scale = _from;
    }
}
#endif
