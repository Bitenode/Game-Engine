using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core;

public enum MeshKind { Generic, Sphere, Cylinder, Cone }

public sealed class Mesh
{
    // --- Public data ---------------------------------------------------------
    public SN.Vector3[] Vertices { get; }
    public SN.Vector3[]? Normals { get; set; }
    public System.Numerics.Vector2[]? UVs { get; set; }
    public int[] LineIndices { get; }   // pairs (a,b)
    public int[] TriIndices { get; }   // triples (a,b,c)

    // Metadata used by SceneView for procedural LOD
    public MeshKind Kind { get; init; } = MeshKind.Generic;
    /// Tessellation parameter A (lon or radial sides)
    public int TessA { get; init; }
    /// Tessellation parameter B (lat for spheres)
    public int TessB { get; init; }

    public SN.Vector2[]? UV2 { get; set; } // wind weight (x) & phase (y)

    // --- Skeletal / skinning data -------------------------------------------
    /// <summary>4 bone weights per vertex (x,y,z,w). Null if mesh is not skinned.</summary>
    public SN.Vector4[]? BoneWeights { get; set; }
    /// <summary>4 bone indices per vertex, packed as flat array [v0i0,v0i1,v0i2,v0i3, v1i0,...]. Length = Vertices.Length * 4.</summary>
    public int[]? BoneIndices { get; set; }
    /// <summary>The skeleton this mesh is bound to (null for non-skinned meshes).</summary>
    public Skeleton? Skeleton { get; set; }
    /// <summary>True if this mesh has bone skinning data.</summary>
    public bool HasBones => BoneWeights != null && BoneIndices != null && Skeleton != null;

    public Mesh(SN.Vector3[] v, int[] lines, int[] tris)
    {
        Vertices = v;
        LineIndices = lines;
        TriIndices = tris;
    }

    // --- Helpers -------------------------------------------------------------

    /// Recompute smooth, area-weighted vertex normals for the current mesh.
    public void RecalculateNormalsSmooth()
        => Normals = BuildVertexNormals(Vertices, TriIndices);

    static int[] BuildEdgesFromTriangles(int[] tris)
    {
        var set = new HashSet<(int, int)>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            Add(tris[i], tris[i + 1]);
            Add(tris[i + 1], tris[i + 2]);
            Add(tris[i + 2], tris[i]);
        }

        var list = new List<int>(set.Count * 2);
        foreach (var (a, b) in set) { list.Add(a); list.Add(b); }
        return list.ToArray();

        // NOTE: not static — captures 'set'
        void Add(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            set.Add((a, b));
        }
    }


    public static (int lon, int lat) SuggestSphereTesselation(float projectedRadiusPx)
    {
        // ≈2 px per edge around silhouette; clamp to sane bounds
        int lon = Math.Clamp((int)(MathF.Tau * projectedRadiusPx / 2f), 24, 192);
        int lat = Math.Clamp(lon * 2 / 3, 16, 128);
        return (lon, lat);
    }

    public static int SuggestRadialTessellation(float projectedRadiusPx)
    {
        // same idea for cylinders/cones (edges around circle)
        return Math.Clamp((int)(MathF.Tau * projectedRadiusPx / 2f), 16, 256);
    }

    static SN.Vector3[] BuildVertexNormals(SN.Vector3[] verts, int[] tris)
    {
        static SN.Vector3 SafeNormalize(in SN.Vector3 v)
            => v.LengthSquared() < 1e-12f ? SN.Vector3.UnitY : SN.Vector3.Normalize(v);

        var n = new SN.Vector3[verts.Length];

        for (int i = 0; i < tris.Length; i += 3)
        {
            int ia = tris[i], ib = tris[i + 1], ic = tris[i + 2];
            var e1 = verts[ib] - verts[ia];
            var e2 = verts[ic] - verts[ia];
            var fn = SN.Vector3.Cross(e1, e2);   // area-weighted (not normalized)
            n[ia] += fn; n[ib] += fn; n[ic] += fn;
        }

        for (int i = 0; i < n.Length; i++) n[i] = SafeNormalize(n[i]);
        return n;
    }

    // --- Primitives ----------------------------------------------------------

    public static Mesh CreateCube(float size = 1f)
    {
        float h = size * 0.5f;
        var v = new[]
        {
            new SN.Vector3(-h,-h,-h), new SN.Vector3( h,-h,-h),
            new SN.Vector3( h, h,-h), new SN.Vector3(-h, h,-h),
            new SN.Vector3(-h,-h, h), new SN.Vector3( h,-h, h),
            new SN.Vector3( h, h,  h), new SN.Vector3(-h, h,  h),
        };

        // 12 triangles, CCW
        int[] t =
        {
            0,1,2, 0,2,3,     // back
            4,6,5, 4,7,6,     // front
            0,3,7, 0,7,4,     // left
            1,5,6, 1,6,2,     // right
            3,2,6, 3,6,7,     // top
            0,4,5, 0,5,1      // bottom
        };

        return new Mesh(v, BuildEdgesFromTriangles(t), t) { Kind = MeshKind.Generic };
    }

    /// Quad on XZ plane (Y=0), centered, sizeX × sizeZ.
    public static Mesh CreateQuad(float sizeX = 1f, float sizeZ = 1f)
    {
        float hx = sizeX * 0.5f, hz = sizeZ * 0.5f;
        var v = new[]
        {
            new SN.Vector3(-hx, 0, -hz),
            new SN.Vector3( hx, 0, -hz),
            new SN.Vector3( hx, 0,  hz),
            new SN.Vector3(-hx, 0,  hz),
        };
        int[] t = { 0, 1, 2, 0, 2, 3 }; // CCW, facing +Y
        return new Mesh(v, BuildEdgesFromTriangles(t), t) { Kind = MeshKind.Generic };
    }

    /// Plane on XZ with segments.
    public static Mesh CreatePlane(float sizeX = 2f, float sizeZ = 2f, int segX = 10, int segZ = 10)
    {
        segX = Math.Max(1, segX);
        segZ = Math.Max(1, segZ);
        int nx = segX + 1, nz = segZ + 1;

        var verts = new SN.Vector3[nx * nz];
        float hx = sizeX * 0.5f, hz = sizeZ * 0.5f;
        for (int z = 0; z < nz; z++)
        {
            float tz = z / (float)segZ;
            for (int x = 0; x < nx; x++)
            {
                float tx = x / (float)segX;
                verts[z * nx + x] = new SN.Vector3(-hx + tx * sizeX, 0, -hz + tz * sizeZ);
            }
        }

        var tris = new List<int>(segX * segZ * 6);
        for (int z = 0; z < segZ; z++)
            for (int x = 0; x < segX; x++)
            {
                int a = z * nx + x;
                int b = a + 1;
                int c = a + nx;
                int d = c + 1;
                // two CCW tris
                tris.Add(a); tris.Add(b); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(c);
            }
        var t = tris.ToArray();
        return new Mesh(verts, BuildEdgesFromTriangles(t), t) { Kind = MeshKind.Generic };
    }

    /// UV sphere (longitude/latitude) with explicit poles and a wrapped seam.
    /// lon = longitudes around the equator (min 3)
    /// lat = latitudinal bands between the poles (min 2, excludes the two poles)
    /// radius in world units.
    public static Mesh CreateUvSphere(int lon = 24, int lat = 16, float radius = 0.5f)
    {
        lon = Math.Clamp(lon, 3, 512);
        lat = Math.Clamp(lat, 2, 512);

        int vertsPerRing = lon + 1;  // duplicate seam for proper wrap
        int rings = lat - 1;         // between poles (excludes poles)
        int vertCount = 2 + rings * vertsPerRing;

        var verts = new SN.Vector3[vertCount];
        int vi = 0;

        // Top pole
        verts[vi++] = new SN.Vector3(0f, +radius, 0f);

        // Middle rings (exclude poles)
        for (int y = 1; y < lat; y++)
        {
            float v = y / (float)lat;     // 0..1
            float phi = v * MathF.PI;     // 0..PI
            float sy = MathF.Cos(phi);    // Y up
            float r = MathF.Sin(phi);

            for (int x = 0; x <= lon; x++) // <= lon for wrapped seam
            {
                float u = x / (float)lon;  // 0..1
                float th = u * MathF.Tau;  // 0..2PI
                float sx = r * MathF.Cos(th);
                float sz = r * MathF.Sin(th);
                verts[vi++] = new SN.Vector3(sx, sy, sz) * radius;
            }
        }

        // Bottom pole
        int bottomIndex = vi;
        verts[vi++] = new SN.Vector3(0f, -radius, 0f);

        var tris = new List<int>(lon * 6 * lat);

        // Top cap (pole -> first ring), CCW as seen from outside
        int firstRing = 1;
        for (int x = 0; x < lon; x++)
        {
            int a = firstRing + x;
            int b = firstRing + x + 1;
            tris.Add(0); tris.Add(b); tris.Add(a);
        }

        // Middle quads (between rings)
        for (int y = 1; y < lat - 1; y++)
        {
            int r0 = 1 + (y - 1) * vertsPerRing;
            int r1 = r0 + vertsPerRing;

            for (int x = 0; x < lon; x++)
            {
                int a = r0 + x;
                int b = r0 + x + 1;
                int c = r1 + x;
                int d = r1 + x + 1;

                tris.Add(a); tris.Add(b); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(c);
            }
        }

        // Bottom cap (last ring -> pole), CCW as seen from outside
        int lastRing = 1 + (lat - 2) * vertsPerRing;
        for (int x = 0; x < lon; x++)
        {
            int c = lastRing + x;
            int d = lastRing + x + 1;
            tris.Add(c); tris.Add(d); tris.Add(bottomIndex);
        }

        var t = tris.ToArray();

        // Smooth sphere normals: just normalize positions
        var norms = new SN.Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            norms[i] = SN.Vector3.Normalize(verts[i]);

        return new Mesh(verts, BuildEdgesFromTriangles(t), t)
        {
            Kind = MeshKind.Sphere,
            TessA = lon,
            TessB = lat,
            Normals = norms
        };
    }

    /// Cylinder along Y, centered, height h, radius r. Caps optional.
    public static Mesh CreateCylinder(int sides = 24, float radius = 0.5f, float height = 1f, bool caps = true)
    {
        sides = Math.Max(3, sides);
        int ring = sides + 1; // seam dup

        var verts = new List<SN.Vector3>(ring * 2 + (caps ? 2 : 0));
        float hy = height * 0.5f;

        // top ring (y=+hy), CCW when viewed from +Y
        for (int i = 0; i < ring; i++)
        {
            float u = i / (float)sides;
            float th = u * MathF.Tau;
            float x = radius * MathF.Cos(th);
            float z = radius * MathF.Sin(th);
            verts.Add(new SN.Vector3(x, hy, z));
        }
        // bottom ring (y=-hy), same angular order
        for (int i = 0; i < ring; i++)
        {
            float u = i / (float)sides;
            float th = u * MathF.Tau;
            float x = radius * MathF.Cos(th);
            float z = radius * MathF.Sin(th);
            verts.Add(new SN.Vector3(x, -hy, z));
        }

        int topCenter = -1, botCenter = -1;
        if (caps)
        {
            topCenter = verts.Count; verts.Add(new SN.Vector3(0, hy, 0));
            botCenter = verts.Count; verts.Add(new SN.Vector3(0, -hy, 0));
        }

        var tris = new List<int>();

        // SIDES (outward winding): (a,c,b) and (b,c,d)
        // a=top i, b=top i+1, c=bot i, d=bot i+1
        for (int i = 0; i < sides; i++)
        {
            int a = i;
            int b = i + 1;
            int c = ring + i;
            int d = ring + i + 1;

            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }

        if (caps)
        {
            // TOP CAP — CCW when looking from +Y (normal +Y)
            for (int i = 0; i < sides; i++)
            {
                int a = i;
                int b = i + 1;
                tris.Add(topCenter); tris.Add(a); tris.Add(b);
            }
            // BOTTOM CAP — CCW when looking from -Y (normal -Y)
            for (int i = 0; i < sides; i++)
            {
                int a = ring + i;
                int b = ring + i + 1;
                tris.Add(botCenter); tris.Add(b); tris.Add(a);
            }
        }

        var vArr = verts.ToArray();
        var t = tris.ToArray();
        return new Mesh(vArr, BuildEdgesFromTriangles(t), t)
        {
            Kind = MeshKind.Cylinder,
            TessA = sides
        };
    }

    /// Cone along Y, apex at +Y, base at -Y (optional cap).
    public static Mesh CreateCone(int sides = 24, float radius = 0.5f, float height = 1f, bool cap = true)
    {
        sides = Math.Max(3, sides);
        float hy = height * 0.5f;

        var verts = new List<SN.Vector3>(sides + (cap ? 2 : 1));

        // Base ring
        for (int i = 0; i < sides; i++)
        {
            float th = (i / (float)sides) * MathF.Tau;
            float x = radius * MathF.Cos(th);
            float z = radius * MathF.Sin(th);
            verts.Add(new SN.Vector3(x, -hy, z));
        }

        int apex = verts.Count;
        verts.Add(new SN.Vector3(0, hy, 0));

        int baseCenter = -1;
        if (cap)
        {
            baseCenter = verts.Count;
            verts.Add(new SN.Vector3(0, -hy, 0));
        }

        var tris = new List<int>(sides * 3 + (cap ? sides * 3 : 0));

        // Sides (fan from apex) — outward winding
        for (int i = 0; i < sides; i++)
        {
            int a = i;
            int b = (i + 1) % sides;
            tris.Add(a); tris.Add(b); tris.Add(apex);
        }

        // Bottom cap — wind so normals face -Y (visible from outside/below)
        if (cap)
        {
            for (int i = 0; i < sides; i++)
            {
                int a = i;
                int b = (i + 1) % sides;
                tris.Add(baseCenter); tris.Add(b); tris.Add(a); // note b,a order
            }
        }

        var vArr = verts.ToArray();
        var t = tris.ToArray();
        return new Mesh(vArr, BuildEdgesFromTriangles(t), t)
        {
            Kind = MeshKind.Cone,
            TessA = sides
        };
    }

}
