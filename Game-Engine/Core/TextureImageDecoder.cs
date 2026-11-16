#nullable enable
using System;

namespace Game_Engine.Core
{
    /// <summary>
    /// Optional image decode hooks. The Xbox UWP host registers WIC; other platforms use Skia/Magick.
    /// </summary>
    public static class TextureImageDecoder
    {
        public static Func<string, Texture2D>? DecodeFile { get; set; }
        public static Func<byte[], Texture2D>? DecodeBytes { get; set; }
    }
}
