#nullable enable
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class CameraOverlay
{
    public static void DrawCameraFrustums(
        DrawingContext ctx,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        Size sz, Camera? activeCam)
    {
        var vpScene = view * proj;

        foreach (var cam in SceneQuery.FindBehaviors<Camera>())
        {
            if (!cam.IsActiveAndEnabled) continue;
            if (cam == activeCam) continue; // skip the one we’re looking through

            // That camera’s inverse VP to recover frustum corners in world space
            var camView = cam.GetViewMatrix();
            var camProj = cam.GetProjectionMatrix(sz);
            if (!SN.Matrix4x4.Invert(camView * camProj, out var invVP)) continue;

            // NDC cube corners (DX-style: z in [0..1])
            var ndc = new SN.Vector4[]
            {
                new(-1,-1,0,1), new( 1,-1,0,1), new( 1, 1,0,1), new(-1, 1,0,1), // near
                new(-1,-1,1,1), new( 1,-1,1,1), new( 1, 1,1,1), new(-1, 1,1,1)  // far
            };

            var cW = new SN.Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var w = SN.Vector4.Transform(ndc[i], invVP);
                float iw = System.Math.Abs(w.W) < 1e-6f ? 1f : 1f / w.W;
                cW[i] = new SN.Vector3(w.X * iw, w.Y * iw, w.Z * iw);
            }

            int[][] edges =
            {
                new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0}, // near
                new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4}, // far
                new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7}  // links
            };

            var col = cam.IsMain ? Colors.Gold : Colors.Orange;
            foreach (var e in edges)
                OverlayPrimitives.DrawLine3D(ctx, vpScene, sz, cW[e[0]], cW[e[1]], col, 1.5);

            // Camera position + tiny basis axes + forward arrow
            if (cam.gameObject is { } go)
            {
                var W = SceneGraphUtil.AccumulateWorld(go);
                var camPos = SN.Vector3.Transform(SN.Vector3.Zero, W);
                float s = 0.18f;

                OverlayPrimitives.DrawLine3D(ctx, vpScene, sz, camPos, camPos + s * SN.Vector3.UnitX, Colors.Red, 2);
                OverlayPrimitives.DrawLine3D(ctx, vpScene, sz, camPos, camPos + s * SN.Vector3.UnitY, Colors.Lime, 2);
                OverlayPrimitives.DrawLine3D(ctx, vpScene, sz, camPos, camPos + s * SN.Vector3.UnitZ, Colors.DeepSkyBlue, 2);

                var fwd = TransformUtil.ForwardFrom(go.Transform);      // -Z forward convention
                OverlayPrimitives.DrawLine3D(ctx, vpScene, sz, camPos, camPos + s * 1.3f * fwd, col, 2);
            }
        }
    }
}
