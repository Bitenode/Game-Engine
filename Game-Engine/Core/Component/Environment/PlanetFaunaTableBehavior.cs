#nullable enable
using System;
using Game_Engine.Core.Biome.Graph;

namespace Game_Engine.Core.Component;

/// <summary>
/// Stand-grid-ready fauna table stub. Holds compiled herd/species data from the graph.
/// AI brains are not wired yet — locomotion must use StandRadiusGrid, never density marches.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetFaunaTableBehavior : Behavior
{
    FaunaLayerRecipe[] _layers = Array.Empty<FaunaLayerRecipe>();

    public FaunaLayerRecipe[] Layers => _layers;

    public void Bind(FaunaLayerRecipe[]? layers)
        => _layers = layers ?? Array.Empty<FaunaLayerRecipe>();

    public float HerdSpacingOrDefault(int index, float fallback = 18f)
    {
        if (index < 0 || index >= _layers.Length) return fallback;
        float s = _layers[index].HerdSpacing;
        return s > 0.5f ? s : fallback;
    }
}
