#nullable enable
using System;
using Silk.NET.OpenGL;
using Game_Engine.Core.Rendering.GPU;
using SN = System.Numerics;

namespace Game_Engine.Core;

/// <summary>
/// GPU grid renderer using a fullscreen quad + grid shader.
/// Replaces the old CPU per-pixel ray-casting.
/// </summary>
public static class Grid
{
    /// <summary>
    /// Render the infinite ground grid as a fullscreen GPU pass with proper depth.
    /// Must be called AFTER the sky pass and BEFORE or AFTER mesh rendering.
    /// </summary>
    public static void RenderGPU(
        GL gl,
        ShaderProgram gridShader,
        FullscreenQuad quad,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        float step = 1f,
        int majorEvery = 5)
    {
        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        // Camera world position
        SN.Matrix4x4.Invert(view, out var invView);
        var camPos = new SN.Vector3(invView.M41, invView.M42, invView.M43);

        gridShader.Use();
        gridShader.SetMatrix4("uInvVP", invVP);
        gridShader.SetMatrix4("uVP", vp);
        gridShader.SetVector3("uCamPos", camPos);
        gridShader.SetFloat("uGridStep", step);
        gridShader.SetInt("uMajorEvery", majorEvery);

        // Grid draws with alpha blending over whatever is behind
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Enable depth test so meshes in front occlude the grid
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);

        quad.Draw();

        gl.Disable(EnableCap.Blend);
    }
}
