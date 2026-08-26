using System;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Serialized voxel-edit overlay. Coordinates are planet-local unscaled
/// (same space as <see cref="PlanetVoxelEditStore"/>), never world-space.
/// Written to a sidecar <c>.planetvox</c> next to the <c>.planet</c> JSON.
/// </summary>
public sealed class PlanetVoxelEditAsset
{
    public const int CurrentVersion = 1;
    public const string PlanetLocalUnscaledSpace = "PlanetLocalUnscaled";
    public const string SidecarExtension = ".planetvox";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Must be <see cref="PlanetLocalUnscaledSpace"/>.</summary>
    public string Space { get; set; } = PlanetLocalUnscaledSpace;

    /// <summary>Grid size used when baking strokes into <see cref="BakedCells"/>.</summary>
    public float BakedCellSize { get; set; } = 1f;

    /// <summary>Largest brush radius represented (live strokes and baked cells). Used for crust depth.</summary>
    public float MaxRadius { get; set; }

    public PlanetVoxelSphereStroke[] Strokes { get; set; } = Array.Empty<PlanetVoxelSphereStroke>();

    public PlanetVoxelBakedCell[] BakedCells { get; set; } = Array.Empty<PlanetVoxelBakedCell>();
}

public sealed class PlanetVoxelSphereStroke
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    public float DensityDelta { get; set; }
    public float Falloff { get; set; }
}

/// <summary>Sparse density delta at an integer cell in planet-local space (cell origin = IX/IY/IZ * cellSize).</summary>
public sealed class PlanetVoxelBakedCell
{
    public int IX { get; set; }
    public int IY { get; set; }
    public int IZ { get; set; }
    public float Delta { get; set; }
}
