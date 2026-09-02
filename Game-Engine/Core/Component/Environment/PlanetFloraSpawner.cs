#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Biome.Graph;

namespace Game_Engine.Core.Component;

/// <summary>
/// Tree flora companion: tracks unique imported mesh paths with a hard cap so
/// <see cref="PlanetVegetationSystem"/> does not thrash the template cache.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetFloraSpawner : Behavior
{
    [Persist] public int MaxUniqueMeshes { get; set; } = 24;
    [Persist] public bool Enabled { get; set; } = true;

    readonly HashSet<string> _uniqueMeshes = new(StringComparer.OrdinalIgnoreCase);
    FloraLayerRecipe[] _recipes = Array.Empty<FloraLayerRecipe>();

    public int UniqueMeshCount => _uniqueMeshes.Count;
    public FloraLayerRecipe[] Recipes => _recipes;

    public void ApplyRecipes(FloraLayerRecipe[]? recipes)
    {
        _recipes = recipes ?? Array.Empty<FloraLayerRecipe>();
    }

    public void ClearUniqueMeshes() => _uniqueMeshes.Clear();

    /// <summary>
    /// Returns true when the mesh path is allowed under the unique-mesh budget.
    /// Empty paths always pass (procedural / billboard grass).
    /// </summary>
    public bool TryRegisterMesh(string? modelPath)
    {
        if (!Enabled) return true;
        if (string.IsNullOrWhiteSpace(modelPath)) return true;
        string key = modelPath.Trim();
        if (_uniqueMeshes.Contains(key)) return true;
        if (_uniqueMeshes.Count >= Math.Max(1, MaxUniqueMeshes))
            return false;
        _uniqueMeshes.Add(key);
        return true;
    }

    public bool IsMeshAllowed(string? modelPath)
    {
        if (!Enabled) return true;
        if (string.IsNullOrWhiteSpace(modelPath)) return true;
        if (_uniqueMeshes.Contains(modelPath.Trim())) return true;
        return _uniqueMeshes.Count < Math.Max(1, MaxUniqueMeshes);
    }
}
