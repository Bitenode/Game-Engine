using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

public enum PlanetAtmospherePreset
{
    Custom,
    Thin,
    EarthLike,
    Dense,
    AlienViolet
}

[ComponentCategory("Environment")]
public sealed class PlanetAtmosphere : Behavior
{
    PlanetAtmospherePreset _preset = PlanetAtmospherePreset.EarthLike;
    bool _applyingPreset;
    [Persist]
    public PlanetAtmospherePreset Preset
    {
        get => _preset;
        set
        {
            if (_preset == value) return;
            _preset = value;
            if (!_applyingPreset && value != PlanetAtmospherePreset.Custom)
                ApplyPreset(value);
        }
    }

    [Persist] public bool Enabled { get; set; } = true;
    [Persist] public float Ambient { get; set; } = 0.18f;

    [Persist] public bool UseDirectionalLight { get; set; } = true;
    [Persist] public float SunDirectionX { get; set; } = 0.20f;
    [Persist] public float SunDirectionY { get; set; } = 0.82f;
    [Persist] public float SunDirectionZ { get; set; } = 0.53f;
    [Persist] public float SunIntensity { get; set; } = 1.0f;

    [Persist] public float GroundRadiusOverride { get; set; } = 0f;
    [Persist] public float AtmosphereHeight { get; set; } = 220f;
    [Persist] public float AtmosphereBlend { get; set; } = 0.45f;
    [Persist] public float RayleighStrength { get; set; } = 1.0f;
    [Persist] public float MieStrength { get; set; } = 0.30f;
    [Persist] public float DensityFalloff { get; set; } = 1.25f;
    [Persist] public float HorizonBlend { get; set; } = 1.0f;
    [Persist] public float SunsetBoost { get; set; } = 1.0f;
    [Persist] public int SampleCount { get; set; } = 8;

    [Persist] public float ZenithTintR { get; set; } = 0.26f;
    [Persist] public float ZenithTintG { get; set; } = 0.40f;
    [Persist] public float ZenithTintB { get; set; } = 0.92f;
    [Persist] public float HorizonTintR { get; set; } = 0.82f;
    [Persist] public float HorizonTintG { get; set; } = 0.86f;
    [Persist] public float HorizonTintB { get; set; } = 0.98f;

    [Persist] public bool EnableClouds { get; set; } = true;
    [Persist] public float CloudBaseHeight { get; set; } = 120f;
    [Persist] public float CloudTopHeight { get; set; } = 220f;
    [Persist] public float CloudCoverage { get; set; } = 0.46f;
    [Persist] public float CloudDensity { get; set; } = 1.0f;
    [Persist] public float CloudDetail { get; set; } = 2.0f;
    [Persist] public float CloudSpeed { get; set; } = 0.025f;
    [Persist] public float CloudSoftness { get; set; } = 0.30f;
    [Persist] public float CloudLightResponse { get; set; } = 0.9f;
    [Persist] public float CloudSilverLining { get; set; } = 0.65f;
    [Persist] public int CloudStepCount { get; set; } = 16;

    public SN.Vector3 ZenithTint => new(
        Math.Clamp(ZenithTintR, 0f, 2f),
        Math.Clamp(ZenithTintG, 0f, 2f),
        Math.Clamp(ZenithTintB, 0f, 2f));

    public SN.Vector3 HorizonTint => new(
        Math.Clamp(HorizonTintR, 0f, 2f),
        Math.Clamp(HorizonTintG, 0f, 2f),
        Math.Clamp(HorizonTintB, 0f, 2f));

    public SN.Vector3 SunDirectionOverride
    {
        get
        {
            var raw = new SN.Vector3(SunDirectionX, SunDirectionY, SunDirectionZ);
            if (raw.LengthSquared() < 1e-4f)
                return new SN.Vector3(0.20f, 0.82f, 0.53f);
            return SN.Vector3.Normalize(raw);
        }
    }

    public void ApplyPreset(PlanetAtmospherePreset preset)
    {
        _applyingPreset = true;
        switch (preset)
        {
            case PlanetAtmospherePreset.Thin:
                Ambient = 0.08f;
                AtmosphereHeight = 120f;
                AtmosphereBlend = 0.28f;
                RayleighStrength = 0.55f;
                MieStrength = 0.12f;
                DensityFalloff = 1.75f;
                HorizonBlend = 0.65f;
                SunsetBoost = 0.75f;
                ZenithTintR = 0.20f; ZenithTintG = 0.32f; ZenithTintB = 0.82f;
                HorizonTintR = 0.75f; HorizonTintG = 0.80f; HorizonTintB = 0.90f;
                CloudBaseHeight = 180f;
                CloudTopHeight = 300f;
                CloudCoverage = 0.25f;
                CloudDensity = 0.55f;
                CloudDetail = 2.6f;
                CloudSoftness = 0.24f;
                CloudLightResponse = 1.0f;
                CloudSilverLining = 0.72f;
                CloudStepCount = 12;
                break;
            case PlanetAtmospherePreset.EarthLike:
                Ambient = 0.16f;
                AtmosphereHeight = 220f;
                AtmosphereBlend = 0.50f;
                RayleighStrength = 1.00f;
                MieStrength = 0.30f;
                DensityFalloff = 1.25f;
                HorizonBlend = 1.00f;
                SunsetBoost = 1.10f;
                ZenithTintR = 0.26f; ZenithTintG = 0.40f; ZenithTintB = 0.92f;
                HorizonTintR = 0.82f; HorizonTintG = 0.86f; HorizonTintB = 0.98f;
                CloudBaseHeight = 120f;
                CloudTopHeight = 220f;
                CloudCoverage = 0.46f;
                CloudDensity = 0.85f;
                CloudDetail = 2.0f;
                CloudSoftness = 0.30f;
                CloudLightResponse = 0.90f;
                CloudSilverLining = 0.65f;
                CloudStepCount = 16;
                break;
            case PlanetAtmospherePreset.Dense:
                Ambient = 0.24f;
                AtmosphereHeight = 360f;
                AtmosphereBlend = 0.82f;
                RayleighStrength = 1.35f;
                MieStrength = 0.55f;
                DensityFalloff = 0.70f;
                HorizonBlend = 1.30f;
                SunsetBoost = 1.45f;
                ZenithTintR = 0.34f; ZenithTintG = 0.46f; ZenithTintB = 0.95f;
                HorizonTintR = 0.95f; HorizonTintG = 0.86f; HorizonTintB = 0.78f;
                CloudBaseHeight = 90f;
                CloudTopHeight = 260f;
                CloudCoverage = 0.66f;
                CloudDensity = 0.98f;
                CloudDetail = 1.7f;
                CloudSoftness = 0.38f;
                CloudLightResponse = 0.82f;
                CloudSilverLining = 0.52f;
                CloudStepCount = 20;
                break;
            case PlanetAtmospherePreset.AlienViolet:
                Ambient = 0.20f;
                AtmosphereHeight = 300f;
                AtmosphereBlend = 0.70f;
                RayleighStrength = 0.92f;
                MieStrength = 0.48f;
                DensityFalloff = 0.95f;
                HorizonBlend = 1.15f;
                SunsetBoost = 1.60f;
                ZenithTintR = 0.38f; ZenithTintG = 0.20f; ZenithTintB = 0.80f;
                HorizonTintR = 0.92f; HorizonTintG = 0.45f; HorizonTintB = 0.84f;
                CloudBaseHeight = 130f;
                CloudTopHeight = 280f;
                CloudCoverage = 0.52f;
                CloudDensity = 0.92f;
                CloudDetail = 2.4f;
                CloudSoftness = 0.26f;
                CloudLightResponse = 0.95f;
                CloudSilverLining = 0.80f;
                CloudStepCount = 18;
                break;
            case PlanetAtmospherePreset.Custom:
            default:
                break;
        }
        _preset = preset;
        _applyingPreset = false;
    }

    public override void Awake()
    {
        if (Preset != PlanetAtmospherePreset.Custom)
            ApplyPreset(Preset);
    }

    public override void PostDeserialize()
    {
        if (Preset != PlanetAtmospherePreset.Custom)
            ApplyPreset(Preset);
    }
}
