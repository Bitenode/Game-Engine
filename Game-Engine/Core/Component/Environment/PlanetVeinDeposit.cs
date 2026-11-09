#nullable enable
using System;

namespace Game_Engine.Core.Component;

/// <summary>
/// Resource marker placed by a biome-graph vein recipe. Scripts call
/// <see cref="TryHarvest"/>; this does not dig the planet crust.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetVeinDeposit : Behavior
{
    [Persist] public string ResourceId { get; set; } = "Ore";

    /// <summary>Units left in this marker.</summary>
    [Persist] public int Amount { get; set; } = 4;

    public bool IsEmpty => Amount <= 0;

    /// <summary>Take up to <paramref name="count"/> units. Removes the marker when empty.</summary>
    public int TryHarvest(int count)
    {
        if (count <= 0 || Amount <= 0) return 0;
        int taken = Math.Min(count, Amount);
        Amount -= taken;
        if (Amount <= 0)
            gameObject?.RemoveFromParent();
        return taken;
    }
}
