#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

public enum PlanetWeatherState
{
    Clear,
    Cloudy,
    Rain,
    Snow,
    Storm
}

[ComponentCategory("Environment")]
[Require(typeof(PlanetTerrain))]
public sealed class PlanetWeatherController : Behavior
{
    [Persist] public bool EnableWeather { get; set; } = false;
    [Persist] public float UpdateIntervalSeconds { get; set; } = 0.3f;
    [Persist] public float StateBlendSpeed { get; set; } = 0.65f;
    [Persist] public float PrecipitationHeight { get; set; } = 20f;
    [Persist] public float PrecipitationArea { get; set; } = 36f;
    [Persist] public int PrecipitationLayers { get; set; } = 3;
    [Persist] public float PrecipitationLayerSpacing { get; set; } = 22f;
    [Persist] public bool EnablePrecipitationVisibilityPolling { get; set; } = true;
    [Persist] public float PrecipitationVisibilityPollIntervalSeconds { get; set; } = 0.2f;
    [Persist] public float PrecipitationVisibilityMaxDistance { get; set; } = 220f;
    [Persist] public float PrecipitationVisibilityFovPaddingDegrees { get; set; } = 20f;
    [Persist] public float PrecipitationHiddenClearDelaySeconds { get; set; } = 0.8f;
    [Persist] public bool UsePrecipitationPerformanceBudget { get; set; } = true;
    [Persist] public int MaxActivePrecipitationLayers { get; set; } = 1;
    [Persist] public int RainMaxParticlesPerLayer { get; set; } = 1800;
    [Persist] public int SnowMaxParticlesPerLayer { get; set; } = 900;
    [Persist] public float RainEmissionRatePerLayer { get; set; } = 220f;
    [Persist] public float SnowEmissionRatePerLayer { get; set; } = 85f;
    [Persist] public float RainLifetimeSeconds { get; set; } = 4.5f;
    [Persist] public float SnowLifetimeSeconds { get; set; } = 10f;
    [Persist] public bool DisableSurfaceHitForWeatherPrecipitation { get; set; } = true;
    [Persist] public bool OverrideEmitterParams { get; set; } = false;
    [Persist] public float MinStateHoldSeconds { get; set; } = 18f;
    [Persist] public float RainStateHoldSeconds { get; set; } = 28f;
    [Persist] public float SnowStateHoldSeconds { get; set; } = 30f;
    [Persist] public float StormWindBoost { get; set; } = 1.65f;
    [Persist] public bool DriveAtmosphere { get; set; } = true;
    [Persist] public bool DriveAtmosphereLighting { get; set; } = false;
    [Persist] public bool DrivePostProcessFog { get; set; } = false;
    [Persist] public bool DriveWind { get; set; } = true;
    [Persist] public float AtmosphereCloudInfluence { get; set; } = 0.85f;
    [Persist] public float AtmosphereLightingInfluence { get; set; } = 0.65f;
    [Persist] public bool EnableDiagnostics { get; set; } = false;
    [Persist] public float BoundaryTransitionWarnDelta { get; set; } = 0.6f;

    public PlanetWeatherState CurrentState { get; private set; } = PlanetWeatherState.Clear;
    public float StateBlend { get; private set; }
    public float RainIntensity { get; private set; }
    public float SnowIntensity { get; private set; }
    public float Cloudiness { get; private set; }
    public float Wetness { get; private set; }
    public float SnowCoverage { get; private set; }
    public string DominantBiomeName { get; private set; } = "Unknown";
    public float DominantBiomeWeight { get; private set; }

    PlanetTerrain? _terrain;
    PlanetAtmosphere? _atmo;
    PostProcessVolume? _post;
    PlanetVegetationSystem? _vegetation;
    readonly List<ParticleEmitter> _precipEmitters = new();
    readonly List<GameObject> _precipObjects = new();
    Camera? _cachedCamera;
    float _updateAccum;
    float _precipVisibilityAccum;
    float _precipHiddenAccum;
    float _seasonT;
    float _stateHoldRemaining;
    bool _precipitationVisible = true;
    string _lastDominantBiomeName = "";
    float _lastDominantBiomeWeight;
    bool _effectsApplied;
    bool _capturedAtmosphere;
    bool _capturedPost;
    bool _capturedWind;
    float _baseAtmoAmbient;
    float _baseAtmoSunIntensity;
    bool _baseAtmoEnableClouds;
    float _baseAtmoCloudCoverage;
    float _baseAtmoCloudDensity;
    float _baseAtmoCloudSpeed;
    bool _basePostFogEnabled;
    float _basePostFogDensity;
    float _basePostFogStart;
    float _basePostFogEnd;
    bool _basePostVolFogEnabled;
    float _basePostVolFogDensity;
    float _basePostVolFogMaxDistance;
    float _baseWindAmplitude;
    float _baseWindGustiness;
    float _baseWindTurbulenceFrequency;

    public override void Awake()
    {
        _terrain = GetComponent<PlanetTerrain>();
        _atmo = gameObject?.Behaviors.OfType<PlanetAtmosphere>().FirstOrDefault();
        _vegetation = gameObject?.Behaviors.OfType<PlanetVegetationSystem>().FirstOrDefault();
    }

    public override void OnEnable()
    {
        _effectsApplied = false;
        _updateAccum = 0f;
        _precipVisibilityAccum = 0f;
        _precipHiddenAccum = 0f;
        _precipitationVisible = true;
        CaptureBaselines();
    }

    public override void OnDisable()
    {
        RestoreBaselines();
    }

    public override void Update()
    {
        float frameDt = Math.Max(0f, (float)Time.deltaTime);
        if (!EnableWeather || _terrain == null || _terrain.Config == null || !_terrain.IsActiveAndEnabled)
        {
            if (_effectsApplied)
                RestoreBaselines();
            SetPrecipEmittersActive(false, clearParticles: true);
            return;
        }

        if (!_effectsApplied)
            CaptureBaselines();

        PollPrecipitationVisibility(frameDt);

        _updateAccum += frameDt;
        if (_updateAccum < Math.Max(0.05f, UpdateIntervalSeconds))
            return;

        _updateAccum = 0f;
        StepWeather();
    }

    public override void OnDestroy()
    {
        RestoreBaselines();
        for (int i = 0; i < _precipObjects.Count; i++)
        {
            _precipObjects[i].RemoveFromParent();
        }
        _precipObjects.Clear();
        _precipEmitters.Clear();
    }

    void StepWeather()
    {
        var cfg = _terrain!.Config!;
        var camPos = ResolveCameraPosition();
        if (!_terrain.TryGetBiomeBlendsAtWorldPos(camPos, out var blends) || blends.Length == 0)
            return;

        float avgRain = 0f, avgSnow = 0f, avgStorm = 0f, avgWindBias = 1f, avgCloudBias = 1f, avgFogBias = 1f;
        float avgTempCenter = 0.5f, growthMul = 1f;
        for (int i = 0; i < blends.Length; i++)
        {
            var b = blends[i].Biome;
            float w = blends[i].Weight;
            avgRain += b.RainChance * w;
            avgSnow += b.SnowChance * w;
            avgStorm += b.StormChance * w;
            avgWindBias += (b.WindBias - 1f) * w;
            avgCloudBias += (b.CloudCoverageBias - 1f) * w;
            avgFogBias += (b.FogDensityBias - 1f) * w;
            avgTempCenter += (((b.MinTemperature + b.MaxTemperature) * 0.5f) - 0.5f) * w;
            growthMul += (b.SeasonalGrowthMultiplier - 1f) * w;
        }
        DominantBiomeName = blends[0].Biome.Name;
        DominantBiomeWeight = blends[0].Weight;
        ValidateBoundaryTransition();

        _seasonT += (float)Time.deltaTime / Math.Max(60f, cfg.SeasonLengthMinutes * 60f);
        if (_seasonT > 1f) _seasonT -= 1f;
        float seasonal = 0.5f + 0.5f * MathF.Sin(_seasonT * MathF.Tau);

        float climateWet = Math.Clamp((avgRain + avgSnow * 0.5f + avgStorm) * cfg.GlobalWeatherIntensity, 0f, 1f);
        float coldness = Math.Clamp(1f - avgTempCenter, 0f, 1f);
        float snowPreference = Math.Clamp(avgSnow + coldness * 0.25f, 0f, 1f);
        float stormPreference = Math.Clamp(avgStorm * (0.6f + climateWet), 0f, 1f);

        float choice = Noise01(cfg.WeatherSeed, Time.time * 0.12f + avgTempCenter * 2.2f + seasonal * 1.1f);
        PlanetWeatherState target = PlanetWeatherState.Clear;
        if (choice < stormPreference)
            target = PlanetWeatherState.Storm;
        else if (choice < stormPreference + snowPreference * climateWet)
            target = PlanetWeatherState.Snow;
        else if (choice < stormPreference + snowPreference * climateWet + avgRain * climateWet)
            target = PlanetWeatherState.Rain;
        else if (choice < 0.55f + avgCloudBias * 0.15f)
            target = PlanetWeatherState.Cloudy;

        float dt = Math.Max(UpdateIntervalSeconds, 0.05f);
        _stateHoldRemaining = Math.Max(0f, _stateHoldRemaining - dt);
        if (_stateHoldRemaining <= 0f && target != CurrentState)
        {
            CurrentState = target;
            _stateHoldRemaining = GetStateHoldDuration(target);
        }

        float blendRate = Math.Max(0.05f, StateBlendSpeed);
        StateBlend = Math.Clamp(StateBlend + blendRate * dt, 0f, 1f);

        float rainTarget = target is PlanetWeatherState.Rain or PlanetWeatherState.Storm ? 1f : 0f;
        float snowTarget = target == PlanetWeatherState.Snow ? 1f : 0f;
        RainIntensity = Damp(RainIntensity, rainTarget, 5.5f, dt);
        SnowIntensity = Damp(SnowIntensity, snowTarget, 5.5f, dt);
        Cloudiness = Damp(Cloudiness, target == PlanetWeatherState.Clear ? 0.25f : (target == PlanetWeatherState.Cloudy ? 0.55f : 0.88f), 2.6f, dt);
        Wetness = Damp(Wetness, Math.Clamp(RainIntensity * 0.85f + (target == PlanetWeatherState.Storm ? 0.35f : 0f), 0f, 1f), 1.8f, dt);
        SnowCoverage = Damp(SnowCoverage, Math.Clamp(SnowIntensity * (0.5f + coldness * 0.5f), 0f, 1f), 0.7f * Math.Max(0.35f, growthMul), dt);

        if (DriveAtmosphere) ApplyAtmosphere(avgCloudBias);
        if (DriveWind) ApplyWind(cfg, avgWindBias);
        ApplyPostFog(avgFogBias);
        ApplyPrecipitation(camPos);
        ApplyVegetationCoupling(growthMul, avgWindBias);
    }

    void ApplyAtmosphere(float cloudBias)
    {
        _atmo ??= gameObject?.Behaviors.OfType<PlanetAtmosphere>().FirstOrDefault();
        if (_atmo == null) return;

        float cloudInf = Math.Clamp(AtmosphereCloudInfluence, 0f, 1.5f);
        float lightInf = Math.Clamp(AtmosphereLightingInfluence, 0f, 1.5f);
        float weatherCloud = Math.Clamp(Cloudiness * cloudInf * Math.Max(0.4f, cloudBias), 0f, 1f);
        float precip = Math.Clamp(RainIntensity * 0.8f + SnowIntensity * 0.6f, 0f, 1f);

        _atmo.EnableClouds = _baseAtmoEnableClouds || weatherCloud > 0.05f;
        float covTarget = Math.Clamp(_baseAtmoCloudCoverage + weatherCloud * 0.55f, 0f, 0.98f);
        float denTarget = Math.Clamp(_baseAtmoCloudDensity + weatherCloud * 0.42f, 0.05f, 1.75f);
        _atmo.CloudCoverage = Damp(_atmo.CloudCoverage, covTarget, 2.2f, Math.Max(UpdateIntervalSeconds, 0.05f));
        _atmo.CloudDensity = Damp(_atmo.CloudDensity, denTarget, 2.0f, Math.Max(UpdateIntervalSeconds, 0.05f));
        _atmo.CloudSpeed = Math.Clamp(_baseAtmoCloudSpeed + precip * 0.035f, 0.005f, 0.16f);

        if (DriveAtmosphereLighting)
        {
            // Keep cloud cover from crushing scene brightness: mild sun attenuation + slight sky fill boost.
            float sunMul = 1f - (weatherCloud * 0.35f + precip * 0.12f) * lightInf;
            float sunTarget = Math.Clamp(_baseAtmoSunIntensity * sunMul, _baseAtmoSunIntensity * 0.45f, _baseAtmoSunIntensity * 1.05f);
            float ambientTarget = Math.Clamp(_baseAtmoAmbient + weatherCloud * 0.03f * lightInf, 0.08f, 0.40f);
            _atmo.SunIntensity = Damp(_atmo.SunIntensity, sunTarget, 2.0f, Math.Max(UpdateIntervalSeconds, 0.05f));
            _atmo.Ambient = Damp(_atmo.Ambient, ambientTarget, 2.2f, Math.Max(UpdateIntervalSeconds, 0.05f));
        }
        else
        {
            _atmo.SunIntensity = _baseAtmoSunIntensity;
            _atmo.Ambient = _baseAtmoAmbient;
        }
        _effectsApplied = true;
    }

    void ApplyWind(PlanetConfig cfg, float biomeWindBias)
    {
        float targetMul = cfg.GlobalWindMultiplier * biomeWindBias * (CurrentState == PlanetWeatherState.Storm ? StormWindBoost : 1f);
        WindSystem.Amplitude = Damp(WindSystem.Amplitude, 0.08f * targetMul, 1.7f, UpdateIntervalSeconds);
        WindSystem.Gustiness = Damp(WindSystem.Gustiness, 0.35f + targetMul * 0.15f, 1.4f, UpdateIntervalSeconds);
        WindSystem.TurbulenceFrequency = Damp(WindSystem.TurbulenceFrequency, 0.85f + targetMul * 0.4f, 1.2f, UpdateIntervalSeconds);
        _effectsApplied = true;
    }

    void ApplyPostFog(float fogBias)
    {
        if (!DrivePostProcessFog) return;
        _post ??= PostProcessVolume.GetActive();
        if (_post == null) return;

        _post.FogEnabled = true;
        _post.FogDensity = Math.Clamp(0.004f + Cloudiness * 0.012f * fogBias, 0.0005f, 0.04f);
        _post.FogStart = 8f;
        _post.FogEnd = 320f - Cloudiness * 90f;
        _post.VolumetricFogEnabled = Cloudiness > 0.45f;
        _post.VolumetricFogDensity = Math.Clamp(_post.FogDensity * 0.45f, 0.0005f, 0.02f);
        _post.VolumetricFogMaxDistance = Math.Clamp(360f - Cloudiness * 100f, 120f, 360f);
        _effectsApplied = true;
    }

    void ApplyPrecipitation(SN.Vector3 cameraPos)
    {
        bool emitRain = CurrentState is PlanetWeatherState.Rain or PlanetWeatherState.Storm;
        bool emitSnow = CurrentState == PlanetWeatherState.Snow;
        float effectiveRain = emitRain ? Math.Max(RainIntensity, 0.45f) : RainIntensity;
        float effectiveSnow = emitSnow ? Math.Max(SnowIntensity, 0.35f) : SnowIntensity;
        bool wantsPrecipitation = effectiveRain > 0.02f || effectiveSnow > 0.02f;

        if (!wantsPrecipitation)
        {
            _precipHiddenAccum += Math.Max(0.05f, UpdateIntervalSeconds);
            SetPrecipEmittersActive(false, clearParticles: _precipHiddenAccum >= Math.Max(0.1f, PrecipitationHiddenClearDelaySeconds));
            return;
        }

        if (EnablePrecipitationVisibilityPolling && !_precipitationVisible)
        {
            _precipHiddenAccum += Math.Max(0.05f, UpdateIntervalSeconds);
            SetPrecipEmittersActive(false, clearParticles: _precipHiddenAccum >= Math.Max(0.1f, PrecipitationHiddenClearDelaySeconds));
            return;
        }

        _precipHiddenAccum = 0f;
        EnsurePrecipEmitter();
        if (_precipObjects.Count == 0 || _precipEmitters.Count == 0) return;
        SetPrecipEmittersActive(true, clearParticles: false);

        int layerCount = Math.Min(_precipObjects.Count, _precipEmitters.Count);
        int activeLayerCap = Math.Max(1, MaxActivePrecipitationLayers);
        for (int i = 0; i < layerCount; i++)
        {
            var go = _precipObjects[i];
            var emitter = _precipEmitters[i];
            float layerH = PrecipitationHeight + i * Math.Max(4f, PrecipitationLayerSpacing);
            go.Transform.Position = new Vector3(cameraPos.X, cameraPos.Y + layerH, cameraPos.Z);

            float layerFactor = 1f - (i / Math.Max(1f, layerCount - 1f)) * 0.28f;
            bool layerActive = i < activeLayerCap;
            if (!layerActive)
            {
                emitter.Stop();
                if (emitter.Enabled)
                    emitter.SetEnabledSilent(false);
                continue;
            }

            if (!emitter.Enabled)
                emitter.SetEnabledSilent(true);
            if (emitSnow)
            {
                if (emitter.Preset != ParticlePreset.Snow)
                    emitter.ApplyPreset(ParticlePreset.Snow);
                if (UsePrecipitationPerformanceBudget)
                {
                    emitter.MaxParticles = Math.Max(250, (int)(RainSafeInt(SnowMaxParticlesPerLayer) * layerFactor));
                    emitter.EmissionRate = Math.Max(5f, SnowEmissionRatePerLayer * (0.35f + effectiveSnow * 0.9f) * layerFactor);
                    emitter.Lifetime = Math.Max(1.2f, SnowLifetimeSeconds);
                    emitter.BoxSize = new SN.Vector3(PrecipitationArea * 0.95f, 0f, PrecipitationArea * 0.95f);
                    if (DisableSurfaceHitForWeatherPrecipitation)
                        emitter.StopOnPlanetSurfaceHit = false;
                }
                else if (OverrideEmitterParams)
                {
                    emitter.MaxParticles = Math.Max(1000, (int)(1400 * layerFactor));
                    emitter.EmissionRate = (35f + effectiveSnow * 140f) * layerFactor;
                    emitter.BoxSize = new SN.Vector3(PrecipitationArea * 1.1f, 0f, PrecipitationArea * 1.1f);
                }
                emitter.Loop = true;
                emitter.Play();
            }
            else if (emitRain)
            {
                if (emitter.Preset != ParticlePreset.Rain)
                    emitter.ApplyPreset(ParticlePreset.Rain);
                if (UsePrecipitationPerformanceBudget)
                {
                    emitter.MaxParticles = Math.Max(350, (int)(RainSafeInt(RainMaxParticlesPerLayer) * layerFactor));
                    emitter.EmissionRate = Math.Max(10f, RainEmissionRatePerLayer * (0.35f + effectiveRain * 0.9f) * layerFactor);
                    emitter.Lifetime = Math.Max(0.8f, RainLifetimeSeconds);
                    emitter.BoxSize = new SN.Vector3(PrecipitationArea, Math.Max(24f, PrecipitationHeight * 0.7f), PrecipitationArea);
                    if (DisableSurfaceHitForWeatherPrecipitation)
                        emitter.StopOnPlanetSurfaceHit = false;
                }
                else if (OverrideEmitterParams)
                {
                    emitter.MaxParticles = Math.Max(2000, (int)(2800 * layerFactor));
                    emitter.EmissionRate = (90f + effectiveRain * 340f) * layerFactor;
                    emitter.BoxSize = new SN.Vector3(PrecipitationArea * 1.2f, 0f, PrecipitationArea * 1.2f);
                }
                emitter.Loop = true;
                emitter.Play();
            }
            else
            {
                // Let already-spawned particles continue to simulate to planet hit.
                emitter.Stop();
            }
        }
    }

    static int RainSafeInt(int value) => Math.Max(1, value);

    void PollPrecipitationVisibility(float frameDt)
    {
        if (!EnablePrecipitationVisibilityPolling)
        {
            _precipitationVisible = true;
            return;
        }

        _precipVisibilityAccum += frameDt;
        float pollInterval = Math.Max(0.05f, PrecipitationVisibilityPollIntervalSeconds);
        if (_precipVisibilityAccum < pollInterval)
            return;
        _precipVisibilityAccum = 0f;

        var camera = ResolveActiveCamera();
        _precipitationVisible = IsPrecipitationVolumeVisible(camera);
    }

    bool IsPrecipitationVolumeVisible(Camera? camera)
    {
        if (camera == null || !camera.IsActiveAndEnabled || camera.gameObject == null)
            return true;

        var camPos = new SN.Vector3((float)camera.Transform.Position.X, (float)camera.Transform.Position.Y, (float)camera.Transform.Position.Z);
        var up = camera.WorldUp;
        if (up.LengthSquared() <= 1e-8f) up = SN.Vector3.UnitY;
        else up = SN.Vector3.Normalize(up);

        int layerCount = Math.Max(1, PrecipitationLayers);
        float layerStride = Math.Max(4f, PrecipitationLayerSpacing);
        float heightSpan = Math.Max(0f, layerStride * Math.Max(0, layerCount - 1));
        float radius = Math.Max(6f, PrecipitationArea * 0.75f);
        radius = MathF.Sqrt(radius * radius + heightSpan * heightSpan * 0.25f);

        var center = camPos + up * (Math.Max(0f, PrecipitationHeight) + heightSpan * 0.5f);
        var toVolume = center - camPos;
        float distSq = toVolume.LengthSquared();
        float maxDist = Math.Max(24f, PrecipitationVisibilityMaxDistance) + radius;
        if (distSq > maxDist * maxDist)
            return false;

        float dist = MathF.Sqrt(Math.Max(1e-8f, distSq));
        float near = Math.Max(0.01f, camera.Near);
        float far = Math.Max(near + 0.1f, camera.Far);
        if (dist + radius < near || dist - radius > far)
            return false;

        var forward = TransformUtil.ForwardFrom(camera.Transform);
        if (forward.LengthSquared() <= 1e-8f)
            return true;
        var toNorm = toVolume / dist;
        float dot = SN.Vector3.Dot(forward, toNorm);

        if (camera.Projection == Projection.Perspective)
        {
            float halfFov = Math.Clamp(camera.FieldOfView * 0.5f + PrecipitationVisibilityFovPaddingDegrees, 1f, 179f) * MathF.PI / 180f;
            float angularRadius = dist > 1e-3f
                ? MathF.Asin(Math.Clamp(radius / dist, 0f, 1f))
                : MathF.PI * 0.5f;
            float minDot = MathF.Cos(Math.Clamp(halfFov + angularRadius, 0f, MathF.PI));
            return dot >= minDot;
        }

        // Orthographic: if the volume is behind camera, skip precipitation work.
        return dot >= -0.25f;
    }

    void SetPrecipEmittersActive(bool active, bool clearParticles)
    {
        for (int i = 0; i < _precipEmitters.Count; i++)
        {
            var emitter = _precipEmitters[i];
            if (!active)
            {
                emitter.Stop();
                if (clearParticles)
                    emitter.Clear();
                if (emitter.Enabled)
                    emitter.SetEnabledSilent(false);
            }
            else
            {
                if (!emitter.Enabled)
                    emitter.SetEnabledSilent(true);
            }
        }
    }

    float GetStateHoldDuration(PlanetWeatherState state)
    {
        return state switch
        {
            PlanetWeatherState.Rain => Math.Max(MinStateHoldSeconds, RainStateHoldSeconds),
            PlanetWeatherState.Snow => Math.Max(MinStateHoldSeconds, SnowStateHoldSeconds),
            PlanetWeatherState.Storm => Math.Max(MinStateHoldSeconds, RainStateHoldSeconds + 6f),
            _ => Math.Max(4f, MinStateHoldSeconds * 0.5f),
        };
    }

    void ApplyVegetationCoupling(float growthMul, float windBias)
    {
        _vegetation ??= gameObject?.Behaviors.OfType<PlanetVegetationSystem>().FirstOrDefault();
        if (_vegetation != null)
            _vegetation.ApplyWeather(Wetness, SnowCoverage, Math.Max(0.25f, windBias));

        BiomeWeatherRuntime.Wetness = Wetness;
        BiomeWeatherRuntime.SnowCoverage = SnowCoverage;
        BiomeWeatherRuntime.CloudTint = SN.Vector3.Lerp(new SN.Vector3(1f, 1f, 1f), new SN.Vector3(0.88f, 0.92f, 1f), Cloudiness)
            * Math.Clamp(growthMul, 0.65f, 1.4f);
    }

    void EnsurePrecipEmitter()
    {
        int targetLayers = Math.Max(1, PrecipitationLayers);
        if (_precipEmitters.Count == targetLayers && _precipObjects.Count == targetLayers) return;
        if (_terrain?.gameObject == null) return;

        for (int i = 0; i < _precipObjects.Count; i++)
            _precipObjects[i].RemoveFromParent();
        _precipObjects.Clear();
        _precipEmitters.Clear();

        for (int i = 0; i < targetLayers; i++)
        {
            var obj = new GameObject($"BiomeWeatherPrecipitation_{i}");
            var emitter = obj.AddBehavior<ParticleEmitter>();
            emitter.Preset = ParticlePreset.Rain;
            emitter.PlayOnAwake = false;
            emitter.ApplyPreset(ParticlePreset.Rain);
            emitter.UsePlanetGravity = true;
            emitter.AlignEmissionToGravity = true;
            emitter.StopOnPlanetSurfaceHit = true;
            emitter.Stop();
            _terrain.gameObject.AddChild(obj);
            _precipObjects.Add(obj);
            _precipEmitters.Add(emitter);
        }
    }

    SN.Vector3 ResolveCameraPosition()
    {
        if (_terrain != null && _terrain.LastCameraPosition.LengthSquared() > 1e-6f)
            return _terrain.LastCameraPosition;
        var cam = ResolveActiveCamera();
        return cam != null
            ? new SN.Vector3((float)cam.Transform.Position.X, (float)cam.Transform.Position.Y, (float)cam.Transform.Position.Z)
            : SN.Vector3.Zero;
    }

    Camera? ResolveActiveCamera()
    {
        if (_cachedCamera != null && _cachedCamera.IsActiveAndEnabled && _cachedCamera.gameObject != null)
            return _cachedCamera;

        _cachedCamera = CameraService.MainOrFirst();
        if (_cachedCamera != null && _cachedCamera.IsActiveAndEnabled && _cachedCamera.gameObject != null)
            return _cachedCamera;

        _cachedCamera = CameraService.All.FirstOrDefault(c => c.IsActiveAndEnabled && c.gameObject != null);
        return _cachedCamera;
    }

    static float Damp(float current, float target, float speed, float dt)
        => current + (target - current) * (1f - MathF.Exp(-Math.Max(0f, speed) * Math.Max(0f, dt)));

    static float Noise01(int seed, float x)
    {
        int n = (int)(x * 1024f) ^ seed;
        unchecked
        {
            uint u = (uint)n;
            u ^= u << 13;
            u ^= u >> 17;
            u ^= u << 5;
            return (u & 0x00FFFFFF) / 16777215f;
        }
    }

    void ValidateBoundaryTransition()
    {
        if (!EnableDiagnostics) return;
        if (string.IsNullOrEmpty(_lastDominantBiomeName))
        {
            _lastDominantBiomeName = DominantBiomeName;
            _lastDominantBiomeWeight = DominantBiomeWeight;
            return;
        }

        float delta = MathF.Abs(DominantBiomeWeight - _lastDominantBiomeWeight);
        if (DominantBiomeName != _lastDominantBiomeName && delta > Math.Max(0.1f, BoundaryTransitionWarnDelta))
        {
            Log.Warning($"[PlanetWeatherController] sharp biome transition {_lastDominantBiomeName}->{DominantBiomeName} delta={delta:F2}");
        }

        _lastDominantBiomeName = DominantBiomeName;
        _lastDominantBiomeWeight = DominantBiomeWeight;
    }

    void CaptureBaselines()
    {
        _atmo ??= gameObject?.Behaviors.OfType<PlanetAtmosphere>().FirstOrDefault();
        if (_atmo != null && !_capturedAtmosphere)
        {
            _baseAtmoAmbient = _atmo.Ambient;
            _baseAtmoSunIntensity = _atmo.SunIntensity;
            _baseAtmoEnableClouds = _atmo.EnableClouds;
            _baseAtmoCloudCoverage = _atmo.CloudCoverage;
            _baseAtmoCloudDensity = _atmo.CloudDensity;
            _baseAtmoCloudSpeed = _atmo.CloudSpeed;
            _capturedAtmosphere = true;
        }

        _post ??= PostProcessVolume.GetActive();
        if (_post != null && !_capturedPost)
        {
            _basePostFogEnabled = _post.FogEnabled;
            _basePostFogDensity = _post.FogDensity;
            _basePostFogStart = _post.FogStart;
            _basePostFogEnd = _post.FogEnd;
            _basePostVolFogEnabled = _post.VolumetricFogEnabled;
            _basePostVolFogDensity = _post.VolumetricFogDensity;
            _basePostVolFogMaxDistance = _post.VolumetricFogMaxDistance;
            _capturedPost = true;
        }

        if (!_capturedWind)
        {
            _baseWindAmplitude = WindSystem.Amplitude;
            _baseWindGustiness = WindSystem.Gustiness;
            _baseWindTurbulenceFrequency = WindSystem.TurbulenceFrequency;
            _capturedWind = true;
        }
    }

    void RestoreBaselines()
    {
        if (_capturedAtmosphere && _atmo != null)
        {
            _atmo.Ambient = _baseAtmoAmbient;
            _atmo.SunIntensity = _baseAtmoSunIntensity;
            _atmo.EnableClouds = _baseAtmoEnableClouds;
            _atmo.CloudCoverage = _baseAtmoCloudCoverage;
            _atmo.CloudDensity = _baseAtmoCloudDensity;
            _atmo.CloudSpeed = _baseAtmoCloudSpeed;
        }

        if (_capturedPost && _post != null)
        {
            _post.FogEnabled = _basePostFogEnabled;
            _post.FogDensity = _basePostFogDensity;
            _post.FogStart = _basePostFogStart;
            _post.FogEnd = _basePostFogEnd;
            _post.VolumetricFogEnabled = _basePostVolFogEnabled;
            _post.VolumetricFogDensity = _basePostVolFogDensity;
            _post.VolumetricFogMaxDistance = _basePostVolFogMaxDistance;
        }

        if (_capturedWind)
        {
            WindSystem.Amplitude = _baseWindAmplitude;
            WindSystem.Gustiness = _baseWindGustiness;
            WindSystem.TurbulenceFrequency = _baseWindTurbulenceFrequency;
        }

        for (int i = 0; i < _precipEmitters.Count; i++)
        {
            _precipEmitters[i].Stop();
            _precipEmitters[i].Clear();
            if (_precipEmitters[i].Enabled)
                _precipEmitters[i].SetEnabledSilent(false);
        }

        BiomeWeatherRuntime.Wetness = 0f;
        BiomeWeatherRuntime.SnowCoverage = 0f;
        BiomeWeatherRuntime.CloudTint = new SN.Vector3(1f, 1f, 1f);
        _effectsApplied = false;
    }
}
