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

        if (c is CapsuleCollider capsule)
            return RaycastCapsule(capsule, origin, dir, maxDist, out hit);

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

    /// <summary>
    /// Push the first capsule out of the second. False when they do not overlap.
    /// </summary>
    public static bool TrySeparateCapsules(
        SN.Vector3 a0, SN.Vector3 b0, float r0,
        SN.Vector3 a1, SN.Vector3 b1, float r1,
        out SN.Vector3 push)
    {
        push = default;
        ClosestPointsOnSegments(a0, b0, a1, b1, out var c0, out var c1);
        var away = c0 - c1;
        float dist = away.Length();
        float need = MathF.Max(0.001f, r0) + MathF.Max(0.001f, r1);
        if (dist >= need)
            return false;

        SN.Vector3 n;
        if (dist > 1e-5f)
            n = away / dist;
        else
        {
            var axis = b0 - a0;
            if (axis.LengthSquared() < 1e-8f)
                axis = SN.Vector3.UnitY;
            var side = SN.Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX);
            n = side.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(side) : SN.Vector3.UnitX;
        }

        push = n * (need - dist + 0.001f);
        return true;
    }

    static bool RaycastCapsule(CapsuleCollider capsule, SN.Vector3 origin, SN.Vector3 dir, float maxDist,
        out CollisionWorld.RaycastHit hit)
    {
        hit = default;
        if (dir.LengthSquared() < 1e-12f || maxDist <= 0f)
            return false;
        dir = SN.Vector3.Normalize(dir);
        capsule.GetWorldCapsule(out var a, out var b, out float radius);
        radius = MathF.Max(0.001f, radius);

        float along = DistancePointToSegment(origin, a, b, out var closest);
        if (along <= radius)
        {
            var n = origin - closest;
            hit = new CollisionWorld.RaycastHit
            {
                Point = origin,
                Normal = n.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY,
                Distance = 0f,
                Collider = capsule
            };
            return true;
        }

        if (!RayCapsule(origin, dir, a, b, radius, maxDist, out float t, out var normal))
            return false;
        hit = new CollisionWorld.RaycastHit
        {
            Point = origin + dir * t,
            Normal = normal,
            Distance = t,
            Collider = capsule
        };
        return true;
    }

    static bool RayCapsule(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 a, SN.Vector3 b, float radius, float maxDist,
        out float tHit, out SN.Vector3 normal)
    {
        tHit = maxDist;
        normal = SN.Vector3.UnitY;
        bool hit = false;
        var ab = b - a;
        float abLen2 = SN.Vector3.Dot(ab, ab);
        if (abLen2 > 1e-8f)
        {
            var oc = origin - a;
            float dirDotAb = SN.Vector3.Dot(dir, ab);
            float ocDotAb = SN.Vector3.Dot(oc, ab);
            var dirPerp = dir * abLen2 - ab * dirDotAb;
            var ocPerp = oc * abLen2 - ab * ocDotAb;
            float A = SN.Vector3.Dot(dirPerp, dirPerp);
            float B = 2f * SN.Vector3.Dot(dirPerp, ocPerp);
            float C = SN.Vector3.Dot(ocPerp, ocPerp) - radius * radius * abLen2 * abLen2;
            if (A > 1e-8f)
            {
                float disc = B * B - 4f * A * C;
                if (disc >= 0f)
                {
                    float s = MathF.Sqrt(disc);
                    float inv = 0.5f / A;
                    if (TryCylinderHit((-B - s) * inv, origin, dir, a, ab, abLen2, ref tHit, ref normal))
                        hit = true;
                    if (TryCylinderHit((-B + s) * inv, origin, dir, a, ab, abLen2, ref tHit, ref normal))
                        hit = true;
                }
            }
        }

        if (RaySphere(origin, dir, a, radius, tHit, out float ts, out var ns))
        {
            tHit = ts;
            normal = ns;
            hit = true;
        }
        if (RaySphere(origin, dir, b, radius, tHit, out ts, out ns))
        {
            tHit = ts;
            normal = ns;
            hit = true;
        }
        return hit;
    }

    static bool TryCylinderHit(float t, SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 a, SN.Vector3 ab, float abLen2,
        ref float tHit, ref SN.Vector3 normal)
    {
        if (t < 0f || t > tHit || abLen2 <= 1e-8f) return false;
        var p = origin + dir * t;
        float u = SN.Vector3.Dot(p - a, ab) / abLen2;
        if (u < 0f || u > 1f) return false;
        var nrm = p - (a + ab * u);
        normal = nrm.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(nrm) : SN.Vector3.UnitY;
        tHit = t;
        return true;
    }

    static bool RaySphere(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 center, float radius, float maxDist,
        out float t, out SN.Vector3 normal)
    {
        t = 0f;
        normal = SN.Vector3.UnitY;
        var oc = origin - center;
        float b = SN.Vector3.Dot(oc, dir);
        float c = SN.Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;
        if (disc < 0f) return false;
        float s = MathF.Sqrt(disc);
        float t0 = -b - s;
        float use = t0 >= 0f ? t0 : -b + s;
        if (use < 0f || use > maxDist) return false;
        t = use;
        var n = origin + dir * use - center;
        normal = n.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(n) : SN.Vector3.UnitY;
        return true;
    }

    static float DistancePointToSegment(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, out SN.Vector3 closest)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float u = len2 > 1e-8f ? Math.Clamp(SN.Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        closest = a + ab * u;
        return SN.Vector3.Distance(p, closest);
    }

    static void ClosestPointsOnSegments(
        SN.Vector3 p1, SN.Vector3 q1, SN.Vector3 p2, SN.Vector3 q2,
        out SN.Vector3 c1, out SN.Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = SN.Vector3.Dot(d1, d1);
        float e = SN.Vector3.Dot(d2, d2);
        float f = SN.Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f)
        {
            c1 = p1;
            c2 = p2;
            return;
        }
        if (a <= 1e-8f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = SN.Vector3.Dot(d1, r);
            if (e <= 1e-8f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = SN.Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-8f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }
}
