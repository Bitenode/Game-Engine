#nullable enable
using System.Collections.Generic;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Physics
{
    /// <summary>
    /// Shared per-frame collision cache used by both Rigidbody and CharacterController.
    /// Call <see cref="RefreshFrame"/> once per physics tick to update the caches.
    /// Both systems read from these lists instead of each doing separate scene traversals.
    /// </summary>
    public static class PhysicsCache
    {
        private static int _lastFrame = -1;

        // ── Cached lists ──
        private static readonly List<Terrain> _terrains = new(4);
        private static readonly HashSet<GameObject> _terrainGOs = new();
        private static readonly List<MeshCollider> _meshColliders = new(64);
        private static readonly List<Collider> _nonMeshColliders = new(64);
        private static readonly List<Collider> _triggerColliders = new(32);

        /// <summary>All active Terrain components in the scene.</summary>
        public static IReadOnlyList<Terrain> Terrains => _terrains;

        /// <summary>GameObjects that are terrains or terrain chunks (to skip their MeshColliders).</summary>
        public static IReadOnlyCollection<GameObject> TerrainGOs => _terrainGOs;

        /// <summary>All active non-trigger MeshColliders (excluding terrain MeshColliders).</summary>
        public static IReadOnlyList<MeshCollider> MeshColliders => _meshColliders;

        /// <summary>All active non-trigger, non-mesh colliders (BoxCollider, CapsuleCollider, etc.).</summary>
        public static IReadOnlyList<Collider> NonMeshColliders => _nonMeshColliders;

        /// <summary>All active trigger colliders in the scene.</summary>
        public static IReadOnlyList<Collider> TriggerColliders => _triggerColliders;

        /// <summary>
        /// Refresh all caches. Safe to call multiple times per frame — only rebuilds once.
        /// </summary>
        static readonly List<Terrain> _terrainScratch = new(4);
        static readonly List<Collider> _colliderScratch = new(64);
        static readonly Stack<GameObject> _walkScratch = new(64);

        public static void RefreshFrame()
        {
            int frame = UnityFrameCounter;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            _terrains.Clear();
            _terrainGOs.Clear();
            _meshColliders.Clear();
            _nonMeshColliders.Clear();
            _triggerColliders.Clear();

            // Terrains
            _terrainScratch.Clear();
            SceneQuery.CollectBehaviors(_terrainScratch, _walkScratch);
            for (int ti = 0; ti < _terrainScratch.Count; ti++)
            {
                var t = _terrainScratch[ti];
                if (!t.Enabled || t.gameObject == null) continue;
                _terrains.Add(t);
                _terrainGOs.Add(t.gameObject);
                for (int i = 0; i < t.gameObject.Children.Count; i++)
                    _terrainGOs.Add(t.gameObject.Children[i]);
            }

            // Planet roots only — mesh colliders check ancestry instead of us hashing the
            // whole planet hierarchy (chunks + vegetation) every physics tick.
            foreach (var p in PlanetTerrain.ActivePlanets)
            {
                if (p?.gameObject == null) continue;
                _terrainGOs.Add(p.gameObject);
            }

            // All colliders in one pass
            _colliderScratch.Clear();
            SceneQuery.CollectBehaviors(_colliderScratch, _walkScratch);
            for (int ci = 0; ci < _colliderScratch.Count; ci++)
            {
                var c = _colliderScratch[ci];
                if (!c.Enabled) continue;

                if (c.IsTrigger)
                {
                    _triggerColliders.Add(c);
                    continue;
                }

                if (c is MeshCollider mc)
                {
                    // Skip MeshColliders on terrain / planet GameObjects — use heightfield instead
                    if (mc.gameObject != null && IsUnderTerrain(mc.gameObject)) continue;
                    _meshColliders.Add(mc);
                }
                else if (c is PlanetCollider)
                {
                    // Density raycasts own the planet. The AABB is the whole globe and
                    // would trap a player standing on the surface.
                    continue;
                }
                else
                {
                    _nonMeshColliders.Add(c);
                }
            }
        }

        /// <summary>
        /// Sample terrain height at a world XZ position across all cached terrains.
        /// Returns true if any terrain was hit, with the highest Y and its normal.
        /// </summary>
        public static bool SampleTerrainHeight(float worldX, float worldZ, out float groundY, out System.Numerics.Vector3 groundNormal)
        {
            groundY = float.NegativeInfinity;
            groundNormal = System.Numerics.Vector3.UnitY;
            bool anyHit = false;

            for (int i = 0; i < _terrains.Count; i++)
            {
                if (_terrains[i].SampleHeightWorld(worldX, worldZ, out float y, out var n))
                {
                    if (y > groundY)
                    {
                        groundY = y;
                        groundNormal = n;
                        anyHit = true;
                    }
                }
            }

            return anyHit;
        }

        // Advances once per render frame. Several fixed steps run back-to-back inside one
        // frame and see the same scene graph, so a rebuild per step was wasted traversal.
        private static int _frameCount;
        private static int _lastRenderFrame = int.MinValue;
        private static int UnityFrameCounter => _frameCount;

        /// <summary>Call at the start of each physics tick to advance the frame counter.</summary>
        public static void Tick()
        {
            int render = Time.frameCount;
            if (render == _lastRenderFrame && _frameCount > 0)
                return;
            _lastRenderFrame = render;
            _frameCount++;
        }

        /// <summary>Force a cache rebuild on next access.</summary>
        public static void Invalidate() => _lastFrame = -1;

        static bool IsUnderTerrain(GameObject go)
        {
            for (var cur = go; cur != null; cur = cur.Parent)
            {
                if (_terrainGOs.Contains(cur)) return true;
            }
            return false;
        }
    }
}
