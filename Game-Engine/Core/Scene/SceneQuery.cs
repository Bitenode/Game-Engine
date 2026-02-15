#nullable enable
using System.Collections.Generic;

namespace Game_Engine.Core;

public static class SceneQuery
{
    public static IEnumerable<T> FindBehaviors<T>() where T : Behavior
    {
        static IEnumerable<GameObject> Traverse(GameObject n)
        {
            yield return n;
            foreach (var c in n.Children)
                foreach (var s in Traverse(c)) yield return s;
        }

        foreach (var root in SceneService.Root)
            foreach (var go in Traverse(root))
                foreach (var b in go.Behaviors)
                    if (b.Enabled && b is T t) yield return t;
    }
}
