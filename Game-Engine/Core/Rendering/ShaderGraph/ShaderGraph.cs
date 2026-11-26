#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game_Engine.Core.Rendering.GPU;

namespace Game_Engine.Core.Rendering.ShaderGraph
{
    /// <summary>
    /// A visual shader graph — a collection of connected shader nodes
    /// that compiles to a unified <c>Surface</c> <c>.shader</c> asset.
    /// </summary>
    public sealed class ShaderGraph
    {
        public string Name { get; set; } = "Custom Shader";
        public List<ShaderNode> Nodes { get; } = new();
        public List<ShaderConnection> Connections { get; } = new();

        /// <summary>The master output node (always exists, exactly one).</summary>
        public OutputNode Output { get; private set; }

        public ShaderGraph()
        {
            Output = new OutputNode { EditorX = 600, EditorY = 300 };
            Nodes.Add(Output);
        }

        /// <summary>Add a node to the graph.</summary>
        public T AddNode<T>(float x = 0, float y = 0) where T : ShaderNode, new()
        {
            var node = new T { EditorX = x, EditorY = y };
            Nodes.Add(node);
            return node;
        }

        /// <summary>Instantiate a node by its serialized type identifier.</summary>
        public static ShaderNode CreateNodeFromType(string type) => type switch
        {
            "Output" => new OutputNode(),
            "TextureSample" => new TextureSampleNode(),
            "Color" => new ColorNode(),
            "Float" => new FloatNode(),
            "Math" => new MathNode(),
            "Coordinates" => new CoordinateNode(),
            "Fresnel" => new FresnelNode(),
            "Noise" => new NoiseNode(),
            "Property" => new PropertyNode(),
            "Split" => new SplitNode(),
            "Combine" => new CombineNode(),
            "UVTransform" => new UVTransformNode(),
            "Time" => new TimeNode(),
            "Blend" => new BlendNode(),
            _ => throw new InvalidOperationException($"Unknown node type: {type}")
        };

        /// <summary>
        /// Connect an output port to an input port.
        /// Output ports support fan-out (multiple targets); input ports accept a single incoming wire.
        /// </summary>
        public bool Connect(ShaderPort from, ShaderPort to)
        {
            if (!from.IsOutput || to.IsOutput) return false;
            if (from.Owner == to.Owner) return false;

            // Disconnect existing incoming connection on the target input only
            Disconnect(to);

            // Prevent duplicate exact wire
            if (Connections.Any(c => c.From == from && c.To == to))
                return true;

            to.Connection = from;
            from.Connection = to;
            Connections.Add(new ShaderConnection { From = from, To = to });
            return true;
        }

        /// <summary>
        /// Disconnect a port. For an input port, removes its incoming wire.
        /// For an output port, removes all outgoing fan-out wires.
        /// </summary>
        public void Disconnect(ShaderPort port)
        {
            if (port.IsOutput)
            {
                DisconnectOutput(port);
                return;
            }

            var prevFrom = port.Connection;
            port.Connection = null;
            Connections.RemoveAll(c => c.To == port);

            // Keep prevFrom.Connection synced with any remaining fan-out wire
            if (prevFrom != null)
            {
                prevFrom.Connection = Connections.FirstOrDefault(c => c.From == prevFrom)?.To;
            }
        }

        /// <summary>Disconnect all outgoing wires from an output port.</summary>
        public void DisconnectOutput(ShaderPort outputPort)
        {
            if (!outputPort.IsOutput) return;

            var outgoing = Connections.Where(c => c.From == outputPort).ToList();
            foreach (var conn in outgoing)
            {
                conn.To.Connection = null;
            }
            Connections.RemoveAll(c => c.From == outputPort);
            outputPort.Connection = null;
        }

        /// <summary>Returns true if the given port has at least one active connection.</summary>
        public bool IsPortConnected(ShaderPort port)
        {
            return port.IsOutput
                ? Connections.Any(c => c.From == port)
                : port.Connection != null;
        }

        /// <summary>Remove a node and all its incoming/outgoing connections.</summary>
        public void RemoveNode(ShaderNode node)
        {
            if (node is OutputNode) return; // Can't remove output
            foreach (var port in node.Inputs)
                Disconnect(port);
            foreach (var port in node.Outputs)
                DisconnectOutput(port);
            Nodes.Remove(node);
        }

        /// <summary>
        /// Compile the shader graph into a unified <c>Surface</c> <c>.shader</c> source string.
        /// </summary>
        public ShaderGraphCompileResult CompileToSurfaceShader()
            => ShaderGraphCompiler.Compile(this);

        /// <summary>
        /// Compile the shader graph into Forward GLSL vertex and fragment shader sources
        /// (backward-compatible API backed by the unified Surface codegen + <see cref="ShaderFileParser"/>).
        /// </summary>
        public (string vertexSource, string fragmentSource) Compile()
        {
            var result = ShaderGraphCompiler.Compile(this);
            if (!result.Success)
                throw new InvalidOperationException(result.ErrorMessage ?? "Shader graph compilation failed.");

            var parsed = ShaderFileParser.Parse(result.ShaderSource, Name);
            var (vert, _, frag, _) = ShaderFileParser.GeneratePassSources(parsed, ShaderPassType.Forward);
            return (vert, frag);
        }

        // ── Serialization ─────────────────────────────────────────────────────

        /// <summary>Save the shader graph to a JSON file.</summary>
        public void SaveToFile(string path)
        {
            var root = new JsonObject
            {
                ["name"] = Name,
                ["nodes"] = SerializeNodes(),
                ["connections"] = SerializeConnections()
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(opts));
        }

        /// <summary>Load a shader graph from a JSON file.</summary>
        public static ShaderGraph LoadFromFile(string path)
        {
            var json = File.ReadAllText(path);
            return LoadFromJson(json);
        }

        /// <summary>Load a shader graph from a JSON string.</summary>
        public static ShaderGraph LoadFromJson(string json)
        {
            var root = JsonNode.Parse(json)!.AsObject();

            var graph = new ShaderGraph();
            graph.Nodes.Clear(); // Remove default output node

            graph.Name = root["name"]?.GetValue<string>() ?? "Custom Shader";

            var nodeMap = new Dictionary<string, ShaderNode>();
            OutputNode? outputNode = null;

            if (root["nodes"] is JsonArray nodesArr)
            {
                foreach (var nodeJson in nodesArr)
                {
                    var obj = nodeJson!.AsObject();
                    string type = obj["type"]!.GetValue<string>();
                    string id = obj["id"]!.GetValue<string>();
                    float x = obj["x"]?.GetValue<float>() ?? 0f;
                    float y = obj["y"]?.GetValue<float>() ?? 0f;

                    ShaderNode node = CreateNodeFromType(type);
                    node.Id = id;
                    node.EditorX = x;
                    node.EditorY = y;

                    if (obj["props"] is JsonObject props)
                    {
                        DeserializeNodeProps(node, props);
                    }

                    if (node is OutputNode o) outputNode = o;
                    nodeMap[id] = node;
                    graph.Nodes.Add(node);
                }
            }

            if (outputNode == null)
            {
                outputNode = new OutputNode { EditorX = 600, EditorY = 300 };
                graph.Nodes.Add(outputNode);
            }

            graph.Output = outputNode;

            if (root["connections"] is JsonArray connsArr)
            {
                foreach (var connJson in connsArr)
                {
                    var cObj = connJson!.AsObject();
                    string fromNodeId = cObj["fromNode"]!.GetValue<string>();
                    int fromPort = cObj["fromPort"]!.GetValue<int>();
                    string toNodeId = cObj["toNode"]!.GetValue<string>();
                    int toPort = cObj["toPort"]!.GetValue<int>();

                    if (nodeMap.TryGetValue(fromNodeId, out var fromNode) &&
                        nodeMap.TryGetValue(toNodeId, out var toNode) &&
                        fromPort >= 0 && fromPort < fromNode.Outputs.Count &&
                        toPort >= 0 && toPort < toNode.Inputs.Count)
                    {
                        graph.Connect(fromNode.Outputs[fromPort], toNode.Inputs[toPort]);
                    }
                }
            }

            return graph;
        }

        private static void DeserializeNodeProps(ShaderNode node, JsonObject props)
        {
            switch (node)
            {
                case OutputNode o:
                    if (Enum.TryParse<ShaderRenderQueue>(props["Queue"]?.GetValue<string>(), true, out var q)) o.Queue = q;
                    if (Enum.TryParse<ShaderCullMode>(props["Cull"]?.GetValue<string>(), true, out var c)) o.Cull = c;
                    if (Enum.TryParse<ShaderLightingModel>(props["Lighting"]?.GetValue<string>(), true, out var l)) o.Lighting = l;
                    if (props["ZWrite"] != null) o.ZWrite = props["ZWrite"]!.GetValue<bool>();
                    if (props["AlphaClipThreshold"] != null) o.AlphaClipThreshold = props["AlphaClipThreshold"]!.GetValue<float>();
                    break;

                case ColorNode cn:
                    cn.R = props["R"]?.GetValue<float>() ?? 1f;
                    cn.G = props["G"]?.GetValue<float>() ?? 1f;
                    cn.B = props["B"]?.GetValue<float>() ?? 1f;
                    cn.A = props["A"]?.GetValue<float>() ?? 1f;
                    break;

                case FloatNode fn:
                    fn.Value = props["Value"]?.GetValue<float>() ?? 0f;
                    break;

                case TextureSampleNode tn:
                    tn.TextureSlot = props["Slot"]?.GetValue<int>() ?? 0;
                    tn.TexturePath = props["Path"]?.GetValue<string>() ?? "";
                    tn.IsNormalMap = props["IsNormalMap"]?.GetValue<bool>() ?? false;
                    tn.PropertyName = props["PropertyName"]?.GetValue<string>() ?? "";
                    tn.DisplayName = props["DisplayName"]?.GetValue<string>() ?? "";
                    tn.DefaultTexture = props["DefaultTexture"]?.GetValue<string>() ?? "white";
                    break;

                case MathNode mn:
                    mn.Operation = Enum.TryParse<MathNode.MathOp>(
                        props["Op"]?.GetValue<string>(), true, out var op) ? op : MathNode.MathOp.Multiply;
                    break;

                case CoordinateNode coord:
                    coord.Source = Enum.TryParse<CoordinateNode.CoordType>(
                        props["Source"]?.GetValue<string>(), true, out var src) ? src : CoordinateNode.CoordType.UV;
                    break;

                case NoiseNode nn:
                    if (Enum.TryParse<NoiseNode.NoiseType>(props["NoiseType"]?.GetValue<string>(), true, out var nt))
                        nn.Type = nt;
                    if (props["Octaves"] != null)
                        nn.Octaves = Math.Clamp(props["Octaves"]!.GetValue<int>(), 1, 8);
                    break;

                case PropertyNode pn:
                    pn.PropertyName = props["PropertyName"]?.GetValue<string>() ?? "_CustomProp";
                    pn.DisplayName = props["DisplayName"]?.GetValue<string>() ?? "Custom Property";
                    if (Enum.TryParse<PropertyNode.PropertyKind>(props["Kind"]?.GetValue<string>(), true, out var pk))
                        pn.Kind = pk;
                    pn.RangeMin = props["RangeMin"]?.GetValue<float>() ?? 0f;
                    pn.RangeMax = props["RangeMax"]?.GetValue<float>() ?? 1f;
                    pn.DefaultFloat = props["DefaultFloat"]?.GetValue<float>() ?? 0.5f;
                    pn.DefaultX = props["DefaultX"]?.GetValue<float>() ?? 1f;
                    pn.DefaultY = props["DefaultY"]?.GetValue<float>() ?? 1f;
                    pn.DefaultZ = props["DefaultZ"]?.GetValue<float>() ?? 1f;
                    pn.DefaultW = props["DefaultW"]?.GetValue<float>() ?? 1f;
                    pn.DefaultTexture = props["DefaultTexture"]?.GetValue<string>() ?? "white";
                    pn.TexturePath = props["TexturePath"]?.GetValue<string>() ?? "";
                    break;

                case UVTransformNode uvt:
                    uvt.TilingX = props["TilingX"]?.GetValue<float>() ?? 1f;
                    uvt.TilingY = props["TilingY"]?.GetValue<float>() ?? 1f;
                    uvt.OffsetX = props["OffsetX"]?.GetValue<float>() ?? 0f;
                    uvt.OffsetY = props["OffsetY"]?.GetValue<float>() ?? 0f;
                    uvt.ScrollX = props["ScrollX"]?.GetValue<float>() ?? 0f;
                    uvt.ScrollY = props["ScrollY"]?.GetValue<float>() ?? 0f;
                    uvt.Rotation = props["Rotation"]?.GetValue<float>() ?? 0f;
                    break;

                case BlendNode bn:
                    if (Enum.TryParse<BlendNode.BlendMode>(props["Mode"]?.GetValue<string>(), true, out var bm))
                        bn.Mode = bm;
                    break;
            }
        }

        private JsonArray SerializeNodes()
        {
            var arr = new JsonArray();
            foreach (var node in Nodes)
            {
                var obj = new JsonObject
                {
                    ["id"] = node.Id,
                    ["type"] = GetNodeTypeName(node),
                    ["x"] = node.EditorX,
                    ["y"] = node.EditorY
                };

                var props = new JsonObject();
                switch (node)
                {
                    case OutputNode o:
                        props["Queue"] = o.Queue.ToString();
                        props["Cull"] = o.Cull.ToString();
                        props["Lighting"] = o.Lighting.ToString();
                        props["ZWrite"] = o.ZWrite;
                        props["AlphaClipThreshold"] = o.AlphaClipThreshold;
                        break;

                    case ColorNode cn:
                        props["R"] = cn.R; props["G"] = cn.G;
                        props["B"] = cn.B; props["A"] = cn.A;
                        break;

                    case FloatNode fn:
                        props["Value"] = fn.Value;
                        break;

                    case TextureSampleNode tn:
                        props["Slot"] = tn.TextureSlot;
                        props["Path"] = tn.TexturePath;
                        if (tn.IsNormalMap) props["IsNormalMap"] = true;
                        if (!string.IsNullOrWhiteSpace(tn.PropertyName)) props["PropertyName"] = tn.PropertyName;
                        if (!string.IsNullOrWhiteSpace(tn.DisplayName)) props["DisplayName"] = tn.DisplayName;
                        if (!string.Equals(tn.DefaultTexture, "white", StringComparison.OrdinalIgnoreCase))
                            props["DefaultTexture"] = tn.DefaultTexture;
                        break;

                    case MathNode mn:
                        props["Op"] = mn.Operation.ToString();
                        break;

                    case CoordinateNode coord:
                        props["Source"] = coord.Source.ToString();
                        break;

                    case NoiseNode nn:
                        props["NoiseType"] = nn.Type.ToString();
                        props["Octaves"] = nn.Octaves;
                        break;

                    case PropertyNode pn:
                        props["PropertyName"] = pn.PropertyName;
                        props["DisplayName"] = pn.DisplayName;
                        props["Kind"] = pn.Kind.ToString();
                        props["RangeMin"] = pn.RangeMin;
                        props["RangeMax"] = pn.RangeMax;
                        props["DefaultFloat"] = pn.DefaultFloat;
                        props["DefaultX"] = pn.DefaultX;
                        props["DefaultY"] = pn.DefaultY;
                        props["DefaultZ"] = pn.DefaultZ;
                        props["DefaultW"] = pn.DefaultW;
                        props["DefaultTexture"] = pn.DefaultTexture;
                        if (!string.IsNullOrWhiteSpace(pn.TexturePath)) props["TexturePath"] = pn.TexturePath;
                        break;

                    case UVTransformNode uvt:
                        props["TilingX"] = uvt.TilingX;
                        props["TilingY"] = uvt.TilingY;
                        props["OffsetX"] = uvt.OffsetX;
                        props["OffsetY"] = uvt.OffsetY;
                        props["ScrollX"] = uvt.ScrollX;
                        props["ScrollY"] = uvt.ScrollY;
                        props["Rotation"] = uvt.Rotation;
                        break;

                    case BlendNode bn:
                        props["Mode"] = bn.Mode.ToString();
                        break;
                }

                if (props.Count > 0) obj["props"] = props;
                arr.Add(obj);
            }
            return arr;
        }

        private JsonArray SerializeConnections()
        {
            var arr = new JsonArray();
            foreach (var conn in Connections)
            {
                var fromNode = conn.From.Owner;
                var toNode = conn.To.Owner;
                int fromIdx = fromNode.Outputs.IndexOf(conn.From);
                int toIdx = toNode.Inputs.IndexOf(conn.To);
                if (fromIdx < 0 || toIdx < 0) continue;

                arr.Add(new JsonObject
                {
                    ["fromNode"] = fromNode.Id,
                    ["fromPort"] = fromIdx,
                    ["toNode"] = toNode.Id,
                    ["toPort"] = toIdx
                });
            }
            return arr;
        }

        public static string GetNodeTypeName(ShaderNode node) => node switch
        {
            OutputNode => "Output",
            TextureSampleNode => "TextureSample",
            ColorNode => "Color",
            FloatNode => "Float",
            MathNode => "Math",
            CoordinateNode => "Coordinates",
            FresnelNode => "Fresnel",
            NoiseNode => "Noise",
            PropertyNode => "Property",
            SplitNode => "Split",
            CombineNode => "Combine",
            UVTransformNode => "UVTransform",
            TimeNode => "Time",
            BlendNode => "Blend",
            _ => node.GetType().Name
        };
    }

    /// <summary>A connection between two shader ports.</summary>
    public sealed class ShaderConnection
    {
        public ShaderPort From { get; set; } = null!;
        public ShaderPort To { get; set; } = null!;
    }

    /// <summary>Result of compiling a <see cref="ShaderGraph"/> into a unified <c>Surface</c> <c>.shader</c> file.</summary>
    public sealed class ShaderGraphCompileResult
    {
        public bool Success { get; init; }
        public string ShaderSource { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Compiles a <see cref="ShaderGraph"/> into the unified Phase 1–4 <c>Surface</c> <c>.shader</c> format.
    /// Performs cycle detection, topological traversal, type auto-casting, helper emission, and property reflection.
    /// </summary>
    public sealed class ShaderGraphCompiler
    {
        private readonly ShaderGraph _graph;
        private readonly StringBuilder _surfBody = new();
        private readonly HashSet<string> _generatedNodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visitingNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<ShaderPort, string> _portVarNames = new();
        private readonly Dictionary<ShaderPort, ShaderDataType> _portEffectiveTypes = new();
        private readonly HashSet<ShaderPort> _tangentNormalPorts = new();
        private readonly HashSet<string> _requiredHelpers = new(StringComparer.Ordinal);
        private readonly List<string> _propertyDecls = new();
        private readonly HashSet<string> _declaredPropNames = new(StringComparer.Ordinal);
        private readonly List<string> _diagnostics = new();

        public ShaderGraphCompiler(ShaderGraph graph) => _graph = graph;

        /// <summary>Compile <paramref name="graph"/> into a unified <c>Surface</c> <c>.shader</c> asset.</summary>
        public static ShaderGraphCompileResult Compile(ShaderGraph graph)
        {
            var compiler = new ShaderGraphCompiler(graph);
            return compiler.CompileSurface();
        }

        /// <summary>Compile this graph into a unified <c>Surface</c> <c>.shader</c> asset.</summary>
        public ShaderGraphCompileResult CompileSurface()
        {
            try
            {
                // Generate code for all nodes feeding the master Output node in dependency order
                foreach (var input in _graph.Output.Inputs)
                {
                    if (input.Connection != null)
                        GenerateNodeCode(input.Connection.Owner);
                }
            }
            catch (InvalidOperationException ex)
            {
                _diagnostics.Add(ex.Message);
                return new ShaderGraphCompileResult
                {
                    Success = false,
                    ShaderSource = string.Empty,
                    ErrorMessage = ex.Message,
                    Diagnostics = _diagnostics
                };
            }

            var output = _graph.Output;
            string safeName = (string.IsNullOrWhiteSpace(_graph.Name) ? "Custom Shader" : _graph.Name)
                .Replace("\\", "\\\\").Replace("\"", "\\\"");

            var sb = new StringBuilder(1024);
            sb.AppendLine($"// Auto-generated by Visual Shader Editor");
            sb.AppendLine($"Shader \"{safeName}\"");
            sb.AppendLine("{");

            // 1. Properties block
            if (_propertyDecls.Count > 0)
            {
                sb.AppendLine("    Properties");
                sb.AppendLine("    {");
                foreach (var decl in _propertyDecls)
                    sb.AppendLine($"        {decl}");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // 2. RenderState block
            sb.AppendLine("    RenderState");
            sb.AppendLine("    {");
            sb.AppendLine($"        Queue {output.Queue}");
            sb.AppendLine($"        Cull {output.Cull}");
            sb.AppendLine($"        Lighting {output.Lighting}");
            sb.AppendLine($"        ZWrite {(output.ZWrite ? "On" : "Off")}");
            if (output.Queue == ShaderRenderQueue.AlphaTest)
                sb.AppendLine($"        AlphaClipThreshold {ShaderNode.F(Math.Clamp(output.AlphaClipThreshold, 0f, 1f))}");
            sb.AppendLine("    }");
            sb.AppendLine();

            // 3. Surface block
            sb.AppendLine("    Surface");
            sb.AppendLine("    {");

            EmitRequiredHelpers(sb);

            sb.AppendLine("        void surf(in SurfaceInput IN, inout SurfaceOutput OUT)");
            sb.AppendLine("        {");

            if (_surfBody.Length > 0)
                sb.Append(_surfBody);

            // Wire master Output inputs with automatic type casting
            var albedoPort = output.Inputs[0];
            var normalPort = output.Inputs[1];
            var metallicPort = output.Inputs[2];
            var roughnessPort = output.Inputs[3];
            var emissionPort = output.Inputs[4];
            var opacityPort = output.Inputs[5];
            var aoPort = output.Inputs[6];

            if (albedoPort.Connection != null)
                sb.AppendLine($"            OUT.Albedo = {GetCastInputExpression(albedoPort, ShaderDataType.Vec3, "vec3(1.0)")};");

            if (normalPort.Connection != null)
            {
                string normalExpr = GetCastInputExpression(normalPort, ShaderDataType.Vec3, "vec3(0.0, 0.0, 1.0)");
                bool isTangent = _tangentNormalPorts.Contains(normalPort.Connection);
                sb.AppendLine($"            OUT.Normal = {normalExpr};");
                if (!isTangent)
                    sb.AppendLine("            OUT.NormalIsWorldSpace = true;");
            }

            if (metallicPort.Connection != null)
                sb.AppendLine($"            OUT.Metallic = clamp({GetCastInputExpression(metallicPort, ShaderDataType.Float, "uMetallic")}, 0.0, 1.0);");

            if (roughnessPort.Connection != null)
                sb.AppendLine($"            OUT.Roughness = clamp({GetCastInputExpression(roughnessPort, ShaderDataType.Float, "uRoughness")}, 0.04, 1.0);");

            if (emissionPort.Connection != null)
                sb.AppendLine($"            OUT.Emission = {GetCastInputExpression(emissionPort, ShaderDataType.Vec3, "vec3(0.0)")};");

            if (opacityPort.Connection != null)
                sb.AppendLine($"            OUT.Alpha = clamp({GetCastInputExpression(opacityPort, ShaderDataType.Float, "1.0")}, 0.0, 1.0);");

            if (aoPort.Connection != null)
                sb.AppendLine($"            OUT.AO = clamp({GetCastInputExpression(aoPort, ShaderDataType.Float, "1.0")}, 0.0, 1.0);");

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return new ShaderGraphCompileResult
            {
                Success = true,
                ShaderSource = sb.ToString(),
                Diagnostics = _diagnostics
            };
        }

        private void GenerateNodeCode(ShaderNode node)
        {
            if (_generatedNodes.Contains(node.Id))
                return;

            if (!_visitingNodes.Add(node.Id))
                throw new InvalidOperationException($"Cycle detected in shader graph involving node '{node.Name}' ({node.Id}).");

            try
            {
                foreach (var input in node.Inputs)
                {
                    if (input.Connection != null)
                        GenerateNodeCode(input.Connection.Owner);
                }

                var ctx = new ShaderCodegenContext();
                foreach (var input in node.Inputs)
                {
                    if (input.Connection != null && _portVarNames.TryGetValue(input.Connection, out var varName))
                    {
                        ctx.InputExpressions[input] = varName;
                        ctx.InputTypes[input] = _portEffectiveTypes.TryGetValue(input.Connection, out var effType)
                            ? effType
                            : input.Connection.DataType;
                    }
                    else if (!string.IsNullOrEmpty(input.DefaultExpression))
                    {
                        ctx.InputExpressions[input] = input.DefaultExpression!;
                        ctx.InputTypes[input] = input.DataType;
                    }
                    else
                    {
                        ctx.InputExpressions[input] = DefaultValueToGLSL(input.DataType, input.DefaultValue);
                        ctx.InputTypes[input] = input.DataType;
                    }
                }

                var outputs = node.GenerateCode(ctx);

                // Merge context side-effects
                foreach (var h in ctx.RequiredHelpers)
                    _requiredHelpers.Add(h);
                for (int i = 0; i < ctx.PropertyDeclarations.Count; i++)
                {
                    // Deduplicate by exact property name already checked in ctx, plus across nodes
                    string decl = ctx.PropertyDeclarations[i];
                    int space = decl.IndexOf(' ');
                    string propName = space > 0 ? decl.Substring(0, space) : decl;
                    if (_declaredPropNames.Add(propName))
                        _propertyDecls.Add(decl);
                }
                foreach (var d in ctx.Diagnostics)
                    _diagnostics.Add(d);
                foreach (var tp in ctx.TangentNormalPorts)
                    _tangentNormalPorts.Add(tp);

                if (outputs.TryGetValue("_code", out var code) && !string.IsNullOrWhiteSpace(code))
                {
                    foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
                        _surfBody.AppendLine($"            {line.TrimStart()}");
                }

                foreach (var output in node.Outputs)
                {
                    if (outputs.TryGetValue(output.Name, out var expr))
                    {
                        _portVarNames[output] = expr;
                        _portEffectiveTypes[output] = ctx.OutputTypes.TryGetValue(output, out var overrideType)
                            ? overrideType
                            : output.DataType;
                    }
                }

                _generatedNodes.Add(node.Id);
            }
            finally
            {
                _visitingNodes.Remove(node.Id);
            }
        }

        private string GetCastInputExpression(ShaderPort input, ShaderDataType targetType, string fallback)
        {
            if (input.Connection != null && _portVarNames.TryGetValue(input.Connection, out var expr))
            {
                var srcType = _portEffectiveTypes.TryGetValue(input.Connection, out var effType)
                    ? effType
                    : input.Connection.DataType;
                return CastExpr(expr, srcType, targetType);
            }
            return fallback;
        }

        /// <summary>Promote two numeric/vector shader types to the higher dimension for GLSL operations.</summary>
        public static ShaderDataType PromoteNumericType(ShaderDataType a, ShaderDataType b)
        {
            int rankA = TypeRank(a);
            int rankB = TypeRank(b);
            return Math.Max(rankA, rankB) switch
            {
                4 => ShaderDataType.Vec4,
                3 => ShaderDataType.Vec3,
                2 => ShaderDataType.Vec2,
                _ => ShaderDataType.Float
            };

            static int TypeRank(ShaderDataType t) => t switch
            {
                ShaderDataType.Vec4 => 4,
                ShaderDataType.Vec3 => 3,
                ShaderDataType.Vec2 => 2,
                _ => 1
            };
        }

        /// <summary>
        /// Automatically cast a GLSL expression from <paramref name="from"/> to <paramref name="to"/>.
        /// </summary>
        public static string CastExpr(string expr, ShaderDataType from, ShaderDataType to)
        {
            if (from == to)
                return expr;

            return (from, to) switch
            {
                (ShaderDataType.Float, ShaderDataType.Vec2) => $"vec2({expr})",
                (ShaderDataType.Float, ShaderDataType.Vec3) => $"vec3({expr})",
                (ShaderDataType.Float, ShaderDataType.Vec4) => $"vec4(vec3({expr}), 1.0)",

                (ShaderDataType.Vec2, ShaderDataType.Float) => $"({expr}).x",
                (ShaderDataType.Vec2, ShaderDataType.Vec3) => $"vec3({expr}, 0.0)",
                (ShaderDataType.Vec2, ShaderDataType.Vec4) => $"vec4({expr}, 0.0, 1.0)",

                (ShaderDataType.Vec3, ShaderDataType.Float) => $"({expr}).x",
                (ShaderDataType.Vec3, ShaderDataType.Vec2) => $"({expr}).xy",
                (ShaderDataType.Vec3, ShaderDataType.Vec4) => $"vec4({expr}, 1.0)",

                (ShaderDataType.Vec4, ShaderDataType.Float) => $"({expr}).x",
                (ShaderDataType.Vec4, ShaderDataType.Vec2) => $"({expr}).xy",
                (ShaderDataType.Vec4, ShaderDataType.Vec3) => $"({expr}).rgb",

                _ => expr
            };
        }

        private static string DefaultValueToGLSL(ShaderDataType type, float[] values)
        {
            return type switch
            {
                ShaderDataType.Float => Val(values, 0),
                ShaderDataType.Vec2 => $"vec2({Val(values, 0)}, {Val(values, 1)})",
                ShaderDataType.Vec3 => $"vec3({Val(values, 0)}, {Val(values, 1)}, {Val(values, 2)})",
                ShaderDataType.Vec4 => $"vec4({Val(values, 0)}, {Val(values, 1)}, {Val(values, 2)}, {Val(values, 3)})",
                _ => "0.0"
            };

            static string Val(float[] v, int i) => ShaderNode.F(i < v.Length ? v[i] : 0f);
        }

        private void EmitRequiredHelpers(StringBuilder sb)
        {
            if (_requiredHelpers.Count == 0)
                return;

            if (_requiredHelpers.Contains("hash21"))
            {
                sb.AppendLine("        float _sg_hash21(vec2 p)");
                sb.AppendLine("        {");
                sb.AppendLine("            p = fract(p * vec2(123.34, 456.21));");
                sb.AppendLine("            p += dot(p, p + 45.32);");
                sb.AppendLine("            return fract(p.x * p.y);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (_requiredHelpers.Contains("hash22"))
            {
                sb.AppendLine("        vec2 _sg_hash22(vec2 p)");
                sb.AppendLine("        {");
                sb.AppendLine("            vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));");
                sb.AppendLine("            p3 += dot(p3, p3.yzx + 33.33);");
                sb.AppendLine("            return fract((p3.xx + p3.yz) * p3.zy);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (_requiredHelpers.Contains("valueNoise"))
            {
                sb.AppendLine("        float _sg_valueNoise(vec2 p)");
                sb.AppendLine("        {");
                sb.AppendLine("            vec2 i = floor(p);");
                sb.AppendLine("            vec2 f = fract(p);");
                sb.AppendLine("            vec2 u = f * f * (3.0 - 2.0 * f);");
                sb.AppendLine("            float a = _sg_hash21(i + vec2(0.0, 0.0));");
                sb.AppendLine("            float b = _sg_hash21(i + vec2(1.0, 0.0));");
                sb.AppendLine("            float c = _sg_hash21(i + vec2(0.0, 1.0));");
                sb.AppendLine("            float d = _sg_hash21(i + vec2(1.0, 1.0));");
                sb.AppendLine("            return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (_requiredHelpers.Contains("fbm"))
            {
                sb.AppendLine("        float _sg_fbm(vec2 p, int octaves)");
                sb.AppendLine("        {");
                sb.AppendLine("            float val = 0.0;");
                sb.AppendLine("            float amp = 0.5;");
                sb.AppendLine("            for (int i = 0; i < 8; ++i)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (i >= octaves) break;");
                sb.AppendLine("                val += amp * _sg_valueNoise(p);");
                sb.AppendLine("                p *= 2.03;");
                sb.AppendLine("                amp *= 0.5;");
                sb.AppendLine("            }");
                sb.AppendLine("            return val;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (_requiredHelpers.Contains("voronoi"))
            {
                sb.AppendLine("        float _sg_voronoi(vec2 p)");
                sb.AppendLine("        {");
                sb.AppendLine("            vec2 n = floor(p);");
                sb.AppendLine("            vec2 f = fract(p);");
                sb.AppendLine("            float minDist = 8.0;");
                sb.AppendLine("            for (int j = -1; j <= 1; ++j)");
                sb.AppendLine("            {");
                sb.AppendLine("                for (int i = -1; i <= 1; ++i)");
                sb.AppendLine("                {");
                sb.AppendLine("                    vec2 g = vec2(float(i), float(j));");
                sb.AppendLine("                    vec2 o = _sg_hash22(n + g);");
                sb.AppendLine("                    vec2 r = g + o - f;");
                sb.AppendLine("                    float d = dot(r, r);");
                sb.AppendLine("                    minDist = min(minDist, d);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("            return sqrt(minDist);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (_requiredHelpers.Contains("blendOverlay"))
            {
                sb.AppendLine("        vec3 _sg_blendOverlay(vec3 baseCol, vec3 blendCol)");
                sb.AppendLine("        {");
                sb.AppendLine("            vec3 lo = 2.0 * baseCol * blendCol;");
                sb.AppendLine("            vec3 hi = vec3(1.0) - 2.0 * (vec3(1.0) - baseCol) * (vec3(1.0) - blendCol);");
                sb.AppendLine("            return mix(lo, hi, step(vec3(0.5), baseCol));");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }
    }
}
