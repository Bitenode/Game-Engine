#nullable enable
using System;
using System.Diagnostics;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using Game_Engine.Core.Rendering;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Wraps an OpenGL 2D texture. Uploads Texture2D RGBA data to GPU memory.
/// </summary>
public sealed class GPUTexture : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public GPUTexture(GL gl)
    {
        _gl = gl;
        Handle = _gl.GenTexture();
    }

    /// <summary>
    /// Upload engine Texture2D data (RGBA byte array) to GPU.
    /// Generates mipmaps automatically.
    /// </summary>
    public unsafe void Upload(Texture2D tex)
    {
        if (tex == null || tex.Rgba == null) return;

        Width = tex.Width;
        Height = tex.Height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        fixed (byte* ptr = tex.Rgba)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.Rgba8,
                (uint)Width, (uint)Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }

        _gl.GenerateMipmap(TextureTarget.Texture2D);

        // Default filtering: trilinear
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

        // Anisotropic filtering (critical for oblique views on planet terrain)
        const TextureParameterName GL_TEXTURE_MAX_ANISOTROPY = (TextureParameterName)0x84FE;
        try { _gl.TexParameter(TextureTarget.Texture2D, GL_TEXTURE_MAX_ANISOTROPY, 16.0f); }
        catch { }

        // Default wrap: repeat
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Upload BCn-compressed mips from a DDS face. Returns false on size/GL mismatch (caller should use RGBA path).
    /// <paramref name="stripSidecar"/> is true when DDS mip sizes do not match BCn layout — safe to delete sidecar and re-encode.
    /// </summary>
    public unsafe bool TryUploadCompressedFromDds(DdsFile dds, CompressionFormat bcFormat, out bool stripSidecar)
    {
        stripSidecar = false;
        if (dds.Faces.Count == 0 || dds.Faces[0].MipMaps.Length == 0) return false;
        var face = dds.Faces[0];
        var mips = face.MipMaps;
        var mip0 = mips[0];

        var glFmt = TextureBcCompression.ToGlInternalFormat(bcFormat);
        if ((uint)glFmt == 0) return false;

        for (int level = 0; level < mips.Length; level++)
        {
            var mip = mips[level];
            int expected = TextureBcCompression.ExpectedCompressedMipSize((int)mip.Width, (int)mip.Height, bcFormat);
            if (expected <= 0 || mip.Data.Length != expected)
            {
                stripSidecar = true;
                Debug.WriteLine($"[GPUTexture] BCn mip {level} size mismatch: got {mip.Data.Length}, expected {expected} for {mip.Width}x{mip.Height} fmt={bcFormat}");
                return false;
            }
        }

        Width = (int)mip0.Width;
        Height = (int)mip0.Height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        while (_gl.GetError() != GLEnum.NoError) { }

        for (int level = 0; level < mips.Length; level++)
        {
            var mip = mips[level];
            fixed (byte* p = mip.Data)
            {
                _gl.CompressedTexImage2D(TextureTarget.Texture2D, level, glFmt,
                    mip.Width, mip.Height, 0, (uint)mip.Data.Length, p);
            }
            if (_gl.GetError() != GLEnum.NoError)
            {
                Debug.WriteLine($"[GPUTexture] glCompressedTexImage2D failed level={level} internal={glFmt} {mip.Width}x{mip.Height}");
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                return false;
            }
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            mips.Length > 1 ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        const TextureParameterName GL_TEXTURE_MAX_ANISOTROPY = (TextureParameterName)0x84FE;
        try { _gl.TexParameter(TextureTarget.Texture2D, GL_TEXTURE_MAX_ANISOTROPY, 16.0f); }
        catch { }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    /// <summary>
    /// Create a depth-only texture for shadow mapping.
    /// </summary>
    public unsafe void CreateDepth(int width, int height)
    {
        Width = width;
        Height = height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.DepthComponent24,
            (uint)width, (uint)height, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToBorder);

        // Border color = 1.0 (far) so pixels outside shadow map are never in shadow
        float* border = stackalloc float[4] { 1f, 1f, 1f, 1f };
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Depth + stencil for G-buffer (DEPTH24_STENCIL8).</summary>
    public unsafe void CreateDepthStencil24(int width, int height)
    {
        Width = width;
        Height = height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.Depth24Stencil8,
            (uint)width, (uint)height, 0,
            PixelFormat.DepthStencil, PixelType.UnsignedInt248, null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToBorder);
        float* border = stackalloc float[4] { 1f, 1f, 1f, 1f };
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Create an RGBA8 color texture (for off-screen FBO rendering).
    /// </summary>
    public unsafe void CreateColor(int width, int height)
    {
        Width = width;
        Height = height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.Rgba8,
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Create an RGBA16F color texture (for G-buffer normal+roughness with higher precision).
    /// </summary>
    public unsafe void CreateColorFloat16(int width, int height)
    {
        Width = width;
        Height = height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.Rgba16f,
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.HalfFloat, null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Upload RGBA float data to GPU (for splatmap textures).
    /// Data length must be width * height * 4 floats.
    /// </summary>
    public unsafe void UploadFloat(float[] data, int width, int height)
    {
        if (data == null || data.Length < width * height * 4) return;
        Width = width;
        Height = height;

        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        fixed (float* ptr = data)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.Rgba32f,
                (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.Float, ptr);
        }

        // No mipmaps for splatmaps — linear filtering only
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    /// <summary>Normalized R8 atlases (tile meta / light indices), GLES-friendly.</summary>
    public unsafe void CreateR8UNorm(int width, int height)
    {
        Width = width;
        Height = height;
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.R8,
            (uint)width, (uint)height, 0,
            PixelFormat.Red, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public unsafe void UploadR8UNormData(ReadOnlySpan<byte> data)
    {
        if (data.Length < Width * Height) return;
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* p = data)
        {
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                (uint)Width, (uint)Height,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public unsafe void CreateRgbaFloat32(int width, int height)
    {
        Width = width;
        Height = height;
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.Rgba32f,
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Upload RGBA32F texels in column-major order: width lights × height rows (4 rows).</summary>
    public unsafe void UploadRgbaFloatGridColumns(float[] src16PerLight, int lightCount)
    {
        if (lightCount <= 0 || src16PerLight.Length < lightCount * 16) return;
        var dst = new float[lightCount * 4 * 4];
        for (int i = 0; i < lightCount; i++)
        {
            int b = i * 16;
            for (int row = 0; row < 4; row++)
            {
                for (int c = 0; c < 4; c++)
                    dst[(row * lightCount + i) * 4 + c] = src16PerLight[b + row * 4 + c];
            }
        }
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (float* p = dst)
        {
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                (uint)lightCount, 4,
                PixelFormat.Rgba, PixelType.Float, p);
        }
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Allocate an RGBA8 cubemap (empty). Faces are size×size.</summary>
    public unsafe void CreateCubemapRgba8(int faceSize)
    {
        Width = faceSize;
        Height = faceSize;
        _gl.BindTexture(TextureTarget.TextureCubeMap, Handle);
        for (int i = 0; i < 6; i++)
        {
            var target = TextureTarget.TextureCubeMapPositiveX + i;
            _gl.TexImage2D(target, 0, InternalFormat.Rgba8,
                (uint)faceSize, (uint)faceSize, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
        _gl.GenerateMipmap(TextureTarget.TextureCubeMap);
        _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
    }

    public void BindCubemap(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureCubeMap, Handle);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(Handle);
        Handle = 0;
    }
}
