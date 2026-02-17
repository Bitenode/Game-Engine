using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Game_Engine.Core
{
    public static class SceneService
    {
        // Backing store so we can rewire CollectionChanged when the root changes.
        private static ObservableCollection<GameObject> _root = new();

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

        /// <summary>Signal listeners that something in the scene changed.</summary>
        public static void NotifyChanged() => Changed?.Invoke();

        public static event Action? Changed;

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
            Changed?.Invoke();
        }

        public static bool Remove(GameObject go)
        {
            var removed = _root.Remove(go);
            if (removed) Changed?.Invoke();
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
            Changed?.Invoke();
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
            Changed?.Invoke();
        }

        /// <summary>Save current root to a JSON scene file.</summary>
        public static void SaveToFile(string path)
        {
            SceneSerialization.SaveScene(path, Root);
        }

        /// <summary>Load a JSON scene file and replace the current root.</summary>
        public static void LoadFromFile(string path)
        {
            var loaded = SceneSerialization.LoadScene(path);
            SceneService.ReplaceAll(loaded);
            MaterialRebind.RepairScene();
            RebuildVegetation();
            NotifyChanged();
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
            => Changed?.Invoke();

    }
}
