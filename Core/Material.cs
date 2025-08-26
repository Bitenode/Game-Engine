using SkiaSharp;

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

        // NEW: used by the Inspector via reflection
        public static Texture2D FromFile(string path)
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null) throw new Exception($"Failed to decode image: {path}");

            var rgba = new byte[bmp.Width * bmp.Height * 4];
            int i = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y); // RGBA (unpremultiplied)
                    rgba[i++] = c.Red;
                    rgba[i++] = c.Green;
                    rgba[i++] = c.Blue;
                    rgba[i++] = c.Alpha;
                }
            }
            return new Texture2D(bmp.Width, bmp.Height, rgba);
        }

        // Optional: also supports the Inspector’s FromBytes() probe
        public static Texture2D FromBytes(byte[] encoded)
        {
            using var bmp = SKBitmap.Decode(encoded);
            if (bmp is null) throw new Exception("Failed to decode image bytes.");

            var rgba = new byte[bmp.Width * bmp.Height * 4];
            int i = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    rgba[i++] = c.Red;
                    rgba[i++] = c.Green;
                    rgba[i++] = c.Blue;
                    rgba[i++] = c.Alpha;
                }
            }
            return new Texture2D(bmp.Width, bmp.Height, rgba);
        }
    }

    public sealed class MaterialTexture
    {
        public string? Name { get; set; }
        public Texture2D? Texture { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public Avalonia.Media.IImage? Preview { get; set; }
    }

    public sealed class Material
    {
        public List<MaterialTexture> Textures { get; } = new();
        public Avalonia.Media.Color Tint { get; set; } = Avalonia.Media.Colors.White;
    }
}
