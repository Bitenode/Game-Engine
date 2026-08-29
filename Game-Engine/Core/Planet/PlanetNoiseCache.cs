using Game_Engine.Core.Noise;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Shared fractal-noise instances for one planet. Reused across chunk jobs so
/// mesh generation does not allocate a new <see cref="FractalNoise"/> per leaf.
/// Sample methods are thread-safe if callers do not mutate Frequency/Lacunarity.
/// </summary>
public sealed class PlanetNoiseCache
{
    public FractalNoise[] BiomeNoises { get; }
    public FractalNoise ErosionNoise { get; }
    public FractalNoise? RidgeNoise { get; }
    public FractalNoise? BasinNoise { get; }
    public FractalNoise CaveCellNoise { get; }
    public FractalNoise CaveWormNoise { get; }
    public FractalNoise CaveCavernNoise { get; }
    public FractalNoise CaveDetailNoise { get; }
    public SimplexNoise RiverPrimary { get; }
    public SimplexNoise RiverMeander { get; }

    PlanetNoiseCache(
        FractalNoise[] biomeNoises,
        FractalNoise erosionNoise,
        FractalNoise? ridgeNoise,
        FractalNoise? basinNoise,
        FractalNoise caveCellNoise,
        FractalNoise caveWormNoise,
        FractalNoise caveCavernNoise,
        FractalNoise caveDetailNoise,
        SimplexNoise riverPrimary,
        SimplexNoise riverMeander)
    {
        BiomeNoises = biomeNoises;
        ErosionNoise = erosionNoise;
        RidgeNoise = ridgeNoise;
        BasinNoise = basinNoise;
        CaveCellNoise = caveCellNoise;
        CaveWormNoise = caveWormNoise;
        CaveCavernNoise = caveCavernNoise;
        CaveDetailNoise = caveDetailNoise;
        RiverPrimary = riverPrimary;
        RiverMeander = riverMeander;
    }

    public static PlanetNoiseCache Create(PlanetConfig config)
    {
        int seed = config.Seed;
        var biomes = config.Biomes;
        var biomeNoises = new FractalNoise[biomes.Length];
        for (int i = 0; i < biomes.Length; i++)
        {
            biomeNoises[i] = new FractalNoise(seed)
            {
                Octaves = biomes[i].NoiseOctaves,
                Frequency = biomes[i].NoiseFrequency,
                Lacunarity = biomes[i].NoiseLacunarity,
                Persistence = 0.5f,
                Mode = ParseMode(biomes[i].NoiseMode),
            };
        }

        // Frequency stays 1; callers scale sample coordinates by biome erosion frequency
        // so concurrent chunk jobs can share this instance.
        var erosionNoise = new FractalNoise(seed + 8000)
        {
            Octaves = 4,
            Frequency = 1f,
            Persistence = 0.45f,
            Mode = FractalMode.Ridged,
        };

        var ridgeNoise = config.RidgeStrength > 0f
            ? new FractalNoise(seed + 7100)
            {
                Octaves = 4,
                Frequency = config.MacroFrequency,
                Persistence = 0.5f,
                Mode = FractalMode.Ridged,
            }
            : null;

        var basinNoise = config.BasinStrength > 0f
            ? new FractalNoise(seed + 7200)
            {
                Octaves = 3,
                Frequency = config.MacroFrequency,
                Persistence = 0.55f,
                Mode = FractalMode.FBM,
            }
            : null;

        var caveCellNoise = new FractalNoise(seed + 1000)
        {
            Octaves = 3,
            Frequency = 1f,
            Lacunarity = 2.0f,
            Persistence = 0.5f,
            Mode = FractalMode.Ridged,
        };

        var caveWormNoise = new FractalNoise(seed + 2000)
        {
            Octaves = 3,
            Frequency = 1f,
            Lacunarity = 2.15f,
            Persistence = 0.48f,
            Mode = FractalMode.Ridged,
        };

        var caveCavernNoise = new FractalNoise(seed + 2400)
        {
            Octaves = 5,
            Frequency = 1f,
            Lacunarity = 2.0f,
            Persistence = 0.52f,
            Mode = FractalMode.FBM,
        };

        var caveDetailNoise = new FractalNoise(seed + 2800)
        {
            Octaves = 4,
            Frequency = 1f,
            Lacunarity = 2.2f,
            Persistence = 0.45f,
            Mode = FractalMode.FBM,
        };

        var riverPrimary = new SimplexNoise(seed + 10000);
        var riverMeander = new SimplexNoise(seed + 11000);

        return new PlanetNoiseCache(
            biomeNoises,
            erosionNoise,
            ridgeNoise,
            basinNoise,
            caveCellNoise,
            caveWormNoise,
            caveCavernNoise,
            caveDetailNoise,
            riverPrimary,
            riverMeander);
    }

    public static FractalMode ParseMode(string mode) => mode switch
    {
        "Ridged" => FractalMode.Ridged,
        "Billow" => FractalMode.Billow,
        _ => FractalMode.FBM,
    };
}
