using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Game_Engine.Core.Planet;

namespace Game_Engine.Core
{
    public static class SceneService
    {
        private static bool _suppressDirtyTracking;

        /// <summary>
        /// While true, <see cref="PlanetTerrain"/> skips applying vegetation from a synchronous .planet read so
        /// <see cref="PlanetVegetationSceneLoader"/> can deserialize off-thread after <see cref="LoadFromFile"/>.
        /// </summary>
        public static bool DeferPlanetVegetationImport { get; private set; }

        private static string? _currentScenePath;
        private static bool _isDirty;

        // Backing store so we can rewire CollectionChanged when the root changes.
        private static ObservableCollection<GameObject> _root = new();
        public static string? CurrentScenePath => _currentScenePath;
        public static bool IsDirty => _isDirty;

        /// <summary>Top-level scene objects.</summary>
        public static ObservableCollection<GameObject> Root
        {
            get => _root;
            private set
            {
                if (ReferenceEquals(_root, value)) return;

                if (_root != null)
                    _root.CollectionChanged -= OnRootChanged;

                _root = value ?? new ObservableCollection<GameObject>();
                _root.CollectionChanged += OnRootChanged;

                Changed?.Invoke();
            }
        }

        /// <summary>Replace the current root collection (e.g. when loading a scene).</summary>
        public static void AttachRoot(ObservableCollection<GameObject> root) => Root = root;

        /// <summary>True while Game view is in Play. Transform motion must not fire <see cref="Changed"/>.</summary>
        public static bool PlayMode { get; set; }

        static readonly List<Behavior> s_behaviorScratch = new(256);

        /// <summary>
        /// Walk enabled, visible scene behaviors without allocating per node.
        /// Snapshot first so Update can spawn/despawn safely.
        /// </summary>
        public static void ForEachActiveBehavior(Action<Behavior> action)
        {
            s_behaviorScratch.Clear();
            var roots = _root;
            for (int i = 0; i < roots.Count; i++)
                CollectActiveBehaviors(roots[i], s_behaviorScratch);
            for (int i = 0; i < s_behaviorScratch.Count; i++)
                action(s_behaviorScratch[i]);
        }

        /// <summary>
        /// Tick every active behavior for one phase. When script sampling is on,
        /// costs are attributed per type for the profiler breakdown.
        /// </summary>
        public static void TickActiveBehaviors(Profiler.ScriptPhase phase)
        {
            s_behaviorScratch.Clear();
            var roots = _root;
            for (int i = 0; i < roots.Count; i++)
                CollectActiveBehaviors(roots[i], s_behaviorScratch);
            for (int i = 0; i < s_behaviorScratch.Count; i++)
                Profiler.InvokeAndRecord(s_behaviorScratch[i], phase);
        }

        static void CollectActiveBehaviors(GameObject go, List<Behavior> dst)
        {
            if (!go.Enabled || go.HideInHierarchy) return;
            var behaviors = go.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
                dst.Add(behaviors[i]);
            var children = go.Children;
            for (int i = 0; i < children.Count; i++)
                CollectActiveBehaviors(children[i], dst);
        }

        /// <summary>Signal listeners that something in the scene changed.</summary>
        public static void NotifyChanged() => RaiseChanged(markDirty: true);

        public static event Action? Changed;
        public static event Action<bool>? DirtyStateChanged;

        /// <summary>
        /// Fired when the entire scene is replaced (e.g. loading a new .scene file).
        /// Subscribers should perform full cleanup (flush GPU caches, clear stale references, etc.).
        /// This fires BEFORE <see cref="Changed"/> during a scene replacement.
        /// </summary>
        public static event Action? SceneReplaced;

        // convenience helpers (all notify)
        public static void Add(GameObject go)
        {
            _root.Add(go);
            RaiseChanged(markDirty: true);
        }

        public static bool Remove(GameObject go)
        {
            var removed = _root.Remove(go);
            if (removed) RaiseChanged(markDirty: true);
            return removed;
        }

        public static void ReplaceAll(IEnumerable<GameObject> items)
        {
            SceneReplaced?.Invoke();

            // Tear down ALL old behaviors (including those on disabled GameObjects)
            // so that static component registries (Canvas._all, Light._allLights, etc.)
            // are properly cleaned up. __OnDestroy is idempotent (_destroyed guard).
            foreach (var go in _root)
                DestroyBehaviorsRecursive(go);

            _root.CollectionChanged -= OnRootChanged;
            _root.Clear();
            foreach (var go in items) _root.Add(go);
            _root.CollectionChanged += OnRootChanged;
            RaiseChanged(markDirty: true);
        }

        /// <summary>
        /// Recursively call __OnDestroy on all behaviors in the hierarchy,
        /// including those on disabled GameObjects. This ensures static
        /// component registries are cleaned up during scene replacement.
        /// </summary>
        private static void DestroyBehaviorsRecursive(GameObject go)
        {
            foreach (var b in go.Behaviors)
            {
                try { b.__OnDestroy(); } catch { }
            }
            foreach (var child in go.Children)
                DestroyBehaviorsRecursive(child);
        }

        public static void Clear()
        {
            _root.Clear();
            RaiseChanged(markDirty: true);
        }

        /// <summary>Save current root to a JSON scene file.</summary>
        public static void SaveToFile(string path)
        {
            SceneSerialization.SaveScene(path, Root);
            _currentScenePath = path;
            SetDirty(false);
        }

        /// <summary>Load a JSON scene file and replace the current root.</summary>
        public static void LoadFromFile(string path)
        {
            _suppressDirtyTracking = true;
            DeferPlanetVegetationImport = true;
            try
            {
                var loaded = SceneSerialization.LoadScene(path);
                SceneService.ReplaceAll(loaded);
                MaterialRebind.RepairScene();
                RebuildVegetation();
                PlanetVegetationSceneLoader.ScheduleHydrateAfterSceneReplace();
            }
            finally
            {
                DeferPlanetVegetationImport = false;
            }
            _currentScenePath = path;
            _suppressDirtyTracking = false;
            SetDirty(false);
            Changed?.Invoke();
        }

        /// <summary>Rebuild grass for any VegetationPainter with GrassBuilt=true (grass is not serialized).</summary>
        private static void RebuildVegetation()
        {
            try
            {
                foreach (var root in Root)
                    RebuildVegetationRecursive(root);
            }
            catch { }
        }

        private static void RebuildVegetationRecursive(GameObject go)
        {
            foreach (var b in go.Behaviors)
            {
                if (b is Component.VegetationPainter vp && vp.GrassBuilt)
                {
                    try { vp.BuildOnTerrain(); }
                    catch (System.Exception ex) { Log.Warning($"[VegetationPainter] Rebuild failed: {ex.Message}"); }
                }
            }
            foreach (var child in go.Children)
                RebuildVegetationRecursive(child);
        }



        private static void OnRootChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => RaiseChanged(markDirty: true);

        private static void RaiseChanged(bool markDirty)
        {
            if (PlayMode)
                return;
            if (markDirty && !_suppressDirtyTracking)
                SetDirty(true);
            Changed?.Invoke();
        }

        public static void SetCurrentScenePath(string? path) => _currentScenePath = path;

        public static void SetDirty(bool dirty)
        {
            if (_isDirty == dirty) return;
            _isDirty = dirty;
            DirtyStateChanged?.Invoke(_isDirty);
        }

    }
}
