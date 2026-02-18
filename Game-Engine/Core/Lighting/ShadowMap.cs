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
        float sceneRadius)
    {
        var lightForward = SN.Vector3.Normalize(lightDir);
        var lightUp = Math.Abs(SN.Vector3.Dot(lightForward, SN.Vector3.UnitY)) > 0.99f
            ? SN.Vector3.UnitZ
            : SN.Vector3.UnitY;

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

    /// <summary>
    /// Compute cascade split distances and per-cascade light VP matrices.
    /// Uses a practical split scheme (logarithmic + linear blend).
    /// </summary>
    public void Update(
        SN.Vector3 lightDir,
        in SN.Matrix4x4 cameraView,
        in SN.Matrix4x4 cameraProj,
        float cameraNear,
        float cameraFar,
        float splitLambda = 0.75f)
    {
        float ratio = cameraFar / Math.Max(cameraNear, 0.001f);

        // Compute split distances
        for (int i = 0; i < CascadeCount; i++)
        {
            float p = (i + 1f) / CascadeCount;
            float logSplit = cameraNear * MathF.Pow(ratio, p);
            float uniformSplit = cameraNear + (cameraFar - cameraNear) * p;
            SplitDistances[i] = splitLambda * logSplit + (1f - splitLambda) * uniformSplit;
        }

        // Invert the view-projection to go from NDC back to world
        var viewProj = cameraView * cameraProj;
        SN.Matrix4x4.Invert(viewProj, out var invViewProj);

        var lightForward = SN.Vector3.Normalize(lightDir);
        var lightUp = Math.Abs(SN.Vector3.Dot(lightForward, SN.Vector3.UnitY)) > 0.99f
            ? SN.Vector3.UnitZ
            : SN.Vector3.UnitY;

        for (int i = 0; i < CascadeCount; i++)
        {
            float near = i == 0 ? cameraNear : SplitDistances[i - 1];
            float far = SplitDistances[i];

            // Get frustum corners for this cascade slice
            var corners = GetFrustumCorners(invViewProj, near, far, cameraNear, cameraFar);

            // Compute the cascade bounding sphere center and radius
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

            // Snap to texel grid to prevent shadow shimmer
            radius = MathF.Ceiling(radius * 16f) / 16f;

            var lightPos = center - lightForward * radius * 2f;
            var lightView = SN.Matrix4x4.CreateLookAt(lightPos, center, lightUp);
            var lightProj = SN.Matrix4x4.CreateOrthographic(
                radius * 2f, radius * 2f, 0.1f, radius * 4f);

            LightVPs[i] = lightView * lightProj;
            Cascades[i].LightVP = LightVPs[i];
        }
    }

    /// <summary>
    /// Get the 8 corners of a frustum sub-region defined by near/far fractions.
    /// </summary>
    private static SN.Vector3[] GetFrustumCorners(
        in SN.Matrix4x4 invViewProj,
        float sliceNear, float sliceFar,
        float cameraNear, float cameraFar)
    {
        // Map slice near/far to NDC Z range [0,1] (reversed: near=0, far=1)
        float range = cameraFar - cameraNear;
        float nearNDC = (sliceNear - cameraNear) / range * 2f - 1f;
        float farNDC = (sliceFar - cameraNear) / range * 2f - 1f;

        SN.Vector3[] corners = new SN.Vector3[8];
        int idx = 0;
        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                // Near and far planes
                for (int z = 0; z <= 1; z++)
                {
                    float ndcZ = z == 0 ? nearNDC : farNDC;
                    var pt = new SN.Vector4(
                        x * 2f - 1f,
                        y * 2f - 1f,
                        ndcZ,
                        1f);

                    var world = SN.Vector4.Transform(pt, invViewProj);
                    corners[idx++] = new SN.Vector3(world.X, world.Y, world.Z) / world.W;
                }
            }
        }
        return corners;
    }

    public void Dispose()
    {
        foreach (var cascade in Cascades)
            cascade.Dispose();
    }
}
