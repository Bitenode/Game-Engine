#nullable enable
using System;
using System.Linq;
using System.Text;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Biome.Graph;

/// <summary>In-memory checks for biome-graph leftovers (no play mode required).</summary>
public static class BiomeGraphSmokeChecks
{
    public static string RunAll()
    {
        var sb = new StringBuilder();
        int fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) sb.AppendLine($"PASS  {name}");
            else
            {
                fail++;
                sb.AppendLine($"FAIL  {name}{(detail.Length > 0 ? ": " + detail : "")}");
            }
        }

        var g = BiomeGraph.CreateDefault();
        var grass = g.Nodes.OfType<BiomeLayerNode>().First(n => n.BiomeName == "Grassland");
        grass.WeatherProfileId = "Temperate";
        grass.VegetationProfileId = "Grassland";

        var season = g.AddNode<BiomeSeasonNode>();
        season.SeasonPhase = 0.25f;
        season.SnowLineAltitude = 0.66f;
        season.GrowthMultiplier = 1.4f;

        var weather = g.AddNode<BiomeWeatherProfileNode>();
        weather.ProfileId = "Temperate";
        weather.RainChance = 0.55f;
        weather.SnowChance = 0.02f;
        weather.StormChance = 0.08f;
        weather.WindBias = 1.3f;
        weather.CloudCoverageBias = 1.2f;
        weather.FogDensityBias = 0.9f;

        var flora = g.AddNode<BiomeFloraLayerNode>();
        flora.TargetBiome = "Grassland";
        flora.ProfileId = "Grassland";
        flora.GrassDensity = 0.7f;
        flora.BushDensity = 0.45f;
        flora.TreeDensity = 0.5f;

        var fauna = g.AddNode<BiomeFaunaLayerNode>();
        fauna.SpeciesId = "Deer";
        fauna.TargetBiome = "Grassland";
        fauna.BiomeMask = "Grassland,Forest";
        fauna.Density = 0.2f;

        var cloud = g.AddNode<BiomeCloudLayerNode>();
        cloud.CloudType = "Stratus";
        cloud.Coverage = 0.7f;
        cloud.Density = 1.1f;

        var wetland = g.AddNode<BiomeWetlandNode>();
        wetland.TargetBiome = "Grassland";
        wetland.FloodDepth = 2.5f;
        wetland.ReedDensity = 0.6f;
        wetland.MoistureBoost = 0.4f;

        var scatter = g.AddNode<BiomeScatterLayerNode>();
        scatter.TargetBiome = "Grassland";
        scatter.RockDensity = 0.5f;
        scatter.ProfileId = "Default";

        var underwater = g.AddNode<BiomeUnderwaterLifeNode>();
        underwater.ProfileId = "Ocean";
        underwater.KelpDensity = 0.3f;

        var vein = g.AddNode<BiomeResourceVeinNode>();
        vein.ResourceId = "Iron";
        vein.Density = 0.4f;
        vein.CaveOnlyBias = 0.8f;

        var output = g.OutputNode!;
        g.Connect(flora.Outputs[0], output.FindInput("Life")!);
        g.Connect(scatter.Outputs[0], output.FindInput("Scatter")!);
        g.Connect(cloud.Outputs[0], output.FindInput("Atmosphere")!);

        var result = g.Compile();

        Check("season phase compiled",
            result.Seasons.Length > 0 && Math.Abs(result.Seasons[0].SeasonPhase - 0.25f) < 1e-4f,
            $"phase={result.Seasons.FirstOrDefault()?.SeasonPhase}");

        Check("season snow line compiled",
            result.Seasons.Length > 0 && Math.Abs(result.Seasons[0].SnowLineAltitude - 0.66f) < 1e-4f);

        var grassLayer = result.Layers.FirstOrDefault(l => l.BiomeName == "Grassland");
        Check("weather profile applied to matching layer",
            grassLayer != null && Math.Abs(grassLayer.RainChance - 0.55f) < 1e-4f,
            $"rain={grassLayer?.RainChance}");
        Check("weather storm chance applied",
            grassLayer != null && Math.Abs(grassLayer.StormChance - 0.08f) < 1e-4f);

        Check("flora bush density on layer",
            grassLayer != null && Math.Abs(grassLayer.BushDensity - 0.45f) < 1e-4f,
            $"bush={grassLayer?.BushDensity}");
        Check("flora grass density on layer",
            grassLayer != null && Math.Abs(grassLayer.VegetationDensity - 0.7f) < 1e-4f);

        Check("deer fauna layer compiled with mask",
            result.FaunaLayers.Any(f =>
                f.SpeciesId == "Deer"
                && f.TargetBiome == "Grassland"
                && f.BiomeMask.Contains("Forest", StringComparison.OrdinalIgnoreCase)));

        Check("cloud type Stratus compiled",
            result.CloudLayers.Length > 0
            && string.Equals(result.CloudLayers[0].CloudType, "Stratus", StringComparison.OrdinalIgnoreCase));

        Check("wetland flood depth compiled",
            result.Wetlands.Length > 0 && Math.Abs(result.Wetlands[0].FloodDepth - 2.5f) < 1e-4f);

        Check("Life output port connected", result.HasLifePort && result.Recipe.Life.Connected);
        Check("Scatter output port connected", result.HasScatterPort && result.Recipe.Scatter.Connected);
        Check("Atmosphere output port connected", result.HasAtmospherePort && result.Recipe.Atmosphere.Connected);

        Check("scatter recipe compiled", result.ScatterLayers.Length > 0);
        Check("underwater recipe compiled", result.UnderwaterLife.Length > 0);
        Check("vein recipe compiled",
            result.ResourceVeins.Any(v => string.Equals(v.ResourceId, "Iron", StringComparison.OrdinalIgnoreCase)));

        // Wetland flood dip against a single-biome map (always Grassland).
        var cfg = new PlanetConfig
        {
            Radius = 1000f,
            Wetlands = result.Wetlands,
            Biomes = new[]
            {
                new BiomeDefinition
                {
                    Name = "Grassland", BiomeIndex = 0,
                    MinAltitude = 0f, MaxAltitude = 1f,
                    MinTemperature = 0f, MaxTemperature = 1f,
                    MinMoisture = 0f, MaxMoisture = 1f,
                },
            }
        };
        var map = new BiomeMap(42, cfg.Biomes, altitudeSeaLevel: 0f, altitudeMaxHeight: 1f, heightAmplitudeRef: 50f);
        float baseH = 20f;
        var equator = SN.Vector3.Normalize(new SN.Vector3(1f, 0f, 0f));
        float dipped = PlanetSurfaceUtility.ApplyWetlandFloodDip(cfg, equator, baseH, map);
        Check("wetland flood dips crust", dipped < baseH - 0.1f, $"base={baseH} dipped={dipped}");
        Check("wetland flood amount ≈ FloodDepth", Math.Abs((baseH - dipped) - 2.5f) < 0.15f,
            $"delta={baseH - dipped}");

        sb.AppendLine(fail == 0 ? "All biome graph smoke checks passed." : $"{fail} check(s) failed.");
        return sb.ToString();
    }
}
