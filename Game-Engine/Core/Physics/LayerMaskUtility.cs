namespace Game_Engine.Core.Physics;

/// <summary>Bit mask helpers for 32 physics layers (indices 0–31).</summary>
public static class LayerMaskUtility
{
    public static bool Contains(int mask, int layerIndex)
        => layerIndex is >= 0 and < 32 && (mask & (1 << layerIndex)) != 0;

    public static int SetLayer(int mask, int layerIndex, bool enabled)
    {
        if (layerIndex < 0 || layerIndex > 31) return mask;
        if (enabled) return mask | (1 << layerIndex);
        return mask & ~(1 << layerIndex);
    }
}
