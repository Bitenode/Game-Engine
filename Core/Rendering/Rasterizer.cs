#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using SN = System.Numerics;

namespace Game_Engine.Core;

public static class Rasterizer
{
    // ======== PUBLIC ENTRY POINTS ========

    public static void RasterizeDepth(
        Mesh mesh,
        in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
        float[] zbuf, int W, int H, bool doubleSided = false)
    {
        if (mesh.Vertices == null || mesh.TriIndices == null) return;
        var V = mesh.Vertices;
        var I = mesh.TriIndices;
        var WV = world * view;
        var WVP = WV * proj;
        float winding = world.GetDeterminant() >= 0 ? 1f : -1f;

        static int OutMask(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);

        for (int i = 0; i < I.Length; i += 3)
        {
            int ia = I[i], ib = I[i + 1], ic = I[i + 2];
            var a = V[ia]; var b = V[ib]; var c = V[ic];

            var Ac = SN.Vector4.Transform(new SN.Vector4(a, 1), WVP);
            var Bc = SN.Vector4.Transform(new SN.Vector4(b, 1), WVP);
            var Cc = SN.Vector4.Transform(new SN.Vector4(c, 1), WVP);
            if (Ac.W <= 0 || Bc.W <= 0 || Cc.W <= 0) continue;

            var An = Ac / Ac.W; var Bn = Bc / Bc.W; var Cn = Cc / Cc.W;
            if ((OutMask(An) & OutMask(Bn) & OutMask(Cn)) != 0) continue;

            var av = SN.Vector3.Transform(a, WV);
            var bv = SN.Vector3.Transform(b, WV);
            var cv = SN.Vector3.Transform(c, WV);
            var nView = SN.Vector3.Cross(bv - av, cv - av);
            float viewNormalSign = winding * nView.Z;
            if (!doubleSided && viewNormalSign >= 0f) continue;

            var As = new SN.Vector2((An.X * 0.5f + 0.5f) * W, (1 - (An.Y * 0.5f + 0.5f)) * H);
            var Bs = new SN.Vector2((Bn.X * 0.5f + 0.5f) * W, (1 - (Bn.Y * 0.5f + 0.5f)) * H);
            var Cs = new SN.Vector2((Cn.X * 0.5f + 0.5f) * W, (1 - (Cn.Y * 0.5f + 0.5f)) * H);

            float aInvW = 1f / Ac.W, bInvW = 1f / Bc.W, cInvW = 1f / Cc.W;
            float aZw = An.Z * aInvW, bZw = Bn.Z * bInvW, cZw = Cn.Z * cInvW;

            int minX = (int)MathF.Floor(MathF.Min(As.X, MathF.Min(Bs.X, Cs.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(As.X, MathF.Max(Bs.X, Cs.X)));
            int minY = (int)MathF.Floor(MathF.Min(As.Y, MathF.Min(Bs.Y, Cs.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(As.Y, MathF.Max(Bs.Y, Cs.Y)));
            if (maxX < 0 || maxY < 0 || minX >= W || minY >= H) continue;
            minX = Math.Clamp(minX, 0, W - 1); maxX = Math.Clamp(maxX, 0, W - 1);
            minY = Math.Clamp(minY, 0, H - 1); maxY = Math.Clamp(maxY, 0, H - 1);

            static float Edge(SN.Vector2 p, SN.Vector2 a2, SN.Vector2 b2)
                => (p.X - a2.X) * (b2.Y - a2.Y) - (p.Y - a2.Y) * (b2.X - a2.X);

            float area = Edge(Cs, As, Bs); if (area == 0) continue;
            float invArea = 1f / area;

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new SN.Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(p, Bs, Cs);
                    float w1 = Edge(p, Cs, As);
                    float w2 = Edge(p, As, Bs);
                    const float NearEps = 0.001f;
                    if (area > 0f) { if (w0 < -NearEps || w1 < -NearEps || w2 < -NearEps) continue; }
                    else { if (w0 > NearEps || w1 > NearEps || w2 > NearEps) continue; }
                    w0 *= invArea; w1 *= invArea; w2 *= invArea;

                    float invW = w0 * aInvW + w1 * bInvW + w2 * cInvW;
                    if (invW <= 0) continue;
                    float z = (w0 * aZw + w1 * bZw + w2 * cZw) / invW;
                    int idx = y * W + x;
                    if (z < zbuf[idx]) zbuf[idx] = z;
                }
        }
    }

    public static void RasterizeMeshSolidZ(
    Mesh m,
    in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    uint[] color, float[] zbuf, int W, int H,
    Color tint, Material? mat,
    SN.Vector3 L, float DiffuseK, float Ambient,
    bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
    ShadowMap? shadow, bool receiveShadows, bool doubleSided,
    bool invertFrontFace,
    bool transparentPass)
    {
        var Vtx = m.Vertices;
        var Idx = m.TriIndices;
        var Nor = m.Normals;

        var UVMesh = GetMeshUVs(m);

        // object-space AABB (for planar UV fallback)
        SN.Vector3 bbMin = new(float.MaxValue), bbMax = new(float.MinValue);
        for (int v = 0; v < Vtx.Length; v++)
        {
            var p = Vtx[v];
            bbMin = new SN.Vector3(MathF.Min(bbMin.X, p.X), MathF.Min(bbMin.Y, p.Y), MathF.Min(bbMin.Z, p.Z));
            bbMax = new SN.Vector3(MathF.Max(bbMax.X, p.X), MathF.Max(bbMax.Y, p.Y), MathF.Max(bbMax.Z, p.Z));
        }
        var bbSize = bbMax - bbMin;
        bbSize = new SN.Vector3(bbSize.X == 0 ? 1f : bbSize.X,
                                bbSize.Y == 0 ? 1f : bbSize.Y,
                                bbSize.Z == 0 ? 1f : bbSize.Z);

        bool hasAnyTexture = mat?.Textures?.Any(t => t?.Texture != null) == true;

        // Matrices
        SN.Matrix4x4 mv = world * view;
        SN.Matrix4x4 mvp = mv * proj;

        const float near = 0.1f;

        // Normal matrices
        SN.Matrix4x4.Invert(mv, out var invMv);
        SN.Matrix4x4 normalMatrixV = SN.Matrix4x4.Transpose(invMv);    // for view-space culling (existing)
        SN.Matrix4x4.Invert(world, out var invWorld);
        SN.Matrix4x4 normalMatrixW = SN.Matrix4x4.Transpose(invWorld);     // for world-space lighting (new)

        // Light in WORLD space (camera-invariant)
        SN.Vector3 LdirW = lightIsPoint ? SN.Vector3.Zero : SN.Vector3.Normalize(-L);
        SN.Vector3 lightPosWorld = lightIsPoint ? lightPosW : SN.Vector3.Zero;

        const float INSIDE_EPS = 1e-3f;

        float matOpacity = 1f;
        if (mat != null)
        {
            var p = mat.GetType().GetProperty("Opacity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var v = p.GetValue(mat);
                if (v is float f) matOpacity = Math.Clamp(f, 0f, 1f);
                else if (v is double d) matOpacity = (float)Math.Clamp(d, 0.0, 1.0);
            }
        }
        float tintA = tint.A / 255f;

        // ===== main tri loop =====
        for (int i = 0; i < Idx.Length; i += 3)
        {
            int ia = Idx[i], ib = Idx[i + 1], ic = Idx[i + 2];

            var Pa = Vtx[ia]; var Pb = Vtx[ib]; var Pc = Vtx[ic];

            // choose UVs
            SN.Vector2 Ua, Ub, Uc;
            if (UVMesh != null && UVMesh.Length == Vtx.Length)
            {
                Ua = UVMesh[ia]; Ub = UVMesh[ib]; Uc = UVMesh[ic];
            }
            else if (hasAnyTexture)
            {
                var nObj = SN.Vector3.Normalize(SN.Vector3.Cross(Pb - Pa, Pc - Pa));
                var a = new SN.Vector3(MathF.Abs(nObj.X), MathF.Abs(nObj.Y), MathF.Abs(nObj.Z));
                if (a.X >= a.Y && a.X >= a.Z)
                {
                    Ua = new((Pa.Z - bbMin.Z) / bbSize.Z, (Pa.Y - bbMin.Y) / bbSize.Y);
                    Ub = new((Pb.Z - bbMin.Z) / bbSize.Z, (Pb.Y - bbMin.Y) / bbSize.Y);
                    Uc = new((Pc.Z - bbMin.Z) / bbSize.Z, (Pc.Y - bbMin.Y) / bbSize.Y);
                }
                else if (a.Y >= a.X && a.Y >= a.Z)
                {
                    Ua = new((Pa.X - bbMin.X) / bbSize.X, (Pa.Z - bbMin.Z) / bbSize.Z);
                    Ub = new((Pb.X - bbMin.X) / bbSize.X, (Pb.Z - bbMin.Z) / bbSize.Z);
                    Uc = new((Pc.X - bbMin.X) / bbSize.X, (Pc.Z - bbMin.Z) / bbSize.Z);
                }
                else
                {
                    Ua = new((Pa.X - bbMin.X) / bbSize.X, (Pa.Y - bbMin.Y) / bbSize.Y);
                    Ub = new((Pb.X - bbMin.X) / bbSize.X, (Pb.Y - bbMin.Y) / bbSize.Y);
                    Uc = new((Pc.X - bbMin.X) / bbSize.X, (Pc.Y - bbMin.Y) / bbSize.Y);
                }
            }
            else { Ua = Ub = Uc = new SN.Vector2(0.5f, 0.5f); }

            // transform
            var A = SN.Vector4.Transform(new SN.Vector4(Pa, 1f), mvp);
            var B = SN.Vector4.Transform(new SN.Vector4(Pb, 1f), mvp);
            var C = SN.Vector4.Transform(new SN.Vector4(Pc, 1f), mvp);

            var Va = SN.Vector3.Transform(Pa, mv);
            var Vb = SN.Vector3.Transform(Pb, mv);
            var Vc = SN.Vector3.Transform(Pc, mv);

            // World positions for lighting
            var Wa = SN.Vector3.Transform(Pa, world);
            var Wb = SN.Vector3.Transform(Pb, world);
            var Wc = SN.Vector3.Transform(Pc, world);

            // Normals in VIEW for culling; in WORLD for lighting
            var NaV = Nor != null ? SN.Vector3.TransformNormal(Nor[ia], normalMatrixV) : SN.Vector3.UnitY;
            var NbV = Nor != null ? SN.Vector3.TransformNormal(Nor[ib], normalMatrixV) : SN.Vector3.UnitY;
            var NcV = Nor != null ? SN.Vector3.TransformNormal(Nor[ic], normalMatrixV) : SN.Vector3.UnitY;

            var NaW = Nor != null ? SN.Vector3.TransformNormal(Nor[ia], normalMatrixW) : SN.Vector3.UnitY;
            var NbW = Nor != null ? SN.Vector3.TransformNormal(Nor[ib], normalMatrixW) : SN.Vector3.UnitY;
            var NcW = Nor != null ? SN.Vector3.TransformNormal(Nor[ic], normalMatrixW) : SN.Vector3.UnitY;

            var cv0 = new ClipVertex { ClipPos = A, ViewPos = Va, WorldPos = Wa, ViewNormal = NaV, UV = Ua };
            var cv1 = new ClipVertex { ClipPos = B, ViewPos = Vb, WorldPos = Wb, ViewNormal = NbV, UV = Ub };
            var cv2 = new ClipVertex { ClipPos = C, ViewPos = Vc, WorldPos = Wc, ViewNormal = NcV, UV = Uc };

            var clipped = ClipTriangle(cv0, cv1, cv2, near);
            if (clipped.Count < 3) continue;

            for (int kt = 0; kt < clipped.Count - 2; kt++)
            {
                cv0 = clipped[0]; cv1 = clipped[kt + 1]; cv2 = clipped[kt + 2];
                A = cv0.ClipPos; Va = cv0.ViewPos; Wa = cv0.WorldPos; NaV = cv0.ViewNormal; Ua = cv0.UV;
                B = cv1.ClipPos; Vb = cv1.ViewPos; Wb = cv1.WorldPos; NbV = cv1.ViewNormal; Ub = cv1.UV;
                C = cv2.ClipPos; Vc = cv2.ViewPos; Wc = cv2.WorldPos; NcV = cv2.ViewNormal; Uc = cv2.UV;

                var nView = SN.Vector3.Cross(Vb - Va, Vc - Va);
                float facing = nView.Z * (world.GetDeterminant() >= 0 ? 1f : -1f);
                if (invertFrontFace) facing = -facing;
                bool backfacing = (facing >= 0f);
                if (!doubleSided && backfacing) continue;

                // screen positions
                float aInvW = 1f / A.W; float Axs = (A.X * aInvW + 1f) * 0.5f * W; float Ays = (1f - (A.Y * aInvW) * 0.5f - 0.5f) * H; float aZw = A.Z * aInvW;
                float bInvW = 1f / B.W; float Bxs = (B.X * bInvW + 1f) * 0.5f * W; float Bys = (1f - (B.Y * bInvW) * 0.5f - 0.5f) * H; float bZw = B.Z * bInvW;
                float cInvW = 1f / C.W; float Cxs = (C.X * cInvW + 1f) * 0.5f * W; float Cys = (1f - (C.Y * cInvW) * 0.5f - 0.5f) * H; float cZw = C.Z * cInvW;

                var As = new SN.Vector2(Axs, Ays);
                var Bs = new SN.Vector2(Bxs, Bys);
                var Cs = new SN.Vector2(Cxs, Cys);

                float area = Edge(As, Bs, Cs);
                if (MathF.Abs(area) < 1e-6f) continue;
                float invArea = 1f / area;

                int minX = (int)MathF.Max(0, MathF.Min(Axs, MathF.Min(Bxs, Cxs)));
                int maxX = (int)MathF.Min(W - 1, MathF.Ceiling(MathF.Max(Axs, MathF.Max(Bxs, Cxs))));
                int minY = (int)MathF.Max(0, MathF.Min(Ays, MathF.Min(Bys, Cys)));
                int maxY = (int)MathF.Min(H - 1, MathF.Ceiling(MathF.Max(Ays, MathF.Max(Bys, Cys))));

                // material slots for this tri
                int triFaceMask = transparentPass ? -1 : FaceMaskFromTriAndAabb(Pa, Pb, Pc, bbMin, bbMax);
                var slots = ResolveSlotsForTriangle(mat, triFaceMask);
                bool hasOpacitySlotTri = slots.Any(s => s.Usage == SlotUsage.Opacity);
                bool hasAnySlots = slots.Count > 0;

                // buckets
                List<ResolvedSlot>? albedoSlots = null, emissiveSlots = null, opacitySlots = null,
                                    aoSlots = null, detailSlots = null, specSlots = null,
                                    roughSlots = null, metalSlots = null;

                if (hasAnySlots)
                {
                    albedoSlots = new(4); emissiveSlots = new(2); opacitySlots = new(2);
                    aoSlots = new(2); detailSlots = new(2); specSlots = new(1);
                    roughSlots = new(1); metalSlots = new(1);

                    for (int si = 0; si < slots.Count; si++)
                    {
                        var rs = slots[si];
                        switch (rs.Usage)
                        {
                            case SlotUsage.Emissive: emissiveSlots.Add(rs); break;
                            case SlotUsage.Opacity: opacitySlots.Add(rs); break;
                            case SlotUsage.Occlusion: aoSlots.Add(rs); break;
                            case SlotUsage.Detail: detailSlots.Add(rs); break;
                            case SlotUsage.Specular: specSlots.Add(rs); break;
                            case SlotUsage.Roughness: roughSlots.Add(rs); break;
                            case SlotUsage.Metallic: metalSlots.Add(rs); break;
                            default: albedoSlots.Add(rs); break;
                        }
                    }
                }

                for (int y = minY; y <= maxY; y++)
                {
                    int rowOff = y * W;
                    for (int x = minX; x <= maxX; x++)
                    {
                        var p = new SN.Vector2(x + 0.5f, y + 0.5f);

                        float w0 = Edge(p, Bs, Cs);
                        float w1 = Edge(p, Cs, As);
                        float w2 = Edge(p, As, Bs);

                        if (area > 0f) { if (w0 < -INSIDE_EPS || w1 < -INSIDE_EPS || w2 < -INSIDE_EPS) continue; }
                        else { if (w0 > INSIDE_EPS || w1 > INSIDE_EPS || w2 > INSIDE_EPS) continue; }

                        w0 *= invArea; w1 *= invArea; w2 *= invArea;

                        float invW = w0 * aInvW + w1 * bInvW + w2 * cInvW;
                        if (invW <= 0) continue;

                        // perspective correct depth (store/compare in Zw form)
                        float z = w0 * aZw + w1 * bZw + w2 * cZw;

                        int idx = rowOff + x;

                        // depth test
                        if (transparentPass)
                        {
                            // transparent must be **strictly** in front of what’s there
                            if (z >= zbuf[idx] - 1e-4f) continue;
                        }
                        else
                        {
                            if (z >= zbuf[idx]) continue;
                            zbuf[idx] = z; // write early for opaque
                        }

                        // perspective-correct interpolation (world & view)
                        var viewPos = (w0 * Va * aInvW + w1 * Vb * bInvW + w2 * Vc * cInvW) / invW;
                        var worldPos = (w0 * Wa * aInvW + w1 * Wb * bInvW + w2 * Wc * cInvW) / invW;

                        // normals: view for backface flip, world for lighting
                        var nV = (w0 * NaV * aInvW + w1 * NbV * bInvW + w2 * NcV * cInvW) / invW;
                        var nW = (w0 * NaW * aInvW + w1 * NbW * bInvW + w2 * NcW * cInvW) / invW;

                        // Flip ONLY the view-space normal (for consistent front-face tests).
                        if (doubleSided && backfacing) nV = -nV;

                        // normalize world normal (cheap guard)
                        float nWlen2 = nW.X * nW.X + nW.Y * nW.Y + nW.Z * nW.Z;
                        if (nWlen2 < 0.85f || nWlen2 > 1.21f)
                        {
                            float invLen = 1.0f / MathF.Sqrt(nWlen2 + 1e-12f);
                            nW.X *= invLen; nW.Y *= invLen; nW.Z *= invLen;
                        }

                        // ---------------- Lighting (WORLD space, camera-invariant) ----------------
                        float ndl, atten = 1f;

                        if (lightIsPoint)
                        {
                            var toL = lightPosWorld - worldPos;     // world space
                            float dist = toL.Length();
                            var Ldir = toL / (dist + 1e-6f);

                            float dotNL = SN.Vector3.Dot(nW, Ldir);
                            // Two-sided for point lights (surfaces can be lit on either side).
                            if (doubleSided) dotNL = MathF.Abs(dotNL);
                            ndl = MathF.Max(0f, dotNL);

                            if (lightRange > 0f)
                            {
                                float tR = dist / lightRange;
                                atten = 1f / (1f + tR * tR);
                            }
                        }
                        else
                        {
                            // Directional: allow two-sided lighting for interiors if doubleSided is on.
                            // This lights ceilings even when their normals face away from the sun.
                            float dotNL = SN.Vector3.Dot(nW, LdirW);
                            if (doubleSided)
                                dotNL = MathF.Abs(dotNL);        // non-physical, but great for interior preview
                            else
                                dotNL = MathF.Max(0f, dotNL);

                            ndl = dotNL;
                        }


                        float dir01 = Math.Clamp(ndl * atten, 0f, 1f);

                        // UVs
                        float u = (w0 * Ua.X * aInvW + w1 * Ub.X * bInvW + w2 * Uc.X * cInvW) / invW;
                        float v = (w0 * Ua.Y * aInvW + w1 * Ub.Y * bInvW + w2 * Uc.Y * cInvW) / invW;

                        // -------- material sampling --------
                        Color albedo = Color.FromRgb(255, 255, 255);
                        Color detailMul = Color.FromRgb(255, 255, 255);
                        Color emissive = Color.FromRgb(0, 0, 0);
                        float aoMul = 1f, specMap = 0f, roughFromMap = -1f, metalFromMap = -1f;
                        float albedoAlpha = 0f, opacityMapMul = 1f;
                        bool hadAlbedoRGB = false, sawAlbedoSlot = false;

                        if (hasAnySlots)
                        {
                            if (transparentPass && hasOpacitySlotTri && opacitySlots!.Count > 0)
                            {
                                for (int iop = 0; iop < opacitySlots.Count; iop++)
                                {
                                    var rs = opacitySlots[iop];
                                    rs.ApplyUV(u, v, out var uu, out var vv);
                                    var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                    float op = (s.A < 254) ? (s.A / 255f) : Math.Clamp(ColorUtil.Luma(s), 0f, 1f);
                                    opacityMapMul *= op;
                                    if (!hadAlbedoRGB && !sawAlbedoSlot)
                                    {
                                        albedo = ColorUtil.MulColor(albedo, Color.FromRgb(s.R, s.G, s.B));
                                        hadAlbedoRGB = true;
                                    }
                                }
                            }

                            if (emissiveSlots!.Count > 0)
                                for (int ie = 0; ie < emissiveSlots.Count; ie++)
                                {
                                    var rs = emissiveSlots[ie];
                                    rs.ApplyUV(u, v, out var uu, out var vv);
                                    var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                    emissive = ColorUtil.AddColor(emissive, s);
                                }

                            if (albedoSlots!.Count > 0)
                                for (int iAlb = 0; iAlb < albedoSlots.Count; iAlb++)
                                {
                                    var rs = albedoSlots[iAlb];
                                    rs.ApplyUV(u, v, out var uu, out var vv);
                                    var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                    albedo = ColorUtil.AlphaOver(albedo, s);
                                    hadAlbedoRGB = true; sawAlbedoSlot = true;
                                    float aA = s.A / 255f;
                                    albedoAlpha = albedoAlpha + (1f - albedoAlpha) * aA;
                                }

                            if (Ambient > 0f)
                            {
                                if (aoSlots!.Count > 0)
                                    for (int iao = 0; iao < aoSlots.Count; iao++)
                                    {
                                        var rs = aoSlots[iao];
                                        rs.ApplyUV(u, v, out var uu, out var vv);
                                        var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                        aoMul *= Math.Clamp(ColorUtil.Luma(s), 0f, 1f);
                                    }

                                if (detailSlots!.Count > 0)
                                    for (int idt = 0; idt < detailSlots.Count; idt++)
                                    {
                                        var rs = detailSlots[idt];
                                        rs.ApplyUV(u, v, out var uu, out var vv);
                                        var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                        detailMul = ColorUtil.MulColor(detailMul, s);
                                    }
                            }

                            if (DiffuseK > 0f && dir01 > 0f)
                            {
                                if (specSlots!.Count > 0)
                                    for (int isp = 0; isp < specSlots.Count; isp++)
                                    {
                                        var rs = specSlots[isp];
                                        rs.ApplyUV(u, v, out var uu, out var vv);
                                        var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                        specMap = Math.Clamp(ColorUtil.Luma(s), 0f, 1f);
                                    }

                                if (roughSlots!.Count > 0)
                                    for (int ir = 0; ir < roughSlots.Count; ir++)
                                    {
                                        var rs = roughSlots[ir];
                                        rs.ApplyUV(u, v, out var uu, out var vv);
                                        var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                        roughFromMap = Math.Clamp(ColorUtil.Luma(s), 0f, 1f);
                                    }

                                if (metalSlots!.Count > 0)
                                    for (int im = 0; im < metalSlots.Count; im++)
                                    {
                                        var rs = metalSlots[im];
                                        rs.ApplyUV(u, v, out var uu, out var vv);
                                        var s = TextureSampling.SamplePMClamped(rs.Tex!, uu, rs.NoFlipV ? (1f - vv) : vv);
                                        metalFromMap = Math.Clamp(ColorUtil.Luma(s), 0f, 1f);
                                    }
                            }
                        }

                        float metallic = metalFromMap >= 0 ? metalFromMap : Math.Clamp(mat?.Metallic ?? 0f, 0f, 1f);
                        float smooth = roughFromMap >= 0 ? (1f - roughFromMap) : Math.Clamp(mat?.Smoothness ?? 0.5f, 0f, 1f);
                        float specStr = Math.Clamp(specMap, 0f, 1f);
                        float shininess = 8f + smooth * smooth * 248f;

                        // lighting combine
                        float amb = Ambient * (1f + dir01);
                        float dif = DiffuseK * dir01;
                        float shade = amb * aoMul + dif;

                        Color safeTint = (tint.R | tint.G | tint.B) == 0 ? Color.FromRgb(255, 255, 255) : tint;
                        Color lit = ColorUtil.ShadeColor(safeTint, shade);
                        Color baseCol = ColorUtil.MulColor(ColorUtil.MulColor(albedo, detailMul), lit);

                        // specular (Blinn-Phong) — use world-space half vector
                        Color specAdd = Color.FromRgb(0, 0, 0);
                        if (DiffuseK > 0f && specStr > 0.001f && dir01 > 0f)
                        {
                            var VdirW = SN.Vector3.Normalize(CameraDirectionFromWorldPos(worldPos)); // see helper below
                            var LdirWused = lightIsPoint ? SN.Vector3.Normalize(lightPosWorld - worldPos) : LdirW;
                            SN.Vector3 halfVec = SN.Vector3.Normalize(LdirWused + VdirW);
                            float ndh = MathF.Max(0f, SN.Vector3.Dot(nW, halfVec));
                            float spec = MathF.Pow(ndh, shininess) * specStr * (0.25f + 0.75f * metallic) * dir01;
                            byte sr = (byte)Math.Clamp(spec * 255f, 0f, 255f);
                            specAdd = Color.FromRgb(sr, sr, sr);
                        }

                        Color pix = ColorUtil.AddColor(ColorUtil.AddColor(baseCol, specAdd), emissive);

                        if (transparentPass)
                        {
                            float baseAlpha = sawAlbedoSlot ? albedoAlpha : 1f;
                            float aEff = Math.Clamp(baseAlpha * opacityMapMul * matOpacity * tintA, 0f, 1f);
                            if (aEff <= 0.0001f) continue;

                            

                            color[idx] = ColorUtil.BlendOver(color[idx], pix, aEff);
                        }
                        else
                        {
                            color[idx] = ColorUtil.PackBGRA(pix);
                        }
                    }
                }
            }
        }

        // local helper: view direction in WORLD space (from fragment toward camera)
        static SN.Vector3 CameraDirectionFromWorldPos(SN.Vector3 worldPos)
        {
            // Camera is at world origin in view space; to get a stable specular,
            // use direction from point to camera position. If you keep camera position available,
            
            var v = -worldPos;
            float len = v.Length();
            return len > 1e-6f ? v / len : new SN.Vector3(0, 0, -1);
        }
    }







    // ======== PRIVATES ========

    // Fast usage enum
    private enum SlotUsage : byte
    {
        Albedo, Emissive, Occlusion, Detail, Specular, Roughness, Metallic, Opacity, Normal, Unknown
    }

    // Pre-resolved slot with NO reflection in the inner loop
    private readonly struct ResolvedSlot
    {
        public readonly Texture2D? Tex;
        public readonly SlotUsage Usage;
        public readonly int FaceMask;     // -1 means "any"
        public readonly bool NoFlipV;
        // UV xform precomputed
        public readonly float Su, Sv, Ou, Ov, Cs, Sn;

        public ResolvedSlot(Texture2D? tex, SlotUsage usage, int mask, bool noFlipV,
                            float su, float sv, float ou, float ov, float cs, float sn)
        {
            Tex = tex; Usage = usage; FaceMask = mask; NoFlipV = noFlipV;
            Su = su; Sv = sv; Ou = ou; Ov = ov; Cs = cs; Sn = sn;
        }

        // Apply UV transform without any reflection/Math inside the pixel loop
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public readonly void ApplyUV(float uIn, float vIn, out float u, out float v)
        {
            float uu = (uIn - 0.5f) * Su;
            float vv = (vIn - 0.5f) * Sv;
            if (Sn != 0f || Cs != 1f)
            {
                float x = uu * Cs - vv * Sn;
                float y = uu * Sn + vv * Cs;
                uu = x; vv = y;
            }
            u = uu + 0.5f + Ou;
            v = vv + 0.5f + Ov;
        }
    }

    // Resolve material slots ONCE per triangle (or per material) — no reflection in inner loop.
    private static readonly Dictionary<Type, Func<object, Texture2D?>> _getterTexture = new();
    private static Texture2D? GetTextureFast(object slot)
    {
        var t = slot.GetType();
        if (!_getterTexture.TryGetValue(t, out var getter))
        {
            var pTex = t.GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pTex == null) { _getterTexture[t] = _ => null; return null; }
            getter = (object s) =>
            {
                var raw = pTex.GetValue(s);
                return raw as Texture2D ?? TextureBridge.EnsureEngineTexture2D(raw);
            };
            _getterTexture[t] = getter;
        }
        return getter(slot);
    }

    private static readonly Dictionary<Type, Func<object, string>> _getterUsage = new();
    private static string GetUsageFast(object slot)
    {
        var t = slot.GetType();
        if (!_getterUsage.TryGetValue(t, out var getter))
        {
            var p = t.GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            getter = p == null ? (_ => "Albedo") : (object s) => p.GetValue(s)?.ToString() ?? "Albedo";
            _getterUsage[t] = getter;
        }
        return getter(slot);
    }

    private static readonly Dictionary<Type, Func<object, int>> _getterFaceMask = new();
    private static int GetFaceMaskFast(object slot)
    {
        var t = slot.GetType();
        if (!_getterFaceMask.TryGetValue(t, out var getter))
        {
            var p = t.GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) { _getterFaceMask[t] = _ => -1; return -1; }
            getter = (object s) =>
            {
                var v = p.GetValue(s);
                if (v is int i) return i;
                return v != null && v.GetType().IsEnum ? Convert.ToInt32(v) : -1;
            };
            _getterFaceMask[t] = getter;
        }
        return getter(slot);
    }

    private static readonly Dictionary<Type, Func<object, bool>> _getterNoFlipV = new();
    private static bool GetNoFlipVFast(object slot)
    {
        var t = slot.GetType();
        if (!_getterNoFlipV.TryGetValue(t, out var getter))
        {
            var p = t.GetProperty("NoFlipV", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            getter = p == null ? (_ => false) : (object s) => p.GetValue(s) is bool b && b;
            _getterNoFlipV[t] = getter;
        }
        return getter(slot);
    }

    private static readonly Dictionary<Type, (Func<object, float> su, Func<object, float> sv, Func<object, float> ou, Func<object, float> ov, Func<object, float> rot)> _getterUV = new();
    private static (float su, float sv, float ou, float ov, float cs, float sn) GetUVXformFast(object slot)
    {
        var t = slot.GetType();
        if (!_getterUV.TryGetValue(t, out var g))
        {
            Func<object, float> gf(string name, float def)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null) return _ => def;
                return (object s) =>
                {
                    var v = p.GetValue(s);
                    return v is float f ? f : v is double d ? (float)d : def;
                };
            }
            g = (gf("ScaleU", 1f), gf("ScaleV", 1f), gf("OffsetU", 0f), gf("OffsetV", 0f), gf("RotateUV", 0f));
            _getterUV[t] = g;
        }

        float su = g.su(slot), sv = g.sv(slot), ou = g.ou(slot), ov = g.ov(slot);
        float rotDeg = g.rot(slot);
        float r = rotDeg * (MathF.PI / 180f);
        float cs = MathF.Abs(r) < 1e-6f ? 1f : MathF.Cos(r);
        float sn = MathF.Abs(r) < 1e-6f ? 0f : MathF.Sin(r);
        return (su, sv, ou, ov, cs, sn);
    }

    private static SlotUsage ParseUsage(string usage)
    {
        usage = usage.ToLowerInvariant();
        if (usage.Contains("emis")) return SlotUsage.Emissive;
        if (usage.Contains("occl") || usage == "ao") return SlotUsage.Occlusion;
        if (usage.Contains("detail")) return SlotUsage.Detail;
        if (usage.Contains("spec")) return SlotUsage.Specular;
        if (usage.Contains("rough")) return SlotUsage.Roughness;
        if (usage.Contains("metal")) return SlotUsage.Metallic;
        if (usage.Contains("opacity") || usage.Contains("alpha") || usage.Contains("transp")) return SlotUsage.Opacity;
        if (usage.Contains("normal")) return SlotUsage.Normal;
        return SlotUsage.Albedo;
    }

    // Build a filtered list of slots for THIS triangle (respect FaceMask) – no string/reflection in the inner loop.
    private static List<ResolvedSlot> ResolveSlotsForTriangle(Material? mat, int triFaceMask)
    {
        var list = new List<ResolvedSlot>(8);
        if (mat?.Textures == null || mat.Textures.Count == 0) return list;

        foreach (var slot in mat.Textures)
        {
            if (slot == null) continue;
            int mask = GetFaceMaskFast(slot);
            if (mask != -1 && triFaceMask != -1 && (mask & triFaceMask) == 0) continue;

            var tex = GetTextureFast(slot);
            if (tex == null) continue;

            var (su, sv, ou, ov, cs, sn) = GetUVXformFast(slot);
            bool noFlipV = GetNoFlipVFast(slot);
            var usage = ParseUsage(GetUsageFast(slot));

            list.Add(new ResolvedSlot(tex, usage, mask, noFlipV, su, sv, ou, ov, cs, sn));
        }
        return list;
    }


    private struct ClipVertex
    {
        public SN.Vector4 ClipPos;
        public SN.Vector3 ViewPos;
        public SN.Vector3 WorldPos;
        public SN.Vector3 ViewNormal;
        public SN.Vector2 UV;
    }

    private static ClipVertex Interp(in ClipVertex a, in ClipVertex b, float t) => new()
    {
        ClipPos = a.ClipPos + t * (b.ClipPos - a.ClipPos),
        ViewPos = a.ViewPos + t * (b.ViewPos - a.ViewPos),
        WorldPos = a.WorldPos + t * (b.WorldPos - a.WorldPos),
        ViewNormal = a.ViewNormal + t * (b.ViewNormal - a.ViewNormal),
        UV = a.UV + t * (b.UV - a.UV)
    };

    private static List<ClipVertex> ClipAgainstPlane(List<ClipVertex> poly, SN.Vector4 plane, float planeD)
    {
        var result = new List<ClipVertex>(poly.Count + 1);
        int count = poly.Count;

        for (int i = 0; i < count; i++)
        {
            ClipVertex curr = poly[i];
            ClipVertex prev = poly[(i + count - 1) % count];

            float currDist = SN.Vector4.Dot(curr.ClipPos, plane) + planeD;
            float prevDist = SN.Vector4.Dot(prev.ClipPos, plane) + planeD;

            bool currIn = currDist >= 0f;
            bool prevIn = prevDist >= 0f;

            if (prevIn != currIn)
            {
                float t = prevDist / (prevDist - currDist);
                result.Add(Interp(prev, curr, t));
            }
            if (currIn) result.Add(curr);
        }
        return result;
    }

    private static List<ClipVertex> ClipTriangle(ClipVertex v0, ClipVertex v1, ClipVertex v2, float near)
    {
        var input = new List<ClipVertex> { v0, v1, v2 };
        return ClipAgainstPlane(input, new SN.Vector4(0f, 0f, 0f, 1f), -near);
    }

    private static float Edge(SN.Vector2 a, SN.Vector2 b, SN.Vector2 c)
        => (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);

    // Cache for reflection lookups so we don't pay repeatedly
    private static readonly Dictionary<Type, SN.Vector2[]?> _uvCache = new();

    private static SN.Vector2[]? GetMeshUVs(Mesh m)
    {
        var t = m.GetType();
        if (_uvCache.TryGetValue(t, out var cached)) return cached;

        string[] names = { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(SN.Vector2[]))
            { var v = (SN.Vector2[]?)p.GetValue(m); _uvCache[t] = v; return v; }

            var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(SN.Vector2[]))
            { var v = (SN.Vector2[]?)f.GetValue(m); _uvCache[t] = v; return v; }
        }
        _uvCache[t] = null;
        return null;
    }

    // Face helpers
    private static int FaceMaskFromTriAndAabb(in SN.Vector3 pa, in SN.Vector3 pb, in SN.Vector3 pc,
                                              in SN.Vector3 bbMin, in SN.Vector3 bbMax)
    {
        var n = SN.Vector3.Cross(pb - pa, pc - pa);
        var an = new SN.Vector3(MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));

        float cx = (pa.X + pb.X + pc.X) / 3f;
        float cy = (pa.Y + pb.Y + pc.Y) / 3f;
        float cz = (pa.Z + pb.Z + pc.Z) / 3f;
        float mx = (bbMin.X + bbMax.X) * 0.5f;
        float my = (bbMin.Y + bbMax.Y) * 0.5f;
        float mz = (bbMin.Z + bbMax.Z) * 0.5f;

        if (an.X >= an.Y && an.X >= an.Z) return (cx >= mx) ? 1 : 2;    // +X / -X
        if (an.Y >= an.X && an.Y >= an.Z) return (cy >= my) ? 4 : 8;    // +Y / -Y
        return (cz >= mz) ? 16 : 32;                                     // +Z / -Z
    }

  
}
