#nullable enable
using Avalonia.Media;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class CameraClear
{
    public static void ClearForCamera(Camera cam,
                            uint[] color, float[] zbuf, int W, int H,
                            in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                            Color skyTop, Color skyBot, SN.Vector3? sunDir,
                            Texture2D? skyTex, float skyBlend,
                            float skyYaw, float seamFeather, bool keyOut, float keyLuma)
    {
        for (int i = 0; i < zbuf.Length; i++) zbuf[i] = 1.1f;

        switch (cam.Clear)
        {
            case ClearFlags.SolidColor:
                {
                    uint bg = ColorUtil.PackBGRA(cam.Background);
                    for (int i = 0; i < color.Length; i++) color[i] = bg;
                    break;
                }
            case ClearFlags.Skybox:
                {
                    Sky.FillWorldUp(color, zbuf, W, H, view, proj,
                       skyTop, skyBot, sunDir,
                       skyTex, skyBlend, skyYaw, seamFeather, keyOut, keyLuma,
                       zWriteNdc: 1f - 1e-6f);
                    break;
                }
            case ClearFlags.DepthOnly:
                // leave color as-is, depth already cleared above
                break;

            case ClearFlags.Nothing:
                // do nothing
                break;
        }
    }
}
