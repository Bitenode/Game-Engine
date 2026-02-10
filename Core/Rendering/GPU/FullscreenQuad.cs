#nullable enable
using System;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// A shared fullscreen triangle (more efficient than a quad) for sky, grid, and post-processing.
/// Covers the entire NDC [-1,1] viewport with a single oversized triangle.
/// </summary>
public sealed class FullscreenQuad : IDisposable
{
    private readonly GL _gl;
    public uint VAO { get; private set; }
    private uint _vbo;

    public FullscreenQuad(GL gl)
    {
        _gl = gl;

        // Oversized triangle that covers the full screen:
        //   (-1, -1), (3, -1), (-1, 3)
        // UVs: (0, 0), (2, 0), (0, 2) — the GPU clips to [0,1] naturally
        float[] vertices =
        {
            // pos.x, pos.y, uv.x, uv.y
            -1f, -1f,  0f, 0f,
             3f, -1f,  2f, 0f,
            -1f,  3f,  0f, 2f,
        };

        VAO = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();

        _gl.BindVertexArray(VAO);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* ptr = vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
            }
        }

        // location 0: position (vec2)
        _gl.EnableVertexAttribArray(0);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        }

        // location 1: uv (vec2)
        _gl.EnableVertexAttribArray(1);
        unsafe
        {
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        }

        _gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(VAO);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(VAO);
        _gl.DeleteBuffer(_vbo);
        VAO = 0;
    }
}
