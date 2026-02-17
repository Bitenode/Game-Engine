#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Game_Engine.Core.Component.UI;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.UI
{
    /// <summary>
    /// Batched UI renderer. Walks Canvas hierarchies, collects quads from UIElements,
    /// and issues draw calls with the UI shader. One instance per GL context.
    /// </summary>
    public sealed class CanvasRenderer : IDisposable
    {
        private readonly GL _gl;
        private ShaderProgram? _uiShader;
        private ShaderProgram? _textShader;
        private uint _vao;
        private uint _vbo;
        private uint _ebo;
        private bool _isES;

        // White 1x1 fallback texture
        private uint _whiteTexture;

        // Dynamic vertex buffer
        private readonly List<UIVertex> _vertices = new(1024);
        private readonly List<uint> _indices = new(2048);
        private readonly List<DrawBatch> _batches = new(32);

        /// <summary>Per-vertex layout: pos(2) + uv(2) + color(4) = 32 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct UIVertex
        {
            public float PosX, PosY;
            public float UvX, UvY;
            public float R, G, B, A;
        }

        /// <summary>One draw call — a contiguous range of indices sharing a texture.</summary>
        private struct DrawBatch
        {
            public uint TextureHandle;
            public int IndexOffset;
            public int IndexCount;
            public bool IsSDF; // true for SDF text rendering
        }

        public CanvasRenderer(GL gl, bool isES)
        {
            _gl = gl;
            _isES = isES;

            // Compile shaders
            string vert = ShaderSources.Adapt(ShaderSources.UIVert, isES);
            string frag = ShaderSources.Adapt(ShaderSources.UIFrag, isES);
            string textFrag = ShaderSources.Adapt(ShaderSources.UITextFrag, isES);

            _uiShader = new ShaderProgram(gl, vert, frag);
            _textShader = new ShaderProgram(gl, vert, textFrag);

            // Create VAO/VBO/EBO
            _vao = _gl.GenVertexArray();
            _vbo = _gl.GenBuffer();
            _ebo = _gl.GenBuffer();

            _gl.BindVertexArray(_vao);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

            uint stride = (uint)Marshal.SizeOf<UIVertex>(); // 32 bytes

            // location 0: aPos (vec2)
            _gl.EnableVertexAttribArray(0);
            unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0); }

            // location 1: aUV (vec2)
            _gl.EnableVertexAttribArray(1);
            unsafe { _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float))); }

            // location 2: aColor (vec4)
            _gl.EnableVertexAttribArray(2);
            unsafe { _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float))); }

            _gl.BindVertexArray(0);

            // 1x1 white texture for solid-color quads
            _whiteTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);
            unsafe
            {
                byte[] white = { 255, 255, 255, 255 };
                fixed (byte* ptr = white)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                        1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                }
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        /// <summary>
        /// Render all active screen-space overlay canvases.
        /// Call after post-processing, with the default framebuffer bound.
        /// </summary>
        public void RenderOverlays(float viewportWidth, float viewportHeight, ResourceCache cache)
        {
            if (Canvas.All.Count == 0) return;

            // Sort canvases by SortOrder (low to high)
            var sorted = Canvas.All
                .Where(c => c.IsActiveAndEnabled && c.RenderMode == CanvasRenderMode.ScreenSpaceOverlay)
                .OrderBy(c => c.SortOrder)
                .ToList();

            if (sorted.Count == 0) return;

            // Orthographic projection: (0,0) = bottom-left, (viewportW, viewportH) = top-right
            var proj = SN.Matrix4x4.CreateOrthographicOffCenter(
                0f, viewportWidth, 0f, viewportHeight, -1f, 1f);

            // GL state for UI rendering
            _gl.Disable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            foreach (var canvas in sorted)
            {
                var canvasRect = canvas.GetCanvasRect(viewportWidth, viewportHeight);
                float scaleFactor = canvas.GetScaleFactor(viewportWidth, viewportHeight);

                // Build MVP: canvas-space -> screen-space -> clip-space
                var scaleMatrix = SN.Matrix4x4.CreateScale(scaleFactor, scaleFactor, 1f);
                var mvp = scaleMatrix * proj;

                BuildBatches(canvas, in canvasRect, cache);
                FlushBatches(in mvp);
            }

            // Restore GL state
            _gl.Enable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.Blend);
        }

        /// <summary>
        /// Render a single world-space canvas. Called during the transparent pass.
        /// </summary>
        public void RenderWorldCanvas(Canvas canvas, in SN.Matrix4x4 viewProj, ResourceCache cache)
        {
            if (!canvas.IsActiveAndEnabled || canvas.RenderMode != CanvasRenderMode.WorldSpace) return;

            var go = canvas.gameObject;
            if (go == null) return;

            var tr = go.Transform;
            float worldW = canvas.WorldSizeX;
            float worldH = canvas.WorldSizeY;

            // Canvas rect in local canvas coordinates (pixels mapped to world units)
            float canvasPixelsW = canvas.ReferenceResolutionX;
            float canvasPixelsH = canvas.ReferenceResolutionY;
            var canvasRect = new RectTransform.Rect(0, 0, canvasPixelsW, canvasPixelsH);

            // Model matrix: position + rotation of the GameObject, scaled so canvas pixels map to world units
            float scaleX = worldW / canvasPixelsW;
            float scaleY = worldH / canvasPixelsH;

            static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);
            var model = SN.Matrix4x4.CreateScale(scaleX, scaleY, 1f)
                      * SN.Matrix4x4.CreateFromYawPitchRoll(
                            Deg2Rad(tr.Rotation.Y), Deg2Rad(tr.Rotation.X), Deg2Rad(tr.Rotation.Z))
                      * SN.Matrix4x4.CreateTranslation(
                            (float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

            var mvp = model * viewProj;

            // GL state
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            BuildBatches(canvas, in canvasRect, cache);
            FlushBatches(in mvp);

            _gl.Enable(EnableCap.CullFace);
        }

        // ── Batch building ──

        private void BuildBatches(Canvas canvas, in RectTransform.Rect canvasRect, ResourceCache cache)
        {
            _vertices.Clear();
            _indices.Clear();
            _batches.Clear();

            var go = canvas.gameObject;
            if (go == null) return;

            // Traverse children depth-first (sibling order = draw order)
            GatherElements(go, in canvasRect, cache);
        }

        private void GatherElements(GameObject go, in RectTransform.Rect canvasRect, ResourceCache cache)
        {
            if (!go.Enabled) return;

            // Collect UI element from this object
            foreach (var b in go.Behaviors)
            {
                if (b is UIElement element && element.Enabled)
                {
                    EmitElement(element, in canvasRect, cache);
                }
            }

            // Recurse into children
            foreach (var child in go.Children)
            {
                GatherElements(child, in canvasRect, cache);
            }
        }

        private void EmitElement(UIElement element, in RectTransform.Rect canvasRect, ResourceCache cache)
        {
            var rt = element.gameObject?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            if (rt == null) return;

            var rect = rt.GetWorldRect(in canvasRect);

            // Ask the element for its draw data
            var drawData = element.GetDrawData(in rect);
            if (drawData.QuadCount == 0) return;

            for (int q = 0; q < drawData.QuadCount; q++)
            {
                var quad = drawData.Quads[q];

                // Resolve Texture2D -> GPU handle via ResourceCache
                uint texHandle;
                if (quad.Texture != null && cache != null)
                    texHandle = cache.GetTexture(quad.Texture).Handle;
                else if (quad.TextureHandle != 0)
                    texHandle = quad.TextureHandle;
                else
                    texHandle = _whiteTexture;
                bool isSDF = quad.IsSDF;

                // Try to merge with the last batch
                if (_batches.Count > 0)
                {
                    var last = _batches[^1];
                    if (last.TextureHandle == texHandle && last.IsSDF == isSDF)
                    {
                        // Extend the last batch
                        var updatedLast = last;
                        updatedLast.IndexCount += 6;
                        _batches[^1] = updatedLast;
                    }
                    else
                    {
                        _batches.Add(new DrawBatch
                        {
                            TextureHandle = texHandle,
                            IndexOffset = _indices.Count,
                            IndexCount = 6,
                            IsSDF = isSDF
                        });
                    }
                }
                else
                {
                    _batches.Add(new DrawBatch
                    {
                        TextureHandle = texHandle,
                        IndexOffset = _indices.Count,
                        IndexCount = 6,
                        IsSDF = isSDF
                    });
                }

                // Emit 4 vertices + 6 indices for a quad
                uint baseIdx = (uint)_vertices.Count;

                _vertices.Add(new UIVertex { PosX = quad.X0, PosY = quad.Y0, UvX = quad.U0, UvY = quad.V0, R = quad.R, G = quad.G, B = quad.B, A = quad.A });
                _vertices.Add(new UIVertex { PosX = quad.X1, PosY = quad.Y0, UvX = quad.U1, UvY = quad.V0, R = quad.R, G = quad.G, B = quad.B, A = quad.A });
                _vertices.Add(new UIVertex { PosX = quad.X1, PosY = quad.Y1, UvX = quad.U1, UvY = quad.V1, R = quad.R, G = quad.G, B = quad.B, A = quad.A });
                _vertices.Add(new UIVertex { PosX = quad.X0, PosY = quad.Y1, UvX = quad.U0, UvY = quad.V1, R = quad.R, G = quad.G, B = quad.B, A = quad.A });

                _indices.Add(baseIdx + 0);
                _indices.Add(baseIdx + 1);
                _indices.Add(baseIdx + 2);
                _indices.Add(baseIdx + 0);
                _indices.Add(baseIdx + 2);
                _indices.Add(baseIdx + 3);
            }
        }

        private void FlushBatches(in SN.Matrix4x4 mvp)
        {
            if (_vertices.Count == 0 || _batches.Count == 0) return;

            // Upload vertex data
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            unsafe
            {
                fixed (UIVertex* ptr = CollectionsMarshal.AsSpan(_vertices))
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer,
                        (nuint)(_vertices.Count * Marshal.SizeOf<UIVertex>()),
                        ptr, BufferUsageARB.StreamDraw);
                }
            }

            // Upload index data
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            unsafe
            {
                fixed (uint* ptr = CollectionsMarshal.AsSpan(_indices))
                {
                    _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                        (nuint)(_indices.Count * sizeof(uint)),
                        ptr, BufferUsageARB.StreamDraw);
                }
            }

            // Draw batches
            ShaderProgram? activeShader = null;

            foreach (var batch in _batches)
            {
                var shader = batch.IsSDF ? _textShader : _uiShader;
                if (shader == null) continue;

                if (shader != activeShader)
                {
                    shader.Use();
                    shader.SetMatrix4("uMVP", in mvp);

                    if (!batch.IsSDF)
                        shader.SetInt("uHasTexture", batch.TextureHandle != _whiteTexture ? 1 : 0);

                    shader.SetTexture("uTex", 0);
                    activeShader = shader;
                }
                else if (!batch.IsSDF)
                {
                    shader.SetInt("uHasTexture", batch.TextureHandle != _whiteTexture ? 1 : 0);
                }

                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, batch.TextureHandle);

                unsafe
                {
                    _gl.DrawElements(PrimitiveType.Triangles,
                        (uint)batch.IndexCount,
                        DrawElementsType.UnsignedInt,
                        (void*)(batch.IndexOffset * sizeof(uint)));
                }
            }

            _gl.BindVertexArray(0);
        }

        public void Dispose()
        {
            _uiShader?.Dispose();
            _textShader?.Dispose();
            _uiShader = null;
            _textShader = null;

            if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
            if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
            if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
            if (_whiteTexture != 0) { _gl.DeleteTexture(_whiteTexture); _whiteTexture = 0; }
        }
    }
}
