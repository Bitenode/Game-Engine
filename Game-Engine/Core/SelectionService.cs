#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core;

#if !PLAYER
public static class SelectionService
{
    /// <summary>Primary (last-selected) GameObject. Backward-compatible with single-select code.</summary>
    public static GameObject? Current { get; private set; }

    /// <summary>All currently selected GameObjects. First item is the primary selection.</summary>
    public static IReadOnlyList<GameObject> Selected => _selected;
    private static readonly List<GameObject> _selected = new();

    /// <summary>True when more than one object is selected.</summary>
    public static bool IsMultiSelect => _selected.Count > 1;

    public static event Action? Changed;
    public static event Action<GameObject>? FrameRequested;

    /// <summary>Set a single selection (replaces any existing selection).</summary>
    public static void Set(GameObject? go)
    {
        _selected.Clear();
        if (go != null)
            _selected.Add(go);
        Current = go;
        Changed?.Invoke();
    }

    /// <summary>Add a GameObject to the selection (for multi-select via Ctrl+Click or Shift+Click).</summary>
    public static void Add(GameObject go)
    {
        if (go == null) return;
        if (!_selected.Contains(go))
            _selected.Add(go);
        Current = go; // last added becomes primary
        Changed?.Invoke();
    }

    /// <summary>Remove a GameObject from the selection.</summary>
    public static void Remove(GameObject go)
    {
        _selected.Remove(go);
        Current = _selected.Count > 0 ? _selected[^1] : null;
        Changed?.Invoke();
    }

    /// <summary>Toggle a GameObject's selection state (Ctrl+Click behavior).</summary>
    public static void Toggle(GameObject go)
    {
        if (go == null) return;
        if (_selected.Contains(go))
            Remove(go);
        else
            Add(go);
    }

    /// <summary>Set multiple objects as the selection.</summary>
    public static void SetMultiple(IEnumerable<GameObject> objects)
    {
        _selected.Clear();
        _selected.AddRange(objects);
        Current = _selected.Count > 0 ? _selected[^1] : null;
        Changed?.Invoke();
    }

    /// <summary>Clear all selection.</summary>
    public static void Clear()
    {
        _selected.Clear();
        Current = null;
        Changed?.Invoke();
    }

    public static void Touch() => Changed?.Invoke();

    /// <summary>
    /// Request SceneView to frame/focus a specific object.
    /// Used by hierarchy selection so click-to-select also navigates the camera.
    /// </summary>
    public static void RequestFrame(GameObject? go)
    {
        if (go == null) return;
        FrameRequested?.Invoke(go);
    }
}
#endif
