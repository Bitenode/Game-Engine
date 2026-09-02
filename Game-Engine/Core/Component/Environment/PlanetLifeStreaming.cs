#nullable enable
using System;
using Game_Engine.Core.Biome.Graph;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Face/UV cell streaming glue for planet life. Shared key space with
/// <see cref="PlanetVegetationSystem"/> so LOD splits do not pop plants.
/// Holds compiled fauna/underwater tables for later consumers.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetLifeStreaming : Behavior
{
    public const int CellsPerFaceEdge = 18;

    [Persist] public int CellsPerEdge { get; set; } = CellsPerFaceEdge;

    FaunaLayerRecipe[] _fauna = Array.Empty<FaunaLayerRecipe>();
    UnderwaterLifeRecipe[] _underwater = Array.Empty<UnderwaterLifeRecipe>();
    ResourceVeinRecipe[] _veins = Array.Empty<ResourceVeinRecipe>();

    public FaunaLayerRecipe[] FaunaLayers => _fauna;
    public UnderwaterLifeRecipe[] UnderwaterLife => _underwater;
    public ResourceVeinRecipe[] ResourceVeins => _veins;

    public void BindRecipe(PlanetRecipe? recipe)
    {
        if (recipe == null)
        {
            _fauna = Array.Empty<FaunaLayerRecipe>();
            _underwater = Array.Empty<UnderwaterLifeRecipe>();
            _veins = Array.Empty<ResourceVeinRecipe>();
            return;
        }
        _fauna = recipe.FaunaLayers ?? Array.Empty<FaunaLayerRecipe>();
        _underwater = recipe.UnderwaterLife ?? Array.Empty<UnderwaterLifeRecipe>();
        _veins = recipe.ResourceVeins ?? Array.Empty<ResourceVeinRecipe>();
    }

    public string MakeCellKey(int face, float u, float v)
        => MakeCellKey(face, u, v, Math.Max(1, CellsPerEdge));

    public static string MakeCellKey(int face, float u, float v, int cellsPerEdge)
    {
        int edge = Math.Max(1, cellsPerEdge);
        int cu = Math.Clamp((int)(u * edge), 0, edge - 1);
        int cv = Math.Clamp((int)(v * edge), 0, edge - 1);
        return $"{face}:{cu}:{cv}";
    }

    public static (int cu, int cv) CellCoords(float u, float v, int cellsPerEdge = CellsPerFaceEdge)
    {
        int edge = Math.Max(1, cellsPerEdge);
        return (Math.Clamp((int)(u * edge), 0, edge - 1), Math.Clamp((int)(v * edge), 0, edge - 1));
    }

    /// <summary>Stable hash for clustered patchiness tests inside a cell.</summary>
    public static float PatchNoise01(SN.Vector3 dir, int seed)
    {
        float x = dir.X * 12.9898f + dir.Y * 78.233f + dir.Z * 37.719f + seed * 0.13f;
        float s = MathF.Sin(x) * 43758.5453f;
        return s - MathF.Floor(s);
    }
}
