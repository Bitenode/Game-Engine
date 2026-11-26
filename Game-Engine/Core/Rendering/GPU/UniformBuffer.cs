#nullable enable
using System;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Zero-allocation wrapper around an OpenGL Uniform Buffer Object (<c>GL_UNIFORM_BUFFER</c>)
/// holding a single <c>std140</c>-aligned unmanaged struct <typeparamref name="T"/>.
/// Compatible with OpenGL 3.3 Core and OpenGL ES 3.0 / ANGLE.
/// </summary>
public sealed class UniformBuffer<T> : IDisposable where T : unmanaged
{
    private readonly GL _gl;
    private bool _disposed;

    public uint Handle { get; private set; }
    public int SizeBytes { get; }

    public unsafe UniformBuffer(GL gl)
    {
        _gl = gl;
        SizeBytes = Unsafe.SizeOf<T>();
        Handle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, Handle);
        _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)SizeBytes, null, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
    }

    /// <summary>Upload the full struct into the UBO via <c>glBufferSubData</c>.</summary>
    public unsafe void Update(in T data)
    {
        if (_disposed || Handle == 0) return;
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, Handle);
        fixed (T* ptr = &data)
        {
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)SizeBytes, ptr);
        }
    }

    /// <summary>Bind this UBO to the specified uniform buffer binding point.</summary>
    public void Bind(uint bindingPoint)
    {
        if (_disposed || Handle == 0) return;
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, bindingPoint, Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0)
        {
            _gl.DeleteBuffer(Handle);
            Handle = 0;
        }
    }
}

/// <summary>
/// Zero-allocation Uniform Buffer Object for skeletal bone matrices (<c>mat4 uBones[MAX_BONES]</c>).
/// Allocates capacity for <see cref="MaxBones"/> matrices (128 on Desktop GL, 48 on OpenGL ES)
/// and uploads only <c>count * 64</c> bytes per skinned draw call.
/// </summary>
public sealed class BoneUniformBuffer : IDisposable
{
    private readonly GL _gl;
    private bool _disposed;

    public uint Handle { get; private set; }
    public int MaxBones { get; }
    public int CapacityBytes { get; }

    public unsafe BoneUniformBuffer(GL gl, int maxBones)
    {
        _gl = gl;
        MaxBones = Math.Max(1, maxBones);
        CapacityBytes = MaxBones * 64; // sizeof(Matrix4x4) == 64 bytes
        Handle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, Handle);
        _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)CapacityBytes, null, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
    }

    /// <summary>
    /// Upload a span of bone matrices (up to <see cref="MaxBones"/>) with zero heap allocations.
    /// </summary>
    public unsafe void UpdateMatrices(ReadOnlySpan<SN.Matrix4x4> matrices)
    {
        if (_disposed || Handle == 0 || matrices.IsEmpty) return;
        int count = Math.Min(matrices.Length, MaxBones);
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, Handle);
        fixed (SN.Matrix4x4* ptr = matrices)
        {
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)(count * 64), ptr);
        }
    }

    /// <summary>Bind this bone UBO to the specified uniform buffer binding point.</summary>
    public void Bind(uint bindingPoint)
    {
        if (_disposed || Handle == 0) return;
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, bindingPoint, Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0)
        {
            _gl.DeleteBuffer(Handle);
            Handle = 0;
        }
    }
}
