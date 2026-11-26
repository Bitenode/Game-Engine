#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Compiles and links a GLSL vertex + fragment shader pair.
/// Caches uniform locations for efficient per-frame updates, automatically binds
/// standard UBO blocks (<c>FrameGlobals</c>, <c>LightingGlobals</c>, <c>BoneGlobals</c>),
/// and tracks the currently bound program per <see cref="GL"/> context to skip redundant <c>glUseProgram</c> calls.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    private sealed class CurrentProgramBox
    {
        public uint Handle;
    }

    private static readonly ConditionalWeakTable<GL, CurrentProgramBox> s_boundPrograms = new();

    private readonly GL _gl;
    private readonly CurrentProgramBox _boundProgram;
    private bool _disposed;
    internal GL GL => _gl;
    public uint Handle { get; }
    public string? DebugLabel { get; }
    private readonly Dictionary<string, int> _uniformCache = new();

    /// <summary>True when this program declares the <c>FrameGlobals</c> UBO block (binding 0).</summary>
    public bool HasFrameUniformBlock { get; private set; }

    /// <summary>True when this program declares the <c>LightingGlobals</c> UBO block (binding 1).</summary>
    public bool HasLightingUniformBlock { get; private set; }

    /// <summary>True when this program declares the <c>BoneGlobals</c> UBO block (binding 2).</summary>
    public bool HasBoneUniformBlock { get; private set; }

    /// <summary>
    /// Notify the per-context program tracker that raw <c>glUseProgram(0)</c> (or external GL code
    /// such as Avalonia's compositor) unbound the active program.
    /// </summary>
    public static void NotifyProgramUnbound(GL gl)
    {
        if (s_boundPrograms.TryGetValue(gl, out var box))
            box.Handle = 0;
    }

    /// <summary>
    /// Folder for linked program binaries, or null to always compile from source. On ANGLE every
    /// link runs the HLSL compiler, which took about 37 s for the player's shader set, so the
    /// Xbox host points this at its local cache folder. A binary the driver rejects is recompiled.
    /// </summary>
    public static string? BinaryCacheDirectory { get; set; }

    static string? s_driverTag;
    static string? s_angleStamp;

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
        : this(gl, vertexSource, fragmentSource, null, null, null)
    {
    }

    public ShaderProgram(
        GL gl,
        string vertexSource,
        string fragmentSource,
        ShaderSourceMap? vertexSourceMap,
        ShaderSourceMap? fragmentSourceMap,
        string? debugLabel = null)
    {
        _gl = gl;
        _boundProgram = s_boundPrograms.GetOrCreateValue(gl);
        DebugLabel = debugLabel;

        string? cachePath = BinaryCachePath(vertexSource, fragmentSource);
        if (cachePath != null && TryLoadBinary(cachePath, out uint cached))
        {
            Handle = cached;
            BindStandardUniformBlocks();
            return;
        }

        uint vert = CompileShader(ShaderType.VertexShader, vertexSource, vertexSourceMap);
        uint frag;
        try
        {
            frag = CompileShader(ShaderType.FragmentShader, fragmentSource, fragmentSourceMap);
        }
        catch
        {
            _gl.DeleteShader(vert);
            throw;
        }

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vert);
        _gl.AttachShader(Handle, frag);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetProgramInfoLog(Handle);
            _gl.DeleteProgram(Handle);
            _gl.DeleteShader(vert);
            _gl.DeleteShader(frag);
            string tag = string.IsNullOrEmpty(DebugLabel) ? "" : $" [{DebugLabel}]";
            throw new Exception($"Shader link error{tag}: {log}");
        }

        _gl.DetachShader(Handle, vert);
        _gl.DetachShader(Handle, frag);
        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);

        BindStandardUniformBlocks();

        if (cachePath != null)
            TrySaveBinary(cachePath);
    }

    /// <summary>
    /// Bind standard std140 UBO blocks by name for OpenGL 3.3 Core &amp; OpenGL ES 3.0 compatibility
    /// (<c>layout(std140, binding = N)</c> is invalid in GLSL 330 core / 300 es).
    /// Must be called after BOTH fresh linking and <c>glProgramBinary</c> loading.
    /// </summary>
    private void BindStandardUniformBlocks()
    {
        const uint InvalidIndex = 0xFFFFFFFFu;

        uint frameIdx = _gl.GetUniformBlockIndex(Handle, "FrameGlobals");
        if (frameIdx != InvalidIndex)
        {
            _gl.UniformBlockBinding(Handle, frameIdx, GlobalUniforms.FrameBindingPoint);
            HasFrameUniformBlock = true;
        }

        uint lightIdx = _gl.GetUniformBlockIndex(Handle, "LightingGlobals");
        if (lightIdx != InvalidIndex)
        {
            _gl.UniformBlockBinding(Handle, lightIdx, GlobalUniforms.LightingBindingPoint);
            HasLightingUniformBlock = true;
        }

        uint boneIdx = _gl.GetUniformBlockIndex(Handle, "BoneGlobals");
        if (boneIdx != InvalidIndex)
        {
            _gl.UniformBlockBinding(Handle, boneIdx, GlobalUniforms.BoneBindingPoint);
            HasBoneUniformBlock = true;
        }
    }

    string? BinaryCachePath(string vertexSource, string fragmentSource)
    {
        var dir = BinaryCacheDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            // A binary is only valid for the driver that produced it. Debug and release
            // ANGLE report the same GL_VERSION / RENDERER, so the on-disk libGLESv2
            // stamp must be part of the key — otherwise a DLL swap reuses bad binaries
            // and DrawElements can AV (ExecutionEngineException).
            s_driverTag ??= (_gl.GetStringS(StringName.Renderer) ?? "") + "|" + (_gl.GetStringS(StringName.Version) ?? "");
            s_angleStamp ??= AngleModuleStamp();
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                s_driverTag + "\0" + s_angleStamp + "\0" + vertexSource + "\0" + fragmentSource);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Path.Combine(dir, Convert.ToHexString(hash, 0, 16) + ".glbin");
        }
        catch { return null; }
    }

    static string AngleModuleStamp()
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (!module.ModuleName.Equals("libGLESv2.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                long length = 0;
                long ticks = 0;
                try
                {
                    var info = new FileInfo(module.FileName);
                    length = info.Length;
                    ticks = info.LastWriteTimeUtc.Ticks;
                }
                catch { }
                return length.ToString() + "|" + ticks.ToString();
            }
        }
        catch { }
        return "unknown";
    }

    unsafe bool TryLoadBinary(string path, out uint program)
    {
        program = 0;
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            var data = System.IO.File.ReadAllBytes(path);
            if (data.Length <= 4) return false;
            var format = (GLEnum)BitConverter.ToUInt32(data, 0);

            while (_gl.GetError() != GLEnum.NoError) { }
            uint p = _gl.CreateProgram();
            fixed (byte* ptr = &data[4])
                _gl.ProgramBinary(p, format, ptr, (uint)(data.Length - 4));
            _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int status);
            if (status == 0 || _gl.GetError() != GLEnum.NoError)
            {
                _gl.DeleteProgram(p);
                try { System.IO.File.Delete(path); } catch { }
                return false;
            }
            program = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    unsafe void TrySaveBinary(string path)
    {
        try
        {
            while (_gl.GetError() != GLEnum.NoError) { }
            _gl.GetProgram(Handle, (ProgramPropertyARB)0x8741 /* GL_PROGRAM_BINARY_LENGTH */, out int length);
            if (length <= 0) return;
            var data = new byte[length + 4];
            uint written = 0;
            GLEnum format = 0;
            fixed (byte* ptr = &data[4])
                _gl.GetProgramBinary(Handle, (uint)length, &written, &format, ptr);
            if (written == 0 || _gl.GetError() != GLEnum.NoError) return;
            BitConverter.TryWriteBytes(new Span<byte>(data, 0, 4), (uint)format);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = System.IO.File.Create(tmp))
                fs.Write(data, 0, (int)written + 4);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The cache is an optimization. A failed write only costs the next launch a recompile.
        }
    }

    public void Use()
    {
        if (_boundProgram.Handle == Handle) return;
        _gl.UseProgram(Handle);
        _boundProgram.Handle = Handle;
    }

    public int GetUniformLocation(string name)
    {
        if (_uniformCache.TryGetValue(name, out int loc))
            return loc;
        loc = _gl.GetUniformLocation(Handle, name);
        _uniformCache[name] = loc;
        return loc;
    }

    public void SetInt(string name, int value) => SetInt(GetUniformLocation(name), value);

    public void SetInt(int loc, int value)
    {
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetFloat(string name, float value) => SetFloat(GetUniformLocation(name), value);

    public void SetFloat(int loc, float value)
    {
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetVector3(string name, SN.Vector3 value) => SetVector3(GetUniformLocation(name), value);

    public void SetVector3(int loc, SN.Vector3 value)
    {
        if (loc >= 0) _gl.Uniform3(loc, value.X, value.Y, value.Z);
    }

    public void SetVector2(string name, float x, float y) => SetVector2(GetUniformLocation(name), x, y);

    public void SetVector2(int loc, float x, float y)
    {
        if (loc >= 0) _gl.Uniform2(loc, x, y);
    }

    public void SetVector4(string name, float x, float y, float z, float w)
        => SetVector4(GetUniformLocation(name), x, y, z, w);

    public void SetVector4(int loc, float x, float y, float z, float w)
    {
        if (loc >= 0) _gl.Uniform4(loc, x, y, z, w);
    }

    public void SetMatrix4(string name, in SN.Matrix4x4 mat) => SetMatrix4(GetUniformLocation(name), in mat);

    public unsafe void SetMatrix4(int loc, in SN.Matrix4x4 mat)
    {
        if (loc < 0) return;
        // System.Numerics.Matrix4x4 is row-major; passing with transpose=false
        // makes GL read the data as column-major, which effectively gives us
        // the transposed matrix. GLSL's mat * vec then matches System.Numerics' vec * mat.
        // NOTE: transpose=true is INVALID on OpenGL ES and would silently fail.
        fixed (SN.Matrix4x4* ptr = &mat)
        {
            _gl.UniformMatrix4(loc, 1, false, (float*)ptr);
        }
    }

    public unsafe void SetMatrix4Array(string name, ReadOnlySpan<SN.Matrix4x4> matrices)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) SetMatrix4Array(loc, matrices);
    }

    public unsafe void SetMatrix4Array(int loc, ReadOnlySpan<SN.Matrix4x4> matrices)
    {
        if (loc < 0 || matrices.IsEmpty) return;
        fixed (SN.Matrix4x4* ptr = matrices)
        {
            _gl.UniformMatrix4(loc, (uint)matrices.Length, false, (float*)ptr);
        }
    }

    public void SetTexture(string name, int unit) => SetTexture(GetUniformLocation(name), unit);

    public void SetTexture(int loc, int unit)
    {
        if (loc >= 0) _gl.Uniform1(loc, unit);
    }

    private uint CompileShader(ShaderType type, string source, ShaderSourceMap? sourceMap)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            if (sourceMap != null)
                log = sourceMap.FormatErrorLog(log);
            string tag = string.IsNullOrEmpty(DebugLabel) ? "" : $" [{DebugLabel}]";
            throw new Exception($"Shader compile error ({type}){tag}: {log}");
        }
        return shader;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_boundProgram.Handle == Handle)
            _boundProgram.Handle = 0;
        if (Handle != 0)
            _gl.DeleteProgram(Handle);
    }
}
