#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// std140 layout matching <c>Common/FrameGlobals.glsl</c> (binding 0, 352 bytes).
/// Every member is 16-byte aligned (<c>mat4</c> = 64 bytes, <c>vec4</c> = 16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FrameUboData
{
    public SN.Matrix4x4 View;           // offset 0   (64 bytes)
    public SN.Matrix4x4 Proj;           // offset 64  (64 bytes)
    public SN.Matrix4x4 ViewProj;       // offset 128 (64 bytes)
    public SN.Matrix4x4 InvView;        // offset 192 (64 bytes)
    public SN.Matrix4x4 InvProj;        // offset 256 (64 bytes)
    public SN.Vector4 CamPosTime;       // offset 320 (16 bytes): xyz = camera world pos, w = elapsed time
    public SN.Vector4 ScreenNearFar;    // offset 336 (16 bytes): xy = viewport W/H, z = near, w = far
}

/// <summary>
/// std140 layout matching <c>Common/LightingGlobals.glsl</c> (binding 1, 544 bytes).
/// Every member is 16-byte aligned (<c>vec4</c> = 16 bytes, <c>mat4</c> = 64 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LightingUboData
{
    public SN.Vector4 LightDirDiffuse;    // offset 0   (16 bytes): xyz = directional light dir, w = diffuseK
    public SN.Vector4 LightColorAmbient;  // offset 16  (16 bytes): xyz = light color, w = ambient
    public SN.Vector4 LightPosRange;      // offset 32  (16 bytes): xyz = primary point light pos, w = range
    public SN.Vector4 LightFlags;         // offset 48  (16 bytes): x = lightIsPoint, y = hasShadow, z = cascadeCount, w = pointCount
    public SN.Matrix4x4 ShadowVP;         // offset 64  (64 bytes)
    public SN.Matrix4x4 ShadowVPC0;       // offset 128 (64 bytes): uShadowVPC[0]
    public SN.Matrix4x4 ShadowVPC1;       // offset 192 (64 bytes): uShadowVPC[1]
    public SN.Matrix4x4 ShadowVPC2;       // offset 256 (64 bytes): uShadowVPC[2]
    public SN.Matrix4x4 ShadowVPC3;       // offset 320 (64 bytes): uShadowVPC[3]
    public SN.Vector4 CascadeSplits;      // offset 384 (16 bytes): uCascadeSplits (x=c0, y=c1, z=c2, w=c3)
    public SN.Vector4 PointPosRange0;     // offset 400 (16 bytes): uPointPosRange[0]
    public SN.Vector4 PointPosRange1;     // offset 416 (16 bytes): uPointPosRange[1]
    public SN.Vector4 PointPosRange2;     // offset 432 (16 bytes): uPointPosRange[2]
    public SN.Vector4 PointPosRange3;     // offset 448 (16 bytes): uPointPosRange[3]
    public SN.Vector4 PointColor0;        // offset 464 (16 bytes): uPointColor[0]
    public SN.Vector4 PointColor1;        // offset 480 (16 bytes): uPointColor[1]
    public SN.Vector4 PointColor2;        // offset 496 (16 bytes): uPointColor[2]
    public SN.Vector4 PointColor3;        // offset 512 (16 bytes): uPointColor[3]
    public SN.Vector4 WindDirTime;        // offset 528 (16 bytes): xyz = wind dir, w = wind time
}

/// <summary>
/// Per-GL-context owner of the engine's global Uniform Buffer Objects:
/// <list type="bullet">
///   <item>Binding 0: <c>FrameGlobals</c> (<see cref="FrameUboData"/>)</item>
///   <item>Binding 1: <c>LightingGlobals</c> (<see cref="LightingUboData"/>)</item>
///   <item>Binding 2: <c>BoneGlobals</c> (<see cref="BoneUniformBuffer"/>)</item>
/// </list>
/// </summary>
public sealed class GlobalUniforms : IDisposable
{
    public const uint FrameBindingPoint = 0;
    public const uint LightingBindingPoint = 1;
    public const uint BoneBindingPoint = 2;

    public const int ExpectedFrameUboSize = 352;
    public const int ExpectedLightingUboSize = 544;

    private static readonly ConditionalWeakTable<GL, GlobalUniforms> s_byContext = new();
    private static readonly object s_registryLock = new();

    static GlobalUniforms()
    {
        int frameSize = Unsafe.SizeOf<FrameUboData>();
        int lightSize = Unsafe.SizeOf<LightingUboData>();
        Debug.Assert(frameSize == ExpectedFrameUboSize,
            $"FrameUboData std140 size mismatch: expected {ExpectedFrameUboSize}, got {frameSize}");
        Debug.Assert(lightSize == ExpectedLightingUboSize,
            $"LightingUboData std140 size mismatch: expected {ExpectedLightingUboSize}, got {lightSize}");
    }

    private readonly GL _gl;
    private readonly UniformBuffer<FrameUboData> _frameUbo;
    private readonly UniformBuffer<LightingUboData> _lightingUbo;
    private readonly BoneUniformBuffer _boneUbo;
    private FrameUboData _frameData;
    private LightingUboData _lightingData;
    private bool _disposed;

    public bool IsES { get; }
    public int MaxBones => _boneUbo.MaxBones;
    public ref readonly FrameUboData FrameData => ref _frameData;
    public ref readonly LightingUboData LightingData => ref _lightingData;

    private GlobalUniforms(GL gl, bool isES)
    {
        _gl = gl;
        IsES = isES;
        int maxBones = isES ? 48 : 128;
        _frameUbo = new UniformBuffer<FrameUboData>(gl);
        _lightingUbo = new UniformBuffer<LightingUboData>(gl);
        _boneUbo = new BoneUniformBuffer(gl, maxBones);

        // Initialize sensible defaults
        _frameData = new FrameUboData
        {
            View = SN.Matrix4x4.Identity,
            Proj = SN.Matrix4x4.Identity,
            ViewProj = SN.Matrix4x4.Identity,
            InvView = SN.Matrix4x4.Identity,
            InvProj = SN.Matrix4x4.Identity,
            CamPosTime = SN.Vector4.Zero,
            ScreenNearFar = new SN.Vector4(1f, 1f, 0.1f, 1000f)
        };
        _lightingData = new LightingUboData
        {
            LightDirDiffuse = new SN.Vector4(0f, -1f, 0f, 1f),
            LightColorAmbient = new SN.Vector4(1f, 1f, 1f, 0.2f),
            LightPosRange = SN.Vector4.Zero,
            LightFlags = SN.Vector4.Zero,
            ShadowVP = SN.Matrix4x4.Identity,
            ShadowVPC0 = SN.Matrix4x4.Identity,
            ShadowVPC1 = SN.Matrix4x4.Identity,
            ShadowVPC2 = SN.Matrix4x4.Identity,
            ShadowVPC3 = SN.Matrix4x4.Identity,
            CascadeSplits = new SN.Vector4(1000f, 1000f, 1000f, 1000f),
            WindDirTime = new SN.Vector4(1f, 0f, 0f, 0f)
        };

        BindAll();
        _frameUbo.Update(in _frameData);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Get or create the <see cref="GlobalUniforms"/> instance for <paramref name="gl"/>.</summary>
    public static GlobalUniforms GetOrCreate(GL gl, bool isES)
    {
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing) && !existing._disposed)
                return existing;

            var created = new GlobalUniforms(gl, isES);
            s_byContext.AddOrUpdate(gl, created);
            return created;
        }
    }

    /// <summary>
    /// Retrieve the <see cref="GlobalUniforms"/> for the active <see cref="GL"/> context
    /// (auto-creating with ES detection via <see cref="ShaderLibrary"/> if needed).
    /// </summary>
    public static GlobalUniforms ForContext(GL gl)
    {
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing) && !existing._disposed)
                return existing;
        }

        bool isES = true;
        if (ShaderLibrary.TryGet(gl, out var lib) && lib != null)
        {
            isES = lib.IsES;
        }
        else
        {
            try
            {
                isES = gl.GetStringS(StringName.Version)?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { isES = true; }
        }

        return GetOrCreate(gl, isES);
    }

    /// <summary>Dispose and detach the <see cref="GlobalUniforms"/> associated with <paramref name="gl"/>.</summary>
    public static void RemoveContext(GL gl)
    {
        GlobalUniforms? inst = null;
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing))
            {
                s_byContext.Remove(gl);
                inst = existing;
            }
        }
        inst?.Dispose();
    }

    /// <summary>Bind all three global UBOs to binding points 0, 1, and 2.</summary>
    public void BindAll()
    {
        if (_disposed) return;
        _frameUbo.Bind(FrameBindingPoint);
        _lightingUbo.Bind(LightingBindingPoint);
        _boneUbo.Bind(BoneBindingPoint);
    }

    /// <summary>Upload camera and frame parameters into <c>FrameGlobals</c> (binding 0).</summary>
    public void UpdateFrame(
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        SN.Vector3 camPos,
        float elapsedTime,
        float viewportWidth = 0f,
        float viewportHeight = 0f,
        float nearPlane = 0.1f,
        float farPlane = 1000f)
    {
        if (_disposed) return;

        var viewProj = view * proj;
        if (!SN.Matrix4x4.Invert(view, out var invView))
            invView = SN.Matrix4x4.Identity;
        if (!SN.Matrix4x4.Invert(proj, out var invProj))
            invProj = SN.Matrix4x4.Identity;

        _frameData.View = view;
        _frameData.Proj = proj;
        _frameData.ViewProj = viewProj;
        _frameData.InvView = invView;
        _frameData.InvProj = invProj;
        _frameData.CamPosTime = new SN.Vector4(camPos.X, camPos.Y, camPos.Z, elapsedTime);
        if (viewportWidth > 0f && viewportHeight > 0f)
            _frameData.ScreenNearFar = new SN.Vector4(viewportWidth, viewportHeight, nearPlane, farPlane);

        _frameUbo.Bind(FrameBindingPoint);
        _frameUbo.Update(in _frameData);
    }

    /// <summary>Upload lighting, shadow, and wind parameters into <c>LightingGlobals</c> (binding 1).</summary>
    public void UpdateLighting(
        SN.Vector3 lightDir,
        float diffuseK,
        SN.Vector3 lightColor,
        float ambient,
        bool lightIsPoint,
        SN.Vector3 lightPosW,
        float lightRange,
        bool hasShadow,
        in SN.Matrix4x4 shadowVP,
        int cascadeCount = 1,
        SN.Vector4? cascadeSplits = null,
        SN.Vector3? windDir = null,
        float? windTime = null)
    {
        if (_disposed) return;

        if (lightColor == default)
            lightColor = SN.Vector3.One;

        _lightingData.LightDirDiffuse = new SN.Vector4(lightDir.X, lightDir.Y, lightDir.Z, diffuseK);
        _lightingData.LightColorAmbient = new SN.Vector4(lightColor.X, lightColor.Y, lightColor.Z, ambient);
        _lightingData.LightPosRange = new SN.Vector4(lightPosW.X, lightPosW.Y, lightPosW.Z, lightRange);
        _lightingData.LightFlags = new SN.Vector4(
            lightIsPoint ? 1f : 0f,
            hasShadow ? 1f : 0f,
            hasShadow ? Math.Clamp(cascadeCount, 1, 4) : 0f,
            _lightingData.LightFlags.W);
        _lightingData.ShadowVP = shadowVP;
        _lightingData.ShadowVPC0 = shadowVP;
        if (cascadeSplits.HasValue)
            _lightingData.CascadeSplits = cascadeSplits.Value;
        else
            _lightingData.CascadeSplits = new SN.Vector4(1000f, 1000f, 1000f, 1000f);

        if (windDir.HasValue || windTime.HasValue)
        {
            var wd = windDir ?? new SN.Vector3(_lightingData.WindDirTime.X, _lightingData.WindDirTime.Y, _lightingData.WindDirTime.Z);
            float wt = windTime ?? _lightingData.WindDirTime.W;
            _lightingData.WindDirTime = new SN.Vector4(wd.X, wd.Y, wd.Z, wt);
        }

        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Upload the full <see cref="LightingUboData"/> struct directly.</summary>
    public void UpdateLighting(in LightingUboData data)
    {
        if (_disposed) return;
        _lightingData = data;
        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Update cascaded shadow matrices and split distances in <c>LightingGlobals</c>.</summary>
    public void UpdateCascadedShadows(
        int cascadeCount,
        in SN.Matrix4x4 vp0,
        in SN.Matrix4x4 vp1,
        in SN.Matrix4x4 vp2,
        in SN.Matrix4x4 vp3,
        SN.Vector4 splits)
    {
        if (_disposed) return;
        _lightingData.LightFlags.Y = 1f;
        _lightingData.LightFlags.Z = Math.Clamp(cascadeCount, 1, 4);
        _lightingData.ShadowVP = vp0;
        _lightingData.ShadowVPC0 = vp0;
        _lightingData.ShadowVPC1 = vp1;
        _lightingData.ShadowVPC2 = vp2;
        _lightingData.ShadowVPC3 = vp3;
        _lightingData.CascadeSplits = splits;
        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Update the 4 nearest point lights in <c>LightingGlobals</c> with zero allocations.</summary>
    public void UpdatePointLights(
        int count,
        SN.Vector4 posRange0, SN.Vector4 color0,
        SN.Vector4 posRange1, SN.Vector4 color1,
        SN.Vector4 posRange2, SN.Vector4 color2,
        SN.Vector4 posRange3, SN.Vector4 color3)
    {
        if (_disposed) return;
        _lightingData.LightFlags.W = Math.Clamp(count, 0, 4);
        _lightingData.PointPosRange0 = posRange0;
        _lightingData.PointPosRange1 = posRange1;
        _lightingData.PointPosRange2 = posRange2;
        _lightingData.PointPosRange3 = posRange3;
        _lightingData.PointColor0 = color0;
        _lightingData.PointColor1 = color1;
        _lightingData.PointColor2 = color2;
        _lightingData.PointColor3 = color3;
        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Update wind direction, wind time, and ambient override when changed (avoids redundant uploads).</summary>
    public void UpdateWindAndAmbient(SN.Vector3 windDir, float windTime, float ambient)
    {
        if (_disposed) return;
        var nextWind = new SN.Vector4(windDir.X, windDir.Y, windDir.Z, windTime);
        if (_lightingData.WindDirTime == nextWind && MathF.Abs(_lightingData.LightColorAmbient.W - ambient) < 1e-6f)
            return;

        _lightingData.WindDirTime = nextWind;
        _lightingData.LightColorAmbient.W = ambient;
        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Update only ambient in <c>LightingGlobals</c> if it changed.</summary>
    public void UpdateAmbientIfChanged(float ambient)
    {
        if (_disposed) return;
        if (MathF.Abs(_lightingData.LightColorAmbient.W - ambient) < 1e-6f)
            return;

        _lightingData.LightColorAmbient.W = ambient;
        _lightingUbo.Bind(LightingBindingPoint);
        _lightingUbo.Update(in _lightingData);
    }

    /// <summary>Upload skeletal bone matrices into <c>BoneGlobals</c> (binding 2) with zero allocations.</summary>
    public void UpdateBones(ReadOnlySpan<SN.Matrix4x4> matrices)
    {
        if (_disposed || matrices.IsEmpty) return;
        _boneUbo.Bind(BoneBindingPoint);
        _boneUbo.UpdateMatrices(matrices);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _frameUbo.Dispose();
        _lightingUbo.Dispose();
        _boneUbo.Dispose();

        lock (s_registryLock)
        {
            s_byContext.Remove(_gl);
        }
    }
}
