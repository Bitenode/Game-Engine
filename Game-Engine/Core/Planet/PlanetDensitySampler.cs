using Game_Engine.Core.Biome;
using Game_Engine.Core.Noise;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Composes procedural planet density with runtime voxel edits.
/// Density convention: negative = solid, positive = air.
/// </summary>
public sealed class PlanetDensitySampler
{
    readonly PlanetConfig _config;
    readonly BiomeMap _biomeMap;
    readonly FractalNoise[] _biomeNoises;
    readonly FractalNoise? _erosionNoise;
    readonly FractalNoise? _caveNoise;
    readonly FractalNoise? _ridgeNoise;
    readonly FractalNoise? _basinNoise;
    readonly PlanetVoxelEditStore? _editStore;

    public PlanetDensitySampler(
        PlanetConfig config,
        BiomeMap biomeMap,
        FractalNoise[] biomeNoises,
        FractalNoise? erosionNoise,
        FractalNoise? caveNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        PlanetVoxelEditStore? editStore)
    {
        _config = config;
        _biomeMap = biomeMap;
        _biomeNoises = biomeNoises;
        _erosionNoise = erosionNoise;
        _caveNoise = caveNoise;
        _ridgeNoise = ridgeNoise;
        _basinNoise = basinNoise;
        _editStore = editStore;
    }

    public float SampleDensity(SN.Vector3 worldPos)
    {
        float len = worldPos.Length();
        if (len < 1e-5f)
            return 1f;

        var dir = worldPos / len;
        float baseHeight = PlanetSurfaceUtility.SampleHeight(
            _config,
            _biomeMap,
            _biomeNoises,
            _erosionNoise,
            _caveNoise,
            _ridgeNoise,
            _basinNoise,
            dir);

        float baseSurfaceRadius = _config.Radius + baseHeight;
        float density = len - baseSurfaceRadius;

        if (_editStore != null)
            density += _editStore.SampleDensityDelta(worldPos);

        return density;
    }
}
