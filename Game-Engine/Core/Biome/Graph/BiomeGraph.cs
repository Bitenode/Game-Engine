#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game_Engine.Core.Biome.Graph;

public sealed class BiomeConnection
{
    public BiomePort From { get; }
    public BiomePort To { get; }
    public BiomeConnection(BiomePort from, BiomePort to) { From = from; To = to; }
}

/// <summary>
/// Result of compiling a biome graph into generation parameters.
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

    public BiomeLayerInfo[] Layers { get; set; } = Array.Empty<BiomeLayerInfo>();

    public float RiverWidth { get; set; } = 0.02f;
    public float RiverDepth { get; set; } = 5f;
    public float RiverFrequency { get; set; } = 0.003f;
    public float RiverMeander { get; set; } = 0.5f;
    public string[] RiverAllowedBiomes { get; set; } = Array.Empty<string>();
    public bool HasRiver { get; set; } = false;
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

    public bool Connect(BiomePort from, BiomePort to)
    {
        if (!from.IsOutput || to.IsOutput) return false;
        if (from.Owner == to.Owner) return false;

        if (to.Connection != null) Disconnect(to);

        to.Connection = from;
        Connections.Add(new BiomeConnection(from, to));
        return true;
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
        for (int i = 2; i < output.Inputs.Count; i++)
        {
            if (output.Inputs[i].Connection?.Owner is BiomeLayerNode)
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

        return warnings;
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

    /// <summary>Compile the graph into generation parameters.</summary>
    public BiomeGraphResult Compile()
    {
        var result = new BiomeGraphResult();
        var output = OutputNode;
        if (output == null) return result;

        var heightPort = output.Inputs[0];
        if (heightPort.Connection?.Owner is BiomeHeightNode hn)
        {
            result.HeightAmplitude = hn.Amplitude;
            var noiseInput = hn.Inputs[0];
            if (noiseInput.Connection?.Owner is BiomeNoiseNode nn)
            {
                result.NoiseFrequency = nn.Frequency;
                result.NoiseOctaves = nn.Octaves;
                result.NoiseMode = nn.NoiseMode;
            }
        }
        else
        {
            result.HeightAmplitude = heightPort.DefaultValue[0];
        }

        var cavePort = output.Inputs[1];
        if (cavePort.Connection?.Owner is BiomeCaveNode cn)
        {
            result.EnableCaves = true;
            result.CaveFrequency = cn.Frequency;
            result.CaveThreshold = cn.Threshold;
        }
        else
        {
            result.EnableCaves = false;
        }

        foreach (var node in Nodes)
        {
            if (node is BiomeTemperatureNode tn)
            {
                result.TemperatureLatWeight = tn.LatitudeWeight;
                result.TemperatureNoiseWeight = tn.NoiseWeight;
            }
            if (node is BiomeMoistureNode mn)
            {
                result.MoistureNoiseScale = mn.NoiseScale;
            }
        }

        var layers = new List<BiomeLayerInfo>();
        for (int i = 2; i < output.Inputs.Count && i < 10; i++)
        {
            var layerPort = output.Inputs[i];
            if (layerPort.Connection?.Owner is BiomeLayerNode ln)
            {
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
                    ErosionStrength = ln.ErosionStrength,
                    ErosionFrequency = ln.ErosionFrequency,
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
                });
            }
        }
        result.Layers = layers.ToArray();

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
            }
        }

        return result;
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
                obj["allowedBiomes"] = n.AllowedBiomes; break;
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
                n.AllowedBiomes = item["allowedBiomes"]?.GetValue<string>() ?? ""; break;
        }
    }
}
