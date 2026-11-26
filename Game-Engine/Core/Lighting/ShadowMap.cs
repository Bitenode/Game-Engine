#nullable enable
using System;
using Silk.NET.OpenGL;
using Game_Engine.Core.Rendering.GPU;
using SN = System.Numerics;

namespace Game_Engine.Core;

/// <summary>
/// GPU shadow map using a depth-only FBO.
/// Supports Cascaded Shadow Maps (CSM) for directional lights.
/// </summary>
public sealed class ShadowMapGPU : IDisposable
{
    public SN.Matrix4x4 LightVP { get; set; }
    public GPUFramebuffer FBO { get; }
    public int Width { get; }
    public int Height { get; }
    public float Bias { get; set; } = 0.005f;

    public ShadowMapGPU(GL gl, int width = 2048, int height = 2048)
    {
        Width = width;
        Height = height;
        FBO = new GPUFramebuffer(gl);
        FBO.SetupDepthOnly(width, height);
    }

    /// <summary>
    /// Begin shadow map rendering: bind FBO and clear depth.
    /// </summary>
    public void Begin(GL gl)
    {
        FBO.Bind();
        gl.Clear(ClearBufferMask.DepthBufferBit);
    }

    /// <summary>
    /// End shadow map rendering: restore previous framebuffer.
    /// </summary>
    public void End(GL gl, uint defaultFB = 0)
    {
        FBO.Unbind(defaultFB);
    }

    /// <summary>
    /// Build a light view-projection matrix for a directional light.
    /// </summary>
    public static SN.Matrix4x4 BuildDirectionalLightVP(
        SN.Vector3 lightDir,
        SN.Vector3 sceneCenter,
        float sceneRadius,
        int shadowMapResolution = 0)
    {
        var lightForward = SN.Vector3.Normalize(lightDir);
        var lightUp = Math.Abs(SN.Vector3.Dot(lightForward, SN.Vector3.UnitY)) > 0.99f
            ? SN.Vector3.UnitZ
            : SN.Vector3.UnitY;

        // Snap the moving light volume to shadow texels. Without this, a camera-
        // followed orthographic map shifts every sample and dark bands crawl over terrain.
        if (shadowMapResolution > 0)
        {
            var side = SN.Vector3.Normalize(SN.Vector3.Cross(lightUp, lightForward));
            var vertical = SN.Vector3.Normalize(SN.Vector3.Cross(lightForward, side));
            float texelWorld = (sceneRadius * 2f) / Math.Max(1, shadowMapResolution);
            float x = SN.Vector3.Dot(sceneCenter, side);
            float y = SN.Vector3.Dot(sceneCenter, vertical);
            float snappedX = MathF.Round(x / texelWorld) * texelWorld;
            float snappedY = MathF.Round(y / texelWorld) * texelWorld;
            sceneCenter += side * (snappedX - x) + vertical * (snappedY - y);
        }

        var lightPos = sceneCenter - lightForward * sceneRadius * 2f;
        var lightView = SN.Matrix4x4.CreateLookAt(lightPos, sceneCenter, lightUp);
        var lightProj = SN.Matrix4x4.CreateOrthographic(
            sceneRadius * 2f, sceneRadius * 2f, 0.1f, sceneRadius * 4f);

        return lightView * lightProj;
    }

    public void Dispose()
    {
        FBO.Dispose();
    }
}

/// <summary>
/// Cascaded Shadow Map system. Splits the camera frustum into multiple cascades,
/// each with its own shadow map and light view-projection matrix.
/// </summary>
public sealed class CascadedShadowMap : IDisposable
{
    /// <summary>Number of cascades (1-4).</summary>
    public int CascadeCount { get; }

    /// <summary>Per-cascade shadow maps.</summary>
    public ShadowMapGPU[] Cascades { get; }

    /// <summary>Per-cascade light view-projection matrices.</summary>
    public SN.Matrix4x4[] LightVPs { get; }

    /// <summary>Far plane split distances (in view space, positive values).</summary>
    public float[] SplitDistances { get; }

    public CascadedShadowMap(GL gl, int cascadeCount = 4, int resolution = 2048)
    {
        CascadeCount = Math.Clamp(cascadeCount, 1, 4);
        Cascades = new ShadowMapGPU[CascadeCount];
        LightVPs = new SN.Matrix4x4[CascadeCount];
        SplitDistances = new float[CascadeCount];

        for (int i = 0; i < CascadeCount; i++)
            Cascades[i] = new ShadowMapGPU(gl, resolution, resolution);
    }

    /// <summary>Maximum shadow distance in world units (clamps far camera planes such as planets).</summary>
    public float MaxShadowDistance { get; set; } = 160f;

    /// <summary>
    /// Compute cascade split distances and per-cascade light VP matrices.
    /// Uses a practical split scheme (logarithmic + linear blend) and light-space texel snapping.
    /// </summary>
    public void Update(
        SN.Vector3 lightDir,
        in SN.Matrix4x4 cameraView,
        in SN.Matrix4x4 cameraProj,
        float cameraNear,
        float cameraFar,
        float splitLambda = 0.75f)
    {
        float nearClip = MathF.Max(cameraNear, 0.05f);
        float maxDist = MathF.Min(MathF.Max(cameraFar, nearClip + 1f), MathF.Max(MaxShadowDistance, nearClip + 10f));
        float ratio = maxDist / nearClip;

        // Compute practical split distances (logarithmic + linear blend)
        for (int i = 0; i < CascadeCount; i++)
        {
            float p = (i + 1f) / CascadeCount;
            float logSplit = nearClip * MathF.Pow(ratio, p);
            float uniformSplit = nearClip + (maxDist - nearClip) * p;
            SplitDistances[i] = splitLambda * logSplit + (1f - splitLambda) * uniformSplit;
        }

        if (!SN.Matrix4x4.Invert(cameraView, out var invView))
            invView = SN.Matrix4x4.Identity;

        var lightForward = lightDir.LengthSquared() > 1e-8f
            ? SN.Vector3.Normalize(lightDir)
            : SN.Vector3.Normalize(new SN.Vector3(-0.35f, -0.8f, 0.45f));
        var lightUp = MathF.Abs(SN.Vector3.Dot(lightForward, SN.Vector3.UnitY)) > 0.99f
            ? SN.Vector3.UnitZ
            : SN.Vector3.UnitY;
        var lightRight = SN.Vector3.Normalize(SN.Vector3.Cross(lightUp, lightForward));
        var lightOrthoUp = SN.Vector3.Normalize(SN.Vector3.Cross(lightForward, lightRight));

        bool isOrtho = MathF.Abs(cameraProj.M34) < 1e-5f;
        float invM11 = MathF.Abs(cameraProj.M11) > 1e-6f ? 1f / MathF.Abs(cameraProj.M11) : 1f;
        float invM22 = MathF.Abs(cameraProj.M22) > 1e-6f ? 1f / MathF.Abs(cameraProj.M22) : 1f;

        Span<SN.Vector3> corners = stackalloc SN.Vector3[8];

        for (int i = 0; i < CascadeCount; i++)
        {
            float near = i == 0 ? nearClip : SplitDistances[i - 1];
            float far = SplitDistances[i];

            GetFrustumSliceCorners(in invView, isOrtho, invM11, invM22, near, far, corners);

            // Compute bounding sphere center & radius (rotation-invariant -> zero shimmer on rotation)
            SN.Vector3 center = SN.Vector3.Zero;
            for (int j = 0; j < 8; j++)
                center += corners[j];
            center /= 8f;

            float radius = 0f;
            for (int j = 0; j < 8; j++)
            {
                float dist = (corners[j] - center).Length();
                if (dist > radius) radius = dist;
            }

            // Round radius slightly so small floating-point drift doesn't rescale texels
            radius = MathF.Max(1f, MathF.Ceiling(radius * 16f) / 16f);

            // Texel-snap the light center in light ortho space to eliminate shadow swimming
            int resolution = Math.Max(1, Cascades[i].Width);
            float texelWorld = (radius * 2f) / resolution;
            float sx = SN.Vector3.Dot(center, lightRight);
            float sy = SN.Vector3.Dot(center, lightOrthoUp);
            float snappedX = MathF.Round(sx / texelWorld) * texelWorld;
            float snappedY = MathF.Round(sy / texelWorld) * texelWorld;
            center += lightRight * (snappedX - sx) + lightOrthoUp * (snappedY - sy);

            // Pull light camera back enough so tall/behind-frustum casters still cast shadows in
            float pullBack = MathF.Max(radius * 2.5f, 40f);
            var lightPos = center - lightForward * pullBack;
            var lightView = SN.Matrix4x4.CreateLookAt(lightPos, center, lightUp);
            var lightProj = SN.Matrix4x4.CreateOrthographic(
                radius * 2f, radius * 2f, 0.1f, pullBack + radius * 2f);

            LightVPs[i] = lightView * lightProj;
            Cascades[i].LightVP = LightVPs[i];
        }
    }

    /// <summary>
    /// Compute the 8 world-space corners of a view-space frustum slice [sliceNear, sliceFar].
    /// </summary>
    private static void GetFrustumSliceCorners(
        in SN.Matrix4x4 invView,
        bool isOrtho,
        float invM11,
        float invM22,
        float sliceNear,
        float sliceFar,
        Span<SN.Vector3> corners)
    {
        float nearW = isOrtho ? invM11 : sliceNear * invM11;
        float nearH = isOrtho ? invM22 : sliceNear * invM22;
        float farW  = isOrtho ? invM11 : sliceFar  * invM11;
        float farH  = isOrtho ? invM22 : sliceFar  * invM22;

        corners[0] = SN.Vector3.Transform(new SN.Vector3(-nearW, -nearH, -sliceNear), invView);
        corners[1] = SN.Vector3.Transform(new SN.Vector3( nearW, -nearH, -sliceNear), invView);
        corners[2] = SN.Vector3.Transform(new SN.Vector3( nearW,  nearH, -sliceNear), invView);
        corners[3] = SN.Vector3.Transform(new SN.Vector3(-nearW,  nearH, -sliceNear), invView);

        corners[4] = SN.Vector3.Transform(new SN.Vector3(-farW, -farH, -sliceFar), invView);
        corners[5] = SN.Vector3.Transform(new SN.Vector3( farW, -farH, -sliceFar), invView);
        corners[6] = SN.Vector3.Transform(new SN.Vector3( farW,  farH, -sliceFar), invView);
        corners[7] = SN.Vector3.Transform(new SN.Vector3(-farW,  farH, -sliceFar), invView);
    }

    public void Dispose()
    {
        foreach (var cascade in Cascades)
            cascade.Dispose();
    }
}
