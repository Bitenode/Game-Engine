#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core
{
    /// <summary>
    /// Marshals work onto the engine thread. The editor and desktop player forward to
    /// Avalonia's UI thread. The Xbox UWP host queues and drains once per frame.
    /// </summary>
    public static class EngineDispatcher
    {
        static readonly object Gate = new();
        static readonly Queue<Action> Queue = new();
        static Func<bool>? _check;
        static Action<Action>? _post;

        public static void UseCallbacks(Func<bool> checkAccess, Action<Action> post)
        {
            _check = checkAccess ?? throw new ArgumentNullException(nameof(checkAccess));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>Queue posts and treat <paramref name="isOwnerThread"/> as the engine thread.</summary>
        public static void UseQueue(Func<bool> isOwnerThread)
        {
            _check = isOwnerThread;
            _post = action =>
            {
                lock (Gate) Queue.Enqueue(action);
            };
        }

        public static bool CheckAccess() => _check?.Invoke() ?? true;

        public static void Post(Action action)
        {
            if (action == null) return;
            if (_post != null) _post(action);
            else action();
        }

        public static void Drain()
        {
            while (true)
            {
                Action action;
                lock (Gate)
                {
                    if (Queue.Count == 0) return;
                    action = Queue.Dequeue();
                }
                try { action(); }
                catch (Exception ex) { Log.Warning("[EngineDispatcher] " + ex.Message); }
            }
        }
    }
}
