#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Events
{
    /// <summary>
    /// Lightweight global event bus for decoupled component communication.
    /// Supports type-safe publish/subscribe with automatic cleanup.
    /// </summary>
    public static class EventBus
    {
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private static readonly Dictionary<Behavior, List<(Type type, Delegate handler)>> _behaviorSubs = new();

        /// <summary>
        /// Subscribe to events of type <typeparamref name="T"/>.
        /// </summary>
        public static void Subscribe<T>(Action<T> handler)
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<Delegate>();
                _handlers[type] = list;
            }
            if (!list.Contains(handler))
                list.Add(handler);
        }

        /// <summary>
        /// Subscribe to events of type <typeparamref name="T"/> with auto-unsubscribe
        /// when the owning Behavior is destroyed.
        /// </summary>
        public static void Subscribe<T>(Behavior owner, Action<T> handler)
        {
            Subscribe(handler);

            if (!_behaviorSubs.TryGetValue(owner, out var subs))
            {
                subs = new List<(Type, Delegate)>();
                _behaviorSubs[owner] = subs;
            }
            subs.Add((typeof(T), handler));
        }

        /// <summary>
        /// Unsubscribe a handler from events of type <typeparamref name="T"/>.
        /// </summary>
        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        /// <summary>
        /// Publish an event to all subscribers of type <typeparamref name="T"/>.
        /// </summary>
        public static void Publish<T>(T evt)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;

            // Iterate over a snapshot to allow subscribe/unsubscribe during publish
            var snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    ((Action<T>)snapshot[i])(evt);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"EventBus.Publish<{typeof(T).Name}>");
                }
            }
        }

        /// <summary>
        /// Remove all subscriptions registered by a Behavior.
        /// Called automatically when a Behavior is destroyed.
        /// </summary>
        public static void UnsubscribeAll(Behavior owner)
        {
            if (!_behaviorSubs.TryGetValue(owner, out var subs)) return;

            foreach (var (type, handler) in subs)
            {
                if (_handlers.TryGetValue(type, out var list))
                    list.Remove(handler);
            }

            _behaviorSubs.Remove(owner);
        }

        /// <summary>
        /// Clear all subscriptions. Call during scene teardown.
        /// </summary>
        public static void ClearAll()
        {
            _handlers.Clear();
            _behaviorSubs.Clear();
        }

        /// <summary>
        /// Get the number of subscribers for a given event type.
        /// </summary>
        public static int SubscriberCount<T>()
        {
            return _handlers.TryGetValue(typeof(T), out var list) ? list.Count : 0;
        }
    }

    // ── Built-in event types ──

    /// <summary>Raised when a scene finishes loading.</summary>
    public struct SceneLoadedEvent
    {
        public string SceneName { get; set; }
    }

    /// <summary>Raised when the game is paused or resumed.</summary>
    public struct GamePausedEvent
    {
        public bool IsPaused { get; set; }
    }

    /// <summary>Raised when a collision occurs between two colliders.</summary>
    public struct CollisionEvent
    {
        public GameObject ObjectA { get; set; }
        public GameObject ObjectB { get; set; }
        public System.Numerics.Vector3 ContactPoint { get; set; }
        public System.Numerics.Vector3 ContactNormal { get; set; }
    }

    /// <summary>Raised when a GameObject is spawned.</summary>
    public struct ObjectSpawnedEvent
    {
        public GameObject Object { get; set; }
    }

    /// <summary>Raised when a GameObject is about to be destroyed.</summary>
    public struct ObjectDestroyedEvent
    {
        public GameObject Object { get; set; }
    }

    /// <summary>String-named signal from visual blueprints (Fire Event node) — subscribe via <see cref="EventBus.Subscribe{T}(Action{T})"/>.</summary>
    public struct BlueprintMessageEvent
    {
        public string Name { get; set; }
        public string? Data { get; set; }
        public GameObject? Sender { get; set; }
    }
}
