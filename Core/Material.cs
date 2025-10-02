using SkiaSharp;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace Game_Engine.Core
{
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
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null) throw new System.Exception($"Failed to decode image: {path}");

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

        public static Texture2D FromBytes(byte[] encoded)
        {
            using var bmp = SKBitmap.Decode(encoded);
            if (bmp is null) throw new System.Exception("Failed to decode image bytes.");

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

    public sealed class MaterialTexture
    {
        [Persist] public string? Name { get; set; }
        [JsonIgnore] public Texture2D? Texture { get; set; }
        [JsonIgnore] public IImage? Preview { get; set; }
        [Persist] public string? SourcePath { get; set; }

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

        // (per-side on primitives)

        
        public enum CubeFaceMask
        {
            None = 0,
            Right = 1,   // +X
            Left = 2,   // -X
            Top = 4,   // +Y
            Bottom = 8,   // -Y
            Back = 16,  // +Z
            Front = 32,  // -Z
            All = Right | Left | Top | Bottom | Back | Front
        }

        [Persist] public CubeFaceMask FaceMask { get; set; } = CubeFaceMask.All;
        
    }


    public sealed class Material
    {
        [Persist] public List<MaterialTexture> Textures { get; } = new();

        // serialized as "#AARRGGBB" by SceneSerialization
        [Persist] public Color Tint { get; set; } = Colors.White;

        //  knobs WIP
        [Persist] public float Metallic { get; set; }
        [Persist] public float Smoothness { get; set; }

        public enum BlendMode { Opaque, Alpha }
        public BlendMode Blend = BlendMode.Opaque;
    }
}
