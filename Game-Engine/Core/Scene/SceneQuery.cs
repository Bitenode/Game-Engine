#nullable enable
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>Find a GameObject by name (first match). Returns null if not found.</summary>
    public static GameObject? FindByName(string name)
    {
        static GameObject? Search(GameObject n, string name)
        {
            if (n.Name == name) return n;
            foreach (var c in n.Children)
            {
                var found = Search(c, name);
                if (found != null) return found;
            }
            return null;
        }

        foreach (var root in SceneService.Root)
        {
            var found = Search(root, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Find a GameObject by hierarchical path (e.g. "Parent/Child/GrandChild").</summary>
    public static GameObject? FindByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var parts = path.Split('/');
        GameObject? current = null;

        foreach (var root in SceneService.Root)
        {
            if (root.Name == parts[0])
            {
                current = root;
                break;
            }
        }

        if (current == null) return null;

        for (int i = 1; i < parts.Length; i++)
        {
            current = current.Children.FirstOrDefault(c => c.Name == parts[i]);
            if (current == null) return null;
        }
        return current;
    }
}
