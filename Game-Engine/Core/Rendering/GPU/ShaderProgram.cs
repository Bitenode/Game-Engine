#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Compiles and links a GLSL vertex + fragment shader pair.
/// Caches uniform locations for efficient per-frame updates.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; }
    private readonly Dictionary<string, int> _uniformCache = new();

    /// <summary>
    /// Folder for linked program binaries, or null to always compile from source. On ANGLE every
    /// link runs the HLSL compiler, which took about 37 s for the player's shader set, so the
    /// Xbox host points this at its local cache folder. A binary the driver rejects is recompiled.
    /// </summary>
    public static string? BinaryCacheDirectory { get; set; }

    static string? s_driverTag;
    static string? s_angleStamp;

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        string? cachePath = BinaryCachePath(vertexSource, fragmentSource);
        if (cachePath != null && TryLoadBinary(cachePath, out uint cached))
        {
            Handle = cached;
            return;
        }

        uint vert = CompileShader(ShaderType.VertexShader, vertexSource);
        uint frag = CompileShader(ShaderType.FragmentShader, fragmentSource);

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
            throw new Exception($"Shader link error: {log}");
        }

        _gl.DetachShader(Handle, vert);
        _gl.DetachShader(Handle, frag);
        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);

        if (cachePath != null)
            TrySaveBinary(cachePath);
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

    public void Use() => _gl.UseProgram(Handle);

    public int GetUniformLocation(string name)
    {
        if (_uniformCache.TryGetValue(name, out int loc))
            return loc;
        loc = _gl.GetUniformLocation(Handle, name);
        _uniformCache[name] = loc;
        return loc;
    }

    public void SetInt(string name, int value)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetFloat(string name, float value)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetVector3(string name, SN.Vector3 value)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform3(loc, value.X, value.Y, value.Z);
    }

    public void SetVector2(string name, float x, float y)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform2(loc, x, y);
    }

    public void SetVector4(string name, float x, float y, float z, float w)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform4(loc, x, y, z, w);
    }

    public unsafe void SetMatrix4(string name, in SN.Matrix4x4 mat)
    {
        int loc = GetUniformLocation(name);
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

    public void SetTexture(string name, int unit)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, unit);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new Exception($"Shader compile error ({type}): {log}");
        }
        return shader;
    }

    public void Dispose()
    {
        _gl.DeleteProgram(Handle);
    }
}
