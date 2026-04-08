#nullable enable
using System;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using Game_Engine.Core.Rendering.GPU;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;
using Silk.NET.OpenGL;

using Game_Engine.Core;

namespace Game_Engine.Core.Rendering;

/// <summary>Builds and loads sidecar .dds files next to source images using BCnEncoder.Net.</summary>
public static class TextureBcCompression
{
    public static bool ShouldCompressMaterialSlot(MaterialTexture slot, Texture2D tex)
    {
        if (!slot.CompressTextures) return false;
        if (tex.Width <= 512 && tex.Height <= 512) return false;
        return true;
    }

    public static CompressionFormat ChooseFormat(MaterialTexture.TexUsage usage, bool hasAlpha, bool bptc)
    {
        return usage switch
        {
            MaterialTexture.TexUsage.Normal => CompressionFormat.Bc5,
            MaterialTexture.TexUsage.Metallic or MaterialTexture.TexUsage.Roughness
                or MaterialTexture.TexUsage.Specular or MaterialTexture.TexUsage.AmbientOcclusion
                or MaterialTexture.TexUsage.Opacity => CompressionFormat.Bc1,
            _ => bptc
                ? CompressionFormat.Bc7
                : (hasAlpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1)
        };
    }

    /// <summary>True if any pixel has alpha &lt; 255.</summary>
    public static bool HasMeaningfulAlpha(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int n = width * height * 4;
        for (int i = 3; i < n; i += 4)
        {
            if (rgba[i] < 255) return true;
        }
        return false;
    }

    public static string GetSidecarDdsPath(string absoluteImagePath)
        => Path.ChangeExtension(absoluteImagePath, ".dds");

    /// <summary>Encode to sidecar .dds if needed. Returns false to fall back to RGBA upload.</summary>
    public static bool TryEnsureSidecarDds(
        string absoluteImagePath,
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        CompressionFormat format)
    {
        try
        {
            string ddsPath = GetSidecarDdsPath(absoluteImagePath);
            if (File.Exists(ddsPath) && File.Exists(absoluteImagePath))
            {
                if (File.GetLastWriteTimeUtc(ddsPath) >= File.GetLastWriteTimeUtc(absoluteImagePath))
                    return true;
            }

            if (!GpuCompressionCaps.S3tc && format is CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc3 or CompressionFormat.Bc4 or CompressionFormat.Bc5)
                return false;
            var encFormat = format;
            if (encFormat == CompressionFormat.Bc7 && !GpuCompressionCaps.Bptc)
                encFormat = HasMeaningfulAlpha(rgba, width, height) ? CompressionFormat.Bc3 : CompressionFormat.Bc1;

            var enc = new BCnEncoder.Encoder.BcEncoder(encFormat);
            var dds = enc.EncodeToDds(rgba.ToArray(), width, height, BcPixelFormat.Rgba32);
            using var fs = File.Create(ddsPath);
            dds.Write(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoadDdsForGpu(string ddsPath, out DdsFile? file, out CompressionFormat format)
    {
        file = null;
        format = default;
        try
        {
            if (!File.Exists(ddsPath)) return false;
            using var fs = File.OpenRead(ddsPath);
            var dds = DdsFile.Load(fs);
            var dec = new BcDecoder();
            format = dec.GetFormat(dds);
            file = dds;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static InternalFormat ToGlInternalFormat(CompressionFormat fmt) => fmt switch
    {
        // BC1 without alpha: RGB DXT1. BC1 with punch-through alpha must use RGBA DXT1 (different block interpretation).
        CompressionFormat.Bc1 => GpuCompressionCaps.GlCompressedRgbS3tcDxt1Ext,
        CompressionFormat.Bc1WithAlpha => GpuCompressionCaps.GlCompressedRgbaS3tcDxt1Ext,
        CompressionFormat.Bc2 => GpuCompressionCaps.GlCompressedRgbaS3tcDxt3Ext,
        CompressionFormat.Bc3 => GpuCompressionCaps.GlCompressedRgbaS3tcDxt5Ext,
        CompressionFormat.Bc4 => GpuCompressionCaps.GlCompressedRedRgtc1,
        CompressionFormat.Bc5 => GpuCompressionCaps.GlCompressedRgRgtc2,
        CompressionFormat.Bc7 => GpuCompressionCaps.GlCompressedRgbaBptcUnorm,
        _ => (InternalFormat)0
    };

    /// <summary>Expected byte size for one mip level (4×4 blocks).</summary>
    public static int ExpectedCompressedMipSize(int width, int height, CompressionFormat fmt)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        int blocksW = (w + 3) / 4;
        int blocksH = (h + 3) / 4;
        int bpb = fmt switch
        {
            CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 => 8,
            CompressionFormat.Bc2 or CompressionFormat.Bc3 or CompressionFormat.Bc5 or CompressionFormat.Bc7 => 16,
            _ => 0
        };
        return blocksW * blocksH * bpb;
    }

    public static string? ResolveAbsoluteImagePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (Path.IsPathRooted(sourcePath)) return Path.GetFullPath(sourcePath);
        var root = ProjectService.Current?.RootPath;
        if (string.IsNullOrEmpty(root)) return null;
        return Path.GetFullPath(Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static bool IsFormatUploadSupported(CompressionFormat fmt) => fmt switch
    {
        CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc2 or CompressionFormat.Bc3
            or CompressionFormat.Bc4 or CompressionFormat.Bc5 => GpuCompressionCaps.S3tc,
        CompressionFormat.Bc7 => GpuCompressionCaps.Bptc,
        _ => false
    };
}
