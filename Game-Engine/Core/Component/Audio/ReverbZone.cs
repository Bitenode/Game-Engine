#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Preset reverb environments.</summary>
    public enum ReverbPreset
    {
        None,
        Room,
        Hall,
        Cathedral,
        Cave,
        Arena,
        Forest,
        Underwater,
        Bathroom,
        StoneRoom,
        Auditorium
    }

    /// <summary>
    /// Reverb zone component — applies reverb to audio sources within its volume.
    /// When the AudioListener enters this zone, audio sources are processed with
    /// the configured reverb effect. Zones can overlap; the closest zone wins.
    /// </summary>
    [ComponentCategory("Audio")]
    public sealed class ReverbZone : Behavior
    {
        // ── Configuration ──
        [Persist] public ReverbPreset Preset { get; set; } = ReverbPreset.Room;

        /// <summary>Inner radius — full reverb within this distance.</summary>
        [Persist] public float MinDistance { get; set; } = 5f;

        /// <summary>Outer radius — reverb fades to zero at this distance.</summary>
        [Persist] public float MaxDistance { get; set; } = 20f;

        // ── Manual reverb parameters (used when Preset is None) ──
        [Persist] public float DecayTime { get; set; } = 1.5f;
        [Persist] public float Density { get; set; } = 1f;
        [Persist] public float Diffusion { get; set; } = 1f;
        [Persist] public float ReflectionsGain { get; set; } = 0.05f;
        [Persist] public float ReflectionsDelay { get; set; } = 0.007f;
        [Persist] public float LateReverbGain { get; set; } = 1.26f;
        [Persist] public float LateReverbDelay { get; set; } = 0.011f;

        // ── Static registry ──
        private static readonly List<ReverbZone> _zones = new(16);
        public static IReadOnlyList<ReverbZone> AllZones => _zones;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_zones.Contains(this)) _zones.Add(this);
        }

        public override void OnDisable()
        {
            _zones.Remove(this);
            base.OnDisable();
        }

        /// <summary>Get the zone center in world space.</summary>
        public SN.Vector3 GetWorldCenter()
            => new((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

        /// <summary>
        /// Compute the reverb blend weight based on the listener distance.
        /// Returns 0 (outside zone) to 1 (fully inside inner radius).
        /// </summary>
        public float GetBlendWeight(SN.Vector3 listenerPos)
        {
            float dist = SN.Vector3.Distance(listenerPos, GetWorldCenter());
            if (dist <= MinDistance) return 1f;
            if (dist >= MaxDistance) return 0f;
            return 1f - (dist - MinDistance) / (MaxDistance - MinDistance);
        }

        /// <summary>
        /// Find the most relevant reverb zone for the given listener position.
        /// Returns null if no zone is active.
        /// </summary>
        public static (ReverbZone? zone, float weight) GetActiveZone(SN.Vector3 listenerPos)
        {
            ReverbZone? best = null;
            float bestWeight = 0f;

            for (int i = 0; i < _zones.Count; i++)
            {
                float w = _zones[i].GetBlendWeight(listenerPos);
                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = _zones[i];
                }
            }
            return (best, bestWeight);
        }

        /// <summary>Get reverb parameters for a preset.</summary>
        public static (float decayTime, float density, float diffusion) GetPresetParams(ReverbPreset preset)
        {
            return preset switch
            {
                ReverbPreset.Room => (0.8f, 0.5f, 0.8f),
                ReverbPreset.Hall => (2.0f, 0.7f, 1.0f),
                ReverbPreset.Cathedral => (4.0f, 0.9f, 1.0f),
                ReverbPreset.Cave => (3.0f, 1.0f, 0.6f),
                ReverbPreset.Arena => (5.0f, 0.8f, 1.0f),
                ReverbPreset.Forest => (1.5f, 0.3f, 0.9f),
                ReverbPreset.Underwater => (1.5f, 1.0f, 1.0f),
                ReverbPreset.Bathroom => (1.5f, 0.9f, 0.5f),
                ReverbPreset.StoneRoom => (2.3f, 0.8f, 0.7f),
                ReverbPreset.Auditorium => (4.3f, 0.6f, 1.0f),
                _ => (0f, 0f, 0f)
            };
        }
    }
}
