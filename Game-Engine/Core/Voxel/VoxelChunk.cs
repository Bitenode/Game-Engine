using System;
using System.Runtime.CompilerServices;

namespace Game_Engine.Core.Voxel;

/// <summary>
/// A 3D grid of density values and material indices representing a single voxel chunk.
/// Density &lt; 0 = solid, density &gt; 0 = air. The isosurface lives at density == 0.
/// Grid dimensions are (Size+1)^3 where Size is the number of cells per axis,
/// giving one extra sample on each edge for neighbor overlap.
/// </summary>
public sealed class VoxelChunk
{
    public const int DefaultSize = 32;

    public int Size { get; }
    public int SamplesPerAxis { get; }

    /// <summary>Flattened density field. Length = SamplesPerAxis^3. Negative = solid, positive = air.</summary>
    public float[] Density { get; }

    /// <summary>Per-voxel material index for biome/texture blending. Length = SamplesPerAxis^3.</summary>
    public byte[] Material { get; }

    /// <summary>World-space origin of this chunk (minimum corner).</summary>
    public System.Numerics.Vector3 WorldOrigin { get; set; }

    /// <summary>World-space size of one cell along tangential axes (X/Y).</summary>
    public float CellSize { get; set; } = 1f;

    /// <summary>World-space size of one cell along the radial axis (Z/BasisZ). Defaults to CellSize if not set.</summary>
    public float CellSizeZ { get; set; } = -1f;

    /// <summary>Effective radial cell size: returns CellSizeZ if explicitly set, otherwise CellSize.</summary>
    public float EffectiveCellSizeZ => CellSizeZ > 0f ? CellSizeZ : CellSize;

    /// <summary>Oriented basis vectors for cube-sphere chunks. Defaults to axis-aligned.</summary>
    public System.Numerics.Vector3 BasisX { get; set; } = System.Numerics.Vector3.UnitX;
    public System.Numerics.Vector3 BasisY { get; set; } = System.Numerics.Vector3.UnitY;
    public System.Numerics.Vector3 BasisZ { get; set; } = System.Numerics.Vector3.UnitZ;

    /// <summary>LOD level of this chunk (0 = highest detail).</summary>
    public int LodLevel { get; set; }

    public VoxelChunk(int size = DefaultSize)
    {
        Size = Math.Max(1, size);
        SamplesPerAxis = Size + 1;
        int total = SamplesPerAxis * SamplesPerAxis * SamplesPerAxis;
        Density = new float[total];
        Material = new byte[total];
        Array.Fill(Density, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Index(int x, int y, int z)
        => (z * SamplesPerAxis + y) * SamplesPerAxis + x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(int x, int y, int z)
        => Density[Index(x, y, z)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int x, int y, int z, float value)
        => Density[Index(x, y, z)] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetMaterial(int x, int y, int z)
        => Material[Index(x, y, z)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMaterial(int x, int y, int z, byte mat)
        => Material[Index(x, y, z)] = mat;

    /// <summary>
    /// Optional spherical (or other non-linear) mapping. When set, both
    /// <see cref="GridToWorld(int,int,int)"/> overloads use this instead of the planar basis.
    /// Planet crust chunks use this to map (U, V, radial) to planet-local positions.
    /// </summary>
    public System.Func<float, float, float, System.Numerics.Vector3>? CustomGridToWorld { get; set; }

    /// <summary>Convert grid coordinates to world (or planet-local) position using the oriented basis.</summary>
    public System.Numerics.Vector3 GridToWorld(int x, int y, int z)
        => GridToWorld((float)x, y, z);

    /// <summary>Convert grid coordinates to world (or planet-local) position with fractional interpolation.</summary>
    public System.Numerics.Vector3 GridToWorld(float x, float y, float z)
    {
        if (CustomGridToWorld != null)
            return CustomGridToWorld(x, y, z);
        return WorldOrigin + BasisX * (x * CellSize) + BasisY * (y * CellSize) + BasisZ * (z * EffectiveCellSizeZ);
    }

    /// <summary>Fill the chunk with a sphere density field for testing.</summary>
    public void FillSphere(System.Numerics.Vector3 center, float radius)
    {
        int n = SamplesPerAxis;
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var worldPos = GridToWorld(x, y, z);
                    float dist = System.Numerics.Vector3.Distance(worldPos, center);
                    Density[Index(x, y, z)] = dist - radius;
                }
    }
}
