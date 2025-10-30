using System;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SkiaSharp;

namespace Game_Engine.Core
{
    // ----------------------- Image primitives -----------------------
    public sealed class Texture2D
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Rgba { get; }

        public Texture2D(int width, int height, byte[] rgba)
        {
            Width = width; Height = height; Rgba = rgba;
        }

        public static Texture2D FromFile(string path)
        {
            using (var bmp = SKBitmap.Decode(path))
            {
                if (bmp == null) throw new Exception("Failed to decode image: " + path);
                var rgba = new byte[bmp.Width * bmp.Height * 4];
                int i = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        rgba[i++] = c.Red; rgba[i++] = c.Green; rgba[i++] = c.Blue; rgba[i++] = c.Alpha;
                    }
                return new Texture2D(bmp.Width, bmp.Height, rgba);
            }
        }

        public static Texture2D FromBytes(byte[] encoded)
        {
            using (var bmp = SKBitmap.Decode(encoded))
            {
                if (bmp == null) throw new Exception("Failed to decode image bytes.");
                var rgba = new byte[bmp.Width * bmp.Height * 4];
                int i = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        rgba[i++] = c.Red; rgba[i++] = c.Green; rgba[i++] = c.Blue; rgba[i++] = c.Alpha;
                    }
                return new Texture2D(bmp.Width, bmp.Height, rgba);
            }
        }
    }

    public sealed class MaterialTexture
    {
        [Persist] public string Name { get; set; }
        [JsonIgnore] public Texture2D Texture { get; set; }
        [JsonIgnore] public IImage Preview { get; set; }
        [Persist] public string SourcePath { get; set; }

        public enum TexUsage
        {
            Albedo,
            Normal,
            Metallic,
            Roughness,
            Specular,
            Emissive,
            AmbientOcclusion,
            Detail,
            Opacity
        }

        [Persist] public TexUsage Usage { get; set; } = TexUsage.Albedo;

        [Flags]
        public enum CubeFaceMask
        {
            None = 0,
            Right = 1,   // +X
            Left = 2,    // -X
            Top = 4,     // +Y
            Bottom = 8,  // -Y
            Back = 16,   // +Z
            Front = 32,  // -Z
            All = Right | Left | Top | Bottom | Back | Front
        }

        [Persist] public CubeFaceMask FaceMask { get; set; } = CubeFaceMask.All;
    }

    /// <summary>
    /// Engine material that supports the new shader/asset approach while
    /// keeping legacy fields so existing code continues to compile.
    /// </summary>
    public sealed class Material
    {
        // ---- Core PBR-ish scalars ----
        [Persist] public string Name { get; set; }
        [Persist] public Color BaseColor { get; set; } = Colors.White;

        /// <summary>
        /// Alias kept for builder code that assigns `m.Tint = …`
        /// </summary>
        public Color Tint
        {
            get { return BaseColor; }
            set { BaseColor = value; }
        }

        [Persist] public bool Transparent { get; set; } = false;
        [Persist] public float AlphaCutoff { get; set; } = 0.5f;

        // Backing for Roughness/Smoothness pair
        private float _roughness = 0.5f;

        /// <summary>Roughness in [0..1].</summary>
        [Persist]
        public float Roughness
        {
            get { return _roughness; }
            set
            {
                var v = value < 0f ? 0f : (value > 1f ? 1f : value);
                _roughness = v;
            }
        }

        /// <summary>
        /// Smoothness in [0..1] (Unity-style). Alias of 1 - Roughness.
        ///  builder paths use `m.Smoothness = x;`
        /// </summary>
        public float Smoothness
        {
            get { return 1f - _roughness; }
            set
            {
                var v = value < 0f ? 0f : (value > 1f ? 1f : value);
                _roughness = 1f - v;
            }
        }

        [Persist] public float Metallic { get; set; } = 0f;

        // Optional shader asset hook for the “aspect-driven by shaders” plan
        [Persist] public string ShaderAssetPath { get; set; }

        // ---- Legacy slots kept for compatibility with importers/builders ----
        [Persist] public List<MaterialTexture> Textures { get; } = new();
        [Persist] public string AlbedoTexturePath { get; set; }
        [Persist] public string AOTexturePath { get; set; }
        [Persist] public string NormalTexturePath { get; set; }
        [Persist] public string EmissiveTexturePath { get; set; }
        [Persist] public string MetallicTexturePath { get; set; }
        [Persist] public string RoughnessTexturePath { get; set; }

        // ---- Legacy Lit toggle (builder reads/sets this) ----
        [Persist] public bool Lit { get; set; } = true;

        public Material Clone()
        {
            var m = new Material();
            m.Name = Name;
            m.BaseColor = BaseColor;
            m.Transparent = Transparent;
            m.AlphaCutoff = AlphaCutoff;
            m._roughness = _roughness;
            m.Metallic = Metallic;
            m.ShaderAssetPath = ShaderAssetPath;
            m.AlbedoTexturePath = AlbedoTexturePath;
            m.AOTexturePath = AOTexturePath;
            m.NormalTexturePath = NormalTexturePath;
            m.EmissiveTexturePath = EmissiveTexturePath;
            m.MetallicTexturePath = MetallicTexturePath;
            m.RoughnessTexturePath = RoughnessTexturePath;
            m.Lit = Lit;
            return m;
        }
    }
}
