#nullable enable
using System;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Physics;

/// <summary>
/// Dispatches Unity-style trigger messages to every <see cref="Behavior"/> on both GameObjects
/// involved in the overlap. The listener is the collider on the detecting body (character/rigidbody);
/// <paramref name="trigger"/> is the other volume (usually <see cref="Collider.IsTrigger"/>).
/// </summary>
public static class TriggerDispatcher
{
    public static void DispatchEnter(Collider? listener, Collider trigger)
    {
        if (!TryPair(listener, trigger, requireActiveHierarchy: true, out var listenerGo, out var trigGo)) return;
        DispatchToBehaviors(listenerGo, trigger, requireActive: true, static (b, o) => b.OnTriggerEnter(o));
        DispatchToBehaviors(trigGo, listener!, requireActive: true, static (b, o) => b.OnTriggerEnter(o));
    }

    public static void DispatchStay(Collider? listener, Collider trigger)
    {
        if (!TryPair(listener, trigger, requireActiveHierarchy: true, out var listenerGo, out var trigGo)) return;
        DispatchToBehaviors(listenerGo, trigger, requireActive: true, static (b, o) => b.OnTriggerStay(o));
        DispatchToBehaviors(trigGo, listener!, requireActive: true, static (b, o) => b.OnTriggerStay(o));
    }

    public static void DispatchExit(Collider? listener, Collider trigger)
    {
        if (listener == null || ReferenceEquals(listener, trigger)) return;
        var lg = listener.gameObject;
        var tg = trigger.gameObject;
        if (lg == null || tg == null) return;
        DispatchToBehaviors(lg, trigger, requireActive: false, static (b, o) => b.OnTriggerExit(o));
        DispatchToBehaviors(tg, listener, requireActive: false, static (b, o) => b.OnTriggerExit(o));
    }

    static bool TryPair(Collider? listener, Collider trigger, bool requireActiveHierarchy, out GameObject listenerGo, out GameObject trigGo)
    {
        listenerGo = null!;
        trigGo = null!;
        if (listener == null || trigger.gameObject is not { } tg) return false;
        if (listener.gameObject is not { } lg) return false;
        trigGo = tg;
        listenerGo = lg;
        if (ReferenceEquals(listener, trigger)) return false;
        if (requireActiveHierarchy)
        {
            if (!tg.IsActiveInHierarchy || !lg.IsActiveInHierarchy) return false;
        }
        return true;
    }

    static void DispatchToBehaviors(GameObject go, Collider? other, bool requireActive, Action<Behavior, Collider?> invoke)
    {
        foreach (var b in go.Behaviors)
        {
            if (requireActive && !b.IsActiveAndEnabled) continue;
            try { invoke(b, other); }
            catch (Exception ex) { Log.Error(ex, $"OnTrigger@{b.GetType().Name}"); }
        }
    }
}
