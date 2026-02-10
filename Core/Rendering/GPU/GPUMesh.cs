#nullable enable
using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Manages a VAO / VBO / EBO on the GPU for an engine Mesh.
/// Vertex layout: [Position3, Normal3, UV2] = 32 bytes interleaved.
/// </summary>
public sealed class GPUMesh : IDisposable
{
    private readonly GL _gl;

    public uint VAO { get; private set; }
    public uint VBO { get; private set; }
    public uint EBO { get; private set; }
    public int IndexCount { get; private set; }
    public int LineIndexCount { get; private set; }

    private uint _lineEBO;

    public GPUMesh(GL gl)
    {
        _gl = gl;
        VAO = _gl.GenVertexArray();
        VBO = _gl.GenBuffer();
        EBO = _gl.GenBuffer();
        _lineEBO = _gl.GenBuffer();
    }

    /// <summary>
    /// Upload mesh data to the GPU. Call whenever the mesh changes.
    /// </summary>
    public unsafe void Upload(Mesh mesh)
    {
        var verts = mesh.Vertices;
        var normals = mesh.Normals;
        var uvs = mesh.UVs;
        var tris = mesh.TriIndices;
        var lines = mesh.LineIndices;

        if (verts == null || tris == null) return;

        int vertCount = verts.Length;
        IndexCount = tris.Length;
        LineIndexCount = lines?.Length ?? 0;

        // Build interleaved vertex data: [Pos3, Normal3, UV2] per vertex
        const int STRIDE = 8; // 8 floats = 32 bytes
        float[] data = new float[vertCount * STRIDE];

        for (int i = 0; i < vertCount; i++)
        {
            int off = i * STRIDE;
            data[off + 0] = verts[i].X;
            data[off + 1] = verts[i].Y;
            data[off + 2] = verts[i].Z;

            if (normals != null && i < normals.Length)
            {
                data[off + 3] = normals[i].X;
                data[off + 4] = normals[i].Y;
                data[off + 5] = normals[i].Z;
            }
            else
            {
                data[off + 3] = 0f;
                data[off + 4] = 1f;
                data[off + 5] = 0f;
            }

            if (uvs != null && i < uvs.Length)
            {
                data[off + 6] = uvs[i].X;
                data[off + 7] = uvs[i].Y;
            }
            else
            {
                data[off + 6] = 0f;
                data[off + 7] = 0f;
            }
        }

        _gl.BindVertexArray(VAO);

        // VBO
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
        fixed (float* ptr = data)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw);
        }

        // EBO (triangles)
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
        fixed (int* ptr = tris)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(tris.Length * sizeof(int)), ptr, BufferUsageARB.DynamicDraw);
        }

        // Vertex attributes
        const uint stride = STRIDE * sizeof(float);

        // location 0: position (vec3)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

        // location 1: normal (vec3)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));

        // location 2: uv (vec2)
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);

        // Line EBO (for wireframe)
        if (lines != null && lines.Length > 0)
        {
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _lineEBO);
            fixed (int* ptr = lines)
            {
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(lines.Length * sizeof(int)), ptr, BufferUsageARB.DynamicDraw);
            }
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        }
    }

    /// <summary>Draw triangles.</summary>
    public unsafe void Draw()
    {
        if (IndexCount <= 0) return;
        _gl.BindVertexArray(VAO);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, null);
    }

    /// <summary>Draw wireframe lines.</summary>
    public unsafe void DrawWireframe()
    {
        if (LineIndexCount <= 0) return;
        _gl.BindVertexArray(VAO);
        // Rebind line EBO temporarily
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _lineEBO);
        _gl.DrawElements(PrimitiveType.Lines, (uint)LineIndexCount, DrawElementsType.UnsignedInt, null);
        // Restore tri EBO
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(VAO);
        _gl.DeleteBuffer(VBO);
        _gl.DeleteBuffer(EBO);
        _gl.DeleteBuffer(_lineEBO);
        VAO = VBO = EBO = 0;
    }
}
