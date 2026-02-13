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
            foreach (var t in SceneQuery.FindBehaviors<Terrain>())
            {
                if (!t.Enabled || t.gameObject == null) continue;
                _terrains.Add(t);
                _terrainGOs.Add(t.gameObject);
                for (int i = 0; i < t.gameObject.Children.Count; i++)
                    _terrainGOs.Add(t.gameObject.Children[i]);
            }

            // All colliders in one pass
            foreach (var c in SceneQuery.FindBehaviors<Collider>())
            {
                if (!c.Enabled) continue;

                if (c.IsTrigger)
                {
                    _triggerColliders.Add(c);
                    continue;
                }

                if (c is MeshCollider mc)
                {
                    // Skip MeshColliders on terrain GameObjects — use heightmap instead
                    if (mc.gameObject != null && _terrainGOs.Contains(mc.gameObject)) continue;
                    _meshColliders.Add(mc);
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

        // Simple frame counter — increments each time FixedUpdate is called
        private static int _frameCount;
        private static int UnityFrameCounter => _frameCount;

        /// <summary>Call at the start of each physics tick to advance the frame counter.</summary>
        public static void Tick() => _frameCount++;

        /// <summary>Force a cache rebuild on next access.</summary>
        public static void Invalidate() => _lastFrame = -1;
    }
}
