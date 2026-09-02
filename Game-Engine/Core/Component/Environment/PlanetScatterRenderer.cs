#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Biome.Graph;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// GPU-instanced rocks/grass hook. Collects scatter recipes and instance transforms;
/// SceneRenderer can draw them via the existing <c>DrawArraysInstanced</c> billboard path
/// (and later mesh instances). Does not replace CPU grass merge in
/// <see cref="PlanetVegetationSystem"/> yet — companion storage only.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetScatterRenderer : Behavior
{
    [Persist] public bool Enabled { get; set; } = true;
    [Persist] public int MaxInstances { get; set; } = 8192;

    readonly List<ScatterInstance> _instances = new();
    ScatterLayerRecipe[] _recipes = Array.Empty<ScatterLayerRecipe>();

    public int InstanceCount => _instances.Count;
    public IReadOnlyList<ScatterInstance> Instances => _instances;
    public ScatterLayerRecipe[] Recipes => _recipes;

    public void ApplyRecipes(ScatterLayerRecipe[]? recipes)
    {
        _recipes = recipes ?? Array.Empty<ScatterLayerRecipe>();
    }

    public void BindLayers(ScatterLayerRecipe[]? recipes) => ApplyRecipes(recipes);

    public void ClearInstances() => _instances.Clear();

    public bool TryAddInstance(in ScatterInstance instance)
    {
        if (!Enabled) return false;
        if (_instances.Count >= Math.Max(1, MaxInstances)) return false;
        _instances.Add(instance);
        return true;
    }

    /// <summary>
    /// Pack billboard-style instance data (pos.xyz, scale) for DrawArraysInstanced consumers.
    /// Returns count written.
    /// </summary>
    public int FillBillboardBuffer(Span<float> dst)
    {
        int written = 0;
        int max = Math.Min(_instances.Count, dst.Length / 4);
        for (int i = 0; i < max; i++)
        {
            var inst = _instances[i];
            int o = i * 4;
            dst[o] = inst.Position.X;
            dst[o + 1] = inst.Position.Y;
            dst[o + 2] = inst.Position.Z;
            dst[o + 3] = inst.Scale;
            written++;
        }
        return written;
    }

    public readonly struct ScatterInstance
    {
        public SN.Vector3 Position { get; init; }
        public float Scale { get; init; }
        public float YawDeg { get; init; }
        public string ScatterType { get; init; }
        public string ModelPath { get; init; }

        public ScatterInstance(SN.Vector3 position, float scale, float yawDeg, string scatterType, string modelPath)
        {
            Position = position;
            Scale = scale;
            YawDeg = yawDeg;
            ScatterType = scatterType ?? "Rock";
            ModelPath = modelPath ?? "";
        }
    }
}
