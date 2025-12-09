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
            _root.CollectionChanged -= OnRootChanged;
            _root.Clear();
            foreach (var go in items) _root.Add(go);
            _root.CollectionChanged += OnRootChanged;
            Changed?.Invoke();
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
            NotifyChanged();
        }



        private static void OnRootChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Changed?.Invoke();

    }
}
