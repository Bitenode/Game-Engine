using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Avalonia.Media;
#if !ANDROID
using ImageMagick;
#endif
using SkiaSharp;

namespace Game_Engine.Core
{
    // ----------------------- Image primitives -----------------------
    public sealed class Texture2D
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Rgba { get; }
        /// <summary>Full path passed to <see cref="FromFile"/> when loaded from disk; used for BCn sidecar .dds.</summary>
        public string? SourcePath { get; }

        public Texture2D(int width, int height, byte[] rgba, string? sourcePath = null)
        {
            Width = width; Height = height; Rgba = rgba; SourcePath = sourcePath;
        }

        static readonly Dictionary<string, Texture2D> s_fromFileCache = new(StringComparer.OrdinalIgnoreCase);
        static readonly object s_fromFileLock = new();

        /// <summary>Decoded RGBA cache keyed by full file path (materials, models, UI share one copy in RAM).</summary>
        public static Texture2D FromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path required", nameof(path));
            string key = Path.GetFullPath(path);
            lock (s_fromFileLock)
            {
                if (s_fromFileCache.TryGetValue(key, out var cached) && cached != null)
                    return cached;
            }

            var loaded = LoadFromFileUncached(key);

            lock (s_fromFileLock)
            {
                if (!s_fromFileCache.ContainsKey(key))
                    s_fromFileCache[key] = loaded;
                return s_fromFileCache[key];
            }
        }

        static Texture2D LoadFromFileUncached(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext is ".tga" or ".targa" or ".tif" or ".tiff")
            {
#if !ANDROID
                try { return DecodeWithMagick(path); }
                catch { /* custom TGA/TIFF fallbacks below */ }
#endif
                if (ext is ".tga" or ".targa")
                    return DecodeTga(File.ReadAllBytes(path), path);
            }

            try
            {
                using var codec = SKCodec.Create(path);
                if (codec == null) throw new Exception("SKCodec returned null");

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                                           SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var bmp = SKBitmap.Decode(codec, info);
                if (bmp == null) throw new Exception("SKBitmap.Decode returned null");

                var pixels = bmp.GetPixelSpan();
                var rgba = new byte[bmp.Width * bmp.Height * 4];
                pixels.CopyTo(rgba);
                return new Texture2D(bmp.Width, bmp.Height, rgba, path);
            }
            catch
            {
                if (ext == ".psd" || ext == ".psb")
                {
#if ANDROID
                    throw new Exception("PSD/PSB textures are not supported on Android; use PNG, JPEG, or TGA.");
#else
                    return DecodePsdWithMagick(path);
#endif
                }
                if (ext == ".tif" || ext == ".tiff")
                    return DecodeTiff(path);
                throw;
            }
        }

#if !ANDROID
        static Texture2D DecodePsdWithMagick(string path) => DecodeWithMagick(path);

        static Texture2D DecodeWithMagick(string path)
        {
            using var img = new MagickImage(path);
            img.ColorSpace = ColorSpace.sRGB;
            if (img.HasAlpha)
                img.Alpha(AlphaOption.On);
            else
                img.Alpha(AlphaOption.Off);

            // Ensure 8-bit RGBA output expected by renderer.
            img.Depth = 8;
            var pixels = img.GetPixels();
            var rgba = pixels.ToByteArray(PixelMapping.RGBA)
                ?? throw new Exception("Magick returned no pixels: " + path);
            if (rgba.Length < img.Width * img.Height * 4)
                throw new Exception("Magick pixel size mismatch: " + path);
            return new Texture2D((int)img.Width, (int)img.Height, rgba, Path.GetFullPath(path));
        }
#endif

        static Texture2D DecodeTga(byte[] data, string debugPath)
        {
            if (data.Length < 18)
                throw new Exception("TGA file too small: " + debugPath);

            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            int cmapLength = data[5] | (data[6] << 8);
            int cmapEntrySize = data[7];
            int width = data[12] | (data[13] << 8);
            int height = data[14] | (data[15] << 8);
            int bpp = data[16];
            int descriptor = data[17];
            bool topToBottom = (descriptor & 0x20) != 0;
            bool rightToLeft = (descriptor & 0x10) != 0;

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                throw new Exception($"TGA invalid dimensions {width}x{height}: {debugPath}");

            int bytesPerPixel = bpp / 8;
            if (bytesPerPixel < 3 || bytesPerPixel > 4)
                throw new Exception($"TGA unsupported bpp {bpp}: {debugPath}");

            int offset = 18 + idLength;
            if (colorMapType != 0)
                offset += cmapLength * ((cmapEntrySize + 7) / 8);

            byte[] rgba = new byte[width * height * 4];

            // For uncompressed TGA, detect if the file has per-row padding
            int minRowStride = width * bytesPerPixel;
            int rowStride = minRowStride;
            if (imageType == 2 || imageType == 3)
            {
                int pixelDataLen = data.Length - offset;
                int expectedLen = minRowStride * height;
                if (pixelDataLen > expectedLen && height > 1)
                {
                    int paddedStride = (minRowStride + 3) & ~3;
                    if (pixelDataLen >= paddedStride * height)
                        rowStride = paddedStride;
                }
            }

            if (imageType == 2)
            {
                for (int y = 0; y < height; y++)
                {
                    int destY = topToBottom ? y : (height - 1 - y);
                    int rowOff = offset + y * rowStride;

                    for (int x = 0; x < width; x++)
                    {
                        int destX = rightToLeft ? (width - 1 - x) : x;
                        int si = rowOff + x * bytesPerPixel;
                        int di = (destY * width + destX) * 4;
                        if (si + bytesPerPixel > data.Length) break;
                        rgba[di + 0] = data[si + 2];
                        rgba[di + 1] = data[si + 1];
                        rgba[di + 2] = data[si + 0];
                        rgba[di + 3] = 255;
                    }
                }
            }
            else if (imageType == 10)
            {
                int si = offset;
                int pixelIndex = 0;
                int totalPixels = width * height;
                while (pixelIndex < totalPixels && si < data.Length)
                {
                    if (si >= data.Length) break;
                    byte header = data[si++];
                    int count = (header & 0x7F) + 1;
                    bool isRle = (header & 0x80) != 0;

                    if (isRle)
                    {
                        if (si + bytesPerPixel > data.Length) break;
                        byte tb = data[si], tg = data[si + 1], tr = data[si + 2];
                        si += bytesPerPixel;

                        for (int i = 0; i < count && pixelIndex < totalPixels; i++, pixelIndex++)
                        {
                            int py = pixelIndex / width;
                            int px = pixelIndex % width;
                            int destY = topToBottom ? py : (height - 1 - py);
                            int destX = rightToLeft ? (width - 1 - px) : px;
                            int di = (destY * width + destX) * 4;
                            rgba[di] = tr; rgba[di + 1] = tg; rgba[di + 2] = tb; rgba[di + 3] = 255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count && pixelIndex < totalPixels; i++, pixelIndex++)
                        {
                            if (si + bytesPerPixel > data.Length) break;
                            int py = pixelIndex / width;
                            int px = pixelIndex % width;
                            int destY = topToBottom ? py : (height - 1 - py);
                            int destX = rightToLeft ? (width - 1 - px) : px;
                            int di = (destY * width + destX) * 4;
                            rgba[di] = data[si + 2]; rgba[di + 1] = data[si + 1];
                            rgba[di + 2] = data[si]; rgba[di + 3] = 255;
                            si += bytesPerPixel;
                        }
                    }
                }
            }
            else if (imageType == 3)
            {
                for (int y = 0; y < height; y++)
                {
                    int destY = topToBottom ? y : (height - 1 - y);
                    int rowOff = offset + y * rowStride;
                    for (int x = 0; x < width; x++)
                    {
                        int si = rowOff + x;
                        if (si >= data.Length) break;
                        int destX = rightToLeft ? (width - 1 - x) : x;
                        int di = (destY * width + destX) * 4;
                        byte v = data[si];
                        rgba[di] = v; rgba[di + 1] = v; rgba[di + 2] = v; rgba[di + 3] = 255;
                    }
                }
            }
            else
            {
                throw new Exception($"TGA unsupported image type {imageType}: {debugPath}");
            }

            return new Texture2D(width, height, rgba, Path.GetFullPath(debugPath));
        }

        static Texture2D DecodeTiff(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 8)
                throw new Exception("TIFF file too small: " + path);

            bool le = data[0] == 'I' && data[1] == 'I';
            bool be = data[0] == 'M' && data[1] == 'M';
            if (!le && !be)
                throw new Exception("Not a valid TIFF file: " + path);

            int ReadU16(int o)
            {
                if (o < 0 || o + 1 >= data.Length) return 0;
                return le ? (data[o] | (data[o + 1] << 8)) : ((data[o] << 8) | data[o + 1]);
            }
            int ReadU32(int o)
            {
                if (o < 0 || o + 3 >= data.Length) return 0;
                uint v = le
                    ? (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24))
                    : (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
                return (int)(v & 0x7FFFFFFF);
            }

            int ifdOffset = ReadU32(4);
            int numEntries = ReadU16(ifdOffset);

            int width = 0, height = 0, bitsPerSample = 8, samplesPerPixel = 1;
            int compression = 1, predictor = 1;
            int rowsPerStrip = int.MaxValue;
            int[] stripOffsets = null, stripByteCounts = null;

            for (int i = 0; i < numEntries; i++)
            {
                int entryOff = ifdOffset + 2 + i * 12;
                int tag = ReadU16(entryOff);
                int type = ReadU16(entryOff + 2);
                int count = ReadU32(entryOff + 4);
                int valOff = entryOff + 8;

                // SHORT (type 3): up to 2 values fit inline (4 bytes).
                // LONG (type 4): 1 value fits inline.
                bool fitsInline = type == 3 ? count <= 2 : count <= 1;

                int ReadVal() => type switch
                {
                    3 => ReadU16(fitsInline ? valOff : ReadU32(valOff)),
                    4 => ReadU32(fitsInline ? valOff : ReadU32(valOff)),
                    _ => ReadU32(valOff)
                };

                int[] ReadVals()
                {
                    var arr = new int[count];
                    int off = fitsInline ? valOff : ReadU32(valOff);
                    if (off < 0) off = 0;
                    for (int j = 0; j < count; j++)
                    {
                        int addr = type == 3 ? off + j * 2 : off + j * 4;
                        if (addr < 0 || addr + 3 >= data.Length) break;
                        arr[j] = type == 3 ? ReadU16(addr) : ReadU32(addr);
                    }
                    return arr;
                }

                switch (tag)
                {
                    case 256: width = ReadVal(); break;
                    case 257: height = ReadVal(); break;
                    case 258: bitsPerSample = ReadVal(); break;
                    case 259: compression = ReadVal(); break;
                    case 273: stripOffsets = ReadVals(); break;
                    case 277: samplesPerPixel = ReadVal(); break;
                    case 278: rowsPerStrip = ReadVal(); break;
                    case 279: stripByteCounts = ReadVals(); break;
                    case 317: predictor = ReadVal(); break;
                }
            }

            if (compression != 1 && compression != 5)
                throw new Exception($"TIFF compressed format {compression} not supported: {path}");
            if (width <= 0 || height <= 0)
                throw new Exception($"TIFF invalid dimensions: {path}");

            int bytesPerSample = bitsPerSample / 8;
            int bytesPerPixel = samplesPerPixel * bytesPerSample;
            int rowBytes = width * bytesPerPixel;

            byte[] rgba = new byte[width * height * 4];
            int destRow = 0;

            if (stripOffsets != null)
            {
                for (int s = 0; s < stripOffsets.Length; s++)
                {
                    int sOff = stripOffsets[s];
                    int sLen = (stripByteCounts != null && s < stripByteCounts.Length)
                        ? stripByteCounts[s] : (width * rowsPerStrip * bytesPerPixel);

                    if (sOff <= 0 || sOff >= data.Length || sLen <= 0)
                    {
                        destRow += Math.Min(rowsPerStrip, height - destRow);
                        continue;
                    }

                    byte[] raw;
                    try
                    {
                        if (compression == 5)
                            raw = DecodeLzw(data, sOff, Math.Min(sLen, data.Length - sOff));
                        else
                        {
                            int len = Math.Min(sLen, data.Length - sOff);
                            raw = new byte[len];
                            Array.Copy(data, sOff, raw, 0, len);
                        }
                    }
                    catch
                    {
                        destRow += Math.Min(rowsPerStrip, height - destRow);
                        continue;
                    }

                    int stripRows = Math.Min(rowsPerStrip, height - destRow);
                    if (stripRows <= 0) break;

                    if (predictor == 2 && raw.Length >= rowBytes)
                    {
                        for (int r = 0; r < stripRows; r++)
                        {
                            int rOff = r * rowBytes;
                            if (rOff + rowBytes > raw.Length) break;
                            for (int x = 1; x < width; x++)
                            {
                                for (int c = 0; c < samplesPerPixel; c++)
                                {
                                    int idx = rOff + x * bytesPerPixel + c * bytesPerSample;
                                    int prev = rOff + (x - 1) * bytesPerPixel + c * bytesPerSample;
                                    if (idx + bytesPerSample > raw.Length || prev + bytesPerSample > raw.Length) break;
                                    if (bytesPerSample == 1)
                                        raw[idx] = (byte)(raw[idx] + raw[prev]);
                                    else if (bytesPerSample == 2)
                                    {
                                        int curVal = le ? (raw[idx] | (raw[idx + 1] << 8)) : ((raw[idx] << 8) | raw[idx + 1]);
                                        int prevVal = le ? (raw[prev] | (raw[prev + 1] << 8)) : ((raw[prev] << 8) | raw[prev + 1]);
                                        int sum = (curVal + prevVal) & 0xFFFF;
                                        if (le) { raw[idx] = (byte)sum; raw[idx + 1] = (byte)(sum >> 8); }
                                        else { raw[idx] = (byte)(sum >> 8); raw[idx + 1] = (byte)sum; }
                                    }
                                }
                            }
                        }
                    }

                    for (int r = 0; r < stripRows && destRow < height; r++, destRow++)
                    {
                        int rOff = r * rowBytes;
                        for (int x = 0; x < width; x++)
                        {
                            int si = rOff + x * bytesPerPixel;
                            if (si + bytesPerPixel > raw.Length) break;
                            int di = (destRow * width + x) * 4;
                            if (di + 3 >= rgba.Length) break;

                            if (bytesPerSample == 2)
                            {
                                if (si + 1 >= raw.Length) break;
                                int r16 = le ? (raw[si] | (raw[si + 1] << 8)) : ((raw[si] << 8) | raw[si + 1]);
                                rgba[di] = (byte)(r16 >> 8);
                                if (samplesPerPixel >= 3 && si + 5 < raw.Length)
                                {
                                    int g16 = le ? (raw[si + 2] | (raw[si + 3] << 8)) : ((raw[si + 2] << 8) | raw[si + 3]);
                                    int b16 = le ? (raw[si + 4] | (raw[si + 5] << 8)) : ((raw[si + 4] << 8) | raw[si + 5]);
                                    rgba[di + 1] = (byte)(g16 >> 8);
                                    rgba[di + 2] = (byte)(b16 >> 8);
                                }
                                else { rgba[di + 1] = rgba[di]; rgba[di + 2] = rgba[di]; }
                                rgba[di + 3] = 255;
                            }
                            else
                            {
                                rgba[di] = raw[si];
                                rgba[di + 1] = (samplesPerPixel >= 3 && si + 1 < raw.Length) ? raw[si + 1] : raw[si];
                                rgba[di + 2] = (samplesPerPixel >= 3 && si + 2 < raw.Length) ? raw[si + 2] : raw[si];
                                rgba[di + 3] = 255;
                            }
                        }
                    }
                }
            }

            return new Texture2D(width, height, rgba, Path.GetFullPath(path));
        }

        static byte[] DecodeLzw(byte[] src, int offset, int length)
        {
            using var output = new MemoryStream();
            int bitPos = 0;
            int end = offset + length;
            int codeSize = 9;
            const int ClearCode = 256;
            const int EoiCode = 257;

            var table = new List<byte[]>(4096);
            void ResetTable()
            {
                table.Clear();
                for (int i = 0; i < 258; i++)
                    table.Add(i < 256 ? new byte[] { (byte)i } : Array.Empty<byte>());
                codeSize = 9;
            }

            int ReadCode()
            {
                int byteOff = offset + (bitPos >> 3);
                if (byteOff + 2 >= src.Length) return EoiCode;
                int bitOff = bitPos & 7;
                int raw = (src[byteOff] << 16)
                        | (byteOff + 1 < src.Length ? src[byteOff + 1] << 8 : 0)
                        | (byteOff + 2 < src.Length ? src[byteOff + 2] : 0);
                int shift = 24 - bitOff - codeSize;
                int code = (raw >> shift) & ((1 << codeSize) - 1);
                bitPos += codeSize;
                return code;
            }

            ResetTable();
            int firstCode = ReadCode();
            if (firstCode == ClearCode) firstCode = ReadCode();
            if (firstCode == EoiCode) return output.ToArray();

            byte[] prev = firstCode < table.Count ? table[firstCode] : new byte[] { (byte)firstCode };
            output.Write(prev, 0, prev.Length);

            while (bitPos < length * 8)
            {
                int code = ReadCode();
                if (code == EoiCode) break;
                if (code == ClearCode)
                {
                    ResetTable();
                    code = ReadCode();
                    if (code == EoiCode) break;
                    prev = code < table.Count ? table[code] : new byte[] { (byte)code };
                    output.Write(prev, 0, prev.Length);
                    continue;
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                }
                else
                {
                    var buf = new byte[prev.Length + 1];
                    Array.Copy(prev, buf, prev.Length);
                    buf[prev.Length] = prev[0];
                    entry = buf;
                }

                output.Write(entry, 0, entry.Length);

                if (table.Count < 4093)
                {
                    var newEntry = new byte[prev.Length + 1];
                    Array.Copy(prev, newEntry, prev.Length);
                    newEntry[prev.Length] = entry[0];
                    table.Add(newEntry);

                    if (table.Count == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }

                prev = entry;
            }

            return output.ToArray();
        }

        public static Texture2D FromBytes(byte[] encoded)
        {
            using var codec = SKCodec.Create(new MemoryStream(encoded));
            if (codec == null) throw new Exception("Failed to decode image bytes.");

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                                       SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bmp = SKBitmap.Decode(codec, info);
            if (bmp == null) throw new Exception("Failed to decode image bytes.");

            var pixels = bmp.GetPixelSpan();
            var rgba = new byte[bmp.Width * bmp.Height * 4];
            pixels.CopyTo(rgba);

            return new Texture2D(bmp.Width, bmp.Height, rgba);
        }
    }

    public sealed class MaterialTexture
    {
        [Persist] public string Name { get; set; }
        [JsonIgnore] public Texture2D Texture { get; set; }
        [JsonIgnore] public IImage Preview { get; set; }
        [Persist] public string SourcePath { get; set; }

        /// <summary>When true and the texture is larger than 512px, a GPU-compressed .dds is built next to the source on first upload.</summary>
        [Persist] public bool CompressTextures { get; set; } = true;

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
        /// <summary>When &gt; 0, fragments with albedo alpha below this are discarded in the opaque pass (foliage cutout). Transparent materials use the cutoff from the renderer (low threshold).</summary>
        [Persist] public float AlphaCutoff { get; set; } = 0f;

        /// <summary>When &gt; 0, opaque pass also discards very dark albedo texels (RGB-only leaf sheets with no alpha channel).</summary>
        [Persist] public float LumaClip { get; set; } = 0f;

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
        // ── Emissive ──
        /// <summary>Emissive color (self-illumination, not affected by lighting).</summary>
        [Persist] public Color EmissiveColor { get; set; } = Colors.Black;
        /// <summary>Emissive intensity multiplier (0 = no glow).</summary>
        [Persist] public float EmissiveIntensity { get; set; } = 0f;
        // ── Normal map ──
        /// <summary>Normal map strength (0 = flat, 1 = full normal map effect).</summary>
        [Persist] public float NormalStrength { get; set; } = 1f;

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
                LumaClip = this.LumaClip,
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
