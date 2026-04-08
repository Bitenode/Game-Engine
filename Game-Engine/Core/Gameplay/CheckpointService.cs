using System.Numerics;

namespace Game_Engine.Core.Gameplay;

/// <summary>Stores the last checkpoint position set by <see cref="Physics.TriggerVolume"/> (checkpoint preset). Respawn logic can read this at runtime.</summary>
public static class CheckpointService
{
    public static Vector3 LastCheckpointPosition { get; private set; }
    public static bool HasCheckpoint { get; private set; }

    public static void SetLastCheckpoint(Vector3 worldPosition)
    {
        LastCheckpointPosition = worldPosition;
        HasCheckpoint = true;
    }

    public static void Clear()
    {
        LastCheckpointPosition = default;
        HasCheckpoint = false;
    }
}
