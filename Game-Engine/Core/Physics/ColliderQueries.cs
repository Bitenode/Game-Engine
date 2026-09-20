#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics;

/// <summary>Narrow-phase tests against a single collider.</summary>
public static class ColliderQueries
{
    public static bool Raycast(Collider c, SN.Vector3 origin, SN.Vector3 dir, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        if (c is PlanetCollider) return false;

        if (c is MeshCollider mesh)
            return RaycastMesh(mesh, origin, dir, maxDist, out hit);

        var aabb = c.GetWorldAABB();
        if (!GeometryQueries.RayAABB(origin, dir, aabb.Min, aabb.Max, out float t, out var n) || t < 0f || t > maxDist)
            return false;

        hit = new CollisionWorld.RaycastHit
        {
            Point = origin + dir * t,
            Normal = n,
            Distance = t,
            Collider = c
        };
        return true;
    }

    public static bool SphereCast(Collider c, SN.Vector3 origin, SN.Vector3 dir, float radius, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        if (c is PlanetCollider) return false;

        if (c is MeshCollider mesh)
            return SphereCastMesh(mesh, origin, dir, radius, maxDist, out hit);

        var aabb = c.GetWorldAABB();
        var expand = new SN.Vector3(radius);
        if (!GeometryQueries.RayAABB(origin, dir, aabb.Min - expand, aabb.Max + expand, out float t, out var n)
            || t < 0f || t > maxDist)
            return false;

        hit = new CollisionWorld.RaycastHit
        {
            Point = origin + dir * t,
            Normal = n,
            Distance = t,
            Collider = c
        };
        return true;
    }

    public static bool CapsuleCast(Collider c, SN.Vector3 p0, SN.Vector3 p1, float radius, SN.Vector3 dir, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        if (c is PlanetCollider) return false;

        if (c is MeshCollider mesh)
            return CapsuleCastMesh(mesh, p0, p1, radius, dir, maxDist, out hit);

        var mid = (p0 + p1) * 0.5f;
        return SphereCast(c, mid, dir, radius + SN.Vector3.Distance(p0, p1) * 0.5f, maxDist, out hit);
    }

    static readonly List<int> TriScratch = new(128);

    static bool RaycastMesh(MeshCollider mesh, SN.Vector3 origin, SN.Vector3 dir, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        float best = maxDist;
        SN.Vector3 bestN = SN.Vector3.UnitY;
        bool found = false;
        var end = origin + dir * maxDist;
        var qMin = SN.Vector3.Min(origin, end);
        var qMax = SN.Vector3.Max(origin, end);

        foreach (var (m, world) in mesh.EnumerateTargetMeshesWorld())
        {
            var tris = m.TriIndices;
            var verts = m.Vertices;
            if (tris == null || verts == null) continue;
            TriScratch.Clear();
            MeshSpatialIndex.For(m).QueryWorldAabb(world, qMin, qMax, TriScratch);
            for (int n = 0; n < TriScratch.Count; n++)
            {
                int i = TriScratch[n];
                if ((uint)(i + 2) >= (uint)tris.Length) continue;
                var a = SN.Vector3.Transform(verts[tris[i]], world);
                var b = SN.Vector3.Transform(verts[tris[i + 1]], world);
                var c = SN.Vector3.Transform(verts[tris[i + 2]], world);
                if (!GeometryQueries.RayTriangle(origin, dir, a, b, c, out float t) || t > best)
                    continue;
                best = t;
                var nrm = SN.Vector3.Cross(b - a, c - a);
                if (nrm.LengthSquared() > 1e-12f) bestN = SN.Vector3.Normalize(nrm);
                if (SN.Vector3.Dot(bestN, dir) > 0f) bestN = -bestN;
                found = true;
            }
        }

        if (!found) return false;
        hit = new CollisionWorld.RaycastHit
        {
            Point = origin + dir * best,
            Normal = bestN,
            Distance = best,
            Collider = mesh
        };
        return true;
    }

    static bool SphereCastMesh(MeshCollider mesh, SN.Vector3 origin, SN.Vector3 dir, float radius, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        float best = maxDist;
        SN.Vector3 bestN = SN.Vector3.UnitY;
        bool found = false;
        var end = origin + dir * maxDist;
        var pad = new SN.Vector3(Math.Max(0.01f, radius));
        var qMin = SN.Vector3.Min(origin, end) - pad;
        var qMax = SN.Vector3.Max(origin, end) + pad;

        foreach (var (m, world) in mesh.EnumerateTargetMeshesWorld())
        {
            var tris = m.TriIndices;
            var verts = m.Vertices;
            if (tris == null || verts == null) continue;
            TriScratch.Clear();
            MeshSpatialIndex.For(m).QueryWorldAabb(world, qMin, qMax, TriScratch);
            for (int n = 0; n < TriScratch.Count; n++)
            {
                int i = TriScratch[n];
                if ((uint)(i + 2) >= (uint)tris.Length) continue;
                var a = SN.Vector3.Transform(verts[tris[i]], world);
                var b = SN.Vector3.Transform(verts[tris[i + 1]], world);
                var c = SN.Vector3.Transform(verts[tris[i + 2]], world);
                if (!GeometryQueries.SphereCastTriangle(origin, dir, radius, a, b, c, best, out float t, out var nrm))
                    continue;
                best = t;
                bestN = nrm;
                found = true;
            }
        }

        if (!found) return false;
        hit = new CollisionWorld.RaycastHit
        {
            Point = origin + dir * best,
            Normal = bestN,
            Distance = best,
            Collider = mesh
        };
        return true;
    }

    static bool CapsuleCastMesh(MeshCollider mesh, SN.Vector3 p0, SN.Vector3 p1, float radius, SN.Vector3 dir, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        float best = maxDist;
        SN.Vector3 bestN = SN.Vector3.UnitY;
        bool found = false;
        var mid = (p0 + p1) * 0.5f;
        var end = mid + dir * maxDist;
        float extra = SN.Vector3.Distance(p0, p1) * 0.5f + radius;
        var pad = new SN.Vector3(Math.Max(0.01f, extra));
        var qMin = SN.Vector3.Min(SN.Vector3.Min(p0, p1), end) - pad;
        var qMax = SN.Vector3.Max(SN.Vector3.Max(p0, p1), end) + pad;

        foreach (var (m, world) in mesh.EnumerateTargetMeshesWorld())
        {
            var tris = m.TriIndices;
            var verts = m.Vertices;
            if (tris == null || verts == null) continue;
            TriScratch.Clear();
            MeshSpatialIndex.For(m).QueryWorldAabb(world, qMin, qMax, TriScratch);
            for (int n = 0; n < TriScratch.Count; n++)
            {
                int i = TriScratch[n];
                if ((uint)(i + 2) >= (uint)tris.Length) continue;
                var a = SN.Vector3.Transform(verts[tris[i]], world);
                var b = SN.Vector3.Transform(verts[tris[i + 1]], world);
                var c = SN.Vector3.Transform(verts[tris[i + 2]], world);
                if (!GeometryQueries.CapsuleCastTriangle(p0, p1, radius, dir, a, b, c, best, out float t, out var nrm))
                    continue;
                best = t;
                bestN = nrm;
                found = true;
            }
        }

        if (!found) return false;
        hit = new CollisionWorld.RaycastHit
        {
            Point = p0 + dir * best,
            Normal = bestN,
            Distance = best,
            Collider = mesh
        };
        return true;
    }
}
