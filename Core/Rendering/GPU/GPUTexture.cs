#nullable enable
using System;
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

        // Default wrap: repeat
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
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

    public void Dispose()
    {
        _gl.DeleteTexture(Handle);
        Handle = 0;
    }
}
