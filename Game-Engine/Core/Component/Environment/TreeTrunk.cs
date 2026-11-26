namespace Game_Engine.Core.Component;

/// <summary>
/// Marks a tree trunk collider (mesh). Chopping reads <see cref="Health"/>.
/// Physics keeps these even when the tree sits under a planet root.
/// </summary>
public sealed class TreeTrunk : Behavior
{
    public float Health = 36f;

    public static bool IsOn(GameObject? go)
    {
        if (go == null) return false;
        var behaviors = go.Behaviors;
        for (int i = 0; i < behaviors.Count; i++)
        {
            if (behaviors[i] is TreeTrunk)
                return true;
        }
        return false;
    }
}
