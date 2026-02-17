#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core;

public static class SceneQuery
{
    private static IEnumerable<GameObject> Traverse(GameObject n)
    {
        if (!n.Enabled) yield break;
        yield return n;
        foreach (var c in n.Children)
            foreach (var s in Traverse(c)) yield return s;
    }

    public static IEnumerable<T> FindBehaviors<T>() where T : Behavior
    {
        foreach (var root in SceneService.Root)
            foreach (var go in Traverse(root))
                foreach (var b in go.Behaviors)
                    if (b.IsActiveAndEnabled && b is T t) yield return t;
    }

    public static GameObject? FindByName(string name)
    {
        foreach (var root in SceneService.Root)
            foreach (var go in Traverse(root))
                if (string.Equals(go.Name, name, StringComparison.Ordinal))
                    return go;
        return null;
    }

    public static GameObject? FindByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('/');
        foreach (var root in SceneService.Root)
        {
            if (!string.Equals(root.Name, parts[0], StringComparison.Ordinal)) continue;
            var current = root;
            bool found = true;
            for (int i = 1; i < parts.Length; i++)
            {
                var child = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, parts[i], StringComparison.Ordinal));
                if (child == null) { found = false; break; }
                current = child;
            }
            if (found) return current;
        }
        return null;
    }
}
