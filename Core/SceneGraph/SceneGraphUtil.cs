#nullable enable
using System.Linq;
using System.Collections.Generic;
using SN = System.Numerics;
using CoreVec3 = Game_Engine.Core.Vector3;
using Game_Engine.Core.Component;

namespace Game_Engine.Core;

public static class SceneGraphUtil
{
    public static SN.Matrix4x4 AccumulateWorld(GameObject go)
    {
        var w = SN.Matrix4x4.Identity;
        for (var n = go; n != null; n = n.Parent)
            w = w * TransformUtil.WorldFromTransform(n.Transform);
        return w;
    }

    public static void SetPositionWorld(GameObject go, SN.Vector3 pWorld)
    {
        SN.Matrix4x4 parentW = SN.Matrix4x4.Identity;
        for (var p = go.Parent; p != null; p = p.Parent)
            parentW = parentW * TransformUtil.WorldFromTransform(p.Transform);

        SN.Matrix4x4.Invert(parentW, out var inv);
        var pLocal = SN.Vector3.Transform(pWorld, inv);
        go.Transform.Position = new CoreVec3(pLocal.X, pLocal.Y, pLocal.Z);
    }

    /// World-space AABB for a subtree. If no mesh, falls back to the object origin.
    public static (SN.Vector3 min, SN.Vector3 max) ComputeWorldAABB(GameObject root)
    {
        bool hasPoint = false;
        SN.Vector3 min = default, max = default;

        void Expand(in SN.Vector3 p)
        {
            if (!hasPoint) { min = max = p; hasPoint = true; }
            else
            {
                min = new SN.Vector3(System.MathF.Min(min.X, p.X), System.MathF.Min(min.Y, p.Y), System.MathF.Min(min.Z, p.Z));
                max = new SN.Vector3(System.MathF.Max(max.X, p.X), System.MathF.Max(max.Y, p.Y), System.MathF.Max(max.Z, p.Z));
            }
        }

        void Walk(GameObject go, SN.Matrix4x4 parentW)
        {
            var W = TransformUtil.WorldFromTransform(go.Transform) * parentW;

            foreach (var mf in go.Behaviors.OfType<MeshFilter>())
            {
                var vtx = mf.Mesh?.Vertices;
                if (mf.Enabled && vtx is { Length: > 0 })
                {
                    for (int i = 0; i < vtx.Length; i++)
                        Expand(SN.Vector3.Transform(vtx[i], W));
                }
            }

            if (!go.Behaviors.OfType<MeshFilter>().Any())
                Expand(SN.Vector3.Transform(SN.Vector3.Zero, W));

            foreach (var ch in go.Children)
                Walk(ch, W);
        }

        Walk(root, SN.Matrix4x4.Identity);
        return (min, max);
    }

    public static (SN.Vector3 min, SN.Vector3 max) ComputeSceneAABB()
    {
        bool any = false;
        SN.Vector3 min = default, max = default;
        void Acc(in SN.Vector3 p)
        {
            if (!any) { min = max = p; any = true; }
            else
            {
                min = new SN.Vector3(System.MathF.Min(min.X, p.X), System.MathF.Min(min.Y, p.Y), System.MathF.Min(min.Z, p.Z));
                max = new SN.Vector3(System.MathF.Max(max.X, p.X), System.MathF.Max(max.Y, p.Y), System.MathF.Max(max.Z, p.Z));
            }
        }
        foreach (var root in SceneService.Root)
        {
            var (rmin, rmax) = ComputeWorldAABB(root);
            Acc(rmin); Acc(rmax);
        }
        if (!any) { min = new SN.Vector3(-1, -1, -1); max = new SN.Vector3(1, 1, 1); }
        return (min, max);
    }

    public static float Clamp01Finite(float v, float def)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return def;
        if (v < 0f) return 0f;
        if (v > 1f) return 1f;
        return v;
    }

    /// Convert normalized camera viewport to pixels inside a framebuffer (dw x dh).
    public static (int x, int y, int w, int h) ViewportPx(Camera cam, int fbW, int fbH)
    {
        float nx = Clamp01Finite(cam.ViewportX, 0f);
        float ny = Clamp01Finite(cam.ViewportY, 0f);
        float nw = Clamp01Finite(cam.ViewportW, 1f);
        float nh = Clamp01Finite(cam.ViewportH, 1f);

        if (nx + nw > 1f) nw = 1f - nx;
        if (ny + nh > 1f) nh = 1f - ny;

        int w = Math.Max(1, (int)Math.Round(nw * fbW));
        int h = Math.Max(1, (int)Math.Round(nh * fbH));
        int x = (int)Math.Round(nx * fbW);
        int y = (int)Math.Round(ny * fbH);

        x = Math.Clamp(x, 0, Math.Max(0, fbW - w));
        y = Math.Clamp(y, 0, Math.Max(0, fbH - h));
        return (x, y, w, h);
    }
}
