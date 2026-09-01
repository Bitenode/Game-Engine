namespace Game_Engine.Core
{
    /// <summary>Common scene object tags for gameplay and effect queries.</summary>
    public static class SceneTags
    {
        public const string Water = "Water";
    }

    /// <summary>Scene layer indices (0–31) used with <see cref="GameObject.Layer"/>.</summary>
    public static class SceneLayers
    {
        public const int Default = 0;
        public const int Water = 4;

        public static int WaterMask => 1 << Water;
    }

    public static class SceneIdentity
    {
        public static bool IsWater(GameObject? go)
            => go != null && (go.Tag == SceneTags.Water || go.Layer == SceneLayers.Water);
    }
}
