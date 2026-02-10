#nullable enable
using System;
using Silk.NET.OpenGL;
using Silk.NET.Core.Contexts;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Wraps a Silk.NET GL instance created from Avalonia's OpenGL context.
/// Lifetime is tied to the viewport that owns it.
/// </summary>
public sealed class GLContext : IDisposable
{
    public GL GL { get; }

    /// <summary>OpenGL version detected at init time (e.g. "3.3").</summary>
    public string VersionString { get; }

    /// <summary>True when running on an OpenGL ES context (ANGLE on Windows).</summary>
    public bool IsES { get; }

    /// <summary>
    /// Create from Avalonia's GlInterface (call inside OnOpenGlInit).
    /// </summary>
    public GLContext(Func<string, IntPtr> getProcAddress)
    {
        GL = GL.GetApi(new AvaloniaGLNativeContext(getProcAddress));

        var versionStr = GL.GetStringS(StringName.Version);
        VersionString = versionStr ?? "unknown";

        IsES = VersionString.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check for GL errors and throw if any are found (debug helper).
    /// </summary>
    public void CheckError(string label = "")
    {
        var err = GL.GetError();
        if (err != GLEnum.NoError)
            System.Diagnostics.Debug.WriteLine($"[GL ERROR] {label}: {err}");
    }

    public void Dispose()
    {
        GL.Dispose();
    }

    // Silk.NET native context adapter for Avalonia's GetProcAddress
    private sealed class AvaloniaGLNativeContext : INativeContext
    {
        private readonly Func<string, IntPtr> _getProcAddress;

        public AvaloniaGLNativeContext(Func<string, IntPtr> getProcAddress)
        {
            _getProcAddress = getProcAddress;
        }

        public nint GetProcAddress(string proc, int? slot = null)
        {
            return _getProcAddress(proc);
        }

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        {
            addr = _getProcAddress(proc);
            return addr != IntPtr.Zero;
        }

        public void Dispose() { }
    }
}
