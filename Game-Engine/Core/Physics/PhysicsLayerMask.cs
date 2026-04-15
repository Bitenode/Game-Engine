namespace Game_Engine.Core.Physics
{
    /// <summary>
    /// Bitmask helpers for <see cref="GameObject.Layer"/> (0–31).
    /// Masks follow Unity-style semantics: bit <c>1 &lt;&lt; layer</c> selects that layer; <c>-1</c> matches everything.
    /// </summary>
    public static class PhysicsLayerMask
    {
        /// <summary>Returns true if <paramref name="layerMask"/> includes <paramref name="layer"/>.</summary>
        public static bool Includes(int layerMask, int layer)
        {
            if (layerMask == -1) return true;
            int l = layer < 0 ? 0 : (layer > 31 ? 31 : layer);
            return (layerMask & (1 << l)) != 0;
        }
    }
}
