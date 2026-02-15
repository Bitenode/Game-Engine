#nullable enable
using Avalonia;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class MeshLod
{
    /// Upgrades procedural meshes (sphere/cylinder/cone) based on projected size.
    public static Mesh EnsureProceduralLod(MeshFilter mf,
                         in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var mesh = mf.Mesh!;
        switch (mesh.Kind)
        {
            case MeshKind.Sphere:
                {
                    float rLocal = MeshUtil.ApproxLocalRadius(mesh);
                    float rPx = Core.Projection.EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    var (needLon, needLat) = Mesh.SuggestSphereTesselation(rPx);
                    if (needLon > mesh.TessA || needLat > mesh.TessB)
                    {
                        var upgraded = Mesh.CreateUvSphere(needLon, needLat, rLocal);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cylinder:
                {
                    var (rLocal, hLocal) = MeshUtil.ApproxRadialAndHeight(mesh);
                    float rPx = Core.Projection.EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);
                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCylinder(needSides, rLocal, hLocal, caps: true);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cone:
                {
                    var (rLocal, hLocal) = MeshUtil.ApproxRadialAndHeight(mesh);
                    float rPx = Core.Projection.EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);
                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCone(needSides, rLocal, hLocal, cap: true);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }
        }
        return mesh;
    }
}
