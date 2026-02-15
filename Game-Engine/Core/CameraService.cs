using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>Global registry of cameras.</summary>
    public static class CameraService
    {
        static readonly List<Camera> _cameras = new();
        public static IReadOnlyList<Camera> All => _cameras;
        public static event Action? Changed;

        public static void Register(Camera c) { if (!_cameras.Contains(c)) { _cameras.Add(c); Changed?.Invoke(); } }
        public static void Unregister(Camera c) { if (_cameras.Remove(c)) Changed?.Invoke(); }

        public static Camera? MainOrFirst()
            => _cameras.FirstOrDefault(c => c.IsMain) ?? _cameras.FirstOrDefault();
    }
}
