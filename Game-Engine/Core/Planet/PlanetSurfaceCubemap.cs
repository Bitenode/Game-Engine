#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Authoritative planet surface: 6-face height cubemap + dig deltas + 8-weight splat.
/// Height is meters above <see cref="PlanetConfig.Radius"/>. Dig deltas are separate
/// so rebaking biomes does not wipe player sculpt.
/// </summary>
public sealed class PlanetSurfaceCubemap
{
    public const int DefaultResolution = 512;
    public const int MinResolution = 256;
    public const int MaxResolution = 1024;
    public const int SplatLayerCount = 8;

    public int Resolution { get; }
    public ulong RecipeHash { get; }

    /// <summary>Per face, row-major [v * res + u] height above planet radius.</summary>
    public float[][] Height { get; }

    /// <summary>Per face dig sculpt added to <see cref="Height"/> (same layout).</summary>
    public float[][] HeightDelta { get; }

    /// <summary>Layers 0–3 as RGBA floats per texel: length res*res*4.</summary>
    public float[][] Splat0 { get; }

    /// <summary>Layers 4–7 as RGBA floats per texel: length res*res*4.</summary>
    public float[][] Splat1 { get; }

    /// <summary>Height/dig revision used by shell meshes and the dig GPU atlas.</summary>
    public int HeightVersion { get; private set; }

    /// <summary>Biome-weight revision; height-only digs must not re-upload splat atlases.</summary>
    public int SplatVersion { get; private set; }

    /// <summary>Compatibility alias for mesh recipes, which depend on edited height.</summary>
    public int Version => HeightVersion;

    /// <summary>
    /// True after a graph bake filled <see cref="Height"/> and splat.
    /// Scratch maps created so digs can land during an in-flight bake stay false.
    /// </summary>
    public bool HasBaseHeights { get; private set; }

    public PlanetSurfaceCubemap(int resolution, ulong recipeHash)
    {
        Resolution = Math.Clamp(resolution, MinResolution, MaxResolution);
        RecipeHash = recipeHash;
        Height = AllocFaces(Resolution);
        HeightDelta = AllocFaces(Resolution);
        Splat0 = AllocSplat(Resolution);
        Splat1 = AllocSplat(Resolution);
    }

    static float[][] AllocFaces(int res)
    {
        var faces = new float[6][];
        for (int f = 0; f < 6; f++)
            faces[f] = new float[res * res];
        return faces;
    }

    static float[][] AllocSplat(int res)
    {
        var faces = new float[6][];
        for (int f = 0; f < 6; f++)
            faces[f] = new float[res * res * 4];
        return faces;
    }

    /// <summary>Mark Height + splat as authored (call from the cubemap baker only).</summary>
    public void MarkBaseHeightsReady() => HasBaseHeights = true;

    /// <summary>Full bake changed height and splat data.</summary>
    public void BumpVersion()
    {
        HeightVersion++;
        SplatVersion++;
    }

    /// <summary>
    /// Authored surface height: baked cubemap, or live graph height plus any
    /// in-flight dig deltas when this map is still a scratch.
    /// </summary>
    public float SampleAuthoredHeight(SN.Vector3 sphereDir, float liveGraphHeight)
        => (HasBaseHeights ? SampleBaseHeight(sphereDir) : liveGraphHeight) + SampleHeightDelta(sphereDir);

    /// <summary>Base height only (no digs). Scratch maps return the live graph height.</summary>
    public float SampleAuthoredBaseHeight(SN.Vector3 sphereDir, float liveGraphHeight)
        => HasBaseHeights ? SampleBaseHeight(sphereDir) : liveGraphHeight;

    /// <summary>Dig/build edit changed height only.</summary>
    public void BumpHeightVersion() => HeightVersion++;

    public void ClearHeightDeltas()
    {
        for (int f = 0; f < 6; f++)
            Array.Clear(HeightDelta[f]);
        BumpHeightVersion();
    }

    public void CopyHeightDeltasFrom(PlanetSurfaceCubemap? other)
    {
        if (other == null)
            return;
        if (other.Resolution == Resolution)
        {
            for (int f = 0; f < 6; f++)
                Array.Copy(other.HeightDelta[f], HeightDelta[f], HeightDelta[f].Length);
            BumpHeightVersion();
            return;
        }

        int res = Resolution;
        float inv = 1f / MathF.Max(1, res - 1);
        for (int f = 0; f < 6; f++)
        {
            var dst = HeightDelta[f];
            for (int y = 0; y < res; y++)
            {
                float v = y * inv;
                for (int x = 0; x < res; x++)
                {
                    var dir = CubeSphereMath.FaceUVToDirection(f, x * inv, v);
                    dst[y * res + x] = other.SampleHeightDelta(dir);
                }
            }
        }
        BumpHeightVersion();
    }

    /// <summary>Bilinear sample of base height (no dig delta, no water carve).</summary>
    public float SampleBaseHeight(SN.Vector3 sphereDir)
    {
        ResolveFaceUV(sphereDir, out int face, out float u, out float v);
        return SampleFaceBilerp(Height[face], Resolution, u, v);
    }

    /// <summary>Bilinear sample of dig height delta.</summary>
    public float SampleHeightDelta(SN.Vector3 sphereDir)
    {
        ResolveFaceUV(sphereDir, out int face, out float u, out float v);
        return SampleFaceBilerp(HeightDelta[face], Resolution, u, v);
    }

    /// <summary>Edited height = base + dig delta.</summary>
    public float SampleEditedHeight(SN.Vector3 sphereDir)
        => SampleBaseHeight(sphereDir) + SampleHeightDelta(sphereDir);

    /// <summary>Surface radius = planet radius + edited height.</summary>
    public float SampleEditedSurfaceRadius(float planetRadius, SN.Vector3 sphereDir)
        => planetRadius + SampleEditedHeight(sphereDir);

    /// <summary>
    /// Sample normalized 8 splat weights at a direction.
    /// <paramref name="weights"/> must have length &gt;= 8.
    /// </summary>
    public void SampleSplatWeights(SN.Vector3 sphereDir, Span<float> weights)
    {
        ResolveFaceUV(sphereDir, out int face, out float u, out float v);
        SampleFaceSplat(face, u, v, weights);
    }

    public void SampleFaceSplat(int face, float u, float v, Span<float> weights)
    {
        face = Math.Clamp(face, 0, 5);
        int res = Resolution;
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float fx = u * (res - 1);
        float fy = v * (res - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, res - 1);
        int y1 = Math.Min(y0 + 1, res - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        var s0 = Splat0[face];
        var s1 = Splat1[face];
        for (int i = 0; i < 4; i++)
        {
            weights[i] = BilerpChannel(s0, res, i, x0, y0, x1, y1, tx, ty);
            weights[i + 4] = BilerpChannel(s1, res, i, x0, y0, x1, y1, tx, ty);
        }

        float sum = 0f;
        for (int i = 0; i < 8; i++)
            sum += MathF.Max(0f, weights[i]);
        if (sum < 1e-5f)
        {
            weights[0] = 1f;
            for (int i = 1; i < 8; i++) weights[i] = 0f;
            return;
        }
        float inv = 1f / sum;
        for (int i = 0; i < 8; i++)
            weights[i] = MathF.Max(0f, weights[i]) * inv;
    }

    /// <summary>
    /// Approximate meters per cubemap texel along a face edge at <paramref name="surfaceRadius"/>.
    /// </summary>
    public float TexelMeters(float surfaceRadius)
        => (MathF.PI * 0.5f * MathF.Max(1f, surfaceRadius)) / Math.Max(1, Resolution);

    /// <summary>
    /// Apply a spherical dig/build brush onto height deltas.
    /// Positive delta lowers the surface (dig), negative raises (build) — matches density convention.
    /// Only visits a local UV window (wrapping across cube edges), not the full 6×res² atlas.
    /// </summary>
    public void ApplyHeightBrush(SN.Vector3 localCenter, float radius, float heightDelta, float falloff)
    {
        if (radius <= 0.001f || MathF.Abs(heightDelta) <= 1e-6f)
            return;

        float len = localCenter.Length();
        if (len < 1e-5f)
            return;
        var centerDir = localCenter / len;
        float surfaceR = MathF.Max(1f, len);
        float texel = TexelMeters(surfaceR);
        // Foot-scale brushes are smaller than one 512² texel on large planets — expand so digs show.
        // Aim for a visible crater (~4–6 texels across), not a single-texel pinprick.
        float brushR = MathF.Max(MathF.Max(0.05f, radius), texel * 5.5f);
        falloff = Math.Clamp(falloff, 0f, 1f);

        ResolveFaceUV(centerDir, out int face, out float cu, out float cv);
        int res = Resolution;
        float inv = 1f / MathF.Max(1, res - 1);
        float uvPad = (brushR / surfaceR) * (res / (MathF.PI * 0.5f)) + 3.5f;
        int pad = Math.Clamp((int)MathF.Ceiling(uvPad), 4, res);
        int cx = Math.Clamp((int)MathF.Round(cu * (res - 1)), 0, res - 1);
        int cy = Math.Clamp((int)MathF.Round(cv * (res - 1)), 0, res - 1);

        // Hard crater core (full strength) so bilinear mesh samples can't wash out the hole.
        int core = Math.Max(1, pad / 3);
        for (int dy = -core; dy <= core; dy++)
        {
            for (int dx = -core; dx <= core; dx++)
            {
                if (dx * dx + dy * dy > core * core)
                    continue;
                float u = (cx + dx) * inv;
                float v = (cy + dy) * inv;
                var w = CubeSphereMath.WrapFaceUV(face, u, v);
                int ix = (int)MathF.Round(Math.Clamp(w.U, 0f, 1f) * (res - 1));
                int iy = (int)MathF.Round(Math.Clamp(w.V, 0f, 1f) * (res - 1));
                HeightDelta[w.Face][iy * res + ix] -= heightDelta;
            }
        }

        for (int dy = -pad; dy <= pad; dy++)
        {
            for (int dx = -pad; dx <= pad; dx++)
            {
                if (dx * dx + dy * dy <= core * core)
                    continue;
                float u = (cx + dx) * inv;
                float v = (cy + dy) * inv;
                var w = CubeSphereMath.WrapFaceUV(face, u, v);
                int ix = (int)MathF.Round(Math.Clamp(w.U, 0f, 1f) * (res - 1));
                int iy = (int)MathF.Round(Math.Clamp(w.V, 0f, 1f) * (res - 1));
                var sampleDir = CubeSphereMath.FaceUVToDirection(w.Face, ix * inv, iy * inv);
                float dot = SN.Vector3.Dot(sampleDir, centerDir);
                if (dot < 0.2f)
                    continue;
                float ang = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                float dist = ang * surfaceR;
                if (dist >= brushR)
                    continue;
                float t = 1f - dist / brushR;
                float weight = falloff <= 1e-4f ? t : MathF.Pow(t, 1f + falloff * 2f);
                HeightDelta[w.Face][iy * res + ix] -= heightDelta * weight;
            }
        }

        BumpHeightVersion();
    }

    /// <summary>Collect nonzero height digs for compact .planetvox persistence.</summary>
    public PlanetHeightDeltaTexel[] ExportSparseHeightDeltas(float epsilon = 1e-5f)
    {
        int res = Resolution;
        var list = new System.Collections.Generic.List<PlanetHeightDeltaTexel>(256);
        for (int face = 0; face < 6; face++)
        {
            var d = HeightDelta[face];
            for (int i = 0; i < d.Length; i++)
            {
                if (MathF.Abs(d[i]) <= epsilon)
                    continue;
                list.Add(new PlanetHeightDeltaTexel { Face = face, Index = i, Value = d[i] });
            }
        }
        return list.ToArray();
    }

    public void ImportSparseHeightDeltas(PlanetHeightDeltaTexel[]? texels, int resolution)
    {
        if (texels == null || texels.Length == 0 || resolution != Resolution)
            return;
        ClearHeightDeltas();
        for (int i = 0; i < texels.Length; i++)
        {
            var t = texels[i];
            if ((uint)t.Face >= 6u) continue;
            var face = HeightDelta[t.Face];
            if ((uint)t.Index >= (uint)face.Length) continue;
            face[t.Index] = t.Value;
        }
        BumpHeightVersion();
    }

    /// <summary>
    /// Pack 6 faces into a 3×2 atlas (RGBA float).
    /// R = base height, G = dig delta (negative when dug), B = edited height (base+delta).
    /// </summary>
    public float[] BuildHeightAtlasRgba(bool includeDelta = true)
    {
        int res = Resolution;
        int aw = res * 3;
        int ah = res * 2;
        var atlas = new float[aw * ah * 4];
        for (int face = 0; face < 6; face++)
        {
            int col = face % 3;
            int row = face / 3;
            var h = Height[face];
            var d = HeightDelta[face];
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int src = y * res + x;
                    float dig = d[src];
                    float edited = includeDelta ? h[src] + dig : h[src];
                    int ax = col * res + x;
                    int ay = row * res + y;
                    int dst = (ay * aw + ax) * 4;
                    atlas[dst] = h[src];
                    atlas[dst + 1] = dig;
                    atlas[dst + 2] = edited;
                }
            }
        }
        return atlas;
    }

    /// <summary>
    /// Snapshot dig deltas under a world-direction disk so feet can be protected from self-digs.
    /// </summary>
    public (int Face, int Index, float Value)[] CaptureHeightDeltasNear(
        SN.Vector3 sphereDir, float surfaceRadius, float protectRadiusMeters)
    {
        if (protectRadiusMeters <= 0.05f || sphereDir.LengthSquared() < 1e-12f)
            return Array.Empty<(int, int, float)>();

        sphereDir = SN.Vector3.Normalize(sphereDir);
        float surfaceR = MathF.Max(1f, surfaceRadius);
        float texel = TexelMeters(surfaceR);
        float protectR = MathF.Max(protectRadiusMeters, texel * 2f);
        ResolveFaceUV(sphereDir, out int face, out float cu, out float cv);
        int res = Resolution;
        float inv = 1f / MathF.Max(1, res - 1);
        float uvPad = (protectR / surfaceR) * (res / (MathF.PI * 0.5f)) + 2f;
        int pad = Math.Clamp((int)MathF.Ceiling(uvPad), 2, res / 4);
        int cx = Math.Clamp((int)MathF.Round(cu * (res - 1)), 0, res - 1);
        int cy = Math.Clamp((int)MathF.Round(cv * (res - 1)), 0, res - 1);

        var list = new System.Collections.Generic.List<(int, int, float)>(pad * pad * 4);
        var seen = new System.Collections.Generic.HashSet<long>();
        for (int dy = -pad; dy <= pad; dy++)
        {
            for (int dx = -pad; dx <= pad; dx++)
            {
                float u = (cx + dx) * inv;
                float v = (cy + dy) * inv;
                var w = CubeSphereMath.WrapFaceUV(face, u, v);
                int ix = (int)MathF.Round(Math.Clamp(w.U, 0f, 1f) * (res - 1));
                int iy = (int)MathF.Round(Math.Clamp(w.V, 0f, 1f) * (res - 1));
                long key = ((long)w.Face << 32) | (uint)(iy * res + ix);
                if (!seen.Add(key))
                    continue;
                var dir = CubeSphereMath.FaceUVToDirection(w.Face, ix * inv, iy * inv);
                float dot = SN.Vector3.Dot(dir, sphereDir);
                if (dot < 0.2f)
                    continue;
                float ang = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                if (ang * surfaceR >= protectR)
                    continue;
                int idx = iy * res + ix;
                list.Add((w.Face, idx, HeightDelta[w.Face][idx]));
            }
        }
        return list.ToArray();
    }

    public void RestoreHeightDeltas((int Face, int Index, float Value)[]? texels)
    {
        if (texels == null || texels.Length == 0)
            return;
        bool any = false;
        for (int i = 0; i < texels.Length; i++)
        {
            var t = texels[i];
            if ((uint)t.Face >= 6u) continue;
            var face = HeightDelta[t.Face];
            if ((uint)t.Index >= (uint)face.Length) continue;
            if (MathF.Abs(face[t.Index] - t.Value) <= 1e-8f) continue;
            face[t.Index] = t.Value;
            any = true;
        }
        if (any)
            BumpHeightVersion();
    }

    /// <summary>Pack splat0 or splat1 faces into a 3×2 RGBA atlas.</summary>
    public float[] BuildSplatAtlas(bool splat1)
    {
        int res = Resolution;
        int aw = res * 3;
        int ah = res * 2;
        var atlas = new float[aw * ah * 4];
        for (int face = 0; face < 6; face++)
        {
            int col = face % 3;
            int row = face / 3;
            var srcFace = splat1 ? Splat1[face] : Splat0[face];
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int src = (y * res + x) * 4;
                    int ax = col * res + x;
                    int ay = row * res + y;
                    int dst = (ay * aw + ax) * 4;
                    atlas[dst] = srcFace[src];
                    atlas[dst + 1] = srcFace[src + 1];
                    atlas[dst + 2] = srcFace[src + 2];
                    atlas[dst + 3] = srcFace[src + 3];
                }
            }
        }
        return atlas;
    }

    public static void ResolveFaceUV(SN.Vector3 sphereDir, out int face, out float u, out float v)
    {
        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        else
            sphereDir = SN.Vector3.Normalize(sphereDir);
        var mapped = CubeSphereMath.SphereToCube(sphereDir);
        face = Math.Clamp(mapped.Face, 0, 5);
        u = Math.Clamp(mapped.U, 0f, 1f);
        v = Math.Clamp(mapped.V, 0f, 1f);
    }

    /// <summary>Map cube-sphere face UV into the 3×2 atlas UV used by GPU uploads.</summary>
    public static SN.Vector2 FaceUvToAtlasUv(int face, float u, float v)
    {
        face = Math.Clamp(face, 0, 5);
        int col = face % 3;
        int row = face / 3;
        return new SN.Vector2((col + Math.Clamp(u, 0f, 1f)) / 3f, (row + Math.Clamp(v, 0f, 1f)) / 2f);
    }

    static float SampleFaceBilerp(float[] data, int res, float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float fx = u * (res - 1);
        float fy = v * (res - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, res - 1);
        int y1 = Math.Min(y0 + 1, res - 1);
        return Bilerp(data, res, x0, y0, x1, y1, fx - x0, fy - y0);
    }

    static float Bilerp(float[] data, int res, int x0, int y0, int x1, int y1, float tx, float ty)
    {
        float a = data[y0 * res + x0];
        float b = data[y0 * res + x1];
        float c = data[y1 * res + x0];
        float d = data[y1 * res + x1];
        float top = a + (b - a) * tx;
        float bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }

    static float BilerpChannel(float[] rgba, int res, int channel, int x0, int y0, int x1, int y1, float tx, float ty)
    {
        float a = rgba[(y0 * res + x0) * 4 + channel];
        float b = rgba[(y0 * res + x1) * 4 + channel];
        float c = rgba[(y1 * res + x0) * 4 + channel];
        float d = rgba[(y1 * res + x1) * 4 + channel];
        float top = a + (b - a) * tx;
        float bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }
}
