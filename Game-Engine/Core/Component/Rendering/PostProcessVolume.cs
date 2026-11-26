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
    [ComponentCategory("Rendering")]
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
        /// <summary>How strongly SSAO darkens ambient (0 = off, 1 = full).</summary>
        [Persist] public float SSAOIntensity { get; set; } = 1f;
        [Persist] public float SSAOBias { get; set; } = 0.025f;
        [Persist] public int SSAOSamples { get; set; } = 24;
        /// <summary>Bilateral blur edge sharpness vs depth (higher = sharper depth edges).</summary>
        [Persist] public float SSAODepthSigma { get; set; } = 80f;

        // ── SSR ──
        [Persist] public bool SSREnabled { get; set; } = false;
        [Persist] public int SSRMaxRaySteps { get; set; } = 64;
        [Persist] public float SSRRoughnessCutoff { get; set; } = 0.6f;
        [Persist] public float SSRMaxRayDistance { get; set; } = 50f;

        // ── TAA ──
        [Persist] public bool TAAEnabled { get; set; } = false;
        /// <summary>Weight for current frame (0.1 ≈ 10% new, 90% history).</summary>
        [Persist] public float TAAFrameBlend { get; set; } = 0.12f;
        [Persist] public float TAASharpen { get; set; } = 0.35f;

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

        // ── Volumetric Fog ──
        [Persist] public bool VolumetricFogEnabled { get; set; } = false;
        /// <summary>Base fog density for volumetric scattering.</summary>
        [Persist] public float VolumetricFogDensity { get; set; } = 0.02f;
        /// <summary>Henyey-Greenstein scattering anisotropy (-1 to 1). Positive = forward scattering.</summary>
        [Persist] public float VolumetricFogAnisotropy { get; set; } = 0.3f;
        /// <summary>Scattering intensity multiplier for in-scattered light.</summary>
        [Persist] public float VolumetricFogScattering { get; set; } = 1.0f;
        /// <summary>Height below which fog is at full density. Above this, density falls off.</summary>
        [Persist] public float VolumetricFogHeightFalloff { get; set; } = 0.1f;
        /// <summary>Base height of the fog volume (world Y).</summary>
        [Persist] public float VolumetricFogBaseHeight { get; set; } = 0f;
        /// <summary>Scale of 3D noise applied to fog density.</summary>
        [Persist] public float VolumetricFogNoiseScale { get; set; } = 0.1f;
        /// <summary>Speed of noise animation.</summary>
        [Persist] public float VolumetricFogNoiseSpeed { get; set; } = 0.5f;
        /// <summary>Maximum ray march distance.</summary>
        [Persist] public float VolumetricFogMaxDistance { get; set; } = 200f;
        /// <summary>Color tint for the volumetric fog.</summary>
        [Persist] public SN.Vector3 VolumetricFogColor { get; set; } = new SN.Vector3(1f, 1f, 1f);
        /// <summary>Number of ray march steps (higher = better quality, lower performance).</summary>
        [Persist] public int VolumetricFogSteps { get; set; } = 32;

        // ── Depth of Field ──
        [Persist] public bool DepthOfFieldEnabled { get; set; } = false;
        /// <summary>Focus distance from camera in world units.</summary>
        [Persist] public float DoFFocusDistance { get; set; } = 10f;
        /// <summary>Aperture (f-stop). Lower = shallower depth of field.</summary>
        [Persist] public float DoFAperture { get; set; } = 5.6f;
        /// <summary>Focal length in mm (affects bokeh size).</summary>
        [Persist] public float DoFFocalLength { get; set; } = 50f;
        /// <summary>Maximum blur radius in pixels.</summary>
        [Persist] public float DoFMaxBlurRadius { get; set; } = 8f;
        /// <summary>Blend factor for near-field blur (0 = no near blur).</summary>
        [Persist] public float DoFNearBlurScale { get; set; } = 1f;
        /// <summary>Blend factor for far-field blur.</summary>
        [Persist] public float DoFFarBlurScale { get; set; } = 1f;

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

        /// <summary>Get the highest-priority active global volume (or null). Kept for backward compatibility.</summary>
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

        [ThreadStatic] private static PostProcessSettings? s_blendedScratch;
        [ThreadStatic] private static System.Collections.Generic.List<PostProcessVolume>? s_localScratch;

        /// <summary>
        /// Sample the blended post-processing settings at <paramref name="cameraWorldPos"/>:
        /// starts from the highest-priority enabled global volume, then smoothly blends every
        /// overlapping local volume (<see cref="IsGlobal"/> == false) ordered by <see cref="Priority"/>
        /// using signed distance to the volume's oriented box and <see cref="BlendDistance"/>.
        /// Returns null when no global or overlapping local volume is active.
        /// </summary>
        public static PostProcessSettings? SampleBlended(SN.Vector3 cameraWorldPos)
        {
            for (int i = _volumes.Count - 1; i >= 0; i--)
            {
                if (_volumes[i].gameObject == null)
                    _volumes.RemoveAt(i);
            }

            PostProcessVolume? globalVol = null;
            var locals = s_localScratch ??= new System.Collections.Generic.List<PostProcessVolume>(8);
            locals.Clear();

            for (int i = 0; i < _volumes.Count; i++)
            {
                var v = _volumes[i];
                if (!v.IsActiveAndEnabled || v.gameObject == null) continue;
                if (v.IsGlobal)
                {
                    if (globalVol == null || v.Priority >= globalVol.Priority)
                        globalVol = v;
                }
                else
                {
                    locals.Add(v);
                }
            }

            if (globalVol == null && locals.Count == 0)
                return null;

            if (locals.Count > 1)
                locals.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var result = s_blendedScratch ??= new PostProcessSettings();
            bool hasAny = false;

            if (globalVol != null)
            {
                result.CopyFrom(globalVol);
                hasAny = true;
            }

            for (int i = 0; i < locals.Count; i++)
            {
                var local = locals[i];
                float dist = ComputeSignedBoxDistance(local, cameraWorldPos);
                float blendDist = MathF.Max(local.BlendDistance, 0f);
                float weight;
                if (dist <= 0f)
                    weight = 1f;
                else if (blendDist <= 1e-4f)
                    weight = 0f;
                else
                    weight = 1f - Math.Clamp(dist / blendDist, 0f, 1f);

                if (weight <= 0f) continue;

                if (!hasAny)
                {
                    result.CopyFrom(local);
                    if (weight < 0.9999f)
                        result.ScaleEffectStrengths(weight);
                    hasAny = true;
                }
                else
                {
                    result.BlendWith(local, weight);
                }
            }

            return hasAny ? result : null;
        }

        private static float ComputeSignedBoxDistance(PostProcessVolume vol, SN.Vector3 worldPos)
        {
            var go = vol.gameObject;
            if (go == null) return float.MaxValue;

            SN.Vector3 localCenter = SN.Vector3.Zero;
            SN.Vector3 localHalf = new SN.Vector3(0.5f, 0.5f, 0.5f);

            // Prefer BoxCollider bounds if attached to this volume's GameObject
            for (int i = 0; i < go.Behaviors.Count; i++)
            {
                if (go.Behaviors[i] is BoxCollider bc)
                {
                    localCenter = new SN.Vector3((float)bc.Center.X, (float)bc.Center.Y, (float)bc.Center.Z);
                    localHalf = new SN.Vector3(
                        MathF.Abs((float)bc.Size.X) * 0.5f,
                        MathF.Abs((float)bc.Size.Y) * 0.5f,
                        MathF.Abs((float)bc.Size.Z) * 0.5f);
                    break;
                }
            }

            var worldMat = TransformUtil.WorldFromTransform(go.Transform);
            // Extract world-space basis axes and scale so distance is measured in true world units
            var axisX = new SN.Vector3(worldMat.M11, worldMat.M12, worldMat.M13);
            var axisY = new SN.Vector3(worldMat.M21, worldMat.M22, worldMat.M23);
            var axisZ = new SN.Vector3(worldMat.M31, worldMat.M32, worldMat.M33);
            float sx = MathF.Max(axisX.Length(), 1e-4f);
            float sy = MathF.Max(axisY.Length(), 1e-4f);
            float sz = MathF.Max(axisZ.Length(), 1e-4f);
            axisX /= sx;
            axisY /= sy;
            axisZ /= sz;

            var worldCenter = SN.Vector3.Transform(localCenter, worldMat);
            var rel = worldPos - worldCenter;

            // Project onto the oriented box axes in world units
            float px = MathF.Abs(SN.Vector3.Dot(rel, axisX)) - localHalf.X * sx;
            float py = MathF.Abs(SN.Vector3.Dot(rel, axisY)) - localHalf.Y * sy;
            float pz = MathF.Abs(SN.Vector3.Dot(rel, axisZ)) - localHalf.Z * sz;

            float ox = MathF.Max(px, 0f);
            float oy = MathF.Max(py, 0f);
            float oz = MathF.Max(pz, 0f);
            float outside = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
            float inside = MathF.Min(MathF.Max(px, MathF.Max(py, pz)), 0f);
            return outside + inside;
        }

        /// <summary>Clear all volumes (call on game stop to prevent stale entries).</summary>
        public static void ClearAll() => _volumes.Clear();
    }

    /// <summary>
    /// Blended post-processing snapshot produced by <see cref="PostProcessVolume.SampleBlended"/>.
    /// </summary>
    public sealed class PostProcessSettings
    {
        // Bloom
        public bool BloomEnabled;
        public float BloomThreshold = 0.8f;
        public float BloomIntensity = 0.5f;
        public int BloomIterations = 5;

        // SSAO
        public bool SSAOEnabled;
        public float SSAORadius = 0.5f;
        public float SSAOIntensity = 1f;
        public float SSAOBias = 0.025f;
        public int SSAOSamples = 24;
        public float SSAODepthSigma = 80f;

        // SSR
        public bool SSREnabled;
        public int SSRMaxRaySteps = 64;
        public float SSRRoughnessCutoff = 0.6f;
        public float SSRMaxRayDistance = 50f;

        // TAA
        public bool TAAEnabled;
        public float TAAFrameBlend = 0.12f;
        public float TAASharpen = 0.35f;

        // Fog
        public bool FogEnabled;
        public SN.Vector3 FogColor = new SN.Vector3(0.7f, 0.75f, 0.8f);
        public float FogDensity = 0.02f;
        public float FogStart = 10f;
        public float FogEnd = 100f;
        public bool FogHeightBased;
        public float FogHeightFalloff = 0.1f;

        // Color Grading
        public bool ColorGradingEnabled;
        public float Brightness;
        public float Contrast = 1f;
        public float Saturation = 1f;
        public float Exposure = 1f;
        public ToneMapping ToneMap = ToneMapping.ACES;

        // Vignette
        public bool VignetteEnabled;
        public float VignetteIntensity = 0.3f;
        public float VignetteSmoothness = 0.4f;

        // FXAA
        public bool FXAAEnabled = true;
        public float FXAAThreshold = 0.0625f;
        public float FXAAThresholdMin = 0.0312f;

        // Volumetric Fog
        public bool VolumetricFogEnabled;
        public float VolumetricFogDensity = 0.02f;
        public float VolumetricFogAnisotropy = 0.3f;
        public float VolumetricFogScattering = 1.0f;
        public float VolumetricFogHeightFalloff = 0.1f;
        public float VolumetricFogBaseHeight;
        public float VolumetricFogNoiseScale = 0.1f;
        public float VolumetricFogNoiseSpeed = 0.5f;
        public float VolumetricFogMaxDistance = 200f;
        public SN.Vector3 VolumetricFogColor = SN.Vector3.One;
        public int VolumetricFogSteps = 32;

        // Depth of Field
        public bool DepthOfFieldEnabled;
        public float DoFFocusDistance = 10f;
        public float DoFAperture = 5.6f;
        public float DoFFocalLength = 50f;
        public float DoFMaxBlurRadius = 8f;
        public float DoFNearBlurScale = 1f;
        public float DoFFarBlurScale = 1f;

        public static PostProcessSettings FromVolume(PostProcessVolume v)
        {
            var s = new PostProcessSettings();
            s.CopyFrom(v);
            return s;
        }

        public void CopyFrom(PostProcessVolume v)
        {
            BloomEnabled = v.BloomEnabled;
            BloomThreshold = v.BloomThreshold;
            BloomIntensity = v.BloomIntensity;
            BloomIterations = v.BloomIterations;

            SSAOEnabled = v.SSAOEnabled;
            SSAORadius = v.SSAORadius;
            SSAOIntensity = v.SSAOIntensity;
            SSAOBias = v.SSAOBias;
            SSAOSamples = v.SSAOSamples;
            SSAODepthSigma = v.SSAODepthSigma;

            SSREnabled = v.SSREnabled;
            SSRMaxRaySteps = v.SSRMaxRaySteps;
            SSRRoughnessCutoff = v.SSRRoughnessCutoff;
            SSRMaxRayDistance = v.SSRMaxRayDistance;

            TAAEnabled = v.TAAEnabled;
            TAAFrameBlend = v.TAAFrameBlend;
            TAASharpen = v.TAASharpen;

            FogEnabled = v.FogEnabled;
            FogColor = v.FogColor;
            FogDensity = v.FogDensity;
            FogStart = v.FogStart;
            FogEnd = v.FogEnd;
            FogHeightBased = v.FogHeightBased;
            FogHeightFalloff = v.FogHeightFalloff;

            ColorGradingEnabled = v.ColorGradingEnabled;
            Brightness = v.Brightness;
            Contrast = v.Contrast;
            Saturation = v.Saturation;
            Exposure = v.Exposure;
            ToneMap = v.ToneMap;

            VignetteEnabled = v.VignetteEnabled;
            VignetteIntensity = v.VignetteIntensity;
            VignetteSmoothness = v.VignetteSmoothness;

            FXAAEnabled = v.FXAAEnabled;
            FXAAThreshold = v.FXAAThreshold;
            FXAAThresholdMin = v.FXAAThresholdMin;

            VolumetricFogEnabled = v.VolumetricFogEnabled;
            VolumetricFogDensity = v.VolumetricFogDensity;
            VolumetricFogAnisotropy = v.VolumetricFogAnisotropy;
            VolumetricFogScattering = v.VolumetricFogScattering;
            VolumetricFogHeightFalloff = v.VolumetricFogHeightFalloff;
            VolumetricFogBaseHeight = v.VolumetricFogBaseHeight;
            VolumetricFogNoiseScale = v.VolumetricFogNoiseScale;
            VolumetricFogNoiseSpeed = v.VolumetricFogNoiseSpeed;
            VolumetricFogMaxDistance = v.VolumetricFogMaxDistance;
            VolumetricFogColor = v.VolumetricFogColor;
            VolumetricFogSteps = v.VolumetricFogSteps;

            DepthOfFieldEnabled = v.DepthOfFieldEnabled;
            DoFFocusDistance = v.DoFFocusDistance;
            DoFAperture = v.DoFAperture;
            DoFFocalLength = v.DoFFocalLength;
            DoFMaxBlurRadius = v.DoFMaxBlurRadius;
            DoFNearBlurScale = v.DoFNearBlurScale;
            DoFFarBlurScale = v.DoFFarBlurScale;
        }

        public void ScaleEffectStrengths(float t)
        {
            BloomIntensity *= t;
            SSAOIntensity *= t;
            FogDensity *= t;
            Brightness *= t;
            Contrast = Lerp(1f, Contrast, t);
            Saturation = Lerp(1f, Saturation, t);
            Exposure = Lerp(1f, Exposure, t);
            VignetteIntensity *= t;
            VolumetricFogDensity *= t;
            DoFMaxBlurRadius *= t;
        }

        public void BlendWith(PostProcessVolume v, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            if (t <= 0f) return;

            BloomEnabled = BloomEnabled || v.BloomEnabled;
            BloomThreshold = Lerp(BloomThreshold, v.BloomThreshold, t);
            BloomIntensity = Lerp(BloomIntensity, v.BloomEnabled ? v.BloomIntensity : 0f, t);
            BloomIterations = t >= 0.5f ? v.BloomIterations : BloomIterations;

            SSAOEnabled = SSAOEnabled || v.SSAOEnabled;
            SSAORadius = Lerp(SSAORadius, v.SSAORadius, t);
            SSAOIntensity = Lerp(SSAOIntensity, v.SSAOEnabled ? v.SSAOIntensity : 0f, t);
            SSAOBias = Lerp(SSAOBias, v.SSAOBias, t);
            SSAOSamples = (int)MathF.Round(Lerp(SSAOSamples, v.SSAOSamples, t));
            SSAODepthSigma = Lerp(SSAODepthSigma, v.SSAODepthSigma, t);

            SSREnabled = t >= 0.5f ? v.SSREnabled : SSREnabled;
            SSRMaxRaySteps = (int)MathF.Round(Lerp(SSRMaxRaySteps, v.SSRMaxRaySteps, t));
            SSRRoughnessCutoff = Lerp(SSRRoughnessCutoff, v.SSRRoughnessCutoff, t);
            SSRMaxRayDistance = Lerp(SSRMaxRayDistance, v.SSRMaxRayDistance, t);

            TAAEnabled = t >= 0.5f ? v.TAAEnabled : TAAEnabled;
            TAAFrameBlend = Lerp(TAAFrameBlend, v.TAAFrameBlend, t);
            TAASharpen = Lerp(TAASharpen, v.TAASharpen, t);

            FogEnabled = FogEnabled || v.FogEnabled;
            FogColor = SN.Vector3.Lerp(FogColor, v.FogColor, t);
            FogDensity = Lerp(FogDensity, v.FogEnabled ? v.FogDensity : 0f, t);
            FogStart = Lerp(FogStart, v.FogStart, t);
            FogEnd = Lerp(FogEnd, v.FogEnd, t);
            FogHeightBased = t >= 0.5f ? v.FogHeightBased : FogHeightBased;
            FogHeightFalloff = Lerp(FogHeightFalloff, v.FogHeightFalloff, t);

            ColorGradingEnabled = ColorGradingEnabled || v.ColorGradingEnabled;
            Brightness = Lerp(Brightness, v.ColorGradingEnabled ? v.Brightness : 0f, t);
            Contrast = Lerp(Contrast, v.ColorGradingEnabled ? v.Contrast : 1f, t);
            Saturation = Lerp(Saturation, v.ColorGradingEnabled ? v.Saturation : 1f, t);
            Exposure = Lerp(Exposure, v.ColorGradingEnabled ? v.Exposure : 1f, t);
            if (t >= 0.5f && v.ColorGradingEnabled) ToneMap = v.ToneMap;

            VignetteEnabled = VignetteEnabled || v.VignetteEnabled;
            VignetteIntensity = Lerp(VignetteIntensity, v.VignetteEnabled ? v.VignetteIntensity : 0f, t);
            VignetteSmoothness = Lerp(VignetteSmoothness, v.VignetteSmoothness, t);

            FXAAEnabled = t >= 0.5f ? v.FXAAEnabled : FXAAEnabled;
            FXAAThreshold = Lerp(FXAAThreshold, v.FXAAThreshold, t);
            FXAAThresholdMin = Lerp(FXAAThresholdMin, v.FXAAThresholdMin, t);

            VolumetricFogEnabled = VolumetricFogEnabled || v.VolumetricFogEnabled;
            VolumetricFogDensity = Lerp(VolumetricFogDensity, v.VolumetricFogEnabled ? v.VolumetricFogDensity : 0f, t);
            VolumetricFogAnisotropy = Lerp(VolumetricFogAnisotropy, v.VolumetricFogAnisotropy, t);
            VolumetricFogScattering = Lerp(VolumetricFogScattering, v.VolumetricFogScattering, t);
            VolumetricFogHeightFalloff = Lerp(VolumetricFogHeightFalloff, v.VolumetricFogHeightFalloff, t);
            VolumetricFogBaseHeight = Lerp(VolumetricFogBaseHeight, v.VolumetricFogBaseHeight, t);
            VolumetricFogNoiseScale = Lerp(VolumetricFogNoiseScale, v.VolumetricFogNoiseScale, t);
            VolumetricFogNoiseSpeed = Lerp(VolumetricFogNoiseSpeed, v.VolumetricFogNoiseSpeed, t);
            VolumetricFogMaxDistance = Lerp(VolumetricFogMaxDistance, v.VolumetricFogMaxDistance, t);
            VolumetricFogColor = SN.Vector3.Lerp(VolumetricFogColor, v.VolumetricFogColor, t);
            VolumetricFogSteps = (int)MathF.Round(Lerp(VolumetricFogSteps, v.VolumetricFogSteps, t));

            DepthOfFieldEnabled = DepthOfFieldEnabled || v.DepthOfFieldEnabled;
            DoFFocusDistance = Lerp(DoFFocusDistance, v.DoFFocusDistance, t);
            DoFAperture = Lerp(DoFAperture, v.DoFAperture, t);
            DoFFocalLength = Lerp(DoFFocalLength, v.DoFFocalLength, t);
            DoFMaxBlurRadius = Lerp(DoFMaxBlurRadius, v.DepthOfFieldEnabled ? v.DoFMaxBlurRadius : 0f, t);
            DoFNearBlurScale = Lerp(DoFNearBlurScale, v.DoFNearBlurScale, t);
            DoFFarBlurScale = Lerp(DoFFarBlurScale, v.DoFFarBlurScale, t);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
