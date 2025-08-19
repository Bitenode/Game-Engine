using Game_Engine.Core;

namespace Game_Engine.Core;
public static class SelectionService
{
    public static GameObject? Current { get; private set; }
    public static event Action? Changed;
    public static void Set(GameObject? go) { Current = go; Changed?.Invoke(); }
    public static void Touch() => Changed?.Invoke();
    public static void Clear() { Current = null; Changed?.Invoke(); }
}
