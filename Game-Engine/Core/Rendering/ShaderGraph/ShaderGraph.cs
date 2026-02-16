#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game_Engine.Core.Rendering.ShaderGraph
{
    /// <summary>
    /// A visual shader graph — a collection of connected shader nodes
    /// that compiles to GLSL vertex + fragment shader source code.
    /// </summary>
    public sealed class ShaderGraph
    {
        public string Name { get; set; } = "Custom Shader";
        public List<ShaderNode> Nodes { get; } = new();
        public List<ShaderConnection> Connections { get; } = new();

        /// <summary>The output node (always exists, exactly one).</summary>
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

        /// <summary>Connect an output port to an input port.</summary>
        public bool Connect(ShaderPort from, ShaderPort to)
        {
            if (!from.IsOutput || to.IsOutput) return false;
            if (from.Owner == to.Owner) return false;

            // Disconnect existing connection on the input
            Disconnect(to);

            from.Connection = to;
            to.Connection = from;
            Connections.Add(new ShaderConnection { From = from, To = to });
            return true;
        }

        /// <summary>Disconnect an input port.</summary>
        public void Disconnect(ShaderPort inputPort)
        {
            if (inputPort.Connection != null)
            {
                inputPort.Connection.Connection = null;
                inputPort.Connection = null;
            }
            Connections.RemoveAll(c => c.To == inputPort);
        }

        /// <summary>Remove a node and all its connections.</summary>
        public void RemoveNode(ShaderNode node)
        {
            if (node is OutputNode) return; // Can't remove output
            foreach (var port in node.Inputs.Concat(node.Outputs))
                Disconnect(port);
            Nodes.Remove(node);
        }

        /// <summary>
        /// Compile the shader graph to GLSL source code.
        /// Returns (vertexShader, fragmentShader).
        /// </summary>
        public (string vertexSource, string fragmentSource) Compile()
        {
            var compiler = new ShaderGraphCompiler(this);
            return compiler.Compile();
        }

        // ── Serialization ──

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
            var root = JsonNode.Parse(json)!.AsObject();

            var graph = new ShaderGraph();
            graph.Nodes.Clear(); // Remove default output node

            graph.Name = root["name"]?.GetValue<string>() ?? "Custom Shader";

            // Deserialize nodes
            var nodeMap = new Dictionary<string, ShaderNode>();
            OutputNode? outputNode = null;

            foreach (var nodeJson in root["nodes"]!.AsArray())
            {
                var obj = nodeJson!.AsObject();
                string type = obj["type"]!.GetValue<string>();
                string id = obj["id"]!.GetValue<string>();
                float x = obj["x"]!.GetValue<float>();
                float y = obj["y"]!.GetValue<float>();

                ShaderNode node = type switch
                {
                    "Output" => new OutputNode(),
                    "TextureSample" => new TextureSampleNode(),
                    "Color" => new ColorNode(),
                    "Float" => new FloatNode(),
                    "Math" => new MathNode(),
                    "Coordinates" => new CoordinateNode(),
                    "Fresnel" => new FresnelNode(),
                    "Noise" => new NoiseNode(),
                    _ => throw new InvalidOperationException($"Unknown node type: {type}")
                };

                node.Id = id;
                node.EditorX = x;
                node.EditorY = y;

                // Type-specific properties
                if (obj.ContainsKey("props"))
                {
                    var props = obj["props"]!.AsObject();
                    switch (node)
                    {
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
                            break;
                        case MathNode mn:
                            mn.Operation = Enum.TryParse<MathNode.MathOp>(
                                props["Op"]?.GetValue<string>(), out var op) ? op : MathNode.MathOp.Multiply;
                            break;
                        case CoordinateNode coord:
                            coord.Source = Enum.TryParse<CoordinateNode.CoordType>(
                                props["Source"]?.GetValue<string>(), out var src) ? src : CoordinateNode.CoordType.UV;
                            break;
                    }
                }

                if (node is OutputNode o) outputNode = o;
                nodeMap[id] = node;
                graph.Nodes.Add(node);
            }

            // Ensure we have an output node
            if (outputNode == null)
            {
                outputNode = new OutputNode { EditorX = 600, EditorY = 300 };
                graph.Nodes.Add(outputNode);
            }

            graph.Output = outputNode;

            // Deserialize connections
            if (root.ContainsKey("connections"))
            {
                foreach (var connJson in root["connections"]!.AsArray())
                {
                    var cObj = connJson!.AsObject();
                    string fromNodeId = cObj["fromNode"]!.GetValue<string>();
                    int fromPort = cObj["fromPort"]!.GetValue<int>();
                    string toNodeId = cObj["toNode"]!.GetValue<string>();
                    int toPort = cObj["toPort"]!.GetValue<int>();

                    if (nodeMap.TryGetValue(fromNodeId, out var fromNode) &&
                        nodeMap.TryGetValue(toNodeId, out var toNode) &&
                        fromPort < fromNode.Outputs.Count &&
                        toPort < toNode.Inputs.Count)
                    {
                        graph.Connect(fromNode.Outputs[fromPort], toNode.Inputs[toPort]);
                    }
                }
            }

            return graph;
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
                        break;
                    case MathNode mn:
                        props["Op"] = mn.Operation.ToString();
                        break;
                    case CoordinateNode coord:
                        props["Source"] = coord.Source.ToString();
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
                arr.Add(new JsonObject
                {
                    ["fromNode"] = fromNode.Id,
                    ["fromPort"] = fromNode.Outputs.IndexOf(conn.From),
                    ["toNode"] = toNode.Id,
                    ["toPort"] = toNode.Inputs.IndexOf(conn.To)
                });
            }
            return arr;
        }

        private static string GetNodeTypeName(ShaderNode node) => node switch
        {
            OutputNode => "Output",
            TextureSampleNode => "TextureSample",
            ColorNode => "Color",
            FloatNode => "Float",
            MathNode => "Math",
            CoordinateNode => "Coordinates",
            FresnelNode => "Fresnel",
            NoiseNode => "Noise",
            _ => node.GetType().Name
        };
    }

    /// <summary>A connection between two shader ports.</summary>
    public sealed class ShaderConnection
    {
        public ShaderPort From { get; set; } = null!;
        public ShaderPort To { get; set; } = null!;
    }

    /// <summary>
    /// Compiles a ShaderGraph into GLSL vertex and fragment shader source code.
    /// Performs topological sort of nodes and generates code in dependency order.
    /// </summary>
    internal sealed class ShaderGraphCompiler
    {
        private readonly ShaderGraph _graph;
        private readonly StringBuilder _fragBody = new();
        private readonly HashSet<string> _generatedNodes = new();
        private readonly Dictionary<ShaderPort, string> _portVarNames = new();

        public ShaderGraphCompiler(ShaderGraph graph) => _graph = graph;

        public (string vertex, string fragment) Compile()
        {
            // Generate code for all nodes connected to the output (in dependency order)
            foreach (var input in _graph.Output.Inputs)
            {
                if (input.Connection != null)
                    GenerateNodeCode(input.Connection.Owner);
            }

            // Build fragment shader
            var fragSb = new StringBuilder();
            fragSb.AppendLine("#version 330 core");
            fragSb.AppendLine();
            fragSb.AppendLine("// ── Inputs from vertex shader ──");
            fragSb.AppendLine("in vec3 vWorldPos;");
            fragSb.AppendLine("in vec3 vNormal;");
            fragSb.AppendLine("in vec2 vTexCoord;");
            fragSb.AppendLine("in vec4 vShadowCoord;");
            fragSb.AppendLine();
            fragSb.AppendLine("// ── Uniforms ──");
            fragSb.AppendLine("uniform vec3 uCameraPos;");
            fragSb.AppendLine("uniform float uTime;");
            fragSb.AppendLine("uniform sampler2D uTexture0;");
            fragSb.AppendLine("uniform sampler2D uTexture1;");
            fragSb.AppendLine("uniform sampler2D uTexture2;");
            fragSb.AppendLine("uniform sampler2D uTexture3;");
            fragSb.AppendLine();
            fragSb.AppendLine("// ── Light uniforms ──");
            fragSb.AppendLine("uniform vec3 uLightDir;");
            fragSb.AppendLine("uniform vec3 uLightColor;");
            fragSb.AppendLine("uniform float uLightIntensity;");
            fragSb.AppendLine();
            fragSb.AppendLine("out vec4 FragColor;");
            fragSb.AppendLine();
            fragSb.AppendLine("void main() {");

            // Insert generated node code
            fragSb.Append(_fragBody);

            // Wire output node inputs to final fragment color
            string albedo = GetInputExpression(_graph.Output.Inputs[0], "vec3(1.0)");
            string normal = GetInputExpression(_graph.Output.Inputs[1], "normalize(vNormal)");
            string metallic = GetInputExpression(_graph.Output.Inputs[2], "0.0");
            string roughness = GetInputExpression(_graph.Output.Inputs[3], "0.5");
            string emission = GetInputExpression(_graph.Output.Inputs[4], "vec3(0.0)");
            string opacity = GetInputExpression(_graph.Output.Inputs[5], "1.0");
            string ao = GetInputExpression(_graph.Output.Inputs[6], "1.0");

            // Simple PBR-like lighting
            fragSb.AppendLine();
            fragSb.AppendLine("    // ── PBR Lighting ──");
            fragSb.AppendLine($"    vec3 albedoVal = {albedo};");
            fragSb.AppendLine($"    vec3 N = normalize({normal});");
            fragSb.AppendLine($"    float metallicVal = {metallic};");
            fragSb.AppendLine($"    float roughnessVal = {roughness};");
            fragSb.AppendLine($"    vec3 emissionVal = {emission};");
            fragSb.AppendLine($"    float opacityVal = {opacity};");
            fragSb.AppendLine($"    float aoVal = {ao};");
            fragSb.AppendLine();
            fragSb.AppendLine("    // Diffuse lighting");
            fragSb.AppendLine("    float NdotL = max(dot(N, -uLightDir), 0.0);");
            fragSb.AppendLine("    vec3 diffuse = albedoVal * uLightColor * uLightIntensity * NdotL;");
            fragSb.AppendLine("    vec3 ambient = albedoVal * 0.15 * aoVal;");
            fragSb.AppendLine();
            fragSb.AppendLine("    // Specular (Blinn-Phong approximation)");
            fragSb.AppendLine("    vec3 viewDir = normalize(uCameraPos - vWorldPos);");
            fragSb.AppendLine("    vec3 halfDir = normalize(viewDir - uLightDir);");
            fragSb.AppendLine("    float spec = pow(max(dot(N, halfDir), 0.0), mix(8.0, 256.0, 1.0 - roughnessVal));");
            fragSb.AppendLine("    vec3 specular = uLightColor * spec * mix(vec3(0.04), albedoVal, metallicVal);");
            fragSb.AppendLine();
            fragSb.AppendLine("    vec3 finalColor = ambient + diffuse + specular + emissionVal;");
            fragSb.AppendLine("    FragColor = vec4(finalColor, opacityVal);");
            fragSb.AppendLine("}");

            // Standard vertex shader
            string vertex = GenerateVertexShader();

            return (vertex, fragSb.ToString());
        }

        private void GenerateNodeCode(ShaderNode node)
        {
            if (_generatedNodes.Contains(node.Id)) return;

            // First, generate code for all input dependencies
            foreach (var input in node.Inputs)
            {
                if (input.Connection != null)
                    GenerateNodeCode(input.Connection.Owner);
            }

            _generatedNodes.Add(node.Id);

            // Build input variable name map
            var inputVars = new Dictionary<ShaderPort, string>();
            foreach (var input in node.Inputs)
            {
                if (input.Connection != null && _portVarNames.TryGetValue(input.Connection, out var varName))
                    inputVars[input] = varName;
                else
                    inputVars[input] = DefaultValueToGLSL(input.DataType, input.DefaultValue);
            }

            // Generate this node's code
            var outputs = node.GenerateCode(inputVars);

            // If the node produced inline code, add it
            if (outputs.TryGetValue("_code", out var code))
            {
                _fragBody.AppendLine($"    {code}");
            }

            // Map output port names to their GLSL expressions
            foreach (var output in node.Outputs)
            {
                if (outputs.TryGetValue(output.Name, out var expr))
                    _portVarNames[output] = expr;
            }
        }

        private string GetInputExpression(ShaderPort input, string fallback)
        {
            if (input.Connection != null && _portVarNames.TryGetValue(input.Connection, out var expr))
                return expr;
            return fallback;
        }

        private static string DefaultValueToGLSL(ShaderDataType type, float[] values)
        {
            return type switch
            {
                ShaderDataType.Float => (values.Length > 0 ? values[0] : 0f).ToString("F4"),
                ShaderDataType.Vec2 => $"vec2({Val(values, 0)}, {Val(values, 1)})",
                ShaderDataType.Vec3 => $"vec3({Val(values, 0)}, {Val(values, 1)}, {Val(values, 2)})",
                ShaderDataType.Vec4 => $"vec4({Val(values, 0)}, {Val(values, 1)}, {Val(values, 2)}, {Val(values, 3)})",
                _ => "0.0"
            };

            static string Val(float[] v, int i) => (i < v.Length ? v[i] : 0f).ToString("F4");
        }

        private static string GenerateVertexShader()
        {
            return @"#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightSpaceMatrix;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vTexCoord;
out vec4 vShadowCoord;

void main() {
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vNormal = normalize(mat3(transpose(inverse(uModel))) * aNormal);
    vTexCoord = aTexCoord;
    vShadowCoord = uLightSpaceMatrix * worldPos;
    gl_Position = uProjection * uView * worldPos;
}
";
        }
    }
}
