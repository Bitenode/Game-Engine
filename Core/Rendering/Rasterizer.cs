#nullable enable
using Avalonia.Media;
using ComputeSharp;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SN = System.Numerics;
using GFX = ComputeSharp.GraphicsDeviceExtensions; // alias the v3 extensions

namespace Game_Engine.Core
{
    public static class Rasterizer
    {
        // =========================================================
        // Tunables
        // =========================================================
        private const bool USE_PARALLEL = true;
        private const int BAND_HEIGHT = 256;

        // GPU pre-Z options
        private const bool USE_GPU_PREZ = true;                 
        private const int GPU_PREZ_TRI_THRESHOLD = 1024;          // offload only when enough clipped tris
        private const float PREZ_EPS = 1e-5f;                    // small epsilon on the z-compare

        // cached GPU temp z and sticky flag if device is unavailable
        private static ReadWriteBuffer<uint>? sZTemp;
        private static int sZTempW, sZTempH;
        private static bool sGpuUnavailable;

        // quality toggles
        private const bool ENABLE_SPECULAR = true;
        private const bool ENABLE_AO = true;
        private const bool ENABLE_DETAIL = true;
        private const bool ENABLE_EMISSIVE = true;

        // --------------------- tiny packed-pixel helpers --------------------
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static uint MulPacked(uint a, uint b)
        {
            uint bb = ((a & 0xFF) * (b & 0xFF) + 0x80); bb = (bb + ((bb >> 8) & 0xFF)) >> 8;
            uint gg = (((a >> 8) & 0xFF) * ((b >> 8) & 0xFF) + 0x80); gg = (gg + ((gg >> 8) & 0xFF)) >> 8;
            uint rr = (((a >> 16) & 0xFF) * ((b >> 16) & 0xFF) + 0x80); rr = (rr + ((rr >> 8) & 0xFF)) >> 8;
            return (0xFFu << 24) | (rr << 16) | (gg << 8) | bb;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static uint AddSaturate(uint a, uint b)
        {
            int bb = (int)(a & 0xFF) + (int)(b & 0xFF); if (bb > 255) bb = 255;
            int gg = (int)((a >> 8) & 0xFF) + (int)((b >> 8) & 0xFF); if (gg > 255) gg = 255;
            int rr = (int)((a >> 16) & 0xFF) + (int)((b >> 16) & 0xFF); if (rr > 255) rr = 255;
            return 0xFF000000u | (uint)(rr << 16) | (uint)(gg << 8) | (uint)bb;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static uint GrayToPacked(byte g) => 0xFF000000u | ((uint)g << 16) | ((uint)g << 8) | g;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static float LumaPacked01(uint p)
        {
            float b = (p & 0xFF) / 255f;
            float g = ((p >> 8) & 0xFF) / 255f;
            float r = ((p >> 16) & 0xFF) / 255f;
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static uint AlphaOverPacked(uint under, uint over)
        {
            float ao = ((over >> 24) & 0xFF) / 255f;
            if (ao <= 0f) return under;
            if (ao >= 1f) return over;

            int ub = (int)(under & 0xFF);
            int ug = (int)((under >> 8) & 0xFF);
            int ur = (int)((under >> 16) & 0xFF);

            int ob = (int)(over & 0xFF);
            int og = (int)((over >> 8) & 0xFF);
            int orr = (int)((over >> 16) & 0xFF);

            float k = 1f - ao;
            int rb = ob + (int)(ub * k + 0.5f); if (rb > 255) rb = 255;
            int rg = og + (int)(ug * k + 0.5f); if (rg > 255) rg = 255;
            int rr = orr + (int)(ur * k + 0.5f); if (rr > 255) rr = 255;

            float au = ((under >> 24) & 0xFF) / 255f;
            float aout = ao + au * (1f - ao);
            int a8 = (int)(aout * 255f + 0.5f); if (a8 > 255) a8 = 255;

            return (uint)(a8 << 24) | (uint)(rr << 16) | (uint)(rg << 8) | (uint)rb;
        }

        // ======================= GPU PRE-Z TYPES & KERNELS ==================


        
        private static ReadWriteBuffer<uint> EnsureZTemp(GraphicsDevice device, int w, int h)
        {
            if (sZTemp is null || sZTempW != w || sZTempH != h)
            {
                sZTemp?.Dispose();
                sZTemp = device.AllocateReadWriteBuffer<uint>(w * h);
                sZTempW = w; sZTempH = h;
            }
            return sZTemp;
        }

        // ----------------------- thread-local scratch -----------------------
        [ThreadStatic] private static List<ClipVertex>? t_poly;
        [ThreadStatic] private static List<ClipVertex>? t_tmp;
        private static List<ClipVertex> Poly => t_poly ??= new List<ClipVertex>(8);
        private static List<ClipVertex> Tmp => t_tmp ??= new List<ClipVertex>(8);

        // ============================= DEPTH ================================
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

            for (int i = 0; i < I.Length; i += 3)
            {
                int ia = I[i], ib = I[i + 1], ic = I[i + 2];
                var a = V[ia]; var b = V[ib]; var c = V[ic];

                var Ac = SN.Vector4.Transform(new SN.Vector4(a, 1f), WVP);
                var Bc = SN.Vector4.Transform(new SN.Vector4(b, 1f), WVP);
                var Cc = SN.Vector4.Transform(new SN.Vector4(c, 1f), WVP);
                if (Ac.W <= 0f || Bc.W <= 0f || Cc.W <= 0f) continue;

                float iaW = 1f / Ac.W, ibW = 1f / Bc.W, icW = 1f / Cc.W;
                float ax = (Ac.X * iaW * 0.5f + 0.5f) * W;
                float ay = (1f - (Ac.Y * iaW * 0.5f + 0.5f)) * H;
                float bx = (Bc.X * ibW * 0.5f + 0.5f) * W;
                float by = (1f - (Bc.Y * ibW * 0.5f + 0.5f)) * H;
                float cx = (Cc.X * icW * 0.5f + 0.5f) * W;
                float cy = (1f - (Cc.Y * icW * 0.5f + 0.5f)) * H;
                float az = Ac.Z * iaW, bz = Bc.Z * ibW, cz = Cc.Z * icW;

                var av = SN.Vector3.Transform(a, WV);
                var bv = SN.Vector3.Transform(b, WV);
                var cv = SN.Vector3.Transform(c, WV);
                float viewNormalSign = winding * SN.Vector3.Cross(bv - av, cv - av).Z;
                if (!doubleSided && viewNormalSign >= 0f) continue;

                int minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
                int maxX = (int)MathF.Min(W - 1, MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                int minY = (int)MathF.Max(0, MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
                int maxY = (int)MathF.Min(H - 1, MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                if (minX > maxX || minY > maxY) continue;

                float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (area == 0f) continue;
                float invArea = 1f / area;

                float A01 = ay - by, B01 = bx - ax, C01 = -(A01 * ax + B01 * ay);
                float A12 = by - cy, B12 = cx - bx, C12 = -(A12 * bx + B12 * by);
                float A20 = cy - ay, B20 = ax - cx, C20 = -(A20 * cx + B20 * cy);

                void DoRows(int y0, int y1)
                {
                    float py = y0 + 0.5f;
                    for (int y = y0; y <= y1; y++, py += 1f)
                    {
                        int row = y * W;
                        float px = minX + 0.5f;

                        float w0 = A12 * px + B12 * py + C12;
                        float w1 = A20 * px + B20 * py + C20;
                        float w2 = A01 * px + B01 * py + C01;

                        int idx = row + minX;
                        for (int x = minX; x <= maxX; x++, idx++, px += 1f, w0 += A12, w1 += A20, w2 += A01)
                        {
                            if ((w0 >= 0f && w1 >= 0f && w2 >= 0f) || (w0 <= 0f && w1 <= 0f && w2 <= 0f))
                            {
                                float b0 = w0 * invArea;
                                float b1 = w1 * invArea;
                                float b2 = 1f - b0 - b1;

                                float z = b0 * az + b1 * bz + b2 * cz;
                                if (z < zbuf[idx]) zbuf[idx] = z;
                            }
                        }
                    }
                }

                if (USE_PARALLEL && maxY - minY + 1 >= BAND_HEIGHT * 2)
                {
                    int yBand0 = minY / BAND_HEIGHT;
                    int yBand1 = maxY / BAND_HEIGHT;
                    Parallel.For(yBand0, yBand1 + 1, band =>
                    {
                        int y0 = Math.Max(minY, band * BAND_HEIGHT);
                        int y1 = Math.Min(maxY, (band + 1) * BAND_HEIGHT - 1);
                        DoRows(y0, y1);
                    });
                }
                else
                {
                    DoRows(minY, maxY);
                }
            }
        }

        // ========================= SHADED PASS ==============================
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
            var Vtx = m.Vertices; if (Vtx == null || Vtx.Length == 0) return;
            var Idx = m.TriIndices; if (Idx == null || Idx.Length == 0) return;
            var Nor = m.Normals;

            // ---------- PRE-Z (GPU) ----------
            bool usePreZ = false;
            if (USE_GPU_PREZ && !transparentPass && !sGpuUnavailable)
            {
                // Build a light screen-space tri list for the GPU pass
                var triSS = BuildPreZTris(m, world, view, proj, W, H, doubleSided, invertFrontFace);
                if (triSS.Count >= GPU_PREZ_TRI_THRESHOLD)
                {
                    usePreZ = TryBuildPreZGPU(triSS, W, H, zbuf);
                    if (!usePreZ) sGpuUnavailable = true; // sticky
                }
            }

            // object AABB (for planar-UV fallback)
            SN.Vector3 bbMin = new SN.Vector3(float.MaxValue), bbMax = new SN.Vector3(float.MinValue);
            for (int v = 0; v < Vtx.Length; v++)
            {
                var p = Vtx[v];
                if (p.X < bbMin.X) bbMin.X = p.X; if (p.Y < bbMin.Y) bbMin.Y = p.Y; if (p.Z < bbMin.Z) bbMin.Z = p.Z;
                if (p.X > bbMax.X) bbMax.X = p.X; if (p.Y > bbMax.Y) bbMax.Y = p.Y; if (p.Z > bbMax.Z) bbMax.Z = p.Z;
            }
            var bbSize = new SN.Vector3(
                bbMax.X == bbMin.X ? 1f : (bbMax.X - bbMin.X),
                bbMax.Y == bbMin.Y ? 1f : (bbMax.Y - bbMin.Y),
                bbMax.Z == bbMin.Z ? 1f : (bbMax.Z - bbMin.Z));

            bool hasAnyTexture = mat?.Textures != null && mat.Textures.Count != 0;

            // matrices
            SN.Matrix4x4 mv = world * view;
            SN.Matrix4x4 mvp = mv * proj;

            // normal matrices
            SN.Matrix4x4.Invert(mv, out var invMv);
            SN.Matrix4x4 normalMatrixV = SN.Matrix4x4.Transpose(invMv);
            SN.Matrix4x4.Invert(world, out var invWorld);
            SN.Matrix4x4 normalMatrixW = SN.Matrix4x4.Transpose(invWorld);

            // world light
            SN.Vector3 LdirW = lightIsPoint ? SN.Vector3.Zero : SN.Vector3.Normalize(-L);
            const float near = 0.1f;
            const float INSIDE_EPS = 1e-3f;

            // material constants
            float matOpacity = 1f;
            if (mat != null)
            {
                var p = mat.GetType().GetProperty("Opacity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = p?.GetValue(mat);
                if (v is float f) matOpacity = Math.Clamp(f, 0f, 1f);
                else if (v is double d) matOpacity = (float)Math.Clamp(d, 0.0, 1.0);
            }
            float tintA = tint.A / 255f;

            // cache winding once
            float winding = world.GetDeterminant() >= 0 ? 1f : -1f;

            // pre-resolve texture slots once per draw
            var groups = BuildGroups(mat);

            // UV array (cached reflection once per type)
            var UVMesh = GetMeshUVs(m);

            // “safe” tint
            uint tintPacked = ((uint)tint.A << 24) | ((uint)tint.R << 16) | ((uint)tint.G << 8) | tint.B;
            uint safeTintPacked = ((tint.R | tint.G | tint.B) == 0) ? 0xFFFFFFFFu : tintPacked;

            // triangle loop
            for (int i = 0; i < Idx.Length; i += 3)
            {
                int ia = Idx[i], ib = Idx[i + 1], ic = Idx[i + 2];

                var Pa = Vtx[ia]; var Pb = Vtx[ib]; var Pc = Vtx[ic];

                // UV choose
                SN.Vector2 Ua, Ub, Uc;
                if (UVMesh != null && UVMesh.Length == Vtx.Length)
                { Ua = UVMesh[ia]; Ub = UVMesh[ib]; Uc = UVMesh[ic]; }
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

                // transforms
                var A = SN.Vector4.Transform(new SN.Vector4(Pa, 1f), mvp);
                var B = SN.Vector4.Transform(new SN.Vector4(Pb, 1f), mvp);
                var C = SN.Vector4.Transform(new SN.Vector4(Pc, 1f), mvp);

                var Va = SN.Vector3.Transform(Pa, mv);
                var Vb = SN.Vector3.Transform(Pb, mv);
                var Vc = SN.Vector3.Transform(Pc, mv);

                var Wa = SN.Vector3.Transform(Pa, world);
                var Wb = SN.Vector3.Transform(Pb, world);
                var Wc = SN.Vector3.Transform(Pc, world);

                var NaW = Nor != null ? SN.Vector3.TransformNormal(Nor[ia], normalMatrixW) : SN.Vector3.UnitY;
                var NbW = Nor != null ? SN.Vector3.TransformNormal(Nor[ib], normalMatrixW) : SN.Vector3.UnitY;
                var NcW = Nor != null ? SN.Vector3.TransformNormal(Nor[ic], normalMatrixW) : SN.Vector3.UnitY;

                var cv0 = new ClipVertex { ClipPos = A, ViewPos = Va, WorldPos = Wa, UV = Ua };
                var cv1 = new ClipVertex { ClipPos = B, ViewPos = Vb, WorldPos = Wb, UV = Ub };
                var cv2 = new ClipVertex { ClipPos = C, ViewPos = Vc, WorldPos = Wc, UV = Uc };

                var clipped = ClipTriangle(cv0, cv1, cv2, near);
                if (clipped.Count < 3) continue;

                for (int kt = 0; kt < clipped.Count - 2; kt++)
                {
                    cv0 = clipped[0]; cv1 = clipped[kt + 1]; cv2 = clipped[kt + 2];
                    A = cv0.ClipPos; Va = cv0.ViewPos; Wa = cv0.WorldPos; Ua = cv0.UV;
                    B = cv1.ClipPos; Vb = cv1.ViewPos; Wb = cv1.WorldPos; Ub = cv1.UV;
                    C = cv2.ClipPos; Vc = cv2.ViewPos; Wc = cv2.WorldPos; Uc = cv2.UV;

                    // backface (view space)
                    float facing = SN.Vector3.Cross(Vb - Va, Vc - Va).Z * winding;
                    if (invertFrontFace) facing = -facing;
                    bool backfacing = (facing >= 0f);
                    if (!doubleSided && backfacing) continue;

                    // screen mapping + depth setup
                    float aInvW = 1f / A.W; float ax = (A.X * aInvW * 0.5f + 0.5f) * W; float ay = (1f - (A.Y * aInvW * 0.5f + 0.5f)) * H; float az = A.Z * aInvW;
                    float bInvW = 1f / B.W; float bx = (B.X * bInvW * 0.5f + 0.5f) * W; float by = (1f - (B.Y * bInvW * 0.5f + 0.5f)) * H; float bz = B.Z * bInvW;
                    float cInvW = 1f / C.W; float cx = (C.X * cInvW * 0.5f + 0.5f) * W; float cy = (1f - (C.Y * cInvW * 0.5f + 0.5f)) * H; float cz = C.Z * cInvW;

                    if (az >= 1f && bz >= 1f && cz >= 1f) continue;

                    float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                    if (area == 0f) continue;
                    float invArea = 1f / area;

                    int minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
                    int maxX = (int)MathF.Min(W - 1, MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                    int minY = (int)MathF.Max(0, MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
                    int maxY = (int)MathF.Min(H - 1, MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                    if (minX > maxX || minY > maxY) continue;

                    int triFaceMask = transparentPass ? -1 : FaceMaskFromTriAndAabb(Pa, Pb, Pc, bbMin, bbMax);

                    float A01 = ay - by, B01 = bx - ax, C01 = -(A01 * ax + B01 * ay);
                    float A12 = by - cy, B12 = cx - bx, C12 = -(A12 * bx + B12 * by);
                    float A20 = cy - ay, B20 = ax - cx, C20 = -(A20 * cx + B20 * cy);

                    // ----- constants for analytic UV gradients (for LOD) -----
                    float gx0 = A12 * invArea, gy0 = B12 * invArea;
                    float gx1 = A20 * invArea, gy1 = B20 * invArea;
                    float gx2 = A01 * invArea, gy2 = B01 * invArea;

                    float u0p = Ua.X * aInvW, u1p = Ub.X * bInvW, u2p = Uc.X * cInvW;
                    float v0p = Ua.Y * aInvW, v1p = Ub.Y * bInvW, v2p = Uc.Y * cInvW;

                    float dDx = gx0 * aInvW + gx1 * bInvW + gx2 * cInvW;
                    float dDy = gy0 * aInvW + gy1 * bInvW + gy2 * cInvW;

                    float dNux = gx0 * u0p + gx1 * u1p + gx2 * u2p;
                    float dNuy = gy0 * u0p + gy1 * u1p + gy2 * u2p;
                    float dNvx = gx0 * v0p + gx1 * v1p + gx2 * v2p;
                    float dNvy = gy0 * v0p + gy1 * v1p + gy2 * v2p;

                    // Row drawer
                    void DrawRows(int y0, int y1)
                    {
                        float py = y0 + 0.5f;
                        for (int y = y0; y <= y1; y++, py += 1f)
                        {
                            int row = y * W;
                            float px = minX + 0.5f;

                            float w0 = A12 * px + B12 * py + C12;
                            float w1 = A20 * px + B20 * py + C20;
                            float w2 = A01 * px + B01 * py + C01;

                            int idx = row + minX;
                            for (int x = minX; x <= maxX; x++, idx++, px += 1f, w0 += A12, w1 += A20, w2 += A01)
                            {
                                if (area > 0f)
                                { if (w0 < -INSIDE_EPS || w1 < -INSIDE_EPS || w2 < -INSIDE_EPS) continue; }
                                else
                                { if (w0 > INSIDE_EPS || w1 > INSIDE_EPS || w2 > INSIDE_EPS) continue; }

                                float b0 = w0 * invArea;
                                float b1 = w1 * invArea;
                                float b2 = 1f - b0 - b1;

                                float z = b0 * az + b1 * bz + b2 * cz;

                                // ---------- PRE-Z TEST ----------
                                if (usePreZ)
                                {
                                    float z01 = z * 0.5f + 0.5f;
                                    float preZ = zbuf[idx];                    // [0,1]
                                    if (z01 > preZ + PREZ_EPS) continue;       // farther than recorded min
                                }
                                else
                                {
                                    // Legacy CPU zbuffer path (stores NDC z)
                                    if (transparentPass)
                                    {
                                        if (z >= zbuf[idx] - 1e-4f) continue;
                                    }
                                    else
                                    {
                                        if (z >= zbuf[idx]) continue;
                                        zbuf[idx] = z; // early write
                                    }
                                }

                                // common denom once (for attrs)
                                float denom = b0 * aInvW + b1 * bInvW + b2 * cInvW;
                                if (denom <= 0f) continue;

                                // world-space normal (renorm if needed)
                                var nW = (b0 * NaW * aInvW + b1 * NbW * bInvW + b2 * NcW * cInvW) / denom;
                                float nWlen2 = nW.X * nW.X + nW.Y * nW.Y + nW.Z * nW.Z;
                                if (nWlen2 < 0.85f || nWlen2 > 1.21f)
                                {
                                    float invLen = 1.0f / MathF.Sqrt(nWlen2 + 1e-12f);
                                    nW.X *= invLen; nW.Y *= invLen; nW.Z *= invLen;
                                }

                                // lighting
                                float ndl, atten = 1f;
                                SN.Vector3 LdirUsed = LdirW;
                                SN.Vector3 worldPos = default;
                                bool needWorld = lightIsPoint;

                                if (lightIsPoint)
                                {
                                    worldPos = (b0 * Wa * aInvW + b1 * Wb * bInvW + b2 * Wc * cInvW) / denom;
                                    var toL = lightPosW - worldPos;
                                    float dist = toL.Length();
                                    LdirUsed = toL / (dist + 1e-6f);
                                    float dotNL = SN.Vector3.Dot(nW, LdirUsed);
                                    if (doubleSided) dotNL = MathF.Abs(dotNL);
                                    ndl = dotNL > 0f ? dotNL : 0f;

                                    if (lightRange > 0f)
                                    {
                                        float tR = dist / lightRange;
                                        atten = 1f / (1f + tR * tR);
                                    }
                                }
                                else
                                {
                                    float dotNL = SN.Vector3.Dot(nW, LdirW);
                                    if (doubleSided) dotNL = MathF.Abs(dotNL);
                                    ndl = dotNL > 0f ? dotNL : 0f;
                                }

                                float dir01 = ndl * atten; if (dir01 > 1f) dir01 = 1f;

                                // ---------- UVs + analytic derivatives ----------
                                float Nu = b0 * u0p + b1 * u1p + b2 * u2p;
                                float Nv = b0 * v0p + b1 * v1p + b2 * v2p;
                                float u = Nu / denom;
                                float v = Nv / denom;

                                float invD2 = 1f / (denom * denom);
                                float dudx = (denom * dNux - Nu * dDx) * invD2;
                                float dudy = (denom * dNuy - Nu * dDy) * invD2;
                                float dvdx = (denom * dNvx - Nv * dDx) * invD2;
                                float dvdy = (denom * dNvy - Nv * dDy) * invD2;

                                // ---------- MATERIAL ----------
                                uint albedoPacked = 0xFFFFFFFFu;
                                uint detailMulPacked = 0xFFFFFFFFu;
                                uint emissivePacked = 0xFF000000u;

                                float aoMul = 1f, specMap = 0f, roughFromMap = -1f, metalFromMap = -1f;
                                float albedoAlpha = 0f;
                                bool sawAlbedo = false;
                                float opacityMul = 1f;
                                bool hadOpacity = false;

                                if (groups.HasAny)
                                {
                                    // Opacity first (transparent pass only)
                                    if (transparentPass && groups.Opacity.Length > 0)
                                    {
                                        hadOpacity = true;
                                        for (int slot = 0; slot < groups.Opacity.Length; slot++)
                                        {
                                            var rs = groups.Opacity[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            float op = ((samp >> 24) & 0xFF) < 254 ? (((samp >> 24) & 0xFF) / 255f) : LumaPacked01(samp);
                                            if (op <= 0f) { opacityMul = 0f; break; }
                                            opacityMul *= Math.Clamp(op, 0f, 1f);
                                            if (opacityMul <= 0.001f) break;
                                        }
                                        float aEffTest = opacityMul * matOpacity * tintA;
                                        if (aEffTest <= 0.0001f) continue;
                                    }

                                    // Emissive
                                    if (ENABLE_EMISSIVE && groups.Emissive.Length > 0)
                                    {
                                        for (int slot = 0; slot < groups.Emissive.Length; slot++)
                                        {
                                            var rs = groups.Emissive[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            emissivePacked = AddSaturate(emissivePacked, samp);
                                        }
                                    }

                                    // Albedo (capture alpha)
                                    if (groups.Albedo.Length > 0)
                                    {
                                        for (int slot = 0; slot < groups.Albedo.Length; slot++)
                                        {
                                            var rs = groups.Albedo[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            albedoPacked = AlphaOverPacked(albedoPacked, samp);
                                            sawAlbedo = true;
                                            float aA = ((samp >> 24) & 0xFF) / 255f;
                                            if (aA < 0.999f) albedoAlpha = albedoAlpha + (1f - albedoAlpha) * aA;
                                        }
                                    }

                                    // AO/Detail – skipped in transparent pass for speed
                                    if (!transparentPass && ENABLE_AO && Ambient > 0f && groups.Occlusion.Length > 0)
                                    {
                                        for (int slot = 0; slot < groups.Occlusion.Length; slot++)
                                        {
                                            var rs = groups.Occlusion[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            float oc = LumaPacked01(samp); aoMul *= Math.Clamp(oc, 0f, 1f);
                                        }
                                    }
                                    if (!transparentPass && ENABLE_DETAIL && Ambient > 0f && groups.Detail.Length > 0)
                                    {
                                        for (int slot = 0; slot < groups.Detail.Length; slot++)
                                        {
                                            var rs = groups.Detail[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint d = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            detailMulPacked = MulPacked(detailMulPacked, d);
                                        }
                                    }

                                    // Spec/rough/metal (only if actually lighting)
                                    if (DiffuseK > 0f && dir01 > 0f)
                                    {
                                        for (int slot = 0; slot < groups.Specular.Length; slot++)
                                        {
                                            var rs = groups.Specular[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            specMap = LumaPacked01(samp);
                                        }
                                        for (int slot = 0; slot < groups.Roughness.Length; slot++)
                                        {
                                            var rs = groups.Roughness[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            roughFromMap = LumaPacked01(samp);
                                        }
                                        for (int slot = 0; slot < groups.Metallic.Length; slot++)
                                        {
                                            var rs = groups.Metallic[slot];
                                            if (rs.FaceMask != -1 && triFaceMask != -1 && (rs.FaceMask & triFaceMask) == 0) continue;

                                            float uu = u, vv = v, dxu = dudx, dyu = dudy, dxv = dvdx, dyv = dvdy;
                                            ApplySlotUVTransform(rs, ref uu, ref vv, ref dxu, ref dyu, ref dxv, ref dyv);

                                            uint samp = TexMip.SamplePackedLOD(rs.Tex, uu, vv, dxu, dyu, dxv, dyv);
                                            metalFromMap = LumaPacked01(samp);
                                        }
                                    }
                                }

                                // ==== lighting combine ====
                                float metallic = metalFromMap >= 0f ? metalFromMap : (mat?.Metallic ?? 0f);
                                metallic = Math.Clamp(metallic, 0f, 1f);

                                float smooth = roughFromMap >= 0f ? (1f - roughFromMap) : (mat?.Smoothness ?? 0.5f);
                                smooth = Math.Clamp(smooth, 0f, 1f);

                                float specStr = Math.Clamp(specMap, 0f, 1f);
                                float shininess = 8f + smooth * smooth * 248f;

                                float amb = Ambient * (1f + dir01);
                                float dif = DiffuseK * dir01;
                                float shade = Math.Clamp(amb * aoMul + dif, 0f, 1f);

                                uint baseCol = MulPacked(albedoPacked, detailMulPacked);

                                int shade8 = (int)(shade * 255f + 0.5f);
                                if (shade8 < 0) shade8 = 0; else if (shade8 > 255) shade8 = 255;
                                uint sPacked = 0xFF000000u | (uint)(shade8 << 16) | (uint)(shade8 << 8) | (uint)shade8;

                                uint pix = MulPacked(MulPacked(baseCol, sPacked), safeTintPacked);

                                if (ENABLE_SPECULAR && DiffuseK > 0f && specStr > 0.001f && dir01 > 0f)
                                {
                                    if (!needWorld) worldPos = (b0 * Wa * aInvW + b1 * Wb * bInvW + b2 * Wc * cInvW) / denom;
                                    var VdirW = -worldPos; float len = VdirW.Length(); VdirW = len > 1e-6f ? VdirW / len : new SN.Vector3(0, 0, -1);
                                    var halfVec = SN.Vector3.Normalize(LdirUsed + VdirW);
                                    float ndh = SN.Vector3.Dot(nW, halfVec); if (ndh < 0f) ndh = 0f;

                                    float spec = MathF.Pow(ndh, shininess) * specStr * (0.25f + 0.75f * metallic) * dir01;
                                    int sb = (int)(spec * 255f + 0.5f); if (sb < 0) sb = 0; else if (sb > 255) sb = 255;
                                    pix = AddSaturate(pix, GrayToPacked((byte)sb));
                                }

                                // emissive add
                                pix = AddSaturate(pix, emissivePacked);

                                if (transparentPass)
                                {
                                    float baseAlpha = (groups.HasAny && hadOpacity) ? opacityMul
                                                    : ((groups.HasAny && sawAlbedo && albedoAlpha > 0f) ? albedoAlpha : 1f);
                                    float aEff = Math.Clamp(baseAlpha * matOpacity * tintA, 0f, 1f);
                                    if (aEff > 0.0001f)
                                        color[idx] = ColorUtil.BlendOverBGRA(color[idx], pix, (byte)(aEff * 255f + 0.5f));
                                }
                                else
                                {
                                    color[idx] = pix;
                                }
                            }
                        }
                    }
                    ;

                    if (USE_PARALLEL && maxY - minY + 1 >= BAND_HEIGHT * 2)
                    {
                        int yBand0 = minY / BAND_HEIGHT;
                        int yBand1 = maxY / BAND_HEIGHT;
                        Parallel.For(yBand0, yBand1 + 1, by =>
                        {
                            int y0 = Math.Max(minY, by * BAND_HEIGHT);
                            int y1 = Math.Min(maxY, (by + 1) * BAND_HEIGHT - 1);
                            DrawRows(y0, y1);
                        });
                    }
                    else
                    {
                        DrawRows(minY, maxY);
                    }
                }
            }
        }

        // ====================== PRE-Z BUILD HELPERS =========================

        private static bool TryBuildPreZGPU(List<TriSS> triSS, int W, int H, float[] preZ01)
        {
#if !COMPUTE_PREZ
    return false;
#else
            if (sGpuUnavailable || triSS.Count == 0) return false;

            try
            {
                var device = GraphicsDevice.GetDefault();
                using ReadWriteBuffer<uint> zb = EnsureZTemp(device, W, H);

                // clear to far
                device.For(zb.Length, new ClearKernel(zb));

                // upload triangles (already clipped, z in [0,1])
                ReadOnlySpan<TriSS> span = CollectionsMarshal.AsSpan(triSS);
                using ReadOnlyBuffer<TriSS> tbuf = device.AllocateReadOnlyBuffer<TriSS>(span);

                // one thread per triangle
                device.For(triSS.Count, new ZOnlyKernel(tbuf, zb, W, H));

                // read back packed 24-bit depth -> float [0,1]
                var packed = new uint[W * H];
                zb.CopyTo(packed);

                const float inv24 = 1.0f / 16777215.0f;
                for (int i = 0; i < packed.Length; i++)
                    preZ01[i] = packed[i] * inv24;

                return true;
            }
            catch
            {
                // Device missing / WARP fallback unsupported / generator issue
                sGpuUnavailable = true;
                return false;
            }
#endif
        }


        // Build clipped screen-space triangles with z in [0,1] for the GPU pre-Z pass
        private static List<TriSS> BuildPreZTris(
    Mesh m,
    in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    int W, int H, bool doubleSided, bool invertFrontFace)
        {
            var Vtx = m.Vertices!;
            var Idx = m.TriIndices!;

            // Matrices
            SN.Matrix4x4 mv = world * view;
            SN.Matrix4x4 mvp = mv * proj;

            // Winding like the shaded pass
            float winding = world.GetDeterminant() >= 0 ? 1f : -1f;

            const float near = 0.1f;
            var outTris = new List<TriSS>(Idx.Length / 3);

            for (int i = 0; i < Idx.Length; i += 3)
            {
                int ia = Idx[i], ib = Idx[i + 1], ic = Idx[i + 2];

                // Original object-space
                var Pa = Vtx[ia]; var Pb = Vtx[ib]; var Pc = Vtx[ic];

                // Clip-space
                var A = SN.Vector4.Transform(new SN.Vector4(Pa, 1f), mvp);
                var B = SN.Vector4.Transform(new SN.Vector4(Pb, 1f), mvp);
                var C = SN.Vector4.Transform(new SN.Vector4(Pc, 1f), mvp);

                // View-space (needed to match shaded-pass backface test)
                var Va = SN.Vector3.Transform(Pa, mv);
                var Vb = SN.Vector3.Transform(Pb, mv);
                var Vc = SN.Vector3.Transform(Pc, mv);

                // Seed clip verts with BOTH clip and view positions so clipping
                // keeps view positions coherent for the culling test.
                var cv0 = new ClipVertex { ClipPos = A, ViewPos = Va };
                var cv1 = new ClipVertex { ClipPos = B, ViewPos = Vb };
                var cv2 = new ClipVertex { ClipPos = C, ViewPos = Vc };

                var poly = ClipTriangle(cv0, cv1, cv2, near);
                if (poly.Count < 3) continue;

                // Fan triangulation of the clipped polygon
                for (int k = 0; k < poly.Count - 2; k++)
                {
                    var c0 = poly[0];
                    var c1 = poly[k + 1];
                    var c2 = poly[k + 2];

                    // --- Backface test in VIEW space (same as shaded pass) ---
                    float facing = SN.Vector3.Cross(c1.ViewPos - c0.ViewPos, c2.ViewPos - c0.ViewPos).Z * winding;
                    if (invertFrontFace) facing = -facing;
                    bool backfacing = (facing >= 0f);
                    if (!doubleSided && backfacing) continue;

                    // Perspective divide
                    float aInvW = 1f / c0.ClipPos.W;
                    float bInvW = 1f / c1.ClipPos.W;
                    float cInvW = 1f / c2.ClipPos.W;

                    // Screen coords
                    float ax = (c0.ClipPos.X * aInvW * 0.5f + 0.5f) * W;
                    float ay = (1f - (c0.ClipPos.Y * aInvW * 0.5f + 0.5f)) * H;

                    float bx = (c1.ClipPos.X * bInvW * 0.5f + 0.5f) * W;
                    float by = (1f - (c1.ClipPos.Y * bInvW * 0.5f + 0.5f)) * H;

                    float cx = (c2.ClipPos.X * cInvW * 0.5f + 0.5f) * W;
                    float cy = (1f - (c2.ClipPos.Y * cInvW * 0.5f + 0.5f)) * H;

                    // Depth in [0,1] (REQUIRED by the GPU kernel)
                    float az01 = c0.ClipPos.Z * aInvW * 0.5f + 0.5f;
                    float bz01 = c1.ClipPos.Z * bInvW * 0.5f + 0.5f;
                    float cz01 = c2.ClipPos.Z * cInvW * 0.5f + 0.5f;

                    // All behind far plane? (early-out)
                    if (az01 >= 1f && bz01 >= 1f && cz01 >= 1f) continue;

                    // Pixel-space bbox (inclusive). The GPU kernel will clamp.
                    float minXf = MathF.Min(ax, MathF.Min(bx, cx));
                    float maxXf = MathF.Max(ax, MathF.Max(bx, cx));
                    float minYf = MathF.Min(ay, MathF.Min(by, cy));
                    float maxYf = MathF.Max(ay, MathF.Max(by, cy));

                    int minXi = (int)MathF.Floor(minXf);
                    int maxXi = (int)MathF.Ceiling(maxXf);
                    int minYi = (int)MathF.Floor(minYf);
                    int maxYi = (int)MathF.Ceiling(maxYf);

                    outTris.Add(new TriSS(
                        ax, ay, az01,
                        bx, by, bz01,
                        cx, cy, cz01,
                        minXi, minYi, maxXi, maxYi));
                }
            }

            return outTris;
        }


        // ============================ CLIPPING ==============================
        private struct ClipVertex
        {
            public SN.Vector4 ClipPos;
            public SN.Vector3 ViewPos;
            public SN.Vector3 WorldPos;
            public SN.Vector2 UV;
        }

        private static ClipVertex Lerp(in ClipVertex a, in ClipVertex b, float t) => new ClipVertex
        {
            ClipPos = a.ClipPos + t * (b.ClipPos - a.ClipPos),
            ViewPos = a.ViewPos + t * (b.ViewPos - a.ViewPos),
            WorldPos = a.WorldPos + t * (b.WorldPos - a.WorldPos),
            UV = a.UV + t * (b.UV - a.UV)
        };

        private static List<ClipVertex> ClipTriangle(ClipVertex v0, ClipVertex v1, ClipVertex v2, float near)
        {
            var poly = Poly; poly.Clear();
            poly.Add(v0); poly.Add(v1); poly.Add(v2);
            return ClipAgainstPlane(poly, new SN.Vector4(0f, 0f, 0f, 1f), -near);
        }

        private static List<ClipVertex> ClipAgainstPlane(List<ClipVertex> poly, SN.Vector4 plane, float planeD)
        {
            var res = Tmp; res.Clear();
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
                    res.Add(Lerp(prev, curr, t));
                }
                if (currIn) res.Add(curr);
            }
            var tmp = t_poly; t_poly = t_tmp; t_tmp = tmp; // swap buffers
            return Poly;
        }

        // ======================= UVs & FACEMASK HELPERS =====================
        private static readonly Dictionary<Type, SN.Vector2[]?> _uvCache = new();

        private static SN.Vector2[]? GetMeshUVs(Mesh m)
        {
            var t = m.GetType();
            if (_uvCache.TryGetValue(t, out var cached)) return cached;

            string[] names = { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i], flags);
                if (p != null && p.PropertyType == typeof(SN.Vector2[]))
                { var v = (SN.Vector2[]?)p.GetValue(m); _uvCache[t] = v; return v; }
                var f = t.GetField(names[i], flags);
                if (f != null && f.FieldType == typeof(SN.Vector2[]))
                { var v = (SN.Vector2[]?)f.GetValue(m); _uvCache[t] = v; return v; }
            }
            _uvCache[t] = null;
            return null;
        }

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

            if (an.X >= an.Y && an.X >= an.Z) return (cx >= mx) ? 1 : 2;  // +X/-X
            if (an.Y >= an.X && an.Y >= an.Z) return (cy >= my) ? 4 : 8;  // +Y/-Y
            return (cz >= mz) ? 16 : 32;                                   // +Z/-Z
        }

        // ======================== TEXTURE MIP SYSTEM ========================
        private static class TexMip
        {
            private sealed class Chain
            {
                public int Levels = 0;
                public int[] W = Array.Empty<int>();
                public int[] H = Array.Empty<int>();
                public uint[][] Data = Array.Empty<uint[]>();
            }

            private static readonly Dictionary<object, Chain> _cache = new();

            private static readonly Dictionary<Type, Func<object, int>> _getW = new();
            private static readonly Dictionary<Type, Func<object, int>> _getH = new();
            private static readonly Dictionary<Type, Func<object, uint[]?>> _getPixels = new();

            private static Func<object, int> GetterInt(Type t, string name, int def)
            {
                if (name == "") return _ => def;
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(int)) return s => (int)(p.GetValue(s) ?? def);
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(int)) return s => (int)(f.GetValue(s) ?? def);
                return _ => def;
            }
            private static Func<object, uint[]?> GetterPixels(Type t)
            {
                string[] names = { "Pixels", "Data", "BGRA", "Buffer", "Raw" };
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var n in names)
                {
                    var p = t.GetProperty(n, flags);
                    if (p != null && p.PropertyType == typeof(uint[])) return s => (uint[]?)p.GetValue(s);
                    var f = t.GetField(n, flags);
                    if (f != null && f.FieldType == typeof(uint[])) return s => (uint[]?)f.GetValue(s);
                }
                return _ => null;
            }

            private static int GetW(Texture2D tex)
            {
                var t = tex.GetType();
                if (!_getW.TryGetValue(t, out var g))
                {
                    g = GetterInt(t, "Width", 256);
                    _getW[t] = g;
                }
                return g(tex);
            }
            private static int GetH(Texture2D tex)
            {
                var t = tex.GetType();
                if (!_getH.TryGetValue(t, out var g))
                {
                    g = GetterInt(t, "Height", 256);
                    _getH[t] = g;
                }
                return g(tex);
            }
            private static uint[]? TryGetPixels(Texture2D tex)
            {
                var t = tex.GetType();
                if (!_getPixels.TryGetValue(t, out var g))
                {
                    g = GetterPixels(t);
                    _getPixels[t] = g;
                }
                return g(tex);
            }

            private static Chain Build(Texture2D tex)
            {
                int w0 = Math.Max(1, GetW(tex));
                int h0 = Math.Max(1, GetH(tex));

                uint[] basePixels = TryGetPixels(tex) ?? SampleWholeTexture(tex, w0, h0);

                var chain = new Chain();

                var levels = new List<uint[]>();
                var widths = new List<int>();
                var heights = new List<int>();

                int w = w0, h = h0;
                uint[] prev = basePixels;
                while (true)
                {
                    levels.Add(prev);
                    widths.Add(w);
                    heights.Add(h);
                    if (w == 1 && h == 1) break;

                    int w1 = Math.Max(1, w >> 1);
                    int h1 = Math.Max(1, h >> 1);
                    uint[] next = new uint[w1 * h1];

                    for (int y = 0; y < h1; y++)
                    {
                        int y0 = y * 2;
                        int y1s = Math.Min(y0 + 1, h - 1);

                        for (int x = 0; x < w1; x++)
                        {
                            int x0 = x * 2;
                            int x1s = Math.Min(x0 + 1, w - 1);

                            uint p00 = prev[y0 * w + x0];
                            uint p10 = prev[y0 * w + x1s];
                            uint p01 = prev[y1s * w + x0];
                            uint p11 = prev[y1s * w + x1s];

                            int b = ((int)(p00 & 0xFF) + (int)(p10 & 0xFF) + (int)(p01 & 0xFF) + (int)(p11 & 0xFF) + 2) >> 2;
                            int g = (((int)(p00 >> 8) & 0xFF) + ((int)(p10 >> 8) & 0xFF) + ((int)(p01 >> 8) & 0xFF) + ((int)(p11 >> 8) & 0xFF) + 2) >> 2;
                            int r = (((int)(p00 >> 16) & 0xFF) + ((int)(p10 >> 16) & 0xFF) + ((int)(p01 >> 16) & 0xFF) + ((int)(p11 >> 16) & 0xFF) + 2) >> 2;
                            int a = (((int)(p00 >> 24) & 0xFF) + ((int)(p10 >> 24) & 0xFF) + ((int)(p01 >> 24) & 0xFF) + ((int)(p11 >> 24) & 0xFF) + 2) >> 2;

                            next[y * w1 + x] = (uint)(a << 24) | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
                        }
                    }

                    prev = next; w = w1; h = h1;
                }

                chain.Levels = levels.Count;
                chain.Data = levels.ToArray();
                chain.W = widths.ToArray();
                chain.H = heights.ToArray();
                return chain;
            }

            private static uint[] SampleWholeTexture(Texture2D tex, int w, int h)
            {
                var buf = new uint[w * h];
                for (int y = 0; y < h; y++)
                {
                    float v = (y + 0.5f) / h;
                    for (int x = 0; x < w; x++)
                    {
                        float u = (x + 0.5f) / w;
                        var c = TextureSampling.SamplePMClamped(tex, u, v);
                        buf[y * w + x] = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
                    }
                }
                return buf;
            }

            private static Chain Ensure(Texture2D tex)
            {
                if (!_cache.TryGetValue(tex, out var chain))
                {
                    chain = Build(tex);
                    _cache[tex] = chain;
                }
                return chain;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            private static int SelectLevel(Chain c, float dudx, float dudy, float dvdx, float dvdy)
            {
                float rhoX = MathF.Sqrt((dudx * c.W[0]) * (dudx * c.W[0]) + (dvdx * c.H[0]) * (dvdx * c.H[0]));
                float rhoY = MathF.Sqrt((dudy * c.W[0]) * (dudy * c.W[0]) + (dvdy * c.H[0]) * (dvdy * c.H[0]));
                float rho = MathF.Max(rhoX, rhoY);
                if (rho <= 1f) return 0;
                float lod = MathF.Log2(rho);
                int level = (int)(lod + 0.5f);
                if (level < 0) level = 0;
                if (level >= c.Levels) level = c.Levels - 1;
                return level;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            private static uint SampleNearest(uint[] data, int w, int h, float u, float v)
            {
                if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
                if (v < 0f) v = 0f; else if (v > 1f) v = 1f;
                int x = (int)(u * (w - 1) + 0.5f);
                int y = (int)(v * (h - 1) + 0.5f);
                return data[y * w + x];
            }

            public static uint SamplePackedLOD(Texture2D tex, float u, float v, float dudx, float dudy, float dvdx, float dvdy)
            {
                var c = Ensure(tex);
                int lvl = SelectLevel(c, dudx, dudy, dvdx, dvdy);
                return SampleNearest(c.Data[lvl], c.W[lvl], c.H[lvl], u, v);
            }
        }

        // ============================ MATERIAL SLOTS =========================
        private enum SlotUsage : byte { Albedo, Emissive, Occlusion, Detail, Specular, Roughness, Metallic, Opacity, Normal, Unknown }

        private readonly struct ResolvedSlot
        {
            public readonly Texture2D Tex;
            public readonly SlotUsage Usage;
            public readonly int FaceMask;  // -1 any
            public readonly bool NoFlipV;
            public readonly float Su, Sv, Ou, Ov, Cs, Sn;

            public ResolvedSlot(Texture2D tex, SlotUsage usage, int mask, bool noFlipV,
                                float su, float sv, float ou, float ov, float cs, float sn)
            { Tex = tex; Usage = usage; FaceMask = mask; NoFlipV = noFlipV; Su = su; Sv = sv; Ou = ou; Ov = ov; Cs = cs; Sn = sn; }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void ApplyUV(float uIn, float vIn, out float u, out float v)
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

        private sealed class MatGroups
        {
            public ResolvedSlot[] Albedo = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Emissive = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Opacity = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Occlusion = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Detail = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Specular = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Roughness = Array.Empty<ResolvedSlot>();
            public ResolvedSlot[] Metallic = Array.Empty<ResolvedSlot>();
            public bool HasAny;
            public bool HasOpacity;
        }

        private static readonly Dictionary<Type, Func<object, Texture2D?>> _getterTexture = new();
        private static readonly Dictionary<Type, Func<object, string>> _getterUsage = new();
        private static readonly Dictionary<Type, Func<object, int>> _getterMask = new();
        private static readonly Dictionary<Type, Func<object, bool>> _getterNoFlipV = new();
        private static readonly Dictionary<Type, (Func<object, float> su, Func<object, float> sv, Func<object, float> ou, Func<object, float> ov, Func<object, float> rot)>
            _getterUV = new();

        private static Texture2D? GetTextureFast(object slot)
        {
            var t = slot.GetType();
            if (!_getterTexture.TryGetValue(t, out var g))
            {
                var p = t.GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                g = p == null ? (_ => null) : (object s) =>
                {
                    var raw = p.GetValue(s);
                    return raw as Texture2D ?? TextureBridge.EnsureEngineTexture2D(raw);
                };
                _getterTexture[t] = g;
            }
            return g(slot);
        }

        private static string GetUsageFast(object slot)
        {
            var t = slot.GetType();
            if (!_getterUsage.TryGetValue(t, out var g))
            {
                var p = t.GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                g = p == null ? (_ => "Albedo") : (object s) => p.GetValue(s)?.ToString() ?? "Albedo";
                _getterUsage[t] = g;
            }
            return g(slot);
        }

        private static int GetMaskFast(object slot)
        {
            var t = slot.GetType();
            if (!_getterMask.TryGetValue(t, out var g))
            {
                var p = t.GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                g = p == null ? (_ => -1) : (object s) =>
                {
                    var v = p.GetValue(s);
                    if (v is int i) return i;
                    return v != null && v.GetType().IsEnum ? Convert.ToInt32(v) : -1;
                };
                _getterMask[t] = g;
            }
            return g(slot);
        }

        private static bool GetNoFlipVFast(object slot)
        {
            var t = slot.GetType();
            if (!_getterNoFlipV.TryGetValue(t, out var g))
            {
                var p = t.GetProperty("NoFlipV", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                g = p == null ? (_ => false) : (object s) => p.GetValue(s) is bool b && b;
                _getterNoFlipV[t] = g;
            }
            return g(slot);
        }

        private static (float su, float sv, float ou, float ov, float cs, float sn) GetUVXformFast(object slot)
        {
            var t = slot.GetType();
            if (!_getterUV.TryGetValue(t, out var g))
            {
                Func<object, float> gf(string name, float def)
                {
                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    return p == null ? (_ => def) : (object s) =>
                    {
                        var v = p.GetValue(s);
                        if (v is float f) return f;
                        if (v is double d) return (float)d;
                        return def;
                    };
                }
                g = (gf("ScaleU", 1f), gf("ScaleV", 1f), gf("OffsetU", 0f), gf("OffsetV", 0f), gf("RotateUV", 0f));
                _getterUV[t] = g;
            }
            float su = g.su(slot), sv = g.sv(slot), ou = g.ou(slot), ov = g.ov(slot);
            float rot = g.rot(slot) * (MathF.PI / 180f);
            float cs = MathF.Abs(rot) < 1e-6f ? 1f : MathF.Cos(rot);
            float sn = MathF.Abs(rot) < 1e-6f ? 0f : MathF.Sin(rot);
            return (su, sv, ou, ov, cs, sn);
        }

        private static SlotUsage ParseUsage(string u)
        {
            u = u.ToLowerInvariant();
            if (u.Contains("emis")) return SlotUsage.Emissive;
            if (u.Contains("ambientoc") || u.Contains("occl") || u == "ao") return SlotUsage.Occlusion;
            if (u.Contains("detail")) return SlotUsage.Detail;
            if (u.Contains("spec")) return SlotUsage.Specular;
            if (u.Contains("rough")) return SlotUsage.Roughness;
            if (u.Contains("metal")) return SlotUsage.Metallic;
            if (u.Contains("opacity") || u.Contains("alpha") || u.Contains("transp")) return SlotUsage.Opacity;
            if (u.Contains("normal")) return SlotUsage.Normal;
            return SlotUsage.Albedo;
        }


        private static MatGroups BuildGroups(Material? mat)
        {
            var g = new MatGroups();
            if (mat?.Textures == null || mat.Textures.Count == 0) return g;

            var alb = new List<ResolvedSlot>(4);
            var emi = new List<ResolvedSlot>(2);
            var opa = new List<ResolvedSlot>(2);
            var occ = new List<ResolvedSlot>(2);
            var det = new List<ResolvedSlot>(2);
            var spc = new List<ResolvedSlot>(1);
            var rgh = new List<ResolvedSlot>(1);
            var met = new List<ResolvedSlot>(1);

            for (int i = 0; i < mat.Textures.Count; i++)
            {
                var slot = mat.Textures[i];
                if (slot == null) continue;
                var tex = GetTextureFast(slot);
                if (tex == null) continue;

                var (su, sv, ou, ov, cs, sn) = GetUVXformFast(slot);
                bool noFlipV = GetNoFlipVFast(slot);
                var rs = new ResolvedSlot(tex, ParseUsage(GetUsageFast(slot)), GetMaskFast(slot), noFlipV, su, sv, ou, ov, cs, sn);

                switch (rs.Usage)
                {
                    case SlotUsage.Emissive: emi.Add(rs); break;
                    case SlotUsage.Opacity: opa.Add(rs); break;
                    case SlotUsage.Occlusion: occ.Add(rs); break;
                    case SlotUsage.Detail: det.Add(rs); break;
                    case SlotUsage.Specular: spc.Add(rs); break;
                    case SlotUsage.Roughness: rgh.Add(rs); break;
                    case SlotUsage.Metallic: met.Add(rs); break;
                    default: alb.Add(rs); break;
                }
            }

            g.Albedo = alb.Count > 0 ? alb.ToArray() : Array.Empty<ResolvedSlot>();
            g.Emissive = emi.Count > 0 ? emi.ToArray() : Array.Empty<ResolvedSlot>();
            g.Opacity = opa.Count > 0 ? opa.ToArray() : Array.Empty<ResolvedSlot>();
            g.Occlusion = occ.Count > 0 ? occ.ToArray() : Array.Empty<ResolvedSlot>();
            g.Detail = det.Count > 0 ? det.ToArray() : Array.Empty<ResolvedSlot>();
            g.Specular = spc.Count > 0 ? spc.ToArray() : Array.Empty<ResolvedSlot>();
            g.Roughness = rgh.Count > 0 ? rgh.ToArray() : Array.Empty<ResolvedSlot>();
            g.Metallic = met.Count > 0 ? met.ToArray() : Array.Empty<ResolvedSlot>();
            g.HasAny = alb.Count + emi.Count + opa.Count + occ.Count + det.Count + spc.Count + rgh.Count + met.Count > 0;
            g.HasOpacity = opa.Count > 0;
            return g;
        }

        // APPLY SLOT UV TRANSFORM 
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void ApplySlotUVTransform(in ResolvedSlot rs,
            ref float u, ref float v, ref float dudx, ref float dudy, ref float dvdx, ref float dvdy)
        {
            // translate to (-0.5,0.5) space, scale, rotate, translate back + offsets
            float uu = (u - 0.5f) * rs.Su;
            float vv = (v - 0.5f) * rs.Sv;

            float x = uu * rs.Cs - vv * rs.Sn;
            float y = uu * rs.Sn + vv * rs.Cs;
            u = x + 0.5f + rs.Ou;
            v = y + 0.5f + rs.Ov;

            // derivatives: same linear transform
            float dxu = dudx * rs.Su, dyu = dudy * rs.Su;
            float dxv = dvdx * rs.Sv, dyv = dvdy * rs.Sv;

            float ndxu = dxu * rs.Cs - dxv * rs.Sn;
            float ndxv = dxu * rs.Sn + dxv * rs.Cs;
            float ndyu = dyu * rs.Cs - dyv * rs.Sn;
            float ndyv = dyu * rs.Sn + dyv * rs.Cs;

            dudx = ndxu; dudy = ndyu; dvdx = ndxv; dvdy = ndyv;


            // if NoFlipV == true -> flip V (assets authored with opposite convention)
            if (rs.NoFlipV)
            {
                v = 1f - v;
                dvdx = -dvdx;
                dvdy = -dvdy;
            }
        }



    }
}