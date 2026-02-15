#nullable enable
using System;
using Silk.NET.OpenGL;
using Game_Engine.Core.Rendering.GPU;
using SN = System.Numerics;

namespace Game_Engine.Core;

/// <summary>
/// GPU shadow map using a depth-only FBO.
/// Replaces the old CPU float[] depth buffer.
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
