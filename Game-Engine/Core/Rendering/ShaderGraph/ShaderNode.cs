#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Game_Engine.Core.Rendering.ShaderGraph
{
    /// <summary>Data type flowing through shader node connections.</summary>
    public enum ShaderDataType
    {
        Float,
        Vec2,
        Vec3,
        Vec4,
        Sampler2D,
        Mat3,
        Mat4,
        Bool
    }

    /// <summary>
    /// A port (input or output) on a shader node.
    /// </summary>
    public sealed class ShaderPort
    {
        public string Name { get; set; } = "";
        public ShaderDataType DataType { get; set; }
        public bool IsOutput { get; set; }
        public ShaderNode Owner { get; set; } = null!;

        /// <summary>Default value when not connected (for inputs).</summary>
        public float[] DefaultValue { get; set; } = { 0f };

        /// <summary>Connected port (null if unconnected).</summary>
        public ShaderPort? Connection { get; set; }

        /// <summary>GLSL type name for this port's data type.</summary>
        public string GLSLType => DataType switch
        {
            ShaderDataType.Float => "float",
            ShaderDataType.Vec2 => "vec2",
            ShaderDataType.Vec3 => "vec3",
            ShaderDataType.Vec4 => "vec4",
            ShaderDataType.Sampler2D => "sampler2D",
            ShaderDataType.Mat3 => "mat3",
            ShaderDataType.Mat4 => "mat4",
            ShaderDataType.Bool => "bool",
            _ => "float"
        };
    }

    /// <summary>
    /// Base class for a node in the visual shader graph.
    /// Each node type generates a GLSL code snippet.
    /// </summary>
    public abstract class ShaderNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Node";
        public float EditorX { get; set; }
        public float EditorY { get; set; }

        public List<ShaderPort> Inputs { get; } = new();
        public List<ShaderPort> Outputs { get; } = new();

        /// <summary>Add an input port.</summary>
        protected ShaderPort AddInput(string name, ShaderDataType type, params float[] defaultValue)
        {
            var port = new ShaderPort
            {
                Name = name, DataType = type, IsOutput = false, Owner = this,
                DefaultValue = defaultValue.Length > 0 ? defaultValue : new[] { 0f }
            };
            Inputs.Add(port);
            return port;
        }

        /// <summary>Add an output port.</summary>
        protected ShaderPort AddOutput(string name, ShaderDataType type)
        {
            var port = new ShaderPort { Name = name, DataType = type, IsOutput = true, Owner = this };
            Outputs.Add(port);
            return port;
        }

        /// <summary>
        /// Generate the GLSL code for this node.
        /// The result maps output variable names to their GLSL expressions.
        /// </summary>
        public abstract Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames);

        /// <summary>Get a unique variable prefix for this node.</summary>
        protected string VarPrefix => $"n{Id}_";
    }

    // ── Concrete node types ──

    /// <summary>Output node — the final fragment output (required, exactly one per graph).</summary>
    public sealed class OutputNode : ShaderNode
    {
        public OutputNode()
        {
            Name = "Output";
            AddInput("Albedo", ShaderDataType.Vec3, 1f, 1f, 1f);
            AddInput("Normal", ShaderDataType.Vec3, 0f, 0f, 1f);
            AddInput("Metallic", ShaderDataType.Float, 0f);
            AddInput("Roughness", ShaderDataType.Float, 0.5f);
            AddInput("Emission", ShaderDataType.Vec3, 0f, 0f, 0f);
            AddInput("Opacity", ShaderDataType.Float, 1f);
            AddInput("AO", ShaderDataType.Float, 1f);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            return new Dictionary<string, string>(); // Output node is handled specially
        }
    }

    /// <summary>Texture sample node — samples a 2D texture.</summary>
    public sealed class TextureSampleNode : ShaderNode
    {
        public string TexturePath { get; set; } = "";
        public int TextureSlot { get; set; } = 0;

        public TextureSampleNode()
        {
            Name = "Texture Sample";
            AddInput("UV", ShaderDataType.Vec2, 0f, 0f);
            AddOutput("RGB", ShaderDataType.Vec3);
            AddOutput("R", ShaderDataType.Float);
            AddOutput("G", ShaderDataType.Float);
            AddOutput("B", ShaderDataType.Float);
            AddOutput("A", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            string uv = inputVarNames.GetValueOrDefault(Inputs[0], "vTexCoord");
            string sampleVar = $"{VarPrefix}sample";
            string code = $"vec4 {sampleVar} = texture(uTexture{TextureSlot}, {uv});";

            return new Dictionary<string, string>
            {
                ["_code"] = code,
                [Outputs[0].Name] = $"{sampleVar}.rgb",
                [Outputs[1].Name] = $"{sampleVar}.r",
                [Outputs[2].Name] = $"{sampleVar}.g",
                [Outputs[3].Name] = $"{sampleVar}.b",
                [Outputs[4].Name] = $"{sampleVar}.a",
            };
        }
    }

    /// <summary>Color constant node — outputs a fixed color.</summary>
    public sealed class ColorNode : ShaderNode
    {
        public float R { get; set; } = 1f;
        public float G { get; set; } = 1f;
        public float B { get; set; } = 1f;
        public float A { get; set; } = 1f;

        public ColorNode()
        {
            Name = "Color";
            AddOutput("RGB", ShaderDataType.Vec3);
            AddOutput("RGBA", ShaderDataType.Vec4);
            AddOutput("R", ShaderDataType.Float);
            AddOutput("G", ShaderDataType.Float);
            AddOutput("B", ShaderDataType.Float);
            AddOutput("A", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            return new Dictionary<string, string>
            {
                [Outputs[0].Name] = $"vec3({R:F4}, {G:F4}, {B:F4})",
                [Outputs[1].Name] = $"vec4({R:F4}, {G:F4}, {B:F4}, {A:F4})",
                [Outputs[2].Name] = $"{R:F4}",
                [Outputs[3].Name] = $"{G:F4}",
                [Outputs[4].Name] = $"{B:F4}",
                [Outputs[5].Name] = $"{A:F4}",
            };
        }
    }

    /// <summary>Float constant node.</summary>
    public sealed class FloatNode : ShaderNode
    {
        public float Value { get; set; } = 0f;

        public FloatNode()
        {
            Name = "Float";
            AddOutput("Value", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            return new Dictionary<string, string> { [Outputs[0].Name] = $"{Value:F4}" };
        }
    }

    /// <summary>Math operation node — add, subtract, multiply, divide, etc.</summary>
    public sealed class MathNode : ShaderNode
    {
        public enum MathOp { Add, Subtract, Multiply, Divide, Power, Min, Max, Lerp, Dot, Cross }
        public MathOp Operation { get; set; } = MathOp.Multiply;

        public MathNode()
        {
            Name = "Math";
            AddInput("A", ShaderDataType.Float, 0f);
            AddInput("B", ShaderDataType.Float, 1f);
            AddOutput("Result", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            string a = inputVarNames.GetValueOrDefault(Inputs[0], "0.0");
            string b = inputVarNames.GetValueOrDefault(Inputs[1], "1.0");

            string expr = Operation switch
            {
                MathOp.Add => $"({a} + {b})",
                MathOp.Subtract => $"({a} - {b})",
                MathOp.Multiply => $"({a} * {b})",
                MathOp.Divide => $"({a} / max({b}, 0.0001))",
                MathOp.Power => $"pow({a}, {b})",
                MathOp.Min => $"min({a}, {b})",
                MathOp.Max => $"max({a}, {b})",
                MathOp.Lerp => $"mix({a}, {b}, 0.5)",
                MathOp.Dot => $"dot({a}, {b})",
                MathOp.Cross => $"cross({a}, {b})",
                _ => a
            };

            return new Dictionary<string, string> { [Outputs[0].Name] = expr };
        }
    }

    /// <summary>UV/coordinate node — provides texture coordinates and world position.</summary>
    public sealed class CoordinateNode : ShaderNode
    {
        public enum CoordType { UV, WorldPosition, WorldNormal, ViewDirection, Time }
        public CoordType Source { get; set; } = CoordType.UV;

        public CoordinateNode()
        {
            Name = "Coordinates";
            AddOutput("XY", ShaderDataType.Vec2);
            AddOutput("XYZ", ShaderDataType.Vec3);
            AddOutput("X", ShaderDataType.Float);
            AddOutput("Y", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            string src = Source switch
            {
                CoordType.UV => "vTexCoord",
                CoordType.WorldPosition => "vWorldPos",
                CoordType.WorldNormal => "vNormal",
                CoordType.ViewDirection => "normalize(uCameraPos - vWorldPos)",
                CoordType.Time => "vec3(uTime, uTime, uTime)",
                _ => "vTexCoord"
            };

            return new Dictionary<string, string>
            {
                [Outputs[0].Name] = $"{src}.xy",
                [Outputs[1].Name] = src,
                [Outputs[2].Name] = $"{src}.x",
                [Outputs[3].Name] = $"{src}.y",
            };
        }
    }

    /// <summary>Fresnel node — view-angle based effect for rim lighting, etc.</summary>
    public sealed class FresnelNode : ShaderNode
    {
        public FresnelNode()
        {
            Name = "Fresnel";
            AddInput("Power", ShaderDataType.Float, 5f);
            AddOutput("Result", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            string power = inputVarNames.GetValueOrDefault(Inputs[0], "5.0");
            return new Dictionary<string, string>
            {
                [Outputs[0].Name] = $"pow(1.0 - max(dot(normalize(vNormal), normalize(uCameraPos - vWorldPos)), 0.0), {power})"
            };
        }
    }

    /// <summary>Noise node — generates procedural noise (Perlin/Simplex).</summary>
    public sealed class NoiseNode : ShaderNode
    {
        public NoiseNode()
        {
            Name = "Noise";
            AddInput("UV", ShaderDataType.Vec2, 0f, 0f);
            AddInput("Scale", ShaderDataType.Float, 10f);
            AddOutput("Value", ShaderDataType.Float);
        }

        public override Dictionary<string, string> GenerateCode(Dictionary<ShaderPort, string> inputVarNames)
        {
            string uv = inputVarNames.GetValueOrDefault(Inputs[0], "vTexCoord");
            string scale = inputVarNames.GetValueOrDefault(Inputs[1], "10.0");

            // Simple hash-based noise approximation (works in GLSL)
            string noiseFunc = $"fract(sin(dot({uv} * {scale}, vec2(12.9898, 78.233))) * 43758.5453)";

            return new Dictionary<string, string>
            {
                [Outputs[0].Name] = noiseFunc
            };
        }
    }
}
