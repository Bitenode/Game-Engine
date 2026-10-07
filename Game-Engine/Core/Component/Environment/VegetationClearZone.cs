#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Keeps planet grass and streamed trees out of a circle around this object
    /// (buildings, campfires, placed props). Existing vegetation inside is removed.
    /// </summary>
    [ComponentCategory("Environment")]
    public sealed class VegetationClearZone : Behavior
    {
        static readonly List<VegetationClearZone> s_active = new();

        /// <summary>Zones currently enabled in the scene.</summary>
        public static IReadOnlyList<VegetationClearZone> Active => s_active;

        /// <summary>Bumped whenever a zone is added, removed, or resized.</summary>
        public static int Version { get; private set; }

        float _radius = 3f;

        /// <summary>Clear radius in world units, measured along the ground from this object's origin.</summary>
        [Persist, Range(0.25f, 100f)]
        public float Radius
        {
            get => _radius;
            set
            {
                float r = Math.Clamp(float.IsFinite(value) ? value : 3f, 0.25f, 100f);
                if (r == _radius) return;
                _radius = r;
                Version++;
            }
        }

        public SN.Vector3 WorldCenter => gameObject != null
            ? SceneGraphUtil.AccumulateWorld(gameObject).Translation
            : SN.Vector3.Zero;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!s_active.Contains(this))
                s_active.Add(this);
            Version++;
        }

        public override void OnDisable()
        {
            if (s_active.Remove(this))
                Version++;
            base.OnDisable();
        }
    }
}
