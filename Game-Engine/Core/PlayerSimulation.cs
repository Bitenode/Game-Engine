#nullable enable
using System;
using System.Reflection;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;
using Game_Engine.Core.Physics;
using Game_Engine.Core.Rendering.UI;

namespace Game_Engine.Core
{
    /// <summary>Play-mode Update / FixedUpdate shared by the desktop player and the Xbox host.</summary>
    public sealed class PlayerSimulation
    {
        readonly System.Diagnostics.Stopwatch _updateWatch = new();
        readonly System.Diagnostics.Stopwatch _fixedWatch = new();
        bool _awakened, _started, _collidersWarm, _needsWarm;
        bool _playing;
        const double FixedDt = 1.0 / 60.0;
        double _fixedAccum;

        /// <summary>
        /// Called once per Update, right after the input frame starts. A host that receives input on
        /// another thread feeds its queued events to <see cref="Input.Input"/> from here.
        /// </summary>
        public Action? PumpInput { get; set; }

        public void Start()
        {
            if (_playing) return;
            _playing = true;
            _needsWarm = true;
            Time.Reset();
            _updateWatch.Restart();
            _fixedWatch.Restart();
            Input.Input.ClearAll();
        }

        public void TickUpdate(float viewportW, float viewportH, bool pointerCapturedOrOver)
        {
            if (!_playing) return;

            if (SceneManager.HasPendingLoad)
            {
                SceneManager.ProcessPendingLoad(
                    callOnDestroyAll: () => SceneService.ForEachActiveBehavior(b => b.__OnDestroy()),
                    clearRegistries: () =>
                    {
                        PostProcessVolume.ClearAll();
                        Component.UI.Canvas.ClearAll();
                        UIEventSystem.Reset();
                        Input.Input.ClearAll();
                    },
                    rebuildCaches: () =>
                    {
                        _needsWarm = true;
                        _collidersWarm = false;
                    },
                    callAwakeStart: () =>
                    {
                        _awakened = false;
                        _started = false;
                    });
            }

            if (!_awakened) { SceneService.ForEachActiveBehavior(b => b.__Awake()); _awakened = true; }
            if (!_started) { SceneService.ForEachActiveBehavior(b => b.__Start()); _started = true; }
            if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

            var dt = _updateWatch.IsRunning ? _updateWatch.Elapsed.TotalSeconds : 0.0;
            _updateWatch.Restart();
            if (dt > 0.05) dt = 0.05;
            Time.BeginUpdate(dt);
            Input.Input.NewFrame((float)dt);
            // NewFrame clears the pressed/released edges. A host that queues its key and pointer
            // events must apply them here, or GetKeyDown / GetActionDown never fire.
            PumpInput?.Invoke();
            Input.Input.PollHardwareHeldKeys();
            Input.Input.PollPlayMouseButtons(pointerCapturedOrOver);
            float uiW = Math.Max(1f, viewportW);
            float uiH = Math.Max(1f, viewportH);
            Input.Input.FeedViewportSize(uiW, uiH);
            UIEventSystem.ProcessEvents(uiW, uiH);
            if (NetworkManager.IsActive)
                NetworkManager.Update();
            AudioManager.UpdateListenerTransform();
            SceneService.TickActiveBehaviors(Profiler.ScriptPhase.Update);
            SceneService.TickActiveBehaviors(Profiler.ScriptPhase.LateUpdate);
            Profiler.PublishScriptCosts();
            Input.Input.EndFrame();
        }

        public void TickFixed()
        {
            if (!_playing) return;
            if (_needsWarm) { WarmAllColliders(); _needsWarm = false; }

            double dt = _fixedWatch.IsRunning ? _fixedWatch.Elapsed.TotalSeconds : FixedDt;
            _fixedWatch.Restart();
            if (dt > 0.1) dt = 0.1;
            _fixedAccum += dt;
            if (_fixedAccum > 0.25) _fixedAccum = 0.25;
            while (_fixedAccum >= FixedDt)
            {
                Time.BeginFixedUpdate(FixedDt);
                PhysicsCache.Tick();
                SceneService.TickActiveBehaviors(Profiler.ScriptPhase.FixedUpdate);
                _fixedAccum -= FixedDt;
            }
        }

        void WarmAllColliders()
        {
            if (_collidersWarm) return;
            foreach (var root in SceneService.Root)
                Traverse(root, b =>
                {
                    if (b is Collider c)
                    {
                        var t = c.GetType();
                        string[] names = { "EnsureReady", "EnsureBaked", "Bake", "Precompute", "Rebuild", "SyncFromTransform", "Warm" };
                        foreach (var n in names)
                        {
                            var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                            if (m != null) { try { m.Invoke(c, null); } catch { } break; }
                        }
                    }
                });
            _collidersWarm = true;
        }

        static void Traverse(GameObject go, Action<Behavior> a)
        {
            var behaviors = go.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
                a(behaviors[i]);
            var children = go.Children;
            for (int i = 0; i < children.Count; i++)
                Traverse(children[i], a);
        }
    }
}
