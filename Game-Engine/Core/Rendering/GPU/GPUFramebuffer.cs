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

    /// <summary>
    /// Configure as a color+depth FBO for off-screen rendering (e.g., half-resolution).
    /// NOTE: Leaves the framebuffer bound to this FBO.  Callers typically call Bind()
    /// immediately after, so this avoids a pointless unbind→rebind round-trip.
    /// If you need to unbind, call Unbind(avaloniaFB) explicitly.
    /// </summary>
    public void SetupColorDepth(int width, int height)
    {
        Width = width;
        Height = height;

        // Color texture (RGBA8)
        ColorTexture?.Dispose();
        ColorTexture = new GPUTexture(_gl);
        ColorTexture.CreateColor(width, height);

        // Depth renderbuffer — we don't need to sample depth, just need it for Z-test
        DepthTexture?.Dispose();
        DepthTexture = new GPUTexture(_gl);
        DepthTexture.CreateDepth(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ColorTexture.Handle, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, DepthTexture.Handle, 0);

        // Explicitly set draw buffer — prevents inheriting DrawBuffers(None)
        // from a previously bound depth-only FBO (shadow map).
        unsafe
        {
            DrawBufferMode color0 = DrawBufferMode.ColorAttachment0;
            _gl.DrawBuffers(1, &color0);
        }

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            System.Diagnostics.Debug.WriteLine($"[GPUFramebuffer] Color+Depth FBO incomplete: {status}");

        // Don't unbind to FB 0 — in Avalonia's shared GL context, FB 0 is NOT the
        // screen.  The caller should bind the correct target after setup.
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
