#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Views;

/// <summary>Preview geometry options for <see cref="ShaderPreviewControl"/>.</summary>
public enum ShaderPreviewMesh
{
    Sphere = 0,
    Cube = 1,
    Plane = 2
}

/// <summary>
/// Real-time OpenGL preview control for the Visual Shader Editor.
/// Compiles and renders the actual <c>Surface</c> <c>.shader</c> via <see cref="ShaderFileParser"/>,
/// <see cref="ShaderPreprocessor"/>, <see cref="ShaderProgram"/>, <see cref="GlobalUniforms"/>, and
/// <see cref="MaterialBinder"/> with interactive orbit camera and studio PBR lighting.
/// </summary>
public sealed class ShaderPreviewControl : OpenGlControlBase, ICustomHitTest
{
    public bool HitTest(Point point) => true;

    /// <summary>Raised on the UI thread whenever a shader compilation attempt finishes.</summary>
    public event Action<bool, string>? CompilationResultChanged;

    private struct MeshGpu
    {
        public uint Vao;
        public uint Vbo;
        public uint Ebo;
        public uint IndexCount;
    }

    private GLContext? _glCtx;
    private GlobalUniforms? _globals;
    private ResourceCache? _cache;

    private MeshGpu _sphere;
    private MeshGpu _cube;
    private MeshGpu _plane;
    private uint _bgVao;
    private ShaderProgram? _bgShader;

    private ShaderProgram? _activeShader;
    private ParsedShaderFile? _activeSchema;
    private bool _shaderUsesTime;

    private readonly object _pendingLock = new();
    private string? _pendingSource;
    private string _pendingLabel = "ShaderPreview";
    private Material? _pendingMaterial;
    private bool _shaderDirty;
    private Material? _activeMaterial;

    private ShaderPreviewMesh _meshType = ShaderPreviewMesh.Sphere;
    private bool _autoSpin = true;

    // Orbit camera state
    private float _yaw = 0.55f;
    private float _pitch = 0.25f;
    private float _distance = 2.85f;
    private bool _isDragging;
    private Point _lastPointer;

    // Animation timer
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _animTimer;
    private float _spinAngle;
    private double _lastTickSec;

    public ShaderPreviewMesh MeshType
    {
        get => _meshType;
        set
        {
            if (_meshType == value) return;
            _meshType = value;
            RequestNextFrameRendering();
        }
    }

    public bool AutoSpin
    {
        get => _autoSpin;
        set
        {
            if (_autoSpin == value) return;
            _autoSpin = value;
            RequestNextFrameRendering();
        }
    }

    public ShaderPreviewControl()
    {
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _animTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _glCtx == null) return;
            if (_autoSpin || _shaderUsesTime || _shaderDirty)
                RequestNextFrameRendering();
        };
    }

    /// <summary>
    /// Queue a <c>.shader</c> source string for live GPU compilation and rendering.
    /// Safe to call from the UI thread before or after GL initialization.
    /// </summary>
    public void SetShaderSource(string shaderText, string? sourceLabel = null, Material? previewMaterial = null)
    {
        lock (_pendingLock)
        {
            _pendingSource = shaderText;
            _pendingLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "ShaderPreview" : sourceLabel;
            _pendingMaterial = previewMaterial;
            _shaderDirty = true;
        }
        RequestNextFrameRendering();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _animTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
        {
            _isDragging = true;
            _lastPointer = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;

        var pos = e.GetPosition(this);
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        _yaw -= (float)delta.X * 0.012f;
        _pitch = Math.Clamp(_pitch + (float)delta.Y * 0.012f, -1.35f, 1.35f);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _distance = Math.Clamp(_distance - (float)e.Delta.Y * 0.25f, 1.6f, 6.5f);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try
        {
            _glCtx = new GLContext(gl.GetProcAddress);
            var g = _glCtx.GL;
            _globals = GlobalUniforms.GetOrCreate(g, _glCtx.IsES);
            _cache = new ResourceCache(g);

            _sphere = CreateSphereMesh(g, 48, 32, 1.0f);
            _cube = CreateCubeMesh(g, 1.45f);
            _plane = CreatePlaneMesh(g, 2.0f);

            _bgVao = g.GenVertexArray();
            _bgShader = CreateBackgroundShader(g, _glCtx.IsES);

            _lastTickSec = _clock.Elapsed.TotalSeconds;
            if (IsVisible) _animTimer.Start();
            RequestNextFrameRendering();
        }
        catch (Exception ex)
        {
            Log.Error($"[ShaderPreview] GL init failed: {ex.Message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _animTimer.Stop();

        if (_glCtx != null)
        {
            var g = _glCtx.GL;
            _activeShader?.Dispose();
            _activeShader = null;

            _bgShader?.Dispose();
            _bgShader = null;

            DeleteMesh(g, ref _sphere);
            DeleteMesh(g, ref _cube);
            DeleteMesh(g, ref _plane);

            if (_bgVao != 0) { g.DeleteVertexArray(_bgVao); _bgVao = 0; }

            _cache?.Dispose();
            _cache = null;

            _globals = null;
            _glCtx.Dispose();
            _glCtx = null;
        }

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_glCtx == null || _globals == null || _cache == null)
            return;

        var g = _glCtx.GL;

        // Recompile if source changed
        CompilePendingShaderIfNeeded(g);

        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTickSec, 0.0, 0.1);
        _lastTickSec = now;
        if (_autoSpin && !_isDragging)
            _spinAngle += (float)dt * 0.45f;

        double scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int pixelW = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int pixelH = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        ShaderProgram.NotifyProgramUnbound(g);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)pixelW, (uint)pixelH);
        g.ClearColor(0.06f, 0.07f, 0.11f, 1f);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // 1. Subtle studio gradient backdrop
        DrawBackground(g);

        // 2. Render preview mesh with compiled shader
        if (_activeShader != null)
        {
            float aspect = (float)pixelW / Math.Max(1, pixelH);
            var proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 50f);

            float cosP = MathF.Cos(_pitch);
            var camPos = new SN.Vector3(
                MathF.Sin(_yaw) * cosP * _distance,
                MathF.Sin(_pitch) * _distance,
                MathF.Cos(_yaw) * cosP * _distance);
            var view = SN.Matrix4x4.CreateLookAt(camPos, SN.Vector3.Zero, SN.Vector3.UnitY);

            var model = SN.Matrix4x4.CreateRotationY(_spinAngle);
            SN.Matrix4x4.Invert(model, out var invModel);
            var normalMatrix = SN.Matrix4x4.Transpose(invModel);

            float elapsed = (float)now;

            _globals.BindAll();
            _globals.UpdateFrame(in view, in proj, camPos, elapsed, pixelW, pixelH, 0.1f, 50f);

            // Studio 3-point lighting: warm key + cool rim + soft fill
            var keyDir = SN.Vector3.Normalize(new SN.Vector3(-0.55f, -0.70f, -0.45f));
            var identity = SN.Matrix4x4.Identity;
            _globals.UpdateLighting(
                lightDir: keyDir,
                diffuseK: 1.55f,
                lightColor: new SN.Vector3(1.0f, 0.96f, 0.90f),
                ambient: 0.20f,
                lightIsPoint: false,
                lightPosW: SN.Vector3.Zero,
                lightRange: 10f,
                hasShadow: false,
                shadowVP: in identity);

            _globals.UpdatePointLights(
                count: 2,
                posRange0: new SN.Vector4(-2.8f, 1.4f, -2.4f, 10f),
                color0: new SN.Vector4(0.55f, 0.72f, 1.0f, 1.15f),   // cool rim
                posRange1: new SN.Vector4(2.6f, -1.0f, 2.0f, 10f),
                color1: new SN.Vector4(0.95f, 0.85f, 0.75f, 0.55f),  // warm fill
                posRange2: SN.Vector4.Zero, color2: SN.Vector4.Zero,
                posRange3: SN.Vector4.Zero, color3: SN.Vector4.Zero);

            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);

            using (MaterialBinder.ApplyRenderState(
                g,
                _activeSchema?.RenderState,
                doubleSidedOverride: _meshType == ShaderPreviewMesh.Plane,
                restoreCullEnabled: false,
                restoreDepthWrite: true,
                restoreBlend: false))
            {
                _activeShader.Use();
                _activeShader.SetMatrix4("uModel", in model);
                _activeShader.SetMatrix4("uNormalMatrix", in normalMatrix);
                _activeShader.SetInt("uHasBones", 0);
                _activeShader.SetFloat("uRoughness", 0.5f);
                _activeShader.SetFloat("uMetallic", 0.0f);
                _activeShader.SetInt("uHasNormalMap", 0);
                _activeShader.SetFloat("uNormalStrength", 1.0f);
                _activeShader.SetInt("uHasSpecularMap", 0);

                // Legacy fallback uniforms if a custom explicit shader doesn't use UBOs
                if (!_activeShader.HasFrameUniformBlock)
                {
                    _activeShader.SetMatrix4("uView", in view);
                    _activeShader.SetMatrix4("uProjection", in proj);
                    _activeShader.SetMatrix4("uProj", in proj);
                    _activeShader.SetVector3("uCameraPos", camPos);
                    _activeShader.SetFloat("uTime", elapsed);
                }
                if (!_activeShader.HasLightingUniformBlock)
                {
                    _activeShader.SetVector3("uLightDir", keyDir);
                    _activeShader.SetVector3("uLightColor", new SN.Vector3(1.0f, 0.96f, 0.90f));
                    _activeShader.SetFloat("uLightIntensity", 1.55f);
                    _activeShader.SetFloat("uAmbient", 0.20f);
                }

                // Bind standard texture slots (0, 1, 2)
                var mat = _activeMaterial;
                BindStandardSlot(_activeShader, 0, "uTexture0", mat?.Textures, "Albedo", _cache.GetWhiteTexture());
                BindStandardSlot(_activeShader, 1, "uTexture1", mat?.Textures, "Normal", _cache.GetFlatNormalTexture());
                BindStandardSlot(_activeShader, 2, "uTexture2", mat?.Textures, "Specular", _cache.GetWhiteTexture());

                // Bind declared Properties { ... } defaults + overrides
                MaterialBinder.BindProperties(_activeShader, mat, _activeSchema, _cache);

                var mesh = _meshType switch
                {
                    ShaderPreviewMesh.Cube => _cube,
                    ShaderPreviewMesh.Plane => _plane,
                    _ => _sphere
                };

                g.BindVertexArray(mesh.Vao);
                unsafe
                {
                    g.DrawElements(PrimitiveType.Triangles, mesh.IndexCount, DrawElementsType.UnsignedInt, null);
                }
                g.BindVertexArray(0);
            }

            g.Disable(EnableCap.DepthTest);
        }

        // Reset GL state cleanly for Avalonia compositor
        g.UseProgram(0);
        ShaderProgram.NotifyProgramUnbound(g);
    }

    private void BindStandardSlot(
        ShaderProgram shader,
        int unit,
        string uniformName,
        List<object>? slots,
        string usage,
        GPUTexture fallback)
    {
        GPUTexture tex = fallback;
        if (slots != null)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is RuntimeTexSlot slot && slot.Texture != null &&
                    string.Equals(slot.Usage, usage, StringComparison.OrdinalIgnoreCase))
                {
                    tex = _cache!.GetTexture(slot.Texture);
                    break;
                }
            }
        }
        tex.Bind((TextureUnit)((int)TextureUnit.Texture0 + unit));
        shader.SetTexture(uniformName, unit);
    }

    private void CompilePendingShaderIfNeeded(GL g)
    {
        string? src;
        string label;
        Material? mat;

        lock (_pendingLock)
        {
            if (!_shaderDirty || _pendingSource == null)
                return;
            src = _pendingSource;
            label = _pendingLabel;
            mat = _pendingMaterial;
            _shaderDirty = false;
        }

        try
        {
            var parsed = ShaderFileParser.Parse(src, label);
            var (vertSrc, vertLine, fragSrc, fragLine) = ShaderFileParser.GeneratePassSources(parsed, ShaderPassType.Forward);
            if (string.IsNullOrWhiteSpace(vertSrc) || string.IsNullOrWhiteSpace(fragSrc))
                throw new InvalidOperationException("Parsed shader produced empty vertex or fragment stage.");

            var opts = _glCtx!.IsES ? ShaderCompileOptions.OpenGLES : ShaderCompileOptions.Desktop;
            var vertPre = ShaderPreprocessor.PreprocessSource(vertSrc, $"{label}.vert", opts, startLine: vertLine);
            var fragPre = ShaderPreprocessor.PreprocessSource(fragSrc, $"{label}.frag", opts, startLine: fragLine);

            var compiled = new ShaderProgram(g, vertPre.Source, fragPre.Source, vertPre.SourceMap, fragPre.SourceMap, label);

            _activeShader?.Dispose();
            _activeShader = compiled;
            _activeSchema = parsed;
            _activeMaterial = mat;
            _shaderUsesTime = src.Contains("IN.time", StringComparison.Ordinal)
                           || src.Contains("uTime", StringComparison.Ordinal)
                           || src.Contains("uFrameTime", StringComparison.Ordinal);

            Dispatcher.UIThread.Post(() => CompilationResultChanged?.Invoke(true, "GPU Preview OK"));
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            Dispatcher.UIThread.Post(() => CompilationResultChanged?.Invoke(false, msg));
        }
    }

    private void DrawBackground(GL g)
    {
        if (_bgShader == null || _bgVao == 0) return;
        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.DepthMask(false);

        _bgShader.Use();
        g.BindVertexArray(_bgVao);
        g.DrawArrays(PrimitiveType.Triangles, 0, 3);
        g.BindVertexArray(0);

        g.DepthMask(true);
    }

    private static ShaderProgram CreateBackgroundShader(GL g, bool isES)
    {
        const string vertSrc = @"
out vec2 vUv;
void main() {
    vec2 pos = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vUv = pos * 0.5 + 0.5;
    gl_Position = vec4(pos, 0.9999, 1.0);
}";
        const string fragSrc = @"
in vec2 vUv;
out vec4 FragColor;
void main() {
    vec3 bottom = vec3(0.055, 0.060, 0.095);
    vec3 top    = vec3(0.115, 0.135, 0.210);
    vec3 col = mix(bottom, top, clamp(vUv.y, 0.0, 1.0));
    float vig = 1.0 - 0.28 * dot(vUv - vec2(0.5), vUv - vec2(0.5));
    FragColor = vec4(col * vig, 1.0);
}";
        var opts = isES ? ShaderCompileOptions.OpenGLES : ShaderCompileOptions.Desktop;
        var vp = ShaderPreprocessor.PreprocessSource(vertSrc, "PreviewBg.vert", opts);
        var fp = ShaderPreprocessor.PreprocessSource(fragSrc, "PreviewBg.frag", opts);
        return new ShaderProgram(g, vp.Source, fp.Source, vp.SourceMap, fp.SourceMap, "PreviewBg");
    }

    // ── Procedural Preview Geometry ───────────────────────────────────────────

    private static unsafe MeshGpu UploadMesh(GL g, float[] vertices, uint[] indices)
    {
        uint vao = g.GenVertexArray();
        uint vbo = g.GenBuffer();
        uint ebo = g.GenBuffer();

        g.BindVertexArray(vao);

        g.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* vPtr = vertices)
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), vPtr, BufferUsageARB.StaticDraw);

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* iPtr = indices)
            g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iPtr, BufferUsageARB.StaticDraw);

        const uint stride = 8 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        g.BindVertexArray(0);

        return new MeshGpu { Vao = vao, Vbo = vbo, Ebo = ebo, IndexCount = (uint)indices.Length };
    }

    private static void DeleteMesh(GL g, ref MeshGpu mesh)
    {
        if (mesh.Vao != 0) g.DeleteVertexArray(mesh.Vao);
        if (mesh.Vbo != 0) g.DeleteBuffer(mesh.Vbo);
        if (mesh.Ebo != 0) g.DeleteBuffer(mesh.Ebo);
        mesh = default;
    }

    private static MeshGpu CreateSphereMesh(GL g, int segments, int rings, float radius)
    {
        var verts = new List<float>((rings + 1) * (segments + 1) * 8);
        var idx = new List<uint>(rings * segments * 6);

        for (int y = 0; y <= rings; y++)
        {
            float v = (float)y / rings;
            float phi = v * MathF.PI;
            float sinP = MathF.Sin(phi);
            float cosP = MathF.Cos(phi);

            for (int x = 0; x <= segments; x++)
            {
                float u = (float)x / segments;
                float theta = u * MathF.PI * 2f;
                float nx = MathF.Cos(theta) * sinP;
                float ny = cosP;
                float nz = MathF.Sin(theta) * sinP;

                verts.Add(nx * radius);
                verts.Add(ny * radius);
                verts.Add(nz * radius);
                verts.Add(nx);
                verts.Add(ny);
                verts.Add(nz);
                verts.Add(u);
                verts.Add(1f - v);
            }
        }

        int row = segments + 1;
        for (int y = 0; y < rings; y++)
        {
            for (int x = 0; x < segments; x++)
            {
                uint a = (uint)(y * row + x);
                uint b = (uint)(a + row);
                uint c = (uint)(b + 1);
                uint d = (uint)(a + 1);

                idx.Add(a); idx.Add(d); idx.Add(b);
                idx.Add(d); idx.Add(c); idx.Add(b);
            }
        }

        return UploadMesh(g, verts.ToArray(), idx.ToArray());
    }

    private static MeshGpu CreateCubeMesh(GL g, float size)
    {
        float h = size * 0.5f;
        var verts = new List<float>(24 * 8);
        var idx = new List<uint>(36);

        void AddFace(SN.Vector3 n, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, SN.Vector3 d)
        {
            uint start = (uint)(verts.Count / 8);
            void Push(SN.Vector3 p, float u, float v)
            {
                verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
                verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                verts.Add(u); verts.Add(v);
            }
            Push(a, 0, 0); Push(b, 1, 0); Push(c, 1, 1); Push(d, 0, 1);
            idx.Add(start); idx.Add(start + 1); idx.Add(start + 2);
            idx.Add(start); idx.Add(start + 2); idx.Add(start + 3);
        }

        AddFace(new(0, 0, 1), new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h));
        AddFace(new(0, 0, -1), new(h, -h, -h), new(-h, -h, -h), new(-h, h, -h), new(h, h, -h));
        AddFace(new(1, 0, 0), new(h, -h, h), new(h, -h, -h), new(h, h, -h), new(h, h, h));
        AddFace(new(-1, 0, 0), new(-h, -h, -h), new(-h, -h, h), new(-h, h, h), new(-h, h, -h));
        AddFace(new(0, 1, 0), new(-h, h, h), new(h, h, h), new(h, h, -h), new(-h, h, -h));
        AddFace(new(0, -1, 0), new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h));

        return UploadMesh(g, verts.ToArray(), idx.ToArray());
    }

    private static MeshGpu CreatePlaneMesh(GL g, float size)
    {
        float h = size * 0.5f;
        float[] verts =
        {
            -h, -h, 0f,  0f, 0f, 1f,  0f, 0f,
             h, -h, 0f,  0f, 0f, 1f,  1f, 0f,
             h,  h, 0f,  0f, 0f, 1f,  1f, 1f,
            -h,  h, 0f,  0f, 0f, 1f,  0f, 1f,
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };
        return UploadMesh(g, verts, idx);
    }

    /// <summary>
    /// Helper to resolve a texture path (absolute or project-relative) into a <see cref="Texture2D"/> for preview binding.
    /// </summary>
    public static Texture2D? TryLoadPreviewTexture(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;
        try
        {
            string? resolved = null;
            if (Path.IsPathRooted(texturePath) && File.Exists(texturePath))
            {
                resolved = texturePath;
            }
            else if (ProjectService.Current != null)
            {
                string cand = Path.Combine(ProjectService.Current.RootPath, texturePath);
                if (File.Exists(cand))
                {
                    resolved = cand;
                }
                else
                {
                    // Search Assets folder by filename stem
                    string assetsDir = ProjectService.Current.AssetsPath;
                    if (Directory.Exists(assetsDir))
                    {
                        foreach (var ext in new[] { "", ".png", ".jpg", ".jpeg", ".bmp", ".tga" })
                        {
                            var matches = Directory.GetFiles(assetsDir, texturePath + ext, SearchOption.AllDirectories);
                            if (matches.Length > 0) { resolved = matches[0]; break; }
                        }
                    }
                }
            }

            return resolved != null ? Texture2D.FromFile(resolved) : null;
        }
        catch
        {
            return null;
        }
    }
}
