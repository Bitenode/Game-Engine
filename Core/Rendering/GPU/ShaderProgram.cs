#nullable enable
using System;
using System.Collections.Generic;
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

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

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
