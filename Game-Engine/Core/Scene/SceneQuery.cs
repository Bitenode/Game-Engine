#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core;

public static class SceneQuery
{
    // Explicit stack instead of recursive yield: nested iterators allocate one enumerator
    // per depth level and cost O(depth) per yielded node.
    private static IEnumerable<GameObject> Traverse(GameObject n)
    {
        if (!n.Enabled) yield break;
        var stack = new Stack<GameObject>();
        stack.Push(n);
        while (stack.Count > 0)
        {
            var go = stack.Pop();
            yield return go;
            var children = go.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                var c = children[i];
                if (c.Enabled) stack.Push(c);
            }
        }
    }

    public static IEnumerable<T> FindBehaviors<T>() where T : Behavior
    {
        foreach (var root in SceneService.Root)
            foreach (var go in Traverse(root))
                foreach (var b in go.Behaviors)
                    if (b.IsActiveAndEnabled && b is T t) yield return t;
    }

    /// <summary>Allocation-free variant of <see cref="FindBehaviors{T}"/> for per-tick callers.</summary>
    public static void CollectBehaviors<T>(List<T> into, Stack<GameObject> scratch) where T : Behavior
    {
        scratch.Clear();
        var roots = SceneService.Root;
        for (int r = roots.Count - 1; r >= 0; r--)
        {
            if (roots[r].Enabled) scratch.Push(roots[r]);
        }
        while (scratch.Count > 0)
        {
            var go = scratch.Pop();
            var behaviors = go.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
            {
                var b = behaviors[i];
                if (b is T t && b.IsActiveAndEnabled) into.Add(t);
            }
            var children = go.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                var c = children[i];
                if (c.Enabled) scratch.Push(c);
            }
        }
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
