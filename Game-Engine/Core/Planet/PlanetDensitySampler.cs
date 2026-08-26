using System;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Composes procedural planet density (crust + 3D caves) with runtime voxel edits.
/// All positions are planet-local unscaled (see <see cref="PlanetSpace"/>).
/// Density convention: negative = solid, positive = air. Isosurface is density == 0.
/// </summary>
public sealed class PlanetDensitySampler
{
    readonly PlanetConfig _config;
    readonly BiomeMap _biomeMap;
    readonly FractalNoise[] _biomeNoises;
    readonly FractalNoise? _erosionNoise;
    readonly FractalNoise? _ridgeNoise;
    readonly FractalNoise? _basinNoise;
    readonly PlanetNoiseCache? _noise;
    readonly PlanetVoxelEditStore? _editStore;

    public PlanetDensitySampler(
        PlanetConfig config,
        BiomeMap biomeMap,
        PlanetNoiseCache noise,
        PlanetVoxelEditStore? editStore)
        : this(
            config,
            biomeMap,
            noise.BiomeNoises,
            noise.ErosionNoise,
            noise.RidgeNoise,
            noise.BasinNoise,
            editStore,
            noise)
    {
    }

    public PlanetDensitySampler(
        PlanetConfig config,
        BiomeMap biomeMap,
        FractalNoise[] biomeNoises,
        FractalNoise? erosionNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        PlanetVoxelEditStore? editStore)
        : this(config, biomeMap, biomeNoises, erosionNoise, ridgeNoise, basinNoise, editStore, null)
    {
    }

    PlanetDensitySampler(
        PlanetConfig config,
        BiomeMap biomeMap,
        FractalNoise[] biomeNoises,
        FractalNoise? erosionNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        PlanetVoxelEditStore? editStore,
        PlanetNoiseCache? noise)
    {
        _config = config;
        _biomeMap = biomeMap;
        _biomeNoises = biomeNoises;
        _erosionNoise = erosionNoise;
        _ridgeNoise = ridgeNoise;
        _basinNoise = basinNoise;
        _editStore = editStore;
        _noise = noise;
    }

    /// <summary>Procedural crust density only (no caves, no paint strokes).</summary>
    public float SampleProceduralDensity(SN.Vector3 localPos)
    {
        float len = localPos.Length();
        if (len < 1e-5f)
            return 1f;

        var dir = localPos / len;
        float baseHeight = PlanetSurfaceUtility.SampleHeight(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _ridgeNoise,
            _basinNoise,
            dir);

        return len - (_config.Radius + baseHeight);
    }

    /// <summary>
    /// Same field used for transvoxel meshing: crust + worm caves + edit deltas.
    /// <paramref name="localPos"/> is planet-local unscaled space.
    /// </summary>
    public float SampleDensity(SN.Vector3 localPos)
    {
        float density = SampleProceduralDensity(localPos);
        density = ApplyCaveCarve(localPos, density);
        if (_editStore != null)
            density += _editStore.SampleDensityDelta(localPos);
        return density;
    }

    /// <summary>
    /// Outer crust radius along <paramref name="sphereDir"/>, including paint strokes
    /// but not worm caves (those must not punch the heightfield shell).
    /// </summary>
    public float SampleEditedSurfaceRadius(SN.Vector3 sphereDir, float vertexSpacing = 0f)
    {
        float lenSq = sphereDir.LengthSquared();
        if (lenSq < 1e-12f)
            return _config.Radius;
        var dir = sphereDir / MathF.Sqrt(lenSq);
        float height = PlanetSurfaceUtility.SampleHeight(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _ridgeNoise,
            _basinNoise,
            dir);
        float r0 = _config.Radius + height;
        if (_editStore == null || (_editStore.SphereEditCount == 0 && _editStore.BakedCellCount == 0))
            return r0;

        // Widen the footprint to the local vertex spacing so a 8–12 m brush still
        // hits Scene View verts (often 40–90 m apart). Do not scale the height.
        var surfacePos = dir * r0;
        float delta = _editStore.SampleHeightDelta(surfacePos, 0f);
        float maxDisp = MathF.Min(_config.Radius * 0.05f, 32f);
        delta = Math.Clamp(delta, -maxDisp, maxDisp);
        return MathF.Max(_config.Radius * 0.5f, r0 - delta);
    }

    /// <summary>Finite-difference gradient. Points toward air (increasing density).</summary>
    public SN.Vector3 SampleDensityGradient(SN.Vector3 localPos, float epsilon = 0.45f)
    {
        float e = MathF.Max(0.05f, epsilon);
        var ex = new SN.Vector3(e, 0f, 0f);
        var ey = new SN.Vector3(0f, e, 0f);
        var ez = new SN.Vector3(0f, 0f, e);
        var g = new SN.Vector3(
            SampleDensity(localPos + ex) - SampleDensity(localPos - ex),
            SampleDensity(localPos + ey) - SampleDensity(localPos - ey),
            SampleDensity(localPos + ez) - SampleDensity(localPos - ez));
        float len = g.Length();
        if (len < 1e-8f)
        {
            float r = localPos.Length();
            return r > 1e-6f ? localPos / r : SN.Vector3.UnitY;
        }
        return g / len;
    }

    /// <summary>3D worm-cave carve matching <see cref="DensityGenerator"/>.</summary>
    public float ApplyCaveCarve(SN.Vector3 localPos, float density)
    {
        if (_noise == null || !_config.EnableCaves)
            return density;

        float len = localPos.Length();
        if (len < 1e-5f)
            return density;

        var sphereDir = localPos / len;
        var blends = _biomeMap.GetBiomes(sphereDir);

        float blendedCaveDensity = 0f;
        bool anyCaves = false;

        for (int b = 0; b < blends.Length; b++)
        {
            var biome = blends[b].Biome;
            float w = blends[b].Weight;
            if (biome.CavesEnabled && biome.CaveDensity > 0.01f)
            {
                blendedCaveDensity += biome.CaveDensity * w;
                anyCaves = true;
            }
        }

        if (!anyCaves || blendedCaveDensity <= 0.01f)
            return density;

        float surfaceRadius = len - density;
        float below = surfaceRadius - len;
        // Thin roof so the outer crust stays intact; caves start just under it.
        if (below < 12f)
            return density;
        // Leave a tiny solid core so cube-sphere samples never collapse at r=0.
        if (len < MathF.Max(16f, _config.Radius * 0.035f))
            return density;

        float mouth = Smooth01(12f, 36f, below);
        float depthFrac = Math.Clamp(below / MathF.Max(1f, surfaceRadius - 24f), 0f, 1f);

        var warp = new SN.Vector3(
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f + 11f, localPos.Y * 0.0014f, localPos.Z * 0.0014f),
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f, localPos.Y * 0.0014f + 19f, localPos.Z * 0.0014f),
            _noise.CaveDetailNoise.Sample3D(localPos.X * 0.0014f, localPos.Y * 0.0014f, localPos.Z * 0.0014f + 7f));
        var p = localPos + warp * 28f;

        // Small tunnels — higher frequency, present at every depth.
        float smallN = _noise.CaveWormNoise.Sample3D(p.X * 0.0052f, p.Y * 0.0052f, p.Z * 0.0052f);
        float small = Smooth01(0.50f, 0.74f, smallN);

        // Medium passages — same scale as the old near-surface worms.
        float medN = _noise.CaveWormNoise.Sample3D(p.X * 0.0028f + 17f, p.Y * 0.0028f, p.Z * 0.0028f);
        float medium = Smooth01(0.48f, 0.70f, medN);

        // Large caverns — throughout, a bit more open toward the core.
        float cavernN = _noise.CaveCavernNoise.Sample3D(p.X * 0.0009f, p.Y * 0.0009f, p.Z * 0.0009f);
        cavernN = Math.Clamp(cavernN * 0.5f + 0.5f, 0f, 1f);
        float caverns = Smooth01(0.50f, 0.66f, cavernN);
        caverns *= 0.72f + 0.45f * depthFrac;

        // Sparse huge chambers, mostly in the inner half.
        float hugeN = _noise.CaveCavernNoise.Sample3D(p.X * 0.00042f + 63f, p.Y * 0.00042f, p.Z * 0.00042f);
        hugeN = Math.Clamp(hugeN * 0.5f + 0.5f, 0f, 1f);
        float huge = Smooth01(0.60f, 0.76f, hugeN) * Smooth01(0.28f, 0.62f, depthFrac);

        float open = MathF.Max(small * 0.82f, MathF.Max(medium * 0.75f, MathF.Max(caverns, huge)));
        open *= Math.Clamp(0.65f + blendedCaveDensity * 0.45f, 0.65f, 1.1f);
        float detail = _noise.CaveDetailNoise.Sample3D(p.X * 0.018f, p.Y * 0.018f, p.Z * 0.018f);
        open = Math.Clamp(open + detail * 0.06f * open, 0f, 1.15f);
        open *= mouth;

        if (open > 0.16f)
            density = Math.Max(density, open * 1.15f - 0.05f);

        return density;
    }

    static float Smooth01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
