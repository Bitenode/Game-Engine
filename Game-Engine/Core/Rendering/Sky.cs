#nullable enable
using System;
using Avalonia.Media;
using Silk.NET.OpenGL;
using Game_Engine.Core.Rendering.GPU;
using SN = System.Numerics;

namespace Game_Engine.Core;

/// <summary>
/// GPU sky renderer using a fullscreen quad + sky shader.
/// Replaces the old CPU per-pixel ray-casting.
/// </summary>
public static class Sky
{
    /// <summary>
    /// Render the sky as a fullscreen pass using the GPU.
    /// Must be called within an active GL context.
    /// </summary>
    public static void RenderGPU(
        GL gl,
        ShaderProgram skyShader,
        FullscreenQuad quad,
        ResourceCache cache,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        Color topCol,
        Color botCol,
        SN.Vector3? sunDirWorld = null,
        Texture2D? skyTex = null,
        float skyTexBlend = 0f,
        float skyYawDegrees = 0f)
    {
        // Compute inverse VP for world-ray reconstruction
        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        skyShader.Use();
        skyShader.SetMatrix4("uInvVP", invVP);
        skyShader.SetVector3("uTopColor", new SN.Vector3(topCol.R / 255f, topCol.G / 255f, topCol.B / 255f));
        skyShader.SetVector3("uBotColor", new SN.Vector3(botCol.R / 255f, botCol.G / 255f, botCol.B / 255f));

        bool useSun = sunDirWorld.HasValue && sunDirWorld.Value.LengthSquared() > 0.5f;
        skyShader.SetInt("uUseSun", useSun ? 1 : 0);
        if (useSun)
            skyShader.SetVector3("uSunDir", SN.Vector3.Normalize(sunDirWorld!.Value));

        float yawRad = skyYawDegrees * (MathF.PI / 180f);
        skyShader.SetFloat("uSkyYaw", yawRad);

        bool hasTex = skyTex != null && skyTexBlend > 0.0001f;
        skyShader.SetInt("uHasSkyTex", hasTex ? 1 : 0);
        skyShader.SetFloat("uSkyBlend", skyTexBlend);

        if (hasTex)
        {
            var gpuTex = cache.GetTexture(skyTex!);
            gpuTex.Bind(TextureUnit.Texture0);
            // Equirectangular maps should repeat horizontally but clamp vertically.
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            skyShader.SetTexture("uSkyTex", 0);
        }

        // Draw fullscreen - no depth test, write at far depth
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);

        quad.Draw();

        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }
}
