#nullable enable
using System;
using Silk.NET.OpenGL;
using Profiler = Game_Engine.Core.Profiler;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Per-GL-context dynamic GPU-instanced particle batcher.
/// Owns a static billboard quad VBO, a growable dynamic instance VBO, a VAO, and a
/// reusable CPU float staging buffer so an emitter can render thousands of particles
/// in a single <c>glDrawArraysInstanced</c> call with zero per-frame allocations.
///
/// Interleaved per-instance layout (12 floats = 48 bytes):
///   location 1: vec4 aInstPosSize  (xyz = world position, w = size)
///   location 2: vec4 aInstColor    (rgba color)
///   location 3: vec4 aInstParams   (x = rotation radians, y = normalized age 0..1, z = flipbook cols, w = flipbook rows)
/// </summary>
public sealed class GPUParticleBatcher : IDisposable
{
    public const int FloatsPerInstance = 12;
    public const int BytesPerInstance = FloatsPerInstance * sizeof(float);

    private readonly GL _gl;
    private uint _vao;
    private uint _quadVbo;
    private uint _instanceVbo;
    private int _gpuCapacityFloats;
    private float[] _staging = new float[256 * FloatsPerInstance];
    private bool _initialized;
    private bool _disposed;

    public GPUParticleBatcher(GL gl)
    {
        _gl = gl;
    }

    /// <summary>
    /// Ensure the reusable CPU staging buffer can hold at least <paramref name="instanceCount"/> particles
    /// and return it for direct writing (zero per-frame allocations once warmed up).
    /// </summary>
    public float[] EnsureStagingCapacity(int instanceCount)
    {
        int needFloats = Math.Max(0, instanceCount) * FloatsPerInstance;
        if (needFloats > _staging.Length)
        {
            int nextCap = Math.Max(needFloats, _staging.Length * 3 / 2);
            _staging = new float[nextCap];
        }
        return _staging;
    }

    private unsafe void EnsureInitialized()
    {
        if (_initialized) return;

        float[] quadVerts =
        {
            -0.5f, -0.5f,
             0.5f, -0.5f,
            -0.5f,  0.5f,
             0.5f, -0.5f,
             0.5f,  0.5f,
            -0.5f,  0.5f
        };

        _vao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();
        _instanceVbo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* ptr = quadVerts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.VertexAttribDivisor(0, 0);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
#if !XBOX_UWP
        _gl.BindVertexArray(0);
#endif
        _initialized = true;
    }

    /// <summary>
    /// Upload <paramref name="instanceCount"/> instances from the staging buffer and issue a single
    /// instanced draw call (or Xbox-safe chunked instanced draws on <c>XBOX_UWP</c>).
    /// Resets per-instance attribute divisors in a <c>finally</c> block so ANGLE / ES 3.0 never
    /// leaks divisor state onto subsequent non-instanced draws.
    /// </summary>
    public unsafe void UploadAndDraw(int instanceCount)
    {
        if (_disposed || instanceCount <= 0)
            return;

        EnsureInitialized();

        int needFloats = instanceCount * FloatsPerInstance;
        if (needFloats > _staging.Length)
            return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* ptr = _staging)
        {
            if (needFloats > _gpuCapacityFloats)
            {
                int cap = Math.Max(needFloats, Math.Max(256 * FloatsPerInstance, _gpuCapacityFloats * 3 / 2));
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), null, BufferUsageARB.DynamicDraw);
                _gpuCapacityFloats = cap;
            }
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(needFloats * sizeof(float)), ptr);
        }

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.VertexAttribDivisor(0, 0);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.EnableVertexAttribArray(1);
        _gl.EnableVertexAttribArray(2);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribDivisor(1, 1);
        _gl.VertexAttribDivisor(2, 1);
        _gl.VertexAttribDivisor(3, 1);

        try
        {
#if XBOX_UWP
            const int group = 64;
            for (int start = 0; start < instanceCount; start += group)
            {
                int count = Math.Min(group, instanceCount - start);
                int off = start * BytesPerInstance;
                _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)off);
                _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)(off + 16));
                _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)(off + 32));
                _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)count);
            }
#else
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)0);
            _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)16);
            _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, BytesPerInstance, (void*)32);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)instanceCount);
#endif
            Profiler.CountDrawCall();
            Profiler.CountTriangles(2 * instanceCount);
            Game_Engine.Core.Rendering.FrameProfile.CountDraw();
        }
        finally
        {
            _gl.VertexAttribDivisor(1, 0);
            _gl.VertexAttribDivisor(2, 0);
            _gl.VertexAttribDivisor(3, 0);
            _gl.DisableVertexAttribArray(1);
            _gl.DisableVertexAttribArray(2);
            _gl.DisableVertexAttribArray(3);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
#if !XBOX_UWP
            _gl.BindVertexArray(0);
#endif
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            try
            {
                if (_instanceVbo != 0) _gl.DeleteBuffer(_instanceVbo);
                if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
            }
            catch { }
        }

        _instanceVbo = 0;
        _quadVbo = 0;
        _vao = 0;
        _gpuCapacityFloats = 0;
        _initialized = false;
    }
}
