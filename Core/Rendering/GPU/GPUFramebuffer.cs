#nullable enable
using System;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// FBO wrapper for off-screen rendering (shadow maps, post-processing).
/// Supports depth-only and color+depth attachments.
/// </summary>
public sealed class GPUFramebuffer : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public GPUTexture? DepthTexture { get; private set; }
    public GPUTexture? ColorTexture { get; private set; }

    public GPUFramebuffer(GL gl)
    {
        _gl = gl;
        Handle = _gl.GenFramebuffer();
    }

    /// <summary>
    /// Configure as a depth-only FBO for shadow mapping.
    /// </summary>
    public void SetupDepthOnly(int width, int height)
    {
        Width = width;
        Height = height;

        // Create depth texture
        DepthTexture?.Dispose();
        DepthTexture = new GPUTexture(_gl);
        DepthTexture.CreateDepth(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, DepthTexture.Handle, 0);

        // No color buffer — use DrawBuffers (plural) which works on both
        // desktop GL and GLES 3.0.  glDrawBuffer (singular) doesn't exist in ES.
        unsafe
        {
            DrawBufferMode none = DrawBufferMode.None;
            _gl.DrawBuffers(1, &none);
        }
        try { _gl.ReadBuffer(ReadBufferMode.None); } catch { /* GLES may not support this */ }

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            System.Diagnostics.Debug.WriteLine($"[GPUFramebuffer] Depth FBO incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Bind this FBO as the render target.</summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    /// <summary>Unbind (restore default framebuffer 0 or a specific one).</summary>
    public void Unbind(uint defaultFB = 0)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, defaultFB);
    }

    public void Dispose()
    {
        DepthTexture?.Dispose();
        ColorTexture?.Dispose();
        _gl.DeleteFramebuffer(Handle);
        Handle = 0;
    }
}
