#nullable enable
using System;
using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.VirtualCamera
{
    /// <summary>
    /// Camera Zone trigger. When the player enters this zone,
    /// the specified VirtualCamera is activated (priority boost).
    /// Attach a BoxCollider as a trigger to define the zone bounds.
    /// </summary>
    public sealed class CameraZone : Behavior
    {
        /// <summary>Name of the VirtualCamera GameObject to activate.</summary>
        [Persist] public string VirtualCameraName { get; set; } = "";

        /// <summary>Priority boost applied when player is inside the zone.</summary>
        [Persist] public int PriorityBoost { get; set; } = 100;

        /// <summary>Name of the player GameObject to detect.</summary>
        [Persist] public string PlayerName { get; set; } = "Player";

        /// <summary>Zone half-extents (local space box).</summary>
        [Persist] public Vector3 HalfExtents { get; set; } = new Vector3(5, 5, 5);

        private VirtualCamera? _targetVCam;
        private int _originalPriority;
        private bool _playerInside;

        public override void Start()
        {
            _targetVCam = FindVirtualCamera();
            if (_targetVCam != null)
                _originalPriority = _targetVCam.Priority;
        }

        public override void Update()
        {
            var player = FindPlayer();
            if (player == null) return;

            var playerPos = new SN.Vector3(
                (float)player.Transform.Position.X,
                (float)player.Transform.Position.Y,
                (float)player.Transform.Position.Z);

            var zonePos = new SN.Vector3(
                (float)Transform.Position.X,
                (float)Transform.Position.Y,
                (float)Transform.Position.Z);

            var halfExt = new SN.Vector3((float)HalfExtents.X, (float)HalfExtents.Y, (float)HalfExtents.Z);
            var diff = playerPos - zonePos;

            bool inside = MathF.Abs(diff.X) <= halfExt.X
                       && MathF.Abs(diff.Y) <= halfExt.Y
                       && MathF.Abs(diff.Z) <= halfExt.Z;

            if (inside && !_playerInside)
            {
                _playerInside = true;
                if (_targetVCam != null)
                    _targetVCam.Priority = _originalPriority + PriorityBoost;
            }
            else if (!inside && _playerInside)
            {
                _playerInside = false;
                if (_targetVCam != null)
                    _targetVCam.Priority = _originalPriority;
            }
        }

        private VirtualCamera? FindVirtualCamera()
        {
            if (string.IsNullOrEmpty(VirtualCameraName)) return null;
            foreach (var vcam in VirtualCamera.All)
                if (vcam.gameObject?.Name == VirtualCameraName) return vcam;
            return null;
        }

        private GameObject? FindPlayer()
        {
            if (string.IsNullOrEmpty(PlayerName)) return null;
            foreach (var root in SceneService.Root)
            {
                var found = FindByName(root, PlayerName);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject? FindByName(GameObject go, string name)
        {
            if (go.Name == name) return go;
            foreach (var child in go.Children)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
