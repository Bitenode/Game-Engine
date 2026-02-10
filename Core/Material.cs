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
    /// Engine material that supports the new shader/asset approach
    /// </summary>
    public sealed partial class Material
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

       

        // ---- Legacy Lit toggle (builder reads/sets this) ----
        [Persist] public bool Lit { get; set; } = true;


        //WIP later down roadmap
        public Material Clone(bool copyTextures = true)
        {
            var m = new Material
            {
                Name = this.Name,
                BaseColor = this.BaseColor,
                Transparent = this.Transparent,
                AlphaCutoff = this.AlphaCutoff,
                Metallic = this.Metallic,
                ShaderAssetPath = this.ShaderAssetPath,
                Lit = this.Lit
            };
            // keep exact roughness value (don’t re-map through Smoothness)
            m._roughness = this._roughness;

            if (copyTextures && this.Textures != null)
            {
                for (int i = 0; i < this.Textures.Count; i++)
                {
                    var s = this.Textures[i];
                    if (s == null) continue;

                    // Fast path: our RuntimeTexSlot
                    if (s is RuntimeTexSlot rs)
                    {
                        m.Textures.Add(new RuntimeTexSlot
                        {
                            Texture = rs.Texture,   // share Texture2D ref
                            Usage = rs.Usage,
                            FaceMask = rs.FaceMask,
                            NoFlipV = rs.NoFlipV,
                            SourcePath = rs.SourcePath,
                            ScaleU = rs.ScaleU,
                            ScaleV = rs.ScaleV,
                            OffsetU = rs.OffsetU,
                            OffsetV = rs.OffsetV,
                            RotateUV = rs.RotateUV
                        });
                        continue;
                    }

                    // Generic reflection copy for any other slot shape that matches our names
                    var t = s.GetType();
                    var dst = Activator.CreateInstance(t);
                    const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

                    foreach (var p in t.GetProperties(BF))
                    {
                        if (!p.CanRead || !p.CanWrite) continue;
                        try { p.SetValue(dst, p.GetValue(s)); } catch { }
                    }
                    foreach (var f in t.GetFields(BF))
                    {
                        try { f.SetValue(dst, f.GetValue(s)); } catch { }
                    }
                    m.Textures.Add(dst);
                }
            }

            return m;
        }

    }

}
//runtime
namespace Game_Engine.Core
{
    public sealed partial class Material
    {
        // Runtime-only list of texture slots for GPU rendering.
        // No persistence; Asset files (.material) remain the source of truth.
        [JsonIgnore]
        public List<object> Textures { get; } = new List<object>();
    }

    // Minimal slot type for associating textures with material usage channels.
    internal sealed class RuntimeTexSlot
    {
        public Game_Engine.Core.Texture2D Texture { get; set; }    // required
        public string Usage { get; set; } = "Albedo";               // Albedo/Normal/Roughness/Metallic/AmbientOcclusion/Emissive/Opacity/Specular
        public int FaceMask { get; set; } = -1;                     // -1 means "all"
        public bool NoFlipV { get; set; } = false;

        /// <summary>
        /// Project-relative path to the source image file. Used for scene serialization
        /// so textures can be reloaded after save/load without a .material file.
        /// </summary>
        public string? SourcePath { get; set; }

        // UV transforms (optional, keep defaults)
        public float ScaleU { get; set; } = 1f;
        public float ScaleV { get; set; } = 1f;
        public float OffsetU { get; set; } = 0f;
        public float OffsetV { get; set; } = 0f;
        public float RotateUV { get; set; } = 0f;                   // degrees
    }
}
