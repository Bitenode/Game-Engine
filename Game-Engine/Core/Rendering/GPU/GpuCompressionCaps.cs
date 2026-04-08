#nullable enable
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>Runtime probe for compressed texture upload support (S3TC / BPTC).</summary>
public static class GpuCompressionCaps
{
    public static bool Initialized { get; private set; }
    public static bool S3tc { get; private set; }
    public static bool Bptc { get; private set; }

    public static void Initialize(GL gl)
    {
        if (Initialized) return;
        Initialized = true;
        S3tc = ProbeCompressedFormat(gl, GlCompressedRgbS3tcDxt1Ext, 4, 4, 8);
        Bptc = ProbeCompressedFormat(gl, GlCompressedRgbaBptcUnorm, 4, 4, 16);
    }

    // GL_COMPRESSED_RGBA_S3TC_DXT1_EXT (BC1 with 1-bit alpha / punch-through)
    internal const InternalFormat GlCompressedRgbaS3tcDxt1Ext = (InternalFormat)0x83F0;
    // GL_COMPRESSED_RGB_S3TC_DXT1_EXT (opaque BC1)
    internal const InternalFormat GlCompressedRgbS3tcDxt1Ext = (InternalFormat)0x83F1;
    // GL_COMPRESSED_RGBA_S3TC_DXT3_EXT (BC2 / DXT3)
    internal const InternalFormat GlCompressedRgbaS3tcDxt3Ext = (InternalFormat)0x83F2;
    // GL_COMPRESSED_RGBA_S3TC_DXT5_EXT (BC3)
    internal const InternalFormat GlCompressedRgbaS3tcDxt5Ext = (InternalFormat)0x83F3;
    // GL_COMPRESSED_RED_RGTC1 / BC4
    internal const InternalFormat GlCompressedRedRgtc1 = (InternalFormat)0x8DBB;
    // GL_COMPRESSED_RG_RGTC2 (BC5)
    internal const InternalFormat GlCompressedRgRgtc2 = (InternalFormat)0x8DBD;
    // GL_COMPRESSED_RGBA_BPTC_UNORM (BC7)
    internal const InternalFormat GlCompressedRgbaBptcUnorm = (InternalFormat)0x8E8C;

    static unsafe bool ProbeCompressedFormat(GL gl, InternalFormat fmt, uint w, uint h, int sizeBytes)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        var data = stackalloc byte[64];
        gl.GetError(); // clear
        gl.CompressedTexImage2D(TextureTarget.Texture2D, 0, fmt, w, h, 0, (uint)sizeBytes, data);
        bool ok = gl.GetError() == GLEnum.NoError;
        gl.DeleteTexture(tex);
        return ok;
    }
}
