#nullable enable
using System;
using Game_Engine.Core.Biome;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Baked 6-face climate / height / biome LUTs produced on graph apply.
/// Runtime samples bilinearly instead of allocating biome lists per voxel.
/// Optional flow-accumulation river channel is baked once when enabled.
/// </summary>
public sealed class PlanetClimateAtlas
{
    public const int DefaultResolution = 256;

    public int Resolution { get; }
    public ulong RecipeHash { get; }
    public bool HasFlowRivers { get; private set; }

    // Per face, row-major [v * res + u]
    readonly float[][] _temp;
    readonly float[][] _moist;
    readonly float[][] _height;
    readonly float[][] _flow;
    readonly byte[][] _biome0;
    readonly byte[][] _biome1;
    readonly byte[][] _w0; // weight*255
    readonly byte[][] _w1;

    public PlanetClimateAtlas(int resolution, ulong recipeHash)
    {
        Resolution = Math.Clamp(resolution, 64, 512);
        RecipeHash = recipeHash;
        _temp = AllocFaces(Resolution);
        _moist = AllocFaces(Resolution);
        _height = AllocFaces(Resolution);
        _flow = AllocFaces(Resolution);
        _biome0 = AllocBytes(Resolution);
        _biome1 = AllocBytes(Resolution);
        _w0 = AllocBytes(Resolution);
        _w1 = AllocBytes(Resolution);
    }

    static float[][] AllocFaces(int res)
    {
        var faces = new float[6][];
        for (int f = 0; f < 6; f++)
            faces[f] = new float[res * res];
        return faces;
    }

    static byte[][] AllocBytes(int res)
    {
        var faces = new byte[6][];
        for (int f = 0; f < 6; f++)
            faces[f] = new byte[res * res];
        return faces;
    }

    public static PlanetClimateAtlas Bake(
        PlanetConfig config,
        BiomeMap biomeMap,
        PlanetNoiseCache? noise,
        int resolution = DefaultResolution)
    {
        var atlas = new PlanetClimateAtlas(resolution, config.RecipeHash);
        int res = atlas.Resolution;
        float inv = 1f / MathF.Max(1, res - 1);
        float maxAmp = MathF.Max(1f, DensityGenerator.MaxAmplitude(config));

        for (int face = 0; face < 6; face++)
        {
            var fTemp = atlas._temp[face];
            var fMoist = atlas._moist[face];
            var fHeight = atlas._height[face];
            var fB0 = atlas._biome0[face];
            var fB1 = atlas._biome1[face];
            var fW0 = atlas._w0[face];
            var fW1 = atlas._w1[face];

            for (int y = 0; y < res; y++)
            {
                float v = y * inv;
                for (int x = 0; x < res; x++)
                {
                    float u = x * inv;
                    var dir = CubeSphereMath.FaceUVToDirection(face, u, v);
                    int idx = y * res + x;

                    float height = 0f;
                    if (noise != null)
                    {
                        height = PlanetSurfaceUtility.SampleHeight(
                            config, biomeMap,
                            noise.BiomeNoises, noise.ErosionNoise,
                            noise.RidgeNoise, noise.BasinNoise, dir);
                    }

                    float alt = Math.Clamp((height / maxAmp) * 0.5f + 0.5f, 0f, 1f);
                    // Climate coupling lives in BiomeMap (lapse / water / rain shadow).
                    float temp = biomeMap.GetTemperature(dir, alt);
                    float moist = biomeMap.GetMoisture(dir, alt);

                    var blends = biomeMap.GetBiomes(dir, alt);
                    byte b0 = 0, b1 = 0;
                    float w0 = 1f, w1 = 0f;
                    if (blends.Length > 0)
                    {
                        b0 = blends[0].Biome.BiomeIndex;
                        w0 = blends[0].Weight;
                    }
                    if (blends.Length > 1)
                    {
                        b1 = blends[1].Biome.BiomeIndex;
                        w1 = blends[1].Weight;
                    }

                    fTemp[idx] = temp;
                    fMoist[idx] = moist;
                    fHeight[idx] = height;
                    fB0[idx] = b0;
                    fB1[idx] = b1;
                    fW0[idx] = (byte)Math.Clamp((int)(w0 * 255f + 0.5f), 0, 255);
                    fW1[idx] = (byte)Math.Clamp((int)(w1 * 255f + 0.5f), 0, 255);
                }
            }
        }

        if (config.UseFlowAccumulationRivers)
            atlas.BakeFlowRivers(config);

        return atlas;
    }

    /// <summary>
    /// One-shot D8 flow accumulation per cube face. Not per-frame.
    /// Writes a 0–1 river channel mask into <see cref="_flow"/>.
    /// </summary>
    public void BakeFlowRivers(PlanetConfig config)
    {
        int res = Resolution;
        float threshold = Math.Clamp(config.FlowRiverThreshold, 0.05f, 0.99f);
        // 8 neighbor offsets (dx, dy) — D8.
        int[] ndx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] ndy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int face = 0; face < 6; face++)
        {
            var h = _height[face];
            var flow = _flow[face];
            Array.Clear(flow);

            // Order cells high→low so donors flush downhill once.
            int n = res * res;
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                flow[i] = 1f; // unit rainfall
                order[i] = i;
            }
            Array.Sort(order, (a, b) => h[b].CompareTo(h[a]));

            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                int x = i % res;
                int y = i / res;
                float bestDrop = 0f;
                int bestJ = -1;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + ndx[k];
                    int ny = y + ndy[k];
                    if ((uint)nx >= (uint)res || (uint)ny >= (uint)res)
                        continue;
                    int j = ny * res + nx;
                    float drop = h[i] - h[j];
                    if (drop > bestDrop)
                    {
                        bestDrop = drop;
                        bestJ = j;
                    }
                }
                if (bestJ >= 0)
                    flow[bestJ] += flow[i];
            }

            // Normalize log-scaled accumulation into a soft river mask.
            float maxFlow = 1f;
            for (int i = 0; i < n; i++)
                maxFlow = MathF.Max(maxFlow, flow[i]);
            float invLog = 1f / MathF.Log(maxFlow + 1f);
            for (int i = 0; i < n; i++)
            {
                float t = MathF.Log(flow[i] + 1f) * invLog;
                flow[i] = t >= threshold
                    ? Math.Clamp((t - threshold) / MathF.Max(1e-4f, 1f - threshold), 0f, 1f)
                    : 0f;
            }
        }

        HasFlowRivers = true;
    }

    public void Sample(SN.Vector3 sphereDir, out float temp, out float moist, out float height,
        out byte biome0, out byte biome1, out float w0, out float w1)
    {
        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        else
            sphereDir = SN.Vector3.Normalize(sphereDir);

        var (face, u, v) = CubeSphereMath.SphereToCube(sphereDir);
        face = Math.Clamp(face, 0, 5);
        SampleFace(face, u, v, out temp, out moist, out height, out biome0, out biome1, out w0, out w1);
    }

    public void SampleFace(int face, float u, float v,
        out float temp, out float moist, out float height,
        out byte biome0, out byte biome1, out float w0, out float w1)
    {
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

        temp = Bilerp(_temp[face], res, x0, y0, x1, y1, tx, ty);
        moist = Bilerp(_moist[face], res, x0, y0, x1, y1, tx, ty);
        height = Bilerp(_height[face], res, x0, y0, x1, y1, tx, ty);

        // Nearest for biome indices, bilinear for weights.
        int nx = tx < 0.5f ? x0 : x1;
        int ny = ty < 0.5f ? y0 : y1;
        int nidx = ny * res + nx;
        biome0 = _biome0[face][nidx];
        biome1 = _biome1[face][nidx];
        w0 = BilerpByte(_w0[face], res, x0, y0, x1, y1, tx, ty) / 255f;
        w1 = BilerpByte(_w1[face], res, x0, y0, x1, y1, tx, ty) / 255f;
        float sum = w0 + w1;
        if (sum > 1e-4f) { w0 /= sum; w1 /= sum; }
        else { w0 = 1f; w1 = 0f; }
    }

    public float SampleMacroHeight(SN.Vector3 sphereDir)
    {
        Sample(sphereDir, out _, out _, out float h, out _, out _, out _, out _);
        return h;
    }

    public float SampleFlowRiver(SN.Vector3 sphereDir)
    {
        if (!HasFlowRivers)
            return 0f;
        if (sphereDir.LengthSquared() < 1e-12f)
            sphereDir = SN.Vector3.UnitY;
        else
            sphereDir = SN.Vector3.Normalize(sphereDir);

        var (face, u, v) = CubeSphereMath.SphereToCube(sphereDir);
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
        return Bilerp(_flow[face], res, x0, y0, x1, y1, fx - x0, fy - y0);
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

    static float BilerpByte(byte[] data, int res, int x0, int y0, int x1, int y1, float tx, float ty)
    {
        float a = data[y0 * res + x0];
        float b = data[y0 * res + x1];
        float c = data[y1 * res + x0];
        float d = data[y1 * res + x1];
        float top = a + (b - a) * tx;
        float bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }
}
