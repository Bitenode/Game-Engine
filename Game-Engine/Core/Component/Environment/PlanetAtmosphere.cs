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
    bool _dayNightInitialized;
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
    [Persist] public bool EnableDayNightCycle { get; set; } = true;
    [Persist] public bool AutoAdvanceTime { get; set; } = true;
    [Persist] public float DayLengthMinutes { get; set; } = 10f;
    [Persist] public float TimeOfDay { get; set; } = 0.25f; // 0..1, wraps
    [Persist] public float AxisX { get; set; } = 0f;
    [Persist] public float AxisY { get; set; } = 1f;
    [Persist] public float AxisZ { get; set; } = 0f;
    [Persist] public float NoonDirectionX { get; set; } = 0.20f;
    [Persist] public float NoonDirectionY { get; set; } = 0.82f;
    [Persist] public float NoonDirectionZ { get; set; } = 0.53f;
    [Persist] public bool AutoAdjustSunIntensity { get; set; } = true;
    [Persist] public float DaySunIntensity { get; set; } = 1.0f;
    [Persist] public float NightSunIntensity { get; set; } = 0.28f;
    [Persist] public bool AutoAdjustAmbient { get; set; } = true;
    [Persist] public float DayAmbient { get; set; } = 0.5f;
    [Persist] public float NightAmbient { get; set; } = 0.12f;
    [Persist] public bool AutoAdjustSkyTint { get; set; } = true;
    [Persist] public float NightSkyHueShiftDegrees { get; set; } = -12f;
    [Persist] public float NightSkyBrightness { get; set; } = 0.42f;

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
    [Persist] public float NightZenithTintR { get; set; } = 0.05f;
    [Persist] public float NightZenithTintG { get; set; } = 0.10f;
    [Persist] public float NightZenithTintB { get; set; } = 0.25f;
    [Persist] public float NightHorizonTintR { get; set; } = 0.03f;
    [Persist] public float NightHorizonTintG { get; set; } = 0.06f;
    [Persist] public float NightHorizonTintB { get; set; } = 0.16f;

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

    public SN.Vector3 ZenithTint => ComputeSkyTint(DayZenithTint, NightZenithTint);

    public SN.Vector3 HorizonTint => ComputeSkyTint(DayHorizonTint, NightHorizonTint);

    SN.Vector3 DayZenithTint => new(
        Math.Clamp(ZenithTintR, 0f, 2f),
        Math.Clamp(ZenithTintG, 0f, 2f),
        Math.Clamp(ZenithTintB, 0f, 2f));

    SN.Vector3 DayHorizonTint => new(
        Math.Clamp(HorizonTintR, 0f, 2f),
        Math.Clamp(HorizonTintG, 0f, 2f),
        Math.Clamp(HorizonTintB, 0f, 2f));

    SN.Vector3 NightZenithTint => new(
        Math.Clamp(NightZenithTintR, 0f, 2f),
        Math.Clamp(NightZenithTintG, 0f, 2f),
        Math.Clamp(NightZenithTintB, 0f, 2f));

    SN.Vector3 NightHorizonTint => new(
        Math.Clamp(NightHorizonTintR, 0f, 2f),
        Math.Clamp(NightHorizonTintG, 0f, 2f),
        Math.Clamp(NightHorizonTintB, 0f, 2f));

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

    public override void Update()
    {
        if (!EnableDayNightCycle) return;

        if (!_dayNightInitialized)
        {
            // Initialize day values from current atmosphere settings once.
            DaySunIntensity = Math.Max(0.01f, SunIntensity);
            DayAmbient = Math.Max(0f, Ambient);
            _dayNightInitialized = true;
        }

        if (AutoAdvanceTime)
        {
            float seconds = Math.Max(10f, DayLengthMinutes * 60f);
            TimeOfDay = Wrap01(TimeOfDay + (float)Time.deltaTime / seconds);
        }

        ApplyDayNightState();
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
        if (EnableDayNightCycle)
            ApplyDayNightState();
    }

    public override void PostDeserialize()
    {
        if (Preset != PlanetAtmospherePreset.Custom)
            ApplyPreset(Preset);
        if (EnableDayNightCycle)
            ApplyDayNightState();
    }

    void ApplyDayNightState()
    {
        var axis = SafeNormalize(new SN.Vector3(AxisX, AxisY, AxisZ), SN.Vector3.UnitY);
        var noon = SafeNormalize(new SN.Vector3(NoonDirectionX, NoonDirectionY, NoonDirectionZ), new SN.Vector3(0.20f, 0.82f, 0.53f));
        float theta = Wrap01(TimeOfDay) * MathF.Tau;
        var rot = SN.Matrix4x4.CreateFromAxisAngle(axis, theta);
        var sunDir = SafeNormalize(SN.Vector3.TransformNormal(noon, rot), noon);
        SunDirectionX = sunDir.X;
        SunDirectionY = sunDir.Y;
        SunDirectionZ = sunDir.Z;

        // 1 at noon, 0 at midnight. Soften transitions around dawn/dusk.
        float dayLerp = GetDayLerpFromTheta(theta);
        if (AutoAdjustSunIntensity)
            SunIntensity = Lerp(NightSunIntensity, DaySunIntensity, dayLerp);
        if (AutoAdjustAmbient)
            Ambient = Lerp(NightAmbient, DayAmbient, dayLerp);
    }

    SN.Vector3 ComputeSkyTint(SN.Vector3 dayTint, SN.Vector3 nightTint)
    {
        if (!EnableDayNightCycle || !AutoAdjustSkyTint)
            return dayTint;

        float theta = Wrap01(TimeOfDay) * MathF.Tau;
        float dayLerp = GetDayLerpFromTheta(theta);
        float nightLerp = 1f - dayLerp;
        var mixed = SN.Vector3.Lerp(dayTint, nightTint, nightLerp);

        float hueShift01 = (NightSkyHueShiftDegrees / 360f) * nightLerp;
        float brightness = Lerp(1f, Math.Clamp(NightSkyBrightness, 0.05f, 1f), nightLerp);
        return ShiftHueAndBrightness(mixed, hueShift01, brightness);
    }

    static float GetDayLerpFromTheta(float theta)
        => MathF.Pow(Math.Clamp(0.5f + 0.5f * MathF.Cos(theta), 0f, 1f), 0.75f);

    static SN.Vector3 ShiftHueAndBrightness(SN.Vector3 rgb, float hueShift01, float brightnessMul)
    {
        var hsv = RgbToHsv(rgb);
        hsv.X = Wrap01(hsv.X + hueShift01);
        hsv.Z = Math.Clamp(hsv.Z * brightnessMul, 0f, 2f);
        return HsvToRgb(hsv);
    }

    static SN.Vector3 RgbToHsv(SN.Vector3 rgb)
    {
        float r = Math.Clamp(rgb.X, 0f, 2f);
        float g = Math.Clamp(rgb.Y, 0f, 2f);
        float b = Math.Clamp(rgb.Z, 0f, 2f);
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        float h = 0f;
        if (delta > 1e-6f)
        {
            if (max == r)
                h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
            else if (max == g)
                h = ((b - r) / delta + 2f) / 6f;
            else
                h = ((r - g) / delta + 4f) / 6f;
        }

        float s = max <= 1e-6f ? 0f : delta / max;
        float v = max;
        return new SN.Vector3(h, s, v);
    }

    static SN.Vector3 HsvToRgb(SN.Vector3 hsv)
    {
        float h = Wrap01(hsv.X) * 6f;
        float s = Math.Clamp(hsv.Y, 0f, 1f);
        float v = Math.Clamp(hsv.Z, 0f, 2f);
        int i = (int)MathF.Floor(h);
        float f = h - i;
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);

        return (i % 6) switch
        {
            0 => new SN.Vector3(v, t, p),
            1 => new SN.Vector3(q, v, p),
            2 => new SN.Vector3(p, v, t),
            3 => new SN.Vector3(p, q, v),
            4 => new SN.Vector3(t, p, v),
            _ => new SN.Vector3(v, p, q),
        };
    }

    static float Wrap01(float t)
    {
        t %= 1f;
        if (t < 0f) t += 1f;
        return t;
    }

    static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
    {
        float lsq = v.LengthSquared();
        if (lsq < 1e-8f) return fallback;
        return v / MathF.Sqrt(lsq);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
