#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Tone mapping operator for HDR → LDR conversion.</summary>
    public enum ToneMapping { None, Reinhard, ACES }

    /// <summary>
    /// Post-processing volume component. Attach to a camera or a volume trigger
    /// to define screen-space effects: Bloom, SSAO, Fog, Color Grading, Vignette, FXAA.
    /// </summary>
    public sealed class PostProcessVolume : Behavior
    {
        // ── Bloom ──
        [Persist] public bool BloomEnabled { get; set; } = true;
        [Persist] public float BloomThreshold { get; set; } = 0.8f;
        [Persist] public float BloomIntensity { get; set; } = 0.5f;
        [Persist] public int BloomIterations { get; set; } = 5;

        // ── SSAO ──
        [Persist] public bool SSAOEnabled { get; set; } = false;
        [Persist] public float SSAORadius { get; set; } = 0.5f;
        [Persist] public float SSAOIntensity { get; set; } = 1f;
        [Persist] public int SSAOSamples { get; set; } = 16;

        // ── Fog ──
        [Persist] public bool FogEnabled { get; set; } = false;
        [Persist] public SN.Vector3 FogColor { get; set; } = new SN.Vector3(0.7f, 0.75f, 0.8f);
        [Persist] public float FogDensity { get; set; } = 0.02f;
        [Persist] public float FogStart { get; set; } = 10f;
        [Persist] public float FogEnd { get; set; } = 100f;
        [Persist] public bool FogHeightBased { get; set; } = false;
        [Persist] public float FogHeightFalloff { get; set; } = 0.1f;

        // ── Color Grading ──
        [Persist] public bool ColorGradingEnabled { get; set; } = false;
        [Persist] public float Brightness { get; set; } = 0f;       // -1..1
        [Persist] public float Contrast { get; set; } = 1f;         // 0..2
        [Persist] public float Saturation { get; set; } = 1f;       // 0..2
        [Persist] public float Exposure { get; set; } = 1f;
        [Persist] public ToneMapping ToneMap { get; set; } = ToneMapping.ACES;

        // ── Vignette ──
        [Persist] public bool VignetteEnabled { get; set; } = false;
        [Persist] public float VignetteIntensity { get; set; } = 0.3f;
        [Persist] public float VignetteSmoothness { get; set; } = 0.4f;

        // ── FXAA ──
        [Persist] public bool FXAAEnabled { get; set; } = true;
        [Persist] public float FXAAThreshold { get; set; } = 0.0625f;
        [Persist] public float FXAAThresholdMin { get; set; } = 0.0312f;

        // ── Global vs Local ──
        [Persist] public bool IsGlobal { get; set; } = true;
        [Persist] public float BlendDistance { get; set; } = 5f;
        [Persist] public int Priority { get; set; } = 0;

        // ── Registry for the rendering pipeline ──
        private static readonly System.Collections.Generic.List<PostProcessVolume> _volumes = new(4);
        public static System.Collections.Generic.IReadOnlyList<PostProcessVolume> ActiveVolumes => _volumes;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_volumes.Contains(this)) _volumes.Add(this);
            _volumes.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public override void OnDisable()
        {
            _volumes.Remove(this);
            base.OnDisable();
        }

        /// <summary>Get the highest-priority active volume (or null).</summary>
        public static PostProcessVolume? GetActive()
        {
            // Purge stale entries (destroyed GameObjects that never called OnDisable)
            for (int i = _volumes.Count - 1; i >= 0; i--)
            {
                if (_volumes[i].gameObject == null)
                    _volumes.RemoveAt(i);
            }

            for (int i = _volumes.Count - 1; i >= 0; i--)
            {
                if (_volumes[i].IsActiveAndEnabled && _volumes[i].IsGlobal)
                    return _volumes[i];
            }
            return null; // no active global volume
        }

        /// <summary>Clear all volumes (call on game stop to prevent stale entries).</summary>
        public static void ClearAll() => _volumes.Clear();
    }
}
