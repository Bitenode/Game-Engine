#nullable enable
using System;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Serialized dig overlay: height-cubemap deltas (surface sculpt) + crust-band cave strokes.
/// Sidecar remains <c>.planetvox</c>; version 2+ carries height faces.
/// </summary>
public sealed class PlanetVoxelEditAsset
{
    public const int CurrentVersion = 3;
    public const string PlanetLocalUnscaledSpace = "PlanetLocalUnscaled";
    public const string SidecarExtension = ".planetvox";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Must be <see cref="PlanetLocalUnscaledSpace"/>.</summary>
    public string Space { get; set; } = PlanetLocalUnscaledSpace;

    /// <summary>Grid size used when baking strokes into <see cref="BakedCells"/>.</summary>
    public float BakedCellSize { get; set; } = 1f;

    /// <summary>Largest brush radius represented (live strokes and baked cells). Used for crust depth.</summary>
    public float MaxRadius { get; set; }

    /// <summary>Shared resolution for <see cref="HeightDeltaFaces"/> (0 = none).</summary>
    public int HeightDeltaResolution { get; set; }

    /// <summary>
    /// Six faces of height dig deltas (row-major). Empty when using <see cref="HeightDeltaSparse"/>.
    /// Legacy full-face dumps from early v2 are still loaded.
    /// </summary>
    public float[][] HeightDeltaFaces { get; set; } = Array.Empty<float[]>();

    /// <summary>Compact nonzero height digs (preferred for v2+ save).</summary>
    public PlanetHeightDeltaTexel[] HeightDeltaSparse { get; set; } = Array.Empty<PlanetHeightDeltaTexel>();

    public PlanetVoxelSphereStroke[] Strokes { get; set; } = Array.Empty<PlanetVoxelSphereStroke>();

    public PlanetVoxelBakedCell[] BakedCells { get; set; } = Array.Empty<PlanetVoxelBakedCell>();
}

/// <summary>One nonzero height-cubemap dig texel.</summary>
public sealed class PlanetHeightDeltaTexel
{
    public int Face { get; set; }
    public int Index { get; set; }
    public float Value { get; set; }
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
