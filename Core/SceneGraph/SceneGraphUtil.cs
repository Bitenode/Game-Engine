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
        var stack = new Stack<GameObject>();
        for (var n = go; n != null; n = n.Parent) stack.Push(n);
        var w = SN.Matrix4x4.Identity;
        while (stack.Count > 0) w = w * TransformUtil.WorldFromTransform(stack.Pop().Transform);
        return w;
    }

    public static void SetPositionWorld(GameObject go, SN.Vector3 pWorld)
    {
        SN.Matrix4x4 parentW = SN.Matrix4x4.Identity;
        for (var p = go.Parent; p != null; p = p.Parent)
            parentW = TransformUtil.WorldFromTransform(p.Transform) * parentW;

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
            var W = parentW * TransformUtil.WorldFromTransform(go.Transform);

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
}
