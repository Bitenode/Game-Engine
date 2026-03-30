#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>Text horizontal alignment.</summary>
    public enum TextAnchor { Left, Center, Right }

    /// <summary>
    /// UI Text element — renders text inside a RectTransform using a bitmap font atlas.
    /// Supports font size scaling, alignment, word wrap, and outline (via SDF if available).
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIText : UIElement
    {
        /// <summary>The text string to display.</summary>
        [Persist] public string Text { get; set; } = "Text";

        /// <summary>Font size in canvas pixels.</summary>
        [Persist] public float FontSize { get; set; } = 24f;

        /// <summary>Path to the BMFont .fnt file (project-relative or absolute).</summary>
        [Persist] public string FontPath { get; set; } = "";

        /// <summary>Horizontal text alignment within the rect.</summary>
        [Persist] public TextAnchor Alignment { get; set; } = TextAnchor.Left;

        /// <summary>Wrap text to fit the rect width.</summary>
        [Persist] public bool WordWrap { get; set; } = true;

        /// <summary>Line spacing multiplier (1.0 = default).</summary>
        [Persist] public float LineSpacing { get; set; } = 1.0f;

        // ── Font caching ──
        private static readonly Dictionary<string, BitmapFont> s_fontCache = new();
        private BitmapFont? _font;
        private string _loadedFontPath = "";

        /// <summary>
        /// Load or return the cached BitmapFont for the current FontPath.
        /// Falls back to a built-in default font if the path is empty.
        /// </summary>
        private BitmapFont? GetFont()
        {
            string path = FontPath;

            // Try to resolve the default font if no path specified
            if (string.IsNullOrEmpty(path))
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    string defaultPath = Path.Combine(proj.AssetsPath, "Standard Assets", "Fonts", "Default.fnt");
                    if (!File.Exists(defaultPath))
                        defaultPath = Path.Combine(proj.RootPath, "Standard Assets", "Fonts", "Default.fnt");
                    if (!File.Exists(defaultPath))
                    {
                        // Auto-generate the default font
                        var generated = DefaultFontGenerator.EnsureDefaultFont();
                        if (generated != null) defaultPath = generated;
                    }
                    if (File.Exists(defaultPath))
                        path = defaultPath;
                    else
                        return null;
                }
                else return null;
            }

            if (_loadedFontPath == path && _font != null)
                return _font;

            if (s_fontCache.TryGetValue(path, out var cached))
            {
                _font = cached;
                _loadedFontPath = path;
                return _font;
            }

            try
            {
                string absPath = path;
                if (!Path.IsPathRooted(absPath))
                {
                    var proj = ProjectService.Current;
                    if (proj != null)
                        absPath = Path.GetFullPath(Path.Combine(proj.RootPath, absPath));
                }

                if (File.Exists(absPath))
                {
                    _font = BitmapFont.Load(absPath);
                    s_fontCache[path] = _font;
                    _loadedFontPath = path;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[UIText] Failed to load font '{path}': {ex.Message}");
                _font = null;
            }

            return _font;
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            var (r, g, b, a) = GetFinalColor();
            if (a <= 0f || string.IsNullOrEmpty(Text)) return UIDrawData.Empty;

            var font = GetFont();
            if (font == null)
            {
                // Fallback: render a colored rect to indicate "no font"
                return base.GetDrawData(in rect);
            }

            float scale = FontSize / Math.Max(1f, font.LineHeight);

            // Build lines (with optional word wrap)
            var lines = LayoutLines(font, Text, rect.Width, scale);
            if (lines.Count == 0) return UIDrawData.Empty;

            float lineH = font.LineHeight * scale * LineSpacing;

            // Count total quads needed
            int totalGlyphs = 0;
            foreach (var line in lines)
                totalGlyphs += line.Length;

            if (totalGlyphs == 0) return UIDrawData.Empty;

            if (_quadBuffer.Length < totalGlyphs)
                _quadBuffer = new UIQuad[totalGlyphs];

            int qi = 0;
            float cursorY = rect.Y + rect.Height - lineH; // start from top

            foreach (var line in lines)
            {
                if (cursorY + lineH < rect.Y) break; // below visible area

                // Measure line width for alignment
                float lineWidth = MeasureLineWidth(font, line, scale);
                float cursorX = rect.X;

                switch (Alignment)
                {
                    case TextAnchor.Center:
                        cursorX = rect.X + (rect.Width - lineWidth) * 0.5f;
                        break;
                    case TextAnchor.Right:
                        cursorX = rect.X + rect.Width - lineWidth;
                        break;
                }

                foreach (char ch in line)
                {
                    if (!font.Glyphs.TryGetValue(ch, out var glyph))
                    {
                        if (font.Glyphs.TryGetValue('?', out glyph)) { }
                        else { cursorX += FontSize * 0.5f; continue; }
                    }

                    float gx = cursorX + glyph.OffsetX * scale;
                    float gy = cursorY + (font.LineHeight - glyph.OffsetY - glyph.Height) * scale;
                    float gw = glyph.Width * scale;
                    float gh = glyph.Height * scale;

                    _quadBuffer[qi++] = new UIQuad
                    {
                        X0 = gx, Y0 = gy,
                        X1 = gx + gw, Y1 = gy + gh,
                        U0 = glyph.U0, V0 = glyph.V1, // flip V for bottom-up
                        U1 = glyph.U1, V1 = glyph.V0,
                        R = r, G = g, B = b, A = a,
                        Texture = font.AtlasTexture,
                        TextureHandle = 0,
                        IsSDF = font.IsSDF
                    };

                    cursorX += glyph.Advance * scale;
                }

                cursorY -= lineH;
            }

            return new UIDrawData { QuadCount = qi, Quads = _quadBuffer };
        }

        private static List<string> LayoutLines(BitmapFont font, string text, float maxWidth, float scale)
        {
            var result = new List<string>(8);

            var hardLines = text.Split('\n');
            foreach (var hardLine in hardLines)
            {
                if (maxWidth <= 0 || !true) // word wrap controlled by the element
                {
                    result.Add(hardLine);
                    continue;
                }

                // Word wrap
                var words = hardLine.Split(' ');
                string currentLine = "";
                float currentWidth = 0f;
                float spaceWidth = font.Glyphs.TryGetValue(' ', out var spGlyph) ? spGlyph.Advance * scale : ScaledLineHeight(font, scale) * 0.3f;

                foreach (var word in words)
                {
                    float wordWidth = MeasureWordWidth(font, word, scale);

                    if (currentLine.Length > 0 && currentWidth + spaceWidth + wordWidth > maxWidth)
                    {
                        result.Add(currentLine);
                        currentLine = word;
                        currentWidth = wordWidth;
                    }
                    else
                    {
                        if (currentLine.Length > 0)
                        {
                            currentLine += " " + word;
                            currentWidth += spaceWidth + wordWidth;
                        }
                        else
                        {
                            currentLine = word;
                            currentWidth = wordWidth;
                        }
                    }
                }

                if (currentLine.Length > 0)
                    result.Add(currentLine);
            }

            return result;
        }

        private static float ScaledLineHeight(BitmapFont font, float scale)
            => font.LineHeight * scale;

        private static float MeasureWordWidth(BitmapFont font, string word, float scale)
        {
            float w = 0f;
            foreach (char ch in word)
            {
                if (font.Glyphs.TryGetValue(ch, out var g))
                    w += g.Advance * scale;
                else
                    w += font.LineHeight * scale * 0.5f;
            }
            return w;
        }

        private static float MeasureLineWidth(BitmapFont font, string line, float scale)
        {
            float w = 0f;
            foreach (char ch in line)
            {
                if (font.Glyphs.TryGetValue(ch, out var g))
                    w += g.Advance * scale;
                else
                    w += font.LineHeight * scale * 0.5f;
            }
            return w;
        }
    }

    // =====================================================================
    // BitmapFont — BMFont text-format parser + glyph data
    // =====================================================================

    /// <summary>A single glyph in a bitmap font atlas.</summary>
    public sealed class BitmapGlyph
    {
        public int Id;
        public float U0, V0, U1, V1; // normalised UV coordinates
        public float Width, Height;  // glyph size in pixels
        public float OffsetX, OffsetY; // bearing
        public float Advance;        // horizontal advance in pixels
    }

    /// <summary>
    /// A bitmap font loaded from BMFont text format (.fnt).
    /// Stores glyph metrics, atlas texture, and metadata.
    /// </summary>
    public sealed class BitmapFont
    {
        public string Name { get; set; } = "";
        public float LineHeight { get; set; } = 32f;
        public float Base { get; set; } = 26f;
        public float ScaleW { get; set; } = 256f;
        public float ScaleH { get; set; } = 256f;
        public bool IsSDF { get; set; }

        public Dictionary<int, BitmapGlyph> Glyphs { get; } = new(128);
        public Texture2D? AtlasTexture { get; set; }

        /// <summary>
        /// Load a BMFont text-format .fnt file and its atlas texture.
        /// </summary>
        public static BitmapFont Load(string fntPath)
        {
            var font = new BitmapFont();
            string dir = Path.GetDirectoryName(fntPath) ?? "";
            string? atlasFile = null;

            foreach (string rawLine in File.ReadLines(fntPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("info "))
                {
                    font.Name = ParseString(line, "face");
                }
                else if (line.StartsWith("common "))
                {
                    font.LineHeight = ParseFloat(line, "lineHeight");
                    font.Base = ParseFloat(line, "base");
                    font.ScaleW = ParseFloat(line, "scaleW");
                    font.ScaleH = ParseFloat(line, "scaleH");
                }
                else if (line.StartsWith("page "))
                {
                    atlasFile = ParseString(line, "file");
                }
                else if (line.StartsWith("char "))
                {
                    int id = (int)ParseFloat(line, "id");
                    float x = ParseFloat(line, "x");
                    float y = ParseFloat(line, "y");
                    float w = ParseFloat(line, "width");
                    float h = ParseFloat(line, "height");
                    float xoff = ParseFloat(line, "xoffset");
                    float yoff = ParseFloat(line, "yoffset");
                    float xadv = ParseFloat(line, "xadvance");

                    float sw = font.ScaleW > 0 ? font.ScaleW : 1f;
                    float sh = font.ScaleH > 0 ? font.ScaleH : 1f;

                    font.Glyphs[id] = new BitmapGlyph
                    {
                        Id = id,
                        U0 = x / sw,
                        V0 = y / sh,
                        U1 = (x + w) / sw,
                        V1 = (y + h) / sh,
                        Width = w,
                        Height = h,
                        OffsetX = xoff,
                        OffsetY = yoff,
                        Advance = xadv
                    };
                }
            }

            // Load atlas texture
            if (!string.IsNullOrEmpty(atlasFile))
            {
                string atlasPath = Path.Combine(dir, atlasFile);
                if (File.Exists(atlasPath))
                {
                    try { font.AtlasTexture = Texture2D.FromFile(atlasPath); }
                    catch (Exception ex) { Log.Warning($"[BitmapFont] Failed to load atlas '{atlasPath}': {ex.Message}"); }
                }
            }

            // Detect SDF from filename convention
            font.IsSDF = fntPath.Contains("sdf", StringComparison.OrdinalIgnoreCase)
                      || fntPath.Contains("SDF", StringComparison.Ordinal);

            return font;
        }

        private static float ParseFloat(string line, string key)
        {
            string pattern = key + "=";
            int idx = line.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return 0f;

            idx += pattern.Length;
            int end = idx;
            while (end < line.Length && line[end] != ' ' && line[end] != '\t')
                end++;

            if (float.TryParse(line.AsSpan(idx, end - idx),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        private static string ParseString(string line, string key)
        {
            string pattern = key + "=\"";
            int idx = line.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                // Try without quotes
                pattern = key + "=";
                idx = line.IndexOf(pattern, StringComparison.Ordinal);
                if (idx < 0) return "";
                idx += pattern.Length;
                int end2 = idx;
                while (end2 < line.Length && line[end2] != ' ' && line[end2] != '\t')
                    end2++;
                return line[idx..end2];
            }

            idx += pattern.Length;
            int end = line.IndexOf('"', idx);
            return end < 0 ? line[idx..] : line[idx..end];
        }
    }
}
