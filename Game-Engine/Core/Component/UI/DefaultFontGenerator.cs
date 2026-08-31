#nullable enable
using System;
using System.IO;
using System.Text;
using SkiaSharp;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Generates a default bitmap font atlas (.fnt + .png) using SkiaSharp
    /// when no font file is available. Produces a simple monospace-like
    /// ASCII font suitable for UI text rendering.
    /// </summary>
    public static class DefaultFontGenerator
    {
        private const int AtlasSize = 512;
        private const int FontSizePx = 32;
        private const int CellPadding = 2;
        private const string FontFamily = "Segoe UI";

        /// <summary>
        /// Generate a default BMFont (.fnt) and atlas (.png) at the given directory.
        /// Returns the path to the .fnt file.
        /// </summary>
        public static string Generate(string directory)
        {
            Directory.CreateDirectory(directory);

            string fntPath = Path.Combine(directory, "Default.fnt");
            string pngPath = Path.Combine(directory, "Default.png");

            // If already generated, skip
            if (File.Exists(fntPath) && File.Exists(pngPath))
                return fntPath;

            using var typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                 ?? SKTypeface.Default;
            using var font = new SKFont(typeface, FontSizePx);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White
            };

            var metrics = font.Metrics;
            float lineHeight = MathF.Ceiling(-metrics.Ascent + metrics.Descent + metrics.Leading);
            float baseline = MathF.Ceiling(-metrics.Ascent);

            // Generate atlas
            using var bitmap = new SKBitmap(AtlasSize, AtlasSize, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            var fnt = new StringBuilder();
            fnt.AppendLine($"info face=\"{FontFamily}\" size={FontSizePx} bold=0 italic=0 charset=\"\" unicode=1 stretchH=100 smooth=1 aa=1 padding=0,0,0,0 spacing=1,1 outline=0");
            fnt.AppendLine($"common lineHeight={lineHeight:F0} base={baseline:F0} scaleW={AtlasSize} scaleH={AtlasSize} pages=1 packed=0 alphaChnl=0 redChnl=4 greenChnl=4 blueChnl=4");
            fnt.AppendLine("page id=0 file=\"Default.png\"");

            int charCount = 0;
            var charLines = new StringBuilder();

            float cursorX = CellPadding;
            float cursorY = CellPadding;

            // Generate printable ASCII characters (32-126)
            for (int ch = 32; ch <= 126; ch++)
            {
                string s = ((char)ch).ToString();

                float advance = font.MeasureText(s);
                float charWidth = MathF.Ceiling(advance);

                // Get glyph bounds for precise positioning
                font.MeasureText(s, out SKRect bounds);
                float glyphW = MathF.Ceiling(bounds.Width) + 2;
                float glyphH = MathF.Ceiling(lineHeight);

                // Wrap to next row if needed
                if (cursorX + glyphW + CellPadding > AtlasSize)
                {
                    cursorX = CellPadding;
                    cursorY += glyphH + CellPadding;
                }

                if (cursorY + glyphH > AtlasSize)
                    break; // Atlas full

                // Draw the character
                float drawX = cursorX - bounds.Left;
                float drawY = cursorY + baseline;
                canvas.DrawText(s, drawX, drawY, font, paint);

                // Emit BMFont char line
                float xoff = bounds.Left;
                float yoff = 0;
                charLines.AppendLine(
                    $"char id={ch,-5} x={cursorX,-5:F0} y={cursorY,-5:F0} width={glyphW,-5:F0} height={glyphH,-5:F0} " +
                    $"xoffset={xoff,-5:F1} yoffset={yoff,-5:F1} xadvance={advance,-5:F1} page=0  chnl=15");

                charCount++;
                cursorX += glyphW + CellPadding;
            }

            fnt.AppendLine($"chars count={charCount}");
            fnt.Append(charLines);

            // Save atlas PNG
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.OpenWrite(pngPath))
            {
                data.SaveTo(stream);
            }

            // Save .fnt file
            File.WriteAllText(fntPath, fnt.ToString());

            Log.Info($"[DefaultFontGenerator] Generated default font atlas at: {fntPath}");
            return fntPath;
        }

        /// <summary>
        /// Ensure the default font exists in the project's Assets/Standard Assets/Fonts/ directory
        /// (or legacy root Standard Assets/Fonts/).
        /// Returns the path to the .fnt file, or null if generation failed.
        /// </summary>
        public static string? EnsureDefaultFont()
        {
            try
            {
                var proj = ProjectService.Current;
                if (proj == null) return null;

                var underAssets = Path.Combine(proj.AssetsPath, "Standard Assets", "Fonts");
                var legacyRoot = Path.Combine(proj.RootPath, "Standard Assets", "Fonts");
                string fontsDir = Directory.Exists(underAssets) || !Directory.Exists(legacyRoot)
                    ? underAssets
                    : legacyRoot;
                return Generate(fontsDir);
            }
            catch (Exception ex)
            {
                Log.Warning($"[DefaultFontGenerator] Failed to generate default font: {ex.Message}");
                return null;
            }
        }
    }
}
