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

        // --- wind data (same as color pass) --------------------
        var UVMesh2 = GetMeshUVs2(mesh); // may be null => no wind
        float windT = WindSystem.Time;
        var windDir = WindSystem.Direction;
        float windAmp = WindSystem.Amplitude;
        // ------------------------------------------------------------

        static int OutMask(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);

        for (int i = 0; i < I.Length; i += 3)
        {
            int ia = I[i], ib = I[i + 1], ic = I[i + 2];
            var a = V[ia]; var b = V[ib]; var c = V[ic];

            //  apply wind before transforms -------------------
            if (UVMesh2 != null && UVMesh2.Length == V.Length)
            {
                SN.Vector2 wa = UVMesh2[ia];
                SN.Vector2 wb = UVMesh2[ib];
                SN.Vector2 wc = UVMesh2[ic];

                a = ApplyWind(a, wa.X, wa.Y, windDir, windT, windAmp);
                b = ApplyWind(b, wb.X, wb.Y, windDir, windT, windAmp);
                c = ApplyWind(c, wc.X, wc.Y, windDir, windT, windAmp);
            }
            // ----------------------------------------------------------

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
        var UVMesh2 = GetMeshUVs2(m); // may be null if mesh has no UV2 (then wind=0)
        float windT = WindSystem.Time;
        var windDir = WindSystem.Direction;
        float windAmp = WindSystem.Amplitude;

        // object-space AABB (for planar UV fallback )
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

        // Resolve material slots ONCE (no reflection in inner loop)
        var slots = ResolveSlots(mat);                   
        bool hasAnyTexture = slots.Count > 0;
        bool anyFaceMasks = false;                      // used to skip mask work when not needed
        for (int s = 0; s < slots.Count; s++)
            if (slots[s].FaceMask != -1) { anyFaceMasks = true; break; }

        SN.Matrix4x4 mv = world * view;
        SN.Matrix4x4 mvp = mv * proj;

        const float near = 0.1f;
        SN.Matrix4x4.Invert(mv, out var invMv);
        SN.Matrix4x4 normalMatrix = SN.Matrix4x4.Transpose(invMv);

        // VIEW-space light params (LdirV points FROM surface TO the light)
        SN.Vector3 LdirV = lightIsPoint ? SN.Vector3.Zero
                                        : SN.Vector3.Normalize(SN.Vector3.TransformNormal(-L, view));
        SN.Vector3 lightPosV = lightIsPoint ? SN.Vector3.Transform(lightPosW, view)
                                            : SN.Vector3.Zero;

        const float INSIDE_EPS = 1e-3f;

        // Material opacity (query once)
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
        Color safeTint = (tint.R | tint.G | tint.B) == 0 ? Color.FromRgb(255, 255, 255) : tint;

        // ===== main tri loop =====
        for (int i = 0; i < Idx.Length; i += 3)
        {
            int ia = Idx[i], ib = Idx[i + 1], ic = Idx[i + 2];
            var Pa = Vtx[ia];
            var Pb = Vtx[ib];
            var Pc = Vtx[ic];

            // Wind weights & phases from UV2 (X=weight, Y=phase), defaults to zero if missing
            
            // var UVMesh2 = UVMesh;
            SN.Vector2 windA = (UVMesh2 != null && UVMesh2.Length == Vtx.Length) ? UVMesh2[ia] : default;
            SN.Vector2 windB = (UVMesh2 != null && UVMesh2.Length == Vtx.Length) ? UVMesh2[ib] : default;
            SN.Vector2 windC = (UVMesh2 != null && UVMesh2.Length == Vtx.Length) ? UVMesh2[ic] : default;

            // Deform in object space BEFORE transform
            Pa = ApplyWind(Pa, windA.X, windA.Y, windDir, windT, windAmp);
            Pb = ApplyWind(Pb, windB.X, windB.Y, windDir, windT, windAmp);
            Pc = ApplyWind(Pc, windC.X, windC.Y, windDir, windT, windAmp);



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
                if (a.X >= a.Y && a.X >= a.Z) // YZ
                {
                    Ua = new((Pa.Z - bbMin.Z) / bbSize.Z, (Pa.Y - bbMin.Y) / bbSize.Y);
                    Ub = new((Pb.Z - bbMin.Z) / bbSize.Z, (Pb.Y - bbMin.Y) / bbSize.Y);
                    Uc = new((Pc.Z - bbMin.Z) / bbSize.Z, (Pc.Y - bbMin.Y) / bbSize.Y);
                }
                else if (a.Y >= a.X && a.Y >= a.Z) // XZ
                {
                    Ua = new((Pa.X - bbMin.X) / bbSize.X, (Pa.Z - bbMin.Z) / bbSize.Z);
                    Ub = new((Pb.X - bbMin.X) / bbSize.X, (Pb.Z - bbMin.Z) / bbSize.Z);
                    Uc = new((Pc.X - bbMin.X) / bbSize.X, (Pc.Z - bbMin.Z) / bbSize.Z);
                }
                else // XY
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

            var Wa = SN.Vector3.Transform(Pa, world);
            var Wb = SN.Vector3.Transform(Pb, world);
            var Wc = SN.Vector3.Transform(Pc, world);

            var Na = Nor != null ? SN.Vector3.TransformNormal(Nor[ia], normalMatrix) : SN.Vector3.UnitY;
            var Nb = Nor != null ? SN.Vector3.TransformNormal(Nor[ib], normalMatrix) : SN.Vector3.UnitY;
            var Nc = Nor != null ? SN.Vector3.TransformNormal(Nor[ic], normalMatrix) : SN.Vector3.UnitY;

            var cv0 = new ClipVertex { ClipPos = A, ViewPos = Va, WorldPos = Wa, ViewNormal = Na, UV = Ua };
            var cv1 = new ClipVertex { ClipPos = B, ViewPos = Vb, WorldPos = Wb, ViewNormal = Nb, UV = Ub };
            var cv2 = new ClipVertex { ClipPos = C, ViewPos = Vc, WorldPos = Wc, ViewNormal = Nc, UV = Uc };

            var clipped = ClipTriangle(cv0, cv1, cv2, near);
            if (clipped.Count < 3) continue;

            for (int kt = 0; kt < clipped.Count - 2; kt++)
            {
                cv0 = clipped[0]; cv1 = clipped[kt + 1]; cv2 = clipped[kt + 2];
                A = cv0.ClipPos; Va = cv0.ViewPos; Wa = cv0.WorldPos; Na = cv0.ViewNormal; Ua = cv0.UV;
                B = cv1.ClipPos; Vb = cv1.ViewPos; Wb = cv1.WorldPos; Nb = cv1.ViewNormal; Ub = cv1.UV;
                C = cv2.ClipPos; Vc = cv2.ViewPos; Wc = cv2.WorldPos; Nc = cv2.ViewNormal; Uc = cv2.UV;

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

                // compute face-mask ONCE per triangle
                int triFaceMask = anyFaceMasks ? FaceMaskFromTriAndAabb(Pa, Pb, Pc, bbMin, bbMax) : -1;

                for (int y = minY; y <= maxY; y++)
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

                        float z = w0 * aZw + w1 * bZw + w2 * cZw;
                        if (doubleSided && backfacing && !transparentPass) z += 1e-5f;

                        int idx = y * W + x;

                        float zTest = transparentPass ? (z - 1e-5f) : z;
                        if (zTest >= zbuf[idx]) continue;
                        if (!transparentPass) zbuf[idx] = z;

                        var viewPos = (w0 * Va * aInvW + w1 * Vb * bInvW + w2 * Vc * cInvW) / invW;

                        var normal = SN.Vector3.Normalize((w0 * Na * aInvW + w1 * Nb * bInvW + w2 * Nc * cInvW) / invW);
                        if (doubleSided && backfacing) normal = -normal;

                        // ---------------- Lighting ----------------
                        float ndl, atten = 1f; SN.Vector3 Ldir;
                        if (lightIsPoint)
                        {
                            var toL = lightPosV - viewPos;
                            float dist = toL.Length();
                            Ldir = toL / (dist + 1e-6f);

                            float dotNL = SN.Vector3.Dot(normal, Ldir);
                            if (doubleSided && dotNL < 0f) dotNL = -dotNL;
                            ndl = MathF.Max(0f, dotNL);

                            if (lightRange > 0f)
                            {
                                float tR = dist / lightRange;
                                atten = 1f / (1f + tR * tR);
                            }
                        }
                        else
                        {
                            Ldir = LdirV;
                            float dotNL = SN.Vector3.Dot(normal, Ldir);
                            ndl = MathF.Max(0f, dotNL);
                        }

                        float dir01 = Math.Clamp(ndl * atten, 0f, 1f);

                        // UVs
                        float u = (w0 * Ua.X * aInvW + w1 * Ub.X * bInvW + w2 * Uc.X * cInvW) / invW;
                        float v = (w0 * Ua.Y * aInvW + w1 * Ub.Y * bInvW + w2 * Uc.Y * cInvW) / invW;

                        // -------- material sampling (no reflection here) --------
                        Color albedo = Color.FromRgb(255, 255, 255);
                        Color detailMul = Color.FromRgb(255, 255, 255);
                        Color emissive = Color.FromRgb(0, 0, 0);
                        float aoMul = 1f, specMap = 0f, roughFromMap = -1f, metalFromMap = -1f;
                        float albedoAlpha = 0f, opacityMapMul = 1f;
                        bool hadAlbedoRGB = false, sawAlbedoSlot = false, hasOpacitySlot = false;

                        if (slots.Count > 0)
                        {
                            for (int si = 0; si < slots.Count; si++)
                            {
                                var s = slots[si];
                                if (s.FaceMask != -1 && triFaceMask != -1 && (s.FaceMask & triFaceMask) == 0) continue;

                                // UV xform
                                float uu = u, vv = v;
                                float U = (uu - 0.5f) * s.Su;
                                float V = (vv - 0.5f) * s.Sv;
                                if (MathF.Abs(s.Sn) > 1e-6f)
                                {
                                    float rU = U * s.Cs - V * s.Sn;
                                    float rV = U * s.Sn + V * s.Cs;
                                    U = rU; V = rV;
                                }
                                uu = U + 0.5f + s.Ou;
                                vv = V + 0.5f + s.Ov;

                                var samp = TextureSampling.SamplePMClamped(s.Tex, uu, s.NoFlipV ? (1f - vv) : vv);

                                if (s.Usage.Contains("emiss"))
                                    emissive = ColorUtil.AddColor(emissive, samp);
                                else if (s.Usage.Contains("occl") || s.Usage == "ao")
                                    aoMul *= Math.Clamp(ColorUtil.Luma(samp), 0f, 1f);
                                else if (s.Usage.Contains("detail"))
                                    detailMul = ColorUtil.MulColor(detailMul, samp);
                                else if (s.Usage.Contains("spec"))
                                    specMap = Math.Clamp(ColorUtil.Luma(samp), 0f, 1f);
                                else if (s.Usage.Contains("rough"))
                                    roughFromMap = Math.Clamp(ColorUtil.Luma(samp), 0f, 1f);
                                else if (s.Usage.Contains("metal"))
                                    metalFromMap = Math.Clamp(ColorUtil.Luma(samp), 0f, 1f);
                                else if (s.Usage.Contains("opacity") || s.Usage.Contains("alpha") || s.Usage.Contains("transp"))
                                {
                                    hasOpacitySlot = true;
                                    float op = (samp.A < 254) ? (samp.A / 255f) : Math.Clamp(ColorUtil.Luma(samp), 0f, 1f);
                                    opacityMapMul *= op;
                                    if (!hadAlbedoRGB && !sawAlbedoSlot)
                                    {
                                        albedo = ColorUtil.MulColor(albedo, Color.FromRgb(samp.R, samp.G, samp.B));
                                        hadAlbedoRGB = true;
                                    }
                                }
                                else
                                {
                                    albedo = ColorUtil.AlphaOver(albedo, samp);
                                    hadAlbedoRGB = true; sawAlbedoSlot = true;
                                    float aA = samp.A / 255f;
                                    albedoAlpha = albedoAlpha + (1f - albedoAlpha) * aA;
                                }
                            }
                        }

                        float metallic = metalFromMap >= 0 ? metalFromMap : Math.Clamp(mat?.Metallic ?? 0f, 0f, 1f);
                        float smooth = roughFromMap >= 0 ? (1f - roughFromMap) : Math.Clamp(mat?.Smoothness ?? 0.5f, 0f, 1f);
                        float specStr = Math.Clamp(specMap, 0f, 1f);
                        float shininess = 8f + smooth * smooth * 248f;

                        // lighting combine (exact)
                        float amb = Ambient * (1f - dir01);
                        float dif = DiffuseK * dir01;
                        float shade = amb * aoMul + dif;

                        Color lit = ColorUtil.ShadeColor(safeTint, shade);
                        Color baseCol = ColorUtil.MulColor(ColorUtil.MulColor(albedo, detailMul), lit);

                        // specular (Blinn-Phong), gated by direct
                        Color specAdd = Color.FromRgb(0, 0, 0);
                        if (DiffuseK > 0f && specStr > 0.001f && dir01 > 0f)
                        {
                            var Vdir = SN.Vector3.Normalize(-viewPos);
                            SN.Vector3 halfVec = SN.Vector3.Normalize(Ldir + Vdir);
                            float ndh = MathF.Max(0f, SN.Vector3.Dot(normal, halfVec));
                            float spec = MathF.Pow(ndh, shininess) * specStr * (0.25f + 0.75f * metallic) * dir01;
                            byte sr = (byte)Math.Clamp(spec * 255f, 0f, 255f);
                            specAdd = Color.FromRgb(sr, sr, sr);
                        }

                        Color pix = ColorUtil.AddColor(ColorUtil.AddColor(baseCol, specAdd), emissive);

                        if (transparentPass)
                        {
                            float baseAlpha = hasOpacitySlot ? 1f : (sawAlbedoSlot ? albedoAlpha : 1f);
                            float aEff = Math.Clamp(baseAlpha * opacityMapMul * matOpacity * tintA, 0f, 1f);
                            if (aEff <= 0.0001f) continue;
                            const float OPAQUEISH = 0.60f;
                            if (aEff >= OPAQUEISH) zbuf[idx] = z;
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

    // ======== PRIVATES ========

    private struct ResolvedSlot
    {
        public string Usage;     
        public Texture2D Tex;     // ensured Texture2D
        public int FaceMask;      // -1 = all
        public bool NoFlipV;
        public float Su, Sv, Ou, Ov;
        public float Cs, Sn;      // cos/sin of RotateUV
    }

    private static List<ResolvedSlot> ResolveSlots(Material mat)
    {
        var list = new List<ResolvedSlot>(mat?.Textures?.Count ?? 0);
        if (mat?.Textures == null) return list;

        for (int i = 0; i < mat.Textures.Count; i++)
        {
            var slot = mat.Textures[i];
            if (slot == null) continue;

            // Texture
            var pTex = slot.GetType().GetProperty("Texture",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var raw = pTex != null ? pTex.GetValue(slot) : null;
            var tex = raw as Texture2D ?? TextureBridge.EnsureEngineTexture2D(raw);
            if (tex == null || tex.Width <= 0 || tex.Height <= 0) continue;
            if (tex != raw && pTex != null && pTex.CanWrite) pTex.SetValue(slot, tex);

            // Usage + mask (use your existing helpers if present)
            string usage = GetUsageName(slot).ToLowerInvariant();
            int faceMask = GetFaceMask(slot);

            // UV xform
            float su = GetFloat(slot, "ScaleU", 1f);
            float sv = GetFloat(slot, "ScaleV", 1f);
            float ou = GetFloat(slot, "OffsetU", 0f);
            float ov = GetFloat(slot, "OffsetV", 0f);
            float rotDeg = GetFloat(slot, "RotateUV", 0f);
            float rot = rotDeg * (MathF.PI / 180f);
            float cs = MathF.Cos(rot), sn = MathF.Sin(rot);
            bool noFlipV = GetBool(slot, "NoFlipV", false);

            list.Add(new ResolvedSlot
            {
                Usage = usage,
                Tex = tex,
                FaceMask = faceMask,
                NoFlipV = noFlipV,
                Su = su,
                Sv = sv,
                Ou = ou,
                Ov = ov,
                Cs = cs,
                Sn = sn
            });
        }
        return list;
    }

    private static float GetFloat(object obj, string name, float defV)
    {
        var p = obj.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (p == null) return defV;
        var v = p.GetValue(obj);
        if (v is float f) return f;
        if (v is double d) return (float)d;
        if (v is int i) return i;
        return defV;
    }

    private static bool GetBool(object obj, string name, bool defV)
    {
        var p = obj.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (p == null) return defV;
        var v = p.GetValue(obj);
        if (v is bool b) return b;
        return defV;
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

    // --- UV2 cache (for wind metadata) ------------------------------------------
    private static readonly Dictionary<Type, SN.Vector2[]?> _uv2Cache = new Dictionary<Type, SN.Vector2[]?>();

    private static SN.Vector2[]? GetMeshUVs2(Mesh m)
    {
        var t = m.GetType();
        SN.Vector2[]? v;
        if (_uv2Cache.TryGetValue(t, out v)) return v;

        // common names that exporters use for the 2nd channel
        string[] names = { "UV2", "TexCoord1", "UV1" };
        foreach (var n in names)
        {
            var p = t.GetProperty(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(SN.Vector2[]))
            {
                v = (SN.Vector2[]?)p.GetValue(m);
                _uv2Cache[t] = v;
                return v;
            }
            var f = t.GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(SN.Vector2[]))
            {
                v = (SN.Vector2[]?)f.GetValue(m);
                _uv2Cache[t] = v;
                return v;
            }
        }
        _uv2Cache[t] = null;
        return null;
    }

    private static SN.Vector3 ApplyWind(in SN.Vector3 p, float weight01, float phase01,
                                    in SN.Vector3 windDir, float t, float amp)
    {
        // Clamp weight (safety if data is dirty)
        if (weight01 < 0f) weight01 = 0f; else if (weight01 > 1f) weight01 = 1f;

        // Phase gives per-vertex variability so trees don't sway in lockstep
        float phase = phase01 * 6.2831853f; // 2π

        // Low-frequency sway + a touch of flutter for leaves/cards
        float sway = (float)System.Math.Sin(t * 0.7f + phase);
        float flutter = 0.15f * (float)System.Math.Sin(t * 5.3f + phase * 1.7f);

        float disp = amp * weight01 * (0.6f * sway + 0.4f * flutter);

        // Simple displacement along wind direction (object space)
        var wd = windDir;
        if (wd.LengthSquared() > 1e-6f) wd = SN.Vector3.Normalize(wd);
        else wd = new SN.Vector3(1f, 0f, 0f);

        return p + wd * disp;
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


    private static string GetUsageName(MaterialTexture slot)
        => slot.GetType().GetProperty("Usage")?.GetValue(slot)?.ToString() ?? "Albedo";

    private static int GetFaceMask(object slot)
    {
        var prop = slot.GetType().GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop == null) return -1;
        var v = prop.GetValue(slot);
        if (v is int i) return i;
        if (v != null && v.GetType().IsEnum) return Convert.ToInt32(v);
        return -1;
    }

}
