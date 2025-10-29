using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Game_Engine.Core.Rendering
{
    public enum ShaderPropType
    {
        Float, Range, Color, Vector2, Vector3, Vector4, Int, Bool, Texture2D
    }

    public sealed class ShaderPropertyDecl
    {
        public string Name;            // e.g. "_BaseColor"
        public ShaderPropType Type;    // typed property
        public float Min;              // for Range
        public float Max;              // for Range
        public float[] Default;        // floats (len depends on type); for Color store RGBA
        public string DefaultTexture;  // for Texture2D
        public string Tooltip;
    }

    /// <summary>
    /// Describes a shader "technique" and its property schema.
    /// Technique is a lightweight tag the engine understands, e.g. "Unlit/Color", "Lit/Standard".
    /// </summary>
    public sealed class ShaderAsset
    {
        public string Name;
        public string Technique;                 // "Unlit/Color", "Lit/Standard", etc.
        public List<ShaderPropertyDecl> Properties = new List<ShaderPropertyDecl>();

        public static ShaderAsset Load(string absPath)
        {
            var json = File.ReadAllText(absPath);
            return JsonSerializer.Deserialize<ShaderAsset>(json);
        }

        public static void Save(string absPath, ShaderAsset sa)
        {
            var json = JsonSerializer.Serialize(sa, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(absPath, json);
        }
    }
}
