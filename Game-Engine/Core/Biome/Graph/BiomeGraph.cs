#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game_Engine.Core.Planet;

namespace Game_Engine.Core.Biome.Graph;

public sealed class BiomeConnection
{
    public BiomePort From { get; }
    public BiomePort To { get; }
    public BiomeConnection(BiomePort from, BiomePort to) { From = from; To = to; }
}

/// <summary>
/// Result of compiling a biome graph into generation parameters (PlanetRecipe-lite).
/// </summary>
public sealed class BiomeGraphResult
{
    public float HeightAmplitude { get; set; } = 50f;
    public float NoiseFrequency { get; set; } = 0.005f;
    public int NoiseOctaves { get; set; } = 6;
    public float NoiseLacunarity { get; set; } = 2f;
    public float NoisePersistence { get; set; } = 0.5f;
    public string NoiseMode { get; set; } = "FBM";

    public bool EnableCaves { get; set; } = true;
    public float CaveFrequency { get; set; } = 0.02f;
    public float CaveThreshold { get; set; } = 0.18f;

    public float TemperatureLatWeight { get; set; } = 1f;
    public float TemperatureNoiseWeight { get; set; } = 0.15f;
    public float MoistureNoiseScale { get; set; } = 3f;

    /// <summary>When true, runtime BiomeMap prefers Select altitude/temp/moisture wiring.</summary>
    public bool UseBiomeSelect { get; set; }
    public float SelectAltitudeWeight { get; set; } = 0.45f;
    public float CompiledAltitudeHint { get; set; } = 0.35f;

    public BiomeLayerInfo[] Layers { get; set; } = Array.Empty<BiomeLayerInfo>();

    public float RiverWidth { get; set; } = 0.02f;
    public float RiverDepth { get; set; } = 5f;
    public float RiverFrequency { get; set; } = 0.003f;
    public float RiverMeander { get; set; } = 0.5f;
    public string[] RiverAllowedBiomes { get; set; } = Array.Empty<string>();
    public bool HasRiver { get; set; } = false;

    public PlanetWaterBody[] WaterBodies { get; set; } = Array.Empty<PlanetWaterBody>();
    public PlanetWaterPath[] WaterPaths { get; set; } = Array.Empty<PlanetWaterPath>();

    // Output port hooks
    public bool HasClimatePort { get; set; }
    public bool HasLifePort { get; set; }
    public bool HasScatterPort { get; set; }
    public bool HasAtmospherePort { get; set; }
    public float ClimateHint { get; set; }
    public float LifeHint { get; set; }
    public float ScatterHint { get; set; }
    public float AtmosphereHint { get; set; }

    public float AltitudeSeaLevel { get; set; }
    public float AltitudeMaxHeight { get; set; } = 1f;
    public float AltitudeWeight { get; set; } = 0.3f;

    /// <summary>Full compile recipe (LUTs, life, atmosphere).</summary>
    public PlanetRecipe Recipe { get; set; } = new();

    // Recipe list aliases (same arrays live on Recipe)
    public ContinentRecipe[] Continents { get => Recipe.Continents; set => Recipe.Continents = value; }
    public CraterRecipe[] Craters { get => Recipe.Craters; set => Recipe.Craters = value; }
    public VolcanoRecipe[] Volcanoes { get => Recipe.Volcanoes; set => Recipe.Volcanoes = value; }
    public CliffRecipe[] Cliffs { get => Recipe.Cliffs; set => Recipe.Cliffs = value; }
    public DomainWarpRecipe[] DomainWarps { get => Recipe.DomainWarps; set => Recipe.DomainWarps = value; }
    public ClimateNodeRecipe[] ClimateNodes { get => Recipe.ClimateNodes; set => Recipe.ClimateNodes = value; }
    public RainShadowRecipe[] RainShadows { get => Recipe.RainShadows; set => Recipe.RainShadows = value; }
    public SeasonRecipe[] Seasons { get => Recipe.Seasons; set => Recipe.Seasons = value; }
    public LatitudeBandRecipe[] LatitudeBands { get => Recipe.LatitudeBands; set => Recipe.LatitudeBands = value; }
    public FloraLayerRecipe[] FloraLayers { get => Recipe.FloraLayers; set => Recipe.FloraLayers = value; }
    public ScatterLayerRecipe[] ScatterLayers { get => Recipe.ScatterLayers; set => Recipe.ScatterLayers = value; }
    public FaunaLayerRecipe[] FaunaLayers { get => Recipe.FaunaLayers; set => Recipe.FaunaLayers = value; }
    public UnderwaterLifeRecipe[] UnderwaterLife { get => Recipe.UnderwaterLife; set => Recipe.UnderwaterLife = value; }
    public ResourceVeinRecipe[] ResourceVeins { get => Recipe.ResourceVeins; set => Recipe.ResourceVeins = value; }
    public AtmosphereNodeRecipe[] AtmosphereNodes { get => Recipe.AtmosphereNodes; set => Recipe.AtmosphereNodes = value; }
    public WeatherProfileRecipe[] WeatherProfiles { get => Recipe.WeatherProfiles; set => Recipe.WeatherProfiles = value; }
    public CloudLayerRecipe[] CloudLayers { get => Recipe.CloudLayers; set => Recipe.CloudLayers = value; }
    public IceSheetRecipe[] IceSheets { get => Recipe.IceSheets; set => Recipe.IceSheets = value; }
    public WetlandRecipe[] Wetlands { get => Recipe.Wetlands; set => Recipe.Wetlands = value; }

    /// <summary>Stable hash of compiled recipe for chunk cache keys.</summary>
    public ulong RecipeHash { get; set; }
}

public sealed class BiomeLayerInfo
{
    public string AlbedoPath { get; set; } = "";
    public string NormalPath { get; set; } = "";
    public float Tiling { get; set; } = 10f;
    public float Roughness { get; set; } = 0.8f;
    public float Metallic { get; set; } = 0f;
    public float BaseColorR { get; set; } = 0.5f;
    public float BaseColorG { get; set; } = 0.5f;
    public float BaseColorB { get; set; } = 0.5f;
    public string BiomeName { get; set; } = "";

    public string UnderTexturePath { get; set; } = "";
    public string UnderNormalPath { get; set; } = "";
    public float UnderTiling { get; set; } = 10f;
    public string NoiseMode { get; set; } = "FBM";
    public int NoiseOctaves { get; set; } = 6;
    public float ErosionStrength { get; set; } = 0f;
    public float ErosionFrequency { get; set; } = 0.01f;

    public bool SpawnWater { get; set; } = false;
    public float WaterShallowR { get; set; } = 0.08f;
    public float WaterShallowG { get; set; } = 0.30f;
    public float WaterShallowB { get; set; } = 0.38f;
    public float WaterDeepR { get; set; } = 0.02f;
    public float WaterDeepG { get; set; } = 0.08f;
    public float WaterDeepB { get; set; } = 0.22f;
    public float VegetationDensity { get; set; } = 0f;
    public float TreeDensity { get; set; } = 0f;
    public string VegetationProfileId { get; set; } = "Default";
    public float VegetationPatchiness { get; set; } = 0.45f;
    public string WeatherProfileId { get; set; } = "Temperate";
    public float RainChance { get; set; } = 0.15f;
    public float SnowChance { get; set; } = 0.04f;
    public float StormChance { get; set; } = 0.01f;
    public float WindBias { get; set; } = 1f;
    public float CloudCoverageBias { get; set; } = 1f;
    public float FogDensityBias { get; set; } = 1f;
    public float SeasonalGrowthMultiplier { get; set; } = 1f;

    public float HeightAmplitude { get; set; } = -1f;
    public float NoiseFrequency { get; set; } = -1f;
    public bool HasHeightInput { get; set; }
    public bool HasNoiseInput { get; set; }
    public bool HasErosionInput { get; set; }
    public float GrowthTemperatureMin { get; set; } = 0.2f;
    public float GrowthTemperatureMax { get; set; } = 0.8f;
    public float GrowthMoistureMin { get; set; } = 0.2f;
    public float GrowthMoistureMax { get; set; } = 0.9f;
    public float TreeMinSlope { get; set; }
    public float TreeMaxSlope { get; set; } = 35f;
    public float TreeMinAltitude { get; set; }
    public float TreeMaxAltitude { get; set; } = 0.85f;
}

/// <summary>
/// Container for a biome generation graph: nodes, connections, serialization, compilation.
/// </summary>
public sealed class BiomeGraph
{
    public List<BiomeNode> Nodes { get; } = new();
    public List<BiomeConnection> Connections { get; } = new();

    public BiomeOutputNode? OutputNode => Nodes.OfType<BiomeOutputNode>().FirstOrDefault();

    public T AddNode<T>() where T : BiomeNode, new()
    {
        var node = new T();
        Nodes.Add(node);
        return node;
    }

    public void AddNode(BiomeNode node) => Nodes.Add(node);

    public void RemoveNode(BiomeNode node)
    {
        foreach (var p in node.Inputs.Concat(node.Outputs))
            Disconnect(p);
        Nodes.Remove(node);
    }

    /// <summary>Compile-time float walk along connections (Math/Blend/Mask/Noise/Altitude/…).</summary>
    public static float EvaluateFloat(BiomePort? port) =>
        BiomeGraphEvaluator.EvaluateFloat(port, BiomeEvalContext.EquatorMid);

    public static float EvaluateFloat(BiomePort? port, in BiomeEvalContext ctx, Dictionary<string, float>? cache = null) =>
        BiomeGraphEvaluator.EvaluateFloat(port, in ctx, cache);

    public bool Connect(BiomePort from, BiomePort to)
    {
        if (!from.IsOutput || to.IsOutput) return false;
        if (from.Owner == to.Owner) return false;
        if (!TypesCompatible(from.DataType, to.DataType)) return false;

        if (to.Connection != null) Disconnect(to);

        to.Connection = from;
        Connections.Add(new BiomeConnection(from, to));
        return true;
    }

    static bool TypesCompatible(BiomeDataType a, BiomeDataType b)
    {
        if (a == b) return true;
        // Climate/Life/Scatter/Atmosphere accept a Float source so Select and math can wire in.
        bool aHook = a is BiomeDataType.Climate or BiomeDataType.Life or BiomeDataType.Scatter or BiomeDataType.Atmosphere;
        bool bHook = b is BiomeDataType.Climate or BiomeDataType.Life or BiomeDataType.Scatter or BiomeDataType.Atmosphere;
        return (a == BiomeDataType.Float && bHook) || (b == BiomeDataType.Float && aHook);
    }

    public void Disconnect(BiomePort port)
    {
        if (port.IsOutput)
        {
            var toRemove = Connections.Where(c => c.From == port).ToList();
            foreach (var conn in toRemove)
            {
                conn.To.Connection = null;
                Connections.Remove(conn);
            }
        }
        else
        {
            if (port.Connection != null)
            {
                port.Connection = null;
                Connections.RemoveAll(c => c.To == port);
            }
        }
    }

    /// <summary>Validate the graph and return any warnings/errors.</summary>
    public List<string> Validate()
    {
        var warnings = new List<string>();
        var output = OutputNode;
        if (output == null)
        {
            warnings.Add("No Output node found.");
            return warnings;
        }

        if (output.Inputs[0].Connection == null)
            warnings.Add("Output: Height input is not connected.");

        int layerCount = 0;
        foreach (var p in output.Inputs)
        {
            if (p.Name.StartsWith("Layer", StringComparison.Ordinal) &&
                p.Connection?.Owner is BiomeLayerNode)
                layerCount++;
        }
        if (layerCount == 0)
            warnings.Add("Output: No BiomeLayer nodes connected.");

        var visited = new HashSet<string>();
        foreach (var conn in Connections)
        {
            if (HasCycle(conn.To.Owner, conn.From.Owner, visited))
            {
                warnings.Add($"Circular connection detected involving '{conn.From.Owner.Name}' -> '{conn.To.Owner.Name}'.");
                break;
            }
            visited.Clear();
        }

        var reachable = new HashSet<string>();
        WalkReachable(output, reachable);
        foreach (var conn in Connections)
        {
            if (!reachable.Contains(conn.To.Owner.Id))
                warnings.Add($"Error: unused connection '{conn.From.Owner.Name}.{conn.From.Name}' -> '{conn.To.Owner.Name}.{conn.To.Name}'.");
        }

        foreach (var node in Nodes)
        {
            if (node is not BiomeSelectNode select) continue;
            bool used = select.Outputs.Any(op => Connections.Any(c => c.From == op));
            if (!used)
                warnings.Add("Error: BiomeSelect is dangling — connect BiomeIndex to Output.Climate.");
        }

        return warnings;
    }

    static void WalkReachable(BiomeNode node, HashSet<string> reachable)
    {
        if (!reachable.Add(node.Id)) return;
        foreach (var input in node.Inputs)
        {
            if (input.Connection != null)
                WalkReachable(input.Connection.Owner, reachable);
        }
    }

    bool HasCycle(BiomeNode current, BiomeNode target, HashSet<string> visited)
    {
        if (current == target) return true;
        if (!visited.Add(current.Id)) return false;
        foreach (var output in current.Outputs)
        {
            foreach (var conn in Connections.Where(c => c.From == output))
            {
                if (HasCycle(conn.To.Owner, target, visited))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Compile the graph into generation parameters and a <see cref="PlanetRecipe"/>.</summary>
    public BiomeGraphResult Compile()
    {
        var result = new BiomeGraphResult { Recipe = new PlanetRecipe() };
        var output = OutputNode;
        if (output == null) return result;

        var ctx = BiomeEvalContext.EquatorMid;
        var cache = new Dictionary<string, float>(128);

        CompileHeight(result, output.Inputs[0], in ctx, cache);
        CompileCave(result, output.Inputs[1], in ctx, cache);
        CompileClimateAndSelect(result, output, in ctx, cache);
        CompileLayers(result, output, in ctx, cache);
        CompileOutputHooks(result, output, in ctx, cache);
        CompileWater(result, output);
        CompileFeatureTables(result);
        FillRecipe(result);
        result.RecipeHash = ComputeRecipeHash(result);
        return result;
    }

    void CompileHeight(BiomeGraphResult result, BiomePort heightPort, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        if (heightPort.Connection == null)
        {
            result.HeightAmplitude = heightPort.DefaultValue[0];
            return;
        }

        _ = EvaluateFloat(heightPort, in ctx, cache);

        var hn = FindUpstream<BiomeHeightNode>(heightPort);
        var nn = FindUpstream<BiomeNoiseNode>(heightPort);
        var en = FindUpstream<BiomeErosionNode>(heightPort);

        if (hn != null)
        {
            float amp = hn.Inputs[2].Connection != null
                ? EvaluateFloat(hn.Inputs[2], in ctx, cache)
                : hn.Amplitude;
            result.HeightAmplitude = MathF.Max(1f, amp);
        }
        else
        {
            result.HeightAmplitude = MathF.Max(1f, MathF.Abs(EvaluateFloat(heightPort, in ctx, cache)));
        }

        if (nn != null)
        {
            result.NoiseFrequency = MathF.Max(0.00001f,
                nn.Inputs[0].Connection != null ? EvaluateFloat(nn.Inputs[0], in ctx, cache) : nn.Frequency);
            result.NoiseOctaves = nn.Octaves;
            result.NoiseMode = nn.NoiseMode;
        }

        if (en != null)
        {
            result.Recipe.Geology.ErosionStrength = en.Inputs[0].Connection != null
                ? EvaluateFloat(en.Inputs[0], in ctx, cache)
                : en.Strength;
            result.Recipe.Geology.ErosionFrequency = en.Inputs[1].Connection != null
                ? EvaluateFloat(en.Inputs[1], in ctx, cache)
                : en.Frequency;
            result.Recipe.Geology.ErosionOctaves = en.Octaves;
        }
    }

    static void CompileCave(BiomeGraphResult result, BiomePort cavePort, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        if (cavePort.Connection?.Owner is BiomeCaveNode cn)
        {
            result.EnableCaves = true;
            result.CaveFrequency = cn.Inputs[0].Connection != null
                ? EvaluateFloat(cn.Inputs[0], in ctx, cache)
                : cn.Frequency;
            result.CaveThreshold = cn.Inputs[1].Connection != null
                ? EvaluateFloat(cn.Inputs[1], in ctx, cache)
                : cn.Threshold;
        }
        else if (cavePort.Connection != null)
        {
            result.EnableCaves = true;
            result.CaveThreshold = EvaluateFloat(cavePort, in ctx, cache);
        }
        else
        {
            result.EnableCaves = false;
        }
    }

    void CompileClimateAndSelect(BiomeGraphResult result, BiomeOutputNode output, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        foreach (var node in Nodes)
        {
            if (node is BiomeTemperatureNode tn)
            {
                result.TemperatureLatWeight = tn.LatitudeWeight;
                result.TemperatureNoiseWeight = tn.NoiseWeight;
            }
            if (node is BiomeMoistureNode mn)
                result.MoistureNoiseScale = mn.NoiseScale;
        }

        var climatePort = output.FindInput("Climate");
        var select = climatePort?.Connection?.Owner as BiomeSelectNode
                     ?? Nodes.OfType<BiomeSelectNode>().FirstOrDefault();
        if (select == null) return;

        // Honor Select whenever it exists (default PlanetBiomes wires T/M/A into it).
        result.UseBiomeSelect = true;
        result.SelectAltitudeWeight = 0.55f;
        result.AltitudeWeight = 0.55f;

        if (select.Inputs[0].Connection?.Owner is BiomeTemperatureNode selT)
        {
            result.TemperatureLatWeight = selT.LatitudeWeight;
            result.TemperatureNoiseWeight = selT.NoiseWeight;
        }
        else if (select.Inputs[0].Connection != null)
            result.TemperatureLatWeight = EvaluateFloat(select.Inputs[0], in ctx, cache);

        if (select.Inputs[1].Connection?.Owner is BiomeMoistureNode selM)
            result.MoistureNoiseScale = selM.NoiseScale;
        else if (select.Inputs[1].Connection != null)
            result.MoistureNoiseScale = EvaluateFloat(select.Inputs[1], in ctx, cache);

        if (select.Inputs[2].Connection?.Owner is BiomeAltitudeNode alt)
        {
            result.AltitudeSeaLevel = alt.Inputs[1].Connection != null
                ? EvaluateFloat(alt.Inputs[1], in ctx, cache)
                : alt.SeaLevel;
            result.AltitudeMaxHeight = alt.Inputs[2].Connection != null
                ? EvaluateFloat(alt.Inputs[2], in ctx, cache)
                : alt.MaxHeight;
            result.CompiledAltitudeHint = EvaluateFloat(select.Inputs[2], in ctx, cache);
            result.Recipe.Climate.HasAltitudeFromGraph = true;
        }
        else if (select.Inputs[2].Connection != null)
        {
            result.CompiledAltitudeHint = EvaluateFloat(select.Inputs[2], in ctx, cache);
            result.Recipe.Climate.HasAltitudeFromGraph = true;
        }
    }

    void CompileLayers(BiomeGraphResult result, BiomeOutputNode output, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        var layers = new List<BiomeLayerInfo>();
        foreach (var layerPort in output.Inputs)
        {
            if (layerPort.DataType != BiomeDataType.BiomeLayer) continue;
            if (layerPort.Connection?.Owner is not BiomeLayerNode ln) continue;

            var heightIn = ln.FindInput("HeightAmp") ?? (ln.Inputs.Count > 0 ? ln.Inputs[0] : null);
            var noiseIn = ln.FindInput("NoiseFreq") ?? (ln.Inputs.Count > 1 ? ln.Inputs[1] : null);
            var erosionIn = ln.FindInput("Erosion") ?? (ln.Inputs.Count > 2 ? ln.Inputs[2] : null);

            bool hasHeight = heightIn?.Connection != null || ln.HeightAmplitude > 0f;
            bool hasNoise = noiseIn?.Connection != null || ln.NoiseFrequency > 0f;
            bool hasErosion = erosionIn?.Connection != null;

            float heightAmp = ln.HeightAmplitude;
            float noiseFreq = ln.NoiseFrequency;
            float erosionStr = ln.ErosionStrength;
            float erosionFreq = ln.ErosionFrequency;

            if (heightIn?.Connection != null)
            {
                var hn = FindUpstream<BiomeHeightNode>(heightIn);
                heightAmp = hn != null
                    ? (hn.Inputs[2].Connection != null ? EvaluateFloat(hn.Inputs[2], in ctx, cache) : hn.Amplitude)
                    : EvaluateFloat(heightIn, in ctx, cache);
            }

            if (noiseIn?.Connection != null)
            {
                var nn = FindUpstream<BiomeNoiseNode>(noiseIn);
                if (nn != null)
                {
                    noiseFreq = nn.Inputs[0].Connection != null
                        ? EvaluateFloat(nn.Inputs[0], in ctx, cache)
                        : nn.Frequency;
                }
                else
                    noiseFreq = EvaluateFloat(noiseIn, in ctx, cache);
            }

            if (erosionIn?.Connection != null)
            {
                var en = FindUpstream<BiomeErosionNode>(erosionIn);
                if (en != null)
                {
                    erosionStr = en.Inputs[0].Connection != null
                        ? EvaluateFloat(en.Inputs[0], in ctx, cache)
                        : en.Strength;
                    erosionFreq = en.Inputs[1].Connection != null
                        ? EvaluateFloat(en.Inputs[1], in ctx, cache)
                        : en.Frequency;
                }
                else
                    erosionStr = EvaluateFloat(erosionIn, in ctx, cache);
            }

            layers.Add(new BiomeLayerInfo
            {
                AlbedoPath = ln.AlbedoPath,
                NormalPath = ln.NormalPath,
                Tiling = ln.Tiling,
                Roughness = ln.Roughness,
                Metallic = ln.Metallic,
                BaseColorR = ln.BaseColorR,
                BaseColorG = ln.BaseColorG,
                BaseColorB = ln.BaseColorB,
                BiomeName = ln.BiomeName,
                UnderTexturePath = ln.UnderTexturePath,
                UnderNormalPath = ln.UnderNormalPath,
                UnderTiling = ln.UnderTiling,
                NoiseMode = ln.NoiseMode,
                NoiseOctaves = ln.NoiseOctaves,
                ErosionStrength = erosionStr,
                ErosionFrequency = erosionFreq,
                SpawnWater = ln.SpawnWater,
                WaterShallowR = ln.WaterShallowR,
                WaterShallowG = ln.WaterShallowG,
                WaterShallowB = ln.WaterShallowB,
                WaterDeepR = ln.WaterDeepR,
                WaterDeepG = ln.WaterDeepG,
                WaterDeepB = ln.WaterDeepB,
                VegetationDensity = ln.VegetationDensity,
                TreeDensity = ln.TreeDensity,
                VegetationProfileId = ln.VegetationProfileId,
                VegetationPatchiness = ln.VegetationPatchiness,
                WeatherProfileId = ln.WeatherProfileId,
                RainChance = ln.RainChance,
                SnowChance = ln.SnowChance,
                StormChance = ln.StormChance,
                WindBias = ln.WindBias,
                CloudCoverageBias = ln.CloudCoverageBias,
                FogDensityBias = ln.FogDensityBias,
                SeasonalGrowthMultiplier = ln.SeasonalGrowthMultiplier,
                HeightAmplitude = heightAmp,
                NoiseFrequency = noiseFreq,
                HasHeightInput = hasHeight,
                HasNoiseInput = hasNoise,
                HasErosionInput = hasErosion,
                GrowthTemperatureMin = ln.GrowthTemperatureMin,
                GrowthTemperatureMax = ln.GrowthTemperatureMax,
                GrowthMoistureMin = ln.GrowthMoistureMin,
                GrowthMoistureMax = ln.GrowthMoistureMax,
            });
        }
        result.Layers = layers.ToArray();
    }

    static void CompileOutputHooks(BiomeGraphResult result, BiomeOutputNode output, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        var climate = output.FindInput("Climate");
        if (climate?.Connection != null)
        {
            result.HasClimatePort = true;
            result.ClimateHint = EvaluateFloat(climate, in ctx, cache);
        }

        CaptureHook(output, "Life", result.Recipe.Life, v => { result.HasLifePort = true; result.LifeHint = v; }, in ctx, cache);
        CaptureHook(output, "Scatter", result.Recipe.Scatter, v => { result.HasScatterPort = true; result.ScatterHint = v; }, in ctx, cache);
        CaptureHook(output, "Atmosphere", result.Recipe.Atmosphere, v => { result.HasAtmospherePort = true; result.AtmosphereHint = v; }, in ctx, cache);
    }

    static void CaptureHook(BiomeOutputNode output, string name, GraphPortRecipe dest, Action<float> setHint, in BiomeEvalContext ctx, Dictionary<string, float> cache)
    {
        var p = output.FindInput(name);
        if (p?.Connection == null) return;
        dest.Connected = true;
        dest.NodeId = p.Connection.Owner.Id;
        dest.NodeType = p.Connection.Owner.GetType().Name;
        dest.NodeName = p.Connection.Owner.Name;
        dest.PortName = p.Connection.Name;
        dest.EvaluatedValue = EvaluateFloat(p, in ctx, cache);
        setHint(dest.EvaluatedValue);
    }

    void CompileFeatureTables(BiomeGraphResult result)
    {
        var continents = new List<ContinentRecipe>();
        var craters = new List<CraterRecipe>();
        var volcanoes = new List<VolcanoRecipe>();
        var cliffs = new List<CliffRecipe>();
        var warps = new List<DomainWarpRecipe>();
        var climates = new List<ClimateNodeRecipe>();
        var rainShadows = new List<RainShadowRecipe>();
        var seasons = new List<SeasonRecipe>();
        var bands = new List<LatitudeBandRecipe>();
        var flora = new List<FloraLayerRecipe>();
        var scatter = new List<ScatterLayerRecipe>();
        var fauna = new List<FaunaLayerRecipe>();
        var underwater = new List<UnderwaterLifeRecipe>();
        var veins = new List<ResourceVeinRecipe>();
        var atmospheres = new List<AtmosphereNodeRecipe>();
        var weather = new List<WeatherProfileRecipe>();
        var clouds = new List<CloudLayerRecipe>();
        var ice = new List<IceSheetRecipe>();
        var wetlands = new List<WetlandRecipe>();

        foreach (var node in Nodes)
        {
            switch (node)
            {
                case BiomeContinentNode n:
                    continents.Add(new ContinentRecipe
                    {
                        NodeId = n.Id, Frequency = n.Frequency, Threshold = n.Threshold,
                        Strength = n.Strength, Seed = n.Seed
                    });
                    if (continents.Count == 1 && n.Frequency > 0f)
                        result.Recipe.Geology.MacroFrequency = n.Frequency;
                    break;
                case BiomeCraterNode n:
                    craters.Add(new CraterRecipe
                    {
                        NodeId = n.Id, Radius = n.Radius, Depth = n.Depth,
                        RimHeight = n.RimHeight, Density = n.Density, Seed = n.Seed
                    });
                    break;
                case BiomeVolcanoNode n:
                    volcanoes.Add(new VolcanoRecipe
                    {
                        NodeId = n.Id, Radius = n.Radius, Height = n.Height,
                        CalderaRadius = n.CalderaRadius, LavaBiomeName = n.LavaBiomeName,
                        Density = n.Density, Seed = n.Seed
                    });
                    break;
                case BiomeCliffNode n:
                    cliffs.Add(new CliffRecipe
                    {
                        NodeId = n.Id, Strength = n.Strength,
                        Frequency = n.Frequency, SlopeBias = n.SlopeBias
                    });
                    break;
                case BiomeDomainWarpNode n:
                    warps.Add(new DomainWarpRecipe
                    {
                        NodeId = n.Id, Strength = n.Strength,
                        Frequency = n.Frequency, Octaves = n.Octaves, Seed = n.Seed
                    });
                    break;
                case BiomeClimateNode n:
                    climates.Add(new ClimateNodeRecipe
                    {
                        NodeId = n.Id, LatitudeWeight = n.LatitudeWeight,
                        AltitudeLapse = n.AltitudeLapse, MoistureWeight = n.MoistureWeight,
                        NoiseWeight = n.NoiseWeight
                    });
                    break;
                case BiomeRainShadowNode n:
                    rainShadows.Add(new RainShadowRecipe
                    {
                        NodeId = n.Id, Strength = n.Strength,
                        Width = n.Width, RidgeFrequency = n.RidgeFrequency
                    });
                    break;
                case BiomeSeasonNode n:
                    seasons.Add(new SeasonRecipe
                    {
                        NodeId = n.Id, GrowthMultiplier = n.GrowthMultiplier,
                        SnowLineAltitude = n.SnowLineAltitude, SeasonPhase = n.SeasonPhase
                    });
                    if (seasons.Count == 1)
                    {
                        for (int i = 0; i < result.Layers.Length; i++)
                            result.Layers[i].SeasonalGrowthMultiplier = n.GrowthMultiplier;
                    }
                    break;
                case BiomeLatitudeBandNode n:
                    bands.Add(new LatitudeBandRecipe
                    {
                        NodeId = n.Id, MinLatitude = n.MinLatitude, MaxLatitude = n.MaxLatitude,
                        TemperatureBias = n.TemperatureBias, MoistureBias = n.MoistureBias,
                        BandName = n.BandName
                    });
                    break;
                case BiomeFloraLayerNode n:
                    flora.Add(new FloraLayerRecipe
                    {
                        NodeId = n.Id, ProfileId = n.ProfileId, TargetBiome = n.TargetBiome,
                        GrassDensity = n.GrassDensity, BushDensity = n.BushDensity,
                        TreeDensity = n.TreeDensity, Patchiness = n.Patchiness,
                        MinSlope = n.MinSlope, MaxSlope = n.MaxSlope,
                        MinAltitude = n.MinAltitude, MaxAltitude = n.MaxAltitude,
                        GrowthTemperatureMin = n.GrowthTemperatureMin,
                        GrowthTemperatureMax = n.GrowthTemperatureMax,
                        GrowthMoistureMin = n.GrowthMoistureMin,
                        GrowthMoistureMax = n.GrowthMoistureMax
                    });
                    ApplyFloraToMatchingLayers(result.Layers, n);
                    break;
                case BiomeScatterLayerNode n:
                    scatter.Add(new ScatterLayerRecipe
                    {
                        NodeId = n.Id, ProfileId = n.ProfileId, TargetBiome = n.TargetBiome,
                        RockDensity = n.RockDensity, DebrisDensity = n.DebrisDensity,
                        MinSlope = n.MinSlope, MaxSlope = n.MaxSlope,
                        MinAltitude = n.MinAltitude, MaxAltitude = n.MaxAltitude,
                        ScatterType = n.ScatterType
                    });
                    break;
                case BiomeFaunaLayerNode n:
                    fauna.Add(new FaunaLayerRecipe
                    {
                        NodeId = n.Id, SpeciesId = n.SpeciesId, TargetBiome = n.TargetBiome,
                        HerdSpacing = n.HerdSpacing, Density = n.Density,
                        Diurnal = n.Diurnal, BiomeMask = n.BiomeMask
                    });
                    break;
                case BiomeUnderwaterLifeNode n:
                    underwater.Add(new UnderwaterLifeRecipe
                    {
                        NodeId = n.Id, ProfileId = n.ProfileId,
                        KelpDensity = n.KelpDensity, CoralDensity = n.CoralDensity,
                        FishDensity = n.FishDensity, MinDepth = n.MinDepth,
                        MaxDepth = n.MaxDepth, RequireWaterPlanet = n.RequireWaterPlanet
                    });
                    break;
                case BiomeResourceVeinNode n:
                    veins.Add(new ResourceVeinRecipe
                    {
                        NodeId = n.Id, ResourceId = n.ResourceId, Density = n.Density,
                        Frequency = n.Frequency, CaveOnlyBias = n.CaveOnlyBias, Seed = n.Seed
                    });
                    break;
                case BiomeAtmosphereNode n:
                    atmospheres.Add(new AtmosphereNodeRecipe
                    {
                        NodeId = n.Id, Preset = n.Preset,
                        RayleighStrength = n.RayleighStrength, MieStrength = n.MieStrength,
                        DayLengthMinutes = n.DayLengthMinutes, AtmosphereHeight = n.AtmosphereHeight
                    });
                    break;
                case BiomeWeatherProfileNode n:
                    weather.Add(new WeatherProfileRecipe
                    {
                        NodeId = n.Id, ProfileId = n.ProfileId,
                        RainChance = n.RainChance, SnowChance = n.SnowChance,
                        StormChance = n.StormChance, WindBias = n.WindBias,
                        CloudCoverageBias = n.CloudCoverageBias, FogDensityBias = n.FogDensityBias
                    });
                    break;
                case BiomeCloudLayerNode n:
                    clouds.Add(new CloudLayerRecipe
                    {
                        NodeId = n.Id, Coverage = n.Coverage, Density = n.Density,
                        BaseHeight = n.BaseHeight, TopHeight = n.TopHeight, CloudType = n.CloudType
                    });
                    break;
                case BiomeIceSheetNode n:
                    ice.Add(new IceSheetRecipe
                    {
                        NodeId = n.Id, MaxTemperature = n.MaxTemperature,
                        Thickness = n.Thickness, Coverage = n.Coverage,
                        TargetWaterKind = n.TargetWaterKind
                    });
                    break;
                case BiomeWetlandNode n:
                    wetlands.Add(new WetlandRecipe
                    {
                        NodeId = n.Id, FloodDepth = n.FloodDepth,
                        ReedDensity = n.ReedDensity, MoistureBoost = n.MoistureBoost,
                        TargetBiome = n.TargetBiome
                    });
                    break;
            }
        }

        result.Continents = continents.ToArray();
        result.Craters = craters.ToArray();
        result.Volcanoes = volcanoes.ToArray();
        result.Cliffs = cliffs.ToArray();
        result.DomainWarps = warps.ToArray();
        result.ClimateNodes = climates.ToArray();
        result.RainShadows = rainShadows.ToArray();
        result.Seasons = seasons.ToArray();
        result.LatitudeBands = bands.ToArray();
        result.FloraLayers = flora.ToArray();
        result.ScatterLayers = scatter.ToArray();
        result.FaunaLayers = fauna.ToArray();
        result.UnderwaterLife = underwater.ToArray();
        result.ResourceVeins = veins.ToArray();
        result.AtmosphereNodes = atmospheres.ToArray();
        result.WeatherProfiles = weather.ToArray();
        result.CloudLayers = clouds.ToArray();
        result.IceSheets = ice.ToArray();
        result.Wetlands = wetlands.ToArray();
    }

    static void ApplyFloraToMatchingLayers(BiomeLayerInfo[] layers, BiomeFloraLayerNode flora)
    {
        if (layers.Length == 0) return;
        for (int i = 0; i < layers.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(flora.TargetBiome) &&
                !string.Equals(layers[i].BiomeName, flora.TargetBiome, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(flora.ProfileId))
                layers[i].VegetationProfileId = flora.ProfileId;
            if (flora.GrassDensity > 0f)
                layers[i].VegetationDensity = flora.GrassDensity;
            if (flora.TreeDensity > 0f)
                layers[i].TreeDensity = flora.TreeDensity;
            layers[i].VegetationPatchiness = flora.Patchiness;
            layers[i].GrowthTemperatureMin = flora.GrowthTemperatureMin;
            layers[i].GrowthTemperatureMax = flora.GrowthTemperatureMax;
            layers[i].GrowthMoistureMin = flora.GrowthMoistureMin;
            layers[i].GrowthMoistureMax = flora.GrowthMoistureMax;
            layers[i].TreeMinSlope = flora.MinSlope;
            layers[i].TreeMaxSlope = flora.MaxSlope;
            layers[i].TreeMinAltitude = flora.MinAltitude;
            layers[i].TreeMaxAltitude = flora.MaxAltitude;

            if (!string.IsNullOrWhiteSpace(flora.TargetBiome))
                break;
        }
    }

    static void FillRecipe(BiomeGraphResult result)
    {
        var recipe = result.Recipe;
        recipe.Climate.TemperatureLatWeight = result.TemperatureLatWeight;
        recipe.Climate.TemperatureNoiseWeight = result.TemperatureNoiseWeight;
        recipe.Climate.MoistureNoiseScale = result.MoistureNoiseScale;
        recipe.Climate.AltitudeSeaLevel = result.AltitudeSeaLevel;
        recipe.Climate.AltitudeMaxHeight = result.AltitudeMaxHeight;
        recipe.Climate.AltitudeWeight = result.AltitudeWeight;

        // Climate coupling authored via Climate / RainShadow nodes when present.
        if (recipe.ClimateNodes is { Length: > 0 })
        {
            float lapse = 0f;
            float moistW = 0f;
            for (int i = 0; i < recipe.ClimateNodes.Length; i++)
            {
                lapse += recipe.ClimateNodes[i].AltitudeLapse;
                moistW += recipe.ClimateNodes[i].MoistureWeight;
            }
            recipe.Climate.AltitudeLapseRate = lapse / recipe.ClimateNodes.Length;
            recipe.Climate.WaterMoistureBoost = Math.Clamp(moistW / recipe.ClimateNodes.Length * 0.35f, 0.05f, 1f);
        }
        if (recipe.RainShadows is { Length: > 0 })
        {
            float s = 0f;
            for (int i = 0; i < recipe.RainShadows.Length; i++)
                s += recipe.RainShadows[i].Strength;
            recipe.Climate.RainShadowStrength = s / recipe.RainShadows.Length;
        }

        recipe.Geology.HeightAmplitude = result.HeightAmplitude;
        recipe.Geology.NoiseFrequency = result.NoiseFrequency;
        recipe.Geology.NoiseOctaves = result.NoiseOctaves;
        recipe.Geology.NoiseMode = result.NoiseMode;
        recipe.Geology.NoiseLacunarity = result.NoiseLacunarity;
        recipe.Geology.NoisePersistence = result.NoisePersistence;

        recipe.Cave.Enable = result.EnableCaves;
        recipe.Cave.Frequency = result.CaveFrequency;
        recipe.Cave.Threshold = result.CaveThreshold;

        recipe.Classifier.UseSelectClassifier = result.UseBiomeSelect;
        recipe.Classifier.Rules = CompileSelectRules(result.Layers);
    }

    static BiomeSelectRule[] CompileSelectRules(BiomeLayerInfo[] layers)
    {
        var presets = BiomeDefinition.AllPresets;
        var rules = new BiomeSelectRule[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            var preset = FindPreset(layers[i].BiomeName) ?? presets[Math.Min(i, presets.Length - 1)];
            rules[i] = new BiomeSelectRule
            {
                BiomeName = layers[i].BiomeName,
                LayerIndex = i,
                MinTemperature = preset.MinTemperature,
                MaxTemperature = preset.MaxTemperature,
                MinMoisture = preset.MinMoisture,
                MaxMoisture = preset.MaxMoisture,
                MinAltitude = preset.MinAltitude,
                MaxAltitude = preset.MaxAltitude,
            };
        }
        return rules;
    }

    static BiomeDefinition FindPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BiomeDefinition.Grassland;
        foreach (var p in BiomeDefinition.AllPresets)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return BiomeDefinition.Grassland;
    }

    static T? FindUpstream<T>(BiomePort port) where T : BiomeNode
    {
        var seen = new HashSet<string>();
        var q = new Queue<BiomePort>();
        if (port.IsOutput) q.Enqueue(port);
        else if (port.Connection != null) q.Enqueue(port.Connection);

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            var node = p.Owner;
            if (node == null || !seen.Add(node.Id)) continue;
            if (node is T match) return match;
            foreach (var input in node.Inputs)
            {
                if (input.Connection != null)
                    q.Enqueue(input.Connection);
            }
        }
        return null;
    }

    static ulong ComputeRecipeHash(BiomeGraphResult r)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }
        void MixF(float f) => Mix((ulong)BitConverter.SingleToInt32Bits(f));
        void MixS(string? s)
        {
            if (string.IsNullOrEmpty(s)) { Mix(0); return; }
            foreach (char c in s) Mix(c);
        }

        MixF(r.HeightAmplitude);
        MixF(r.NoiseFrequency);
        Mix((ulong)r.NoiseOctaves);
        MixS(r.NoiseMode);
        Mix(r.EnableCaves ? 1UL : 0UL);
        MixF(r.CaveFrequency);
        MixF(r.CaveThreshold);
        MixF(r.TemperatureLatWeight);
        MixF(r.MoistureNoiseScale);
        Mix(r.UseBiomeSelect ? 1UL : 0UL);
        Mix((ulong)r.Layers.Length);
        for (int i = 0; i < r.Layers.Length; i++)
        {
            var L = r.Layers[i];
            MixS(L.BiomeName);
            MixF(L.HeightAmplitude);
            MixF(L.NoiseFrequency);
            MixS(L.NoiseMode);
            MixF(L.ErosionStrength);
            MixS(L.AlbedoPath);
            MixS(L.VegetationProfileId);
        }
        Mix((ulong)r.WaterBodies.Length);
        Mix((ulong)r.WaterPaths.Length);
        Mix((ulong)r.FloraLayers.Length);
        Mix((ulong)r.ScatterLayers.Length);
        Mix((ulong)r.FaunaLayers.Length);
        Mix((ulong)r.UnderwaterLife.Length);
        Mix((ulong)r.AtmosphereNodes.Length);
        Mix((ulong)r.Continents.Length);
        MixF(r.Recipe.Geology.MacroFrequency);
        return h;
    }

    void CompileWater(BiomeGraphResult result, BiomeOutputNode output)
    {
        var collected = new List<BiomeNode>();
        var visited = new HashSet<string>();

        BiomePort? waterPort = output.Inputs.FirstOrDefault(p => p.Name == "Water");
        if (waterPort?.Connection != null)
            CollectWaterFromPort(waterPort, collected, visited);
        else
        {
            foreach (var node in Nodes)
            {
                if (node is BiomeWaterBodyNode or BiomeWaterPathNode or BiomeRiverNode)
                    collected.Add(node);
            }
        }

        var bodies = new List<PlanetWaterBody>();
        var paths = new List<PlanetWaterPath>();
        var shoreOverrides = new List<BiomeShoreNode>();

        foreach (var node in collected)
        {
            switch (node)
            {
                case BiomeWaterBodyNode wb:
                    bodies.Add(ConvertWaterBody(wb));
                    break;
                case BiomeWaterPathNode wp:
                    paths.Add(ConvertWaterPath(wp));
                    break;
                case BiomeRiverNode rn:
                    paths.Add(ConvertRiver(rn));
                    break;
                case BiomeShoreNode sh:
                    shoreOverrides.Add(sh);
                    break;
            }
        }

        foreach (var shore in shoreOverrides)
        {
            var bodyNode = FindUpstreamWaterBody(shore);
            if (bodyNode == null) continue;
            int bodyIdx = 0;
            for (int i = 0; i < collected.Count; i++)
            {
                if (collected[i] is BiomeWaterBodyNode wb && wb.Id == bodyNode.Id)
                    break;
                if (collected[i] is BiomeWaterBodyNode)
                    bodyIdx++;
            }
            if (bodyIdx < bodies.Count)
            {
                if (!string.IsNullOrWhiteSpace(shore.ShoreBiomeName))
                    bodies[bodyIdx].ShoreBiomeName = shore.ShoreBiomeName;
                if (shore.ShoreWidth > 0f)
                    bodies[bodyIdx].ShoreWidth = shore.ShoreWidth;
            }

            if (!string.IsNullOrWhiteSpace(shore.TexturePath) || shore.Tiling > 0f)
            {
                string biomeName = string.IsNullOrWhiteSpace(shore.ShoreBiomeName) ? "Beach" : shore.ShoreBiomeName;
                for (int li = 0; li < result.Layers.Length; li++)
                {
                    if (!string.Equals(result.Layers[li].BiomeName, biomeName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrWhiteSpace(shore.TexturePath))
                        result.Layers[li].AlbedoPath = shore.TexturePath;
                    if (shore.Tiling > 0f)
                        result.Layers[li].Tiling = shore.Tiling;
                    break;
                }
            }
        }

        result.WaterBodies = bodies.Take(PlanetWaterSampler.MaxWaterBodies).ToArray();
        result.WaterPaths = paths.Take(PlanetWaterSampler.MaxWaterPaths).ToArray();

        if (paths.Count > 0)
        {
            var first = paths[0];
            result.HasRiver = true;
            result.RiverWidth = first.Width;
            result.RiverDepth = first.Depth;
            result.RiverFrequency = first.Frequency;
            result.RiverMeander = first.Meander;
            result.RiverAllowedBiomes = first.AllowedBiomes;
        }
        else
        {
            foreach (var node in Nodes)
            {
                if (node is BiomeRiverNode rn)
                {
                    result.HasRiver = true;
                    result.RiverWidth = rn.RiverWidth;
                    result.RiverDepth = rn.RiverDepth;
                    result.RiverFrequency = rn.Frequency;
                    result.RiverMeander = rn.Meander;
                    if (!string.IsNullOrWhiteSpace(rn.AllowedBiomes))
                        result.RiverAllowedBiomes = rn.AllowedBiomes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    break;
                }
            }
        }
    }

    static BiomeWaterBodyNode? FindUpstreamWaterBody(BiomeShoreNode shore)
    {
        var port = shore.Inputs.Count > 0 ? shore.Inputs[0].Connection : null;
        while (port != null)
        {
            if (port.Owner is BiomeWaterBodyNode wb)
                return wb;
            if (port.Owner is BiomeWaterMergeNode merge)
            {
                port = merge.Inputs[0].Connection ?? merge.Inputs[1].Connection;
                continue;
            }
            if (port.Owner is BiomeShoreNode innerShore)
            {
                port = innerShore.Inputs[0].Connection;
                continue;
            }
            break;
        }
        return null;
    }

    static void CollectWaterFromPort(BiomePort port, List<BiomeNode> collected, HashSet<string> visited)
    {
        var from = port.Connection;
        if (from == null) return;
        var owner = from.Owner;
        if (!visited.Add(owner.Id)) return;

        switch (owner)
        {
            case BiomeWaterBodyNode:
            case BiomeWaterPathNode:
            case BiomeRiverNode:
            case BiomeShoreNode:
                collected.Add(owner);
                if (owner is BiomeShoreNode shore && shore.Inputs[0].Connection != null)
                    CollectWaterFromPort(shore.Inputs[0], collected, visited);
                break;
            case BiomeWaterMergeNode merge:
                if (merge.Inputs[0].Connection != null)
                    CollectWaterFromPort(merge.Inputs[0], collected, visited);
                if (merge.Inputs[1].Connection != null)
                    CollectWaterFromPort(merge.Inputs[1], collected, visited);
                break;
        }
    }

    static PlanetWaterBody ConvertWaterBody(BiomeWaterBodyNode node)
    {
        var kind = Enum.TryParse<PlanetWaterBodyKind>(node.Kind, true, out var parsed)
            ? parsed
            : PlanetWaterBodyKind.Ocean;
        return new PlanetWaterBody
        {
            Kind = kind,
            FillFraction = node.FillFraction,
            MaskBiomes = SplitCsv(node.AllowedBiomes),
            MinBasinDepth = node.MinBasinDepth,
            ShallowR = node.ShallowR,
            ShallowG = node.ShallowG,
            ShallowB = node.ShallowB,
            DeepR = node.DeepR,
            DeepG = node.DeepG,
            DeepB = node.DeepB,
            DeepestR = node.DeepestR,
            DeepestG = node.DeepestG,
            DeepestB = node.DeepestB,
            ShoreBiomeName = string.IsNullOrWhiteSpace(node.ShoreBiomeName) ? "Beach" : node.ShoreBiomeName,
            ShoreWidth = node.ShoreWidth
        };
    }

    static PlanetWaterPath ConvertWaterPath(BiomeWaterPathNode node) => new()
    {
        Width = node.Width,
        Depth = node.Depth,
        Frequency = node.Frequency,
        Meander = node.Meander,
        AllowedBiomes = SplitCsv(node.AllowedBiomes),
        SandWidth = node.SandWidth,
        SandBiomeName = string.IsNullOrWhiteSpace(node.SandBiomeName) ? "Beach" : node.SandBiomeName,
        FlowToOcean = node.FlowToOcean
    };

    static PlanetWaterPath ConvertRiver(BiomeRiverNode node) => new()
    {
        Width = node.RiverWidth,
        Depth = node.RiverDepth,
        Frequency = node.Frequency,
        Meander = node.Meander,
        AllowedBiomes = SplitCsv(node.AllowedBiomes),
        SandWidth = node.SandWidth,
        SandBiomeName = string.IsNullOrWhiteSpace(node.SandBiomeName) ? "Beach" : node.SandBiomeName,
        FlowToOcean = node.FlowToOcean
    };

    static string[] SplitCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<string>();
        return csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Create a default graph with standard biome generation nodes.</summary>
    public static BiomeGraph CreateDefault()
    {
        var g = new BiomeGraph();

        var coords = g.AddNode<BiomeCoordinateNode>();
        coords.EditorX = 50; coords.EditorY = 50;

        var noise = g.AddNode<BiomeNoiseNode>();
        noise.EditorX = 50; noise.EditorY = 250;
        noise.Frequency = 0.005f; noise.Octaves = 6;

        var height = g.AddNode<BiomeHeightNode>();
        height.EditorX = 300; height.EditorY = 200;

        var cave = g.AddNode<BiomeCaveNode>();
        cave.EditorX = 300; cave.EditorY = 400;

        var temp = g.AddNode<BiomeTemperatureNode>();
        temp.EditorX = 50; temp.EditorY = 450;

        var moist = g.AddNode<BiomeMoistureNode>();
        moist.EditorX = 50; moist.EditorY = 600;

        var select = g.AddNode<BiomeSelectNode>();
        select.EditorX = 300; select.EditorY = 550;

        var layer0 = g.AddNode<BiomeLayerNode>();
        layer0.EditorX = 300; layer0.EditorY = 750;
        layer0.BiomeName = "Grassland"; layer0.BaseColorR = 0.3f; layer0.BaseColorG = 0.7f; layer0.BaseColorB = 0.2f;

        var layer1 = g.AddNode<BiomeLayerNode>();
        layer1.EditorX = 500; layer1.EditorY = 750;
        layer1.BiomeName = "Desert"; layer1.BaseColorR = 0.9f; layer1.BaseColorG = 0.75f; layer1.BaseColorB = 0.4f;

        var output = g.AddNode<BiomeOutputNode>();
        output.EditorX = 600; output.EditorY = 300;

        g.Connect(noise.Outputs[0], height.Inputs[0]);
        g.Connect(height.Outputs[0], output.Inputs[0]);
        g.Connect(cave.Outputs[0], output.Inputs[1]);
        g.Connect(temp.Outputs[0], select.Inputs[0]);
        g.Connect(moist.Outputs[0], select.Inputs[1]);
        g.Connect(select.Outputs[0], output.Inputs.First(p => p.Name == "Climate"));
        g.Connect(layer0.Outputs[0], output.Inputs[2]);
        g.Connect(layer1.Outputs[0], output.Inputs[3]);

        return g;
    }

    // ── Serialization ──

    public void SaveToFile(string path)
    {
        var root = new JsonObject();
        var nodesArr = new JsonArray();
        foreach (var node in Nodes)
        {
            var obj = new JsonObject
            {
                ["type"] = GetNodeTypeName(node),
                ["id"] = node.Id,
                ["name"] = node.Name,
                ["x"] = node.EditorX,
                ["y"] = node.EditorY,
            };
            SerializeNodeProps(node, obj);
            nodesArr.Add(obj);
        }
        root["nodes"] = nodesArr;

        var connsArr = new JsonArray();
        foreach (var conn in Connections)
        {
            connsArr.Add(new JsonObject
            {
                ["fromNode"] = conn.From.Owner.Id,
                ["fromPort"] = conn.From.Name,
                ["toNode"] = conn.To.Owner.Id,
                ["toPort"] = conn.To.Name,
            });
        }
        root["connections"] = connsArr;

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static BiomeGraph LoadFromFile(string path)
    {
        var g = new BiomeGraph();
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text);
        if (root == null) return g;

        var nodesArr = root["nodes"]?.AsArray();
        if (nodesArr != null)
        {
            foreach (var item in nodesArr)
            {
                if (item == null) continue;
                string type = item["type"]?.GetValue<string>() ?? "";
                var node = CreateNodeByType(type);
                if (node == null) continue;
                node.Id = item["id"]?.GetValue<string>() ?? node.Id;
                node.Name = item["name"]?.GetValue<string>() ?? node.Name;
                node.EditorX = item["x"]?.GetValue<float>() ?? 0;
                node.EditorY = item["y"]?.GetValue<float>() ?? 0;
                DeserializeNodeProps(node, item);
                g.Nodes.Add(node);
            }
        }

        var connsArr = root["connections"]?.AsArray();
        if (connsArr != null)
        {
            foreach (var item in connsArr)
            {
                if (item == null) continue;
                string fromId = item["fromNode"]?.GetValue<string>() ?? "";
                string fromPort = item["fromPort"]?.GetValue<string>() ?? "";
                string toId = item["toNode"]?.GetValue<string>() ?? "";
                string toPort = item["toPort"]?.GetValue<string>() ?? "";

                var fromNode = g.Nodes.FirstOrDefault(n => n.Id == fromId);
                var toNode = g.Nodes.FirstOrDefault(n => n.Id == toId);
                if (fromNode == null || toNode == null) continue;

                var from = fromNode.Outputs.FirstOrDefault(p => p.Name == fromPort);
                var to = toNode.Inputs.FirstOrDefault(p => p.Name == toPort);
                if (from != null && to != null)
                    g.Connect(from, to);
            }
        }

        return g;
    }

    static string GetNodeTypeName(BiomeNode node) => node switch
    {
        BiomeCoordinateNode => "Coordinate",
        BiomeNoiseNode => "Noise",
        BiomeTemperatureNode => "Temperature",
        BiomeMoistureNode => "Moisture",
        BiomeSelectNode => "BiomeSelect",
        BiomeLayerNode => "BiomeLayer",
        BiomeBlendNode => "Blend",
        BiomeMathNode => "Math",
        BiomeHeightNode => "Height",
        BiomeCaveNode => "Cave",
        BiomeAltitudeNode => "Altitude",
        BiomeSlopeNode => "Slope",
        BiomeErosionNode => "Erosion",
        BiomeMaskNode => "Mask",
        BiomeRiverNode => "River",
        BiomeWaterBodyNode => "WaterBody",
        BiomeWaterPathNode => "WaterPath",
        BiomeShoreNode => "Shore",
        BiomeWaterMergeNode => "WaterMerge",
        BiomeContinentNode => "Continent",
        BiomeCraterNode => "Crater",
        BiomeVolcanoNode => "Volcano",
        BiomeCliffNode => "Cliff",
        BiomeDomainWarpNode => "DomainWarp",
        BiomeClimateNode => "Climate",
        BiomeRainShadowNode => "RainShadow",
        BiomeSeasonNode => "Season",
        BiomeLatitudeBandNode => "LatitudeBand",
        BiomeFloraLayerNode => "FloraLayer",
        BiomeScatterLayerNode => "ScatterLayer",
        BiomeFaunaLayerNode => "FaunaLayer",
        BiomeUnderwaterLifeNode => "UnderwaterLife",
        BiomeResourceVeinNode => "ResourceVein",
        BiomeAtmosphereNode => "Atmosphere",
        BiomeWeatherProfileNode => "WeatherProfile",
        BiomeCloudLayerNode => "CloudLayer",
        BiomeIceSheetNode => "IceSheet",
        BiomeWetlandNode => "Wetland",
        BiomeOutputNode => "Output",
        _ => "Unknown",
    };

    static BiomeNode? CreateNodeByType(string type) => type switch
    {
        "Coordinate" => new BiomeCoordinateNode(),
        "Noise" => new BiomeNoiseNode(),
        "Temperature" => new BiomeTemperatureNode(),
        "Moisture" => new BiomeMoistureNode(),
        "BiomeSelect" => new BiomeSelectNode(),
        "BiomeLayer" => new BiomeLayerNode(),
        "Blend" => new BiomeBlendNode(),
        "Math" => new BiomeMathNode(),
        "Height" => new BiomeHeightNode(),
        "Cave" => new BiomeCaveNode(),
        "Altitude" => new BiomeAltitudeNode(),
        "Slope" => new BiomeSlopeNode(),
        "Erosion" => new BiomeErosionNode(),
        "Mask" => new BiomeMaskNode(),
        "River" => new BiomeRiverNode(),
        "WaterBody" => new BiomeWaterBodyNode(),
        "WaterPath" => new BiomeWaterPathNode(),
        "Shore" => new BiomeShoreNode(),
        "WaterMerge" => new BiomeWaterMergeNode(),
        "Continent" => new BiomeContinentNode(),
        "Crater" => new BiomeCraterNode(),
        "Volcano" => new BiomeVolcanoNode(),
        "Cliff" => new BiomeCliffNode(),
        "DomainWarp" => new BiomeDomainWarpNode(),
        "Climate" => new BiomeClimateNode(),
        "RainShadow" => new BiomeRainShadowNode(),
        "Season" => new BiomeSeasonNode(),
        "LatitudeBand" => new BiomeLatitudeBandNode(),
        "FloraLayer" => new BiomeFloraLayerNode(),
        "ScatterLayer" => new BiomeScatterLayerNode(),
        "FaunaLayer" => new BiomeFaunaLayerNode(),
        "UnderwaterLife" => new BiomeUnderwaterLifeNode(),
        "ResourceVein" => new BiomeResourceVeinNode(),
        "Atmosphere" => new BiomeAtmosphereNode(),
        "WeatherProfile" => new BiomeWeatherProfileNode(),
        "CloudLayer" => new BiomeCloudLayerNode(),
        "IceSheet" => new BiomeIceSheetNode(),
        "Wetland" => new BiomeWetlandNode(),
        "Output" => new BiomeOutputNode(),
        _ => null,
    };

    static void SerializeNodeProps(BiomeNode node, JsonObject obj)
    {
        switch (node)
        {
            case BiomeNoiseNode n:
                obj["frequency"] = n.Frequency; obj["octaves"] = n.Octaves;
                obj["seed"] = n.Seed; obj["noiseMode"] = n.NoiseMode; break;
            case BiomeTemperatureNode n:
                obj["latWeight"] = n.LatitudeWeight; obj["noiseWeight"] = n.NoiseWeight; break;
            case BiomeMoistureNode n:
                obj["noiseScale"] = n.NoiseScale; break;
            case BiomeLayerNode n:
                obj["albedoPath"] = n.AlbedoPath; obj["normalPath"] = n.NormalPath;
                obj["tiling"] = n.Tiling; obj["roughness"] = n.Roughness; obj["metallic"] = n.Metallic;
                obj["colorR"] = n.BaseColorR; obj["colorG"] = n.BaseColorG; obj["colorB"] = n.BaseColorB;
                obj["biomeName"] = n.BiomeName;
                obj["underTexPath"] = n.UnderTexturePath; obj["underNormPath"] = n.UnderNormalPath;
                obj["underTiling"] = n.UnderTiling;
                obj["layerNoiseMode"] = n.NoiseMode; obj["layerNoiseOctaves"] = n.NoiseOctaves;
                obj["erosionStrength"] = n.ErosionStrength; obj["erosionFrequency"] = n.ErosionFrequency;
                obj["spawnWater"] = n.SpawnWater;
                if (n.SpawnWater)
                {
                    obj["waterShallowR"] = n.WaterShallowR; obj["waterShallowG"] = n.WaterShallowG; obj["waterShallowB"] = n.WaterShallowB;
                    obj["waterDeepR"] = n.WaterDeepR; obj["waterDeepG"] = n.WaterDeepG; obj["waterDeepB"] = n.WaterDeepB;
                }
                obj["vegetationDensity"] = n.VegetationDensity;
                obj["treeDensity"] = n.TreeDensity;
                obj["vegetationProfileId"] = n.VegetationProfileId;
                obj["vegetationPatchiness"] = n.VegetationPatchiness;
                obj["weatherProfileId"] = n.WeatherProfileId;
                obj["rainChance"] = n.RainChance;
                obj["snowChance"] = n.SnowChance;
                obj["stormChance"] = n.StormChance;
                obj["windBias"] = n.WindBias;
                obj["cloudCoverageBias"] = n.CloudCoverageBias;
                obj["fogDensityBias"] = n.FogDensityBias;
                obj["seasonalGrowthMultiplier"] = n.SeasonalGrowthMultiplier;
                obj["heightAmplitude"] = n.HeightAmplitude;
                obj["noiseFrequency"] = n.NoiseFrequency;
                obj["growthTempMin"] = n.GrowthTemperatureMin;
                obj["growthTempMax"] = n.GrowthTemperatureMax;
                obj["growthMoistMin"] = n.GrowthMoistureMin;
                obj["growthMoistMax"] = n.GrowthMoistureMax;
                break;
            case BiomeMathNode n:
                obj["operation"] = n.Operation.ToString(); break;
            case BiomeHeightNode n:
                obj["baseHeight"] = n.BaseHeight; obj["amplitude"] = n.Amplitude; break;
            case BiomeCaveNode n:
                obj["frequency"] = n.Frequency; obj["threshold"] = n.Threshold; break;
            case BiomeAltitudeNode n:
                obj["seaLevel"] = n.SeaLevel; obj["maxHeight"] = n.MaxHeight; break;
            case BiomeSlopeNode n:
                obj["slopeScale"] = n.SlopeScale; break;
            case BiomeErosionNode n:
                obj["strength"] = n.Strength; obj["frequency"] = n.Frequency; obj["octaves"] = n.Octaves; break;
            case BiomeMaskNode n:
                obj["blendMode"] = n.BlendMode.ToString(); break;
            case BiomeRiverNode n:
                obj["riverWidth"] = n.RiverWidth; obj["riverDepth"] = n.RiverDepth;
                obj["frequency"] = n.Frequency; obj["meander"] = n.Meander;
                obj["allowedBiomes"] = n.AllowedBiomes;
                obj["sandWidth"] = n.SandWidth;
                obj["sandBiome"] = n.SandBiomeName;
                obj["flowToOcean"] = n.FlowToOcean;
                break;
            case BiomeWaterBodyNode n:
                obj["kind"] = n.Kind;
                obj["fillFraction"] = n.FillFraction;
                obj["allowedBiomes"] = n.AllowedBiomes;
                obj["minBasinDepth"] = n.MinBasinDepth;
                obj["shallowR"] = n.ShallowR; obj["shallowG"] = n.ShallowG; obj["shallowB"] = n.ShallowB;
                obj["deepR"] = n.DeepR; obj["deepG"] = n.DeepG; obj["deepB"] = n.DeepB;
                obj["deepestR"] = n.DeepestR; obj["deepestG"] = n.DeepestG; obj["deepestB"] = n.DeepestB;
                obj["shoreBiome"] = n.ShoreBiomeName;
                obj["shoreWidth"] = n.ShoreWidth;
                break;
            case BiomeWaterPathNode n:
                obj["width"] = n.Width; obj["depth"] = n.Depth;
                obj["frequency"] = n.Frequency; obj["meander"] = n.Meander;
                obj["allowedBiomes"] = n.AllowedBiomes;
                obj["sandWidth"] = n.SandWidth;
                obj["sandBiome"] = n.SandBiomeName;
                obj["flowToOcean"] = n.FlowToOcean;
                break;
            case BiomeShoreNode n:
                obj["shoreBiome"] = n.ShoreBiomeName;
                obj["shoreWidth"] = n.ShoreWidth;
                obj["texturePath"] = n.TexturePath;
                obj["tiling"] = n.Tiling;
                break;
            case BiomeContinentNode n:
                obj["frequency"] = n.Frequency; obj["threshold"] = n.Threshold;
                obj["strength"] = n.Strength; obj["seed"] = n.Seed; break;
            case BiomeCraterNode n:
                obj["radius"] = n.Radius; obj["depth"] = n.Depth;
                obj["rimHeight"] = n.RimHeight; obj["density"] = n.Density; obj["seed"] = n.Seed; break;
            case BiomeVolcanoNode n:
                obj["radius"] = n.Radius; obj["height"] = n.Height;
                obj["calderaRadius"] = n.CalderaRadius; obj["lavaBiome"] = n.LavaBiomeName;
                obj["density"] = n.Density; obj["seed"] = n.Seed; break;
            case BiomeCliffNode n:
                obj["strength"] = n.Strength; obj["frequency"] = n.Frequency; obj["slopeBias"] = n.SlopeBias; break;
            case BiomeDomainWarpNode n:
                obj["strength"] = n.Strength; obj["frequency"] = n.Frequency;
                obj["octaves"] = n.Octaves; obj["seed"] = n.Seed; break;
            case BiomeClimateNode n:
                obj["latWeight"] = n.LatitudeWeight; obj["altitudeLapse"] = n.AltitudeLapse;
                obj["moistureWeight"] = n.MoistureWeight; obj["noiseWeight"] = n.NoiseWeight; break;
            case BiomeRainShadowNode n:
                obj["strength"] = n.Strength; obj["width"] = n.Width; obj["ridgeFrequency"] = n.RidgeFrequency; break;
            case BiomeSeasonNode n:
                obj["growthMul"] = n.GrowthMultiplier; obj["snowLine"] = n.SnowLineAltitude; obj["phase"] = n.SeasonPhase; break;
            case BiomeLatitudeBandNode n:
                obj["minLat"] = n.MinLatitude; obj["maxLat"] = n.MaxLatitude;
                obj["tempBias"] = n.TemperatureBias; obj["moistBias"] = n.MoistureBias; obj["bandName"] = n.BandName; break;
            case BiomeFloraLayerNode n:
                obj["profileId"] = n.ProfileId; obj["targetBiome"] = n.TargetBiome;
                obj["grassDensity"] = n.GrassDensity; obj["bushDensity"] = n.BushDensity; obj["treeDensity"] = n.TreeDensity;
                obj["patchiness"] = n.Patchiness;
                obj["minSlope"] = n.MinSlope; obj["maxSlope"] = n.MaxSlope;
                obj["minAltitude"] = n.MinAltitude; obj["maxAltitude"] = n.MaxAltitude;
                obj["growthTempMin"] = n.GrowthTemperatureMin; obj["growthTempMax"] = n.GrowthTemperatureMax;
                obj["growthMoistMin"] = n.GrowthMoistureMin; obj["growthMoistMax"] = n.GrowthMoistureMax; break;
            case BiomeScatterLayerNode n:
                obj["profileId"] = n.ProfileId; obj["targetBiome"] = n.TargetBiome;
                obj["rockDensity"] = n.RockDensity; obj["debrisDensity"] = n.DebrisDensity;
                obj["minSlope"] = n.MinSlope; obj["maxSlope"] = n.MaxSlope;
                obj["minAltitude"] = n.MinAltitude; obj["maxAltitude"] = n.MaxAltitude;
                obj["scatterType"] = n.ScatterType; break;
            case BiomeFaunaLayerNode n:
                obj["speciesId"] = n.SpeciesId; obj["targetBiome"] = n.TargetBiome;
                obj["herdSpacing"] = n.HerdSpacing; obj["density"] = n.Density;
                obj["diurnal"] = n.Diurnal; obj["biomeMask"] = n.BiomeMask; break;
            case BiomeUnderwaterLifeNode n:
                obj["profileId"] = n.ProfileId;
                obj["kelpDensity"] = n.KelpDensity; obj["coralDensity"] = n.CoralDensity; obj["fishDensity"] = n.FishDensity;
                obj["minDepth"] = n.MinDepth; obj["maxDepth"] = n.MaxDepth;
                obj["requireWaterPlanet"] = n.RequireWaterPlanet; break;
            case BiomeResourceVeinNode n:
                obj["resourceId"] = n.ResourceId; obj["density"] = n.Density;
                obj["frequency"] = n.Frequency; obj["caveOnlyBias"] = n.CaveOnlyBias; obj["seed"] = n.Seed; break;
            case BiomeAtmosphereNode n:
                obj["preset"] = n.Preset; obj["rayleigh"] = n.RayleighStrength; obj["mie"] = n.MieStrength;
                obj["dayLength"] = n.DayLengthMinutes; obj["atmHeight"] = n.AtmosphereHeight; break;
            case BiomeWeatherProfileNode n:
                obj["profileId"] = n.ProfileId;
                obj["rainChance"] = n.RainChance; obj["snowChance"] = n.SnowChance; obj["stormChance"] = n.StormChance;
                obj["windBias"] = n.WindBias; obj["cloudBias"] = n.CloudCoverageBias; obj["fogBias"] = n.FogDensityBias; break;
            case BiomeCloudLayerNode n:
                obj["coverage"] = n.Coverage; obj["density"] = n.Density;
                obj["baseHeight"] = n.BaseHeight; obj["topHeight"] = n.TopHeight; obj["cloudType"] = n.CloudType; break;
            case BiomeIceSheetNode n:
                obj["maxTemp"] = n.MaxTemperature; obj["thickness"] = n.Thickness;
                obj["coverage"] = n.Coverage; obj["targetWater"] = n.TargetWaterKind; break;
            case BiomeWetlandNode n:
                obj["floodDepth"] = n.FloodDepth; obj["reedDensity"] = n.ReedDensity;
                obj["moistureBoost"] = n.MoistureBoost; obj["targetBiome"] = n.TargetBiome; break;
        }
    }

    static void DeserializeNodeProps(BiomeNode node, JsonNode item)
    {
        switch (node)
        {
            case BiomeNoiseNode n:
                n.Frequency = item["frequency"]?.GetValue<float>() ?? n.Frequency;
                n.Octaves = item["octaves"]?.GetValue<int>() ?? n.Octaves;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed;
                n.NoiseMode = item["noiseMode"]?.GetValue<string>() ?? n.NoiseMode; break;
            case BiomeTemperatureNode n:
                n.LatitudeWeight = item["latWeight"]?.GetValue<float>() ?? n.LatitudeWeight;
                n.NoiseWeight = item["noiseWeight"]?.GetValue<float>() ?? n.NoiseWeight; break;
            case BiomeMoistureNode n:
                n.NoiseScale = item["noiseScale"]?.GetValue<float>() ?? n.NoiseScale; break;
            case BiomeLayerNode n:
                n.AlbedoPath = item["albedoPath"]?.GetValue<string>() ?? "";
                n.NormalPath = item["normalPath"]?.GetValue<string>() ?? "";
                n.Tiling = item["tiling"]?.GetValue<float>() ?? 10f;
                n.Roughness = item["roughness"]?.GetValue<float>() ?? 0.8f;
                n.Metallic = item["metallic"]?.GetValue<float>() ?? 0f;
                n.BaseColorR = item["colorR"]?.GetValue<float>() ?? 0.5f;
                n.BaseColorG = item["colorG"]?.GetValue<float>() ?? 0.5f;
                n.BaseColorB = item["colorB"]?.GetValue<float>() ?? 0.5f;
                n.BiomeName = item["biomeName"]?.GetValue<string>() ?? "";
                n.UnderTexturePath = item["underTexPath"]?.GetValue<string>() ?? "";
                n.UnderNormalPath = item["underNormPath"]?.GetValue<string>() ?? "";
                n.UnderTiling = item["underTiling"]?.GetValue<float>() ?? 10f;
                n.NoiseMode = item["layerNoiseMode"]?.GetValue<string>() ?? "FBM";
                n.NoiseOctaves = item["layerNoiseOctaves"]?.GetValue<int>() ?? 6;
                n.ErosionStrength = item["erosionStrength"]?.GetValue<float>() ?? 0f;
                n.ErosionFrequency = item["erosionFrequency"]?.GetValue<float>() ?? 0.01f;
                n.SpawnWater = item["spawnWater"]?.GetValue<bool>() ?? false;
                n.WaterShallowR = item["waterShallowR"]?.GetValue<float>() ?? 0.08f;
                n.WaterShallowG = item["waterShallowG"]?.GetValue<float>() ?? 0.30f;
                n.WaterShallowB = item["waterShallowB"]?.GetValue<float>() ?? 0.38f;
                n.WaterDeepR = item["waterDeepR"]?.GetValue<float>() ?? 0.02f;
                n.WaterDeepG = item["waterDeepG"]?.GetValue<float>() ?? 0.08f;
                n.WaterDeepB = item["waterDeepB"]?.GetValue<float>() ?? 0.22f;
                n.VegetationDensity = item["vegetationDensity"]?.GetValue<float>() ?? 0f;
                n.TreeDensity = item["treeDensity"]?.GetValue<float>() ?? 0f;
                n.VegetationProfileId = item["vegetationProfileId"]?.GetValue<string>() ?? "Default";
                n.VegetationPatchiness = item["vegetationPatchiness"]?.GetValue<float>() ?? 0.45f;
                n.WeatherProfileId = item["weatherProfileId"]?.GetValue<string>() ?? "Temperate";
                n.RainChance = item["rainChance"]?.GetValue<float>() ?? 0.15f;
                n.SnowChance = item["snowChance"]?.GetValue<float>() ?? 0.04f;
                n.StormChance = item["stormChance"]?.GetValue<float>() ?? 0.01f;
                n.WindBias = item["windBias"]?.GetValue<float>() ?? 1f;
                n.CloudCoverageBias = item["cloudCoverageBias"]?.GetValue<float>() ?? 1f;
                n.FogDensityBias = item["fogDensityBias"]?.GetValue<float>() ?? 1f;
                n.SeasonalGrowthMultiplier = item["seasonalGrowthMultiplier"]?.GetValue<float>() ?? 1f;
                n.HeightAmplitude = item["heightAmplitude"]?.GetValue<float>() ?? -1f;
                n.NoiseFrequency = item["noiseFrequency"]?.GetValue<float>() ?? -1f;
                n.GrowthTemperatureMin = item["growthTempMin"]?.GetValue<float>() ?? 0.2f;
                n.GrowthTemperatureMax = item["growthTempMax"]?.GetValue<float>() ?? 0.8f;
                n.GrowthMoistureMin = item["growthMoistMin"]?.GetValue<float>() ?? 0.2f;
                n.GrowthMoistureMax = item["growthMoistMax"]?.GetValue<float>() ?? 0.9f;
                break;
            case BiomeMathNode n:
                if (Enum.TryParse<BiomeMathOp>(item["operation"]?.GetValue<string>(), out var op))
                    n.Operation = op; break;
            case BiomeHeightNode n:
                n.BaseHeight = item["baseHeight"]?.GetValue<float>() ?? 0f;
                n.Amplitude = item["amplitude"]?.GetValue<float>() ?? 50f; break;
            case BiomeCaveNode n:
                n.Frequency = item["frequency"]?.GetValue<float>() ?? 0.02f;
                n.Threshold = item["threshold"]?.GetValue<float>() ?? 0.7f; break;
            case BiomeAltitudeNode n:
                n.SeaLevel = item["seaLevel"]?.GetValue<float>() ?? 0f;
                n.MaxHeight = item["maxHeight"]?.GetValue<float>() ?? 1f; break;
            case BiomeSlopeNode n:
                n.SlopeScale = item["slopeScale"]?.GetValue<float>() ?? 1f; break;
            case BiomeErosionNode n:
                n.Strength = item["strength"]?.GetValue<float>() ?? 0.5f;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? 0.02f;
                n.Octaves = item["octaves"]?.GetValue<int>() ?? 4; break;
            case BiomeMaskNode n:
                if (Enum.TryParse<BiomeMaskBlendMode>(item["blendMode"]?.GetValue<string>(), out var bm))
                    n.BlendMode = bm; break;
            case BiomeRiverNode n:
                n.RiverWidth = item["riverWidth"]?.GetValue<float>() ?? 0.02f;
                n.RiverDepth = item["riverDepth"]?.GetValue<float>() ?? 5f;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? 0.003f;
                n.Meander = item["meander"]?.GetValue<float>() ?? 0.5f;
                n.AllowedBiomes = item["allowedBiomes"]?.GetValue<string>() ?? "";
                n.SandWidth = item["sandWidth"]?.GetValue<float>() ?? 0.04f;
                n.SandBiomeName = item["sandBiome"]?.GetValue<string>() ?? "Beach";
                n.FlowToOcean = item["flowToOcean"]?.GetValue<bool>() ?? true;
                break;
            case BiomeWaterBodyNode n:
                n.Kind = item["kind"]?.GetValue<string>() ?? "Ocean";
                n.FillFraction = item["fillFraction"]?.GetValue<float>() ?? 0.55f;
                n.AllowedBiomes = item["allowedBiomes"]?.GetValue<string>() ?? "";
                n.MinBasinDepth = item["minBasinDepth"]?.GetValue<float>() ?? 8f;
                n.ShallowR = item["shallowR"]?.GetValue<float>() ?? 0.08f;
                n.ShallowG = item["shallowG"]?.GetValue<float>() ?? 0.30f;
                n.ShallowB = item["shallowB"]?.GetValue<float>() ?? 0.38f;
                n.DeepR = item["deepR"]?.GetValue<float>() ?? 0.02f;
                n.DeepG = item["deepG"]?.GetValue<float>() ?? 0.08f;
                n.DeepB = item["deepB"]?.GetValue<float>() ?? 0.22f;
                n.DeepestR = item["deepestR"]?.GetValue<float>() ?? 0.01f;
                n.DeepestG = item["deepestG"]?.GetValue<float>() ?? 0.04f;
                n.DeepestB = item["deepestB"]?.GetValue<float>() ?? 0.12f;
                n.ShoreBiomeName = item["shoreBiome"]?.GetValue<string>() ?? "Beach";
                n.ShoreWidth = item["shoreWidth"]?.GetValue<float>() ?? 0.08f;
                break;
            case BiomeWaterPathNode n:
                n.Width = item["width"]?.GetValue<float>() ?? 0.02f;
                n.Depth = item["depth"]?.GetValue<float>() ?? 5f;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? 0.003f;
                n.Meander = item["meander"]?.GetValue<float>() ?? 0.5f;
                n.AllowedBiomes = item["allowedBiomes"]?.GetValue<string>() ?? "";
                n.SandWidth = item["sandWidth"]?.GetValue<float>() ?? 0.04f;
                n.SandBiomeName = item["sandBiome"]?.GetValue<string>() ?? "Beach";
                n.FlowToOcean = item["flowToOcean"]?.GetValue<bool>() ?? true;
                break;
            case BiomeShoreNode n:
                n.ShoreBiomeName = item["shoreBiome"]?.GetValue<string>() ?? "Beach";
                n.ShoreWidth = item["shoreWidth"]?.GetValue<float>() ?? 0.08f;
                n.TexturePath = item["texturePath"]?.GetValue<string>() ?? "";
                n.Tiling = item["tiling"]?.GetValue<float>() ?? 28f;
                break;
            case BiomeContinentNode n:
                n.Frequency = item["frequency"]?.GetValue<float>() ?? n.Frequency;
                n.Threshold = item["threshold"]?.GetValue<float>() ?? n.Threshold;
                n.Strength = item["strength"]?.GetValue<float>() ?? n.Strength;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed; break;
            case BiomeCraterNode n:
                n.Radius = item["radius"]?.GetValue<float>() ?? n.Radius;
                n.Depth = item["depth"]?.GetValue<float>() ?? n.Depth;
                n.RimHeight = item["rimHeight"]?.GetValue<float>() ?? n.RimHeight;
                n.Density = item["density"]?.GetValue<float>() ?? n.Density;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed; break;
            case BiomeVolcanoNode n:
                n.Radius = item["radius"]?.GetValue<float>() ?? n.Radius;
                n.Height = item["height"]?.GetValue<float>() ?? n.Height;
                n.CalderaRadius = item["calderaRadius"]?.GetValue<float>() ?? n.CalderaRadius;
                n.LavaBiomeName = item["lavaBiome"]?.GetValue<string>() ?? n.LavaBiomeName;
                n.Density = item["density"]?.GetValue<float>() ?? n.Density;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed; break;
            case BiomeCliffNode n:
                n.Strength = item["strength"]?.GetValue<float>() ?? n.Strength;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? n.Frequency;
                n.SlopeBias = item["slopeBias"]?.GetValue<float>() ?? n.SlopeBias; break;
            case BiomeDomainWarpNode n:
                n.Strength = item["strength"]?.GetValue<float>() ?? n.Strength;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? n.Frequency;
                n.Octaves = item["octaves"]?.GetValue<int>() ?? n.Octaves;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed; break;
            case BiomeClimateNode n:
                n.LatitudeWeight = item["latWeight"]?.GetValue<float>() ?? n.LatitudeWeight;
                n.AltitudeLapse = item["altitudeLapse"]?.GetValue<float>() ?? n.AltitudeLapse;
                n.MoistureWeight = item["moistureWeight"]?.GetValue<float>() ?? n.MoistureWeight;
                n.NoiseWeight = item["noiseWeight"]?.GetValue<float>() ?? n.NoiseWeight; break;
            case BiomeRainShadowNode n:
                n.Strength = item["strength"]?.GetValue<float>() ?? n.Strength;
                n.Width = item["width"]?.GetValue<float>() ?? n.Width;
                n.RidgeFrequency = item["ridgeFrequency"]?.GetValue<float>() ?? n.RidgeFrequency; break;
            case BiomeSeasonNode n:
                n.GrowthMultiplier = item["growthMul"]?.GetValue<float>() ?? n.GrowthMultiplier;
                n.SnowLineAltitude = item["snowLine"]?.GetValue<float>() ?? n.SnowLineAltitude;
                n.SeasonPhase = item["phase"]?.GetValue<float>() ?? n.SeasonPhase; break;
            case BiomeLatitudeBandNode n:
                n.MinLatitude = item["minLat"]?.GetValue<float>() ?? n.MinLatitude;
                n.MaxLatitude = item["maxLat"]?.GetValue<float>() ?? n.MaxLatitude;
                n.TemperatureBias = item["tempBias"]?.GetValue<float>() ?? n.TemperatureBias;
                n.MoistureBias = item["moistBias"]?.GetValue<float>() ?? n.MoistureBias;
                n.BandName = item["bandName"]?.GetValue<string>() ?? n.BandName; break;
            case BiomeFloraLayerNode n:
                n.ProfileId = item["profileId"]?.GetValue<string>() ?? n.ProfileId;
                n.TargetBiome = item["targetBiome"]?.GetValue<string>() ?? "";
                n.GrassDensity = item["grassDensity"]?.GetValue<float>() ?? n.GrassDensity;
                n.BushDensity = item["bushDensity"]?.GetValue<float>() ?? n.BushDensity;
                n.TreeDensity = item["treeDensity"]?.GetValue<float>() ?? n.TreeDensity;
                n.Patchiness = item["patchiness"]?.GetValue<float>() ?? n.Patchiness;
                n.MinSlope = item["minSlope"]?.GetValue<float>() ?? n.MinSlope;
                n.MaxSlope = item["maxSlope"]?.GetValue<float>() ?? n.MaxSlope;
                n.MinAltitude = item["minAltitude"]?.GetValue<float>() ?? n.MinAltitude;
                n.MaxAltitude = item["maxAltitude"]?.GetValue<float>() ?? n.MaxAltitude;
                n.GrowthTemperatureMin = item["growthTempMin"]?.GetValue<float>() ?? n.GrowthTemperatureMin;
                n.GrowthTemperatureMax = item["growthTempMax"]?.GetValue<float>() ?? n.GrowthTemperatureMax;
                n.GrowthMoistureMin = item["growthMoistMin"]?.GetValue<float>() ?? n.GrowthMoistureMin;
                n.GrowthMoistureMax = item["growthMoistMax"]?.GetValue<float>() ?? n.GrowthMoistureMax; break;
            case BiomeScatterLayerNode n:
                n.ProfileId = item["profileId"]?.GetValue<string>() ?? n.ProfileId;
                n.TargetBiome = item["targetBiome"]?.GetValue<string>() ?? "";
                n.RockDensity = item["rockDensity"]?.GetValue<float>() ?? n.RockDensity;
                n.DebrisDensity = item["debrisDensity"]?.GetValue<float>() ?? n.DebrisDensity;
                n.MinSlope = item["minSlope"]?.GetValue<float>() ?? n.MinSlope;
                n.MaxSlope = item["maxSlope"]?.GetValue<float>() ?? n.MaxSlope;
                n.MinAltitude = item["minAltitude"]?.GetValue<float>() ?? n.MinAltitude;
                n.MaxAltitude = item["maxAltitude"]?.GetValue<float>() ?? n.MaxAltitude;
                n.ScatterType = item["scatterType"]?.GetValue<string>() ?? n.ScatterType; break;
            case BiomeFaunaLayerNode n:
                n.SpeciesId = item["speciesId"]?.GetValue<string>() ?? n.SpeciesId;
                n.TargetBiome = item["targetBiome"]?.GetValue<string>() ?? "";
                n.HerdSpacing = item["herdSpacing"]?.GetValue<float>() ?? n.HerdSpacing;
                n.Density = item["density"]?.GetValue<float>() ?? n.Density;
                n.Diurnal = item["diurnal"]?.GetValue<bool>() ?? n.Diurnal;
                n.BiomeMask = item["biomeMask"]?.GetValue<string>() ?? ""; break;
            case BiomeUnderwaterLifeNode n:
                n.ProfileId = item["profileId"]?.GetValue<string>() ?? n.ProfileId;
                n.KelpDensity = item["kelpDensity"]?.GetValue<float>() ?? n.KelpDensity;
                n.CoralDensity = item["coralDensity"]?.GetValue<float>() ?? n.CoralDensity;
                n.FishDensity = item["fishDensity"]?.GetValue<float>() ?? n.FishDensity;
                n.MinDepth = item["minDepth"]?.GetValue<float>() ?? n.MinDepth;
                n.MaxDepth = item["maxDepth"]?.GetValue<float>() ?? n.MaxDepth;
                n.RequireWaterPlanet = item["requireWaterPlanet"]?.GetValue<bool>() ?? n.RequireWaterPlanet; break;
            case BiomeResourceVeinNode n:
                n.ResourceId = item["resourceId"]?.GetValue<string>() ?? n.ResourceId;
                n.Density = item["density"]?.GetValue<float>() ?? n.Density;
                n.Frequency = item["frequency"]?.GetValue<float>() ?? n.Frequency;
                n.CaveOnlyBias = item["caveOnlyBias"]?.GetValue<float>() ?? n.CaveOnlyBias;
                n.Seed = item["seed"]?.GetValue<int>() ?? n.Seed; break;
            case BiomeAtmosphereNode n:
                n.Preset = item["preset"]?.GetValue<string>() ?? n.Preset;
                n.RayleighStrength = item["rayleigh"]?.GetValue<float>() ?? n.RayleighStrength;
                n.MieStrength = item["mie"]?.GetValue<float>() ?? n.MieStrength;
                n.DayLengthMinutes = item["dayLength"]?.GetValue<float>() ?? n.DayLengthMinutes;
                n.AtmosphereHeight = item["atmHeight"]?.GetValue<float>() ?? n.AtmosphereHeight; break;
            case BiomeWeatherProfileNode n:
                n.ProfileId = item["profileId"]?.GetValue<string>() ?? n.ProfileId;
                n.RainChance = item["rainChance"]?.GetValue<float>() ?? n.RainChance;
                n.SnowChance = item["snowChance"]?.GetValue<float>() ?? n.SnowChance;
                n.StormChance = item["stormChance"]?.GetValue<float>() ?? n.StormChance;
                n.WindBias = item["windBias"]?.GetValue<float>() ?? n.WindBias;
                n.CloudCoverageBias = item["cloudBias"]?.GetValue<float>() ?? n.CloudCoverageBias;
                n.FogDensityBias = item["fogBias"]?.GetValue<float>() ?? n.FogDensityBias; break;
            case BiomeCloudLayerNode n:
                n.Coverage = item["coverage"]?.GetValue<float>() ?? n.Coverage;
                n.Density = item["density"]?.GetValue<float>() ?? n.Density;
                n.BaseHeight = item["baseHeight"]?.GetValue<float>() ?? n.BaseHeight;
                n.TopHeight = item["topHeight"]?.GetValue<float>() ?? n.TopHeight;
                n.CloudType = item["cloudType"]?.GetValue<string>() ?? n.CloudType; break;
            case BiomeIceSheetNode n:
                n.MaxTemperature = item["maxTemp"]?.GetValue<float>() ?? n.MaxTemperature;
                n.Thickness = item["thickness"]?.GetValue<float>() ?? n.Thickness;
                n.Coverage = item["coverage"]?.GetValue<float>() ?? n.Coverage;
                n.TargetWaterKind = item["targetWater"]?.GetValue<string>() ?? n.TargetWaterKind; break;
            case BiomeWetlandNode n:
                n.FloodDepth = item["floodDepth"]?.GetValue<float>() ?? n.FloodDepth;
                n.ReedDensity = item["reedDensity"]?.GetValue<float>() ?? n.ReedDensity;
                n.MoistureBoost = item["moistureBoost"]?.GetValue<float>() ?? n.MoistureBoost;
                n.TargetBiome = item["targetBiome"]?.GetValue<string>() ?? n.TargetBiome; break;
        }
    }
}
