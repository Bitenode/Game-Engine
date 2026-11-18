#nullable enable
using System;
using Silk.NET.OpenGL;
using SN = System.Numerics;
using Profiler = Game_Engine.Core.Profiler;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Manages a VAO / VBO / EBO on the GPU for an engine Mesh.
/// Non-skinned layout: [Position3, Normal3, UV2] = 32 bytes interleaved.
/// Skinned layout:     [Position3, Normal3, UV2, BoneIdx4_as_float, BoneWeight4] = 64 bytes interleaved.
/// </summary>
public sealed class GPUMesh : IDisposable
{
    private readonly GL _gl;

    public uint VAO { get; private set; }
    public uint VBO { get; private set; }
    public uint EBO { get; private set; }
    public int IndexCount { get; private set; }
    public int LineIndexCount { get; private set; }

    /// <summary>True if the last Upload() included bone data.</summary>
    public bool IsSkinned { get; private set; }

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
        IsSkinned = mesh.HasBones;

        if (mesh.IsPlanetMesh)
            UploadPlanet(mesh, mesh.PlanetBlendIndices, mesh.PlanetBlendWeights);
        else if (IsSkinned)
            UploadSkinned(mesh, vertCount);
        else
            UploadStatic(mesh, vertCount);

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

    /// <summary>Upload non-skinned mesh: [Pos3, Norm3, UV2] = 32 bytes/vert.</summary>
    private unsafe void UploadStatic(Mesh mesh, int vertCount)
    {
        var verts = mesh.Vertices;
        var normals = mesh.Normals;
        var uvs = mesh.UVs;
        var tris = mesh.TriIndices;

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

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
        fixed (float* ptr = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
        fixed (int* ptr = tris)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(tris.Length * sizeof(int)), ptr, BufferUsageARB.DynamicDraw);

        const uint stride = STRIDE * sizeof(float);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // Disable bone attribs in case this VAO was previously skinned
        _gl.DisableVertexAttribArray(3);
        _gl.DisableVertexAttribArray(4);

        _gl.BindVertexArray(0);
    }

    /// <summary>Upload skinned mesh: [Pos3, Norm3, UV2, BoneIdx4_as_float, BoneWeight4] = 64 bytes/vert.</summary>
    private unsafe void UploadSkinned(Mesh mesh, int vertCount)
    {
        var verts = mesh.Vertices;
        var normals = mesh.Normals;
        var uvs = mesh.UVs;
        var tris = mesh.TriIndices;
        var boneIndices = mesh.BoneIndices;
        var boneWeights = mesh.BoneWeights;

        const int STRIDE = 16; // 16 floats = 64 bytes
        float[] data = new float[vertCount * STRIDE];

        for (int i = 0; i < vertCount; i++)
        {
            int off = i * STRIDE;

            data[off + 0] = verts[i].X;
            data[off + 1] = verts[i].Y;
            data[off + 2] = verts[i].Z;

            if (normals != null && i < normals.Length)
            { data[off + 3] = normals[i].X; data[off + 4] = normals[i].Y; data[off + 5] = normals[i].Z; }
            else
            { data[off + 3] = 0f; data[off + 4] = 1f; data[off + 5] = 0f; }

            if (uvs != null && i < uvs.Length)
            { data[off + 6] = uvs[i].X; data[off + 7] = uvs[i].Y; }
            else
            { data[off + 6] = 0f; data[off + 7] = 0f; }

            // Bone indices (as float — shader reads via vec4 then converts to int)
            int bi = i * 4;
            if (boneIndices != null && bi + 3 < boneIndices.Length)
            {
                data[off + 8] = boneIndices[bi + 0];
                data[off + 9] = boneIndices[bi + 1];
                data[off + 10] = boneIndices[bi + 2];
                data[off + 11] = boneIndices[bi + 3];
            }

            // Bone weights
            if (boneWeights != null && i < boneWeights.Length)
            {
                data[off + 12] = boneWeights[i].X;
                data[off + 13] = boneWeights[i].Y;
                data[off + 14] = boneWeights[i].Z;
                data[off + 15] = boneWeights[i].W;
            }
            else
            {
                data[off + 12] = 1f; // default: all weight on bone 0
            }
        }

        _gl.BindVertexArray(VAO);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
        fixed (float* ptr = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
        fixed (int* ptr = tris)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(tris.Length * sizeof(int)), ptr, BufferUsageARB.DynamicDraw);

        const uint stride = STRIDE * sizeof(float); // 64 bytes

        // location 0: position (vec3)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

        // location 1: normal (vec3)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));

        // location 2: uv (vec2)
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // location 3: bone indices (vec4 — read as float, cast to int in shader)
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));

        // location 4: bone weights (vec4)
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>True if the last upload used the planet vertex format.</summary>
    public bool IsPlanet { get; private set; }

    /// <summary>
    /// Upload planet terrain mesh: [Pos3, Norm3, UV2, BlendIdx4, BlendWt4] = 64 bytes/vert.
    /// BlendIdx are biome indices (as float), BlendWt are biome weights.
    /// </summary>
    public unsafe void UploadPlanet(Mesh mesh, SN.Vector4[]? blendIndices, SN.Vector4[]? blendWeights)
    {
        var verts = mesh.Vertices;
        var normals = mesh.Normals;
        var uvs = mesh.UVs;
        var tris = mesh.TriIndices;

        if (verts == null || tris == null) return;

        int vertCount = verts.Length;
        IndexCount = tris.Length;
        IsSkinned = false;
        IsPlanet = true;

        const int STRIDE = 16; // Pos3 + Norm3 + UV2 + BlendIdx4 + BlendWt4
        float[] data = new float[vertCount * STRIDE];

        for (int i = 0; i < vertCount; i++)
        {
            int off = i * STRIDE;
            data[off + 0] = verts[i].X;
            data[off + 1] = verts[i].Y;
            data[off + 2] = verts[i].Z;

            if (normals != null && i < normals.Length)
            { data[off + 3] = normals[i].X; data[off + 4] = normals[i].Y; data[off + 5] = normals[i].Z; }
            else
            { data[off + 3] = 0f; data[off + 4] = 1f; data[off + 5] = 0f; }

            if (uvs != null && i < uvs.Length)
            { data[off + 6] = uvs[i].X; data[off + 7] = uvs[i].Y; }

            if (blendIndices != null && i < blendIndices.Length)
            {
                data[off + 8]  = blendIndices[i].X;
                data[off + 9]  = blendIndices[i].Y;
                data[off + 10] = blendIndices[i].Z;
                data[off + 11] = blendIndices[i].W;
            }

            if (blendWeights != null && i < blendWeights.Length)
            {
                data[off + 12] = blendWeights[i].X;
                data[off + 13] = blendWeights[i].Y;
                data[off + 14] = blendWeights[i].Z;
                data[off + 15] = blendWeights[i].W;
            }
            else
            {
                data[off + 12] = 1f;
            }
        }

        _gl.BindVertexArray(VAO);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, VBO);
        fixed (float* ptr = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, EBO);
        fixed (int* ptr = tris)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(tris.Length * sizeof(int)), ptr, BufferUsageARB.DynamicDraw);

        const uint stride = STRIDE * sizeof(float);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>Draw triangles.</summary>
    public unsafe void Draw()
    {
        if (IndexCount <= 0) return;
        _gl.BindVertexArray(VAO);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, null);
        Profiler.CountDrawCall();
        Profiler.CountTriangles(IndexCount / 3);
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
